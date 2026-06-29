import { Injectable, signal } from '@angular/core';

/**
 * Client-side feature gate. Works alongside the server-side [RequireFeature] filter.
 * Uses the key "plan" concept: Trial, Pro, Enterprise.
 *
 * In production, features should be fetched from GET /api/v1/me or an entitlements
 * endpoint. For now they're derived client-side from a hardcoded mapping matching
 * the backend FeatureGateService.
 */
@Injectable({ providedIn: 'root' })
export class FeatureGateService {
  private _features = signal<Set<string>>(new Set());

  readonly features = this._features.asReadonly();

  /** Set the features based on the plan key from tenant context */
  setPlan(planKey: string | null | undefined): void {
    const features = PLAN_FEATURES[planKey?.toLowerCase() ?? ''] ?? PLAN_FEATURES['trial'];
    this._features.set(features);
  }

  hasFeature(feature: string): boolean {
    return this._features().has(feature);
  }
}

// Matches backend FeatureGateService.PlanFeatures
const PLAN_FEATURES: Record<string, Set<string>> = {
  trial: new Set([
    'products:read',
    'products:create',
    'dashboard:view',
  ]),
  pro: new Set([
    'products:read', 'products:create', 'products:update', 'products:delete',
    'dashboard:view', 'analytics:view', 'reports:export', 'api:access',
  ]),
  enterprise: new Set([
    'products:read', 'products:create', 'products:update', 'products:delete',
    'dashboard:view', 'analytics:view', 'reports:export', 'api:access',
    'sso:manage', 'audit:view', 'custom:branding', 'priority:support',
  ]),
};
