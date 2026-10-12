const THEME_STORAGE_KEY = 'compressarr.theme';

function getPreferredTheme() {
  const saved = localStorage.getItem(THEME_STORAGE_KEY);
  if (saved === 'light' || saved === 'dark') return saved;
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function applyTheme(theme) {
  document.documentElement.setAttribute('data-theme', theme);
}

function setTheme(theme) {
  localStorage.setItem(THEME_STORAGE_KEY, theme);
  applyTheme(theme);
}

// Line-style icons matching the approved sidebar mockup - kept as plain shape primitives
// (circles/rects/polylines), not hand-authored illustration paths.
const NAV_ICONS = {
  monitor: '<polygon points="5 3 19 12 5 21 5 3"></polygon>',
  lanes: '<rect x="3" y="4" width="7" height="16" rx="1"></rect><rect x="14" y="4" width="7" height="10" rx="1"></rect>',
  settings: '<circle cx="12" cy="12" r="3"></circle><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"></path>',
  notifications: '<path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9"></path><path d="M13.73 21a2 2 0 0 1-3.46 0"></path>',
  history: '<circle cx="12" cy="12" r="9"></circle><polyline points="12 7 12 12 15.5 14"></polyline>',
  about: '<circle cx="12" cy="12" r="9"></circle><line x1="12" y1="16" x2="12" y2="11.5"></line><circle cx="12" cy="8" r="0.6" fill="currentColor" stroke="none"></circle>',
  // Solid and always red (not currentColor) - unlike every other nav icon, this one shouldn't
  // fade/recolor with hover or active state, the same way a "Sponsor"/donate heart never does.
  donate: '<path d="M12 21s-6.72-4.35-9.34-8.02C.9 10.49 1.02 7.49 3.42 5.6c1.88-1.5 4.46-1.2 6 .5L12 9l2.58-2.9c1.54-1.7 4.12-2 6-.5 2.4 1.89 2.52 4.89.72 7.38C18.72 16.65 12 21 12 21z" fill="#ef4444" stroke="none"></path>'
};

function navIcon(name) {
  return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${NAV_ICONS[name]}</svg>`;
}

const CPU_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="6" y="6" width="12" height="12" rx="1"></rect><line x1="6" y1="10" x2="3" y2="10"></line><line x1="6" y1="14" x2="3" y2="14"></line><line x1="18" y1="10" x2="21" y2="10"></line><line x1="18" y1="14" x2="21" y2="14"></line><line x1="10" y1="6" x2="10" y2="3"></line><line x1="14" y1="6" x2="14" y2="3"></line><line x1="10" y1="18" x2="10" y2="21"></line><line x1="14" y1="18" x2="14" y2="21"></line></svg>';
const BELL_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"></path><path d="M13.73 21a2 2 0 0 1-3.46 0"></path></svg>';
const UPDATE_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"></circle><polyline points="16 12 12 8 8 12"></polyline><line x1="12" y1="16" x2="12" y2="8"></line></svg>';

function renderNav(activePage) {
  const links = [
    { href: '/monitor.html', label: 'Monitor', page: 'monitor', icon: 'monitor' },
    { href: '/lanes.html', label: 'Lanes', page: 'lanes', icon: 'lanes' },
    { href: '/index.html', label: 'Settings', page: 'settings', icon: 'settings' },
    { href: '/notifications.html', label: 'Notifications', page: 'notifications', icon: 'notifications' },
    { href: '/history.html', label: 'History', page: 'history', icon: 'history' },
    { href: '/about.html', label: 'About', page: 'about', icon: 'about' }
  ];

  // Rendered as a second, separate <nav> pinned to the bottom of the sidebar (see
  // .sidebar-nav-bottom's margin-top: auto) rather than appended to the main list above - a
  // Donate link belongs visually apart from the app's actual pages, not mixed into a growing list
  // of them.
  const bottomLinks = [
    { href: '/donate.html', label: 'Donate', page: 'donate', icon: 'donate' }
  ];

  // The page's real content is already sitting in <main> - moved into the new layout below
  // rather than rebuilt, so none of the per-page HTML/JS needs to change for this.
  const existingMain = document.querySelector('body > main');

  const appShell = document.createElement('div');
  appShell.className = 'app-shell';

  const sidebar = document.createElement('aside');
  sidebar.className = 'sidebar';
  sidebar.innerHTML = `
    <div class="sidebar-brand">
      <img src="/assets/logo.png" alt="Compressarr" />
      <span class="name">Compressarr</span>
    </div>
    <nav class="sidebar-nav">
      ${links.map(l => `<a href="${l.href}"${l.page === activePage ? ' class="active"' : ''}>${navIcon(l.icon)}<span>${l.label}</span>${l.page === 'history' ? '<span class="sidebar-badges" id="historyBadges"></span>' : ''}</a>`).join('')}
    </nav>
    <nav class="sidebar-nav sidebar-nav-bottom">
      ${bottomLinks.map(l => `<a href="${l.href}"${l.page === activePage ? ' class="active"' : ''}>${navIcon(l.icon)}<span>${l.label}</span></a>`).join('')}
    </nav>
  `;

  const mainCol = document.createElement('div');
  mainCol.className = 'main-col';

  // A page's own <h2 class="page-title"> (if it has one) becomes the toolbar title and is
  // removed from the content below it, so it isn't shown twice - falls back to this page's own
  // nav label for pages that never had a title heading of their own (Settings, About).
  const titleEl = existingMain ? existingMain.querySelector('.page-title') : null;
  const titleText = titleEl ? titleEl.textContent : (links.find(l => l.page === activePage)?.label ?? '');
  if (titleEl) titleEl.remove();

  // A page's own primary action buttons (if marked) move into the toolbar too, right next to
  // the title - pages without one (About) just get a title with no actions.
  const actionsEl = existingMain ? existingMain.querySelector('.page-actions') : null;

  const currentTheme = getPreferredTheme();
  const toolbar = document.createElement('div');
  toolbar.className = 'toolbar';
  toolbar.innerHTML = `
    <button type="button" class="hamburger-btn" id="hamburgerBtn" aria-label="Open menu"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="3" y1="6" x2="21" y2="6"></line><line x1="3" y1="12" x2="21" y2="12"></line><line x1="3" y1="18" x2="21" y2="18"></line></svg></button>
    <h1>${titleText}</h1>
    <span class="toolbar-spacer"></span>
    <div class="toolbar-global-status">
      <button type="button" id="announcementsBtn" class="toolbar-bell hidden" title="Announcements" aria-label="Announcements">${BELL_ICON}<span class="bell-dot hidden"></span></button>
      <a id="updateLink" class="toolbar-update hidden" target="_blank" rel="noopener" title="Update available">${UPDATE_ICON}</a>
      <span class="status-dot" id="statusDot"></span>
      <span id="monitoringState"></span>
      <span id="countdown" class="countdown"></span>
      <span class="toolbar-cpu" title="CPU Usage">${CPU_ICON}<span id="cpuValue">-</span></span>
    </div>
    <label class="theme-toggle">
      <span>&#9728;</span>
      <span class="theme-switch">
        <input type="checkbox" id="themeToggleInput" ${currentTheme === 'dark' ? 'checked' : ''} />
        <span class="slider"></span>
      </span>
      <span>&#127769;</span>
    </label>
  `;
  // The spacer takes the room between the title and the global status cluster - a page's own
  // action buttons (if any) sit inside it, left-aligned, same as the title. Pages with no
  // actions (About) just leave it empty.
  if (actionsEl) toolbar.querySelector('.toolbar-spacer').appendChild(actionsEl);

  // The one place every page's transient action feedback ("Saved.", "Testing...", a validation
  // error) is shown - see setStatusMessage below. Every page used to set this into its own local
  // element (#status, #presetStatus, a per-card .preset-status, ...); those are gone from the page
  // bodies now, so this toolbar span is the only target left.
  const toolbarStatus = document.createElement('span');
  toolbarStatus.className = 'toolbar-page-status';
  toolbar.querySelector('.toolbar-spacer').appendChild(toolbarStatus);
  toolbarStatusEl = toolbarStatus;

  // Wraps the page's remaining content in .content-inner (see styles.css) so <main> itself can
  // span the full column width for scrolling while the actual content still visually caps/centers
  // at 1080px, same as before.
  if (existingMain) {
    const contentInner = document.createElement('div');
    contentInner.className = 'content-inner';
    while (existingMain.firstChild) contentInner.appendChild(existingMain.firstChild);
    existingMain.appendChild(contentInner);
  }

  mainCol.appendChild(toolbar);
  if (existingMain) mainCol.appendChild(existingMain);

  // Off-canvas at narrow viewports (see styles.css's sidebar media query) - inert/invisible at
  // desktop width, where .sidebar is always visible in its normal flex position instead. Sits
  // between sidebar and mainCol in the DOM only so its z-index sits above mainCol but the sidebar
  // itself (also elevated at narrow widths) still layers above the scrim.
  const scrim = document.createElement('div');
  scrim.className = 'sidebar-scrim';
  scrim.id = 'sidebarScrim';

  appShell.appendChild(sidebar);
  appShell.appendChild(scrim);
  appShell.appendChild(mainCol);
  document.body.prepend(appShell);

  document.getElementById('themeToggleInput').addEventListener('change', e => {
    setTheme(e.target.checked ? 'dark' : 'light');
  });

  function closeSidebar() {
    sidebar.classList.remove('open');
    scrim.classList.remove('open');
  }
  document.getElementById('hamburgerBtn').addEventListener('click', () => {
    sidebar.classList.add('open');
    scrim.classList.add('open');
  });
  scrim.addEventListener('click', closeSidebar);
  // Only actually matters at narrow widths (desktop never opens the drawer in the first place),
  // but harmless to always attach - a real page navigation happens regardless of this class.
  sidebar.querySelectorAll('a').forEach(a => a.addEventListener('click', closeSidebar));

  renderHistoryBadges();
  startGlobalStatusPoll();
  registerServiceWorker();
  initInstallPrompt();
  checkForUpdate();
  loadAnnouncements();
}

// The announcements bell (see announcements.js) - loaded after the toolbar exists, so it can find its button.
function loadAnnouncements() {
  if (!document.getElementById('announcementsBtn')) return;
  const script = document.createElement('script');
  script.src = '/announcements.js';
  document.body.appendChild(script);
}

// ---- PWA: service worker registration + install support. Best-effort and must never break page
// load if unsupported/unavailable - a service worker in particular silently fails on any
// non-secure context other than localhost (see manifest.json's own real-world caveat: a phone
// opening Compressarr over a plain-http LAN address, not localhost, gets no install prompt and no
// service worker at all, by browser design, not a bug here). Install itself has no toolbar banner
// any more (removed once Settings > Web UI grew its own persistent "Install as app" card) - this
// file's only remaining job here is capturing beforeinstallprompt so that card has something to
// call, and exposing standalone/iOS detection for its other two states.
function registerServiceWorker() {
  if (!('serviceWorker' in navigator)) return;
  navigator.serviceWorker.register('/sw.js').catch(() => {
    // Best-effort - most commonly rejected because the page isn't in a secure context (see above).
  });
}

let deferredInstallPrompt = null;

function isRunningStandalone() {
  return window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
}

function isIosDevice() {
  return /iphone|ipad|ipod/i.test(navigator.userAgent) && !window.MSStream;
}

// Exposed unconditionally (regardless of standalone state) so Settings' own "Install as app" card
// can drive the install flow, and detect standalone/iOS, without duplicating any of this file's
// beforeinstallprompt/iOS-detection logic. There is deliberately no uninstall() counterpart here -
// no browser exposes a web-page-triggerable "uninstall this PWA" API at all (letting a site
// force-remove an installed app would be an obvious abuse vector), so Settings can only ever show
// instructions for the browser's own uninstall UI, never a working button.
window.CompressarrPwa = {
  isStandalone: isRunningStandalone,
  isIos: isIosDevice,
  canInstall: () => deferredInstallPrompt !== null,
  promptInstall: async () => {
    if (!deferredInstallPrompt) return false;
    deferredInstallPrompt.prompt();
    const choice = await deferredInstallPrompt.userChoice; // single-use regardless of accept/dismiss
    deferredInstallPrompt = null;
    return choice.outcome === 'accepted';
  }
};

function initInstallPrompt() {
  // Chrome/Edge/Android - fires only once the browser itself decides the page meets its
  // installability criteria (valid manifest + registered service worker + secure context), and
  // never fires at all once already installed/running standalone. preventDefault() suppresses the
  // browser's own mini-infobar - Settings' "Install as app" card is the only install UI now.
  window.addEventListener('beforeinstallprompt', e => {
    e.preventDefault();
    deferredInstallPrompt = e;
    window.dispatchEvent(new CustomEvent('compressarr-install-available'));
  });
}

// Update indicator in the toolbar - visible on every page, same reasoning as the monitoring
// status cluster above. The actual GitHub check runs server-side (IUpdateCheckService), on a
// background loop that checks immediately on app startup and roughly once a day after that - this
// just renders whatever the server currently knows, on every page load. Deliberately not cached
// client-side any more: an in-browser cache would keep showing a stale "update available" for up
// to a day after an upgrade actually resolved it (the app restarting - the one moment an upgrade
// always causes - is exactly when the server-side check re-runs and clears it), and a plain local
// API call is cheap enough to just make every time.
function renderUpdateIndicator(result) {
  const link = document.getElementById('updateLink');
  if (!link) return; // toolbar not built yet, or this raced a navigation

  if (result && result.hasUpdate && result.releaseUrl) {
    link.href = result.releaseUrl;
    link.classList.remove('hidden');
  } else {
    link.classList.add('hidden');
  }
}

function checkForUpdate() {
  fetch('/api/about/update-status')
    .then(res => res.json())
    .then(renderUpdateIndicator)
    .catch(() => {}); // best-effort - server unreachable just means the indicator stays whatever it already was
}

// Counts runs (within the History page's own retention window, and not yet seen - see
// /api/history/viewed-through) that had at least one error or at least one post-process warning -
// same data /api/history/reports already exposes, just aggregated here since every page (not only
// History) shows the sidebar. Best-effort: if this fails, the badges just don't appear rather than
// breaking the rest of the nav.
function renderHistoryBadges() {
  const holder = document.getElementById('historyBadges');
  if (!holder) return;

  Promise.all([
    fetch('/api/history/reports').then(res => res.json()),
    fetch('/api/history/viewed-through').then(res => res.json())
  ])
    .then(([entries, viewed]) => {
      const unseen = entries.filter(e => e.runNumber > viewed.runNumber);
      const errorRuns = unseen.filter(e => e.errorCount > 0).length;
      const warningRuns = unseen.filter(e => e.warningCount > 0).length;
      const parts = [];
      if (errorRuns > 0) parts.push(`<span class="sidebar-badge err" title="${errorRuns} unseen run(s) with errors">${errorRuns}</span>`);
      if (warningRuns > 0) parts.push(`<span class="sidebar-badge warn" title="${warningRuns} unseen run(s) with warnings">${warningRuns}</span>`);
      holder.innerHTML = parts.join('');
    })
    .catch(() => {});
}

// Monitoring status, countdown, and CPU are shown in the toolbar on every page (not just
// Monitor), so this owns its own independent poll of /api/run/status rather than relying on
// monitor.js, which only loads on monitor.html. On Monitor itself, monitor.js still owns the
// queue/log/current-file detail and button enable/disable from its own poll of the same
// endpoint - two lightweight polls of the same cheap endpoint, not worth sharing one across
// scripts for.
let globalNextRunAtMs = null;
const GLOBAL_STOPPING_MESSAGE = 'Stopping monitor after current task completes';

function renderGlobalCountdown() {
  const el = document.getElementById('countdown');
  if (!el) return;
  if (globalNextRunAtMs === null) {
    el.textContent = '';
    return;
  }
  const secondsLeft = Math.max(0, Math.ceil((globalNextRunAtMs - Date.now()) / 1000));
  el.textContent = `Next Pass in: ${secondsLeft} Seconds`;
}

// "H hrs, M min S sec" - drops the leading unit(s) once they're zero (matches
// formatCompletionDuration's own adaptive-unit-dropping on the Monitor page's Queue Completion
// stat), down to just "S sec". The comma sits only after "hrs" (Settings/User's own spec), never
// after "min". Ticks every second (see renderMonitoringState below), not just once per ~1.5s
// poll, so the seconds actually count up smoothly instead of jumping unevenly.
function formatElapsedTime(startedAtMs) {
  const totalSeconds = Math.max(0, Math.floor((Date.now() - startedAtMs) / 1000));
  const hrs = Math.floor(totalSeconds / 3600);
  const mins = Math.floor((totalSeconds % 3600) / 60);
  const secs = totalSeconds % 60;

  if (hrs > 0) return `${hrs} hrs, ${mins} min ${secs} sec`;
  if (mins > 0) return `${mins} min ${secs} sec`;
  return `${secs} sec`;
}

// Raw fields from the last successful poll - rendered on every poll AND on the same 1-second
// ticker renderGlobalCountdown already uses, so the elapsed-time seconds count up smoothly
// between polls instead of only updating (and visibly jumping) once every ~1.5s.
let globalIsStopping = false;
let globalIsMonitoring = false;
let globalIsPaused = false;
let globalIsRunning = false;
let globalRunStartedAtMs = null;

function renderMonitoringState() {
  const stateEl = document.getElementById('monitoringState');
  if (!stateEl) return;

  const runningSuffix = globalIsRunning && globalRunStartedAtMs !== null
    ? `: Running (Time Elapsed: ${formatElapsedTime(globalRunStartedAtMs)})`
    : (globalIsRunning ? ': Running' : '');
  stateEl.textContent = globalIsStopping
    ? GLOBAL_STOPPING_MESSAGE
    : (globalIsMonitoring ? `Monitoring is ON${globalIsPaused ? ': Paused' : runningSuffix}` : 'Monitoring is OFF');
}

async function pollGlobalStatus() {
  const dot = document.getElementById('statusDot');
  const cpuEl = document.getElementById('cpuValue');
  if (!dot || !document.getElementById('monitoringState') || !cpuEl) return; // toolbar not built yet, or this tick raced a navigation

  let s;
  try {
    const res = await fetch('/api/run/status');
    s = await res.json();
  } catch {
    return; // best-effort - leave the last-known values showing rather than blank them out
  }

  const isActive = s.isMonitoring && !s.isStopping;
  const isPaused = isActive && s.isPaused;
  dot.classList.toggle('on', isActive && !isPaused);
  dot.classList.toggle('off', !isActive);
  dot.classList.toggle('paused', isPaused);

  globalIsStopping = s.isStopping;
  globalIsMonitoring = s.isMonitoring;
  globalIsPaused = isPaused;
  globalIsRunning = s.isRunning;
  globalRunStartedAtMs = s.isRunning && s.runStartedUtc ? new Date(s.runStartedUtc).getTime() : null;
  renderMonitoringState();

  globalNextRunAtMs = (s.isMonitoring && s.secondsUntilNextRun !== null && s.secondsUntilNextRun !== undefined)
    ? Date.now() + s.secondsUntilNextRun * 1000
    : null;
  renderGlobalCountdown();

  cpuEl.textContent = (s.cpuUsagePercent === null || s.cpuUsagePercent === undefined) ? 'unavailable' : `${Math.round(s.cpuUsagePercent)}%`;
}

function startGlobalStatusPoll() {
  pollGlobalStatus();
  setInterval(pollGlobalStatus, 1500);
  setInterval(renderGlobalCountdown, 1000);
  setInterval(renderMonitoringState, 1000);
}

// Applied immediately (before renderNav runs) so the page never flashes the wrong theme.
applyTheme(getPreferredTheme());

// .help-tip is position: fixed (see styles.css) specifically so it can escape .card's
// overflow: hidden - a tip near the top of a tall card would otherwise render partly above the
// card and get silently clipped. Since fixed positioning is viewport-relative, not
// anchor-relative, its top/left have to be computed here on every hover/focus rather than left to
// CSS. Delegated on document (not per-.help-tag listeners) so this works for any card added to the
// DOM after this script runs, e.g. notifications.js's channel cards.
function positionHelpTip(tag) {
  const tip = tag.querySelector('.help-tip');
  if (!tip) return;
  const tagRect = tag.getBoundingClientRect();
  const tipRect = tip.getBoundingClientRect(); // visibility: hidden still measures, unlike display: none

  let top = tagRect.top - tipRect.height - 6; // default: above the tag
  if (top < 4) top = tagRect.bottom + 6; // not enough room above - flip below instead

  let left = tagRect.left;
  const maxLeft = window.innerWidth - tipRect.width - 8;
  if (left > maxLeft) left = Math.max(8, maxLeft); // keep it on-screen against the right edge

  tip.style.top = `${top}px`;
  tip.style.left = `${left}px`;
}

document.addEventListener('mouseover', e => {
  const tag = e.target.closest('.help-tag');
  if (tag) positionHelpTip(tag);
});
document.addEventListener('focusin', e => {
  const tag = e.target.closest('.help-tag');
  if (tag) positionHelpTip(tag);
});

// The single element every page's transient action feedback is shown in - set by renderNav()
// above, since it builds the toolbar before any page script's own status-related code runs.
let toolbarStatusEl = null;

// The one status-message convention every page uses - every page used to keep its own local
// element(s) for this (#status, #presetStatus, a per-card .preset-status, etc); those are gone
// now, so this always targets the shared toolbar span. kind: 'success' (green, auto-clears after
// 4s - a success message left up forever is easy to miss/mistake for stale), 'error' (red via
// --err, stays up until the next call - a broken-config error shouldn't silently disappear while
// it's still true), or '' (neutral, e.g. "Saving..." - stays up, no color). Since there's only
// one target now, whichever page/card/button set a message last is simply what's showing -
// there's no per-widget status to preserve across two things happening close together.
function setStatusMessage(text, kind) {
  const el = toolbarStatusEl;
  if (!el) return;
  clearTimeout(el._statusClearTimer);
  el.textContent = text;
  el.classList.toggle('success', kind === 'success');
  el.classList.toggle('error', kind === 'error');

  if (kind === 'success') {
    el._statusClearTimer = setTimeout(() => {
      el.textContent = '';
      el.classList.remove('success');
    }, 4000);
  }
}

// Removes the red outline/tooltip left by a previous applyValidationIssues call. fieldMap:
// { logicalKey: cssSelector }, resolved within `container` (the whole document for a
// single-form page; one lane card's own node for Lanes, since field ids repeat per card).
function clearValidationIssues(container, fieldMap) {
  for (const key of Object.keys(fieldMap)) {
    const el = container.querySelector(fieldMap[key]);
    if (!el) continue;
    el.classList.remove('field-invalid');
    el.removeAttribute('title');
  }
}

// issues: [{ field, message }, ...] (a ValidationIssueDto list from the server, or - for
// Notifications, which validates required fields client-side before ever calling the server -
// a locally-built list shaped the same way). Outlines each matching field red with its specific
// message as a hover tooltip, and shows `genericMessage` via setStatusMessage if there's anything
// to report (pass '' to skip - e.g. when a caller is looping over several cards and wants to set
// one aggregate message itself afterward, not one per card). Returns true if there were any
// issues, so the caller knows whether to fall through to its own normal success message instead.
function applyValidationIssues(container, issues, fieldMap, genericMessage) {
  clearValidationIssues(container, fieldMap);
  if (!issues || issues.length === 0) return false;

  for (const issue of issues) {
    const selector = fieldMap[issue.field];
    if (!selector) continue;
    const el = container.querySelector(selector);
    if (!el) continue;
    el.classList.add('field-invalid');
    el.title = el.title ? `${el.title}\n${issue.message}` : issue.message;
  }

  if (genericMessage) setStatusMessage(genericMessage, 'error');
  return true;
}
