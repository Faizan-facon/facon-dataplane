import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule, Router } from '@angular/router';
import { ApiService, MemberDetailResponse, RoleResponse } from '../../../core/services/api.service';
import { PermissionService } from '../../../core/services/permission.service';

@Component({
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule],
  selector: 'app-admin-user-detail',
  styles: [`
    .back { display: inline-block; margin-bottom: 16px; color: #4fc3f7; text-decoration: none; font-size: 14px; }
    h2 { margin-bottom: 24px; }
    .section { background: #fff; padding: 20px; border-radius: 8px; margin-bottom: 16px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
    .section h3 { font-size: 15px; margin-bottom: 12px; color: #546e7a; }
    .roles-grid { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; }
    .role-chip { display: flex; align-items: center; gap: 6px; padding: 6px 12px; border: 1px solid #ddd; border-radius: 6px; font-size: 13px; }
    .role-chip.active { border-color: #4fc3f7; background: #e3f2fd; }
    .btn { padding: 6px 14px; border: none; border-radius: 6px; cursor: pointer; font-size: 13px; font-weight: 500; }
    .btn-primary { background: #4fc3f7; color: #1a1a2e; }
    .btn-danger { background: #e74c3c; color: #fff; }
    .btn-sm { padding: 4px 10px; font-size: 12px; }
    .perm-row { display: flex; gap: 12px; align-items: center; margin-bottom: 8px; }
    .perm-row select, .perm-row input { padding: 6px 10px; border: 1px solid #ddd; border-radius: 4px; font-size: 13px; }
    .override { padding: 2px 8px; border-radius: 4px; font-size: 12px; }
    .override.allow { background: #e8f5e9; color: #2e7d32; }
    .override.deny { background: #fdecea; color: #c0392b; }
  `],
  template: `
    <a routerLink="/admin/users" class="back">← Back to Members</a>
    <h2>👤 {{ sub }}</h2>

    <!-- Roles -->
    <div class="section">
      <h3>Roles</h3>
      <div class="roles-grid">
        @for (role of roles(); track role.id) {
          <label class="role-chip" [class.active]="selectedRoles().includes(role.id)">
            <input type="checkbox" [checked]="selectedRoles().includes(role.id)"
                   (change)="toggleRole(role.id)" />
            {{ role.name }}
          </label>
        }
      </div>
      <button class="btn btn-primary" (click)="saveRoles()" [disabled]="saving()">Save Roles</button>
    </div>

    <!-- Permission Overrides -->
    @if (perm.has('users.update')) {
      <div class="section">
        <h3>Direct Permission Overrides</h3>
        @for (o of member()?.overrides ?? []; track o.permission) {
          <div class="perm-row">
            <code>{{ o.permission }}</code>
            <span class="override" [class.allow]="o.effect === 1" [class.deny]="o.effect === 2">
              {{ o.effect === 1 ? 'ALLOW' : 'DENY' }}
            </span>
            <button class="btn btn-sm btn-danger" (click)="revokeOverride(o.permission)">Remove</button>
          </div>
        }
        <div class="perm-row" style="margin-top:12px">
          <select [(ngModel)]="newPermCode">
            <option value="">Select permission...</option>
            <option value="users.read">users.read</option>
            <option value="users.create">users.create</option>
            <option value="users.update">users.update</option>
            <option value="users.delete">users.delete</option>
            <option value="products.read">products.read</option>
            <option value="products.create">products.create</option>
            <option value="products.update">products.update</option>
            <option value="products.delete">products.delete</option>
            <option value="analytics.view">analytics.view</option>
            <option value="reports.export">reports.export</option>
            <option value="dashboard.view">dashboard.view</option>
            <option value="billing.view">billing.view</option>
            <option value="settings.write">settings.write</option>
          </select>
          <select [(ngModel)]="newPermEffect">
            <option value="allow">Allow</option>
            <option value="deny">Deny</option>
          </select>
          <button class="btn btn-primary btn-sm" (click)="addOverride()" [disabled]="!newPermCode">Add</button>
        </div>
      </div>
    }

    <!-- Danger Zone -->
    @if (perm.has('users.delete')) {
      <div class="section" style="border:1px solid #e74c3c">
        <h3 style="color:#e74c3c">Danger Zone</h3>
        <p style="font-size:14px;margin-bottom:12px">Remove this member from the tenant. This revokes all access.</p>
        <button class="btn btn-danger" (click)="remove()">Remove Member</button>
      </div>
    }
  `,
})
export class AdminUserDetailComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  perm = inject(PermissionService);

  sub = '';
  member = signal<MemberDetailResponse | null>(null);
  roles = signal<RoleResponse[]>([]);
  selectedRoles = signal<string[]>([]);
  saving = signal(false);
  newPermCode = '';
  newPermEffect = 'allow';

  async ngOnInit(): Promise<void> {
    this.sub = this.route.snapshot.params['sub'];
    const [member, roles] = await Promise.all([
      this.api.getMember(this.sub).toPromise(),
      this.api.getRoles().toPromise(),
    ]);
    this.member.set(member ?? null);
    this.roles.set(roles ?? []);
    this.selectedRoles.set(member?.roles?.map((r) => r.id) ?? []);
  }

  toggleRole(id: string): void {
    const current = this.selectedRoles();
    if (current.includes(id)) {
      this.selectedRoles.set(current.filter((x) => x !== id));
    } else {
      this.selectedRoles.set([...current, id]);
    }
  }

  async saveRoles(): Promise<void> {
    this.saving.set(true);
    try {
      await this.api.updateRoles(this.sub, this.selectedRoles()).toPromise();
      const m = await this.api.getMember(this.sub).toPromise();
      this.member.set(m ?? null);
    } catch { /* ignore */ } finally {
      this.saving.set(false);
    }
  }

  async addOverride(): Promise<void> {
    if (!this.newPermCode) return;
    try {
      await this.api.grantPermission(this.sub, { permission: this.newPermCode, effect: this.newPermEffect }).toPromise();
      const m = await this.api.getMember(this.sub).toPromise();
      this.member.set(m ?? null);
      this.newPermCode = '';
    } catch { /* ignore */ }
  }

  async revokeOverride(code: string): Promise<void> {
    try {
      await this.api.revokePermission(this.sub, code).toPromise();
      const m = await this.api.getMember(this.sub).toPromise();
      this.member.set(m ?? null);
    } catch { /* ignore */ }
  }

  async remove(): Promise<void> {
    if (!confirm(`Remove ${this.sub} from this tenant?`)) return;
    try {
      await this.api.removeMember(this.sub).toPromise();
      this.router.navigate(['/admin/users']);
    } catch { /* ignore */ }
  }
}
