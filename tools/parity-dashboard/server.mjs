/**
 * Live parity dashboard — backend.
 *
 * It owns no parity logic of its own: it shells out to `bash parity/run-all.sh`, follows the
 * `##PARITY` progress markers that script prints, and serves the structured reports the parity
 * scripts write under parity/reports/ (plus the golden files they are compared against).
 *
 *   POST /api/run          start a run (409 while one is in flight)
 *   GET  /api/stream       server-sent events: state, log, done
 *   GET  /api/state        the current run state
 *   GET  /api/results      the last structured results of every level
 *   GET  /api/golden/:file a golden file, so a failing record can be shown in context
 *
 * Usage: node tools/parity-dashboard/server.mjs [--port 5090]
 */
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');
const publicDir = path.join(here, 'public');
const reportsDir = path.join(repoRoot, 'parity', 'reports');
const goldenDir = path.join(repoRoot, 'parity', 'golden');

const portFlag = process.argv.indexOf('--port');
const port = Number(portFlag > -1 ? process.argv[portFlag + 1] : process.env.PORT ?? 5090);

/** The four levels, in display order, with the report each one writes. */
const LEVELS = [
  { id: 'l1', name: 'Layer 1 — UI', unit: 'checks', script: 'parity/run-l1.sh', report: 'l1-ui.json' },
  { id: 'l2', name: 'Layer 2 — Service', unit: 'tests', script: 'parity/run-l2.sh', report: 'l2-service.json' },
  { id: 'l3', name: 'Layer 3 — Data', unit: 'checksums', script: 'parity/run-l3.sh', report: 'data-parity.json' },
  { id: 'l4', name: 'Layer 4 — Workflow', unit: 'scenarios', script: 'parity/run-l4.sh', report: 'l4-e2e.json' },
];

const clients = new Set();
let child = null;
let state = lastRecordedState();

function freshState(status) {
  return {
    status, // idle | running | pass | fail
    runId: null,
    startedAt: null,
    finishedAt: null,
    levels: Object.fromEntries(
      LEVELS.map((level) => [
        level.id,
        { ...level, status: 'pending', totals: null, startedAt: null, finishedAt: null },
      ])
    ),
  };
}

/** The page opens on the last recorded run rather than an empty board. */
function lastRecordedState() {
  const initial = freshState('idle');
  const aggregate = readJson(path.join(reportsDir, 'parity-dashboard.json'));
  if (!aggregate) return initial;
  initial.status = aggregate.status;
  initial.finishedAt = aggregate.generatedAt;
  for (const level of aggregate.levels ?? []) {
    if (!initial.levels[level.id]) continue;
    initial.levels[level.id].status = level.status;
    initial.levels[level.id].totals = level.totals;
  }
  return initial;
}

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

/** Totals as the level's own report states them; nothing is recomputed here. */
function totalsOf(id, report) {
  if (!report) return null;
  if (id === 'l3') {
    return {
      total: report.checks_total,
      passed: report.checks_total - report.checks_failed,
      failed: report.checks_failed,
    };
  }
  return report.totals ?? null;
}

function results() {
  const levels = {};
  for (const level of LEVELS) {
    const report = readJson(path.join(reportsDir, level.report));
    levels[level.id] = { ...level, report, totals: totalsOf(level.id, report) };
  }
  return { aggregate: readJson(path.join(reportsDir, 'parity-dashboard.json')), levels };
}

function broadcast(event, data) {
  const payload = `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  for (const client of clients) client.write(payload);
}

function publishState() {
  broadcast('state', state);
}

/** `##PARITY run|level=... status=...` — the only channel between the script and the dashboard. */
function handleMarker(line) {
  const fields = Object.fromEntries(
    line
      .slice('##PARITY'.length)
      .trim()
      .split(/\s+/)
      .map((token) => (token.includes('=') ? token.split('=') : [token, true]))
  );
  const now = new Date().toISOString();
  if (fields.level) {
    const level = state.levels[fields.level];
    if (!level) return;
    level.status = fields.status;
    if (fields.status === 'running') {
      level.startedAt = now;
      level.totals = null;
    } else {
      level.finishedAt = now;
      level.totals = totalsOf(level.id, readJson(path.join(reportsDir, level.report)));
      broadcast('level', { id: level.id, ...results().levels[level.id] });
    }
  } else if (fields.status !== 'running') {
    state.status = fields.status;
    state.finishedAt = now;
  }
  publishState();
}

function startRun() {
  state = freshState('running');
  state.runId = `run-${Date.now()}`;
  state.startedAt = new Date().toISOString();
  publishState();
  broadcast('log', { line: `$ bash parity/run-all.sh` });

  child = spawn('bash', [path.join(repoRoot, 'parity', 'run-all.sh')], {
    cwd: repoRoot,
    env: { ...process.env, FORCE_COLOR: '0' },
  });

  let buffer = '';
  const onChunk = (chunk) => {
    buffer += chunk.toString();
    const lines = buffer.split('\n');
    buffer = lines.pop() ?? '';
    for (const line of lines) {
      if (line.startsWith('##PARITY')) handleMarker(line);
      else broadcast('log', { line });
    }
  };
  child.stdout.on('data', onChunk);
  child.stderr.on('data', onChunk);

  child.on('close', (code) => {
    child = null;
    if (state.status === 'running') {
      state.status = code === 0 ? 'pass' : 'fail';
      state.finishedAt = new Date().toISOString();
    }
    for (const level of Object.values(state.levels)) {
      if (level.status === 'running' || level.status === 'pending') {
        level.status = code === 0 ? level.status : 'fail';
      }
    }
    publishState();
    broadcast('done', { exitCode: code, results: results() });
  });

  return state.runId;
}

const CONTENT_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
};

function sendJson(response, status, body) {
  const text = JSON.stringify(body);
  response.writeHead(status, { 'content-type': 'application/json; charset=utf-8' });
  response.end(text);
}

function serveStatic(request, response) {
  const requested = request.url === '/' ? '/index.html' : request.url.split('?')[0];
  const file = path.join(publicDir, path.normalize(requested).replace(/^(\.\.[/\\])+/, ''));
  if (!file.startsWith(publicDir) || !fs.existsSync(file)) {
    response.writeHead(404, { 'content-type': 'text/plain' });
    response.end('not found');
    return;
  }
  response.writeHead(200, { 'content-type': CONTENT_TYPES[path.extname(file)] ?? 'application/octet-stream' });
  fs.createReadStream(file).pipe(response);
}

const server = http.createServer((request, response) => {
  const url = new URL(request.url, `http://localhost:${port}`);

  if (request.method === 'POST' && url.pathname === '/api/run') {
    if (child) {
      sendJson(response, 409, { error: 'a parity run is already in flight', runId: state.runId });
      return;
    }
    sendJson(response, 202, { runId: startRun() });
    return;
  }

  if (url.pathname === '/api/state') {
    sendJson(response, 200, state);
    return;
  }

  if (url.pathname === '/api/results') {
    sendJson(response, 200, results());
    return;
  }

  if (url.pathname.startsWith('/api/golden/')) {
    const name = path.basename(decodeURIComponent(url.pathname.slice('/api/golden/'.length)));
    const file = path.join(goldenDir, name);
    if (!name.endsWith('.json') || !fs.existsSync(file)) {
      sendJson(response, 404, { error: `no golden file named ${name}` });
      return;
    }
    response.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    fs.createReadStream(file).pipe(response);
    return;
  }

  if (url.pathname === '/api/stream') {
    response.writeHead(200, {
      'content-type': 'text/event-stream',
      'cache-control': 'no-cache',
      connection: 'keep-alive',
    });
    response.write('retry: 2000\n\n');
    clients.add(response);
    response.write(`event: state\ndata: ${JSON.stringify(state)}\n\n`);
    response.write(`event: results\ndata: ${JSON.stringify(results())}\n\n`);
    request.on('close', () => clients.delete(response));
    return;
  }

  serveStatic(request, response);
});

server.listen(port, () => {
  console.log(`parity dashboard on http://localhost:${port} (repo ${repoRoot})`);
});
