using System.Net.Http.Json;

namespace FaconDataplane.Api.Services;

/// <summary>
/// Wraps HTTP calls to the FaconControlPlane for quota management
/// and entitlement/feature-gate resolution.
/// All calls forward the user's Keycloak Bearer JWT for authorization.
/// </summary>
public sealed class ControlPlaneService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ControlPlaneService> _logger;

    public ControlPlaneService(IHttpClientFactory httpFactory, ILogger<ControlPlaneService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    // ── Entitlements / Feature Gate ──────────────────────────────────────

    /// <summary>
    /// Fetch the full entitlement grant — plan key, quota ledgers.
    /// Used by the feature gate to determine which modules are accessible.
    /// </summary>
    public async Task<EntitlementGrant?> GetEntitlementsAsync(
        Guid tenantId, string bearerJwt, CancellationToken ct = default)
    {
        var client = CreateClient(bearerJwt);
        var response = await client.GetAsync(
            $"api/v1/tenants/{tenantId}/entitlements", ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Entitlements fetch failed: HTTP {Status}", (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<EntitlementGrant>(ct);
    }

    // ── Quota Operations ─────────────────────────────────────────────────

    public const string DimensionSeats = "Seats";
    public const string DimensionStorage = "StorageBytes";
    public const string DimensionRpm = "RequestsPerMinute";

    /// <summary>Read-only quota check. Safe for high-frequency calls.</summary>
    public async Task<bool> CheckQuotaAsync(
        Guid tenantId, string dimension, long amount, string bearerJwt, CancellationToken ct = default)
    {
        var client = CreateClient(bearerJwt);
        var response = await client.GetAsync(
            $"api/v1/tenants/{tenantId}/entitlements/quota/{dimension}/check?amount={amount}", ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Quota check failed: {Dimension}={Amount}, HTTP {Status}",
                dimension, amount, (int)response.StatusCode);
            return false;
        }

        var result = await response.Content.ReadFromJsonAsync<bool>(ct);
        return result;
    }

    /// <summary>Record committed usage. Throws on 422 (quota exceeded).</summary>
    public async Task<bool> ConsumeQuotaAsync(
        Guid tenantId, string dimension, long amount, string bearerJwt, CancellationToken ct = default)
    {
        var client = CreateClient(bearerJwt);
        var response = await client.PostAsJsonAsync(
            $"api/v1/tenants/{tenantId}/entitlements/quota/{dimension}/consume",
            new { amount }, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            _logger.LogWarning("Quota exceeded: {Dimension}={Amount} for tenant {TenantId}",
                dimension, amount, tenantId);
            return false;
        }

        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Consumed {Amount} {Dimension} for tenant {TenantId}",
            amount, dimension, tenantId);
        return true;
    }

    /// <summary>Free committed usage. Safe to call even if tracking is off.</summary>
    public async Task ReleaseQuotaAsync(
        Guid tenantId, string dimension, long amount, string bearerJwt, CancellationToken ct = default)
    {
        var client = CreateClient(bearerJwt);
        var response = await client.PostAsJsonAsync(
            $"api/v1/tenants/{tenantId}/entitlements/quota/{dimension}/release",
            new { amount }, ct);

        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Released {Amount} {Dimension} for tenant {TenantId}",
            amount, dimension, tenantId);
    }

    /// <summary>Two-phase reserve — soft hold before provisioning.</summary>
    public async Task<bool> ReserveQuotaAsync(
        Guid tenantId, string dimension, long amount, string bearerJwt, CancellationToken ct = default)
    {
        var client = CreateClient(bearerJwt);
        var response = await client.PostAsJsonAsync(
            $"api/v1/tenants/{tenantId}/entitlements/quota/{dimension}/reserve",
            new { amount }, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            return false;

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Commit a prior reservation.</summary>
    public async Task CommitReservationAsync(
        Guid tenantId, string dimension, long amount, string bearerJwt, CancellationToken ct = default)
    {
        var client = CreateClient(bearerJwt);
        var response = await client.PostAsJsonAsync(
            $"api/v1/tenants/{tenantId}/entitlements/quota/{dimension}/commit",
            new { amount }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Cancel a prior reservation (e.g. provisioning failed).</summary>
    public async Task CancelReservationAsync(
        Guid tenantId, string dimension, long amount, string bearerJwt, CancellationToken ct = default)
    {
        var client = CreateClient(bearerJwt);
        var response = await client.PostAsJsonAsync(
            $"api/v1/tenants/{tenantId}/entitlements/quota/{dimension}/cancel-reservation",
            new { amount }, ct);
        response.EnsureSuccessStatusCode();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private HttpClient CreateClient(string bearerJwt)
    {
        var client = _httpFactory.CreateClient("ControlPlane");
        if (!string.IsNullOrEmpty(bearerJwt))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerJwt);
        }
        return client;
    }
}

// ── Models ────────────────────────────────────────────────────────────────

public sealed class EntitlementGrant
{
    public Guid TenantId { get; set; }
    public string PlanKey { get; set; } = string.Empty;
    public DateTimeOffset ComputedAt { get; set; }
    public QuotaInfo Seats { get; set; } = new();
    public QuotaInfo Storage { get; set; } = new();
    public QuotaInfo Rpm { get; set; } = new();
}

public sealed class QuotaInfo
{
    public long Limit { get; set; }
    public long CurrentUsage { get; set; }
    public long Reserved { get; set; }
    public long Available => Limit - CurrentUsage - Reserved;
}
