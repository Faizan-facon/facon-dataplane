import { Injectable, signal } from '@angular/core';
import { ApiService, TenantSummary } from './api.service';

export interface TenantContext {
  tenantId: string;
  tenantSlug: string;
  tenantName: string;
  organizationId: string;
  organizationName: string;
  tenantStatus: string;
  subscriptionStatus: string;
  planKey: string;
}

@Injectable({ providedIn: 'root' })
export class TenantService {
  private _context = signal<TenantContext | null>(null);

  readonly context = this._context.asReadonly();

  constructor(private api: ApiService) {}

  /** Load tenant context from the dataplane's GET /api/v1/me */
  async load(): Promise<TenantContext | null> {
    try {
      const profile = await this.api.getMyProfile().toPromise();
      if (!profile?.isOnboarded || !profile.tenants?.length) return null;

      const active = profile.tenants.find(
        (t) => t.status.toLowerCase() === 'active'
      );
      if (!active) return null;

      const ctx: TenantContext = {
        tenantId: active.id,
        tenantSlug: active.slug,
        tenantName: active.name,
        organizationId: profile.organizationId,
        organizationName: profile.organizationName,
        tenantStatus: active.status,
        subscriptionStatus: active.subscriptionStatus ?? 'Unknown',
        planKey: active.planKey ?? 'Trial',
      };
      this._context.set(ctx);
      return ctx;
    } catch {
      return null;
    }
  }

  get tenantId(): string | null {
    return this._context()?.tenantId ?? null;
  }

  /** True if the tenant is blocked (suspended, cancelled, past-due) */
  isBlocked(): boolean {
    const ctx = this._context();
    if (!ctx) return false;
    const blocked = ['Suspended', 'Cancelled', 'Deprovisioning', 'Deprovisioned'];
    const blockedSub = ['PastDue', 'Cancelled', 'Expired', 'Suspended'];
    return blocked.includes(ctx.tenantStatus) || blockedSub.includes(ctx.subscriptionStatus);
  }
}
