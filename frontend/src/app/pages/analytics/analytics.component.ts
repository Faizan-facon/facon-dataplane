import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ApiService } from '../../core/services/api.service';

@Component({
  standalone: true,
  imports: [CommonModule],
  selector: 'app-analytics',
  styles: [`
    .metric { background: #fff; padding: 20px; border-radius: 8px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); margin-bottom: 16px; }
    .metric h3 { font-size: 14px; color: #546e7a; margin-bottom: 4px; }
    .metric .value { font-size: 32px; font-weight: 700; }
    .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; font-weight: 500; margin-top: 16px; }
    .btn-primary { background: #4fc3f7; color: #1a1a2e; }
  `],
  template: `
    <h2 style="margin-bottom:24px">Analytics</h2>
    <div class="metric">
      <h3>Active Users</h3>
      <div class="value">{{ dashboard()?.metrics?.users ?? '...' }}</div>
    </div>
    <div class="metric">
      <h3>Monthly Revenue</h3>
      <div class="value">{{ dashboard()?.metrics?.revenue | currency }}</div>
    </div>
    <button class="btn btn-primary" (click)="exportReport()" [disabled]="exporting()">
      {{ exporting() ? 'Exporting...' : 'Export Report' }}
    </button>
  `,
})
export class AnalyticsComponent {
  private api = inject(ApiService);
  dashboard = signal<any>(null);
  exporting = signal(false);

  constructor() {
    this.api.getDashboard().subscribe((d) => this.dashboard.set(d));
  }

  async exportReport(): Promise<void> {
    this.exporting.set(true);
    try {
      await this.api.exportReport().toPromise();
    } finally {
      this.exporting.set(false);
    }
  }
}
