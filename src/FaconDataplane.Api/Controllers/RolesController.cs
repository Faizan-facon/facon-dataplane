using System.Data.Common;
using Asp.Versioning;
using FaconDataplane.Api.Authorization;
using FaconDataplane.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FaconDataplane.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/tenants/{tenantId:guid}/roles")]
[Authorize]
[RequirePermission("users.read")]
public sealed class RolesController : ControllerBase
{
    private readonly ILogger<RolesController> _logger;

    public RolesController(ILogger<RolesController> logger) => _logger = logger;

    /// <summary>List all roles for the tenant.</summary>
    [HttpGet]
    public async Task<IActionResult> GetRoles(Guid tenantId, CancellationToken ct)
    {
        var conn = GetDbConnection();
        var roles = new List<RoleResponse>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, description, is_system, is_default, priority, created_at
            FROM tenant_roles WHERE tenant_id = @tid ORDER BY priority DESC
            """;
        cmd.AddParameter("@tid", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            roles.Add(new RoleResponse(
                reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4),
                reader.GetInt16(5), reader.GetDateTime(6)));
        }

        return Ok(roles);
    }

    /// <summary>Get a single role with its permissions.</summary>
    [HttpGet("{roleId:guid}")]
    public async Task<IActionResult> GetRole(Guid tenantId, Guid roleId, CancellationToken ct)
    {
        var conn = GetDbConnection();

        // Role info
        await using var roleCmd = conn.CreateCommand();
        roleCmd.CommandText = """
            SELECT id, name, description, is_system, is_default, priority, created_at
            FROM tenant_roles WHERE tenant_id = @tid AND id = @rid
            """;
        roleCmd.AddParameter("@tid", tenantId);
        roleCmd.AddParameter("@rid", roleId);

        await using var rr = await roleCmd.ExecuteReaderAsync(ct);
        if (!await rr.ReadAsync(ct)) return NotFound();
        var role = new RoleResponse(
            rr.GetGuid(0), rr.GetString(1),
            rr.IsDBNull(2) ? null : rr.GetString(2),
            rr.GetBoolean(3), rr.GetBoolean(4),
            rr.GetInt16(5), rr.GetDateTime(6));

        // Permissions for this role
        var permissions = new List<RolePermissionResponse>();
        await using var permCmd = conn.CreateCommand();
        permCmd.CommandText = """
            SELECT p.code, p.display_name, rp.effect
            FROM tenant_role_permissions rp
            JOIN permissions p ON p.id = rp.permission_id
            WHERE rp.tenant_id = @tid AND rp.role_id = @rid
            ORDER BY p.code
            """;
        permCmd.AddParameter("@tid", tenantId);
        permCmd.AddParameter("@rid", roleId);

        await using var pr = await permCmd.ExecuteReaderAsync(ct);
        while (await pr.ReadAsync(ct))
            permissions.Add(new RolePermissionResponse(pr.GetString(0), pr.GetString(1), pr.GetInt16(2)));

        return Ok(new RoleDetailResponse(role, permissions));
    }

    /// <summary>Create a custom role.</summary>
    [HttpPost]
    [RequirePermission("users.update")]
    public async Task<IActionResult> CreateRole(
        Guid tenantId, [FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        var conn = GetDbConnection();
        var roleId = Guid.NewGuid();

        await using var insCmd = conn.CreateCommand();
        insCmd.CommandText = """
            INSERT INTO tenant_roles (id, tenant_id, name, description, is_system, is_default, priority)
            VALUES (@id, @tid, @name, @desc, false, @isDefault, @priority)
            """;
        insCmd.AddParameter("@id", roleId);
        insCmd.AddParameter("@tid", tenantId);
        insCmd.AddParameter("@name", request.Name);
        insCmd.AddParameter("@desc", (object?)request.Description ?? DBNull.Value);
        insCmd.AddParameter("@isDefault", request.IsDefault);
        insCmd.AddParameter("@priority", (short)request.Priority);
        await insCmd.ExecuteNonQueryAsync(ct);

        // Insert permissions
        foreach (var p in request.Permissions)
        {
            await using var permCmd = conn.CreateCommand();
            permCmd.CommandText = """
                INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id, effect)
                SELECT @tid, @rid, id, @effect FROM permissions WHERE code = @code
                """;
            permCmd.AddParameter("@tid", tenantId);
            permCmd.AddParameter("@rid", roleId);
            permCmd.AddParameter("@code", p.Code);
            permCmd.AddParameter("@effect", (short)(p.Effect == "deny" ? 2 : 1));
            await permCmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Role created: {Name} tenant={TenantId} id={RoleId}", request.Name, tenantId, roleId);

        return CreatedAtAction(nameof(GetRole), new { tenantId, roleId },
            new { id = roleId, name = request.Name });
    }

    /// <summary>Update a role's permissions (replaces all).</summary>
    [HttpPut("{roleId:guid}")]
    [RequirePermission("users.update")]
    public async Task<IActionResult> UpdateRole(
        Guid tenantId, Guid roleId, [FromBody] UpdateRoleRequest request, CancellationToken ct)
    {
        var conn = GetDbConnection();

        // Check role exists and is not system
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT is_system FROM tenant_roles WHERE tenant_id = @tid AND id = @rid
            """;
        checkCmd.AddParameter("@tid", tenantId);
        checkCmd.AddParameter("@rid", roleId);
        var isSystem = await checkCmd.ExecuteScalarAsync(ct) as bool?;

        if (isSystem is null) return NotFound();
        if (isSystem == true && request.Permissions is not null)
            return BadRequest(new { error = "system_role", message = "Cannot modify permissions of a system role. Create a custom role instead." });

        // Update name/description
        await using var updCmd = conn.CreateCommand();
        updCmd.CommandText = """
            UPDATE tenant_roles SET name = @name, description = @desc, updated_at = NOW()
            WHERE tenant_id = @tid AND id = @rid
            """;
        updCmd.AddParameter("@tid", tenantId);
        updCmd.AddParameter("@rid", roleId);
        updCmd.AddParameter("@name", request.Name);
        updCmd.AddParameter("@desc", (object?)request.Description ?? DBNull.Value);
        await updCmd.ExecuteNonQueryAsync(ct);

        // Replace permissions if provided
        if (request.Permissions is not null)
        {
            await using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM tenant_role_permissions WHERE tenant_id = @tid AND role_id = @rid";
            delCmd.AddParameter("@tid", tenantId);
            delCmd.AddParameter("@rid", roleId);
            await delCmd.ExecuteNonQueryAsync(ct);

            foreach (var p in request.Permissions)
            {
                await using var insCmd = conn.CreateCommand();
                insCmd.CommandText = """
                    INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id, effect)
                    SELECT @tid, @rid, id, @effect FROM permissions WHERE code = @code
                    """;
                insCmd.AddParameter("@tid", tenantId);
                insCmd.AddParameter("@rid", roleId);
                insCmd.AddParameter("@code", p.Code);
                insCmd.AddParameter("@effect", (short)(p.Effect == "deny" ? 2 : 1));
                await insCmd.ExecuteNonQueryAsync(ct);
            }

            await TenantMigrationService.RefreshEffectivePermissionsAsync(conn, ct);
        }

        return NoContent();
    }

    /// <summary>Delete a custom role. System roles cannot be deleted.</summary>
    [HttpDelete("{roleId:guid}")]
    [RequirePermission("users.update")]
    public async Task<IActionResult> DeleteRole(Guid tenantId, Guid roleId, CancellationToken ct)
    {
        var conn = GetDbConnection();

        // Guard: cannot delete system roles
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT is_system FROM tenant_roles WHERE tenant_id = @tid AND id = @rid
            """;
        checkCmd.AddParameter("@tid", tenantId);
        checkCmd.AddParameter("@rid", roleId);
        var isSystem = await checkCmd.ExecuteScalarAsync(ct) as bool?;

        if (isSystem is null) return NotFound();
        if (isSystem == true)
            return BadRequest(new { error = "system_role", message = "System roles cannot be deleted." });

        // Cascade delete: role_permissions and member_roles are handled by FK ON DELETE CASCADE
        await using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM tenant_roles WHERE tenant_id = @tid AND id = @rid";
        delCmd.AddParameter("@tid", tenantId);
        delCmd.AddParameter("@rid", roleId);
        await delCmd.ExecuteNonQueryAsync(ct);

        await TenantMigrationService.RefreshEffectivePermissionsAsync(conn, ct);

        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private DbConnection GetDbConnection() =>
        (DbConnection)(HttpContext.Items["DbConnection"]
            ?? throw new InvalidOperationException("DB connection not available"));
}

public sealed record RoleResponse(
    Guid Id, string Name, string? Description,
    bool IsSystem, bool IsDefault, short Priority, DateTime CreatedAt);

public sealed record RoleDetailResponse(RoleResponse Role, List<RolePermissionResponse> Permissions);

public sealed record RolePermissionResponse(string Code, string DisplayName, short Effect);

public sealed record CreateRoleRequest(
    string Name, string? Description, bool IsDefault, int Priority,
    List<RolePermissionEntry> Permissions);

public sealed record UpdateRoleRequest(
    string Name, string? Description,
    List<RolePermissionEntry>? Permissions = null);

public sealed record RolePermissionEntry(string Code, string Effect); // "allow" or "deny"

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
