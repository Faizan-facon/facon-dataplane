namespace FaconDataplane.Api.Middleware;

/// <summary>
/// Enforces tenant lifecycle and subscription status.
/// Blocks requests from suspended, cancelled, or past-due tenants.
/// Must run AFTER TenantResolutionMiddleware.
/// </summary>
public sealed class SubscriptionEnforcementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SubscriptionEnforcementMiddleware> _logger;

    // Statuses that block access
    private static readonly HashSet<string> BlockedTenantStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Suspended", "Cancelled", "Deprovisioning", "Deprovisioned"
    };

    private static readonly HashSet<string> BlockedSubscriptionStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PastDue", "Cancelled", "Expired", "Suspended"
    };

    // Endpoints that are always allowed (health checks, etc.)
    private static readonly string[] AllowAnonymousPaths = { "/health", "/health/ready" };

    public SubscriptionEnforcementMiddleware(RequestDelegate next, ILogger<SubscriptionEnforcementMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip health endpoints
        var path = context.Request.Path.Value ?? "";
        if (AllowAnonymousPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Skip unauthenticated requests (they'll get 401 from auth middleware anyway)
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var tenantStatus = context.Items["TenantStatus"] as string;
        var subscriptionStatus = context.Items["SubscriptionStatus"] as string;

        // No tenant context yet — let downstream handle it
        if (tenantStatus is null)
        {
            await _next(context);
            return;
        }

        // ── Tenant lifecycle check ────────────────────────────────────
        if (BlockedTenantStatuses.Contains(tenantStatus))
        {
            _logger.LogWarning(
                "Access denied: tenant status is {TenantStatus}", tenantStatus);

            context.Response.StatusCode = StatusCodes.Status423Locked;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                $"{{\"error\":\"tenant_{tenantStatus.ToLower()}\",\"message\":\"Tenant is {tenantStatus}. Access is locked.\"}}");
            return;
        }

        // ── Subscription status check ────────────────────────────────
        if (subscriptionStatus is not null &&
            subscriptionStatus != "Unknown" &&
            BlockedSubscriptionStatuses.Contains(subscriptionStatus))
        {
            _logger.LogWarning(
                "Access denied: subscription status is {SubscriptionStatus}", subscriptionStatus);

            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                $"{{\"error\":\"subscription_{subscriptionStatus.ToLower()}\",\"message\":\"Subscription is {subscriptionStatus}. Please update your payment method.\"}}");
            return;
        }

        await _next(context);
    }
}
