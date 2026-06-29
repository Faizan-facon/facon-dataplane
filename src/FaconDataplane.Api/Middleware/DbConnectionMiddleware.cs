using System.Net.Http.Json;
using FaconDataplane.Api.Services;

namespace FaconDataplane.Api.Middleware;

/// <summary>
/// Fetches per-tenant database credentials from the control plane
/// and opens a scoped Npgsql connection. Attaches the connection
/// to HttpContext.Items for use by controllers.
/// </summary>
public sealed class DbConnectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TenantConnectionPool _pool;
    private readonly ILogger<DbConnectionMiddleware> _logger;

    public DbConnectionMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpFactory,
        TenantConnectionPool pool,
        ILogger<DbConnectionMiddleware> logger)
    {
        _next = next;
        _httpFactory = httpFactory;
        _pool = pool;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Items.ContainsKey("TenantId") ||
            context.Items["TenantId"] is not Guid tenantId)
        {
            await _next(context);
            return;
        }

        var connection = await _pool.GetConnectionAsync(
            tenantId,
            async () => await FetchCredentialAsync(tenantId, context),
            context.RequestAborted);

        context.Items["DbConnection"] = connection;

        try
        {
            await _next(context);
        }
        finally
        {
            await _pool.ReturnAsync(tenantId, connection);
        }
    }

    private async Task<DbCredential> FetchCredentialAsync(Guid tenantId, HttpContext context)
    {
        var client = _httpFactory.CreateClient("ControlPlane");

        // Forward the user's Bearer JWT
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", authHeader.Replace("Bearer ", ""));
        }

        var url = $"/api/v1/tenants/{tenantId}/credentials/resources/Database/fetch?purpose=Application";
        var response = await client.PostAsync(url, null);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Credential fetch failed: HTTP {(int)response.StatusCode}");

        var resource = await response.Content.ReadFromJsonAsync<ResolvedResourceDescriptor>();
        if (resource is null)
            throw new InvalidOperationException("Credential fetch returned null");

        var connString = BuildConnectionString(resource);
        _logger.LogInformation(
            "Fetched DB cred for tenant {TenantId}, expires {Expires}",
            tenantId, resource.Lease.ExpiresAt);

        return new DbCredential(connString, resource.Lease.ExpiresAt);
    }

    private static string BuildConnectionString(ResolvedResourceDescriptor r)
    {
        var t = r.Topology;
        var c = r.Auth.Credentials;
        return $"Host={t.Endpoint};Port={t.Port};Database={t.Database};" +
               $"Username={c.GetValueOrDefault("username", "")};Password={c.GetValueOrDefault("password", "")}";
    }
}

// ── Control Plane response models ─────────────────────────────────────────

public sealed record ResolvedResourceDescriptor(
    string ResourceType,
    string Engine,
    ResolvedTopology Topology,
    ResolvedAuth Auth,
    ResolvedLease Lease,
    Dictionary<string, string>? Options);

public sealed record ResolvedTopology(
    string Endpoint,
    int Port,
    string? Database);

public sealed record ResolvedAuth(
    string AuthType,
    Dictionary<string, string> Credentials);

public sealed record ResolvedLease(
    string LeaseId,
    DateTimeOffset ExpiresAt);
