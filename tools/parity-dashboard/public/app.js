/**
 * Live parity dashboard — frontend.
 *
 * Renders what the backend streams: level state from the ##PARITY markers of parity/run-all.sh,
 * and the structured reports the parity scripts wrote. No comparison is performed here; a
 * pass/fail shown on this page is the pass/fail recorded in the report.
 */
const LEVEL_ORDER = ['l1', 'l2', 'l3', 'l4'];
const STATUS_TEXT = { pending: 'Pending', running: 'Running', pass: 'Pass', fail: 'Fail' };

const runButton = document.getElementById('run');
const runState = document.getElementById('run-state');
const summaryBody = document.getElementById('summary-body');
const summaryFoot = document.getElementById('summary-foot');
const levelsRoot = document.getElementById('levels');
const logPane = document.getElementById('log');

let state = null;
let results = null;
const expanded = new Set();

const DESCRIPTIONS = {
  l1: 'Angular shell and remotes driven in a real browser; every rendered value compared to the golden corpus, the service and the system of record.',
  l2: 'The 772-record golden corpus replayed against the domain, grouped by business rule.',
  l3: 'Fresh Oracle to Postgres migration, then row counts, numeric sums and table checksums per table.',
  l4: 'Temporal origination workflow scenarios run end to end against the live stack.',
};

function el(tag, attributes = {}, children = []) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attributes)) {
    if (value === null || value === undefined) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else node.setAttribute(key, String(value));
  }
  for (const child of [].concat(children)) {
    if (child === null || child === undefined) continue;
    node.append(typeof child === 'string' ? document.createTextNode(child) : child);
  }
  return node;
}

function statusCell(status) {
  return el('td', { class: 'status-col' }, [
    el('span', { class: `status ${status}`, text: STATUS_TEXT[status] ?? status }),
  ]);
}

function show(value) {
  if (value === null || value === undefined) return '—';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
}

function goldenLink(file, index, label) {
  if (!file) return null;
  const name = file.split('/').pop();
  return el('a', {
    href: `/api/golden/${name}`,
    target: '_blank',
    title: `${file}[${index}]`,
    text: label ?? `${file}[${index}]`,
  });
}

function table(headers, rows, options = {}) {
  const head = el(
    'thead',
    {},
    el(
      'tr',
      {},
      headers.map((header) =>
        el('th', { scope: 'col', class: header.numeric ? 'numeric' : header.status ? 'status-col' : null }, header.label)
      )
    )
  );
  return el('table', options, [head, el('tbody', {}, rows)]);
}

/** An expandable group row plus its (hidden until expanded) detail row. */
function group(key, summaryCells, buildDetail) {
  const open = expanded.has(key);
  const toggle = el('button', {
    type: 'button',
    class: 'group-toggle',
    'aria-expanded': String(open),
    text: summaryCells.title,
  });
  toggle.addEventListener('click', () => {
    if (expanded.has(key)) expanded.delete(key);
    else expanded.add(key);
    renderLevels();
  });
  const rows = [
    el('tr', { class: summaryCells.failed ? 'row-fail' : null }, [
      el('td', {}, [toggle]),
      ...summaryCells.cells,
      statusCell(summaryCells.status),
    ]),
  ];
  if (open) {
    rows.push(
      el('tr', { class: 'detail' }, el('td', { colspan: String(summaryCells.cells.length + 2) }, buildDetail()))
    );
  }
  return rows;
}

function levelCard(id, body, meta) {
  const level = state?.levels?.[id] ?? { name: id, status: 'pending' };
  return el('article', { class: 'level' }, [
    el('header', {}, [
      el('div', {}, [el('h2', { text: level.name }), el('p', { class: 'meta', text: DESCRIPTIONS[id] })]),
      el('div', { class: 'actions' }, [
        el('span', { class: `status ${level.status}`, text: STATUS_TEXT[level.status] ?? level.status }),
        el('p', { class: 'meta', text: meta ?? '' }),
      ]),
    ]),
    body,
  ]);
}

// ------------------------------------------------------------------ level 1 --
function renderL1(report) {
  if (!report) return el('p', { class: 'empty', text: 'No layer 1 report yet.' });
  const rows = [];
  for (const screen of report.screens) {
    const failed = screen.totals.failed;
    rows.push(
      ...group(
        `l1:${screen.screen}`,
        {
          title: `${screen.screen} — ${screen.totals.total} checks, ${failed ? `${failed} failing` : 'all passing'}`,
          cells: [
            el('td', { class: 'mono', text: screen.route }),
            el('td', { class: 'numeric', text: String(screen.totals.passed) }),
            el('td', { class: 'numeric', text: String(failed) }),
          ],
          failed,
          status: failed ? 'fail' : 'pass',
        },
        () =>
          table(
            [
              { label: 'Check' },
              { label: 'Rule' },
              { label: 'Compared with' },
              { label: 'Expected' },
              { label: 'Actual' },
              { label: 'Status', status: true },
            ],
            screen.checks.map((check) =>
              el('tr', { class: check.pass ? null : 'row-fail' }, [
                el('td', {}, [el('div', { class: 'mono', text: check.id }), el('div', { class: 'meta', text: check.description })]),
                el('td', { class: 'mono' }, [
                  check.golden
                    ? goldenLink(check.golden.file, check.golden.index, `${check.ruleId} #${check.golden.index}`)
                    : document.createTextNode(check.ruleId),
                ]),
                el('td', { text: check.source }),
                el('td', { class: 'mono wrap', text: show(check.expected) }),
                el('td', { class: 'mono wrap', text: show(check.actual) }),
                statusCell(check.pass ? 'pass' : 'fail'),
              ])
            )
          )
      )
    );
  }
  return table(
    [
      { label: 'Screen' },
      { label: 'Route' },
      { label: 'Passed', numeric: true },
      { label: 'Failed', numeric: true },
      { label: 'Status', status: true },
    ],
    rows
  );
}

// ------------------------------------------------------------------ level 2 --
const goldenCache = new Map();

async function loadGolden(file) {
  if (!goldenCache.has(file)) {
    const response = await fetch(`/api/golden/${file.split('/').pop()}`);
    goldenCache.set(file, response.ok ? await response.json() : []);
  }
  return goldenCache.get(file);
}

/** The assertion text without the xunit stack trace. */
function assertionOf(message) {
  return (message ?? '').split(/\n\s*at /)[0].trim();
}

function ruleLabel(golden) {
  if (!golden) return '';
  return golden
    .split('/')
    .pop()
    .replace(/\.json$/, '')
    .split('_')
    .slice(1)
    .join(' ');
}

function renderL2(report) {
  if (!report) return el('p', { class: 'empty', text: 'No layer 2 report yet.' });
  const rows = [];
  for (const [ruleId, rule] of Object.entries(report.rules)) {
    const failures = new Map((rule.failures ?? []).map((failure) => [failure.index, failure]));
    rows.push(
      ...group(
        `l2:${ruleId}`,
        {
          title: `${ruleId} ${ruleLabel(rule.golden)} — ${rule.total} cases, ${
            rule.failed ? `${rule.failed} failing` : 'all passing'
          }`,
          cells: [
            el('td', { class: 'mono' }, [rule.golden ? el('a', { href: `/api/golden/${rule.golden.split('/').pop()}`, target: '_blank', text: rule.golden }) : '—']),
            el('td', { class: 'numeric', text: String(rule.passed) }),
            el('td', { class: 'numeric', text: String(rule.failed) }),
          ],
          failed: rule.failed,
          status: rule.failed ? 'fail' : 'pass',
        },
        () => {
          const body = el('div', {}, [el('p', { class: 'empty', text: 'Loading golden records…' })]);
          loadGolden(rule.golden ?? '').then((records) => {
            body.replaceChildren(
              table(
                [
                  { label: 'Record' },
                  { label: 'Input' },
                  { label: 'Expected' },
                  { label: 'Actual' },
                  { label: 'Status', status: true },
                ],
                records.map((record, index) => {
                  const failure = failures.get(index);
                  return el('tr', { class: failure ? 'row-fail' : null }, [
                    el('td', { class: 'mono' }, [goldenLink(rule.golden, index, `${ruleId} #${index}`)]),
                    el('td', { class: 'mono wrap', text: JSON.stringify(record.input) }),
                    el('td', { class: 'mono wrap', text: JSON.stringify(record.expected) }),
                    el('td', { class: 'mono wrap' }, [
                      el('span', { text: failure ? failure.actual ?? 'assertion failed' : 'matches expected' }),
                      failure ? el('pre', { class: 'assertion', text: assertionOf(failure.message) }) : null,
                    ]),
                    statusCell(failure ? 'fail' : 'pass'),
                  ]);
                })
              )
            );
          });
          return body;
        }
      )
    );
  }

  const supporting = report.supportingTests;
  rows.push(
    el('tr', { class: supporting.failed ? 'row-fail' : null }, [
      el('td', { text: `Supporting corpus tests — ${supporting.total} tests` }),
      el('td', { class: 'mono', text: 'tests/Contoso.Lending.ParityTests' }),
      el('td', { class: 'numeric', text: String(supporting.passed) }),
      el('td', { class: 'numeric', text: String(supporting.failed) }),
      statusCell(supporting.failed ? 'fail' : 'pass'),
    ])
  );

  return table(
    [
      { label: 'Business rule' },
      { label: 'Golden file' },
      { label: 'Passed', numeric: true },
      { label: 'Failed', numeric: true },
      { label: 'Status', status: true },
    ],
    rows
  );
}

// ------------------------------------------------------------------ level 3 --
function renderL3(report) {
  if (!report) return el('p', { class: 'empty', text: 'No layer 3 report yet.' });
  return table(
    [
      { label: 'Table' },
      { label: 'Check' },
      { label: 'Oracle' },
      { label: 'Postgres' },
      { label: 'Status', status: true },
    ],
    report.checks.map((check) =>
      el('tr', { class: check.pass ? null : 'row-fail' }, [
        el('td', { class: 'mono', text: check.table }),
        el('td', { class: 'mono', text: check.check }),
        el('td', { class: 'mono wrap', text: check.oracle }),
        el('td', { class: 'mono wrap', text: check.postgres }),
        statusCell(check.pass ? 'pass' : 'fail'),
      ])
    )
  );
}

// ------------------------------------------------------------------ level 4 --
function renderL4(report) {
  if (!report) return el('p', { class: 'empty', text: 'No layer 4 report yet.' });
  const rows = [];
  for (const scenario of report.scenarios) {
    const workflow = scenario.workflow ?? {};
    rows.push(
      ...group(
        `l4:${scenario.scenario}`,
        {
          title: `${scenario.scenario} — ${scenario.description}`,
          cells: [
            el('td', { class: 'mono', text: show((scenario.actual ?? {}).decision) }),
            el('td', { class: 'mono wrap', text: show(workflow.workflowId) }),
            el('td', { class: 'mono', text: show(workflow.loanId) }),
          ],
          failed: scenario.pass ? 0 : 1,
          status: scenario.pass ? 'pass' : 'fail',
        },
        () => {
          const keys = Array.from(
            new Set([...Object.keys(scenario.expected ?? {}), ...Object.keys(scenario.actual ?? {})])
          );
          return el('div', {}, [
            table(
              [{ label: 'Asserted field' }, { label: 'Expected' }, { label: 'Actual' }, { label: 'Status', status: true }],
              keys.map((key) => {
                const expectedValue = (scenario.expected ?? {})[key];
                const actualValue = (scenario.actual ?? {})[key];
                const mismatched = (scenario.mismatches ?? []).some((line) => line.startsWith(`${key}:`));
                return el('tr', { class: mismatched ? 'row-fail' : null }, [
                  el('td', { class: 'mono', text: key }),
                  el('td', { class: 'mono wrap', text: show(expectedValue) }),
                  el('td', { class: 'mono wrap', text: show(actualValue) }),
                  statusCell(mismatched ? 'fail' : 'pass'),
                ]);
              })
            ),
            ...(scenario.mismatches ?? []).map((line) => el('p', { class: 'empty mono', text: line })),
          ]);
        }
      )
    );
  }
  return table(
    [
      { label: 'Scenario' },
      { label: 'Decision' },
      { label: 'Workflow id' },
      { label: 'Loan' },
      { label: 'Status', status: true },
    ],
    rows
  );
}

// -------------------------------------------------------------------- render --
function renderSummary() {
  summaryBody.replaceChildren();
  let passed = 0;
  let failed = 0;
  for (const id of LEVEL_ORDER) {
    const level = state?.levels?.[id];
    if (!level) continue;
    const totals = level.totals;
    passed += totals?.passed ?? 0;
    failed += totals?.failed ?? 0;
    summaryBody.append(
      el('tr', { class: totals?.failed ? 'row-fail' : null }, [
        el('td', { text: level.name }),
        el('td', { class: 'meta', text: DESCRIPTIONS[id] }),
        el('td', { class: 'numeric', text: totals ? String(totals.passed) : '—' }),
        el('td', { class: 'numeric', text: totals ? String(totals.failed) : '—' }),
        statusCell(level.status),
      ])
    );
  }
  const overall = state?.status ?? 'pending';
  summaryFoot.replaceChildren(
    el('tr', {}, [
      el('td', { text: 'All levels' }),
      el('td', { class: 'meta', text: 'Exact equality, tolerance zero' }),
      el('td', { class: 'numeric', text: String(passed) }),
      el('td', { class: 'numeric', text: String(failed) }),
      statusCell(overall === 'idle' ? 'pending' : overall),
    ])
  );
}

function renderLevels() {
  const renderers = { l1: renderL1, l2: renderL2, l3: renderL3, l4: renderL4 };
  levelsRoot.replaceChildren(
    ...LEVEL_ORDER.map((id) => {
      const level = results?.levels?.[id];
      const liveStatus = state?.levels?.[id]?.status;
      const stale =
        state?.status === 'running' && (liveStatus === 'pending' || liveStatus === 'running') && !!level?.report;
      const totals = state?.levels?.[id]?.totals ?? level?.totals;
      const counts = totals ? `${totals.passed}/${totals.total} ${level?.unit ?? ''} passed` : '';
      const meta = stale ? (counts ? `Previous run — ${counts}` : 'Previous run') : counts;
      const body = renderers[id](level?.report ?? null);
      return levelCard(
        id,
        stale
          ? el('div', {}, [
              el('p', {
                class: 'empty',
                text: 'This level has not finished in the current run — the results below are from the previous run.',
              }),
              body,
            ])
          : body,
        meta
      );
    })
  );
}

function render() {
  renderSummary();
  renderLevels();
  const status = state?.status ?? 'idle';
  runButton.disabled = status === 'running';
  runState.textContent =
    status === 'running'
      ? 'Running'
      : status === 'idle'
        ? 'Idle — showing the last recorded run'
        : `Last run ${STATUS_TEXT[status] ?? status}${state?.finishedAt ? ` at ${state.finishedAt}` : ''}`;
}

runButton.addEventListener('click', async () => {
  runButton.disabled = true;
  logPane.textContent = '';
  const response = await fetch('/api/run', { method: 'POST' });
  if (!response.ok) {
    runState.textContent = 'A run is already in flight';
    runButton.disabled = false;
  }
});

const stream = new EventSource('/api/stream');
stream.addEventListener('state', (event) => {
  state = JSON.parse(event.data);
  render();
});
stream.addEventListener('results', (event) => {
  results = JSON.parse(event.data);
  render();
});
stream.addEventListener('level', (event) => {
  const level = JSON.parse(event.data);
  if (results) results.levels[level.id] = level;
  render();
});
stream.addEventListener('done', (event) => {
  results = JSON.parse(event.data).results;
  render();
});
stream.addEventListener('log', (event) => {
  logPane.textContent += `${JSON.parse(event.data).line}\n`;
  logPane.scrollTop = logPane.scrollHeight;
});

const initial = await fetch('/api/results');
results = await initial.json();
state = await (await fetch('/api/state')).json();
render();
