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

    // ── Public Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Refresh the effective permissions cache after role/permission changes.
    /// Must be called after INSERT/UPDATE/DELETE on roles, role_permissions,
    /// member_roles, or member_permissions.
    /// </summary>
    public static async Task RefreshEffectivePermissionsAsync(DbConnection conn, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();

        if (conn is Microsoft.Data.SqlClient.SqlConnection)
        {
            // MSSQL: recompile the stored procedure (it queries live tables)
            cmd.CommandText = "EXEC sp_recompile 'sp_GetEffectivePermissions'";
        }
        else
        {
            // PostgreSQL: refresh the materialized view
            cmd.CommandText = "REFRESH MATERIALIZED VIEW CONCURRENTLY tenant_effective_permissions";
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Internals ───────────────────────────────────────────────────────

    private static async Task EnsureMigrationTableAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = conn is Microsoft.Data.SqlClient.SqlConnection
            ? """
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '__migrations')
                CREATE TABLE __migrations (
                    id INT IDENTITY(1,1) PRIMARY KEY,
                    name VARCHAR(256) NOT NULL UNIQUE,
                    applied_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
                )
              """
            : """
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
        applyCmd.CommandText = migration.GetSql(conn);
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

public sealed record MigrationScript(string Name, string Sql, string? Mssql = null)
{
    /// <summary>Returns the SQL for the given connection type.</summary>
    public string GetSql(DbConnection conn) =>
        Mssql is not null && conn is Microsoft.Data.SqlClient.SqlConnection
            ? Mssql
            : Sql;
}

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

        new("002_authorization", PG_AUTH_SQL, MSSQL_AUTH_SQL),

        new("003_default_roles", PG_SEED_ROLES_SQL, MSSQL_SEED_ROLES_SQL),
    ];
}

public sealed record TenantRef(Guid Id, string Name, string Slug, string Status);

// ── Authorization migration SQL ────────────────────────────────────────────

file const string PG_AUTH_SQL = """
    -- Permissions catalog (immutable)
    CREATE TABLE IF NOT EXISTS permissions (
        id              UUID PRIMARY KEY,
        code            VARCHAR(128) NOT NULL UNIQUE,
        display_name    VARCHAR(128) NOT NULL,
        description     TEXT,
        category        VARCHAR(64) NOT NULL,
        is_deprecated   BOOLEAN NOT NULL DEFAULT FALSE,
        created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
    );

    -- Roles defined per tenant
    CREATE TABLE IF NOT EXISTS tenant_roles (
        id              UUID PRIMARY KEY,
        tenant_id       UUID NOT NULL,
        name            VARCHAR(64) NOT NULL,
        description     TEXT,
        is_default      BOOLEAN NOT NULL DEFAULT FALSE,
        is_system       BOOLEAN NOT NULL DEFAULT FALSE,
        priority        SMALLINT NOT NULL DEFAULT 0,
        created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        UNIQUE (tenant_id, name)
    );

    -- Role → permission mapping (allow/deny)
    CREATE TABLE IF NOT EXISTS tenant_role_permissions (
        tenant_id       UUID NOT NULL,
        role_id         UUID NOT NULL,
        permission_id   UUID NOT NULL,
        effect          SMALLINT NOT NULL,
        PRIMARY KEY (role_id, permission_id),
        FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles (tenant_id, id) ON DELETE CASCADE,
        FOREIGN KEY (permission_id) REFERENCES permissions(id)
    );

    -- User → role assignment
    CREATE TABLE IF NOT EXISTS tenant_member_roles (
        tenant_id       UUID NOT NULL,
        sub             VARCHAR(256) NOT NULL,
        role_id         UUID NOT NULL,
        assigned_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        assigned_by     VARCHAR(256),
        expires_at      TIMESTAMPTZ,
        PRIMARY KEY (tenant_id, sub, role_id),
        FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles (tenant_id, id) ON DELETE CASCADE
    );

    -- Direct permission overrides per user
    CREATE TABLE IF NOT EXISTS tenant_member_permissions (
        tenant_id       UUID NOT NULL,
        sub             VARCHAR(256) NOT NULL,
        permission_id   UUID NOT NULL,
        effect          SMALLINT NOT NULL,
        assigned_by     VARCHAR(256),
        assigned_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        expires_at      TIMESTAMPTZ,
        PRIMARY KEY (tenant_id, sub, permission_id),
        FOREIGN KEY (permission_id) REFERENCES permissions(id)
    );

    -- Effective permissions materialized view
    CREATE MATERIALIZED VIEW IF NOT EXISTS tenant_effective_permissions AS
    WITH direct_rules AS (
        SELECT tenant_id, sub, permission_id, effect,
               ROW_NUMBER() OVER (
                   PARTITION BY tenant_id, sub, permission_id
                   ORDER BY CASE WHEN expires_at IS NOT NULL AND expires_at <= NOW() THEN 1 ELSE 0 END,
                            CASE effect WHEN 2 THEN 1 WHEN 1 THEN 2 END
               ) AS rn
        FROM tenant_member_permissions
        WHERE expires_at IS NULL OR expires_at > NOW()
    ),
    direct_winner AS (
        SELECT tenant_id, sub, permission_id, effect FROM direct_rules WHERE rn = 1
    ),
    role_rules AS (
        SELECT mr.tenant_id, mr.sub, rp.permission_id, rp.effect,
               ROW_NUMBER() OVER (
                   PARTITION BY mr.tenant_id, mr.sub, rp.permission_id
                   ORDER BY CASE WHEN mr.expires_at IS NOT NULL AND mr.expires_at <= NOW() THEN 1 ELSE 0 END,
                            tr.priority DESC,
                            CASE rp.effect WHEN 2 THEN 1 WHEN 1 THEN 2 END
               ) AS rn
        FROM tenant_member_roles mr
        JOIN tenant_role_permissions rp ON rp.role_id = mr.role_id AND rp.tenant_id = mr.tenant_id
        JOIN tenant_roles tr ON tr.id = mr.role_id AND tr.tenant_id = mr.tenant_id
        WHERE mr.expires_at IS NULL OR mr.expires_at > NOW()
    ),
    role_winner AS (
        SELECT tenant_id, sub, permission_id, effect FROM role_rules WHERE rn = 1
    )
    SELECT tenant_id, sub, p.code AS permission
    FROM (
        SELECT tenant_id, sub, permission_id FROM direct_winner WHERE effect = 1
        UNION
        SELECT rw.tenant_id, rw.sub, rw.permission_id
        FROM role_winner rw
        WHERE rw.effect = 1
          AND NOT EXISTS (
              SELECT 1 FROM direct_winner dw
              WHERE dw.tenant_id = rw.tenant_id
                AND dw.sub = rw.sub
                AND dw.permission_id = rw.permission_id
          )
    ) resolved
    JOIN permissions p ON p.id = resolved.permission_id;

    -- Audit log
    CREATE TABLE IF NOT EXISTS tenant_authorization_audit (
        id              UUID PRIMARY KEY,
        tenant_id       UUID NOT NULL,
        actor_sub       VARCHAR(256) NOT NULL,
        target_sub      VARCHAR(256),
        action          VARCHAR(64) NOT NULL,
        payload         JSONB NOT NULL,
        created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
    );
    CREATE INDEX IF NOT EXISTS idx_audit_tenant_ts ON tenant_authorization_audit(tenant_id, created_at DESC);
    CREATE INDEX IF NOT EXISTS idx_audit_target ON tenant_authorization_audit(tenant_id, target_sub);

    -- Seed permissions
    INSERT INTO permissions (id, code, display_name, category) VALUES
      ('a0000001-0000-0000-0000-000000000001', 'users.read',     'View Users',     'Administration'),
      ('a0000001-0000-0000-0000-000000000002', 'users.create',   'Invite Users',   'Administration'),
      ('a0000001-0000-0000-0000-000000000003', 'users.update',   'Edit Users',     'Administration'),
      ('a0000001-0000-0000-0000-000000000004', 'users.delete',   'Remove Users',   'Administration'),
      ('a0000002-0000-0000-0000-000000000001', 'products.read',  'View Products',  'Products'),
      ('a0000002-0000-0000-0000-000000000002', 'products.create','Create Products','Products'),
      ('a0000002-0000-0000-0000-000000000003', 'products.update','Edit Products',  'Products'),
      ('a0000002-0000-0000-0000-000000000004', 'products.delete','Delete Products','Products'),
      ('a0000003-0000-0000-0000-000000000001', 'analytics.view', 'View Analytics', 'Analytics'),
      ('a0000003-0000-0000-0000-000000000002', 'reports.export', 'Export Reports', 'Analytics'),
      ('a0000004-0000-0000-0000-000000000001', 'dashboard.view', 'View Dashboard', 'Dashboard'),
      ('a0000005-0000-0000-0000-000000000001', 'billing.view',   'View Billing',   'Billing'),
      ('a0000006-0000-0000-0000-000000000001', 'settings.write', 'Edit Settings',  'Settings')
    ON CONFLICT (code) DO NOTHING;
    """;

file const string MSSQL_AUTH_SQL = """
    -- Permissions catalog (immutable)
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'permissions')
    CREATE TABLE permissions (
        id              UNIQUEIDENTIFIER PRIMARY KEY,
        code            VARCHAR(128) NOT NULL UNIQUE,
        display_name    VARCHAR(128) NOT NULL,
        description     NVARCHAR(MAX),
        category        VARCHAR(64) NOT NULL,
        is_deprecated   BIT NOT NULL DEFAULT 0,
        created_at      DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'tenant_roles')
    CREATE TABLE tenant_roles (
        id              UNIQUEIDENTIFIER PRIMARY KEY,
        tenant_id       UNIQUEIDENTIFIER NOT NULL,
        name            VARCHAR(64) NOT NULL,
        description     NVARCHAR(MAX),
        is_default      BIT NOT NULL DEFAULT 0,
        is_system       BIT NOT NULL DEFAULT 0,
        priority        SMALLINT NOT NULL DEFAULT 0,
        created_at      DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        updated_at      DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        UNIQUE (tenant_id, name)
    );

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'tenant_role_permissions')
    CREATE TABLE tenant_role_permissions (
        tenant_id       UNIQUEIDENTIFIER NOT NULL,
        role_id         UNIQUEIDENTIFIER NOT NULL,
        permission_id   UNIQUEIDENTIFIER NOT NULL,
        effect          SMALLINT NOT NULL,
        PRIMARY KEY (role_id, permission_id),
        FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles (tenant_id, id) ON DELETE CASCADE,
        FOREIGN KEY (permission_id) REFERENCES permissions(id)
    );

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'tenant_member_roles')
    CREATE TABLE tenant_member_roles (
        tenant_id       UNIQUEIDENTIFIER NOT NULL,
        sub             VARCHAR(256) NOT NULL,
        role_id         UNIQUEIDENTIFIER NOT NULL,
        assigned_at     DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        assigned_by     VARCHAR(256),
        expires_at      DATETIMEOFFSET,
        PRIMARY KEY (tenant_id, sub, role_id),
        FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles (tenant_id, id) ON DELETE CASCADE
    );

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'tenant_member_permissions')
    CREATE TABLE tenant_member_permissions (
        tenant_id       UNIQUEIDENTIFIER NOT NULL,
        sub             VARCHAR(256) NOT NULL,
        permission_id   UNIQUEIDENTIFIER NOT NULL,
        effect          SMALLINT NOT NULL,
        assigned_by     VARCHAR(256),
        assigned_at     DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        expires_at      DATETIMEOFFSET,
        PRIMARY KEY (tenant_id, sub, permission_id),
        FOREIGN KEY (permission_id) REFERENCES permissions(id)
    );

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'tenant_authorization_audit')
    CREATE TABLE tenant_authorization_audit (
        id              UNIQUEIDENTIFIER PRIMARY KEY,
        tenant_id       UNIQUEIDENTIFIER NOT NULL,
        actor_sub       VARCHAR(256) NOT NULL,
        target_sub      VARCHAR(256),
        action          VARCHAR(64) NOT NULL,
        payload         NVARCHAR(MAX) NOT NULL,
        created_at      DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT CK_audit_payload_json CHECK (ISJSON(payload) = 1)
    );

    -- Effective permissions stored procedure
    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'sp_GetEffectivePermissions')
        DROP PROCEDURE sp_GetEffectivePermissions;
    GO

    CREATE PROCEDURE sp_GetEffectivePermissions
        @tenant_id UNIQUEIDENTIFIER,
        @sub VARCHAR(256)
    AS
    BEGIN
        SET NOCOUNT ON;
        DECLARE @direct_denies TABLE (permission_id UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT INTO @direct_denies
        SELECT permission_id FROM tenant_member_permissions
        WHERE tenant_id = @tenant_id AND sub = @sub
          AND effect = 2 AND (expires_at IS NULL OR expires_at > SYSDATETIMEOFFSET());
        SELECT p.code
        FROM tenant_member_permissions d
        JOIN permissions p ON p.id = d.permission_id
        WHERE d.tenant_id = @tenant_id AND d.sub = @sub
          AND d.effect = 1 AND (d.expires_at IS NULL OR d.expires_at > SYSDATETIMEOFFSET())
          AND NOT EXISTS (SELECT 1 FROM @direct_denies dd WHERE dd.permission_id = d.permission_id)
        UNION
        SELECT p.code
        FROM (
            SELECT rp.permission_id,
                   ROW_NUMBER() OVER (PARTITION BY rp.permission_id ORDER BY tr.priority DESC) AS rn
            FROM tenant_member_roles mr
            JOIN tenant_role_permissions rp ON rp.role_id = mr.role_id AND rp.tenant_id = mr.tenant_id
            JOIN tenant_roles tr ON tr.id = mr.role_id AND tr.tenant_id = mr.tenant_id
            WHERE mr.tenant_id = @tenant_id AND mr.sub = @sub
              AND rp.effect = 1
              AND (mr.expires_at IS NULL OR mr.expires_at > SYSDATETIMEOFFSET())
              AND NOT EXISTS (SELECT 1 FROM @direct_denies dd WHERE dd.permission_id = rp.permission_id)
        ) ranked
        JOIN permissions p ON p.id = ranked.permission_id
        WHERE ranked.rn = 1
        ORDER BY p.code;
    END;
    GO

    -- Seed permissions
    MERGE permissions AS t
    USING (VALUES
      ('a0000001-0000-0000-0000-000000000001', 'users.read',     'View Users',     'Administration'),
      ('a0000001-0000-0000-0000-000000000002', 'users.create',   'Invite Users',   'Administration'),
      ('a0000001-0000-0000-0000-000000000003', 'users.update',   'Edit Users',     'Administration'),
      ('a0000001-0000-0000-0000-000000000004', 'users.delete',   'Remove Users',   'Administration'),
      ('a0000002-0000-0000-0000-000000000001', 'products.read',  'View Products',  'Products'),
      ('a0000002-0000-0000-0000-000000000002', 'products.create','Create Products','Products'),
      ('a0000002-0000-0000-0000-000000000003', 'products.update','Edit Products',  'Products'),
      ('a0000002-0000-0000-0000-000000000004', 'products.delete','Delete Products','Products'),
      ('a0000003-0000-0000-0000-000000000001', 'analytics.view', 'View Analytics', 'Analytics'),
      ('a0000003-0000-0000-0000-000000000002', 'reports.export', 'Export Reports', 'Analytics'),
      ('a0000004-0000-0000-0000-000000000001', 'dashboard.view', 'View Dashboard', 'Dashboard'),
      ('a0000005-0000-0000-0000-000000000001', 'billing.view',   'View Billing',   'Billing'),
      ('a0000006-0000-0000-0000-000000000001', 'settings.write', 'Edit Settings',  'Settings')
    ) AS s (id, code, display_name, category)
    ON t.code = s.code
    WHEN NOT MATCHED THEN INSERT (id, code, display_name, category) VALUES (s.id, s.code, s.display_name, s.category);
    """;

file const string PG_SEED_ROLES_SQL = """
    -- Seed built-in roles: Admin, Member, Viewer
    -- Uses DO block to handle idempotency per tenant
    DO $$
    DECLARE
        t record;
        admin_id UUID;
        member_id UUID;
        viewer_id UUID;
    BEGIN
        FOR t IN SELECT DISTINCT tenant_id FROM tenant_member_roles
                 UNION
                 SELECT DISTINCT tenant_id FROM tenant_roles
        LOOP
            -- Admin role
            INSERT INTO tenant_roles (id, tenant_id, name, is_system, is_default, priority)
            VALUES (gen_random_uuid(), t.tenant_id, 'Admin', true, false, 100)
            ON CONFLICT (tenant_id, name) DO NOTHING
            RETURNING id INTO admin_id;
            IF admin_id IS NULL THEN
                SELECT id INTO admin_id FROM tenant_roles WHERE tenant_id = t.tenant_id AND name = 'Admin';
            END IF;

            -- Member role
            INSERT INTO tenant_roles (id, tenant_id, name, is_system, is_default, priority)
            VALUES (gen_random_uuid(), t.tenant_id, 'Member', true, true, 50)
            ON CONFLICT (tenant_id, name) DO NOTHING
            RETURNING id INTO member_id;
            IF member_id IS NULL THEN
                SELECT id INTO member_id FROM tenant_roles WHERE tenant_id = t.tenant_id AND name = 'Member';
            END IF;

            -- Viewer role
            INSERT INTO tenant_roles (id, tenant_id, name, is_system, is_default, priority)
            VALUES (gen_random_uuid(), t.tenant_id, 'Viewer', true, false, 0)
            ON CONFLICT (tenant_id, name) DO NOTHING
            RETURNING id INTO viewer_id;
            IF viewer_id IS NULL THEN
                SELECT id INTO viewer_id FROM tenant_roles WHERE tenant_id = t.tenant_id AND name = 'Viewer';
            END IF;

            -- Admin: all permissions
            INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id, effect)
            SELECT t.tenant_id, admin_id, p.id, 1 FROM permissions p
            ON CONFLICT (role_id, permission_id) DO NOTHING;

            -- Member: read + create products, dashboard
            INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id, effect)
            SELECT t.tenant_id, member_id, p.id, 1 FROM permissions p
            WHERE p.code IN ('products.read', 'products.create', 'dashboard.view')
            ON CONFLICT (role_id, permission_id) DO NOTHING;

            -- Viewer: read only
            INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id, effect)
            SELECT t.tenant_id, viewer_id, p.id, 1 FROM permissions p
            WHERE p.code IN ('products.read', 'dashboard.view')
            ON CONFLICT (role_id, permission_id) DO NOTHING;
        END LOOP;
    END $$;
    """;

file const string MSSQL_SEED_ROLES_SQL = """
    -- Seed built-in roles per tenant
    DECLARE @tenant_id UNIQUEIDENTIFIER;
    DECLARE @admin_id UNIQUEIDENTIFIER;
    DECLARE @member_id UNIQUEIDENTIFIER;
    DECLARE @viewer_id UNIQUEIDENTIFIER;

    DECLARE tenant_cursor CURSOR FOR
        SELECT DISTINCT tenant_id FROM tenant_member_roles
        UNION
        SELECT DISTINCT tenant_id FROM tenant_roles;

    OPEN tenant_cursor;
    FETCH NEXT FROM tenant_cursor INTO @tenant_id;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Admin
        SET @admin_id = NEWID();
        IF NOT EXISTS (SELECT 1 FROM tenant_roles WHERE tenant_id = @tenant_id AND name = 'Admin')
            INSERT INTO tenant_roles (id, tenant_id, name, is_system, is_default, priority)
            VALUES (@admin_id, @tenant_id, 'Admin', 1, 0, 100);
        ELSE
            SELECT @admin_id = id FROM tenant_roles WHERE tenant_id = @tenant_id AND name = 'Admin';

        INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id, effect)
        SELECT @tenant_id, @admin_id, p.id, 1 FROM permissions p
        WHERE NOT EXISTS (
            SELECT 1 FROM tenant_role_permissions rp
            WHERE rp.role_id = @admin_id AND rp.permission_id = p.id
        );

        -- Member
        SET @member_id = NEWID();
        IF NOT EXISTS (SELECT 1 FROM tenant_roles WHERE tenant_id = @tenant_id AND name = 'Member')
            INSERT INTO tenant_roles (id, tenant_id, name, is_system, is_default, priority)
            VALUES (@member_id, @tenant_id, 'Member', 1, 1, 50);
        ELSE
            SELECT @member_id = id FROM tenant_roles WHERE tenant_id = @tenant_id AND name = 'Member';

        INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id, effect)
        SELECT @tenant_id, @member_id, p.id, 1 FROM permissions p
        WHERE p.code IN ('products.read', 'products.create', 'dashboard.view')
          AND NOT EXISTS (
              SELECT 1 FROM tenant_role_permissions rp
              WHERE rp.role_id = @member_id AND rp.permission_id = p.id
          );

        -- Viewer
        SET @viewer_id = NEWID();
        IF NOT EXISTS (SELECT 1 FROM tenant_roles WHERE tenant_id = @tenant_id AND name = 'Viewer')
            INSERT INTO tenant_roles (id, tenant_id, name, is_system, is_default, priority)
            VALUES (@viewer_id, @tenant_id, 'Viewer', 1, 0, 0);
        ELSE
            SELECT @viewer_id = id FROM tenant_roles WHERE tenant_id = @tenant_id AND name = 'Viewer';

        INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id, effect)
        SELECT @tenant_id, @viewer_id, p.id, 1 FROM permissions p
        WHERE p.code IN ('products.read', 'dashboard.view')
          AND NOT EXISTS (
              SELECT 1 FROM tenant_role_permissions rp
              WHERE rp.role_id = @viewer_id AND rp.permission_id = p.id
          );

        FETCH NEXT FROM tenant_cursor INTO @tenant_id;
    END;

    CLOSE tenant_cursor;
    DEALLOCATE tenant_cursor;
    """;
