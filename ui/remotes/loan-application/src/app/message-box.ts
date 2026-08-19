import { Component, input, output } from '@angular/core';

// Web equivalent of the legacy WinForms MessageBox.Show(text, caption, OK, icon).
@Component({
  selector: 'app-message-box',
  template: `
    <div class="msgbox-backdrop">
      <div class="msgbox" role="alertdialog">
        <div class="msgbox-caption">{{ caption() }}</div>
        <div class="msgbox-body">
          <span class="msgbox-icon" [class.warning]="icon() === 'warning'" [class.error]="icon() === 'error'">
            {{ icon() === 'warning' ? '⚠' : icon() === 'error' ? '✖' : 'ℹ' }}
          </span>
          <span class="msgbox-text">{{ text() }}</span>
        </div>
        <div class="msgbox-buttons">
          <button type="button" (click)="closed.emit()">OK</button>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      .msgbox-backdrop { position: fixed; inset: 0; background: rgba(0, 0, 0, 0.3); display: flex; align-items: center; justify-content: center; z-index: 1000; }
      .msgbox { background: #f0f0f0; border: 1px solid #707070; min-width: 320px; max-width: 480px; box-shadow: 4px 4px 12px rgba(0, 0, 0, 0.4); }
      .msgbox-caption { background: #fff; padding: 6px 10px; font-weight: bold; border-bottom: 1px solid #d0d0d0; }
      .msgbox-body { display: flex; gap: 10px; padding: 16px 12px; align-items: flex-start; }
      .msgbox-icon { font-size: 20px; }
      .msgbox-icon.warning { color: #e5a000; }
      .msgbox-icon.error { color: #c00000; }
      .msgbox-text { white-space: pre-line; }
      .msgbox-buttons { text-align: right; padding: 8px 12px 12px; }
      button { min-width: 75px; padding: 4px 12px; }
    `,
  ],
})
export class MessageBox {
  caption = input<string>('');
  text = input<string>('');
  icon = input<'warning' | 'error' | 'info'>('info');
  closed = output<void>();
}
