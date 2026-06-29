import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule, Router } from '@angular/router';
import { ApiService, MemberResponse } from '../../../core/services/api.service';
import { PermissionService } from '../../../core/services/permission.service';

@Component({
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule],
  selector: 'app-admin-users',
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 24px; }
    .page-header h2 { font-size: 22px; }
    .invite-form { display: flex; gap: 12px; margin-bottom: 20px; background: #fff; padding: 16px; border-radius: 8px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); align-items: flex-end; }
    .invite-form input { padding: 8px 12px; border: 1px solid #ddd; border-radius: 6px; font-size: 14px; flex: 1; }
    .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; font-weight: 500; }
    .btn-primary { background: #4fc3f7; color: #1a1a2e; }
    .btn-sm { padding: 4px 10px; font-size: 12px; }
    .btn-outline { background: none; border: 1px solid #4fc3f7; color: #4fc3f7; }
    .btn-danger { background: #e74c3c; color: #fff; }
    table { width: 100%; border-collapse: collapse; background: #fff; border-radius: 8px; overflow: hidden; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
    th, td { padding: 12px 16px; text-align: left; font-size: 14px; border-bottom: 1px solid #eee; }
    th { background: #f8f9fa; font-weight: 600; color: #546e7a; }
    .badge { padding: 2px 8px; border-radius: 4px; font-size: 12px; background: #e8f5e9; color: #2e7d32; }
    .badge.admin { background: #fff3e0; color: #e65100; }
    .toast { padding: 12px; border-radius: 6px; margin-bottom: 16px; font-size: 14px; }
    .toast-success { background: #e8f5e9; color: #2e7d32; }
  `],
  template: `
    <div class="page-header">
      <h2>👥 Team Members</h2>
    </div>

    <!-- Invite -->
    @if (perm.has('users.create')) {
      <div class="invite-form">
        <input [(ngModel)]="inviteEmail" placeholder="Email address to invite" type="email" />
        <button class="btn btn-primary" (click)="invite()" [disabled]="!inviteEmail || inviting()">
          {{ inviting() ? 'Inviting...' : 'Send Invite' }}
        </button>
      </div>
      @if (inviteToken()) {
        <div class="toast toast-success">✅ Invite link: <code>https://app.example.com/join?token={{ inviteToken() }}</code></div>
      }
    }

    <!-- Members -->
    <table>
      <thead>
        <tr><th>User ID</th><th>Roles</th><th></th></tr>
      </thead>
      <tbody>
        @for (m of members(); track m.sub) {
          <tr>
            <td><code>{{ m.sub }}</code></td>
            <td>
              @for (r of m.roles; track r.id) {
                <span class="badge" [class.admin]="r.name === 'Admin'">{{ r.name }}</span>
              }
            </td>
            <td>
              <a [routerLink]="['/admin/users', m.sub]" class="btn btn-sm btn-outline">Manage</a>
            </td>
          </tr>
        } @empty {
          <tr><td colspan="3">No members yet.</td></tr>
        }
      </tbody>
    </table>
  `,
})
export class AdminUsersComponent implements OnInit {
  private api = inject(ApiService);
  perm = inject(PermissionService);

  members = signal<MemberResponse[]>([]);
  inviteEmail = '';
  inviteToken = signal<string | null>(null);
  inviting = signal(false);

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    try {
      const data = await this.api.getMembers().toPromise();
      this.members.set(data ?? []);
    } catch { /* ignore */ }
  }

  async invite(): Promise<void> {
    if (!this.inviteEmail) return;
    this.inviting.set(true);
    try {
      const resp = await this.api.inviteUser({ email: this.inviteEmail }).toPromise();
      this.inviteToken.set(resp?.token ?? null);
      this.inviteEmail = '';
    } catch { /* ignore */ } finally {
      this.inviting.set(false);
    }
  }
}
