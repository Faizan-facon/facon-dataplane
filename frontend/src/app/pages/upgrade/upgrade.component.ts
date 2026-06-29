import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  standalone: true,
  imports: [CommonModule],
  selector: 'app-upgrade',
  styles: [`
    .upgrade-card { max-width: 480px; margin: 60px auto; text-align: center; background: #fff; padding: 40px; border-radius: 12px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); }
    .upgrade-card h2 { margin-bottom: 12px; }
    .upgrade-card p { color: #546e7a; margin-bottom: 24px; font-size: 15px; }
    .btn { padding: 12px 32px; border: none; border-radius: 6px; cursor: pointer; font-size: 15px; font-weight: 600; background: #ffd700; color: #1a1a2e; }
  `],
  template: `
    <div class="upgrade-card">
      <h2>⭐ Upgrade Your Plan</h2>
      <p>This feature requires a higher-tier plan. Upgrade to Pro or Enterprise to unlock analytics, reports, and more.</p>
      <button class="btn">View Plans</button>
    </div>
  `,
})
export class UpgradeComponent {}
