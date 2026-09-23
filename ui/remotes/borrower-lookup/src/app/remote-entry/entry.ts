import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Borrower, LendingApi, LendingApiError, MessageBox, money } from '@lending/api';

/**
 * Legacy `Forms/BorrowerLookupForm`. The search and the pre-qualification hint (BR-PQL-001, whose
 * thresholds are duplicated from eligibility — LEND-3987) both live in the service.
 */
@Component({
  selector: 'app-borrower-lookup-entry',
  imports: [FormsModule, MessageBox],
  templateUrl: './entry.html',
  styleUrl: './entry.css',
})
export class RemoteEntry {
  private readonly api = inject(LendingApi);

  protected readonly money = money;
  protected search = '';

  protected readonly rows = signal<Borrower[]>([]);
  protected readonly searched = signal(false);
  protected readonly selectedId = signal<number | null>(null);
  protected readonly prequalification = signal<string>('');
  protected readonly dialog = signal<string | null>(null);

  protected runSearch(): void {
    this.api.searchBorrowers(this.search).subscribe({
      next: (rows) => {
        this.rows.set(rows);
        this.searched.set(true);
        this.selectedId.set(null);
        this.prequalification.set('');
      },
      error: (error: unknown) => this.dialog.set(message(error)),
    });
  }

  /** Legacy `grid_SelectionChanged`: selecting a row shows that borrower's pre-qualification. */
  protected select(borrower: Borrower): void {
    this.selectedId.set(borrower.borrowerId);
    this.api.prequalification(borrower.borrowerId).subscribe({
      next: (response) => this.prequalification.set(response.resultText),
      error: (error: unknown) => {
        this.prequalification.set('');
        this.dialog.set(message(error));
      },
    });
  }
}

function message(error: unknown): string {
  return error instanceof LendingApiError ? error.message : String(error);
}
