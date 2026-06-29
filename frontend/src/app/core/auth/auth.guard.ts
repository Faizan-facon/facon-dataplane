import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { KeycloakService } from 'keycloak-angular';

/**
 * Redirects unauthenticated users to Keycloak login.
 * Authenticated users proceed to the route.
 */
export const authGuard: CanActivateFn = async () => {
  const keycloak = inject(KeycloakService);
  const router = inject(Router);

  const authenticated = keycloak.getKeycloakInstance().authenticated ?? false;

  if (!authenticated) {
    await keycloak.login({ redirectUri: window.location.origin + router.url });
    return false;
  }

  return true;
};
