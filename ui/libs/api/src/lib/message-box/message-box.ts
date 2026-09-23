import { Component, EventEmitter, Input, Output } from '@angular/core';

/** The legacy `MessageBox.Show(text, title, OK, Warning)` dialog, as a modal. */
@Component({
  selector: 'lending-message-box',
  standalone: true,
  template: `
    @if (text) {
      <div class="msgbox-backdrop">
        <div class="msgbox" role="alertdialog" aria-modal="true">
          <header class="msgbox-title">{{ title }}</header>
          <p class="msgbox-text">{{ text }}</p>
          <footer class="msgbox-actions">
            <button type="button" class="btn" (click)="dismissed.emit()">OK</button>
          </footer>
        </div>
      </div>
    }
  `,
})
export class MessageBox {
  @Input() text: string | null = null;
  @Input() title = 'Validation';
  @Output() readonly dismissed = new EventEmitter<void>();
}

/** Legacy `FormatException` text from every numeric screen. */
export const INVALID_NUMBERS = 'One or more fields contain invalid numbers.';
