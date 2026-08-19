// Development mock of the service layer defined in docs/api-contract.md.
// Serves http://localhost:5080 with the exact contract shapes and legacy result strings
// so UI remotes can be developed and demoed before the real .NET 8 service exists.
// Business rules mirror docs/business-rules.md of the legacy repo (BR-ELG/PRC/AMT/SVC/PQL).
// This file lives under ui/** as UI development infrastructure only — it is NOT the
// service layer and is never bundled into any remote.
import http from 'node:http';

const PORT = process.env.PORT ? Number(process.env.PORT) : 5080;

// ---------------------------------------------------------------- formatting

const roundAway = (x, d) => {
  const s = Math.pow(10, d);
  return Math.sign(x || 1) * Math.round(Number((Math.abs(x) * s).toFixed(6))) / s;
};
const roundEven = (x, d) => {
  const s = Math.pow(10, d);
  const v = Number((Math.abs(x) * s).toFixed(6));
  const fl = Math.floor(v);
  const diff = v - fl;
  let r;
  if (diff > 0.5) r = fl + 1;
  else if (diff < 0.5) r = fl;
  else r = fl % 2 === 0 ? fl : fl + 1;
  return (Math.sign(x || 1) * r) / s;
};
const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2 });
const fmtC2 = (x) => usd.format(roundAway(x, 2));
const fmt3 = (x) => roundAway(x, 3).toFixed(3);

// ---------------------------------------------------------------- seed data (legacy database/seed)

const BORROWERS = [
  { borrowerId: 1, legalName: 'ACME INDUSTRIAL SUPPLY LLC', taxId: '84-1234567', creditScore: 742, depositBalance: 310000.0, yearsInBusiness: 12 },
  { borrowerId: 2, legalName: 'BLUE HARBOR SEAFOOD CO', taxId: '84-2345678', creditScore: 688, depositBalance: 145000.0, yearsInBusiness: 7 },
  { borrowerId: 3, legalName: 'CASCADE PRECISION MACHINING', taxId: '84-3456789', creditScore: 731, depositBalance: 82000.0, yearsInBusiness: 15 },
  { borrowerId: 4, legalName: 'DELTA LOGISTICS PARTNERS', taxId: '84-4567890', creditScore: 655, depositBalance: 21000.0, yearsInBusiness: 3 },
  { borrowerId: 5, legalName: 'EVERGREEN DENTAL GROUP', taxId: '84-5678901', creditScore: 778, depositBalance: 505000.0, yearsInBusiness: 9 },
  { borrowerId: 6, legalName: 'FOUNDRY COFFEE ROASTERS', taxId: '84-6789012', creditScore: 631, depositBalance: 12000.0, yearsInBusiness: 4 },
  { borrowerId: 7, legalName: 'GRANITE PEAK CONSTRUCTION', taxId: '84-7890123', creditScore: 702, depositBalance: 96000.0, yearsInBusiness: 18 },
  { borrowerId: 8, legalName: 'HARBORLIGHT MARINE SERVICES', taxId: '84-8901234', creditScore: 664, depositBalance: 54000.0, yearsInBusiness: 6 },
];

const LOANS = [
  { loanId: 1, borrowerId: 1, productType: 'TERM', principal: 450000.0, annualRate: 7.25, termMonths: 84, origFee: 4500.0, fundedDate: '2021-04-30' },
  { loanId: 2, borrowerId: 2, productType: 'EQUIP', principal: 175000.0, annualRate: 8.15, termMonths: 60, origFee: 2187.5, fundedDate: '2022-02-01' },
  { loanId: 3, borrowerId: 3, productType: 'TERM', principal: 820000.0, annualRate: 6.35, termMonths: 120, origFee: 8200.0, fundedDate: '2020-08-20' },
  { loanId: 4, borrowerId: 5, productType: 'LOC', principal: 250000.0, annualRate: 7.0, termMonths: 36, origFee: 1875.0, fundedDate: '2023-03-10' },
  { loanId: 5, borrowerId: 7, productType: 'EQUIP', principal: 310000.0, annualRate: 8.3, termMonths: 72, origFee: 3875.0, fundedDate: '2021-12-01' },
];

const PAYMENTS = [
  { loanId: 2, lateFee: 0.0 },
  { loanId: 2, lateFee: 150.0 },
  { loanId: 2, lateFee: 0.0 },
  { loanId: 1, lateFee: 0.0 },
  { loanId: 1, lateFee: 0.0 },
];

const activeLoanCount = (borrowerId) => LOANS.filter((l) => l.borrowerId === borrowerId).length;

// ---------------------------------------------------------------- pricing (BR-PRC-001..007)

class UnknownProductError extends Error {
  constructor(productType) {
    super('Unknown product type: ' + productType);
  }
}

const baseRate = (p) => {
  if (p === 'TERM') return 6.5;
  if (p === 'LOC') return 7.25;
  if (p === 'EQUIP') return 6.9;
  throw new UnknownProductError(p);
};
const riskSpread = (s) => (s >= 760 ? 0.0 : s >= 720 ? 0.35 : s >= 680 ? 0.85 : s >= 640 ? 1.6 : 2.75);
const ltvAdjustment = (l) => (l <= 0.6 ? -0.15 : l <= 0.75 ? 0.0 : l <= 0.85 ? 0.4 : 0.9);
const relationshipDiscount = (d) => (d >= 250000 ? 0.25 : d >= 100000 ? 0.1 : 0.0);

const priceRate = (productType, creditScore, ltv, depositBalance) => {
  let rate = baseRate(productType) + riskSpread(creditScore) + ltvAdjustment(ltv) - relationshipDiscount(depositBalance);
  let floored = false;
  let capped = false;
  if (rate < 4.0) {
    rate = 4.0;
    floored = true;
  }
  if (rate > 12.5) {
    rate = 12.5;
    capped = true;
  }
  return {
    baseRate: baseRate(productType),
    riskSpread: riskSpread(creditScore),
    ltvAdjustment: ltvAdjustment(ltv),
    relationshipDiscount: relationshipDiscount(depositBalance),
    rate: roundAway(rate, 2),
    floored,
    capped,
  };
};

const origFee = (amount, productType) => {
  let rate, min, cap;
  if (productType === 'TERM') {
    rate = 0.01; min = 500; cap = 25000;
  } else if (productType === 'LOC') {
    rate = 0.0075; min = 350; cap = null;
  } else if (productType === 'EQUIP') {
    rate = 0.0125; min = 500; cap = null;
  } else {
    throw new UnknownProductError(productType);
  }
  let fee = roundAway(amount * rate, 2);
  let minApplied = false;
  let capApplied = false;
  if (fee < min) { fee = min; minApplied = true; }
  if (cap !== null && fee > cap) { fee = cap; capApplied = true; }
  return { fee, minApplied, capApplied };
};

// ---------------------------------------------------------------- amortization (BR-AMT-001..003)

const monthlyPayment = (principal, annualRatePct, termMonths) => {
  if (termMonths <= 0) throw new Error('termMonths must be positive');
  if (annualRatePct === 0) return roundAway(principal / termMonths, 2);
  const i = annualRatePct / 100 / 12;
  const pow = Math.pow(1 + i, termMonths); // legacy double round-trip (LEND-4471)
  return roundAway((principal * i * pow) / (pow - 1), 2);
};

const buildSchedule = (principal, annualRatePct, termMonths) => {
  const payment = monthlyPayment(principal, annualRatePct, termMonths);
  const i = annualRatePct / 100 / 12;
  const rows = [];
  let balance = principal;
  for (let period = 1; period <= termMonths; period++) {
    const interest = roundAway(balance * i, 2);
    let principalPortion = roundAway(payment - interest, 2);
    let actualPayment = payment;
    if (period === termMonths || principalPortion >= balance) {
      principalPortion = balance;
      actualPayment = roundAway(balance + interest, 2);
    }
    balance = roundAway(balance - principalPortion, 2);
    rows.push({
      period,
      payment: roundAway(actualPayment, 2),
      interest,
      principal: roundAway(principalPortion, 2),
      balance,
    });
    if (balance <= 0) break;
  }
  return { payment, rows };
};

// ---------------------------------------------------------------- servicing (BR-SVC-001..002)

const calcLateFee = (paymentAmount, daysLate) => {
  if (daysLate <= 10) return 0;
  let fee = roundAway(paymentAmount * 0.05, 2);
  if (fee < 25) fee = 25;
  else if (fee > 150) fee = 150;
  return fee;
};

const addMonths = (iso, n) => {
  const [y, m, d] = iso.split('-').map(Number);
  const lastDayOfSource = new Date(Date.UTC(y, m, 0)).getUTCDate();
  // Oracle ADD_MONTHS: a last-day-of-month input yields the last day of the target
  // month; otherwise the day is kept, clamped to the target month's length.
  const lastDayOfTarget = new Date(Date.UTC(y, m + n, 0)).getUTCDate();
  const day = d === lastDayOfSource ? lastDayOfTarget : Math.min(d, lastDayOfTarget);
  return new Date(Date.UTC(y, m - 1 + n, day)).toISOString().slice(0, 10);
};
const daysBetween = (fromIso, toIso) => Math.round((Date.parse(toIso) - Date.parse(fromIso)) / 86400000);

const scheduleForLoan = (loanId) => {
  const loan = LOANS.find((l) => l.loanId === loanId);
  if (!loan) return null;
  const { rows } = buildSchedule(loan.principal, loan.annualRate, loan.termMonths);
  return rows.map((r) => ({
    periodNo: r.period,
    dueDate: addMonths(loan.fundedDate, r.period),
    paymentAmt: r.payment,
    interestAmt: r.interest,
    principalAmt: r.principal,
    balanceAfter: r.balance,
  }));
};

const payoff = (loanId, asOf) => {
  const loan = LOANS.find((l) => l.loanId === loanId);
  if (!loan) return null;
  const sched = scheduleForLoan(loanId).filter((r) => r.dueDate <= asOf);
  let balance, lastDue;
  if (sched.length === 0) {
    balance = loan.principal;
    lastDue = loan.fundedDate;
  } else {
    balance = Math.min(...sched.map((r) => r.balanceAfter));
    lastDue = sched.map((r) => r.dueDate).sort().at(-1);
  }
  let accrued = roundAway(balance * (loan.annualRate / 100) * (daysBetween(lastDue, asOf) / 365), 2);
  if (accrued < 0) accrued = 0;
  const lateFees = PAYMENTS.filter((p) => p.loanId === loanId).reduce((a, p) => a + p.lateFee, 0);
  return {
    loanId,
    asOf,
    balance: roundAway(balance, 2),
    accruedInterest: accrued,
    unpaidLateFees: roundAway(lateFees, 2),
    payoff: roundAway(balance + accrued + lateFees, 2),
  };
};

// ---------------------------------------------------------------- eligibility (BR-ELG-001..009)

let nextAppId = 1000;

const evaluateEligibility = (req) => {
  const { productType, amount, termMonths, annualIncome, monthlyDebt, creditScore, collateralValue, yearsInBusiness } = req;
  const ruleIds = [];
  const decline = (firedRuleId, reason) => ({
    decision: 'DECLINED',
    declineReason: reason,
    dti: null,
    ltv: null,
    estimatedPayment: null,
    resultText: 'DECLINED\n' + reason,
    firedRuleId,
    ruleIds,
  });

  ruleIds.push('BR-ELG-001');
  if (amount < 25000) return decline('BR-ELG-001', 'Loan amount below $25,000 minimum.');
  ruleIds.push('BR-ELG-002');
  if (amount > 5000000) return decline('BR-ELG-002', 'Loan amount exceeds $5,000,000 desk limit; refer to Credit Committee.');
  ruleIds.push('BR-ELG-003');
  const maxTerm = productType === 'TERM' ? 120 : productType === 'EQUIP' ? 84 : 36;
  if (termMonths < 12 || termMonths > maxTerm) {
    return decline('BR-ELG-003', `Term must be between 12 and ${maxTerm} months for product ${productType}.`);
  }
  ruleIds.push('BR-ELG-004');
  const minScore = productType === 'LOC' ? 660 : productType === 'EQUIP' ? 620 : 640;
  if (creditScore < minScore) {
    return decline('BR-ELG-004', `Credit score ${creditScore} below product minimum of ${minScore}.`);
  }
  ruleIds.push('BR-ELG-005');
  if (annualIncome === 0) {
    const err = new Error('Attempted to divide by zero.');
    err.httpStatus = 500;
    throw err;
  }
  const estimatedPayment = monthlyPayment(amount, baseRate(productType), termMonths);
  const dti = (monthlyDebt + estimatedPayment) / (annualIncome / 12);
  const maxDti = productType === 'LOC' ? 0.4 : 0.45;
  if (dti > maxDti) {
    return decline('BR-ELG-005', `DTI ${fmt3(dti)} exceeds maximum ${maxDti.toFixed(2)}.`);
  }
  ruleIds.push('BR-ELG-006');
  if (collateralValue <= 0) return decline('BR-ELG-006', 'Collateral value required.');
  ruleIds.push('BR-ELG-007');
  const ltv = amount / collateralValue;
  const maxLtv = productType === 'TERM' ? 0.9 : productType === 'EQUIP' ? 0.85 : 0.8;
  if (ltv > maxLtv) {
    return decline('BR-ELG-007', `LTV ${fmt3(ltv)} exceeds maximum ${maxLtv.toFixed(2)} for ${productType}.`);
  }
  ruleIds.push('BR-ELG-008');
  if (productType === 'LOC' && yearsInBusiness < 2) {
    return decline('BR-ELG-008', 'Lines of credit require at least 2 years in business.');
  }
  ruleIds.push('BR-ELG-009');
  return {
    decision: 'APPROVED',
    declineReason: null,
    dti,
    ltv,
    estimatedPayment,
    resultText:
      'APPROVED FOR UNDERWRITING\nDTI: ' + fmt3(dti) + '   LTV: ' + fmt3(ltv) +
      '\nEst. payment at base rate: ' + fmtC2(estimatedPayment),
    firedRuleId: 'BR-ELG-009',
    ruleIds,
  };
};

// ---------------------------------------------------------------- pre-qualification (BR-PQL-001)

const prequalify = (score) =>
  score >= 660 ? 'TERM, LOC, EQUIP' : score >= 640 ? 'TERM, EQUIP' : score >= 620 ? 'EQUIP only' : 'None \u2014 refer to special assets';

// ---------------------------------------------------------------- http plumbing

const json = (res, status, body) => {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json',
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': 'Content-Type',
    'Access-Control-Allow-Methods': 'GET,POST,OPTIONS',
  });
  res.end(payload);
};

const readBody = (req) =>
  new Promise((resolve, reject) => {
    let data = '';
    req.on('data', (c) => (data += c));
    req.on('end', () => {
      try {
        resolve(data ? JSON.parse(data) : {});
      } catch (e) {
        reject(e);
      }
    });
  });

http
  .createServer(async (req, res) => {
    const url = new URL(req.url, `http://localhost:${PORT}`);
    const path = url.pathname;
    if (req.method === 'OPTIONS') {
      res.writeHead(204, {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Headers': 'Content-Type',
        'Access-Control-Allow-Methods': 'GET,POST,OPTIONS',
      });
      return res.end();
    }
    try {
      if (req.method === 'POST' && path === '/api/eligibility/evaluate') {
        const body = await readBody(req);
        return json(res, 200, evaluateEligibility(body));
      }
      if (req.method === 'POST' && path === '/api/applications') {
        await readBody(req);
        return json(res, 200, { appId: nextAppId++ });
      }
      let m = path.match(/^\/api\/borrowers\/(\d+)\/prequalification$/);
      if (req.method === 'GET' && m) {
        const b = BORROWERS.find((x) => x.borrowerId === Number(m[1]));
        if (!b) return json(res, 404, { message: 'Borrower not found.' });
        const products = prequalify(b.creditScore);
        return json(res, 200, {
          creditScore: b.creditScore,
          prequalifiedProducts: products,
          resultText: 'Pre-qualified products: ' + products,
        });
      }
      if (req.method === 'GET' && path === '/api/borrowers') {
        const term = (url.searchParams.get('search') ?? '').trim().toUpperCase();
        const rows = BORROWERS.filter(
          (b) => b.legalName.toUpperCase().includes(term) || b.taxId.includes(term),
        )
          .sort((a, b) => (a.legalName < b.legalName ? -1 : 1))
          .map((b) => ({ ...b, activeLoans: activeLoanCount(b.borrowerId) }));
        return json(res, 200, rows);
      }
      if (req.method === 'POST' && path === '/api/pricing/rate') {
        const b = await readBody(req);
        return json(res, 200, priceRate(b.productType, b.creditScore, b.ltv, b.depositBalance));
      }
      if (req.method === 'POST' && path === '/api/pricing/origination-fee') {
        const b = await readBody(req);
        return json(res, 200, origFee(b.amount, b.productType));
      }
      if (req.method === 'POST' && path === '/api/amortization/payment') {
        const b = await readBody(req);
        return json(res, 200, { payment: monthlyPayment(b.principal, b.annualRatePct, b.termMonths) });
      }
      if (req.method === 'POST' && path === '/api/amortization/schedule') {
        const b = await readBody(req);
        return json(res, 200, buildSchedule(b.principal, b.annualRatePct, b.termMonths));
      }
      if (req.method === 'POST' && path === '/api/pricing/quote') {
        const b = await readBody(req);
        const rate = priceRate(b.productType, b.creditScore, b.ltv, b.depositBalance);
        const fee = origFee(b.amount, b.productType);
        const sched = buildSchedule(b.amount, rate.rate, b.termMonths);
        return json(res, 200, {
          ...rate,
          ...fee,
          payment: sched.payment,
          rows: sched.rows,
          ruleIds: ['BR-PRC-001', 'BR-PRC-002', 'BR-PRC-003', 'BR-PRC-004', 'BR-PRC-005', 'BR-PRC-006', 'BR-AMT-001', 'BR-AMT-002'],
        });
      }
      if (req.method === 'POST' && path === '/api/servicing/late-fee') {
        const b = await readBody(req);
        return json(res, 200, { fee: calcLateFee(b.paymentAmount, b.daysLate) });
      }
      m = path.match(/^\/api\/loans\/(\d+)\/payoff$/);
      if (req.method === 'GET' && m) {
        const q = payoff(Number(m[1]), url.searchParams.get('asOf') ?? new Date().toISOString().slice(0, 10));
        if (!q) return json(res, 404, { message: 'ORA-01403: no data found' });
        return json(res, 200, q);
      }
      m = path.match(/^\/api\/loans\/(\d+)\/schedule$/);
      if (req.method === 'GET' && m) {
        return json(res, 200, scheduleForLoan(Number(m[1])) ?? []);
      }
      m = path.match(/^\/api\/loans\/(\d+)\/schedule\.csv$/);
      if (req.method === 'GET' && m) {
        const rows = scheduleForLoan(Number(m[1])) ?? [];
        const mdy = (iso) => {
          const [y, mo, d] = iso.split('-').map(Number);
          return `${mo}/${d}/${y}`;
        };
        const lines = ['PERIOD_NO,DUE_DATE,PAYMENT_AMT,INTEREST_AMT,PRINCIPAL_AMT,BALANCE_AFTER'].concat(
          rows.map((r) => [r.periodNo, mdy(r.dueDate), r.paymentAmt, r.interestAmt, r.principalAmt, r.balanceAfter].join(',')),
        );
        const csv = lines.join('\r\n') + '\r\n';
        res.writeHead(200, {
          'Content-Type': 'text/csv',
          'Access-Control-Allow-Origin': '*',
        });
        return res.end(csv);
      }
      return json(res, 404, { message: 'Not found: ' + path });
    } catch (e) {
      if (e instanceof UnknownProductError) return json(res, 400, { message: e.message });
      return json(res, e.httpStatus ?? 500, { message: e.message });
    }
  })
  .listen(PORT, () => console.log(`[mock-api] service-contract mock listening on http://localhost:${PORT}`));
