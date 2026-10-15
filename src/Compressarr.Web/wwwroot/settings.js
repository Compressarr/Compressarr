renderNav('settings');

// Thin wrapper over nav.js's shared setStatusMessage - every call site below already passes a
// plain success boolean, not a 'success'/'error'/'' kind. Every status message on this page
// (Save, Install/Reload Presets, Fix Network, Arr connection tests, Import, maintenance actions)
// goes through this one function into the toolbar - there's no per-section status left in the
// page body any more.
// success: true = green, 'error' = red, anything else = plain (progress such as "Saving...").
function setStatus(text, success) { setStatusMessage(text, success === 'error' ? 'error' : success ? 'success' : ''); }

// Logical validation-issue field key -> the actual input it lives on (see ValidationIssue's own
// note on why these can differ).
const SETTINGS_FIELD_MAP = {
  tmdbApiKey: '#tmdbApiKey',
  fileBotCliPath: '#fileBotCliPath',
  vidTypes: '#vidTypes'
};

function applySettingsValidation(issues) {
  return applyValidationIssues(document, issues, SETTINGS_FIELD_MAP, 'Error in base configuration, check fields below.');
}

document.getElementById('fileBotFixNetworkBtn').addEventListener('click', async () => {
  setStatus('Applying FileBot network fix...');
  const res = await fetch('/api/filebot/fix-network', { method: 'POST' });
  const body = await res.json().catch(() => ({}));
  setStatus(body.message || 'Fix failed - see the recent log for details.', body.success ? true : 'error');
});

const MIN_SIZE_UNIT_MULTIPLIERS = { KB: 1024, MB: 1024 * 1024, GB: 1024 * 1024 * 1024 };

// Tracks whatever unit the dropdown last resolved to, so the change handler below can convert
// the displayed number from that unit to the newly-selected one instead of reinterpreting the
// same digits in a different unit (which would silently change the underlying byte value).
let minSizeUnit = 'MB';

function minSizeBytesFromForm() {
  const raw = parseFloat(document.getElementById('minSizeBytes').value) || 0;
  return Math.round(raw * MIN_SIZE_UNIT_MULTIPLIERS[minSizeUnit]);
}

document.getElementById('minSizeUnit').addEventListener('change', e => {
  const bytes = minSizeBytesFromForm();
  minSizeUnit = e.target.value;
  document.getElementById('minSizeBytes').value = bytes / MIN_SIZE_UNIT_MULTIPLIERS[minSizeUnit];
});

// A convenience shortcut, not a stored setting of its own - swaps whichever numbering binding is
// currently sitting in the TV Arguments text for the newly-picked one, so the Arguments field
// stays the single source of truth for the real command at all times.
document.getElementById('fileBotTvEpisodeFormat').addEventListener('change', e => {
  const argsEl = document.getElementById('fileBotTvArgs');
  const newFormat = e.target.value;
  const otherFormat = newFormat === '{s00e00}' ? '{sxe}' : '{s00e00}';
  if (argsEl.value.includes(otherFormat)) {
    argsEl.value = argsEl.value.split(otherFormat).join(newFormat);
  }
});

function fillForm(dto) {
  document.getElementById('fileBotEnabled').checked = dto.fileBotEnabled;
  document.getElementById('fileBotCliPath').value = dto.fileBotCliPath;
  document.getElementById('fileBotTvEnabled').checked = dto.fileBotTvEnabled;
  document.getElementById('fileBotTvArgs').value = dto.fileBotTvArgs;
  // The dropdown has no state of its own to persist - it just reflects whichever binding is
  // currently sitting in the Arguments text, so it stays in sync with hand-edits too.
  document.getElementById('fileBotTvEpisodeFormat').value = dto.fileBotTvArgs.includes('{sxe}') ? '{sxe}' : '{s00e00}';
  document.getElementById('fileBotMovieEnabled').checked = dto.fileBotMovieEnabled;
  document.getElementById('fileBotMovieArgs').value = dto.fileBotMovieArgs;
  document.getElementById('vidTypes').value = dto.vidTypes.join(', ');
  minSizeUnit = 'MB';
  document.getElementById('minSizeUnit').value = minSizeUnit;
  document.getElementById('minSizeBytes').value = (dto.minSizeBytes || 0) / MIN_SIZE_UNIT_MULTIPLIERS[minSizeUnit];
  document.getElementById('limit').value = dto.limit;
  document.getElementById('companionExtensions').value = dto.companionExtensions.join(', ');
  document.getElementById('unmatchedCompanionAction').value = dto.unmatchedCompanionAction;
  document.getElementById('onDestinationCollision').value = dto.onDestinationCollision;
  document.getElementById('outSameAsIn').checked = dto.outSameAsIn;
  document.getElementById('moveFiles').checked = dto.moveFiles;
  document.getElementById('clearTitleMetadata').checked = dto.clearTitleMetadata;
  document.getElementById('deleteAfterConvert').value = dto.deleteAfterConvert;
  document.getElementById('logFilePath').value = dto.logFilePath;
  document.getElementById('reportPath').value = dto.reportPath;
  document.getElementById('retentionDays').value = dto.retentionDays;
  document.getElementById('keepSuccessfulHandBrakeLogs').checked = dto.keepSuccessfulHandBrakeLogs;
  document.getElementById('openAfterRun').value = dto.openAfterRun;
  document.getElementById('repeatMonitor').checked = dto.repeatMonitor;
  document.getElementById('launchMonitorAtStartup').checked = dto.launchMonitorAtStartup;
  document.getElementById('pollIntervalSeconds').value = dto.pollIntervalSeconds;
  document.getElementById('queueEtaFormat').value = dto.queueEtaFormat;
  document.getElementById('runAtLogin').checked = dto.runAtLogin;
  document.getElementById('announcementsEnabled').checked = dto.announcementsEnabled;
  const posters = dto.posters || { enabled: false, tmdbApiKey: '', size: 'Medium', smallerImages: false };
  document.getElementById('postersEnabled').checked = posters.enabled;
  document.getElementById('tmdbApiKey').value = posters.tmdbApiKey;
  document.getElementById('posterSize').value = posters.size;
  document.getElementById('posterSmaller').checked = posters.smallerImages;
  document.getElementById('updateChannel').value = dto.updateChannel || 'Stable';
  document.getElementById('postExecCmd').value = dto.postExecCmd;
  document.getElementById('postExecArgs').value = dto.postExecArgs;
  document.getElementById('sonarrEnabled').checked = dto.sonarr.enabled;
  document.getElementById('sonarrUrl').value = dto.sonarr.url;
  document.getElementById('sonarrApiKey').value = dto.sonarr.apiKey;
  document.getElementById('radarrEnabled').checked = dto.radarr.enabled;
  document.getElementById('radarrUrl').value = dto.radarr.url;
  document.getElementById('radarrApiKey').value = dto.radarr.apiKey;
  document.getElementById('webPort').value = dto.webPort;
  document.getElementById('backupFolderPath').value = dto.backupFolderPath;
  document.getElementById('backupIntervalDays').value = dto.backupIntervalDays;
  document.getElementById('backupRetentionDays').value = dto.backupRetentionDays;
  setLastBackupLabel(formatLastBackup(dto.backupLastRunUtc));
  loadBackupList();
}

function formatLastBackup(lastRunUtc) {
  return lastRunUtc ? `Last backup: ${new Date(lastRunUtc).toLocaleString()}` : 'No backup yet.';
}

function readForm() {
  return {
    fileBotEnabled: document.getElementById('fileBotEnabled').checked,
    fileBotCliPath: document.getElementById('fileBotCliPath').value,
    fileBotTvEnabled: document.getElementById('fileBotTvEnabled').checked,
    fileBotTvArgs: document.getElementById('fileBotTvArgs').value,
    fileBotMovieEnabled: document.getElementById('fileBotMovieEnabled').checked,
    fileBotMovieArgs: document.getElementById('fileBotMovieArgs').value,
    vidTypes: document.getElementById('vidTypes').value.split(',').map(s => s.trim()).filter(Boolean),
    outSameAsIn: document.getElementById('outSameAsIn').checked,
    deleteAfterConvert: document.getElementById('deleteAfterConvert').value,
    moveFiles: document.getElementById('moveFiles').checked,
    clearTitleMetadata: document.getElementById('clearTitleMetadata').checked,
    limit: parseInt(document.getElementById('limit').value, 10) || 0,
    minSizeBytes: minSizeBytesFromForm(),
    companionExtensions: document.getElementById('companionExtensions').value.split(',').map(s => s.trim()).filter(Boolean),
    unmatchedCompanionAction: document.getElementById('unmatchedCompanionAction').value,
    onDestinationCollision: document.getElementById('onDestinationCollision').value,
    logFilePath: document.getElementById('logFilePath').value,
    retentionDays: parseInt(document.getElementById('retentionDays').value, 10) || 0,
    keepSuccessfulHandBrakeLogs: document.getElementById('keepSuccessfulHandBrakeLogs').checked,
    postExecCmd: document.getElementById('postExecCmd').value,
    postExecArgs: document.getElementById('postExecArgs').value,
    reportPath: document.getElementById('reportPath').value,
    openAfterRun: document.getElementById('openAfterRun').value,
    repeatCount: 0,
    repeatMonitor: document.getElementById('repeatMonitor').checked,
    launchMonitorAtStartup: document.getElementById('launchMonitorAtStartup').checked,
    pollIntervalSeconds: parseInt(document.getElementById('pollIntervalSeconds').value, 10) || 60,
    queueEtaFormat: document.getElementById('queueEtaFormat').value,
    sonarr: {
      enabled: document.getElementById('sonarrEnabled').checked,
      url: document.getElementById('sonarrUrl').value,
      apiKey: document.getElementById('sonarrApiKey').value
    },
    radarr: {
      enabled: document.getElementById('radarrEnabled').checked,
      url: document.getElementById('radarrUrl').value,
      apiKey: document.getElementById('radarrApiKey').value
    },
    webPort: parseInt(document.getElementById('webPort').value, 10) || 1212,
    runAtLogin: document.getElementById('runAtLogin').checked,
    announcementsEnabled: document.getElementById('announcementsEnabled').checked,
    posters: {
      enabled: document.getElementById('postersEnabled').checked,
      tmdbApiKey: document.getElementById('tmdbApiKey').value.trim(),
      size: document.getElementById('posterSize').value,
      smallerImages: document.getElementById('posterSmaller').checked
    },
    updateChannel: document.getElementById('updateChannel').value,
    backupFolderPath: document.getElementById('backupFolderPath').value,
    backupIntervalDays: parseInt(document.getElementById('backupIntervalDays').value, 10) || 7,
    backupRetentionDays: parseInt(document.getElementById('backupRetentionDays').value, 10) || 28,
    // Read-only from the client - ConfigMapping.ApplySettingsDto never reads this field back in
    // (only BackupService itself sets it, after a real backup runs), so what's sent here doesn't
    // matter; the DTO record just requires a value.
    backupLastRunUtc: null
  };
}

async function loadSettings() {
  const res = await fetch('/api/settings');
  const dto = await res.json();
  fillForm(dto);
  settingsDirty = false;
  applySettingsValidation(dto.validationIssues);
}

document.getElementById('saveBtn').addEventListener('click', async () => {
  setStatus('Saving...');
  const res = await fetch('/api/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(readForm())
  });
  if (res.ok) {
    const dto = await res.json();
    settingsDirty = false;
    // The toolbar (announcements bell, update arrow) follows what was just saved without a page change.
    document.dispatchEvent(new CustomEvent('compressarr:settings-saved'));
    if (!applySettingsValidation(dto.validationIssues)) {
      setStatus('Settings saved.', true);
    }
  } else {
    setStatus('Failed to save settings.', 'error');
  }
});

document.getElementById('clearChangesBtn').addEventListener('click', async () => {
  if (settingsDirty && !confirm('Discard unsaved changes and reload the last saved settings?')) return;
  await loadSettings();
  setStatus('Changes cleared.', true);
});

// Warn before leaving with unsaved edits - covers tab close/reload and sidebar nav clicks alike,
// since the sidebar's links are plain <a href> navigation (no client-side router intercepting
// them), so both are a real page unload beforeunload actually fires for. importConfigFile is
// excluded - picking a file to import isn't itself an unsaved settings change, and Import already
// has its own confirm() before it does anything.
let settingsDirty = false;
const settingsMain = document.querySelector('main');
for (const eventName of ['input', 'change']) {
  settingsMain.addEventListener(eventName, e => {
    if (e.target.id === 'importConfigFile') return;
    settingsDirty = true;
  });
}
window.addEventListener('beforeunload', e => {
  if (!settingsDirty) return;
  e.preventDefault();
  e.returnValue = '';
});

document.getElementById('runOnceBtn').addEventListener('click', async () => {
  setStatus('Running...');
  const res = await fetch('/api/run/once', { method: 'POST' });
  const body = await res.json();
  if (res.ok) {
    setStatus(`Done: ${body.totalFiles} file(s) processed.`, true);
  } else {
    setStatus(body.message || 'Run failed.', 'error');
  }
});

async function testArrConnection(service) {
  setStatus('Testing...');

  const res = await fetch('/api/arr/test', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      enabled: true,
      url: document.getElementById(`${service}Url`).value,
      apiKey: document.getElementById(`${service}ApiKey`).value
    })
  });
  const body = await res.json();
  setStatus(body.message, body.success ? true : 'error');
  if (service === 'radarr' && body.success) refreshRadarrFolderNote();
}

// ---- Radarr's "Delete empty folders": the note always says what to do; once Radarr has been asked (Test Connection, or the button) the
// ---- card says what it found, and the button goes away when the setting is already off.
const RADARR_FOLDER_NOTE = 'To ensure Compressarr acts properly with Radarr, please uncheck "Delete empty folders" in Settings, Media Management, Folders.';

function radarrCredentials() {
  return { enabled: true, url: document.getElementById('radarrUrl').value, apiKey: document.getElementById('radarrApiKey').value };
}

async function refreshRadarrFolderNote() {
  const note = document.getElementById('radarrFolderNote');
  const button = document.getElementById('radarrFolderBtn');
  try {
    const res = await fetch('/api/arr/radarr/delete-empty-folders/check', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(radarrCredentials())
    });
    const body = await res.json();
    if (!body.success || body.deleteEmptyFolders === null || body.deleteEmptyFolders === undefined) return;   // unknown: leave the note as it is
    if (body.deleteEmptyFolders) {
      note.textContent = RADARR_FOLDER_NOTE;
      button.classList.remove('hidden');
    } else {
      note.textContent = 'Radarr\'s "Delete empty folders" is off, as Compressarr needs.';
      button.classList.add('hidden');
    }
  } catch { /* best-effort: the note stays as it was */ }
}

document.getElementById('radarrFolderBtn').addEventListener('click', async () => {
  if (!confirm('Switch off "Delete empty folders" in Radarr (Settings > Media Management > Folders)? Nothing else in Radarr is changed.')) return;
  setStatus('Changing it in Radarr...');
  const res = await fetch('/api/arr/radarr/delete-empty-folders/disable', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(radarrCredentials())
  });
  const body = await res.json();
  setStatus(body.message, body.success ? true : 'error');
  if (body.success) refreshRadarrFolderNote();
});

// ---- Queue Posters: Test Key checks what is typed (saved or not); the cache line says what is stored and whether TMDB is refusing the key.
document.getElementById('tmdbTestBtn').addEventListener('click', async () => {
  setStatus('Testing...');
  const res = await fetch('/api/posters/test', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ apiKey: document.getElementById('tmdbApiKey').value.trim() })
  });
  const body = await res.json();
  setStatus(body.message, body.success ? true : 'error');
});

async function refreshPosterCacheInfo() {
  const el = document.getElementById('posterCacheInfo');
  try {
    const info = await (await fetch('/api/posters/info')).json();
    const size = info.bytes >= 1048576 ? (info.bytes / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(info.bytes / 1024)) + ' KB';
    el.textContent = (info.posters === 0 ? 'No posters saved yet.' : `${info.posters} poster${info.posters === 1 ? '' : 's'} saved (${size}).`)
      + (info.problem ? ' ' + info.problem : '');
  } catch { el.textContent = ''; }
}

document.getElementById('posterClearBtn').addEventListener('click', async () => {
  if (!confirm('Delete all saved posters? They are downloaded again when needed.')) return;
  const body = await (await fetch('/api/posters/clear', { method: 'POST' })).json();
  setStatus(body.removed === 0 ? 'No saved posters to clear.' : `Cleared ${body.removed} saved poster${body.removed === 1 ? '' : 's'}.`, true);
  refreshPosterCacheInfo();
});

refreshPosterCacheInfo();

document.getElementById('sonarrTestBtn').addEventListener('click', () => testArrConnection('sonarr'));
document.getElementById('radarrTestBtn').addEventListener('click', () => testArrConnection('radarr'));

document.getElementById('exportConfigBtn').addEventListener('click', () => {
  // Content-Disposition: attachment (set by Results.File's fileDownloadName on the server) makes
  // the browser download this instead of navigating to it, so a plain location change is enough
  // - no anchor/blob juggling needed.
  window.location.href = '/api/settings/export';
});

document.getElementById('importConfigBtn').addEventListener('click', () => {
  const fileInput = document.getElementById('importConfigFile');
  const file = fileInput.files[0];
  if (!file) {
    setStatus('Choose a file to import first.', 'error');
    return;
  }

  if (!confirm('Import this file? It will replace all current settings and lanes.')) return;

  setStatus('Importing...');

  const reader = new FileReader();
  reader.onload = async () => {
    const res = await fetch('/api/settings/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: reader.result
    });
    if (res.ok) {
      setStatus('Config imported.', true);
      fileInput.value = '';
      loadSettings();
    } else {
      const body = await res.json().catch(() => ({}));
      setStatus(body.message || 'Failed to import config.', 'error');
    }
  };
  reader.readAsText(file);
});

async function runMaintenanceAction(url, confirmMessage, successMessage) {
  if (!confirm(confirmMessage)) return;

  setStatus('Working...');

  const res = await fetch(url, { method: 'POST' });
  setStatus(res.ok ? successMessage : 'Failed - see the recent log for details.', res.ok ? true : 'error');

  // Every maintenance action only ever writes a file - reloading the form (which re-fetches
  // /api/settings) is how the effect actually shows up immediately, no app restart needed. A
  // harmless no-op for Reset Resume File/Clean Up Now, essential for Clear Configuration.
  if (res.ok) loadSettings();
}

document.getElementById('resetResumeBtn').addEventListener('click', () => runMaintenanceAction(
  '/api/maintenance/reset-resume',
  'Clear every tracked resume-state entry?\n\nThe next pass will do a completely fresh scan of every lane\'s Input folder instead of resuming or skipping anything.',
  'Resume file cleared.'
));

document.getElementById('cleanupNowBtn').addEventListener('click', () => runMaintenanceAction(
  '/api/maintenance/cleanup-now',
  'Clean up old logs and reports now, using the retention setting above?\n\nRemoved files go to the Recycle Bin.',
  'Old logs and reports cleaned up.'
));

document.getElementById('clearLogsBtn').addEventListener('click', () => runMaintenanceAction(
  '/api/maintenance/clear-logs',
  'Delete every log file right now, regardless of age?\n\nRemoved files go to the Recycle Bin.',
  'Logs cleared.'
));

document.getElementById('clearHistoryBtn').addEventListener('click', () => runMaintenanceAction(
  '/api/maintenance/clear-history',
  'Delete every HTML report and the run-history CSV - everything the History page shows?\n\nRemoved files go to the Recycle Bin.',
  'History cleared.'
));

document.getElementById('purgeLogsReportsBtn').addEventListener('click', () => runMaintenanceAction(
  '/api/maintenance/purge-logs-reports',
  'Permanently delete every log file, every HTML report, and the run-history CSV?\n\nThis does NOT go to the Recycle Bin - it cannot be undone.',
  'Logs and reports purged.'
));

document.getElementById('clearAllBtn').addEventListener('click', () => runMaintenanceAction(
  '/api/maintenance/clear-all',
  'Delete everything except Settings and Lanes - every report, every log, and all tracked resume state?\n\nThe run counter is left untouched. Removed files go to the Recycle Bin.',
  'Everything except Settings and Lanes cleared.'
));

document.getElementById('resetLanesBtn').addEventListener('click', () => runMaintenanceAction(
  '/api/maintenance/reset-lanes',
  'Delete ALL configured lanes and replace them with a single new, blank lane?\n\nThis cannot be undone unless you\'ve exported a backup first.',
  'Lanes reset to a single new lane.'
));

document.getElementById('clearConfigBtn').addEventListener('click', () => runMaintenanceAction(
  '/api/maintenance/clear-config',
  'Reset ALL settings and lanes back to defaults?\n\nThis cannot be undone unless you\'ve exported a backup first.',
  'Configuration reset to defaults.'
));

// Not a transient action-feedback message like everything else on this page - this is a
// persistent fact ("Last backup: <when>", or "No backup yet.") that stays visible next to the
// Backup Now button regardless of whatever else the toolbar is showing, so it doesn't belong in
// the toolbar's own "last message wins" rotation. Set on load (see fillForm) and refreshed after
// a successful backup/restore.
function setLastBackupLabel(text) {
  document.getElementById('automatedBackupStatus').textContent = text;
}

document.querySelector('.browse-btn[data-target="backupFolderPath"]').addEventListener('click', () => {
  const field = document.getElementById('backupFolderPath');
  openFolderBrowser(field.value, chosenPath => {
    field.value = chosenPath;
    loadBackupList(); // a folder was just deliberately chosen - show what's actually in it
  });
});

// Covers typing/pasting a path (e.g. a UNC share) by hand instead of using Browse - refreshing on
// blur (not every keystroke) avoids spamming /api/backups/list while the user is still mid-edit.
document.getElementById('backupFolderPath').addEventListener('blur', () => loadBackupList());

document.getElementById('runBackupNowBtn').addEventListener('click', async () => {
  setStatus('Creating backup...');
  const res = await fetch('/api/backups/run', { method: 'POST' });
  if (res.ok) {
    const body = await res.json();
    setStatus(`Backup created: ${body.fileName}`, true);
    setLastBackupLabel(formatLastBackup(new Date().toISOString()));
    loadBackupList();
  } else {
    const body = await res.json().catch(() => ({}));
    setStatus(body.message || 'Failed to create backup.', 'error');
  }
});

function formatBackupSize(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

// Reads straight from the Folder input's current (possibly unsaved) value, not the saved config -
// this is what makes Restore usable before Settings has ever been saved, e.g. on a fresh
// install/new machine: point the field at where old backups live and they show up immediately.
async function loadBackupList() {
  const folder = document.getElementById('backupFolderPath').value;
  const res = await fetch(`/api/backups/list?folder=${encodeURIComponent(folder || '')}`);
  const backups = res.ok ? await res.json() : [];

  const body = document.getElementById('backupListBody');
  body.innerHTML = '';

  if (backups.length === 0) {
    const row = document.createElement('tr');
    row.innerHTML = '<td colspan="4">No backups found in this folder.</td>';
    body.appendChild(row);
    return;
  }

  for (const b of backups) {
    const row = document.createElement('tr');
    const restoreBtn = document.createElement('button');
    restoreBtn.textContent = 'Restore';
    restoreBtn.addEventListener('click', () => restoreBackup(b.fileName));

    const downloadBtn = document.createElement('button');
    downloadBtn.textContent = 'Download';
    downloadBtn.title = 'Save a copy of this backup to the computer you are using.';
    downloadBtn.addEventListener('click', () => downloadBackup(b.fileName));

    const fileCell = document.createElement('td');
    fileCell.textContent = b.fileName;
    const sizeCell = document.createElement('td');
    sizeCell.textContent = formatBackupSize(b.sizeBytes);
    const createdCell = document.createElement('td');
    createdCell.textContent = new Date(b.createdUtc).toLocaleString();
    const actionCell = document.createElement('td');
    actionCell.append(restoreBtn, ' ', downloadBtn);

    row.append(fileCell, sizeCell, createdCell, actionCell);
    body.appendChild(row);
  }
}

// A plain link click: the server answers with an attachment, so the browser saves it and this page stays put.
function downloadBackup(fileName) {
  const folder = document.getElementById('backupFolderPath').value;
  const link = document.createElement('a');
  link.href = `/api/backups/download?fileName=${encodeURIComponent(fileName)}&folder=${encodeURIComponent(folder || '')}`;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
}

async function restoreBackup(fileName) {
  if (!confirm(`Restore from ${fileName}?\n\nThis will overwrite your current settings, lanes, resume state, and history with the contents of this backup. This cannot be undone.`)) return;

  setStatus(`Restoring ${fileName}...`);
  const folder = document.getElementById('backupFolderPath').value;
  const res = await fetch('/api/backups/restore', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ fileName, folder })
  });

  if (res.ok) {
    setStatus(`Restored from ${fileName}.`, true);
    loadSettings(); // the config on disk just changed out from under this page - reload the form (and its Last Backup label)
  } else {
    const body = await res.json().catch(() => ({}));
    setStatus(body.message || 'Failed to restore backup.', 'error');
  }
}

// Settings' own persistent "Install as app" card - separate from nav.js's one-time toolbar
// banner, and reachable any time rather than only until dismissed. window.CompressarrPwa (defined
// unconditionally in nav.js, loaded before this file) is the single source of truth for all three
// mutually-exclusive states below, so this never re-implements standalone/iOS/install detection.
function initPwaCard() {
  const installBlock = document.getElementById('pwaInstallBlock');
  const iosBlock = document.getElementById('pwaIosBlock');
  const uninstallBlock = document.getElementById('pwaUninstallBlock');
  if (!installBlock || !iosBlock || !uninstallBlock || !window.CompressarrPwa) return;

  if (window.CompressarrPwa.isStandalone()) {
    // Already running as the installed app - there's no page-triggerable uninstall API on any
    // browser (an obvious abuse vector if there were one), so this is instructions only, not a
    // button.
    uninstallBlock.hidden = false;
    return;
  }

  if (window.CompressarrPwa.isIos()) {
    iosBlock.hidden = false;
    return;
  }

  const installBtn = document.getElementById('pwaInstallBtn');
  const refreshInstallVisibility = () => { installBlock.hidden = !window.CompressarrPwa.canInstall(); };
  refreshInstallVisibility(); // covers the (uncommon) case beforeinstallprompt already fired before this ran

  // beforeinstallprompt is timing-dependent - the browser may not have decided installability yet
  // when this page first loads, so react to it arriving late instead of only checking once.
  window.addEventListener('compressarr-install-available', refreshInstallVisibility);

  installBtn.addEventListener('click', async () => {
    installBtn.disabled = true;
    await window.CompressarrPwa.promptInstall();
    installBtn.disabled = false;
  });

  window.addEventListener('appinstalled', () => { installBlock.hidden = true; });
}

loadSettings();
initPwaCard();
