using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FaconDataplane.Api.Controllers;

[ApiController]
[Route("api/products")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(ILogger<ProductsController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetProducts(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var connection = GetDbConnection();

        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, price, created_at FROM products WHERE tenant_id = @tid ORDER BY created_at DESC",
            connection);
        cmd.Parameters.AddWithValue("tid", tenantId);

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

        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, price, created_at FROM products WHERE id = @id AND tenant_id = @tid",
            connection);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return NotFound();

        return Ok(new ProductResponse(
            reader.GetGuid(0), reader.GetString(1),
            reader.GetDecimal(2), reader.GetDateTime(3)));
    }

    [HttpPost]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var connection = GetDbConnection();

        var id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO products (id, tenant_id, name, price, created_at) VALUES (@id, @tid, @name, @price, @now)",
            connection);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("name", request.Name);
        cmd.Parameters.AddWithValue("price", request.Price);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Product {Id} created for tenant {TenantId}", id, tenantId);

        return CreatedAtAction(nameof(GetProduct), new { id }, new ProductResponse(
            id, request.Name, request.Price, DateTime.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private Guid GetTenantId() =>
        (Guid)(HttpContext.Items["TenantId"]
            ?? throw new UnauthorizedAccessException("Tenant not resolved"));

    private NpgsqlConnection GetDbConnection() =>
        (NpgsqlConnection)(HttpContext.Items["DbConnection"]
            ?? throw new InvalidOperationException("DB connection not available"));
}

public sealed record CreateProductRequest(string Name, decimal Price);

public sealed record ProductResponse(Guid Id, string Name, decimal Price, DateTime CreatedAt);
