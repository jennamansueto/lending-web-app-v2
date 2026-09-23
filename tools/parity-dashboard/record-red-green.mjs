/**
 * Records the red -> green parity moment in one browser session:
 *
 *   1. breaks BR-SVC-001 locally (the late-fee cap in Contoso.Lending.Domain),
 *   2. runs the suite from the dashboard and expands the failing rule group and record,
 *   3. restores the domain file,
 *   4. runs again from the dashboard and shows every level green.
 *
 * The break is applied and reverted by this script only; nothing is committed. Artefacts land in
 * parity/reports/dashboard-run/.
 *
 * Usage: node tools/parity-dashboard/record-red-green.mjs [--url http://localhost:5090/]
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');
const outDir = path.join(repoRoot, 'parity', 'reports', 'dashboard-run');
const videoDir = path.join(outDir, 'video');

const url = process.argv.includes('--url') ? process.argv[process.argv.indexOf('--url') + 1] : 'http://localhost:5090/';

const enginePath = path.join(repoRoot, 'src', 'Contoso.Lending.Domain', 'ServicingEngine.cs');
const original = fs.readFileSync(enginePath, 'utf8');
const CAP_GOOD = 'else if (fee > 150m) fee = 150m;';
const CAP_BROKEN = 'else if (fee > 175m) fee = 175m;';

function writeEngine(text) {
  fs.writeFileSync(enginePath, text);
}

function breakRule() {
  if (!original.includes(CAP_GOOD)) throw new Error(`late-fee cap not found in ${enginePath}`);
  writeEngine(original.replace(CAP_GOOD, CAP_BROKEN));
  console.log(`broke BR-SVC-001 locally: ${CAP_GOOD} -> ${CAP_BROKEN}`);
}

function restoreRule() {
  writeEngine(original);
  console.log('restored ServicingEngine.cs');
}

fs.mkdirSync(outDir, { recursive: true });
fs.rmSync(videoDir, { recursive: true, force: true });

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1440, height: 900 },
  recordVideo: { dir: videoDir, size: { width: 1440, height: 900 } },
});
const page = await context.newPage();

async function shot(name) {
  const file = path.join(outDir, `${name}.png`);
  await page.screenshot({ path: file });
  console.log(`screenshot ${path.relative(repoRoot, file)}`);
}

/** Presses "Run parity checks" and waits for the run to finish, returning its overall status. */
async function runFromDashboard() {
  await page.click('#run');
  await page.waitForFunction(() => document.getElementById('run').disabled, null, { timeout: 60_000 });
  await page.waitForFunction(() => !document.getElementById('run').disabled, null, { timeout: 45 * 60_000 });
  await page.waitForTimeout(2500);
  const text = (await page.locator('#run-state').textContent()).trim();
  return /fail/i.test(text) ? 'fail' : 'pass';
}

async function expandRule(ruleId) {
  const toggle = page.locator(`.group-toggle:has-text("${ruleId}")`).first();
  await toggle.scrollIntoViewIfNeeded();
  await page.waitForTimeout(600);
  await toggle.click();
  await page.waitForTimeout(2000);
}

try {
  breakRule();

  await page.goto(url, { waitUntil: 'load' });
  await page.waitForTimeout(2500);
  await shot('01-idle');

  console.log('run 1 — expecting red');
  await page.waitForTimeout(1000);
  const red = await runFromDashboard();
  await shot('02-red-summary');
  await expandRule('BR-SVC-001');
  await shot('03-red-rule-group');
  const failing = page.locator('.detail tr.row-fail').first();
  await failing.scrollIntoViewIfNeeded();
  await page.waitForTimeout(2500);
  await shot('04-red-failing-record');
  console.log(`run 1 state: ${red}`);

  restoreRule();
  await page.waitForTimeout(2000);

  console.log('run 2 — expecting green');
  const green = await runFromDashboard();
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(2000);
  await shot('05-green-summary');
  await expandRule('BR-SVC-001');
  await page.waitForTimeout(2000);
  await shot('06-green-rule-group');
  console.log(`run 2 state: ${green}`);

  if (red !== 'fail' || green !== 'pass') {
    throw new Error(`expected Fail then Pass, got ${red} then ${green}`);
  }
} finally {
  restoreRule();
  await context.close();
  await browser.close();
  const video = fs.readdirSync(videoDir).find((name) => name.endsWith('.webm'));
  if (video) {
    const target = path.join(outDir, 'dashboard-red-green.webm');
    fs.renameSync(path.join(videoDir, video), target);
    fs.rmSync(videoDir, { recursive: true, force: true });
    console.log(`video ${path.relative(repoRoot, target)}`);
  }
}
