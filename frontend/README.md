# Facon Dataplane Frontend

Angular 19 standalone app with Keycloak authentication, feature gates, and quota-aware UI.

## Quick Start

```bash
cd frontend
npm install
npm start        # http://localhost:4200
```

## Architecture

```
src/app/
├── core/
│   ├── auth/           Keycloak config, JWT interceptor, auth guard
│   └── services/       ApiService, TenantService, FeatureGateService
├── shared/
│   └── guards/         Feature gate guard (client-side)
├── layout/
│   └── shell/          Header (tenant badge, logout) + sidebar nav
└── pages/
    ├── dashboard/      Tenant info cards
    ├── products/       CRUD with quota error handling
    ├── analytics/      Feature-gated (Pro+ plan)
    └── upgrade/        Upsell page for feature-gated redirects
```

## Configuration

Edit `core/auth/auth.config.ts` for Keycloak realm/client.
The JWT interceptor auto-attaches `Authorization: Bearer` to all `/api/` requests.

## Feature Gates

- **Client-side:** `featureGuard('analytics:view')` route guard
- **Sidebar:** `*ngIf="fg.hasFeature('...')"` hides nav links
- **Server-side:** `[RequireFeature]` attribute on controllers (enforced regardless)

Both sides share the same feature keys and plan mapping.
