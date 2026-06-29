namespace FaconDataplane.Api.Middleware;

/// <summary>
/// Enriches every log entry with tenant context (TenantId, TenantSlug, UserId).
/// Must run AFTER TenantResolutionMiddleware so HttpContext.Items is populated.
/// Uses ILogger.BeginScope so all downstream logs inherit these properties automatically.
/// </summary>
public sealed class TenantLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantLoggingMiddleware> _logger;

    public TenantLoggingMiddleware(RequestDelegate next, ILogger<TenantLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantId = context.Items["TenantId"] as Guid?;
        var tenantSlug = context.Items["TenantSlug"] as string;
        var userId = context.User?.FindFirstValue("sub");

        var scopeData = new Dictionary<string, object?>(3);
        if (tenantId.HasValue) scopeData["TenantId"] = tenantId.Value;
        if (!string.IsNullOrEmpty(tenantSlug)) scopeData["TenantSlug"] = tenantSlug;
        if (!string.IsNullOrEmpty(userId)) scopeData["UserId"] = userId;

        using (_logger.BeginScope(scopeData))
        {
            await _next(context);
        }
    }
}
