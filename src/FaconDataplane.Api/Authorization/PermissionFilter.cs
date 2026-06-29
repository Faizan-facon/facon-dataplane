using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FaconDataplane.Api.Authorization;

/// <summary>
/// Action filter that enforces <see cref="RequirePermissionAttribute"/>.
/// Reads resolved permissions from HttpContext.Items["Permissions"] (set by PermissionMiddleware).
/// Returns 403 if any required permission is missing.
/// </summary>
public sealed class PermissionFilter : IAsyncActionFilter
{
    private readonly ILogger<PermissionFilter> _logger;

    public PermissionFilter(ILogger<PermissionFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var attrs = context.ActionDescriptor.EndpointMetadata
            .OfType<RequirePermissionAttribute>()
            .ToList();

        if (attrs.Count == 0)
        {
            await next();
            return;
        }

        // Platform admins bypass permission checks
        if (context.HttpContext.User.IsInRole("platform-admin"))
        {
            await next();
            return;
        }

        var permissions = context.HttpContext.Items["Permissions"] as HashSet<string>;
        if (permissions is null)
        {
            _logger.LogWarning("PermissionFilter: no permissions resolved for request. Ensure PermissionMiddleware runs before this filter.");
            context.Result = new ObjectResult(new
            {
                error = "permission_not_resolved",
                message = "Permissions have not been resolved for this request."
            })
            { StatusCode = StatusCodes.Status500InternalServerError };
            return;
        }

        var missing = new List<string>();
        foreach (var attr in attrs)
        {
            if (!permissions.Contains(attr.Permission))
                missing.Add(attr.Permission);
        }

        if (missing.Count > 0)
        {
            _logger.LogInformation(
                "Permission denied: user={Sub} missing {Missing}",
                context.HttpContext.User.FindFirstValue("sub"),
                string.Join(", ", missing));

            context.Result = new ObjectResult(new
            {
                error = "forbidden",
                message = $"Requires permission(s): {string.Join(", ", missing)}.",
                missingPermissions = missing
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        await next();
    }
}
