import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ApiService } from '../../core/services/api.service';
import { TenantService } from '../../core/services/tenant.service';

@Component({
  standalone: true,
  imports: [CommonModule],
  selector: 'app-dashboard',
  styles: [`
    .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 16px; }
    .card { background: #fff; padding: 20px; border-radius: 8px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
    .card h3 { font-size: 14px; color: #546e7a; margin-bottom: 8px; text-transform: uppercase; letter-spacing: 0.5px; }
    .card .value { font-size: 28px; font-weight: 700; color: #1a1a2e; }
    .card .label { font-size: 12px; color: #a0aec0; margin-top: 4px; }
  `],
  template: `
    <h2 style="margin-bottom:24px">Dashboard</h2>
    <div class="cards">
      <div class="card">
        <h3>Tenant</h3>
        <div class="value">{{ tenant.context()?.tenantName ?? '...' }}</div>
        <div class="label">{{ tenant.context()?.tenantStatus }} · {{ tenant.context()?.subscriptionStatus }} · {{ tenant.context()?.planKey }}</div>
      </div>
      <div class="card">
        <h3>Organization</h3>
        <div class="value">{{ tenant.context()?.organizationName ?? '...' }}</div>
        <div class="label">ID: {{ tenant.context()?.organizationId ?? '...' }}</div>
      </div>
      <div class="card">
        <h3>API Status</h3>
        <div class="value" [style.color]="'#4caf50'">Connected</div>
        <div class="label">Dataplane API v1</div>
      </div>
    </div>
  `,
})
export class DashboardComponent implements OnInit {
  api = inject(ApiService);
  tenant = inject(TenantService);

  async ngOnInit(): Promise<void> {
    await this.tenant.load();
  }
}
