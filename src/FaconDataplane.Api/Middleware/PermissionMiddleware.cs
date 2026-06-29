using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace FaconDataplane.Api.Middleware;

/// <summary>
/// Resolves effective permissions for the current user+tenant from the
/// authorization tables. Queries the materialized view (PostgreSQL) or
/// stored procedure (SQL Server). Results cached for 30 seconds.
/// Must run AFTER DbConnectionMiddleware and BEFORE controllers.
/// </summary>
public sealed class PermissionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PermissionMiddleware> _logger;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public PermissionMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        ILogger<PermissionMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only resolve for authenticated requests with tenant context
        if (context.User.Identity?.IsAuthenticated != true ||
            context.Items["TenantId"] is not Guid tenantId)
        {
            await _next(context);
            return;
        }

        var sub = context.User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
        {
            await _next(context);
            return;
        }

        var cacheKey = $"perms:{tenantId}:{sub}";

        if (!_cache.TryGetValue(cacheKey, out HashSet<string>? permissions))
        {
            var connection = context.Items["DbConnection"] as DbConnection;

            if (connection is not null && connection.State == ConnectionState.Open)
            {
                permissions = await LoadPermissionsAsync(connection, tenantId, sub);
                _cache.Set(cacheKey, permissions, CacheTtl);
            }
            else
            {
                _logger.LogWarning(
                    "PermissionMiddleware: no open DB connection for tenant {TenantId}. Skipping permission resolution.",
                    tenantId);
                permissions = new HashSet<string>();
            }
        }

        context.Items["Permissions"] = permissions!;
        await _next(context);
    }

    private async Task<HashSet<string>> LoadPermissionsAsync(
        DbConnection conn, Guid tenantId, string sub)
    {
        var perms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();

        if (conn is SqlConnection)
        {
            cmd.CommandText = "sp_GetEffectivePermissions";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.AddParameter("@tenant_id", tenantId);
            cmd.AddParameter("@sub", sub);
        }
        else
        {
            cmd.CommandText = """
                SELECT permission
                FROM tenant_effective_permissions
                WHERE tenant_id = @tid AND sub = @sub
                """;
            cmd.AddParameter("@tid", tenantId);
            cmd.AddParameter("@sub", sub);
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            perms.Add(reader.GetString(0));

        _logger.LogDebug(
            "Resolved {Count} permissions for sub={Sub} tenant={TenantId}",
            perms.Count, sub, tenantId);

        return perms;
    }
}

/// <summary>
/// Extension to add parameters to DbCommand in a provider-agnostic way.
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
