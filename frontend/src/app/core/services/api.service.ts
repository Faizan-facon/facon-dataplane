import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

/**
 * Base API URL. Override for production via environment config.
 */
const API_BASE = '/api/v1';

// ── Types ─────────────────────────────────────────────────────────────────

export interface ProductResponse {
  id: string;
  name: string;
  price: number;
  createdAt: string;
}

export interface CreateProductRequest {
  name: string;
  price: number;
  sizeInBytes: number;
}

export interface AnalyticsDashboard {
  tenantId: string;
  metrics: { users: number; revenue: number };
}

export interface QuotaError {
  error: string;
  message: string;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  constructor(private http: HttpClient) {}

  // ── Products ──────────────────────────────────────────────────────────

  getProducts(): Observable<ProductResponse[]> {
    return this.http.get<ProductResponse[]>(`${API_BASE}/products`);
  }

  getProduct(id: string): Observable<ProductResponse> {
    return this.http.get<ProductResponse>(`${API_BASE}/products/${id}`);
  }

  createProduct(req: CreateProductRequest): Observable<ProductResponse> {
    return this.http.post<ProductResponse>(`${API_BASE}/products`, req);
  }

  deleteProduct(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/products/${id}`);
  }

  // ── Analytics ─────────────────────────────────────────────────────────

  getDashboard(): Observable<AnalyticsDashboard> {
    return this.http.get<AnalyticsDashboard>(`${API_BASE}/analytics/dashboard`);
  }

  exportReport(): Observable<{ report: string; generatedAt: string }> {
    return this.http.get<{ report: string; generatedAt: string }>(`${API_BASE}/analytics/export`);
  }

  // ── Me (tenant context) ───────────────────────────────────────────────

  getMyProfile(): Observable<MyProfileResponse> {
    return this.http.get<MyProfileResponse>(`${API_BASE}/me`);
  }

  // ── Users ─────────────────────────────────────────────────────────────

  getMembers(): Observable<MemberResponse[]> {
    return this.http.get<MemberResponse[]>(`${API_BASE}/users`);
  }

  getMember(sub: string): Observable<MemberDetailResponse> {
    return this.http.get<MemberDetailResponse>(`${API_BASE}/users/${sub}`);
  }

  inviteUser(req: InviteRequest): Observable<InvitationResponse> {
    return this.http.post<InvitationResponse>(`${API_BASE}/users/invite`, req);
  }

  updateRoles(sub: string, roleIds: string[]): Observable<void> {
    return this.http.put<void>(`${API_BASE}/users/${sub}/roles`, { roleIds });
  }

  grantPermission(sub: string, req: PermissionOverrideRequest): Observable<void> {
    return this.http.post<void>(`${API_BASE}/users/${sub}/permissions`, req);
  }

  revokePermission(sub: string, code: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/users/${sub}/permissions/${code}`);
  }

  removeMember(sub: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/users/${sub}`);
  }

  // ── Roles ─────────────────────────────────────────────────────────────

  getRoles(): Observable<RoleResponse[]> {
    return this.http.get<RoleResponse[]>(`${API_BASE}/roles`);
  }

  getRole(id: string): Observable<RoleDetailResponse> {
    return this.http.get<RoleDetailResponse>(`${API_BASE}/roles/${id}`);
  }

  createRole(req: CreateRoleRequest): Observable<{ id: string; name: string }> {
    return this.http.post<{ id: string; name: string }>(`${API_BASE}/roles`, req);
  }

  updateRole(id: string, req: UpdateRoleRequest): Observable<void> {
    return this.http.put<void>(`${API_BASE}/roles/${id}`, req);
  }

  deleteRole(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/roles/${id}`);
  }
}

// ── User types ────────────────────────────────────────────────────────────

export interface MemberResponse { sub: string; roles: RoleSummary[]; }
export interface MemberDetailResponse { sub: string; roles: RoleSummary[]; overrides: PermissionOverride[]; }
export interface RoleSummary { id: string; name: string; }
export interface PermissionOverride { permission: string; effect: number; }
export interface InviteRequest { email: string; }
export interface InvitationResponse { token: string; tenantId: string; }
export interface PermissionOverrideRequest { permission: string; effect: string; expiresAt?: string; }

// ── Role types ────────────────────────────────────────────────────────────

export interface RoleResponse { id: string; name: string; description?: string; isSystem: boolean; isDefault: boolean; priority: number; createdAt: string; }
export interface RoleDetailResponse { role: RoleResponse; permissions: RolePermissionResponse[]; }
export interface RolePermissionResponse { code: string; displayName: string; effect: number; }
export interface CreateRoleRequest { name: string; description?: string; isDefault: boolean; priority: number; permissions: { code: string; effect: string }[]; }
export interface UpdateRoleRequest { name: string; description?: string; permissions?: { code: string; effect: string }[]; }

export interface MyProfileResponse {
  isOnboarded: boolean;
  userId: string;
  organizationId: string;
  organizationName: string;
  tenants: TenantSummary[];
}

export interface TenantSummary {
  id: string;
  name: string;
  slug: string;
  status: string;
  planKey?: string;
  subscriptionStatus?: string;
}
