import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { FeatureGateService } from '../services/feature-gate.service';

/**
 * Route guard that checks feature access client-side.
 * Redirects to /upgrade if the feature isn't available.
 *
 * Usage: { path: 'analytics', canActivate: [featureGuard('analytics:view')] }
 */
export function featureGuard(feature: string): CanActivateFn {
  return () => {
    const fg = inject(FeatureGateService);
    const router = inject(Router);

    if (fg.hasFeature(feature)) return true;

    return router.parseUrl('/upgrade');
  };
}
