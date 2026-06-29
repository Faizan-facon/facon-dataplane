import { Component, inject, OnInit } from '@angular/core';
import { RouterModule, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { KeycloakService } from 'keycloak-angular';
import { TenantService } from '../../core/services/tenant.service';
import { FeatureGateService } from '../../core/services/feature-gate.service';

@Component({
  standalone: true,
  imports: [CommonModule, RouterModule],
  selector: 'app-shell',
  templateUrl: './shell.component.html',
  styleUrls: ['./shell.component.css'],
})
export class ShellComponent implements OnInit {
  private keycloak = inject(KeycloakService);
  private router = inject(Router);
  tenant = inject(TenantService);
  fg = inject(FeatureGateService);

  sidebarOpen = false;

  async ngOnInit(): Promise<void> {
    const ctx = await this.tenant.load();
    if (!ctx) {
      console.warn('Tenant resolution failed');
      return;
    }
    // Derive feature set from tenant's actual plan key
    const planKey = ctx.planKey;
    this.fg.setPlan(planKey);
  }

  get username(): string {
    const profile = this.keycloak.getKeycloakInstance().profile;
    return (profile as any)?.username ?? (profile as any)?.preferred_username ?? 'User';
  }

  get tenantName(): string {
    return this.tenant.context()?.tenantName ?? 'Loading...';
  }

  get tenantStatus(): string {
    const s = this.tenant.context()?.tenantStatus;
    if (!s) return '';
    return s === 'Active' ? '' : ` (${s})`;
  }

  get isBlocked(): boolean {
    return this.tenant.isBlocked();
  }

  async logout(): Promise<void> {
    await this.keycloak.logout(window.location.origin);
  }

  toggleSidebar(): void {
    this.sidebarOpen = !this.sidebarOpen;
  }
}
