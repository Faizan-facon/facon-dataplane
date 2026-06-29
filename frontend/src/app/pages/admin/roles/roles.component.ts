import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ApiService, RoleResponse } from '../../../core/services/api.service';

@Component({
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule],
  selector: 'app-admin-roles',
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 24px; }
    .page-header h2 { font-size: 22px; }
    .btn { padding: 6px 14px; border: none; border-radius: 6px; cursor: pointer; font-size: 13px; font-weight: 500; }
    .btn-primary { background: #4fc3f7; color: #1a1a2e; }
    .btn-danger { background: #e74c3c; color: #fff; margin-left: 8px; }
    .btn-sm { padding: 4px 10px; font-size: 12px; }
    .card { background: #fff; padding: 16px; border-radius: 8px; margin-bottom: 12px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); display: flex; justify-content: space-between; align-items: center; }
    .card-info h3 { font-size: 15px; margin-bottom: 2px; }
    .card-info p { font-size: 13px; color: #546e7a; }
    .badge { padding: 2px 8px; border-radius: 4px; font-size: 12px; margin-left: 8px; }
    .badge-system { background: #e3f2fd; color: #1565c0; }
    .badge-default { background: #e8f5e9; color: #2e7d32; }
    .form-overlay { background: #fff; padding: 20px; border-radius: 8px; margin-bottom: 16px; box-shadow: 0 2px 6px rgba(0,0,0,0.15); }
    .form-overlay label { display: block; font-size: 13px; color: #546e7a; margin-bottom: 4px; }
    .form-overlay input, .form-overlay textarea { width: 100%; padding: 8px 12px; border: 1px solid #ddd; border-radius: 6px; font-size: 14px; margin-bottom: 12px; }
    .perm-grid { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 12px; }
    .perm-chip { padding: 4px 10px; border: 1px solid #ddd; border-radius: 4px; font-size: 12px; cursor: pointer; }
    .perm-chip.selected { border-color: #4fc3f7; background: #e3f2fd; }
  `],
  template: `
    <div class="page-header">
      <h2>🔑 Roles</h2>
      <button class="btn btn-primary" (click)="showForm = !showForm">
        {{ showForm ? 'Cancel' : '+ New Role' }}
      </button>
    </div>

    <!-- New Role Form -->
    @if (showForm) {
      <div class="form-overlay">
        <label>Name</label>
        <input [(ngModel)]="newName" placeholder="e.g. Auditor" />
        <label>Description</label>
        <input [(ngModel)]="newDesc" placeholder="Optional" />
        <label>Permissions (click to toggle)</label>
        <div class="perm-grid">
          @for (p of allPermissions; track p) {
            <span class="perm-chip" [class.selected]="newPerms().includes(p)"
                  (click)="togglePerm(p)">{{ p }}</span>
          }
        </div>
        <button class="btn btn-primary" (click)="createRole()" [disabled]="!newName">Create Role</button>
      </div>
    }

    <!-- Role List -->
    @for (r of roles(); track r.id) {
      <div class="card">
        <div class="card-info">
          <h3>{{ r.name }}
            @if (r.isSystem) { <span class="badge badge-system">System</span> }
            @if (r.isDefault) { <span class="badge badge-default">Default</span> }
          </h3>
          <p>{{ r.description || 'No description' }} · Priority: {{ r.priority }}</p>
        </div>
        <div>
          @if (!r.isSystem) {
            <button class="btn btn-sm btn-danger" (click)="deleteRole(r.id)">Delete</button>
          }
        </div>
      </div>
    } @empty {
      <p>No roles defined yet.</p>
    }
  `,
})
export class AdminRolesComponent implements OnInit {
  private api = inject(ApiService);

  roles = signal<RoleResponse[]>([]);
  showForm = false;
  newName = '';
  newDesc = '';
  newPerms = signal<string[]>([]);

  readonly allPermissions = [
    'users.read', 'users.create', 'users.update', 'users.delete',
    'products.read', 'products.create', 'products.update', 'products.delete',
    'analytics.view', 'reports.export', 'dashboard.view',
    'billing.view', 'settings.write',
  ];

  async ngOnInit(): Promise<void> {
    const data = await this.api.getRoles().toPromise();
    this.roles.set(data ?? []);
  }

  togglePerm(code: string): void {
    const p = this.newPerms();
    if (p.includes(code)) this.newPerms.set(p.filter((x) => x !== code));
    else this.newPerms.set([...p, code]);
  }

  async createRole(): Promise<void> {
    try {
      await this.api.createRole({
        name: this.newName, description: this.newDesc || undefined,
        isDefault: false, priority: 0,
        permissions: this.newPerms().map((code) => ({ code, effect: 'allow' })),
      }).toPromise();
      this.showForm = false; this.newName = ''; this.newDesc = ''; this.newPerms.set([]);
      const data = await this.api.getRoles().toPromise();
      this.roles.set(data ?? []);
    } catch { /* ignore */ }
  }

  async deleteRole(id: string): Promise<void> {
    if (!confirm('Delete this role? Users assigned to it will lose those permissions.')) return;
    try {
      await this.api.deleteRole(id).toPromise();
      const data = await this.api.getRoles().toPromise();
      this.roles.set(data ?? []);
    } catch { /* ignore */ }
  }
}
