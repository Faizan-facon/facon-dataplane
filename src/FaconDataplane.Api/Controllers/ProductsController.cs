using System.Data.Common;
using Asp.Versioning;
using FaconDataplane.Api.Authorization;
using FaconDataplane.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FaconDataplane.Api.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/products")]
[Authorize]
[RequireFeature("products:read")] // Base feature gate for all product endpoints
public class ProductsController : ControllerBase
{
    private readonly ControlPlaneService _cp;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(ControlPlaneService cp, ILogger<ProductsController> logger)
    {
        _cp = cp;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetProducts(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var connection = GetDbConnection();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, price, created_at FROM products WHERE tenant_id = @tid ORDER BY created_at DESC";
        cmd.AddParameter("@tid", tenantId);

        var products = new List<ProductResponse>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            products.Add(new ProductResponse(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetDateTime(3)));
        }

        return Ok(products);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetProduct(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var connection = GetDbConnection();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, price, created_at FROM products WHERE id = @id AND tenant_id = @tid";
        cmd.AddParameter("@id", id);
        cmd.AddParameter("@tid", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return NotFound();

        return Ok(new ProductResponse(
            reader.GetGuid(0), reader.GetString(1),
            reader.GetDecimal(2), reader.GetDateTime(3)));
    }

    [HttpPost]
    [RequireFeature("products:create")]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var connection = GetDbConnection();
        var jwt = GetBearerJwt();

        // ── Quota check before creating ──────────────────────────────────
        bool hasQuota = await _cp.CheckQuotaAsync(
            tenantId, ControlPlaneService.DimensionStorage, request.SizeInBytes, jwt, ct);

        if (!hasQuota)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "quota_exceeded",
                message = $"Storage quota exceeded. Cannot create product ({request.SizeInBytes} bytes requested)."
            });
        }

        // ── Two-phase quota: reserve → insert → commit ───────────────────
        bool reserved = await _cp.ReserveQuotaAsync(
            tenantId, ControlPlaneService.DimensionStorage, request.SizeInBytes, jwt, ct);

        if (!reserved)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "quota_exceeded",
                message = "Insufficient storage quota."
            });
        }

        try
        {
            var id = Guid.NewGuid();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO products (id, tenant_id, name, price, created_at) VALUES (@id, @tid, @name, @price, @now)";
            cmd.AddParameter("@id", id);
            cmd.AddParameter("@tid", tenantId);
            cmd.AddParameter("@name", request.Name);
            cmd.AddParameter("@price", request.Price);
            cmd.AddParameter("@now", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(ct);

            // Commit the reservation (converts reserved → consumed)
            await _cp.CommitReservationAsync(
                tenantId, ControlPlaneService.DimensionStorage, request.SizeInBytes, jwt, ct);

            _logger.LogInformation(
                "Product {Id} created for tenant {TenantId}, consumed {Bytes} storage",
                id, tenantId, request.SizeInBytes);

            return CreatedAtAction(nameof(GetProduct), new { id }, new ProductResponse(
                id, request.Name, request.Price, DateTime.UtcNow));
        }
        catch
        {
            // Release the reservation on failure
            await _cp.CancelReservationAsync(
                tenantId, ControlPlaneService.DimensionStorage, request.SizeInBytes, jwt, CancellationToken.None);
            throw;
        }
    }

    [HttpDelete("{id:guid}")]
    [RequireFeature("products:delete")]
    public async Task<IActionResult> DeleteProduct(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var connection = GetDbConnection();
        var jwt = GetBearerJwt();

        // Get the product to know its size for quota release
        await using var lookupCmd = connection.CreateCommand();
        lookupCmd.CommandText = "SELECT id FROM products WHERE id = @id AND tenant_id = @tid";
        lookupCmd.AddParameter("@id", id);
        lookupCmd.AddParameter("@tid", tenantId);

        var existing = await lookupCmd.ExecuteScalarAsync(ct);
        if (existing is null)
            return NotFound();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM products WHERE id = @id AND tenant_id = @tid";
        cmd.AddParameter("@id", id);
        cmd.AddParameter("@tid", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);

        // Release quota (fire-and-forget — best effort)
        _ = _cp.ReleaseQuotaAsync(
            tenantId, ControlPlaneService.DimensionStorage, 4096, jwt, CancellationToken.None);

        _logger.LogInformation("Product {Id} deleted for tenant {TenantId}", id, tenantId);

        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private Guid GetTenantId() =>
        (Guid)(HttpContext.Items["TenantId"]
            ?? throw new UnauthorizedAccessException("Tenant not resolved"));

    private DbConnection GetDbConnection() =>
        (DbConnection)(HttpContext.Items["DbConnection"]
            ?? throw new InvalidOperationException("DB connection not available"));

    private string GetBearerJwt()
    {
        var auth = HttpContext.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..]
            : string.Empty;
    }
}

public sealed record CreateProductRequest(string Name, decimal Price, long SizeInBytes = 4096);

public sealed record ProductResponse(Guid Id, string Name, decimal Price, DateTime CreatedAt);
