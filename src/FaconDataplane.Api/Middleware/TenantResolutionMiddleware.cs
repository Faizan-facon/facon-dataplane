namespace FaconDataplane.Api.Middleware;

/// <summary>
/// Resolves tenant context by forwarding the user's Keycloak Bearer JWT
/// to the control plane's GET /api/v1/me endpoint.
/// Caches result for 30 seconds per user sub.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only resolve for authenticated requests
        if (context.User.Identity?.IsAuthenticated != true)
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

        var cacheKey = $"tenant-resolution:{sub}";
        if (!_cache.TryGetValue(cacheKey, out TenantContext? tenantCtx))
        {
            tenantCtx = await ResolveFromControlPlaneAsync(context, sub);

            if (tenantCtx is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("{\"error\":\"No active tenant found\"}");
                return;
            }

            _cache.Set(cacheKey, tenantCtx, TimeSpan.FromSeconds(30));
        }

        context.Items["TenantId"] = tenantCtx!.TenantId;
        context.Items["TenantSlug"] = tenantCtx.TenantSlug;
        context.Items["TenantName"] = tenantCtx.TenantName;
        context.Items["OrganizationId"] = tenantCtx.OrganizationId;
        context.Items["OrganizationName"] = tenantCtx.OrganizationName;
        context.Items["TenantStatus"] = tenantCtx.TenantStatus;
        context.Items["SubscriptionStatus"] = tenantCtx.SubscriptionStatus;
        context.Items["PlanKey"] = tenantCtx.PlanKey;

        await _next(context);
    }

    private async Task<TenantContext?> ResolveFromControlPlaneAsync(HttpContext context, string sub)
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

        var response = await client.GetAsync("/api/v1/me");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Tenant resolution failed for sub={Sub}: HTTP {Status}", sub, (int)response.StatusCode);
            return null;
        }

        var profile = await response.Content.ReadFromJsonAsync<MyProfileResponse>();
        if (profile is null || !profile.IsOnboarded)
        {
            _logger.LogWarning("Tenant resolution: user {Sub} not onboarded", sub);
            return null;
        }

        // Pick the first active tenant
        var tenant = profile.Tenants?.FirstOrDefault(t =>
            string.Equals(t.Status, "Active", StringComparison.OrdinalIgnoreCase));

        if (tenant is null)
        {
            _logger.LogWarning("Tenant resolution: user {Sub} has no active tenants", sub);
            return null;
        }

        _logger.LogInformation(
            "Tenant resolved: sub={Sub} tenant={TenantId} slug={Slug}", sub, tenant.Id, tenant.Slug);

        return new TenantContext(tenant.Id, tenant.Slug, tenant.Name,
            profile.OrganizationId, profile.OrganizationName,
            tenant.Status, tenant.SubscriptionStatus ?? "Unknown",
            tenant.PlanKey ?? "Trial");
    }
}

public sealed record TenantContext(
    Guid TenantId,
    string TenantSlug,
    string TenantName,
    Guid OrganizationId,
    string OrganizationName,
    string TenantStatus,
    string SubscriptionStatus,
    string PlanKey);

public sealed record MyProfileResponse(
    bool IsOnboarded,
    string UserId,
    Guid OrganizationId,
    string OrganizationName,
    List<TenantSummary> Tenants);

public sealed record TenantSummary(
    Guid Id,
    string Name,
    string Slug,
    string Status,
    string? SubscriptionStatus,
    string? PlanKey);
