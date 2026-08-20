import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { environment } from '../environments/environment';
import { FormatError, parseDecimal, parseIntStrict } from './legacy-parse';
import { MessageBox } from './message-box';

interface EligibilityResponse {
  decision: 'APPROVED' | 'DECLINED';
  declineReason: string | null;
  dti: number | null;
  ltv: number | null;
  estimatedPayment: number | null;
  resultText: string;
  firedRuleId: string;
  ruleIds: string[];
}

// Legacy "New Loan Application" screen (LoanApplicationForm). The UI parses fields,
// submits them to POST /api/eligibility/evaluate and renders the returned resultText
// verbatim; every threshold and calculation lives in the service layer.
@Component({
  imports: [FormsModule, MessageBox],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly products = ['TERM', 'LOC', 'EQUIP']; // BR-UI-012: fixed order, TERM default
  protected product = 'TERM';
  protected borrowerId = '';
  protected amount = '';
  protected termMonths = '';
  protected annualIncome = '';
  protected monthlyDebt = '';
  protected creditScore = '';
  protected collateralValue = '';
  protected yearsInBusiness = '';

  // BR-ELG-009 / BR-UI-006: initial prompt in the default colour; the colour is only
  // ever set on a result and never reset afterwards.
  protected resultText = signal('Enter application details and press Submit.');
  protected resultColor = signal<'default' | 'approved' | 'declined'>('default');

  protected msg = signal<{ caption: string; text: string; icon: 'warning' | 'error' } | null>(null);

  protected async submit(): Promise<void> {
    let body: Record<string, unknown>;
    try {
      // BR-ELG-011 parse order: product, amount, term, income, debt, score, collateral, years.
      body = {
        productType: this.product,
        amount: parseDecimal(this.amount),
        termMonths: parseIntStrict(this.termMonths),
        annualIncome: parseDecimal(this.annualIncome),
        monthlyDebt: parseDecimal(this.monthlyDebt),
        creditScore: parseIntStrict(this.creditScore),
        collateralValue: parseDecimal(this.collateralValue),
        yearsInBusiness: parseIntStrict(this.yearsInBusiness),
      };
    } catch (e) {
      if (e instanceof FormatError) {
        this.showValidationBox();
        return;
      }
      throw e;
    }

    try {
      const res = await fetch(`${environment.apiBaseUrl}/api/eligibility/evaluate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      const payload = await res.json();
      if (!res.ok) {
        this.showUnexpectedError(payload.message ?? res.statusText);
        return;
      }
      const result = payload as EligibilityResponse;

      if (result.decision === 'APPROVED') {
        // BR-ELG-010/BR-UI-006 ordering: the borrower id is parsed only now (legacy parsed
        // it inside SaveApplication), and the panel is updated only after persistence
        // succeeds — a failure leaves the previous text/colour untouched.
        let borrowerId: number;
        try {
          borrowerId = parseIntStrict(this.borrowerId);
        } catch (e) {
          if (e instanceof FormatError) {
            this.showValidationBox();
            return;
          }
          throw e;
        }
        const save = await fetch(`${environment.apiBaseUrl}/api/applications`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ...body, borrowerId }),
        });
        if (!save.ok) {
          const err = await save.json();
          this.showUnexpectedError(err.message ?? save.statusText);
          return;
        }
        this.resultText.set(result.resultText);
        this.resultColor.set('approved');
      } else {
        this.resultText.set(result.resultText);
        this.resultColor.set('declined');
      }
    } catch (e) {
      this.showUnexpectedError(e instanceof Error ? e.message : String(e));
    }
  }

  // BR-UI-007: FormatException → Validation box; anything else → "Unexpected error: ..."
  private showValidationBox(): void {
    this.msg.set({ caption: 'Validation', text: 'One or more fields contain invalid numbers.', icon: 'warning' });
  }

  private showUnexpectedError(message: string): void {
    this.msg.set({ caption: 'Error', text: 'Unexpected error: ' + message, icon: 'error' });
  }
}
