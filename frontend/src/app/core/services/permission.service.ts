import { Injectable, signal } from '@angular/core';
import { ApiService, MemberDetailResponse } from './api.service';
import { TenantService } from './tenant.service';

/**
 * Client-side permission service. Caches the user's effective permissions
 * from a GET /api/v1/users/{sub} call. Used by guards and templates to
 * conditionally show/hide admin UI elements.
 *
 * The server enforces permissions via [RequirePermission] regardless.
 * This is just for UX — hiding links and disabling buttons.
 */
@Injectable({ providedIn: 'root' })
export class PermissionService {
  private _permissions = signal<Set<string>>(new Set());
  private _loaded = signal(false);

  readonly permissions = this._permissions.asReadonly();
  readonly loaded = this._loaded.asReadonly();

  constructor(private api: ApiService, private tenant: TenantService) {}

  /** Load effective permissions for the current user+tenant. */
  async load(): Promise<void> {
    const sub = this.getSub();
    if (!sub) return;

    try {
      const detail: MemberDetailResponse | undefined =
        await this.api.getMember(sub).toPromise();
      if (!detail) return;

      // Collect permissions from both roles and direct overrides
      const perms = new Set<string>();

      // Direct overrides with effect=1 (allow) that aren't cancelled by deny
      const denies = new Set(
        detail.overrides?.filter((o) => o.effect === 2).map((o) => o.permission) ?? []
      );

      for (const o of detail.overrides ?? []) {
        if (o.effect === 1 && !denies.has(o.permission)) {
          perms.add(o.permission);
        }
      }

      // Role-based permissions (server already resolved these)
      // The backend's [RequirePermission] handles the full evaluation order.
      // Here we use a simplified check: if the member endpoint returns roles,
      // we know they have at least basic access.
      //
      // For a more accurate client-side check, we'd need the backend to
      // return the resolved permission list. For now, derive from roles.
      if (detail.roles?.length) {
        perms.add('dashboard.view');
        perms.add('products.read');
      }

      this._permissions.set(perms);
      this._loaded.set(true);
    } catch {
      // Silently fail — permissions remain empty
    }
  }

  /** Check if the current user has a specific permission. */
  has(permission: string): boolean {
    return this._permissions().has(permission);
  }

  private getSub(): string | null {
    // Read from Keycloak instance
    const kc = (window as any).keycloak;
    return kc?.tokenParsed?.sub ?? null;
  }
}
