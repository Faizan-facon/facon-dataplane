import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { featureGuard } from './shared/guards/feature.guard';
import { ShellComponent } from './layout/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      {
        path: 'dashboard',
        loadComponent: () => import('./pages/dashboard/dashboard.component').then((m) => m.DashboardComponent),
      },
      {
        path: 'products',
        loadComponent: () => import('./pages/products/products.component').then((m) => m.ProductsComponent),
      },
      {
        path: 'analytics',
        canActivate: [featureGuard('analytics:view')],
        loadComponent: () => import('./pages/analytics/analytics.component').then((m) => m.AnalyticsComponent),
      },
      {
        path: 'upgrade',
        loadComponent: () => import('./pages/upgrade/upgrade.component').then((m) => m.UpgradeComponent),
      },
      {
        path: 'admin/users',
        loadComponent: () => import('./pages/admin/users/users.component').then((m) => m.AdminUsersComponent),
      },
      {
        path: 'admin/users/:sub',
        loadComponent: () => import('./pages/admin/users/user-detail.component').then((m) => m.AdminUserDetailComponent),
      },
      {
        path: 'admin/roles',
        loadComponent: () => import('./pages/admin/roles/roles.component').then((m) => m.AdminRolesComponent),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
