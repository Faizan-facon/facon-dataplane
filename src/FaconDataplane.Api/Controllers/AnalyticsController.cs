using FaconDataplane.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FaconDataplane.Api.Controllers;

/// <summary>
/// Example feature-gated controller. Requires "analytics:view" on the controller
/// (Pro plan and above) and "reports:export" on the export endpoint (Pro+).
/// </summary>
[ApiController]
[Route("api/analytics")]
[Authorize]
[RequireFeature("analytics:view")]
public class AnalyticsController : ControllerBase
{
    [HttpGet("dashboard")]
    public IActionResult GetDashboard()
    {
        // TenantId from middleware
        var tenantId = HttpContext.Items["TenantId"];
        return Ok(new { tenantId, metrics = new { users = 42, revenue = 15800m } });
    }

    [HttpGet("export")]
    [RequireFeature("reports:export")]
    public IActionResult ExportReport()
    {
        var tenantId = HttpContext.Items["TenantId"];
        return Ok(new { tenantId, report = "CSV data would stream here", generatedAt = DateTime.UtcNow });
    }
}
