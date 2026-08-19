import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { environment } from '../environments/environment';

interface BorrowerRow {
  borrowerId: number;
  legalName: string;
  taxId: string;
  creditScore: number;
  depositBalance: number;
  yearsInBusiness: number;
  activeLoans: number;
}

// Legacy "Borrower Lookup" screen (BorrowerLookupForm). Search results come from
// GET /api/borrowers?search=; the pre-qualification hint text comes verbatim from
// GET /api/borrowers/{id}/prequalification (BR-PQL-001 lives in the service layer).
@Component({
  imports: [FormsModule],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected searchText = '';
  protected rows = signal<BorrowerRow[]>([]);
  protected selectedId = signal<number | null>(null);
  protected hintText = signal('');

  protected async search(): Promise<void> {
    try {
      const res = await fetch(
        `${environment.apiBaseUrl}/api/borrowers?search=${encodeURIComponent(this.searchText.trim())}`,
      );
      if (!res.ok) throw new Error(res.statusText);
      const rows = (await res.json()) as BorrowerRow[];
      this.rows.set(rows);
      // Legacy grid auto-selects the first row after a search, which fires the
      // pre-qualification hint (BR-UI-008).
      if (rows.length > 0) {
        await this.select(rows[0]);
      } else {
        this.selectedId.set(null);
      }
    } catch {
      this.rows.set([]);
      this.selectedId.set(null);
    }
  }

  protected async select(row: BorrowerRow): Promise<void> {
    this.selectedId.set(row.borrowerId);
    try {
      const res = await fetch(`${environment.apiBaseUrl}/api/borrowers/${row.borrowerId}/prequalification`);
      if (!res.ok) throw new Error(res.statusText);
      const payload = (await res.json()) as { resultText: string };
      this.hintText.set(payload.resultText);
    } catch {
      // BR-UI-008: every failure blanks the hint silently — no error indication.
      this.hintText.set('');
    }
  }
}
