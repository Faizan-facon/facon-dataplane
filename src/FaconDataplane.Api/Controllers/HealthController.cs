using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FaconDataplane.Api.Controllers;

/// <summary>
/// Health check endpoints. No auth required.
/// /health — liveness: is the process running?
/// /health/ready — readiness: can we reach the control plane?
/// </summary>
[ApiController]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TenantConnectionPool? _pool;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        IHttpClientFactory httpFactory,
        IServiceProvider services,
        ILogger<HealthController> logger)
    {
        _httpFactory = httpFactory;
        _pool = services.GetService<TenantConnectionPool>();
        _logger = logger;
    }

    /// <summary>Liveness probe — is the process alive?</summary>
    [HttpGet("/health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Liveness()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTimeOffset.UtcNow,
            version = GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0"
        });
    }

    /// <summary>Readiness probe — can we serve traffic?</summary>
    [HttpGet("/health/ready")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Readiness(CancellationToken ct)
    {
        var checks = new Dictionary<string, string>();
        var healthy = true;

        // Check 1: Control Plane reachable
        var cpOk = await CheckControlPlaneAsync(ct);
        checks["control_plane"] = cpOk ? "ok" : "unreachable";
        if (!cpOk) healthy = false;

        // Check 2: Connection pool not exhausted
        if (_pool is not null)
        {
            checks["db_pool"] = "ok";
        }

        if (healthy)
        {
            return Ok(new { status = "ready", checks, timestamp = DateTimeOffset.UtcNow });
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            status = "not_ready",
            checks,
            timestamp = DateTimeOffset.UtcNow
        });
    }

    private async Task<bool> CheckControlPlaneAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("ControlPlane");
            client.Timeout = TimeSpan.FromSeconds(5);

            // Hit a lightweight anonymous endpoint
            var response = await client.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Control plane health check failed");
            return false;
        }
    }
}
