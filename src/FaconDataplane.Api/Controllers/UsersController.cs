using System.Data.Common;
using System.Text.Json;
using Asp.Versioning;
using FaconDataplane.Api.Authorization;
using FaconDataplane.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FaconDataplane.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/tenants/{tenantId:guid}/users")]
[Authorize]
[RequirePermission("users.read")]
public sealed class UsersController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IHttpClientFactory httpFactory, ILogger<UsersController> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    // ── Queries ──────────────────────────────────────────────────────────

    /// <summary>List all members of the tenant with their roles.</summary>
    [HttpGet]
    public async Task<IActionResult> GetMembers(Guid tenantId, CancellationToken ct)
    {
        var conn = GetDbConnection();
        var members = new List<MemberResponse>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT mr.sub, r.name AS role, r.id AS role_id, mr.assigned_at
            FROM tenant_member_roles mr
            JOIN tenant_roles r ON r.id = mr.role_id AND r.tenant_id = mr.tenant_id
            WHERE mr.tenant_id = @tid
              AND (mr.expires_at IS NULL OR mr.expires_at > NOW())
            ORDER BY mr.sub, r.priority DESC
            """;
        cmd.AddParameter("@tid", tenantId);

        // Group by sub to collect multiple roles per user
        var bySub = new Dictionary<string, MemberResponse>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sub = reader.GetString(0);
            var role = reader.GetString(1);
            var roleId = reader.GetGuid(2);

            if (!bySub.TryGetValue(sub, out var member))
            {
                member = new MemberResponse(sub, new List<RoleSummary>());
                bySub[sub] = member;
            }
            member.Roles.Add(new RoleSummary(roleId, role));
        }

        return Ok(bySub.Values.ToList());
    }

    /// <summary>Get a single member's roles and permission overrides.</summary>
    [HttpGet("{sub}")]
    public async Task<IActionResult> GetMember(Guid tenantId, string sub, CancellationToken ct)
    {
        var conn = GetDbConnection();

        // Roles
        var roles = new List<RoleSummary>();
        await using var roleCmd = conn.CreateCommand();
        roleCmd.CommandText = """
            SELECT r.id, r.name FROM tenant_member_roles mr
            JOIN tenant_roles r ON r.id = mr.role_id AND r.tenant_id = mr.tenant_id
            WHERE mr.tenant_id = @tid AND mr.sub = @sub
              AND (mr.expires_at IS NULL OR mr.expires_at > NOW())
            """;
        roleCmd.AddParameter("@tid", tenantId);
        roleCmd.AddParameter("@sub", sub);
        await using var rr = await roleCmd.ExecuteReaderAsync(ct);
        while (await rr.ReadAsync(ct))
            roles.Add(new RoleSummary(rr.GetGuid(0), rr.GetString(1)));

        // Direct permission overrides
        var overrides = new List<PermissionOverride>();
        await using var permCmd = conn.CreateCommand();
        permCmd.CommandText = """
            SELECT p.code, mp.effect FROM tenant_member_permissions mp
            JOIN permissions p ON p.id = mp.permission_id
            WHERE mp.tenant_id = @tid AND mp.sub = @sub
              AND (mp.expires_at IS NULL OR mp.expires_at > NOW())
            """;
        permCmd.AddParameter("@tid", tenantId);
        permCmd.AddParameter("@sub", sub);
        await using var pr = await permCmd.ExecuteReaderAsync(ct);
        while (await pr.ReadAsync(ct))
            overrides.Add(new PermissionOverride(pr.GetString(0), pr.GetInt16(1)));

        return Ok(new MemberDetailResponse(sub, roles, overrides));
    }

    // ── Invitations ──────────────────────────────────────────────────────

    /// <summary>Invite a user to this tenant. Creates a CP invitation.</summary>
    [HttpPost("invite")]
    [RequirePermission("users.create")]
    public async Task<IActionResult> InviteUser(
        Guid tenantId, [FromBody] InviteRequest request, CancellationToken ct)
    {
        var client = CreateCpClient();
        var response = await client.PostAsJsonAsync(
            $"api/v1/tenants/{tenantId}/invitations",
            new { email = request.Email }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            return StatusCode((int)response.StatusCode, error);
        }

        var result = await response.Content.ReadFromJsonAsync<InvitationResponse>(ct);

        _logger.LogInformation(
            "User invited: email={Email} tenant={TenantId} token={Token}",
            request.Email, tenantId, result?.Token);

        return Ok(result);
    }

    // ── Role Management ──────────────────────────────────────────────────

    /// <summary>Replace a user's role assignments.</summary>
    [HttpPut("{sub}/roles")]
    [RequirePermission("users.update")]
    public async Task<IActionResult> UpdateRoles(
        Guid tenantId, string sub, [FromBody] UpdateRolesRequest request, CancellationToken ct)
    {
        var conn = GetDbConnection();
        var actor = User.FindFirstValue("sub") ?? "unknown";

        // Delete existing role assignments
        await using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM tenant_member_roles WHERE tenant_id = @tid AND sub = @sub";
        delCmd.AddParameter("@tid", tenantId);
        delCmd.AddParameter("@sub", sub);
        await delCmd.ExecuteNonQueryAsync(ct);

        // Insert new role assignments
        foreach (var roleId in request.RoleIds)
        {
            await using var insCmd = conn.CreateCommand();
            insCmd.CommandText = """
                INSERT INTO tenant_member_roles (tenant_id, sub, role_id, assigned_by)
                VALUES (@tid, @sub, @roleId, @actor)
                ON CONFLICT (tenant_id, sub, role_id) DO NOTHING
                """;
            insCmd.AddParameter("@tid", tenantId);
            insCmd.AddParameter("@sub", sub);
            insCmd.AddParameter("@roleId", roleId);
            insCmd.AddParameter("@actor", actor);
            await insCmd.ExecuteNonQueryAsync(ct);
        }

        // Audit
        await WriteAuditAsync(conn, tenantId, actor, sub, "roles_updated",
            new { assigned = request.RoleIds }, ct);

        // Refresh permissions view
        await TenantMigrationService.RefreshEffectivePermissionsAsync(conn, ct);

        _logger.LogInformation("Roles updated: sub={Sub} tenant={TenantId} roles={Roles}",
            sub, tenantId, string.Join(",", request.RoleIds));

        return NoContent();
    }

    // ── Permission Overrides ─────────────────────────────────────────────

    /// <summary>Grant or deny a direct permission override for a user.</summary>
    [HttpPost("{sub}/permissions")]
    [RequirePermission("users.update")]
    public async Task<IActionResult> GrantPermission(
        Guid tenantId, string sub, [FromBody] PermissionOverrideRequest request, CancellationToken ct)
    {
        var conn = GetDbConnection();
        var actor = User.FindFirstValue("sub") ?? "unknown";

        // Resolve permission code → id
        await using var lookupCmd = conn.CreateCommand();
        lookupCmd.CommandText = "SELECT id FROM permissions WHERE code = @code";
        lookupCmd.AddParameter("@code", request.Permission);
        var permId = await lookupCmd.ExecuteScalarAsync(ct) as Guid?;
        if (permId is null)
            return NotFound(new { error = "unknown_permission", message = $"Permission '{request.Permission}' not found." });

        await using var upsertCmd = conn.CreateCommand();
        upsertCmd.CommandText = """
            INSERT INTO tenant_member_permissions (tenant_id, sub, permission_id, effect, assigned_by, expires_at)
            VALUES (@tid, @sub, @pid, @effect, @actor, @expires)
            ON CONFLICT (tenant_id, sub, permission_id)
            DO UPDATE SET effect = @effect, assigned_by = @actor, expires_at = @expires
            """;
        upsertCmd.AddParameter("@tid", tenantId);
        upsertCmd.AddParameter("@sub", sub);
        upsertCmd.AddParameter("@pid", permId.Value);
        upsertCmd.AddParameter("@effect", (short)(request.Effect == "deny" ? 2 : 1));
        upsertCmd.AddParameter("@actor", actor);
        upsertCmd.AddParameter("@expires", (object?)request.ExpiresAt ?? DBNull.Value);
        await upsertCmd.ExecuteNonQueryAsync(ct);

        await WriteAuditAsync(conn, tenantId, actor, sub, "permission_override",
            new { permission = request.Permission, effect = request.Effect, expiresAt = request.ExpiresAt }, ct);
        await TenantMigrationService.RefreshEffectivePermissionsAsync(conn, ct);

        return NoContent();
    }

    /// <summary>Remove a direct permission override.</summary>
    [HttpDelete("{sub}/permissions/{permissionCode}")]
    [RequirePermission("users.update")]
    public async Task<IActionResult> RevokePermission(
        Guid tenantId, string sub, string permissionCode, CancellationToken ct)
    {
        var conn = GetDbConnection();
        var actor = User.FindFirstValue("sub") ?? "unknown";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM tenant_member_permissions
            WHERE tenant_id = @tid AND sub = @sub
              AND permission_id = (SELECT id FROM permissions WHERE code = @code)
            """;
        cmd.AddParameter("@tid", tenantId);
        cmd.AddParameter("@sub", sub);
        cmd.AddParameter("@code", permissionCode);
        await cmd.ExecuteNonQueryAsync(ct);

        await WriteAuditAsync(conn, tenantId, actor, sub, "permission_revoked",
            new { permission = permissionCode }, ct);
        await TenantMigrationService.RefreshEffectivePermissionsAsync(conn, ct);

        return NoContent();
    }

    // ── Member Removal ───────────────────────────────────────────────────

    /// <summary>Remove a member from the tenant.</summary>
    [HttpDelete("{sub}")]
    [RequirePermission("users.delete")]
    public async Task<IActionResult> RemoveMember(Guid tenantId, string sub, CancellationToken ct)
    {
        var conn = GetDbConnection();
        var actor = User.FindFirstValue("sub") ?? "unknown";

        // Remove all role assignments
        await using var delRoles = conn.CreateCommand();
        delRoles.CommandText = "DELETE FROM tenant_member_roles WHERE tenant_id = @tid AND sub = @sub";
        delRoles.AddParameter("@tid", tenantId);
        delRoles.AddParameter("@sub", sub);
        await delRoles.ExecuteNonQueryAsync(ct);

        // Remove all direct permissions
        await using var delPerms = conn.CreateCommand();
        delPerms.CommandText = "DELETE FROM tenant_member_permissions WHERE tenant_id = @tid AND sub = @sub";
        delPerms.AddParameter("@tid", tenantId);
        delPerms.AddParameter("@sub", sub);
        await delPerms.ExecuteNonQueryAsync(ct);

        // Proxy to CP to remove OrganizationMember
        var client = CreateCpClient();
        var orgId = HttpContext.Items["OrganizationId"] as Guid?;
        if (orgId.HasValue)
        {
            await client.DeleteAsync($"api/v1/organizations/{orgId}/members/{sub}", ct);
        }

        await WriteAuditAsync(conn, tenantId, actor, sub, "member_removed", null, ct);
        await TenantMigrationService.RefreshEffectivePermissionsAsync(conn, ct);

        _logger.LogInformation("Member removed: sub={Sub} tenant={TenantId}", sub, tenantId);

        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private DbConnection GetDbConnection() =>
        (DbConnection)(HttpContext.Items["DbConnection"]
            ?? throw new InvalidOperationException("DB connection not available"));

    private HttpClient CreateCpClient()
    {
        var client = _httpFactory.CreateClient("ControlPlane");
        var jwt = HttpContext.Request.Headers.Authorization.ToString();
        if (jwt.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt["Bearer ".Length..]);
        return client;
    }

    private static async Task WriteAuditAsync(
        DbConnection conn, Guid tenantId, string actor, string target,
        string action, object? detail, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(detail ?? new { });
        await using var cmd = conn.CreateCommand();

        if (conn is Microsoft.Data.SqlClient.SqlConnection)
        {
            cmd.CommandText = """
                INSERT INTO tenant_authorization_audit (id, tenant_id, actor_sub, target_sub, action, payload)
                VALUES (@id, @tid, @actor, @target, @action, @payload)
                """;
            cmd.AddParameter("@id", Guid.NewGuid());
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO tenant_authorization_audit (id, tenant_id, actor_sub, target_sub, action, payload)
                VALUES (gen_random_uuid(), @tid, @actor, @target, @action, @payload::jsonb)
                """;
        }

        cmd.AddParameter("@tid", tenantId);
        cmd.AddParameter("@actor", actor);
        cmd.AddParameter("@target", target);
        cmd.AddParameter("@action", action);
        cmd.AddParameter("@payload", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

// ── Request/Response models ───────────────────────────────────────────────

public sealed record InviteRequest(string Email);

public sealed record InvitationResponse(string Token, Guid TenantId);

public sealed record UpdateRolesRequest(List<Guid> RoleIds);

public sealed record PermissionOverrideRequest(
    string Permission,
    string Effect,            // "allow" or "deny"
    DateTimeOffset? ExpiresAt = null);

public sealed record MemberResponse(string Sub, List<RoleSummary> Roles);

public sealed record MemberDetailResponse(string Sub, List<RoleSummary> Roles, List<PermissionOverride> Overrides);

public sealed record RoleSummary(Guid Id, string Name);

public sealed record PermissionOverride(string Permission, short Effect);

/// <summary>
/// Extension to add parameters to DbCommand.
/// </summary>
file static class DbCommandExtensions
{
    public static void AddParameter(this DbCommand cmd, string name, object? value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(param);
    }
}
