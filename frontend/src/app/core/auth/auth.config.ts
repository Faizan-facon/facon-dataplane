import { KeycloakConfig } from 'keycloak-angular';

// These should come from environment config. Override in your deployment.
const keycloakUrl = window.location.origin.includes('localhost')
  ? 'http://localhost:8080'
  : 'https://keycloak.example.com';

export const keycloakConfig: KeycloakConfig = {
  url: keycloakUrl,
  realm: 'facon',
  clientId: 'dataplane-frontend',
};
