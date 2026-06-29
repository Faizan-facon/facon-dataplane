# Facon Dataplane

Multi-tenant SaaS dataplane — authenticates users via **Keycloak** and resolves tenant context + database credentials via **FaconControlPlane**.

## Architecture

```
User → Keycloak (Bearer JWT)
     → Dataplane
         ├─ TenantResolutionMiddleware: forwards JWT → CP GET /api/v1/me
         ├─ DbConnectionMiddleware: forwards JWT → CP POST /tenants/{tid}/credentials/.../Database/fetch
         └─ Controller: queries tenant-scoped DB
```

## Getting Started

1. Configure Keycloak in `appsettings.json`
2. Set `ControlPlane:BaseUrl` to your control plane instance
3. `dotnet run`

## Prerequisites

- .NET 9 SDK
- Access to a running FaconControlPlane instance
- Keycloak realm with a client for this dataplane
