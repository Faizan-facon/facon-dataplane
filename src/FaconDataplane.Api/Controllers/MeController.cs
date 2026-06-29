using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FaconDataplane.Api.Controllers;

/// <summary>
/// Returns the tenant context resolved by TenantResolutionMiddleware.
/// The frontend calls this to get plan key, subscription status, and tenant info.
/// No DB connection needed — reads from HttpContext.Items only.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/me")]
[Authorize]
public class MeController : ControllerBase
{
    /// <summary>
    /// Returns the current user's tenant context.
    /// Data comes from TenantResolutionMiddleware (cached from control plane).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetProfile()
    {
        var tenantId = HttpContext.Items["TenantId"] as Guid?;
        var slug = HttpContext.Items["TenantSlug"] as string;
        var name = HttpContext.Items["TenantName"] as string;
        var orgId = HttpContext.Items["OrganizationId"] as Guid?;
        var orgName = HttpContext.Items["OrganizationName"] as string;
        var tenantStatus = HttpContext.Items["TenantStatus"] as string ?? "Active";
        var subscriptionStatus = HttpContext.Items["SubscriptionStatus"] as string ?? "Unknown";
        var planKey = HttpContext.Items["PlanKey"] as string ?? "Trial";
        var userId = User.FindFirstValue("sub");

        if (!tenantId.HasValue)
        {
            return Unauthorized(new { error = "Tenant not resolved" });
        }

        return Ok(new
        {
            isOnboarded = true,
            userId,
            organizationId = orgId,
            organizationName = orgName,
            tenants = new[]
            {
                new
                {
                    id = tenantId.Value,
                    slug,
                    name,
                    status = tenantStatus,
                    subscriptionStatus,
                    planKey,
                }
            }
        });
    }
}
