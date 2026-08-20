import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { environment } from '../environments/environment';
import { FormatError, parseDecimal, parseIntStrict } from './legacy-parse';
import { MessageBox } from './message-box';

interface ScheduleRow {
  period: number;
  payment: number;
  interest: number;
  principal: number;
  balance: number;
}

interface QuoteResponse {
  rate: number;
  originationFee: number;
  monthlyPayment: number;
  rows: ScheduleRow[];
}

// Legacy "Pricing & Amortization" screen (PricingForm). All rate/fee/payment/schedule
// values come from POST /api/pricing/quote; the late fee from POST /api/servicing/late-fee.
// The UI performs no arithmetic — it parses inputs and formats returned numbers.
@Component({
  imports: [FormsModule, MessageBox],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly products = ['TERM', 'LOC', 'EQUIP']; // BR-UI-012: fixed order, TERM default
  protected product = 'TERM';
  protected amount = '';
  protected termMonths = '';
  protected creditScore = '';
  protected ltv = '';
  protected deposits = '';
  protected lateDays = '';

  // BR-UI-001: values persist until the next click.
  protected rateText = signal('');
  protected feeText = signal('');
  protected paymentText = signal('');
  protected lateFeeText = signal(''); // BR-UI-004
  protected rows = signal<ScheduleRow[]>([]);

  protected msg = signal<{ caption: string; text: string; icon: 'warning' | 'error' } | null>(null);

  protected async priceLoan(): Promise<void> {
    let body: Record<string, unknown>;
    try {
      // BR-UI-014: a blank deposit balance is the only optional field and means 0.
      body = {
        productType: this.product,
        amount: parseDecimal(this.amount),
        termMonths: parseIntStrict(this.termMonths),
        creditScore: parseIntStrict(this.creditScore),
        ltv: parseDecimal(this.ltv),
        depositBalance: this.deposits.trim() === '' ? 0 : parseDecimal(this.deposits),
      };
    } catch (e) {
      if (e instanceof FormatError) {
        // BR-UI-003: only parse failures are caught here.
        this.msg.set({ caption: 'Validation', text: 'One or more fields contain invalid numbers.', icon: 'warning' });
        return;
      }
      throw e;
    }

    const res = await fetch(`${environment.apiBaseUrl}/api/pricing/quote`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const payload = await res.json();
    if (!res.ok) {
      this.msg.set({ caption: 'Error', text: payload.message ?? res.statusText, icon: 'error' });
      return;
    }
    const quote = payload as QuoteResponse;
    // BR-UI-001: exact label strings — rate 0.00 with a space before %, fee/payment C2.
    this.rateText.set('Rate: ' + quote.rate.toFixed(2) + ' %');
    this.feeText.set('Origination fee: ' + formatC2(quote.originationFee));
    this.paymentText.set('Monthly payment: ' + formatC2(quote.monthlyPayment));
    this.rows.set(quote.rows);
  }

  protected async lateFee(): Promise<void> {
    try {
      // BR-UI-005 (LEND-5102): the payment amount is deliberately taken from the
      // Loan Amount ($) box, not from a scheduled payment. Preserved as-is.
      const paymentAmount = parseDecimal(this.amount);
      const daysLate = parseIntStrict(this.lateDays);
      const res = await fetch(`${environment.apiBaseUrl}/api/servicing/late-fee`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ paymentAmount, daysLate }),
      });
      const payload = await res.json();
      if (!res.ok) throw new Error(payload.message ?? res.statusText);
      this.lateFeeText.set('Late fee: ' + formatC2(payload.fee));
    } catch (e) {
      // BR-UI-004: a single broad handler — parse, connection and service failures
      // all land in the same box.
      const message = e instanceof FormatError ? 'Input string was not in a correct format.' : e instanceof Error ? e.message : String(e);
      this.msg.set({ caption: 'Error', text: 'Late fee lookup failed: ' + message, icon: 'error' });
    }
  }

  // BR-UI-002: raw decimal rendering in the grid (no currency symbol, 2-dp scale).
  protected cell(v: number): string {
    return v.toFixed(2);
  }
}

const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2 });

function formatC2(v: number): string {
  return usd.format(v);
}
