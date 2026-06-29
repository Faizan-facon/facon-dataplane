using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var connection = GetDbConnection();

        var id = Guid.NewGuid();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO products (id, tenant_id, name, price, created_at) VALUES (@id, @tid, @name, @price, @now)";
        cmd.AddParameter("@id", id);
        cmd.AddParameter("@tid", tenantId);
        cmd.AddParameter("@name", request.Name);
        cmd.AddParameter("@price", request.Price);
        cmd.AddParameter("@now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("Product {Id} created for tenant {TenantId}", id, tenantId);

        return CreatedAtAction(nameof(GetProduct), new { id }, new ProductResponse(
            id, request.Name, request.Price, DateTime.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private Guid GetTenantId() =>
        (Guid)(HttpContext.Items["TenantId"]
            ?? throw new UnauthorizedAccessException("Tenant not resolved"));

    private DbConnection GetDbConnection() =>
        (DbConnection)(HttpContext.Items["DbConnection"]
            ?? throw new InvalidOperationException("DB connection not available"));
}

/// <summary>
/// Extension to add parameters to <see cref="DbCommand"/> in a provider-agnostic way.
/// </summary>
internal static class DbCommandExtensions
{
    public static void AddParameter(this DbCommand cmd, string name, object? value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(param);
    }
}

public sealed record CreateProductRequest(string Name, decimal Price);

public sealed record ProductResponse(Guid Id, string Name, decimal Price, DateTime CreatedAt);
