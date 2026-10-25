renderNav('history');

const EYE_ICON = '<svg class="btn-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"></path><circle cx="12" cy="12" r="3"></circle></svg>';
const EYE_OFF_ICON = '<svg class="btn-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17.94 17.94A10.94 10.94 0 0 1 12 20c-7 0-11-8-11-8a18.5 18.5 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19M14.12 14.12a3 3 0 1 1-4.24-4.24"></path><line x1="1" y1="1" x2="23" y2="23"></line></svg>';

// Two independent show/hide toggles - History (the Period rollup table) and Reports (the run
// list) - each remembers its own state and only ever affects its own section.
function setupToggle(storageKey, sectionId, btnId, label) {
  const section = document.getElementById(sectionId);
  const btn = document.getElementById(btnId);

  function applyVisibility() {
    const hidden = localStorage.getItem(storageKey) === 'true';
    section.style.display = hidden ? 'none' : '';
    // Hidden -> offer to show it again, so the icon shown is the "eye" (what clicking now does);
    // visible -> offer to hide it, so the icon is "eye-off". Same logic the text label follows.
    btn.innerHTML = (hidden ? EYE_ICON : EYE_OFF_ICON) + (hidden ? `Show ${label}` : `Hide ${label}`);
  }

  btn.addEventListener('click', () => {
    const hidden = localStorage.getItem(storageKey) === 'true';
    localStorage.setItem(storageKey, (!hidden).toString());
    applyVisibility();
  });

  applyVisibility();
}

// The whole history as a CSV file: the server answers with a download, so a plain navigation is all it takes.
document.getElementById('exportCsvBtn').addEventListener('click', () => { window.location.href = '/api/history/export.csv'; });

setupToggle('compressarr.historyHidden', 'historySection', 'toggleHistoryBtn', 'history');
setupToggle('compressarr.reportsHidden', 'reportsSection', 'toggleReportsBtn', 'reports');

// Matches the "Xh Ym Zs" convention already used everywhere else (run-complete log lines, the
// HTML report's own duration text) - always all three units, not just the ones that are nonzero.
function formatDuration(totalSeconds) {
  const s = Math.max(0, Math.round(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  return `${h}h ${m}m ${sec}s`;
}

function reportRow(entry) {
  const saved = entry.beforeGb > 0 ? Math.round((1 - entry.afterGb / entry.beforeGb) * 1000) / 10 : 0;
  const date = new Date(entry.date).toLocaleDateString();
  const url = `/api/reports/${encodeURIComponent(entry.reportFileName)}`;
  // Same red/yellow language as the HTML report's own per-file rows (tr.err/tr.warn) and the
  // sidebar's error/warning count badges - a run with any failed file is an error row even if it
  // also has warnings, matching how the sidebar badges count them as separate, non-overlapping
  // buckets.
  //
  // A run that sent any file to a different lane than its own gets its own notice colour (neither red nor
  // yellow - a redirect is a deliberate choice, not a failure), shown when nothing worse applies, plus a
  // small tag beside the report link either way so it is never lost under an error/warning colour.
  const redirectCount = entry.redirectCount || 0;
  const rowClass = entry.errorCount > 0 ? ' class="err"' : entry.warningCount > 0 ? ' class="warn"' : redirectCount > 0 ? ' class="redir"' : '';
  const redirectTag = redirectCount > 0
    ? ` <span class="redirect-tag" title="${redirectCount} file(s) in this run landed in a different lane than the one they were found in">&#8618; ${redirectCount} redirected</span>`
    : '';
  return `<tr${rowClass}>
    <td>${entry.runNumber}</td>
    <td><a href="${url}" target="_blank" rel="noopener">${date} report</a>${redirectTag}</td>
    <td>${entry.fileCount}</td>
    <td>${entry.beforeGb.toFixed(2)} GB</td>
    <td>${entry.afterGb.toFixed(2)} GB</td>
    <td>${saved}%</td>
  </tr>`;
}

// The Reports card has its own period buttons, like the Overview's (the list would otherwise grow with every run for as long as Compressarr is used),
// and shows one page at a time. Today and 25 rows are where it starts; both choices are remembered in the browser.
const RP_PERIOD_KEY = 'compressarr.history.reportsPeriod';
const RP_SIZE_KEY = 'compressarr.history.reportsPageSize';
const RP_PERIODS = { all: 'All time', year: 'Last year', d30: 'Last 30 days', d7: 'Last 7 days', today: 'Today' };
const RP_SIZES = [25, 50, 100];
let rpPeriod = 'today';
let rpPageSize = 25;
let rpPage = 1;

// 1 2 3 ... 9 10 11 ... 20: the first and last page, the current one and its neighbours, with gaps closed up.
function pageNumbers(page, pages) {
  const wanted = new Set([1, 2, pages - 1, pages, page - 1, page, page + 1].filter(n => n >= 1 && n <= pages));
  const out = [];
  let last = 0;
  for (const n of [...wanted].sort((a, b) => a - b)) {
    if (n - last > 1) out.push('gap');
    out.push(n);
    last = n;
  }
  return out;
}

function renderPager(total, page) {
  const pager = document.getElementById('rpPager');
  if (total === 0) { pager.innerHTML = ''; return; }

  const pages = Math.max(1, Math.ceil(total / rpPageSize));
  const first = (page - 1) * rpPageSize + 1;
  const last = Math.min(total, page * rpPageSize);
  const buttons = pages === 1 ? '' :
    `<span class="rp-pages" role="navigation" aria-label="Report pages">` +
    `<button type="button" data-page="${page - 1}" ${page === 1 ? 'disabled' : ''} aria-label="Previous page">&laquo; Previous</button>` +
    pageNumbers(page, pages).map(n => n === 'gap' ? '<span class="gap">&hellip;</span>'
      : `<button type="button" data-page="${n}" class="${n === page ? 'current' : ''}" ${n === page ? 'aria-current="page"' : ''}>${n}</button>`).join('') +
    `<button type="button" data-page="${page + 1}" ${page === pages ? 'disabled' : ''} aria-label="Next page">Next &raquo;</button></span>`;

  pager.innerHTML =
    `<span>Showing ${first}&ndash;${last} of ${total}</span>${buttons}` +
    `<label class="rp-size">Rows per page <select id="rpSize">${RP_SIZES.map(s => `<option value="${s}"${s === rpPageSize ? ' selected' : ''}>${s}</option>`).join('')}</select></label>`;
}

async function loadReports() {
  const res = await fetch(`/api/history/reports?period=${rpPeriod}&page=${rpPage}&pageSize=${rpPageSize}`);
  const entries = await res.json();
  const total = Number(res.headers.get('X-Total-Count')) || entries.length;
  rpPage = Number(res.headers.get('X-Page')) || 1;

  const reportsStatus = document.getElementById('reportsStatus');
  if (entries.length === 0) {
    document.getElementById('reportsRows').innerHTML = '';
    reportsStatus.textContent = rpPeriod === 'today'
      ? 'No runs today. Choose a longer period to see earlier reports.'
      : `No reports in this period (${RP_PERIODS[rpPeriod].toLowerCase()}).`;
  } else {
    reportsStatus.textContent = '';
    document.getElementById('reportsRows').innerHTML = entries.map(reportRow).join('');
  }
  renderPager(total, rpPage);
  return entries;
}

// ---- unseen errors and warnings -------------------------------------------------------------------------------------------------------------
// Above the list, a table of the runs with an error or a warning that have not been looked at yet - the ones the sidebar badges count. Showing
// it counts as seeing them, which clears the badges (here and on every other page) until a new problem run happens. "View recent errors" shows
// the latest problem runs again, seen or not, in the same place. Best-effort and fire-and-forget: a failure just leaves the badges as they were.
let rpUnseen = [];
let rpRecentOn = false;

function markHistoryViewed() {
  fetch('/api/history/mark-viewed', { method: 'POST' })
    .then(() => renderHistoryBadges())
    .catch(() => {});
}

function showAttention(entries, title, emptyText) {
  const panel = document.getElementById('rpAttention');
  if (entries.length === 0 && !emptyText) { panel.hidden = true; return; }
  document.getElementById('rpAttentionTitle').textContent = entries.length === 0 ? title : `${title} (${entries.length})`;
  document.getElementById('rpAttentionRows').innerHTML = entries.map(reportRow).join('');
  document.getElementById('rpAttentionStatus').textContent = entries.length === 0 ? emptyText : '';
  panel.hidden = false;
}

async function loadUnseenProblems() {
  try {
    rpUnseen = await (await fetch('/api/history/problems?scope=unseen')).json();
    showAttention(rpUnseen, 'New errors and warnings you have not seen yet', null);
    markHistoryViewed();
  } catch { /* nothing to show; the badges stay as they were */ }
}

async function toggleRecentProblems() {
  rpRecentOn = !rpRecentOn;
  const button = document.getElementById('rpProblemsBtn');
  button.setAttribute('aria-pressed', String(rpRecentOn));
  button.textContent = rpRecentOn ? 'Hide recent errors' : 'View recent errors';
  if (!rpRecentOn) { showAttention(rpUnseen, 'New errors and warnings you have not seen yet', null); return; }

  try {
    const recent = await (await fetch('/api/history/problems?scope=recent')).json();
    showAttention(recent, 'Recent runs with errors or warnings', 'No run has had an error or a warning.');
  } catch {
    showAttention([], 'Recent runs with errors or warnings', 'The list could not be loaded.');
  }
}

function wireReports() {
  const savedPeriod = ovRead(RP_PERIOD_KEY, 'today');
  rpPeriod = RP_PERIODS[savedPeriod] ? savedPeriod : 'today';
  const savedSize = Number(ovRead(RP_SIZE_KEY, 25));
  rpPageSize = RP_SIZES.includes(savedSize) ? savedSize : 25;
  document.querySelector(`#rpPeriods [value="${rpPeriod}"]`).checked = true;

  document.getElementById('rpPeriods').addEventListener('change', e => {
    if (!RP_PERIODS[e.target.value]) return;
    rpPeriod = e.target.value;
    rpPage = 1;
    ovWrite(RP_PERIOD_KEY, rpPeriod);
    loadReports();
  });
  // One listener for the page buttons and the rows-per-page box: the pager is redrawn with every page.
  document.getElementById('rpPager').addEventListener('click', e => {
    const button = e.target.closest('button[data-page]');
    if (!button || button.disabled) return;
    rpPage = Number(button.dataset.page);
    loadReports();
  });
  document.getElementById('rpPager').addEventListener('change', e => {
    if (e.target.id !== 'rpSize') return;
    rpPageSize = Number(e.target.value);
    rpPage = 1;
    ovWrite(RP_SIZE_KEY, rpPageSize);
    loadReports();
  });
  document.getElementById('rpProblemsBtn').addEventListener('click', toggleRecentProblems);
}

loadOverview();   // history-overview.js: the period selector, the stat cards, the period table and the charts
wireReports();
loadUnseenProblems();
loadReports();
