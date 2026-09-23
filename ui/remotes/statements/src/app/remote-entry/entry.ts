import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  INVALID_NUMBERS,
  LendingApi,
  LendingApiError,
  MessageBox,
  ScheduleRow,
  isInteger,
  money,
} from '@lending/api';

/**
 * Legacy `Forms/StatementsForm`. The schedule, the CSV and the payoff quote (BR-SVC-002) are all
 * produced by the service; the screen loads, renders and downloads them.
 */
@Component({
  selector: 'app-statements-entry',
  imports: [FormsModule, MessageBox],
  templateUrl: './entry.html',
  styleUrl: './entry.css',
})
export class RemoteEntry {
  private readonly api = inject(LendingApi);

  protected readonly money = money;
  protected loanId = '';

  protected readonly rows = signal<ScheduleRow[]>([]);
  protected readonly loadedLoanId = signal<number | null>(null);
  protected readonly payoff = signal<string>('');
  protected readonly dialog = signal<string | null>(null);
  protected readonly dialogTitle = signal('Validation');

  protected loadSchedule(): void {
    const loanId = this.parseLoanId();
    if (loanId === null) return;
    this.api.schedule(loanId).subscribe({
      next: (rows) => {
        this.rows.set(rows);
        this.loadedLoanId.set(loanId);
      },
      error: (error: unknown) => this.showDialog('Error', message(error)),
    });
  }

  /** Legacy export wrote the grid to a temp file; here the service's `schedule.csv` is downloaded. */
  protected exportCsv(): void {
    const loanId = this.loadedLoanId();
    if (loanId === null) {
      this.showDialog('Export', 'Load a schedule first.');
      return;
    }
    const link = document.createElement('a');
    link.href = this.api.scheduleCsvUrl(loanId);
    link.download = `loan_${loanId}_schedule.csv`;
    link.click();
  }

  protected payoffQuote(): void {
    const loanId = this.parseLoanId();
    if (loanId === null) return;
    this.api.payoff(loanId).subscribe({
      next: (response) => this.payoff.set(`Payoff as of ${response.asOf}: ${money(response.payoff)}`),
      error: (error: unknown) => this.showDialog('Error', `Payoff lookup failed: ${message(error)}`),
    });
  }

  private parseLoanId(): number | null {
    if (!isInteger(this.loanId)) {
      this.showDialog('Validation', INVALID_NUMBERS);
      return null;
    }
    return Number(this.loanId);
  }

  private showDialog(title: string, text: string): void {
    this.dialogTitle.set(title);
    this.dialog.set(text);
  }
}

function message(error: unknown): string {
  return error instanceof LendingApiError ? error.message : String(error);
}
