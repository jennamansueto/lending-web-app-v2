'use strict';

const LEVELS = [
  { id: 'L2', scope: 'Service — golden corpus parity' },
  { id: 'L3', scope: 'Data — Oracle to Postgres verification' },
  { id: 'L4', scope: 'End to end — workflow scenarios' },
];

const state = new Map(LEVELS.map((l) => [l.id, { status: 'pending', cases: '—' }]));

const el = {
  run: document.getElementById('run'),
  meta: document.getElementById('run-meta'),
  summary: document.getElementById('level-summary'),
  log: document.getElementById('log'),
  l2: document.getElementById('l2-groups'),
  l3: document.getElementById('l3-rows'),
  l4: document.getElementById('l4-scenarios'),
};

const label = (s) => ({ pass: 'Pass', fail: 'Fail', running: 'Running…', pending: 'Pending' }[s] || s);

function renderSummary() {
  el.summary.innerHTML = '';
  for (const level of LEVELS) {
    const s = state.get(level.id);
    const tr = document.createElement('tr');
    tr.innerHTML =
      `<td class="mono">${level.id}</td>` +
      `<td>${level.scope}</td>` +
      `<td class="num">${s.cases}</td>` +
      `<td class="status status-${s.status}">${label(s.status)}</td>`;
    el.summary.appendChild(tr);
  }
}

function setLevel(id, patch) {
  if (!state.has(id)) return;
  Object.assign(state.get(id), patch);
  renderSummary();
}

function appendLog(line) {
  el.log.textContent += `${line}\n`;
  el.log.scrollTop = el.log.scrollHeight;
}

function pretty(value) {
  if (value === null || value === undefined) return 'null';
  return typeof value === 'string' ? value : JSON.stringify(value, null, 2);
}

function renderFailure(goldenFile, failure) {
  const div = document.createElement('div');
  div.className = 'failure';
  const where = failure.recordIndex === undefined ? '' : ` — record index ${failure.recordIndex}`;
  div.innerHTML =
    `<h4>Failing golden record${where}</h4>` +
    `<div class="kv"><span>File</span><span class="mono">${goldenFile || ''}</span>` +
    `<span>Input</span><pre>${pretty(failure.input)}</pre>` +
    `<span>Expected</span><pre>${pretty(failure.expected)}</pre>` +
    `<span>Actual</span><pre>${pretty(failure.actual)}</pre></div>`;
  return div;
}

function renderL2(level) {
  el.l2.innerHTML = '';
  for (const g of level.groups || []) {
    const d = document.createElement('details');
    d.className = 'group';
    d.dataset.status = g.status;
    if (g.status === 'fail') d.open = true;
    const summary = document.createElement('summary');
    summary.innerHTML =
      `<span class="rule-id">${g.ruleId}</span>` +
      `<span>${g.title || ''}</span>` +
      `<span class="cases">${g.cases} cases, ${g.failed ? `${g.failed} failing` : 'all passing'}</span>` +
      `<span class="status status-${g.status}">${label(g.status)}</span>`;
    d.appendChild(summary);
    const body = document.createElement('div');
    body.className = 'group-body';
    body.innerHTML = `<div class="file">${g.goldenFile || ''}</div>`;
    for (const f of g.failures || []) body.appendChild(renderFailure(g.goldenFile, f));
    d.appendChild(body);
    el.l2.appendChild(d);
  }
  if (!el.l2.children.length) el.l2.innerHTML = '<p class="placeholder">No groups reported.</p>';
}

function renderL3(level) {
  const rows = level.rows || [];
  if (!rows.length) { el.l3.innerHTML = '<p class="placeholder">No checksum rows reported.</p>'; return; }
  el.l3.innerHTML =
    '<table><thead><tr><th>Table</th><th>Metric</th><th>Oracle</th><th>Postgres</th><th>Result</th></tr></thead><tbody>' +
    rows.map((r) =>
      `<tr><td class="mono">${r.table}</td><td class="mono">${r.metric}</td>` +
      `<td class="num">${r.oracle}</td><td class="num">${r.postgres}</td>` +
      `<td class="status status-${r.status}">${label(r.status)}</td></tr>`).join('') +
    '</tbody></table>';
}

function renderL4(level) {
  const scenarios = level.scenarios || [];
  if (!scenarios.length) { el.l4.innerHTML = '<p class="placeholder">No scenarios reported.</p>'; return; }
  el.l4.innerHTML = scenarios.map((s) =>
    `<h3 class="mono">${s.name} <span class="status status-${s.status}">${label(s.status)}</span></h3>` +
    '<table><thead><tr><th>Asserted field</th><th>Expected</th><th>Actual</th><th>Result</th></tr></thead><tbody>' +
    (s.assertions || []).map((a) =>
      `<tr><td class="mono">${a.field}</td><td class="mono">${escapeHtml(a.expected)}</td>` +
      `<td class="mono">${escapeHtml(a.actual)}</td>` +
      `<td class="status status-${a.status}">${label(a.status)}</td></tr>`).join('') +
    '</tbody></table>').join('');
}

function escapeHtml(v) {
  return String(v === undefined || v === null ? '' : v)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/\n/g, '<br>');
}

function applyResults(results) {
  if (!results) return;
  for (const level of results.levels || []) {
    const cases = level.summary
      ? (level.summary.cases ?? level.summary.rows ?? level.summary.scenarios ?? '—')
      : '—';
    setLevel(level.id, { status: level.status, cases });
    if (level.id === 'L2') renderL2(level);
    if (level.id === 'L3') renderL3(level);
    if (level.id === 'L4') renderL4(level);
  }
  if (results.finishedAt) {
    el.meta.textContent = `${results.ok ? 'All levels passing' : 'Parity failure'} — last run ${results.finishedAt}`;
  }
}

function connect() {
  const source = new EventSource('/api/events');
  source.onmessage = (event) => {
    const evt = JSON.parse(event.data);
    switch (evt.type) {
      case 'run-started':
        el.run.disabled = true;
        el.log.textContent = '';
        el.meta.textContent = `run started ${evt.startedAt}`;
        for (const id of evt.levels || []) setLevel(id, { status: 'pending', cases: '—' });
        for (const pane of [el.l2, el.l3, el.l4]) {
          pane.innerHTML = '<p class="placeholder">Waiting for results of this run.</p>';
        }
        break;
      case 'level':
        setLevel(evt.id, { status: evt.status });
        break;
      case 'log':
        appendLog(evt.line);
        break;
      case 'run-finished':
        el.run.disabled = false;
        applyResults(evt.results);
        appendLog(`run finished with exit code ${evt.exitCode} in ${(evt.durationMs / 1000).toFixed(1)}s`);
        break;
    }
  };
  source.onerror = () => { el.run.disabled = false; };
}

el.run.addEventListener('click', async () => {
  el.run.disabled = true;
  const response = await fetch('/api/run', { method: 'POST' });
  if (!response.ok) { el.run.disabled = false; appendLog('a run is already in progress'); }
});

renderSummary();
fetch('/api/results').then((r) => (r.status === 204 ? null : r.json())).then(applyResults).catch(() => {});
connect();
