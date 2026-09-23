import { Component, inject, signal } from '@angular/core';
import { RouterModule } from '@angular/router';
import { LendingApi } from '@lending/api';

/** Launcher chrome of the legacy `MainForm`: title, the four screen entries and a status bar. */
@Component({
  imports: [RouterModule],
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly api = inject(LendingApi);

  protected readonly screens = [
    { path: 'loan-application', text: 'New Loan Application' },
    { path: 'pricing', text: 'Pricing & Amortization' },
    { path: 'borrower-lookup', text: 'Borrower Lookup' },
    { path: 'statements', text: 'Statements Export' },
  ];

  protected readonly connection = signal('Connecting to lending service…');

  constructor() {
    this.api.searchBorrowers('').subscribe({
      next: () => this.connection.set('Connected: LENDING SERVICE (layer 2 /api)'),
      error: () => this.connection.set('Disconnected: LENDING SERVICE (layer 2 /api) unavailable'),
    });
  }
}
