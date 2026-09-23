import { Component } from '@angular/core';
import { RouterModule } from '@angular/router';

/** The legacy `MainForm` launcher buttons, in their original order and wording. */
@Component({
  selector: 'app-home',
  imports: [RouterModule],
  template: `
    <section class="screen">
      <h2 class="screen-title">Lending Desk</h2>
      <p class="screen-subtitle">
        Select a screen. Each one is an independently deployed micro frontend loaded on demand.
      </p>
      <div class="launcher">
        @for (screen of screens; track screen.path) {
          <a class="launcher-card" [routerLink]="screen.path">
            <span class="launcher-title">{{ screen.text }}</span>
            <span class="launcher-sub muted">{{ screen.remote }}</span>
          </a>
        }
      </div>
    </section>
  `,
  styles: [
    `
      .launcher {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
        gap: 16px;
        max-width: 720px;
      }
      .launcher-card {
        display: grid;
        gap: 4px;
        padding: 18px 20px;
        background: var(--surface);
        border: 1px solid var(--line-strong);
        border-radius: 3px;
        text-decoration: none;
        color: inherit;
      }
      .launcher-card:hover {
        border-color: var(--accent);
        background: #f7fafd;
      }
      .launcher-title {
        font-size: 15px;
        font-weight: 600;
      }
      .launcher-sub {
        font-size: 12px;
      }
    `,
  ],
})
export class Home {
  protected readonly screens = [
    { path: 'loan-application', text: 'New Loan Application', remote: 'remote: loanApplication' },
    { path: 'pricing', text: 'Pricing & Amortization', remote: 'remote: pricing' },
    { path: 'borrower-lookup', text: 'Borrower Lookup', remote: 'remote: borrowerLookup' },
    { path: 'statements', text: 'Statements Export', remote: 'remote: statements' },
  ];
}
