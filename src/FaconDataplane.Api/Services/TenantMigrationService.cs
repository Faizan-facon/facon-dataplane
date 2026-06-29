using System.Data.Common;

namespace FaconDataplane.Api.Services;

/// <summary>
/// Runs database migrations against tenant-isolated databases.
/// Migrations are idempotent — each script is tracked in a __migrations table
/// and only executed once per tenant.
/// </summary>
public sealed class TenantMigrationService
{
    private readonly DbConnectionMiddleware _dbMiddleware;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TenantMigrationService> _logger;

    public TenantMigrationService(
        IHttpClientFactory httpFactory,
        ILogger<TenantMigrationService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Run all pending migrations for a tenant. Safe to call multiple times —
    /// already-applied migrations are skipped.
    /// </summary>
    public async Task MigrateTenantAsync(
        Guid tenantId, string bearerJwt, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting migrations for tenant {TenantId}", tenantId);

        // Fetch credentials from control plane
        var client = _httpFactory.CreateClient("ControlPlane");
        if (!string.IsNullOrEmpty(bearerJwt))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerJwt);
        }

        var response = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/credentials/resources/Database/fetch?purpose=Application", null, ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Failed to fetch DB credentials for tenant {tenantId}: HTTP {(int)response.StatusCode}");

        var resource = await response.Content.ReadFromJsonAsync<ResolvedResourceDescriptor>(ct);
        if (resource is null)
            throw new InvalidOperationException("Credential fetch returned null");

        // Create connection and run migrations
        await using var conn = DbConnectionFactory.CreateConnection(resource);
        await conn.OpenAsync(ct);

        // Ensure migrations tracking table exists
        await EnsureMigrationTableAsync(conn, ct);

        // Run each migration in order
        foreach (var migration in Migrations.All)
        {
            await ApplyMigrationIfNeededAsync(conn, tenantId, migration, ct);
        }

        _logger.LogInformation("Migrations complete for tenant {TenantId}", tenantId);
    }

    /// <summary>
    /// Run migrations for all tenants in an organization.
    /// </summary>
    public async Task MigrateOrganizationAsync(
        Guid organizationId, string bearerJwt, CancellationToken ct = default)
    {
        // Resolve tenants from control plane
        var client = _httpFactory.CreateClient("ControlPlane");
        if (!string.IsNullOrEmpty(bearerJwt))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerJwt);
        }

        var response = await client.GetAsync(
            $"/api/v1/tenants/by-organization/{organizationId}", ct);
        response.EnsureSuccessStatusCode();

        var tenants = await response.Content.ReadFromJsonAsync<List<TenantRef>>(ct);
        if (tenants is null) return;

        foreach (var tenant in tenants)
        {
            try
            {
                await MigrateTenantAsync(tenant.Id, bearerJwt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Migration failed for tenant {TenantId}", tenant.Id);
            }
        }
    }

    // ── Internals ───────────────────────────────────────────────────────

    private static async Task EnsureMigrationTableAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS __migrations (
                id SERIAL PRIMARY KEY,
                name VARCHAR(256) NOT NULL UNIQUE,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ApplyMigrationIfNeededAsync(
        DbConnection conn, Guid tenantId, MigrationScript migration, CancellationToken ct)
    {
        // Check if already applied
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT 1 FROM __migrations WHERE name = @name";
        checkCmd.AddParameter("@name", migration.Name);

        var alreadyApplied = await checkCmd.ExecuteScalarAsync(ct) is not null;
        if (alreadyApplied)
        {
            _logger.LogDebug("Migration {Name} already applied for tenant {TenantId}", migration.Name, tenantId);
            return;
        }

        // Apply migration
        _logger.LogInformation("Applying migration {Name} for tenant {TenantId}", migration.Name, tenantId);
        await using var applyCmd = conn.CreateCommand();
        applyCmd.CommandText = migration.Sql;
        await applyCmd.ExecuteNonQueryAsync(ct);

        // Record as applied
        await using var recordCmd = conn.CreateCommand();
        recordCmd.CommandText = "INSERT INTO __migrations (name) VALUES (@name)";
        recordCmd.AddParameter("@name", migration.Name);
        await recordCmd.ExecuteNonQueryAsync(ct);
    }

    // ── Helper ──────────────────────────────────────────────────────────

    private static class DbCommandExtensions
    {
        public static void AddParameter(this DbCommand cmd, string name, object? value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }
    }
}

// ── Migration definitions ─────────────────────────────────────────────────

public sealed record MigrationScript(string Name, string Sql);

public static class Migrations
{
    public static readonly MigrationScript[] All =
    [
        new("001_initial_schema", """
            CREATE TABLE IF NOT EXISTS products (
                id UUID PRIMARY KEY,
                tenant_id UUID NOT NULL,
                name VARCHAR(256) NOT NULL,
                price DECIMAL(10,2) NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_products_tenant ON products(tenant_id);
            """),
        // Add new migrations here in order:
        // new("002_add_category", "ALTER TABLE products ADD COLUMN category VARCHAR(128);"),
    ];
}

public sealed record TenantRef(Guid Id, string Name, string Slug, string Status);
