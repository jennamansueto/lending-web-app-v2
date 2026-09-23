import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { Observable, catchError, map, throwError } from 'rxjs';
import { parseDecimalJson } from './decimal-json';

/**
 * Base URL of the layer-2 service. Defaults to a same-origin `/api`, which the dev servers proxy
 * to `http://localhost:5080` and a deployment fronts with its own reverse proxy.
 */
export const LENDING_API_BASE_URL = new InjectionToken<string>('LENDING_API_BASE_URL', {
  providedIn: 'root',
  factory: () => '/api',
});

/** Money and rates arrive as the service's verbatim decimal text; see `parseDecimalJson`. */
export type Decimal = string;

export interface EligibilityRequest {
  borrowerId: number;
  productType: string;
  amount: number;
  termMonths: number;
  annualIncome: number;
  monthlyDebt: number;
  creditScore: number;
  collateralValue: number;
  yearsInBusiness: number;
}

export interface EligibilityResponse {
  decision: 'APPROVED' | 'DECLINED';
  declineReason: string | null;
  dti: Decimal | null;
  ltv: Decimal | null;
  estimatedPayment: Decimal | null;
  resultText: string;
  firedRuleId: string;
  ruleIds: string[];
}

export interface CreateApplicationRequest {
  borrowerId: number;
  productType: string;
  amount: number;
  termMonths: number;
  creditScore: number;
  dti: number;
  ltv: number;
}

export interface QuoteRequest {
  productType: string;
  amount: number;
  termMonths: number;
  creditScore: number;
  ltv: number;
  depositBalance: number;
}

export interface RateResponse {
  baseRate: Decimal;
  riskSpread: Decimal;
  ltvAdjustment: Decimal;
  relationshipDiscount: Decimal;
  rate: Decimal;
  floored: boolean;
  capped: boolean;
  ruleIds: string[];
}

export interface AmortizationRow {
  period: number;
  payment: Decimal;
  interest: Decimal;
  principal: Decimal;
  balance: Decimal;
}

export interface QuoteResponse {
  rate: RateResponse;
  originationFee: Decimal;
  feeMinApplied: boolean;
  feeCapApplied: boolean;
  payment: Decimal;
  rows: AmortizationRow[];
  ruleIds: string[];
}

export interface LateFeeResponse {
  fee: Decimal;
  ruleIds: string[];
}

export interface Borrower {
  borrowerId: number;
  legalName: string;
  taxId: string;
  creditScore: number;
  depositBalance: Decimal;
  yearsInBusiness: number;
  activeLoans: number;
}

export interface PrequalificationResponse {
  creditScore: number;
  prequalifiedProducts: string;
  resultText: string;
  ruleIds: string[];
}

export interface ScheduleRow {
  periodNo: number;
  dueDate: string;
  paymentAmt: Decimal;
  interestAmt: Decimal;
  principalAmt: Decimal;
  balanceAfter: Decimal;
}

export interface PayoffResponse {
  loanId: number;
  asOf: string;
  balance: Decimal;
  accruedInterest: Decimal;
  unpaidLateFees: Decimal;
  payoff: Decimal;
  ruleIds: string[];
}

/** Carries the service's message verbatim; the screens show it in the legacy message box. */
export class LendingApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
    this.name = 'LendingApiError';
  }
}

/**
 * The only way a screen reaches business behavior. It performs no arithmetic and applies no
 * thresholds: every decision, rate, fee, payment and result string comes back from layer 2.
 */
@Injectable({ providedIn: 'root' })
export class LendingApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(LENDING_API_BASE_URL);

  evaluateEligibility(request: EligibilityRequest): Observable<EligibilityResponse> {
    return this.post<EligibilityResponse>('/eligibility/evaluate', request);
  }

  createApplication(request: CreateApplicationRequest): Observable<{ appId: number }> {
    return this.post<{ appId: number }>('/applications', request);
  }

  quote(request: QuoteRequest): Observable<QuoteResponse> {
    return this.post<QuoteResponse>('/pricing/quote', request);
  }

  lateFee(paymentAmount: number, daysLate: number): Observable<LateFeeResponse> {
    return this.post<LateFeeResponse>('/servicing/late-fee', { paymentAmount, daysLate });
  }

  searchBorrowers(search: string): Observable<Borrower[]> {
    return this.get<Borrower[]>(`/borrowers?search=${encodeURIComponent(search)}`);
  }

  prequalification(borrowerId: number): Observable<PrequalificationResponse> {
    return this.get<PrequalificationResponse>(`/borrowers/${borrowerId}/prequalification`);
  }

  schedule(loanId: number): Observable<ScheduleRow[]> {
    return this.get<ScheduleRow[]>(`/loans/${loanId}/schedule`);
  }

  scheduleCsvUrl(loanId: number): string {
    return `${this.baseUrl}/loans/${loanId}/schedule.csv`;
  }

  payoff(loanId: number): Observable<PayoffResponse> {
    return this.get<PayoffResponse>(`/loans/${loanId}/payoff`);
  }

  private get<T>(path: string): Observable<T> {
    return this.http
      .get(this.baseUrl + path, { responseType: 'text' })
      .pipe(map((text) => parseDecimalJson<T>(text)), catchError(toApiError));
  }

  private post<T>(path: string, body: unknown): Observable<T> {
    return this.http
      .post(this.baseUrl + path, body, { responseType: 'text' })
      .pipe(map((text) => parseDecimalJson<T>(text)), catchError(toApiError));
  }
}

function toApiError(error: unknown): Observable<never> {
  if (error instanceof HttpErrorResponse) {
    let message = error.message;
    try {
      const body = JSON.parse(error.error as string) as { message?: string };
      if (body.message) message = body.message;
    } catch {
      if (typeof error.error === 'string' && error.error) message = error.error;
    }
    return throwError(() => new LendingApiError(message, error.status));
  }
  return throwError(() => error);
}
