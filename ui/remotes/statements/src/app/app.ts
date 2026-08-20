import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { environment } from '../environments/environment';
import { MessageBox } from './message-box';

interface ScheduleRow {
  periodNo: number;
  dueDate: string;
  paymentAmt: number;
  interestAmt: number;
  principalAmt: number;
  balanceAfter: number;
}

// Legacy "Statements Export" screen (StatementsForm). Schedule rows come from
// GET /api/loans/{id}/schedule, the CSV from GET /api/loans/{id}/schedule.csv (rendered
// by the service, byte-identical legacy format), and the payoff from
// GET /api/loans/{id}/payoff?asOf=today (BR-SVC-002 lives in the service layer).
@Component({
  imports: [FormsModule, MessageBox],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected loanId = '';
  protected rows = signal<ScheduleRow[] | null>(null);
  protected payoffText = signal(''); // BR-UI-011
  protected msg = signal<{ caption: string; text: string; icon: 'warning' | 'error' | 'info' } | null>(null);

  protected async loadSchedule(): Promise<void> {
    // BR-UI-009: the legacy handler has no try/catch; an unknown loan id simply
    // yields an empty grid with no message.
    const res = await fetch(`${environment.apiBaseUrl}/api/loans/${encodeURIComponent(this.loanId.trim())}/schedule`);
    if (!res.ok) {
      this.rows.set([]);
      return;
    }
    const payload = (await res.json()) as { rows: ScheduleRow[] };
    this.rows.set(payload.rows);
  }

  protected async exportCsv(): Promise<void> {
    // BR-UI-010: guard checks whether a schedule was ever loaded, not the row count.
    if (this.rows() === null) {
      this.msg.set({ caption: '', text: 'Load a schedule first.', icon: 'info' });
      return;
    }
    const id = this.loanId.trim();
    const res = await fetch(`${environment.apiBaseUrl}/api/loans/${encodeURIComponent(id)}/schedule.csv`);
    const csv = await res.text();
    // Legacy wrote to the workstation temp directory; the web equivalent is a browser
    // download with the same loan_<id>_schedule.csv name (divergence documented in
    // docs/ui-layer.md).
    const fileName = `loan_${id}_schedule.csv`;
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    URL.revokeObjectURL(url);
    this.msg.set({ caption: 'Export complete', text: 'Exported to ' + fileName, icon: 'info' });
  }

  protected async payoffQuote(): Promise<void> {
    try {
      // BR-SVC-004: the as-of date is the client's "today".
      const today = new Date();
      const asOf = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;
      const res = await fetch(
        `${environment.apiBaseUrl}/api/loans/${encodeURIComponent(this.loanId.trim())}/payoff?asOf=${asOf}`,
      );
      const payload = await res.json();
      if (!res.ok) throw new Error(payload.message ?? res.statusText);
      // BR-UI-011: "Payoff as of <short date>: <C2>" — short date is legacy en-US M/d/yyyy.
      const shortDate = `${today.getMonth() + 1}/${today.getDate()}/${today.getFullYear()}`;
      this.payoffText.set('Payoff as of ' + shortDate + ': ' + usd.format(payload.payoff));
    } catch (e) {
      this.msg.set({
        caption: 'Error',
        text: 'Payoff lookup failed: ' + (e instanceof Error ? e.message : String(e)),
        icon: 'error',
      });
    }
  }

  protected cell(v: number): string {
    return v.toFixed(2);
  }
}

const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2 });
