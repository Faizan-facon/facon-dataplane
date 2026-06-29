using System.Text.Json;
using System.Text.Json.Serialization;
using FaconDataplane.Api.Authorization;
using FaconDataplane.Api.Extensions;
using FaconDataplane.Api.Middleware;
using FaconDataplane.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── JSON ─────────────────────────────────────────────────────────────────
builder.Services.AddControllers(o =>
    {
        // Register the feature gate as a global action filter
        o.Filters.Add<FeatureGateFilter>();
    })
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

// ── Keycloak JWT auth ────────────────────────────────────────────────────
builder.Services.AddKeycloakAuthentication(builder.Configuration);

// ── HTTP client for Control Plane calls ──────────────────────────────────
builder.Services.AddHttpClient("ControlPlane", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ControlPlane:BaseUrl"]!);
});

// ── Control Plane services ───────────────────────────────────────────────
builder.Services.AddSingleton<ControlPlaneService>();
builder.Services.AddSingleton<FeatureGateService>();

// ── Tenant connection pool (per-tenant DB connections) ───────────────────
builder.Services.AddSingleton<TenantConnectionPool>();

// ── Feature gate filter ──────────────────────────────────────────────────
builder.Services.AddScoped<FeatureGateFilter>();

// ── Middleware (order matters) ───────────────────────────────────────────
builder.Services.AddScoped<TenantResolutionMiddleware>();
builder.Services.AddScoped<DbConnectionMiddleware>();

// ── CORS ─────────────────────────────────────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ── Pipeline ─────────────────────────────────────────────────────────────
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<DbConnectionMiddleware>();
app.MapControllers();

app.Run();
