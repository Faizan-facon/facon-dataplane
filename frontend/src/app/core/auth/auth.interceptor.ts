import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { KeycloakService } from 'keycloak-angular';

/**
 * Attaches the Keycloak Bearer JWT to every outgoing API request.
 * The dataplane backend forwards this JWT to the control plane for
 * tenant resolution and authorization.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // Only attach token to API calls (skip assets, external URLs)
  if (!req.url.includes('/api/') && !req.url.includes('/health')) {
    return next(req);
  }

  const keycloak = inject(KeycloakService);
  const token = keycloak.getKeycloakInstance().token;

  if (token) {
    const cloned = req.clone({
      setHeaders: { Authorization: `Bearer ${token}` },
    });
    return next(cloned);
  }

  return next(req);
};
