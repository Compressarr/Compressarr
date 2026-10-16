renderNav('monitor');

const toggleMonitorBtn = document.getElementById('toggleMonitorBtn');
const runNowBtn = document.getElementById('runNowBtn');
const togglePauseBtn = document.getElementById('togglePauseBtn');
const abortBtn = document.getElementById('abortBtn');
const logPanel = document.getElementById('log-panel');

// Monitoring status text, the countdown, and CPU are global toolbar elements owned by nav.js
// (visible on every page, not just this one, hence GLOBAL_STOPPING_MESSAGE living there, not
// redeclared here) - this file only sets them optimistically, right at the moment of a click,
// for zero-latency feedback on the one surface that actually acted. nav.js's own poll (every
// 1.5s) is what keeps them accurate on an ongoing basis afterward.

const START_ICON = '<svg class="btn-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="6 3 20 12 6 21 6 3"></polygon></svg>';
const STOP_ICON = '<svg class="btn-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="6" y="6" width="12" height="12" rx="1"></rect></svg>';

// Single Start/Stop toggle - what it shows and what a click does both depend on the monitoring
// state as of the last poll (or an optimistic override set the instant this surface clicks it,
// same zero-latency idea the old two-button version used). Stays the same blue "primary" look in
// every state, on purpose - only the icon/label/action change, not the color.
let toggleIsMonitoring = false;
let toggleIsStopping = false;

function renderToggleButton() {
  if (toggleIsStopping) {
    toggleMonitorBtn.innerHTML = STOP_ICON + 'Stopping...';
    toggleMonitorBtn.title = 'Stopping monitoring';
    toggleMonitorBtn.disabled = true;
  } else if (toggleIsMonitoring) {
    toggleMonitorBtn.innerHTML = STOP_ICON + 'Stop';
    toggleMonitorBtn.title = 'Stop monitoring';
    toggleMonitorBtn.disabled = false;
  } else {
    toggleMonitorBtn.innerHTML = START_ICON + 'Start';
    toggleMonitorBtn.title = 'Start monitoring';
    toggleMonitorBtn.disabled = false;
  }
}

toggleMonitorBtn.addEventListener('click', async () => {
  if (toggleIsMonitoring) {
    // Reflect the click immediately, zero-latency, on this surface - StopAsync doesn't resolve
    // until the in-flight file finishes converting, which can be minutes away. poll() below picks
    // up the server's own isStopping flag (shared with the tray icon) within 1.5s regardless, so a
    // stop requested from either surface shows up on both - this is just to not even wait that
    // long for the surface that actually clicked.
    toggleIsStopping = true;
    renderToggleButton();
    document.getElementById('monitoringState').textContent = 'Stopping monitor after current task completes';

    await fetch('/api/run/stop', { method: 'POST' });
  } else {
    // Reflect the click immediately - a real conversion pass can start on the server within
    // milliseconds, well before the next poll() would otherwise refresh this panel, so the log
    // window needs to say "starting" right now rather than sitting on stale content until then.
    toggleIsMonitoring = true;
    renderToggleButton();
    document.getElementById('monitoringState').textContent = 'Monitoring is ON';
    renderLog([{ text: 'Starting monitoring...', severity: 'Info' }]);

    await fetch('/api/run/start', { method: 'POST' });
  }
  poll();
});

const PAUSE_ICON = '<svg class="btn-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="6" y="4" width="4" height="16"></rect><rect x="14" y="4" width="4" height="16"></rect></svg>';
// Same play-triangle shape as Start Monitoring's icon - "resume" is the same action, visually.
const RESUME_ICON = START_ICON;

// Pause/Resume toggle - only meaningful while a file is actually converting (disabled otherwise,
// same reasoning as Abort). Sends HandBrakeCLI's own interactive "p"/"r" keystrokes via
// IActiveHandBrakeProcess, not a real OS-level process suspend - matches what pressing those keys
// already does when HandBrakeCLI runs directly in a console.
let togglePauseIsRunning = false;
let togglePauseIsPaused = false;

function renderPauseButton() {
  togglePauseBtn.disabled = !togglePauseIsRunning;
  if (togglePauseIsPaused) {
    togglePauseBtn.innerHTML = RESUME_ICON + 'Resume';
    togglePauseBtn.classList.add('primary');
  } else {
    togglePauseBtn.innerHTML = PAUSE_ICON + 'Pause';
    togglePauseBtn.classList.remove('primary');
  }
}

togglePauseBtn.addEventListener('click', async () => {
  if (togglePauseIsPaused) {
    togglePauseIsPaused = false;
    renderPauseButton();
    await fetch('/api/run/resume', { method: 'POST' });
  } else {
    togglePauseIsPaused = true;
    renderPauseButton();
    await fetch('/api/run/pause', { method: 'POST' });
  }
  poll();
});

runNowBtn.addEventListener('click', async () => {
  // Clear the countdown immediately in the UI - the server-side trigger races the remaining
  // delay, but there's no reason to keep showing a countdown the click just made moot.
  document.getElementById('countdown').textContent = '';
  runNowBtn.disabled = true;

  await fetch('/api/run/run-now', { method: 'POST' });
  poll();
});

// Only shown while the optional off-hours-only schedule is holding the queue for the daytime
// window (see poll()). Releases the hold for the rest of this window, then nudges an idle monitor
// to start its next pass right away instead of waiting out the poll countdown.
const runAnywayBtn = document.getElementById('runAnywayBtn');
runAnywayBtn.addEventListener('click', async () => {
  runAnywayBtn.disabled = true;
  await fetch('/api/run/run-anyway', { method: 'POST' });
  await fetch('/api/run/trigger-now', { method: 'POST' });
  runAnywayBtn.disabled = false;
  poll();
});

abortBtn.addEventListener('click', async () => {
  if (!confirm(toggleIsMonitoring ? 'Abort the current conversion immediately and stop monitoring?' : 'Abort the current conversion immediately?')) return;
  await fetch('/api/run/abort', { method: 'POST' });
  poll();
});

// The lines the live panel last drew: what the View Log window shows when it is opened or refreshed.
let latestLogLines = [];

function renderLog(lines) {
  latestLogLines = lines || [];
  // poll() calls this every 1.5s - unconditionally forcing scrollTop to the bottom every time
  // means scrolling up to read something gets yanked back down before you can read it. Only
  // auto-scroll if the user was already at (or very near) the bottom, same "stick to bottom"
  // behavior most live log/chat views use. Captured before the innerHTML replacement below,
  // since replacing it resets scrollTop.
  const wasAtBottom = logPanel.scrollHeight - logPanel.scrollTop - logPanel.clientHeight < 20;

  logPanel.innerHTML = lines
    .map(l => `<div class="log-line${l.severity === 'Error' ? ' error' : ''}">${escapeHtml(l.text)}</div>`)
    .join('');

  if (wasAtBottom) {
    logPanel.scrollTop = logPanel.scrollHeight;
  }
}

// --- View Log: the same lines in a window that does not refresh by itself, so text can be selected and copied.
// The live panel above is redrawn every poll (which clears any selection in it); this one only changes when Refresh is pressed.
const logOverlay = document.getElementById('logOverlay');
const logViewText = document.getElementById('logViewText');
const logViewNote = document.getElementById('logViewNote');
let viewedLogLines = [];

function fillLogView() {
  viewedLogLines = latestLogLines.slice();
  logViewText.innerHTML = viewedLogLines
    .map(l => `<div class="log-line${l.severity === 'Error' ? ' error' : ''}">${escapeHtml(l.text)}</div>`)
    .join('');
  logViewText.scrollTop = logViewText.scrollHeight;
  logViewNote.textContent = `${viewedLogLines.length} line${viewedLogLines.length === 1 ? '' : 's'}, as of ${new Date().toLocaleTimeString()}.`;
}

function closeLogView() { logOverlay.classList.add('hidden'); }

// navigator.clipboard only exists on a secure page (https or localhost); when Compressarr is opened by another machine's address it is missing, so the
// older select-and-copy route is the fallback.
async function copyTextToClipboard(text) {
  try {
    if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); return true; }
  } catch { /* fall through to the older route */ }
  const box = document.createElement('textarea');
  box.value = text;
  box.style.position = 'fixed';
  box.style.opacity = '0';
  document.body.appendChild(box);
  box.select();
  let ok = false;
  try { ok = document.execCommand('copy'); } catch { ok = false; }
  box.remove();
  return ok;
}

// Shown first, then filled: a hidden window has no height, so scrolling it to the end would do nothing and it would open at the top.
document.getElementById('viewLogBtn').addEventListener('click', () => { logOverlay.classList.remove('hidden'); fillLogView(); });
document.getElementById('logRefresh').addEventListener('click', fillLogView);
document.getElementById('logClose').addEventListener('click', closeLogView);
document.getElementById('logCopy').addEventListener('click', async () => {
  const ok = await copyTextToClipboard(viewedLogLines.map(l => l.text).join('\n'));
  logViewNote.textContent = ok ? `Copied ${viewedLogLines.length} line${viewedLogLines.length === 1 ? '' : 's'}.` : 'Could not copy automatically - select the text and press Ctrl+C.';
});
logOverlay.addEventListener('click', e => { if (e.target === logOverlay) closeLogView(); });
document.addEventListener('keydown', e => { if (e.key === 'Escape' && !logOverlay.classList.contains('hidden')) closeLogView(); });

// --- In Queue: drag-to-reorder, skip/remove, per-file preset override -----------------------
//
// latestItems is always the most recent server snapshot (refreshed every poll). displayItems is
// what's actually rendered - normally kept in sync with latestItems, but frozen to a locally
// reordered copy while a drag is in progress so the periodic poll can't yank the list out from
// under the user's hand mid-drag. A composite laneId+fileName key stands in for a stable row id,
// since the server doesn't hand back one.

let latestItems = [];
let displayItems = [];
let draggingKey = null;
let openMenuKey = null;
let selectOpenKey = null;
let ghostEl = null;
let grabOffsetY = 0;
let presetNamesByEngine = { HandBrake: [], FFmpeg: [] };
// Whether any lane uses ffmpeg - only then do HandBrake rows get an encoder chip too, so a HandBrake-only
// setup's queue looks exactly as it always did.
let anyFfmpegLane = false;
// Loaded once at startup alongside the preset list below (same settings fetch, no extra round
// trip) - a Settings-page change to this while Monitor is already open needs a Monitor page
// reload to take effect, same "load once" pattern this function already had for presetNames.
let queueEtaFormat = 'DateTime';

const PRESET_DEFAULT_VALUE = '__lane_default__';

const QUEUE_ICON_GRIP = '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><circle cx="8" cy="6" r="1.6"></circle><circle cx="16" cy="6" r="1.6"></circle><circle cx="8" cy="12" r="1.6"></circle><circle cx="16" cy="12" r="1.6"></circle><circle cx="8" cy="18" r="1.6"></circle><circle cx="16" cy="18" r="1.6"></circle></svg>';
const QUEUE_ICON_DOTS = '<svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor"><circle cx="12" cy="5" r="1.8"></circle><circle cx="12" cy="12" r="1.8"></circle><circle cx="12" cy="19" r="1.8"></circle></svg>';

async function loadQueuePresetNames() {
  try {
    const settingsRes = await fetch('/api/settings');
    const settings = await settingsRes.json();
    queueEtaFormat = settings.queueEtaFormat || 'DateTime';
    const [hb, ff] = await Promise.all([fetch('/api/presets?includePassthrough=1'), fetch('/api/presets?engine=ffmpeg&includePassthrough=1')]);
    presetNamesByEngine = { HandBrake: await hb.json(), FFmpeg: await ff.json() };
  } catch { /* best-effort - the preset-override dropdown just stays empty if this fails */ }
}

// The lanes a queued file can be told to land in. Loaded once and refreshed now and then rather than on every
// poll - it only changes when someone edits the Lanes page.
let queueLanes = [];

async function loadQueueLanes() {
  try {
    const res = await fetch('/api/lanes');
    const lanes = await res.json();
    queueLanes = lanes.map(l => ({ id: l.id, displayName: l.displayName, enabled: l.enabled }));
    anyFfmpegLane = lanes.some(l => l.enabled && l.engine === 'FFmpeg');
  } catch { /* best-effort - the "lands in" dropdown just offers nothing if this fails */ }
}
setInterval(loadQueueLanes, 30000);

// The "lands in" dropdown on a queue row: which lane's library the FINISHED file is filed into. The file
// itself never moves and everything else about it (preset, order, Output folder) stays with its own lane,
// so Sonarr/Radarr never see it go missing. The first option is the file's own lane (the default).
function fillQueueDestinationSelect(select, item) {
  const others = queueLanes.filter(l => l.id !== item.laneId);
  const missing = item.destinationMissing
    ? `<option value="${escapeHtml(item.destinationLaneId)}">Deleted lane - held in Output</option>`
    : '';
  select.innerHTML =
    `<option value="">Lands in: ${escapeHtml(item.laneDisplayName)}</option>` +
    missing +
    others.map(l => `<option value="${escapeHtml(l.id)}">Land in: ${escapeHtml(l.displayName)}${l.enabled ? '' : ' (disabled)'}</option>`).join('');
  select.value = item.destinationLaneId || '';
}

// Same pattern lanes.js's fillPresetSelect uses for the lane card's own TV/Movie preset
// dropdowns - a plain native <select>, not a custom popover list, so a long presets.json reads
// the same familiar, scrollable way everywhere in the app.
//
// Shows the preset actually in effect (item.preset - the override if set, otherwise whatever the
// lane resolves to) as the selected value, rather than a generic "Lane default" placeholder that
// hides which preset is really running. A "Use lane default" entry is still offered as an
// explicit choice to clear an override, but only when there IS one to clear - it's never what's
// shown by default for a file that's already just using the lane's own preset.
function fillQueuePresetSelect(select, item) {
  // Each row offers its OWN lane's encoder's profiles, so a file can only be given one its lane can run.
  const presetNames = presetNamesByEngine[item.engine || 'HandBrake'] || [];
  const currentValue = item.preset || '';
  // If the file's current value isn't in presetNames (a stale override from a presets.json
  // that's since changed), keep it as a selectable option anyway rather than silently dropping it.
  const names = (currentValue && !presetNames.includes(currentValue)) ? [currentValue, ...presetNames] : presetNames;
  const resetOption = item.isCustomPreset ? `<option value="${PRESET_DEFAULT_VALUE}">Use lane default</option>` : '';
  select.innerHTML = resetOption + names.map(n => `<option value="${escapeHtml(n)}">${escapeHtml(n)}</option>`).join('');
  select.value = currentValue;
}

// fullName, not fileName - two different files can legitimately share the same leaf name in
// different lane subfolders (code-review finding), so only the full path is actually unique.
function queueKey(item) { return `${item.laneId}::${item.fullName}`; }

// "-> Kids" beside the lane name when a file is set to land somewhere other than its own lane's library.
function queueDestinationMarker(item) {
  if (!item.destinationLaneId) return '';
  if (item.destinationMissing) {
    return ' <span class="queue-dest missing" title="The lane this file was assigned to has been deleted. The finished file will wait in Output until you choose where it lands.">&#8618; deleted lane</span>';
  }
  return ` <span class="queue-dest" title="The finished file will land in ${escapeHtml(item.destinationLaneName)}'s library. The source file is not moved.">&#8618; ${escapeHtml(item.destinationLaneName)}</span>`;
}

// A small encoder tag beside the lane name: ffmpeg rows always; HandBrake rows only when some lane uses ffmpeg.
function queueEngineChip(item) {
  if (item.isPassthrough) return ' <span class="queue-chip pt" title="This file is copied, not re-encoded. Everything else (filing, renaming, cleanup) still runs.">Passthrough</span>';
  if (item.engine === 'FFmpeg') return ' <span class="queue-chip ff">ffmpeg</span>';
  return anyFfmpegLane ? ' <span class="queue-chip">HandBrake</span>' : '';
}

function queueBadgeClass(item) {

  if (item.isError) return 'error';
  return item.isResumed ? 'resumed' : 'new';
}

function queueBadgeLabel(item) {
  if (item.isError) return 'Error';
  return item.isResumed ? 'Resumed' : 'New';
}

// Poster pictures (Settings > Queue Posters). Set from each poll before the queue is drawn; when off, rows are drawn exactly as before.
let posterConfig = { enabled: false, smaller: false };

function posterSrc(key, job) {
  const size = job ? (posterConfig.smaller ? 'w154' : 'w185') : (posterConfig.smaller ? 'w92' : 'w154');
  return `/api/posters/${encodeURIComponent(key)}?size=${size}`;
}

// A poster when TMDB has been asked about this title, otherwise a neutral placeholder (a file with no year in its name, or one not looked
// up yet). An image that fails to load turns into the placeholder too.
function queuePosterHtml(item) {
  if (!posterConfig.enabled) return '';
  // An Error row has no drag handle; the spacer keeps its poster in line with the other rows.
  const spacer = item.isError ? '<span class="queue-handle-spacer"></span>' : '';
  if (!item.posterKey) return spacer + '<span class="queue-poster noimg"></span>';
  return spacer + `<span class="queue-poster"><img alt="" decoding="async" draggable="false" src="${posterSrc(item.posterKey, false)}" onerror="this.parentNode.classList.add('noimg');this.remove()"></span>`;
}

function renderQueue(items) {
  latestItems = items || [];
  if (draggingKey === null) {
    displayItems = latestItems.map(i => ({ ...i }));
  }
  // Tracks displayItems, not latestItems, so the count never disagrees with what's actually
  // rendered below it - including while the list is frozen mid-drag (see the guard further down).
  const count = displayItems.length;
  document.getElementById('queueHeading').textContent = `${count} File${count === 1 ? '' : 's'} In Queue`;
  // A row whose item disappeared from the server snapshot (completed, removed elsewhere) shouldn't
  // leave a dangling open popover referencing it.
  const keys = new Set(displayItems.map(queueKey));
  if (openMenuKey !== null && !keys.has(openMenuKey)) openMenuKey = null;
  if (selectOpenKey !== null && !keys.has(selectOpenKey)) selectOpenKey = null;

  // The 1.5s poll rebuilding the whole list out from under an open native <select> or 3-dot
  // popover was the actual bug behind "the preset list collapses" and "Remove doesn't work" -
  // rebuilding destroys those DOM nodes (and their event listeners) mid-interaction, sometimes
  // between the user's click landing and the row it was attached to still existing. While any of
  // those are active, skip the rebuild entirely and just let it resume once the user finishes
  // (each action already clears its own key before triggering the next poll).
  if (draggingKey !== null || openMenuKey !== null || selectOpenKey !== null) return;

  renderQueueList();
}

function renderQueueList() {
  const list = document.getElementById('queueList');
  if (displayItems.length === 0) {
    list.innerHTML = '<div class="queue-empty">Nothing queued.</div>';
    return;
  }

  const prevRects = {};
  [...list.children].forEach(el => { if (el.dataset.key) prevRects[el.dataset.key] = el.getBoundingClientRect(); });

  list.innerHTML = '';

  for (const item of displayItems) {
    const key = queueKey(item);

    if (key === draggingKey) {
      const ph = document.createElement('div');
      ph.dataset.key = key;
      ph.className = 'queue-item-placeholder';
      ph.style.height = (ghostEl ? ghostEl.offsetHeight : 46) + 'px';
      list.appendChild(ph);
      continue;
    }

    const row = document.createElement('div');
    row.className = 'queue-item';
    row.dataset.key = key;
    row.dataset.laneId = item.laneId;
    if (item.isSkipped) row.classList.add('skipped');

    row.innerHTML = `
      ${item.isError ? '' : `<span class="queue-handle">${QUEUE_ICON_GRIP}</span>`}
      ${queuePosterHtml(item)}
      <span class="queue-badge ${queueBadgeClass(item)}">${item.isSkipped ? 'Skipped' : queueBadgeLabel(item)}</span>
      ${item.isFileBotUnmatched ? '<span class="queue-badge unmatched">Unmatched</span>' : ''}
      <div class="queue-lane">${escapeHtml(item.laneDisplayName)}${queueEngineChip(item)}${queueDestinationMarker(item)}</div>

      <div class="queue-file">${escapeHtml(item.fileName)}</div>
      <div class="queue-meta">${item.sizeGb.toFixed(2)} GB</div>
      ${item.isError
        ? `<span class="queue-preset-static">${escapeHtml(item.preset || '-')}</span>`
        : `<select class="queue-preset-select${item.isCustomPreset ? ' custom' : ''}"></select>`}
      ${item.isError ? '' : `<select class="queue-dest-select${item.destinationLaneId ? ' redirected' : ''}" title="Which lane's library the finished file lands in. The file itself is not moved."></select>`}
      ${item.isError ? '' : `<div class="queue-menu-wrap"><button type="button" class="queue-dots-btn" aria-label="Row actions">${QUEUE_ICON_DOTS}</button></div>`}
    `;
    list.appendChild(row);

    if (item.isError) {
      // Error entries don't support skip/reorder/preset-override - they're excluded from
      // processing entirely until removed, and the backend's preset-override endpoint only
      // finds-or-creates a Pending entry, so wiring it up here would silently create a duplicate
      // Pending row alongside the untouched Error one instead of editing it.
      const removeBtn = document.createElement('button');
      removeBtn.textContent = 'Remove';
      removeBtn.addEventListener('click', () => removeErrorQueueEntry(item.laneId, item.fileName, item.fullName));
      row.appendChild(removeBtn);
    } else {
      row.querySelector('.queue-handle').addEventListener('pointerdown', e => startQueueDrag(e, item, row));
      row.querySelector('.queue-dots-btn').addEventListener('click', e => {
        e.stopPropagation();
        openMenuKey = openMenuKey === key ? null : key;
        renderQueueList();
      });

      const select = row.querySelector('.queue-preset-select');
      fillQueuePresetSelect(select, item);
      select.addEventListener('click', e => e.stopPropagation());
      // Marks this select as "open" for as long as it holds focus, so a poll landing mid-choice
      // can't rebuild the row out from under it (see renderQueue's guard above) - focus is the
      // closest thing the DOM exposes to "the dropdown is actually showing."
      select.addEventListener('focus', () => { selectOpenKey = key; });
      select.addEventListener('blur', () => { if (selectOpenKey === key) selectOpenKey = null; });
      const destSelect = row.querySelector('.queue-dest-select');
      fillQueueDestinationSelect(destSelect, item);
      destSelect.addEventListener('click', e => e.stopPropagation());
      destSelect.addEventListener('focus', () => { selectOpenKey = key; });
      destSelect.addEventListener('blur', () => { if (selectOpenKey === key) selectOpenKey = null; });
      destSelect.addEventListener('change', async e => {
        selectOpenKey = null;
        await fetch('/api/run/queue/destination', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ laneId: item.laneId, fullName: item.fullName, destinationLaneId: e.target.value || null })
        });
        poll();
      });

      select.addEventListener('change', async e => {
        selectOpenKey = null;
        const chosen = e.target.value === PRESET_DEFAULT_VALUE ? null : e.target.value;
        await fetch('/api/run/queue/preset-override', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ laneId: item.laneId, fullName: item.fullName, preset: chosen })
        });
        poll();
      });
    }

    if (openMenuKey === key) {
      const pop = document.createElement('div');
      pop.className = 'queue-popover';
      pop.innerHTML = `
        <div class="queue-popover-item" data-act="skip">${item.isSkipped ? 'Unskip' : 'Skip'}</div>
        ${item.isCustomPreset ? `<div class="queue-popover-item" data-act="use-lane-preset">Use Lane Preset</div>` : ''}
        <div class="queue-popover-item" data-act="move-top">Move to top</div>
        <div class="queue-popover-item" data-act="move-bottom">Move to bottom</div>
        <div class="queue-popover-item danger" data-act="remove">Remove from queue</div>
      `;
      row.querySelector('.queue-menu-wrap').appendChild(pop);
      pop.querySelectorAll('.queue-popover-item').forEach(opt => opt.addEventListener('click', async e => {
        e.stopPropagation();
        openMenuKey = null;
        const act = opt.dataset.act;
        if (act === 'skip') {
          await fetch('/api/run/queue/skip', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ laneId: item.laneId, fullName: item.fullName, skipped: !item.isSkipped })
          });
        } else if (act === 'use-lane-preset') {
          await fetch('/api/run/queue/preset-override', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ laneId: item.laneId, fullName: item.fullName, preset: null })
          });
        } else if (act === 'move-top' || act === 'move-bottom') {
          await moveQueueItem(item, act === 'move-top');
        } else if (act === 'remove') {
          await fetch('/api/run/queue/remove', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ laneId: item.laneId, fullName: item.fullName })
          });
        }
        poll();
      }));
    }
  }

  [...list.children].forEach(el => {
    const prev = prevRects[el.dataset.key];
    if (!prev) return;
    const now = el.getBoundingClientRect();
    const dy = prev.top - now.top;
    if (Math.abs(dy) > 0.5) {
      el.style.transition = 'none';
      el.style.transform = `translateY(${dy}px)`;
      requestAnimationFrame(() => {
        el.style.transition = 'transform 180ms cubic-bezier(.2,.8,.2,1)';
        el.style.transform = '';
      });
    }
  });
}

function startQueueDrag(e, item, row) {
  e.preventDefault();
  row.querySelectorAll('.queue-popover').forEach(p => p.remove());
  openMenuKey = null; selectOpenKey = null;

  const rect = row.getBoundingClientRect();
  grabOffsetY = e.clientY - rect.top;
  draggingKey = queueKey(item);

  ghostEl = row.cloneNode(true);
  ghostEl.classList.add('queue-item-ghost');
  // The ghost lives on <body>, outside the list, so it does not inherit the poster size or the tighter row spacing the list gives its rows.
  // Carry both over, or the poster falls back to its picture's full size and the dragged row becomes huge.
  ghostEl.style.setProperty('--pw', getComputedStyle(row).getPropertyValue('--pw').trim() || '40px');
  if (document.getElementById('queueList').classList.contains('has-posters')) ghostEl.classList.add('has-posters');
  ghostEl.style.left = rect.left + 'px';
  ghostEl.style.top = rect.top + 'px';
  ghostEl.style.width = rect.width + 'px';
  document.body.appendChild(ghostEl);

  renderQueueList();

  document.addEventListener('pointermove', onQueueDragMove);
  document.addEventListener('pointerup', onQueueDragEnd);
}

function onQueueDragMove(e) {
  if (!ghostEl) return;
  ghostEl.style.top = (e.clientY - grabOffsetY) + 'px';

  const list = document.getElementById('queueList');
  const draggedIdx = displayItems.findIndex(i => queueKey(i) === draggingKey);

  for (const el of [...list.children]) {
    if (el.dataset.key === draggingKey) continue;

    const r = el.getBoundingClientRect();
    const mid = r.top + r.height / 2;
    const targetIdx = displayItems.findIndex(i => queueKey(i) === el.dataset.key);
    if ((e.clientY < mid && targetIdx < draggedIdx) || (e.clientY > mid && targetIdx > draggedIdx)) {
      const [moved] = displayItems.splice(draggedIdx, 1);
      displayItems.splice(targetIdx, 0, moved);
      renderQueueList();
      break;
    }
  }
}

async function onQueueDragEnd() {
  document.removeEventListener('pointermove', onQueueDragMove);
  document.removeEventListener('pointerup', onQueueDragEnd);

  const list = document.getElementById('queueList');
  const ph = [...list.children].find(el => el.dataset.key === draggingKey);
  if (ph && ghostEl) {
    const target = ph.getBoundingClientRect();
    ghostEl.style.transition = 'top 140ms cubic-bezier(.2,.8,.2,1)';
    ghostEl.style.top = target.top + 'px';
  }

  // The whole cross-lane list, in its current display order - a not-yet-started file from any
  // lane can be prioritized ahead of any other lane's now, matching the engine's own global
  // picking order. Error rows are excluded: they're never actually processed, and including one
  // here would let FindOrCreatePendingEntry silently create a duplicate, wrongly re-encodable
  // Pending entry for an already-failed file (a real bug, fixed server-side too as a backstop).
  const items = displayItems.filter(i => !i.isError).map(i => ({ laneId: i.laneId, fullName: i.fullName }));

  setTimeout(() => {
    if (ghostEl) { ghostEl.remove(); ghostEl = null; }
    draggingKey = null;
    renderQueueList();
  }, 150);

  await fetch('/api/run/queue/reorder', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ items })
  });
  poll();
}

// One-click alternative to dragging a row a long distance in a big queue - splices it to the
// front or back of the same displayItems list drag-to-reorder already maintains, then submits
// the exact same payload shape onQueueDragEnd does.
async function moveQueueItem(item, toTop) {
  const key = queueKey(item);
  const idx = displayItems.findIndex(i => queueKey(i) === key);
  if (idx === -1) return;

  const [moved] = displayItems.splice(idx, 1);
  if (toTop) {
    displayItems.unshift(moved);
  } else {
    displayItems.push(moved);
  }
  renderQueueList();

  const items = displayItems.filter(i => !i.isError).map(i => ({ laneId: i.laneId, fullName: i.fullName }));
  await fetch('/api/run/queue/reorder', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ items })
  });
}

async function removeErrorQueueEntry(laneId, fileName, fullName) {
  if (!confirm(`Remove '${fileName}' from the queue?\n\nThis only clears its tracked error status - the file itself is left untouched on disk, and a future scan can pick it back up as new.`)) return;

  await fetch('/api/run/queue/remove-error', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ laneId, fullName })
  });
  poll();
}

document.addEventListener('click', () => {
  if (openMenuKey !== null) {
    openMenuKey = null;
    renderQueueList();
  }
});

loadQueuePresetNames();
loadQueueLanes();

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

// "Sep 06 2026, 1:45PM" - a fixed format rather than toLocaleString, which varies by browser/
// locale and wouldn't reliably match this shape (extra spacing around AM/PM, different date
// ordering, etc.).
function formatCompletionTime(isoString) {
  const d = new Date(isoString);
  const month = MONTH_NAMES[d.getMonth()];
  const day = String(d.getDate()).padStart(2, '0');
  const year = d.getFullYear();
  let hours = d.getHours();
  const ampm = hours >= 12 ? 'PM' : 'AM';
  hours = hours % 12;
  if (hours === 0) hours = 12;
  const minutes = String(d.getMinutes()).padStart(2, '0');
  return `${month} ${day} ${year}, ${hours}:${minutes}${ampm}`;
}

// "2d 5h 36m" (not "02:05:36") - labeled units read unambiguously at a glance, where a
// colon-separated triple looks like a clock (HH:MM:SS) instead of a countdown. Adaptive: drops
// the "d" once under a day out, and "h" too once under an hour, so a near-finish queue reads
// "36m" instead of a cluttered "0d 0h 36m". Floored at 0 rather than going negative (the ETA
// hasn't been recomputed yet for that last few seconds' drift).
function formatCompletionDuration(isoString) {
  const diffMs = Math.max(0, new Date(isoString) - new Date());
  const totalMinutes = Math.floor(diffMs / 60000);
  const days = Math.floor(totalMinutes / 1440);
  const hours = Math.floor((totalMinutes % 1440) / 60);
  const minutes = totalMinutes % 60;

  if (days > 0) return `${days}d ${hours}h ${minutes}m`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}

async function poll() {
  const res = await fetch('/api/run/status');
  const s = await res.json();

  toggleIsMonitoring = s.isMonitoring;
  toggleIsStopping = s.isStopping;
  renderToggleButton();
  abortBtn.disabled = !s.isMonitoring && !s.isRunning;
  // Run Now works with monitoring on (it skips the countdown) or off (it runs one pass without starting monitoring);
  // it is only unavailable while a pass is already running or monitoring is stopping.
  runNowBtn.disabled = s.isRunning || s.isStopping;
  // A passthrough copy has no encoder process to freeze, so Pause is not offered while one runs (Abort still works).
  togglePauseIsRunning = s.isRunning && !s.isPassthrough;
  togglePauseIsPaused = s.isPaused;
  renderPauseButton();

  // "Held" only means something when there is something being held back - a queued file, or an encode
  // the schedule has frozen - not merely because it happens to be daytime with an empty queue.
  const scheduleHeld = !!(s.schedule && s.schedule.isHeld) && ((s.upNext && s.upNext.length > 0) || s.isPaused);
  runAnywayBtn.classList.toggle('hidden', !scheduleHeld);

  const stateValueEl = document.getElementById('stateValue');
  stateValueEl.textContent = s.isRenaming ? 'Renaming' : (s.isRunning ? 'Running' : (scheduleHeld ? 'Held' : (s.isMonitoring ? 'Watching' : 'Idle')));
  stateValueEl.classList.toggle('running', s.isRunning);
  document.getElementById('fileLabel').textContent = s.isRenaming
    ? `Renaming files with FileBot in Lane ${s.laneDisplayName}`
    : (s.isRunning && s.laneDisplayName)
      ? `${s.isPassthrough ? 'Copying' : 'Compressing'} File in Lane ${s.laneDisplayName}${s.isPassthrough ? ' (passthrough, not re-encoded)' : (s.presetName ? ` using ${s.engine === 'FFmpeg' ? 'ffmpeg profile' : 'preset'} ${s.presetName}` : '')}${s.landsInLaneName ? ` and lands in ${s.landsInLaneName}` : ''}`
      : 'Waiting for files';
  document.getElementById('fileValue').textContent = s.isRenaming ? '-' : (s.isRunning ? (s.fileName || '-') : '-');

  const hasPercent = s.progressPercent !== null && s.progressPercent !== undefined;
  document.getElementById('progressFill').style.width = `${hasPercent ? s.progressPercent : 0}%`;

  const subParts = [];
  if (s.fileTotal) subParts.push(`${s.fileIndex} of ${s.fileTotal}`);
  if (hasPercent) subParts.push(`${s.progressPercent.toFixed(1)}%`);
  if (s.progressFps) subParts.push(`${s.progressFps.toFixed(1)} fps`);
  if (s.progressEta) subParts.push(`ETA ${s.progressEta}`);
  document.getElementById('progressSub').textContent = subParts.join(' · ');

  const queueEtaLabelEl = document.getElementById('queueEtaLabel');
  const queueEtaValueEl = document.getElementById('queueEtaValue');
  if (!s.queueEtaText) {
    queueEtaLabelEl.textContent = '';
    queueEtaValueEl.textContent = '';
  } else if (s.queueEtaText === 'Estimating') {
    queueEtaLabelEl.textContent = 'Queue Completion:';
    queueEtaValueEl.textContent = 'Estimating';
  } else {
    queueEtaLabelEl.textContent = 'Queue Completion:';
    queueEtaValueEl.textContent = queueEtaFormat === 'Duration'
      ? formatCompletionDuration(s.queueEtaText)
      : formatCompletionTime(s.queueEtaText);
  }

  const posters = s.posters || {};
  posterConfig = { enabled: !!posters.enabled, smaller: !!posters.smaller };
  const queueListEl = document.getElementById('queueList');
  queueListEl.classList.remove('has-posters', 'poster-small', 'poster-medium', 'poster-large');
  if (posterConfig.enabled) queueListEl.classList.add('has-posters', 'poster-' + String(posters.size || 'Medium').toLowerCase());

  const jobPoster = document.getElementById('jobPoster');
  const jobKey = posterConfig.enabled && s.isRunning ? posters.currentKey : null;
  if (jobKey) {
    const src = posterSrc(jobKey, true);
    if (jobPoster.dataset.src !== src) {
      jobPoster.dataset.src = src;
      jobPoster.innerHTML = `<img alt="" decoding="async" draggable="false" src="${src}" onerror="this.parentNode.classList.add('hidden');this.parentNode.dataset.src=''">`;
    }
    jobPoster.classList.remove('hidden');
  } else {
    jobPoster.classList.add('hidden');
    jobPoster.dataset.src = '';
    jobPoster.innerHTML = '';
  }
  document.getElementById('statFile').classList.toggle('with-poster', !!jobKey);

  renderQueue(s.upNext);
  renderLog(s.recentLogLines);
}

poll();
setInterval(poll, 1500);

// "Finish setting up": a tool a configured lane needs isn't where Compressarr looks for it (HandBrakeCLI
// always; ffmpeg only once a lane uses it). Links to the Encoder page, where Check/Install lives.
async function checkSetup() {
  try {
    const dto = await (await fetch('/api/encoder')).json();
    const missing = [];
    for (const i of dto.validationIssues || []) {
      if (i.field === 'handBrakeCliPath') missing.push('HandBrake');
      else if (i.field === 'ffmpegPath' || i.field === 'ffmpegProbePath') { if (!missing.includes('ffmpeg')) missing.push('ffmpeg'); }
    }
    const banner = document.getElementById('setupBanner');
    if (missing.length === 0) { banner.classList.add('hidden'); return; }
    banner.innerHTML = `Finish setting up: ${missing.join(' and ')} ${missing.length === 1 ? "isn't" : "aren't"} installed where Compressarr looks for ${missing.length === 1 ? 'it' : 'them'}. <a href="/encoder.html">Open the Encoder page</a> to check or install ${missing.length === 1 ? 'it' : 'them'}.`;
    banner.classList.remove('hidden');
  } catch { /* best-effort - the banner is a convenience */ }
}
checkSetup();
setInterval(checkSetup, 60000);
