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

async function loadReports() {
  const res = await fetch('/api/history/reports');
  const entries = await res.json();

  const reportsStatus = document.getElementById('reportsStatus');
  if (entries.length === 0) {
    document.getElementById('reportsRows').innerHTML = '';
    reportsStatus.textContent = 'No reports within the current retention period.';
    return;
  }

  reportsStatus.textContent = '';
  document.getElementById('reportsRows').innerHTML = entries.map(reportRow).join('');
}

// Having actually rendered the Reports list above counts as "seen" - clears the sidebar's
// error/warning badges (here and on every other page) until a new problem run happens. Best-effort
// and fire-and-forget: a failure here just leaves the badges as they were, nothing on this page
// depends on it succeeding.
function markHistoryViewed() {
  fetch('/api/history/mark-viewed', { method: 'POST' })
    .then(() => renderHistoryBadges())
    .catch(() => {});
}

loadOverview();   // history-overview.js: the period selector, the stat cards, the period table and the charts
loadReports().then(markHistoryViewed);
