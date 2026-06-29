using FaconDataplane.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FaconDataplane.Api.Authorization;

/// <summary>
/// Action filter that enforces <see cref="RequireFeatureAttribute"/>.
/// Checks the tenant's plan-based feature set against the declared requirements.
/// Returns 402 Payment Required if the tenant's plan doesn't include the feature,
/// or 503 if the control plane is unreachable (cannot verify).
/// </summary>
public sealed class FeatureGateFilter : IAsyncActionFilter
{
    private readonly FeatureGateService _featureGate;
    private readonly ILogger<FeatureGateFilter> _logger;

    public FeatureGateFilter(FeatureGateService featureGate, ILogger<FeatureGateFilter> logger)
    {
        _featureGate = featureGate;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Gather all [RequireFeature] attributes from the action and controller
        var attributes = context.ActionDescriptor.EndpointMetadata
            .OfType<RequireFeatureAttribute>()
            .ToList();

        if (attributes.Count == 0)
        {
            await next();
            return;
        }

        var httpContext = context.HttpContext;

        // Must have tenant resolved
        if (httpContext.Items["TenantId"] is not Guid tenantId)
        {
            context.Result = new ObjectResult(new
            {
                error = "feature_gate_unauthorized",
                message = "Tenant not resolved. Ensure the request is authenticated."
            })
            { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        // Extract Bearer JWT from the incoming request
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        var bearerJwt = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..]
            : string.Empty;

        // Fetch features from control plane (cached 60s)
        var features = await _featureGate.GetFeaturesAsync(tenantId, bearerJwt, context.HttpContext.RequestAborted);

        if (features is null)
        {
            _logger.LogWarning("Feature gate: control plane unreachable for tenant {TenantId}", tenantId);
            context.Result = new ObjectResult(new
            {
                error = "feature_gate_unavailable",
                message = "Unable to verify feature access. Control plane unreachable."
            })
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }

        // Check all required features (AND logic)
        var missing = new List<string>();
        foreach (var attr in attributes)
        {
            if (!FeatureGateService.IsKnownFeature(attr.Feature))
            {
                _logger.LogWarning("Feature gate: unknown feature '{Feature}' declared", attr.Feature);
                context.Result = new ObjectResult(new
                {
                    error = "feature_gate_unknown",
                    message = $"Feature '{attr.Feature}' is not recognized."
                })
                { StatusCode = StatusCodes.Status500InternalServerError };
                return;
            }

            if (!features.Contains(attr.Feature))
                missing.Add(attr.Feature);
        }

        if (missing.Count > 0)
        {
            _logger.LogInformation(
                "Feature gate denied: tenant {TenantId} missing {MissingFeatures}",
                tenantId, string.Join(", ", missing));

            context.Result = new ObjectResult(new
            {
                error = "feature_gate_denied",
                message = $"Your plan does not include: {string.Join(", ", missing)}.",
                missingFeatures = missing
            })
            { StatusCode = StatusCodes.Status402PaymentRequired };
            return;
        }

        // All features granted — proceed
        await next();
    }
}
