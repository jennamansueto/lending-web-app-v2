/**
 * Layer 1 (UI) parity harness.
 *
 * Drives the Angular shell in a real browser, screen by screen, and compares what the screens
 * render against the legacy behavioural contract:
 *
 *   golden   — the rendered value is compared to a record in parity/golden/ (exact equality)
 *   data     — the rendered value is compared to the Postgres system of record, whose equality
 *              with Oracle is proven by the layer-3 checksum verify
 *   service  — the rendered value is compared to the layer-2 response for the same request
 *              (used only where the legacy screen had no control to drive a golden input)
 *
 * No tolerance anywhere: numbers are compared as exact decimals (presentation-only characters —
 * the currency symbol, thousands separators and the percent suffix — are removed first), strings
 * verbatim. Writes parity/reports/l1-ui.json, per-screen screenshots and a video of the run.
 */
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');
const goldenDir = path.join(repoRoot, 'parity', 'golden');
const reportsDir = path.join(repoRoot, 'parity', 'reports');
const shotsDir = path.join(reportsDir, 'l1-screens');
const videoDir = path.join(reportsDir, 'l1-video');
const reportPath = path.join(reportsDir, 'l1-ui.json');

const uiUrl = (process.env.UI_URL ?? 'http://localhost:4200').replace(/\/$/, '');
const apiUrl = (process.env.SERVICE_API_URL ?? 'http://localhost:5080').replace(/\/$/, '');
const pace = Number(process.env.L1_PACE_MS ?? 350);

/** Reads golden JSON keeping every number as its literal source text, so no double ever appears. */
function readGolden(file) {
  const text = fs.readFileSync(path.join(goldenDir, file), 'utf8');
  return JSON.parse(text, function (key, value, context) {
    return typeof value === 'number' ? context.source : value;
  });
}

/** Exact decimal equality: sign + digits, presentation characters removed, no epsilon. */
function canonicalDecimal(value) {
  if (value === null || value === undefined) return null;
  const text = String(value).replace(/[$,%\s]/g, '');
  if (!/^[-+]?\d*(\.\d*)?$/.test(text) || text === '' || text === '.') return null;
  const negative = text.startsWith('-');
  let [whole, fraction = ''] = text.replace(/^[-+]/, '').split('.');
  whole = whole.replace(/^0+(?=\d)/, '') || '0';
  fraction = fraction.replace(/0+$/, '');
  const digits = fraction === '' ? whole : `${whole}.${fraction}`;
  return negative && digits !== '0' ? `-${digits}` : digits;
}

function numbersEqual(expected, actual) {
  const left = canonicalDecimal(expected);
  const right = canonicalDecimal(actual);
  return left !== null && right !== null && left === right;
}

const screens = [];
let current = null;

function screen(name, route) {
  current = { screen: name, route, screenshots: [], checks: [] };
  screens.push(current);
  return current;
}

function check(entry) {
  const pass =
    entry.compare === 'number'
      ? numbersEqual(entry.expected, entry.actual)
      : entry.expected === entry.actual;
  current.checks.push({ pass, ...entry });
  process.stdout.write(`  ${pass ? 'PASS' : 'FAIL'}  ${entry.id}\n`);
  if (!pass) {
    process.stdout.write(`        expected ${JSON.stringify(entry.expected)}\n`);
    process.stdout.write(`        actual   ${JSON.stringify(entry.actual)}\n`);
  }
  return pass;
}

function psql(sql) {
  const out = execFileSync(
    'docker',
    ['exec', '-i', 'lending-postgres', 'psql', '-U', 'lending', '-d', 'lending', '-tAF', '\u001f', '-c', sql],
    { encoding: 'utf8' }
  );
  return out
    .split('\n')
    .filter((line) => line !== '')
    .map((line) => line.split('\u001f'));
}

async function shot(page, name) {
  const file = path.join(shotsDir, `${name}.png`);
  await page.screenshot({ path: file, fullPage: true });
  current.screenshots.push(path.relative(repoRoot, file));
}

async function goto(page, route) {
  await page.goto(`${uiUrl}${route}`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(pace);
}

async function fillFields(page, values) {
  for (const [id, value] of Object.entries(values)) {
    await page.fill(`#${id}`, String(value));
    await page.waitForTimeout(40);
  }
}

function goldenRef(file, index) {
  return { file: `parity/golden/${file}`, index };
}

fs.rmSync(shotsDir, { recursive: true, force: true });
fs.rmSync(videoDir, { recursive: true, force: true });
fs.mkdirSync(shotsDir, { recursive: true });
fs.mkdirSync(videoDir, { recursive: true });

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1440, height: 900 },
  recordVideo: { dir: videoDir, size: { width: 1440, height: 900 } },
});
const page = await context.newPage();

// ---------------------------------------------------------------- shell ----
screen('Shell', '/');
await goto(page, '/');
await shot(page, 'shell-home');
check({
  id: 'shell/title',
  ruleId: 'BR-UI-001',
  source: 'legacy',
  description: 'Shell header keeps the legacy application title',
  expected: 'Contoso Bank — Commercial Lending Desk',
  actual: (await page.locator('.app-header h1').textContent())?.trim(),
});
check({
  id: 'shell/nav',
  ruleId: 'BR-UI-001',
  source: 'legacy',
  description: 'One navigation entry per legacy screen',
  expected: 'Home | New Loan Application | Pricing & Amortization | Borrower Lookup | Statements Export',
  actual: (await page.locator('.app-nav a').allTextContents()).map((t) => t.trim()).join(' | '),
});
check({
  id: 'shell/connection',
  ruleId: 'BR-UI-001',
  source: 'service',
  description: 'Status bar reports the service connection',
  expected: 'Connected: LENDING SERVICE (layer 2 /api)',
  actual: (await page.locator('.app-status span').first().textContent())?.trim(),
});

// ------------------------------------------------- new loan application ----
screen('New Loan Application', '/loan-application');
await goto(page, '/loan-application');

const eligibilityFiles = [
  'BR-ELG-001_amount_minimum.json',
  'BR-ELG-002_amount_maximum.json',
  'BR-ELG-003_term_limits.json',
  'BR-ELG-004_minimum_credit_score.json',
  'BR-ELG-005_dti_cap.json',
  'BR-ELG-006_collateral_required.json',
  'BR-ELG-007_ltv_cap.json',
  'BR-ELG-008_years_in_business.json',
  'BR-ELG-009_approval_result.json',
];

for (const file of eligibilityFiles) {
  const records = readGolden(file);
  const ruleId = records[0].ruleId;
  const index = records.findIndex((record) => record.expected.firedRuleId === ruleId);
  const record = records[index];
  const input = record.input;
  await page.selectOption('#product', input.productType);
  await fillFields(page, {
    borrowerId: '1',
    amount: input.amount,
    termMonths: input.termMonths,
    annualIncome: input.annualIncome,
    monthlyDebt: input.monthlyDebt,
    creditScore: input.creditScore,
    collateralValue: input.collateralValue,
    yearsInBusiness: input.yearsInBusiness,
  });
  await page.click('button:has-text("Check Eligibility")');
  await page.waitForTimeout(700);
  const resultText = await page.locator('.result-panel').first().textContent();
  check({
    id: `loan-application/${ruleId}/resultText`,
    ruleId,
    source: 'golden',
    description: `Result panel renders the legacy ${ruleId} result text verbatim`,
    input,
    expected: record.expected.resultText,
    actual: resultText,
    golden: goldenRef(file, index),
  });
  check({
    id: `loan-application/${ruleId}/firedRule`,
    ruleId,
    source: 'golden',
    description: `Screen reports ${ruleId} as the rule that fired`,
    input,
    expected: `Rule: ${record.expected.firedRuleId}`,
    actual: (await page.locator('.rule-trace').textContent())?.trim(),
    golden: goldenRef(file, index),
  });
  if (ruleId === 'BR-ELG-009') await shot(page, 'loan-application-approved');
  if (ruleId === 'BR-ELG-005') await shot(page, 'loan-application-declined');
}

// legacy client-side validation (the only rule the screen owns)
await fillFields(page, { amount: 'abc' });
await page.click('button:has-text("Check Eligibility")');
await page.waitForTimeout(500);
check({
  id: 'loan-application/invalid-numbers',
  ruleId: 'BR-UI-003',
  source: 'legacy',
  description: 'Non-numeric input raises the legacy message box text',
  expected: 'One or more fields contain invalid numbers.',
  actual: (await page.locator('.msgbox-text').textContent())?.trim(),
});
await shot(page, 'loan-application-invalid-numbers');
await page.click('button:has-text("OK")');

// ------------------------------------------------ pricing & amortization ----
screen('Pricing & Amortization', '/pricing');
await goto(page, '/pricing');

const pricedRates = readGolden('BR-PRC-005_priced_rate.json');
const fees = readGolden('BR-PRC-006_origination_fee.json');
const payments = readGolden('BR-AMT-001_monthly_payment.json');
const schedules = readGolden('BR-AMT-002_amortization_schedule.json');

const pricingCases = [
  { productType: 'TERM', creditScore: '760', ltv: '0.7', depositBalance: '0', amount: '100000', termMonths: '60' },
  { productType: 'TERM', creditScore: '760', ltv: '0.5', depositBalance: '0', amount: '50000', termMonths: '36' },
  { productType: 'EQUIP', creditScore: '760', ltv: '0.5', depositBalance: '300000', amount: '25000', termMonths: '12' },
];

for (const input of pricingCases) {
  const rateIndex = pricedRates.findIndex(
    (record) =>
      record.input.productType === input.productType &&
      numbersEqual(record.input.creditScore, input.creditScore) &&
      numbersEqual(record.input.ltv, input.ltv) &&
      numbersEqual(record.input.depositBalance, input.depositBalance)
  );
  const rateRecord = pricedRates[rateIndex];
  const feeIndex = fees.findIndex(
    (record) =>
      record.input.productType === input.productType && numbersEqual(record.input.amount, input.amount)
  );
  const feeRecord = fees[feeIndex];

  await page.selectOption('#product', input.productType);
  await fillFields(page, {
    amount: input.amount,
    termMonths: input.termMonths,
    creditScore: input.creditScore,
    ltv: input.ltv,
    depositBalance: input.depositBalance,
  });
  await page.click('button:has-text("Price Loan")');
  await page.waitForTimeout(800);

  const lines = await page.locator('.result-list .result-line').allTextContents();
  const rendered = Object.fromEntries(
    lines.map((line) => {
      const [label, value] = line.split(':');
      return [label.trim(), value.trim()];
    })
  );
  const label = `${input.productType}-${input.amount}-${input.termMonths}`;

  check({
    id: `pricing/${label}/rate`,
    ruleId: 'BR-PRC-005',
    source: 'golden',
    description: 'Quoted rate matches the legacy priced rate',
    input: rateRecord.input,
    expected: rateRecord.expected,
    actual: rendered['Rate'],
    compare: 'number',
    golden: goldenRef('BR-PRC-005_priced_rate.json', rateIndex),
  });
  if (feeIndex >= 0) {
    check({
      id: `pricing/${label}/originationFee`,
      ruleId: 'BR-PRC-006',
      source: 'golden',
      description: 'Origination fee matches the legacy fee',
      input: feeRecord.input,
      expected: feeRecord.expected,
      actual: rendered['Origination fee'],
      compare: 'number',
      golden: goldenRef('BR-PRC-006_origination_fee.json', feeIndex),
    });
  }

  const paymentIndex = payments.findIndex(
    (record) =>
      numbersEqual(record.input.principal, input.amount) &&
      numbersEqual(record.input.annualRatePct, rateRecord.expected) &&
      numbersEqual(record.input.termMonths, input.termMonths)
  );
  if (paymentIndex >= 0) {
    check({
      id: `pricing/${label}/monthlyPayment`,
      ruleId: 'BR-AMT-001',
      source: 'golden',
      description: 'Monthly payment matches the legacy annuity payment',
      input: payments[paymentIndex].input,
      expected: payments[paymentIndex].expected,
      actual: rendered['Monthly payment'],
      compare: 'number',
      golden: goldenRef('BR-AMT-001_monthly_payment.json', paymentIndex),
    });
  }

  const scheduleIndex = schedules.findIndex(
    (record) =>
      numbersEqual(record.input.principal, input.amount) &&
      numbersEqual(record.input.annualRatePct, rateRecord.expected) &&
      numbersEqual(record.input.termMonths, input.termMonths)
  );
  if (scheduleIndex >= 0) {
    const expectedRows = schedules[scheduleIndex].expected;
    const grid = await page.locator('.grid tbody tr').evaluateAll((rows) =>
      rows.map((row) => Array.from(row.querySelectorAll('td')).map((cell) => cell.textContent.trim()))
    );
    const mismatch = [];
    if (grid.length !== expectedRows.length) {
      mismatch.push(`row count: expected ${expectedRows.length}, rendered ${grid.length}`);
    } else {
      expectedRows.forEach((expectedRow, rowIndex) => {
        const cells = grid[rowIndex];
        const columns = ['Period', 'Payment', 'Interest', 'Principal', 'Balance'];
        columns.forEach((column, columnIndex) => {
          if (!numbersEqual(expectedRow[column], cells[columnIndex])) {
            mismatch.push(
              `period ${expectedRow.Period} ${column}: expected ${expectedRow[column]}, rendered ${cells[columnIndex]}`
            );
          }
        });
      });
    }
    check({
      id: `pricing/${label}/schedule`,
      ruleId: 'BR-AMT-002',
      source: 'golden',
      description: `Amortization grid renders all ${expectedRows.length} legacy schedule rows`,
      input: schedules[scheduleIndex].input,
      expected: `${expectedRows.length} rows identical to the golden schedule`,
      actual: mismatch.length === 0 ? `${grid.length} rows identical to the golden schedule` : mismatch.join('; '),
      golden: goldenRef('BR-AMT-002_amortization_schedule.json', scheduleIndex),
    });
  }
  if (input.amount === '100000') await shot(page, 'pricing-quote');
}

// Late fee (LEND-5102: the legacy button sends the Loan Amount field as the payment amount).
const lateFees = readGolden('BR-SVC-001_late_fee.json');
for (const [paymentAmount, daysLate] of [
  ['1000.1', '11'],
  ['3000', '10'],
  ['10000', '30'],
  ['0', '90'],
]) {
  const index = lateFees.findIndex(
    (record) =>
      numbersEqual(record.input.paymentAmount, paymentAmount) && numbersEqual(record.input.daysLate, daysLate)
  );
  const record = lateFees[index];
  await fillFields(page, { amount: paymentAmount, daysLate });
  await page.click('button:has-text("Late Fee")');
  await page.waitForTimeout(600);
  const text = (await page.locator('.late-fee .result-line').textContent()) ?? '';
  check({
    id: `pricing/late-fee/${paymentAmount}-${daysLate}`,
    ruleId: 'BR-SVC-001',
    source: 'golden',
    description: 'Late fee (DB button) matches the legacy PL/SQL fee',
    input: record.input,
    expected: record.expected.lateFee,
    actual: text.replace('Late fee:', '').trim(),
    compare: 'number',
    golden: goldenRef('BR-SVC-001_late_fee.json', index),
  });
}
await shot(page, 'pricing-late-fee');

// ------------------------------------------------------- borrower lookup ----
screen('Borrower Lookup', '/borrower-lookup');
await goto(page, '/borrower-lookup');

const prequalifications = readGolden('BR-PQL-001_prequalification_hint.json');
await page.fill('#search', 'CO');
await page.click('button:has-text("Search")');
await page.waitForTimeout(800);

const expectedBorrowers = psql(
  "select borrower_id, legal_name, tax_id, credit_score, deposit_balance, years_in_business " +
    "from borrower where legal_name ilike '%CO%' or tax_id ilike '%CO%' order by legal_name"
);
const renderedBorrowers = await page.locator('.grid tbody tr').evaluateAll((rows) =>
  rows.map((row) => Array.from(row.querySelectorAll('td')).map((cell) => cell.textContent.trim()))
);
check({
  id: 'borrower-lookup/grid',
  ruleId: 'BR-UI-005',
  source: 'data',
  description: 'Search grid renders the system-of-record rows, ordered by legal name',
  expected: expectedBorrowers.map((row) => row.join(' | ')).join(' / '),
  actual: renderedBorrowers.map((row) => row.slice(0, 6).join(' | ')).join(' / '),
});
await shot(page, 'borrower-lookup-search');

for (const borrowerId of ['1', '2', '4']) {
  const [[, creditScore]] = psql(`select borrower_id, credit_score from borrower where borrower_id = ${borrowerId}`);
  const index = prequalifications.findIndex((record) => numbersEqual(record.input.creditScore, creditScore));
  await page.fill('#search', borrowerId === '1' ? 'ACME' : borrowerId === '2' ? 'BLUE HARBOR' : 'DELTA');
  await page.click('button:has-text("Search")');
  await page.waitForTimeout(700);
  await page.locator(`.grid tbody tr:has(td:text-is("${borrowerId}"))`).first().click();
  await page.waitForTimeout(600);
  const actual = (await page.locator('.result-panel').textContent())?.trim();
  if (index >= 0) {
    check({
      id: `borrower-lookup/prequalification/${borrowerId}`,
      ruleId: 'BR-PQL-001',
      source: 'golden',
      description: `Pre-qualification hint for credit score ${creditScore}`,
      input: prequalifications[index].input,
      expected: prequalifications[index].expected.labelText,
      actual,
      golden: goldenRef('BR-PQL-001_prequalification_hint.json', index),
    });
  } else {
    const response = await (await fetch(`${apiUrl}/api/borrowers/${borrowerId}/prequalification`)).json();
    check({
      id: `borrower-lookup/prequalification/${borrowerId}`,
      ruleId: 'BR-PQL-001',
      source: 'service',
      description: `Pre-qualification hint for credit score ${creditScore} (no golden record at this score)`,
      expected: response.resultText,
      actual,
    });
  }
}
await shot(page, 'borrower-lookup-prequalification');

// ------------------------------------------------------ statements export ----
screen('Statements Export', '/statements');
await goto(page, '/statements');
const loanId = process.env.L1_LOAN_ID ?? '1';
await page.fill('#loanId', loanId);
await page.click('button:has-text("Load Schedule")');
await page.waitForTimeout(1200);

const expectedSchedule = psql(
  "select period_no, to_char(due_date, 'YYYY-MM-DD'), payment_amt, interest_amt, principal_amt, balance_after " +
    `from payment_schedule where loan_id = ${loanId} order by period_no`
);
const renderedSchedule = await page.locator('.grid tbody tr').evaluateAll((rows) =>
  rows.map((row) => Array.from(row.querySelectorAll('td')).map((cell) => cell.textContent.trim()))
);
check({
  id: 'statements/schedule',
  ruleId: 'BR-UI-007',
  source: 'data',
  description: `Schedule grid renders every PAYMENT_SCHEDULE row of loan ${loanId} verbatim`,
  expected: `${expectedSchedule.length} rows: ${expectedSchedule.map((row) => row.join('|')).join(' / ')}`,
  actual: `${renderedSchedule.length} rows: ${renderedSchedule.map((row) => row.join('|')).join(' / ')}`,
});
await shot(page, 'statements-schedule');

const [download] = await Promise.all([
  page.waitForEvent('download'),
  page.click('button:has-text("Export CSV")'),
]);
const csvPath = path.join(reportsDir, `l1-${await download.suggestedFilename()}`);
await download.saveAs(csvPath);
const serviceCsv = await (await fetch(`${apiUrl}/api/loans/${loanId}/schedule.csv`)).text();
check({
  id: 'statements/csv',
  ruleId: 'BR-UI-008',
  source: 'service',
  description: 'Export CSV downloads the service CSV byte-for-byte',
  expected: `${serviceCsv.length} bytes, header ${JSON.stringify(serviceCsv.split('\r\n')[0])}`,
  actual: (() => {
    const downloaded = fs.readFileSync(csvPath, 'utf8');
    return downloaded === serviceCsv
      ? `${downloaded.length} bytes, header ${JSON.stringify(downloaded.split('\r\n')[0])}`
      : `differs from the service CSV (${downloaded.length} bytes)`;
  })(),
});

await page.click('button:has-text("Payoff Quote")');
await page.waitForTimeout(900);
const payoff = await (await fetch(`${apiUrl}/api/loans/${loanId}/payoff`)).json();
check({
  id: 'statements/payoff',
  ruleId: 'BR-SVC-002',
  source: 'service',
  description:
    'Payoff line renders the service payoff for today (the legacy form had no as-of control, so the ' +
    'historical golden as-of dates are not drivable from the UI)',
  expected: payoff.payoff,
  actual: ((await page.locator('.panel:has-text("Payoff") .result-line').last().textContent()) ?? '')
    .split(':')
    .pop()
    ?.trim(),
  compare: 'number',
});
await shot(page, 'statements-payoff');

await context.close();
await browser.close();

const videos = fs
  .readdirSync(videoDir)
  .filter((name) => name.endsWith('.webm'))
  .map((name) => path.relative(repoRoot, path.join(videoDir, name)));

for (const entry of screens) {
  const failed = entry.checks.filter((item) => !item.pass).length;
  entry.totals = { total: entry.checks.length, passed: entry.checks.length - failed, failed };
}
const total = screens.reduce((sum, entry) => sum + entry.totals.total, 0);
const failed = screens.reduce((sum, entry) => sum + entry.totals.failed, 0);

const report = {
  layer: 'l1-ui',
  ui: uiUrl,
  api: apiUrl,
  video: videos,
  screens,
  totals: { total, passed: total - failed, failed },
};
fs.mkdirSync(reportsDir, { recursive: true });
fs.writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`);

console.log('');
console.log('Layer 1 parity — UI screens');
console.log('%s %s %s %s', 'SCREEN'.padEnd(26), 'TOTAL'.padStart(7), 'PASS'.padStart(7), 'FAIL'.padStart(7));
for (const entry of screens) {
  console.log(
    '%s %s %s %s',
    entry.screen.padEnd(26),
    String(entry.totals.total).padStart(7),
    String(entry.totals.passed).padStart(7),
    String(entry.totals.failed).padStart(7)
  );
}
console.log('%s %s %s %s', 'ALL CHECKS'.padEnd(26), String(total).padStart(7), String(total - failed).padStart(7), String(failed).padStart(7));
console.log(`report: ${reportPath}`);
console.log('');
process.exit(failed === 0 ? 0 : 1);
