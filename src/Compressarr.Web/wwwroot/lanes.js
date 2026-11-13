renderNav('lanes');

const lanesContainer = document.getElementById('lanes');
const template = document.getElementById('lane-template');

// Populated by populatePresetList() before any lane card is built - a <select> needs its
// <option>s to already exist before setting .value, unlike the old <input list> combo.
let presetNamesByEngine = { HandBrake: [], FFmpeg: [] };

// The global Settings > Processing values: what a lane starts with when it stops following them (3.1).
let globalProcessing = null;
const PROC_MIN_UNITS = { KB: 1024, MB: 1024 * 1024, GB: 1024 * 1024 * 1024 };

function splitList(text) { return String(text || '').split(',').map(s => s.trim()).filter(Boolean); }

// Fills a lane card's own processing fields from a processing object (the lane's own, or the global one).
function fillProcessingFields(node, p) {
  node.querySelector('.f-procVidTypes').value = (p.vidTypes || []).join(', ');
  node.querySelector('.f-procDelete').value = p.deleteAfterConvert || 'Recycle';
  node.querySelector('.f-procMinUnit').value = 'MB';
  node.querySelector('.f-procMinSize').value = (p.minSizeBytes || 0) / PROC_MIN_UNITS.MB;
  node.querySelector('.f-procCompanions').value = (p.companionExtensions || []).join(', ');
  node.querySelector('.f-procUnmatched').value = p.unmatchedCompanionAction || 'Recycle';
  node.querySelector('.f-procCollision').value = p.onDestinationCollision || 'Overwrite';
  node.querySelector('.f-procMove').checked = !!p.moveFiles;
  node.querySelector('.f-procClearTitle').checked = !!p.clearTitleMetadata;
}

function readProcessingFields(node) {
  const raw = parseFloat(node.querySelector('.f-procMinSize').value) || 0;
  return {
    vidTypes: splitList(node.querySelector('.f-procVidTypes').value),
    deleteAfterConvert: node.querySelector('.f-procDelete').value,
    moveFiles: node.querySelector('.f-procMove').checked,
    clearTitleMetadata: node.querySelector('.f-procClearTitle').checked,
    minSizeBytes: Math.round(raw * PROC_MIN_UNITS[node.querySelector('.f-procMinUnit').value]),
    companionExtensions: splitList(node.querySelector('.f-procCompanions').value),
    unmatchedCompanionAction: node.querySelector('.f-procUnmatched').value,
    onDestinationCollision: node.querySelector('.f-procCollision').value
  };
}

function sameProcessing(a, b) {
  return JSON.stringify(a) === JSON.stringify({ ...a, ...b }) && JSON.stringify(a.vidTypes) === JSON.stringify(b.vidTypes) && JSON.stringify(a.companionExtensions) === JSON.stringify(b.companionExtensions);
}

// Shows or hides the lane's own fields and the "Custom processing" chip to match the checkbox.
function syncProcessingSection(node) {
  const useGlobal = node.querySelector('.f-useGlobalProc').checked;
  node.querySelector('.lane-proc-fields').classList.toggle('hidden', useGlobal);
  node.querySelector('.f-customChip').classList.toggle('hidden', useGlobal);
}

function wireProcessingSection(node, dto) {
  const box = node.querySelector('.f-useGlobalProc');
  const own = dto.processing;
  box.checked = dto.useGlobalProcessing !== false || !own;
  fillProcessingFields(node, own || globalProcessing || {});
  syncProcessingSection(node);
  box.addEventListener('change', () => {
    if (!box.checked) {
      // Stops following the global settings: start from what the lane has been using, so nothing changes until edited.
      fillProcessingFields(node, globalProcessing || {});
    } else if (globalProcessing && !sameProcessing(readProcessingFields(node), globalProcessing) &&
               !confirm('Go back to the global settings? This lane\'s own processing settings will be discarded.')) {
      box.checked = false;
    }
    syncProcessingSection(node);
  });
}

// success truthy -> green (auto-clears after 4s), same convention as settings.js's setStatus -
// this used to just set plain text with no color at all, which is why Lanes' own save messages
// never turned green like Settings/Notifications did.
// success: true = green, 'error' = red, anything else = plain (progress such as "Saving...").
function setStatus(text, success) { setStatusMessage(text, success === 'error' ? 'error' : success ? 'success' : ''); }

// Logical validation-issue field key -> the class each field lives on, resolved within one lane
// card's own node (not the whole document) - every card repeats the same classes, so scoping
// matters. `node.querySelector` (not `document.querySelector`) is what makes that scoping work.
const LANE_FIELD_MAP = {
  input: '.f-input',
  output: '.f-output',
  tvShowBasePath: '.f-tvShowBasePath',
  movieBasePath: '.f-movieBasePath',
  tvPreset: '.f-tvPreset',
  moviePreset: '.f-moviePreset',
  engine: '.f-engine',
  processing: '.f-procVidTypes'
};

// Highlights just this one card's fields - the aggregate page-level status message (if any) is
// decided by the caller, since a single lane's issues shouldn't overwrite/hide another card's.
function applyLaneValidation(node, issues) {
  showEngineNote(node, node.querySelector('.f-engine').value, issues);
  // The Input problem is also spelled out under the field: it can be long (it names the other lane), and the red
  // outline's tooltip and the toolbar line are easy to miss or cut short.
  setInputNote(node, (issues || []).find(i => i.field === 'input')?.message);
  return applyValidationIssues(node, issues, LANE_FIELD_MAP, '');
}

function setInputNote(node, message) {
  const note = node.querySelector('.f-inputNote');
  note.textContent = message || '';
  note.classList.toggle('hidden', !message);
}

function escapeHtml(text) {
  return String(text ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function fillPresetSelect(select, currentValue, engine) {
  const presetNames = presetNamesByEngine[engine || 'HandBrake'] || [];
  // If the lane's saved preset isn't in the current list (e.g. presets.json changed since this
  // lane was configured), keep it as a selectable option anyway rather than silently blanking
  // the field out from under the user.
  const names = (currentValue && !presetNames.includes(currentValue))
    ? [currentValue, ...presetNames]
    : presetNames;

  select.innerHTML = '<option value=""></option>' + names.map(n => `<option value="${escapeHtml(n)}">${escapeHtml(n)}</option>`).join('');
  select.value = currentValue || '';
}

function laneCardFromDto(dto) {
  const node = template.content.firstElementChild.cloneNode(true);
  node.dataset.id = dto.id;
  node.querySelector('.f-enabled').checked = dto.enabled;
  node.querySelector('.f-displayName').value = dto.displayName;
  node.querySelector('.f-input').value = dto.input;
  node.querySelector('.f-output').value = dto.output;
  const engine = dto.engine || 'HandBrake';
  node.querySelector('.f-engine').value = engine;
  node.querySelector('.f-allowHdrLoss').checked = !!dto.allowHdrLoss;
  const syncHdrNote = () => node.querySelector('.f-hdrLossNote').classList.toggle('hidden', !node.querySelector('.f-allowHdrLoss').checked);
  node.querySelector('.f-allowHdrLoss').addEventListener('change', syncHdrNote);
  syncHdrNote();
  fillPresetSelect(node.querySelector('.f-tvPreset'), dto.tvPreset, engine);
  fillPresetSelect(node.querySelector('.f-moviePreset'), dto.moviePreset, engine);
  showEngineNote(node, engine, dto.validationIssues);
  // Switching encoder swaps the profile lists (each tool has its own); a chosen name that isn't in the
  // new list stays selectable and gets flagged on save, rather than being blanked behind the user's back.
  node.querySelector('.f-engine').addEventListener('change', e => {
    const next = e.target.value;
    fillPresetSelect(node.querySelector('.f-tvPreset'), node.querySelector('.f-tvPreset').value, next);
    fillPresetSelect(node.querySelector('.f-moviePreset'), node.querySelector('.f-moviePreset').value, next);
    showEngineNote(node, next, []);
  });
  node.querySelector('.f-tvShowBasePath').value = dto.tvShowBasePath;
  node.querySelector('.f-movieBasePath').value = dto.movieBasePath;
  wireProcessingSection(node, dto);
  wireKindAndTracking(node, dto);

  node.querySelector('.save-lane-btn').addEventListener('click', () => saveLane(node));
  node.querySelector('.remove-lane-btn').addEventListener('click', () => removeLane(node));
  node.querySelector('.duplicate-lane-btn').addEventListener('click', () => duplicateLane(node));

  for (const btn of node.querySelectorAll('.browse-btn')) {
    btn.addEventListener('click', () => {
      const targetField = node.querySelector(`.${btn.dataset.target}`);
      openFolderBrowser(targetField.value, chosenPath => {
        targetField.value = chosenPath;
        targetField.dispatchEvent(new Event('input', { bubbles: true }));
      });
    });
  }

  // A card that is showing red fields re-checks itself as it is edited, so a field turns normal the moment
  // it holds something valid instead of staying red until the next Save.
  for (const eventName of ['input', 'change']) {
    node.addEventListener(eventName, () => scheduleRevalidation(node));
  }
  // Turning a lane on is when a clash with another lane's folder matters, so check straight away rather than at Save.
  node.querySelector('.f-enabled').addEventListener('change', e => { if (e.target.checked) scheduleRevalidation(node, true); });

  return node;
}

let revalidationSeq = 0;

function scheduleRevalidation(node, force = false) {
  if (!force && !node.querySelector('.field-invalid')) return; // nothing red to clear - validation stays a Save-time thing
  clearTimeout(node._revalidateTimer);
  node._revalidateTimer = setTimeout(async () => {
    const seq = ++revalidationSeq;
    node._revalidateSeq = seq;
    try {
      const res = await fetch('/api/lanes/validate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(readLaneCard(node))
      });
      if (!res.ok || node._revalidateSeq !== seq) return; // failed, or a newer check is already on its way
      applyLaneValidation(node, await res.json());
      if (!lanesContainer.querySelector('.field-invalid') && /configuration issue/.test(currentStatusMessage())) {
        setStatusMessage('', '');
      }
    } catch { /* leave the highlight as it was */ }
  }, 400);
}

function readLaneCard(node) {
  return {
    id: node.dataset.id,
    displayName: node.querySelector('.f-displayName').value,
    enabled: node.querySelector('.f-enabled').checked,
    input: node.querySelector('.f-input').value,
    output: node.querySelector('.f-output').value,
    tvPreset: node.querySelector('.f-tvPreset').value,
    moviePreset: node.querySelector('.f-moviePreset').value,
    engine: node.querySelector('.f-engine').value,
    allowHdrLoss: node.querySelector('.f-allowHdrLoss').checked,
    tvShowBasePath: node.querySelector('.f-tvShowBasePath').value,
    movieBasePath: node.querySelector('.f-movieBasePath').value,
    kind: node.dataset.kind || 'Standard',
    tracking: node.querySelector('.f-tracking').value,
    useGlobalProcessing: node.querySelector('.f-useGlobalProc').checked,
    processing: node.querySelector('.f-useGlobalProc').checked ? null : readProcessingFields(node)
  };
}

async function putLane(dto) {
  const res = await fetch(`/api/lanes/${dto.id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(dto)
  });
  if (res.ok) return { ok: true, dto: await res.json() };
  // A refused save (e.g. enabling a lane on a folder another enabled lane already watches) explains itself.
  const body = await res.json().catch(() => ({}));
  return { ok: false, dto: null, message: body.message || null, field: body.field || null };
}

// Shows why a save was refused, on the field it is about when the server says which one.
function showSaveRefusal(node, result) {
  if (result.message && result.field === 'input') {
    applyValidationIssues(node, [{ field: 'input', message: result.message }], LANE_FIELD_MAP, '');
    setInputNote(node, result.message);
  }
}

async function saveLane(node) {
  const dto = readLaneCard(node);
  setStatus(`Saving lane "${dto.displayName}"...`);
  const result = await putLane(dto);
  if (!result.ok) {
    showSaveRefusal(node, result);
    setStatusMessage(result.message ? `Lane "${dto.displayName}" was not saved. ${result.message}` : 'Failed to save lane.', 'error');
    return;
  }

  // A single lane's Save clears the global dirty flag even if another card still has unsaved
  // edits of its own - a known simplification (no per-card tracking), same trade-off as Settings.
  lanesDirty = false;
  if (applyLaneValidation(node, result.dto.validationIssues)) {
    setStatusMessage(`Lane "${dto.displayName}" saved, but has a configuration issue - check the fields below.`, 'error');
  } else {
    setStatus(`Lane "${dto.displayName}" saved.`, true);
  }
}

async function saveAllLanes() {
  const cards = Array.from(lanesContainer.querySelectorAll('.lane-card'));
  if (cards.length === 0) {
    setStatus('No lanes to save.');
    return;
  }

  setStatus(`Saving ${cards.length} lane(s)...`);
  // One at a time, lanes being turned OFF first: the server refuses to enable a lane on a folder another enabled lane
  // watches, so switching which of two lanes is on has to take the old one off before the new one goes on.
  const order = cards.map((node, i) => ({ node, i, dto: readLaneCard(node) })).sort((a, b) => Number(a.dto.enabled) - Number(b.dto.enabled) || a.i - b.i);
  const results = new Array(cards.length);
  for (const { node, i, dto } of order) {
    results[i] = await putLane(dto);
    if (!results[i].ok) showSaveRefusal(node, results[i]);
  }
  const failedCount = results.filter(r => !r.ok).length;
  const firstRefusal = results.find(r => !r.ok && r.message);

  let anyIssues = false;
  results.forEach((result, i) => {
    if (result.ok && applyLaneValidation(cards[i], result.dto.validationIssues)) anyIssues = true;
  });

  if (failedCount > 0) {
    setStatusMessage(`Saved ${cards.length - failedCount} of ${cards.length} lane(s) - ${failedCount} failed.${firstRefusal ? ' ' + firstRefusal.message : ''}`, 'error');
  } else if (anyIssues) {
    setStatusMessage(`All ${cards.length} lane(s) saved, but one or more has a configuration issue - check the fields below.`, 'error');
  } else {
    setStatus(`All ${cards.length} lane(s) saved.`, true);
  }
  if (failedCount === 0) lanesDirty = false;
}

// Copies this lane (as the card shows it right now, every field) under a name the user picks. The copy always starts
// disabled: two enabled lanes can't watch the same folder, and that is checked when the copy is enabled.
async function duplicateLane(node) {
  const dto = readLaneCard(node);
  const name = prompt(`Name for the new lane, copied from "${dto.displayName}":`, `${dto.displayName} (copy)`);
  if (name === null) return;
  if (!name.trim()) {
    setStatus('Enter a name for the new lane.', 'error');
    return;
  }

  const res = await fetch('/api/lanes/duplicate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ displayName: name.trim(), lane: dto })
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    setStatus(body.message || 'Could not duplicate the lane.', 'error');
    return;
  }

  const copy = await res.json();
  const copyNode = laneCardFromDto(copy);
  node.after(copyNode);
  applyLaneValidation(copyNode, copy.validationIssues);
  copyNode.scrollIntoView({ behavior: 'smooth', block: 'center' });
  setStatus(`Lane "${copy.displayName}" created from "${dto.displayName}", switched off. It watches the same folders as the original: choose its own Input folder (or turn the original off), then tick Enabled.`, true);
}

async function removeLane(node) {
  const dto = readLaneCard(node);
  // Files in other lanes may have been told to land in this lane's library. Say so before it goes: they are
  // not misfiled anywhere else - each finished file waits safely in Output until a new destination is chosen.
  let redirectedNote = '';
  try {
    const res = await fetch(`/api/lanes/${encodeURIComponent(dto.id)}/redirected-files`);
    const { waitingFiles } = await res.json();
    if (waitingFiles > 0) {
      redirectedNote = `\n\n${waitingFiles} queued file${waitingFiles === 1 ? ' is' : 's are'} set to land in this lane. ${waitingFiles === 1 ? 'It' : 'They'} will be held in Output (never filed in another library) until you choose a new destination on the Monitor page.`;
    }
  } catch { /* best-effort - the plain confirmation below still works */ }

  const confirmed = confirm(`Remove lane "${dto.displayName}"?\n\nThis only removes it from Compressarr's configuration - no files are touched.${redirectedNote}`);
  if (!confirmed) return;

  const res = await fetch(`/api/lanes/${dto.id}`, { method: 'DELETE' });
  if (res.ok) {
    node.remove();
    setStatus(`Lane "${dto.displayName}" removed.`, true);
  } else {
    setStatus('Failed to remove lane.', 'error');
  }
}

const TRACKING_NOTES = {
  Full: '',
  ErrorsOnly: 'Only files that fail or finish with a warning appear in History, reports and notifications. A file that converts cleanly leaves no row, no savings, no count and no file name in the log. A failed file still shows in the Monitor queue until you remove it.',
  None: 'Nothing about this lane is recorded: no run, no History, no report, no notification, and no file names in the log. The Monitor page still shows the live progress, and a failed file stays in the queue as an error until you remove it. The queue also remembers a source that was kept (Maintain) so it is not converted twice.'
};
const TRACKING_CHIPS = { ErrorsOnly: 'History: errors only', None: 'History: off' };

// A Videos lane: one profile and one destination (no TV column), its own chip, and the tracking choice always visible. Also the tracking select and its chip.
function wireKindAndTracking(node, dto) {
  const kind = dto.kind || 'Standard';
  node.dataset.kind = kind;
  if (kind === 'Videos') {
    node.querySelector('.f-tvCol').classList.add('hidden');
    node.querySelector('.f-videosChip').classList.remove('hidden');
    node.querySelector('.f-trackingBlock').classList.remove('adv-only');
    node.querySelector('.routing-grid').style.gridTemplateColumns = '1fr';
    const head = node.querySelector('.routing-col:not(.f-tvCol) .routing-col-head');
    if (head && head.lastChild) head.lastChild.textContent = ' Videos';
  }
  const select = node.querySelector('.f-tracking');
  select.value = dto.tracking || 'Full';
  const sync = () => {
    const mode = select.value;
    const note = node.querySelector('.f-trackingNote');
    note.textContent = TRACKING_NOTES[mode] || '';
    note.classList.toggle('hidden', !TRACKING_NOTES[mode]);
    const chip = node.querySelector('.f-trackChip');
    chip.textContent = TRACKING_CHIPS[mode] || '';
    chip.classList.toggle('hidden', !TRACKING_CHIPS[mode]);
  };
  select.addEventListener('change', sync);
  sync();
}

async function loadGlobalProcessing() {
  try {
    const res = await fetch('/api/settings');
    if (res.ok) globalProcessing = await res.json();
  } catch { /* the lane card then starts from empty fields; the lane still works */ }
}

async function populatePresetList() {
  // includePassthrough: a lane can pick "Passthrough (no re-encode)" for its TV and/or Movie preset.
  const [hb, ff] = await Promise.all([fetch('/api/presets?includePassthrough=1'), fetch('/api/presets?engine=ffmpeg&includePassthrough=1')]);
  presetNamesByEngine = { HandBrake: await hb.json(), FFmpeg: await ff.json() };
}

// Under the Encoder select: why this lane's encoder can't run yet (ffmpeg missing, ...), or a reminder that it is experimental.
function showEngineNote(node, engine, issues) {
  const note = node.querySelector('.f-engineNote');
  const problem = (issues || []).find(i => i.field === 'engine');
  if (problem) {
    note.textContent = problem.message;
    note.classList.remove('hidden');
  } else if (engine === 'FFmpeg') {
    note.textContent = "Experimental. Files with Dolby Vision or HDR10+ are left alone and reported (ERROR 112) unless the ffmpeg profile names a HandBrake fallback profile.";
    note.classList.remove('hidden');
  } else {
    note.classList.add('hidden');
  }
}

async function loadLanes() {
  const res = await fetch('/api/lanes');
  const lanes = await res.json();
  lanesContainer.innerHTML = '';
  let anyIssues = false;
  for (const dto of lanes) {
    const node = laneCardFromDto(dto);
    lanesContainer.appendChild(node);
    if (applyLaneValidation(node, dto.validationIssues)) anyIssues = true;
  }
  lanesDirty = false;
  if (anyIssues) {
    setStatusMessage('One or more lanes has a configuration issue - check the fields below.', 'error');
  }
}

document.getElementById('addVideosLaneBtn').addEventListener('click', async () => {
  const res = await fetch('/api/lanes?kind=videos', { method: 'POST' });
  const dto = await res.json();
  const node = laneCardFromDto(dto);
  lanesContainer.appendChild(node);
  applyLaneValidation(node, dto.validationIssues);
  node.scrollIntoView({ behavior: 'smooth', block: 'center' });
  setStatus(`Videos lane "${dto.displayName}" added.`, true);
});

document.getElementById('addLaneBtn').addEventListener('click', async () => {
  const res = await fetch('/api/lanes', { method: 'POST' });
  const dto = await res.json();
  const node = laneCardFromDto(dto);
  lanesContainer.appendChild(node);
  applyLaneValidation(node, dto.validationIssues);
  setStatus(`Lane "${dto.displayName}" added.`, true);
});

document.getElementById('saveAllLanesBtn').addEventListener('click', saveAllLanes);

// ---- Queue priority rules (3.1) ----
const prioRulesEl = document.getElementById('prioRules');
const prioStatusEl = document.getElementById('prioStatus');

function prioSay(text, ok) { prioStatusEl.textContent = text; prioStatusEl.style.color = ok === false ? 'var(--err)' : ''; }

function addPrioRow(rule) {
  const row = document.createElement('div');
  row.className = 'prio-rule-row';
  row.dataset.id = rule.id || '';
  row.innerHTML = `
    <input type="text" class="prio-match" placeholder="Title contains..." />
    <input type="number" class="prio-tmdb" placeholder="TMDb id" min="1" />
    <select class="prio-level"><option value="Lower">Lower</option><option value="Higher">Higher</option></select>
    <button type="button" class="prio-del" title="Delete this rule">Delete</button>`;
  row.querySelector('.prio-match').value = rule.match || '';
  row.querySelector('.prio-tmdb').value = rule.tmdbId || '';
  row.querySelector('.prio-level').value = rule.priority || 'Lower';
  row.querySelector('.prio-del').addEventListener('click', () => row.remove());
  prioRulesEl.appendChild(row);
}

function readPrioRules() {
  return [...prioRulesEl.querySelectorAll('.prio-rule-row')].map(r => ({
    id: r.dataset.id || null,
    match: r.querySelector('.prio-match').value.trim(),
    tmdbId: parseInt(r.querySelector('.prio-tmdb').value, 10) || null,
    priority: r.querySelector('.prio-level').value
  })).filter(r => r.match || r.tmdbId);
}

async function loadPrioRules() {
  try {
    const res = await fetch('/api/queue-priority');
    const body = await res.json();
    prioRulesEl.innerHTML = '';
    (body.rules || []).forEach(addPrioRow);
    if (!(body.rules || []).length) prioRulesEl.innerHTML = '<p class="enc-note" id="prioNone">No rules yet. Every new file joins the end of the queue.</p>';
  } catch { /* the card simply stays empty */ }
}

document.getElementById('prioAdd').addEventListener('click', () => { document.getElementById('prioNone')?.remove(); addPrioRow({}); });
document.getElementById('prioSave').addEventListener('click', async () => {
  const res = await fetch('/api/queue-priority', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ rules: readPrioRules() }) });
  if (res.ok) { await loadPrioRules(); prioSay('Rules saved. New files are placed by them from now on.'); }
  else prioSay((await res.json().catch(() => ({}))).message || 'Could not save the rules.', false);
});
document.getElementById('prioApply').addEventListener('click', async () => {
  const res = await fetch('/api/run/queue/priority/apply', { method: 'POST' });
  if (res.ok) { const b = await res.json(); prioSay(b.moved ? `Re-sorted: ${b.moved} waiting file${b.moved === 1 ? '' : 's'} changed place.` : 'The waiting files were already in that order.'); }
  else prioSay('Could not apply the rules.', false);
});
loadPrioRules();

document.getElementById('clearChangesBtn').addEventListener('click', async () => {
  if (lanesDirty && !confirm('Discard unsaved changes and reload the last saved lanes?')) return;
  await loadLanes();
  setStatus('Changes cleared.', true);
});

// Warn before leaving with unsaved field edits inside a lane card - Add/Remove/Save all persist
// immediately on click, so they're never what this is protecting; only in-progress edits to a
// card's own fields (typed but not yet Saved) are. Sidebar nav links are plain <a href>
// navigation, so beforeunload fires for those the same as tab close/reload - no separate
// in-app-click handling needed.
let lanesDirty = false;
for (const eventName of ['input', 'change']) {
  lanesContainer.addEventListener(eventName, () => { lanesDirty = true; });
}
window.addEventListener('beforeunload', e => {
  if (!lanesDirty) return;
  e.preventDefault();
  e.returnValue = '';
});

// loadLanes() (and Add Lane's own card-building) needs presetNames already populated - a
// <select>'s .value only "sticks" once a matching <option> exists.
Promise.all([populatePresetList(), loadGlobalProcessing()]).then(loadLanes);
