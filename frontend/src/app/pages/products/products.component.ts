import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, ProductResponse, QuotaError } from '../../core/services/api.service';
import { HttpClient } from '@angular/common/http';

@Component({
  standalone: true,
  imports: [CommonModule, FormsModule],
  selector: 'app-products',
  templateUrl: './products.component.html',
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 24px; }
    .page-header h2 { font-size: 22px; }
    .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 14px; font-weight: 500; }
    .btn-primary { background: #4fc3f7; color: #1a1a2e; }
    .btn-danger { background: #e74c3c; color: #fff; }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    table { width: 100%; border-collapse: collapse; background: #fff; border-radius: 8px; overflow: hidden; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
    th, td { padding: 12px 16px; text-align: left; font-size: 14px; border-bottom: 1px solid #eee; }
    th { background: #f8f9fa; font-weight: 600; color: #546e7a; }
    .price { font-family: monospace; }
    .create-form { background: #fff; padding: 20px; border-radius: 8px; margin-bottom: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); display: flex; gap: 12px; align-items: flex-end; flex-wrap: wrap; }
    .form-group { display: flex; flex-direction: column; gap: 4px; }
    .form-group label { font-size: 12px; color: #546e7a; font-weight: 500; }
    .form-group input { padding: 8px 12px; border: 1px solid #ddd; border-radius: 6px; font-size: 14px; width: 160px; }
    .toast { padding: 12px 20px; border-radius: 6px; margin-bottom: 16px; font-size: 14px; }
    .toast-error { background: #fdecea; color: #c0392b; border: 1px solid #e74c3c; }
    .toast-success { background: #e8f5e9; color: #2e7d32; border: 1px solid #4caf50; }
  `],
})
export class ProductsComponent implements OnInit {
  private api = inject(ApiService);

  products = signal<ProductResponse[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);

  // Create form
  newName = '';
  newPrice = 0;
  newSize = 4096;
  creating = signal(false);

  async ngOnInit(): Promise<void> {
    await this.loadProducts();
  }

  async loadProducts(): Promise<void> {
    this.loading.set(true);
    try {
      const data = await this.api.getProducts().toPromise();
      this.products.set(data ?? []);
    } catch (e: any) {
      this.error.set(e?.message ?? 'Failed to load products');
    } finally {
      this.loading.set(false);
    }
  }

  async createProduct(): Promise<void> {
    if (!this.newName) return;
    this.creating.set(true);
    this.error.set(null);
    try {
      await this.api.createProduct({
        name: this.newName,
        price: this.newPrice,
        sizeInBytes: this.newSize,
      }).toPromise();
      this.newName = '';
      this.newPrice = 0;
      await this.loadProducts();
    } catch (e: any) {
      const msg = e?.error?.message ?? e?.message ?? 'Failed to create product';
      this.error.set(msg);
    } finally {
      this.creating.set(false);
    }
  }

  async deleteProduct(id: string): Promise<void> {
    try {
      await this.api.deleteProduct(id).toPromise();
      this.products.update((list) => list.filter((p) => p.id !== id));
    } catch (e: any) {
      this.error.set(e?.message ?? 'Failed to delete product');
    }
  }
}
