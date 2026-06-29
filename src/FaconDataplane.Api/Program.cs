using System.Text.Json;
using System.Text.Json.Serialization;
using Asp.Versioning;
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

// ── API Versioning (URL-path: /api/v1/...) ───────────────────────────────
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1);
    o.AssumeDefaultVersionWhenUnspecified = true;
    o.ReportApiVersions = true;
    o.ApiVersionReader = new UrlSegmentApiVersionReader();
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
builder.Services.AddSingleton<TenantMigrationService>();

// ── Tenant connection pool (per-tenant DB connections) ───────────────────
builder.Services.AddSingleton<TenantConnectionPool>();

// ── Feature gate filter ──────────────────────────────────────────────────
builder.Services.AddScoped<FeatureGateFilter>();

// ── Middleware (order matters) ───────────────────────────────────────────
builder.Services.AddScoped<TenantResolutionMiddleware>();
builder.Services.AddScoped<SubscriptionEnforcementMiddleware>();
builder.Services.AddScoped<TenantLoggingMiddleware>();
builder.Services.AddScoped<DbConnectionMiddleware>();

// ── Memory cache (used by TenantResolutionMiddleware) ────────────────────
builder.Services.AddMemoryCache();

// ── CORS ─────────────────────────────────────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ── Pipeline ─────────────────────────────────────────────────────────────
// Order: CORS → Auth → Tenant Resolution → Subscription Check → Logging → DB Connection
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantResolutionMiddleware>();       // 1. Resolve tenant from CP
app.UseMiddleware<SubscriptionEnforcementMiddleware>(); // 2. Block suspended/cancelled/past-due
app.UseMiddleware<TenantLoggingMiddleware>();           // 3. Enrich logs with TenantId/UserId
app.UseMiddleware<DbConnectionMiddleware>();            // 4. Open tenant-scoped DB connection
app.MapControllers();

app.Run();
