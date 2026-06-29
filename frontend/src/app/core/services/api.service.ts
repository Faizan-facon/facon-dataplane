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
}

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
  subscriptionStatus?: string;
}
