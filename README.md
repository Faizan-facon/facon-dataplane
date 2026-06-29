# Facon Dataplane

Multi-tenant SaaS dataplane — authenticates users via **Keycloak** and resolves tenant context + database credentials via **FaconControlPlane**.

## Architecture

```
User → Keycloak (Bearer JWT)
     → Angular Frontend (port 4200)
         → authInterceptor attaches JWT to /api/* calls
     → .NET Backend (port 5200)
         ├─ TenantResolutionMiddleware: forwards JWT → CP GET /api/v1/me
         ├─ SubscriptionEnforcementMiddleware: blocks suspended/past-due
         ├─ TenantLoggingMiddleware: enriches logs with TenantId/UserId
         ├─ DbConnectionMiddleware: forwards JWT → CP POST /tenants/{tid}/credentials/.../Database/fetch
         └─ Controller: queries tenant-scoped DB
```

## Quick Start

### Backend

```bash
cd src/FaconDataplane.Api
# Set env vars (see below)
dotnet run
# → http://localhost:5200
```

### Frontend

```bash
cd frontend
npm install
npm start
# → http://localhost:4200  (proxies /api → localhost:5200)
```

## Environment Variables

All config in `appsettings.json` — override with env vars or user-secrets:

| Variable | Default | Description |
|---|---|---|
| `Keycloak__Authority` | `https://keycloak.example.com/realms/facon` | Keycloak realm URL |
| `Keycloak__Audience` | `dataplane` | Keycloak client ID |
| `Keycloak__RequireHttpsMetadata` | `true` | Set `false` for local dev |
| `ControlPlane__BaseUrl` | `https://controlplane.example.com` | Control Plane API base URL |
| `ConnectionStrings__Default` | `Host=localhost;Database=facon_dp;...` | Fallback DB (not used in tenant mode) |

### Keycloak Setup

1. Create a realm `facon` with a client `dataplane` (public, redirect: `http://localhost:4200/*`)
2. Users must have a `sub` claim matching their `OrganizationMember` record in the control plane
3. Control Plane's `Keycloak:Authority` must point to the same realm for JWT validation

### Control Plane Prerequisites

1. `GET /api/v1/me` must return `planKey` and `subscriptionStatus` per tenant (committed in `d69dfbf`)
2. `POST /api/v1/tenants/{tid}/credentials/resources/Database/fetch?purpose=Application` must work
3. Vault must be running and registered per-tenant (`TenantResourceRegistration`)

### Per-Tenant Database Setup

The control plane provisions tenant databases. The dataplane connects via ephemeral Vault credentials:

```bash
# Platform admin registers a tenant resource in the control plane:
curl -X POST /api/v1/tenants/{tid}/resources \
  -d '{"resourceType":"Database","engine":"postgresql","topology":{"endpoint":"...","port":5432,"database":"tenant_abc"}}'
```

## Project Structure

```
src/FaconDataplane.Api/
├── Controllers/
│   ├── MeController.cs           → GET /api/v1/me (tenant context for frontend)
│   ├── ProductsController.cs     → CRUD with quota enforcement
│   ├── AnalyticsController.cs    → Feature-gated (Pro+ plan)
│   └── HealthController.cs       → /health, /health/ready
├── Middleware/
│   ├── TenantResolutionMiddleware.cs       → CP: GET /me → tenant context
│   ├── SubscriptionEnforcementMiddleware.cs → 402/423 for blocked tenants
│   ├── TenantLoggingMiddleware.cs          → {TenantId, UserId} in all logs
│   └── DbConnectionMiddleware.cs           → CP: credential fetch → pooled Npgsql/SqlClient
├── Services/
│   ├── ControlPlaneService.cs    → Quota check/consume/release/reserve
│   ├── FeatureGateService.cs     → Plan→feature mapping, 60s cache
│   ├── TenantConnectionPool.cs   → Per-tenant DbConnection pool
│   ├── DbConnectionFactory.cs    → Npgsql / SqlClient provider switch
│   └── TenantMigrationService.cs → Idempotent per-tenant migrations
├── Authorization/
│   ├── RequireFeatureAttribute.cs → [RequireFeature("analytics:view")]
│   └── FeatureGateFilter.cs      → Returns 402 if plan lacks feature
└── Extensions/
    └── KeycloakAuthenticationExtensions.cs

frontend/src/app/
├── core/auth/         → Keycloak config, JWT interceptor, auth guard
├── core/services/     → ApiService, TenantService, FeatureGateService
├── layout/shell/      → Header (tenant badge), sidebar (feature-gated nav)
└── pages/             → Dashboard, Products, Analytics, Upgrade
```
