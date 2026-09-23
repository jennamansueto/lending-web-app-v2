import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  INVALID_NUMBERS,
  LendingApi,
  LendingApiError,
  MessageBox,
  QuoteResponse,
  isDecimal,
  isInteger,
  money,
  percent,
} from '@lending/api';

interface Field {
  key: string;
  label: string;
  kind: 'integer' | 'decimal';
}

/**
 * Legacy `Forms/PricingForm`. Rate, origination fee, monthly payment, the amortization grid and
 * the late fee are all returned by the service; the screen only renders them.
 */
@Component({
  selector: 'app-pricing-entry',
  imports: [FormsModule, MessageBox],
  templateUrl: './entry.html',
  styleUrl: './entry.css',
})
export class RemoteEntry {
  private readonly api = inject(LendingApi);

  protected readonly money = money;
  protected readonly percent = percent;
  protected readonly products = ['TERM', 'LOC', 'EQUIP'];

  protected readonly fields: Field[] = [
    { key: 'amount', label: 'Loan Amount ($)', kind: 'decimal' },
    { key: 'termMonths', label: 'Term (months)', kind: 'integer' },
    { key: 'creditScore', label: 'Credit Score', kind: 'integer' },
    { key: 'ltv', label: 'LTV (0-1)', kind: 'decimal' },
    { key: 'depositBalance', label: 'Deposit Balance ($)', kind: 'decimal' },
  ];

  protected product = 'TERM';
  protected values: Record<string, string> = {
    amount: '',
    termMonths: '',
    creditScore: '',
    ltv: '',
    depositBalance: '',
  };
  protected daysLate = '';

  protected readonly dialog = signal<string | null>(null);
  protected readonly dialogTitle = signal('Validation');
  protected readonly invalidKeys = signal<string[]>([]);
  protected readonly quote = signal<QuoteResponse | null>(null);
  protected readonly lateFee = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected priceLoan(): void {
    // Deposit Balance was optional on the legacy form and defaulted to zero.
    const depositBalance = this.values['depositBalance'].trim() === '' ? '0' : this.values['depositBalance'];
    const invalid = this.fields
      .filter(({ key, kind }) => {
        const text = key === 'depositBalance' ? depositBalance : this.values[key];
        return kind === 'integer' ? !isInteger(text) : !isDecimal(text);
      })
      .map((field) => field.key);
    this.invalidKeys.set(invalid);
    if (invalid.length > 0) {
      this.showDialog('Validation', INVALID_NUMBERS);
      return;
    }

    this.busy.set(true);
    this.api
      .quote({
        productType: this.product,
        amount: Number(this.values['amount']),
        termMonths: Number(this.values['termMonths']),
        creditScore: Number(this.values['creditScore']),
        ltv: Number(this.values['ltv']),
        depositBalance: Number(depositBalance),
      })
      .subscribe({
        next: (response) => {
          this.quote.set(response);
          this.busy.set(false);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.showDialog('Validation', message(error));
        },
      });
  }

  protected requestLateFee(): void {
    // LEND-5102: the legacy button sends the Loan Amount box as the payment amount. The defect is
    // observable behavior and is preserved deliberately — do not switch this to a payment field.
    const paymentAmount = this.values['amount'];
    if (!isDecimal(paymentAmount) || !isInteger(this.daysLate)) {
      this.showDialog('Error', INVALID_NUMBERS);
      return;
    }
    this.api.lateFee(Number(paymentAmount), Number(this.daysLate)).subscribe({
      next: (response) => this.lateFee.set(response.fee),
      error: (error: unknown) => this.showDialog('Error', `Late fee lookup failed: ${message(error)}`),
    });
  }

  protected isInvalid(key: string): boolean {
    return this.invalidKeys().includes(key);
  }

  private showDialog(title: string, text: string): void {
    this.dialogTitle.set(title);
    this.dialog.set(text);
  }
}

function message(error: unknown): string {
  return error instanceof LendingApiError ? error.message : String(error);
}
