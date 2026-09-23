import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  EligibilityResponse,
  INVALID_NUMBERS,
  LendingApi,
  LendingApiError,
  MessageBox,
  isDecimal,
  isInteger,
} from '@lending/api';

interface Field {
  key: string;
  label: string;
  kind: 'integer' | 'decimal';
}

/**
 * Legacy `Forms/LoanApplicationForm`. The screen collects the same nine inputs and renders what
 * the service decides; it holds no thresholds and computes no DTI, LTV or payment of its own.
 */
@Component({
  selector: 'app-loan-application-entry',
  imports: [FormsModule, MessageBox],
  templateUrl: './entry.html',
  styleUrl: './entry.css',
})
export class RemoteEntry {
  private readonly api = inject(LendingApi);

  protected readonly products = ['TERM', 'LOC', 'EQUIP'];

  protected readonly fields: Field[] = [
    { key: 'borrowerId', label: 'Borrower ID', kind: 'integer' },
    { key: 'amount', label: 'Loan Amount ($)', kind: 'decimal' },
    { key: 'termMonths', label: 'Term (months)', kind: 'integer' },
    { key: 'annualIncome', label: 'Annual Income ($)', kind: 'decimal' },
    { key: 'monthlyDebt', label: 'Monthly Debt Svc ($)', kind: 'decimal' },
    { key: 'creditScore', label: 'Credit Score', kind: 'integer' },
    { key: 'collateralValue', label: 'Collateral Value ($)', kind: 'decimal' },
    { key: 'yearsInBusiness', label: 'Years in Business', kind: 'integer' },
  ];

  protected product = 'TERM';
  protected values: Record<string, string> = {
    borrowerId: '',
    amount: '',
    termMonths: '',
    annualIncome: '',
    monthlyDebt: '',
    creditScore: '',
    collateralValue: '',
    yearsInBusiness: '',
  };

  protected readonly dialog = signal<string | null>(null);
  protected readonly invalidKeys = signal<string[]>([]);
  protected readonly result = signal<EligibilityResponse | null>(null);
  protected readonly submittedAppId = signal<number | null>(null);
  protected readonly busy = signal(false);

  protected submit(): void {
    // The only client-side rule the legacy screen had: `decimal.Parse` / `int.Parse` failures
    // raised the "invalid numbers" message box before any rule ran.
    const invalid = this.fields
      .filter(({ key, kind }) =>
        kind === 'integer' ? !isInteger(this.values[key]) : !isDecimal(this.values[key])
      )
      .map((field) => field.key);
    this.invalidKeys.set(invalid);
    if (invalid.length > 0) {
      this.dialog.set(INVALID_NUMBERS);
      return;
    }

    this.busy.set(true);
    this.result.set(null);
    this.submittedAppId.set(null);
    this.api
      .evaluateEligibility({
        borrowerId: Number(this.values['borrowerId']),
        productType: this.product,
        amount: Number(this.values['amount']),
        termMonths: Number(this.values['termMonths']),
        annualIncome: Number(this.values['annualIncome']),
        monthlyDebt: Number(this.values['monthlyDebt']),
        creditScore: Number(this.values['creditScore']),
        collateralValue: Number(this.values['collateralValue']),
        yearsInBusiness: Number(this.values['yearsInBusiness']),
      })
      .subscribe({
        next: (response) => {
          this.result.set(response);
          if (response.decision === 'APPROVED') this.persist(response);
          else this.busy.set(false);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.dialog.set(error instanceof LendingApiError ? error.message : String(error));
        },
      });
  }

  /** Legacy `SaveApplication`: the row is written only once the service approves. */
  private persist(response: EligibilityResponse): void {
    this.api
      .createApplication({
        borrowerId: Number(this.values['borrowerId']),
        productType: this.product,
        amount: Number(this.values['amount']),
        termMonths: Number(this.values['termMonths']),
        creditScore: Number(this.values['creditScore']),
        dti: Number(response.dti),
        ltv: Number(response.ltv),
      })
      .subscribe({
        next: ({ appId }) => {
          this.submittedAppId.set(appId);
          this.busy.set(false);
        },
        error: (error: unknown) => {
          this.busy.set(false);
          this.dialog.set(error instanceof LendingApiError ? error.message : String(error));
        },
      });
  }

  protected isInvalid(key: string): boolean {
    return this.invalidKeys().includes(key);
  }
}
