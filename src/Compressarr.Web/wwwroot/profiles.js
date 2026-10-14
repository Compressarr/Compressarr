renderNav('profiles');

// success: true = green, 'error' = red, anything else = plain (progress such as "Saving...").
function setStatus(text, success) { setStatusMessage(text, success === 'error' ? 'error' : success ? 'success' : ''); }

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

const LOCK_ICON = '<svg class="prof-lock-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="11" width="16" height="10" rx="2"></rect><path d="M8 11V7a4 4 0 0 1 8 0v4"></path></svg>';

let profiles = [];
let engineFilter = 'all';
let warnings = {}; // ffmpeg profile name -> encoders this build lacks

function apiBase(p) { return p.engine === 'ffmpeg' ? '/api/profiles/ffmpeg/' : '/api/profiles/handbrake/'; }
function editorUrl(p) { return `${p.engine === 'ffmpeg' ? '/ffmpeg-edit.html' : '/profile-edit.html'}?name=${encodeURIComponent(p.name)}`; }

function profileRow(p, index) {
  const locked = p.builtIn || p.catalog;
  const lock = p.builtIn
    ? `<span class="prof-lock" title="Built-in - ships with Compressarr and can't be changed or deleted">${LOCK_ICON}built-in</span>`
    : p.catalog
      ? `<span class="prof-lock" title="Added from the Compressarr catalog - locked like a built-in, but you can delete it">${LOCK_ICON}catalog</span>`
      : `<span class="prof-lock" title="Created by you - you can edit, duplicate or delete it">user</span>`;
  const engine = p.engine === 'ffmpeg' ? 'ffmpeg' : 'HandBrake';
  const sub = [p.container, locked ? '' : 'saved in Compressarr\'s own file'].filter(Boolean).join(' · ');
  const missing = p.engine === 'ffmpeg' ? warnings[p.name] : null;
  const warn = missing ? `<div class="prof-warn" title="This ffmpeg build can't run this profile">Needs ${escapeHtml(missing.join(', '))}</div>` : '';

  const buttons = [];
  buttons.push(locked ? `<button data-act="view" data-i="${index}">View</button>` : `<button data-act="edit" data-i="${index}">Edit</button>`);
  buttons.push(`<button data-act="dup" data-i="${index}">Duplicate</button>`);
  if (p.engine !== 'ffmpeg') buttons.push(`<button data-act="dupff" data-i="${index}" title="Make the closest ffmpeg profile from this one and list what ffmpeg can't carry over">Duplicate as ffmpeg&hellip;</button>`);
  if (!p.builtIn) buttons.push(`<button data-act="del" data-i="${index}">Delete</button>`);

  return `<tr data-e="${p.engine}">
    <td><div class="prof-name">${escapeHtml(p.name)} ${lock}</div>${sub ? `<div class="prof-sub">${escapeHtml(sub)}</div>` : ''}${p.description ? `<div class="prof-sub" title="${escapeHtml(p.description)}">${escapeHtml(p.description.length > 110 ? p.description.slice(0, 107) + '...' : p.description)}</div>` : ''}${warn}</td>
    <td><span class="prof-chip ${p.engine === 'ffmpeg' ? 'ff' : ''}">${engine}</span></td>
    <td>${escapeHtml(p.video)}</td>
    <td>${escapeHtml(p.audio)}</td>
    <td>${p.usedBy.length ? p.usedBy.map(escapeHtml).join(', ') : '-'}</td>
    <td><div class="prof-actions">${buttons.join('')}</div></td>
  </tr>`;
}

function renderRows() {
  const rows = profiles
    .map((p, i) => ({ p, i }))
    .filter(({ p }) => engineFilter === 'all' || p.engine === engineFilter);
  document.getElementById('profileRows').innerHTML = rows.length
    ? rows.map(({ p, i }) => profileRow(p, i)).join('')
    : '<tr><td colspan="6" class="prof-sub">No profiles.</td></tr>';

  const count = e => profiles.filter(p => e === 'all' || p.engine === e).length;
  for (const b of document.querySelectorAll('#engineTabs button')) {
    const label = { all: 'All', handbrake: 'HandBrake', ffmpeg: 'ffmpeg' }[b.dataset.f];
    b.textContent = `${label} (${count(b.dataset.f)})`;
    b.classList.toggle('on', b.dataset.f === engineFilter);
  }
}

async function loadProfiles() {
  const res = await fetch('/api/profiles');
  const dto = await res.json();
  profiles = dto.profiles;
  renderRows();
  document.getElementById('userFilePath').textContent = dto.userFilePath;
  if (dto.ffmpegUserFilePath) document.getElementById('ffUserFilePath').textContent = dto.ffmpegUserFilePath;
  if (dto.catalogHandBrakeFilePath) document.getElementById('catalogHbFilePath').textContent = dto.catalogHandBrakeFilePath;
  if (dto.catalogFFmpegFilePath) document.getElementById('catalogFfFilePath').textContent = dto.catalogFFmpegFilePath;
  if (typeof showCatalogNote === 'function') showCatalogNote();

  const errors = [];
  if ((dto.damagedCatalog || []).length) errors.push(`${dto.damagedCatalog.map(n => `"${n}"`).join(', ')} from the catalog no longer match${dto.damagedCatalog.length === 1 ? 'es' : ''} what was installed (the stored copy was changed or damaged), so ${dto.damagedCatalog.length === 1 ? 'it is' : 'they are'} not offered to lanes. Add ${dto.damagedCatalog.length === 1 ? 'it' : 'them'} again from the catalog.`);
  if (dto.catalogFileError) errors.push(dto.catalogFileError);
  if (dto.userFileError) errors.push(`Your HandBrake profile file could not be read, so only the built-ins are shown: ${dto.userFileError}`);
  if (dto.ffmpegUserFileError) errors.push(`Your ffmpeg profile file could not be read, so only the built-ins are shown: ${dto.ffmpegUserFileError}`);
  const errorBox = document.getElementById('profileError');
  if (errors.length) {
    errorBox.textContent = errors.join(' ');
    errorBox.classList.remove('hidden');
  } else {
    errorBox.classList.add('hidden');
  }

  loadWarnings();
}

// Which ffmpeg profiles this build can't run - asked after the list is on screen (it runs ffmpeg briefly).
async function loadWarnings() {
  if (!profiles.some(p => p.engine === 'ffmpeg')) return;
  try {
    const caps = await (await fetch('/api/ffmpeg/capabilities')).json();
    warnings = caps.found ? (caps.profileWarnings || {}) : {};
    renderRows();
  } catch { /* the warning is a nicety */ }
}

document.getElementById('engineTabs').addEventListener('click', e => {
  const b = e.target.closest('button[data-f]');
  if (!b) return;
  engineFilter = b.dataset.f;
  renderRows();
});

document.getElementById('profileRows').addEventListener('click', async e => {
  const btn = e.target.closest('button[data-act]');
  if (!btn) return;
  const p = profiles[Number(btn.dataset.i)];

  if (btn.dataset.act === 'edit' || btn.dataset.act === 'view') {
    location.href = editorUrl(p);
  } else if (btn.dataset.act === 'dup') {
    const res = await fetch(`${apiBase(p)}${encodeURIComponent(p.name)}/duplicate`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}'
    });
    const body = await res.json().catch(() => ({}));
    if (res.ok) {
      setStatus(`Duplicated as "${body.name}".`, true);
      await loadProfiles();
    } else {
      setStatus(body.message || 'Could not duplicate the profile.', 'error');
    }
  } else if (btn.dataset.act === 'dupff') {
    const res = await fetch(`/api/profiles/handbrake/${encodeURIComponent(p.name)}/duplicate-as-ffmpeg`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}'
    });
    const body = await res.json().catch(() => ({}));
    if (res.ok) {
      showConversion(p.name, body);
      await loadProfiles();
    } else {
      setStatus(body.message || 'Could not make an ffmpeg profile from this one.', 'error');
    }
  } else if (btn.dataset.act === 'del') {
    if (!confirm(`Delete the ${p.engine === 'ffmpeg' ? 'ffmpeg ' : ''}profile "${p.name}"?\n\nThis can't be undone.`)) return;
    const res = await fetch(`${apiBase(p)}${encodeURIComponent(p.name)}`, { method: 'DELETE' });
    if (res.ok) {
      setStatus(`Deleted "${p.name}".`, true);
      await loadProfiles();
    } else {
      const body = await res.json().catch(() => ({}));
      setStatus(body.message || 'Could not delete the profile.', 'error');
    }
  }
});

document.getElementById('newHbBtn').addEventListener('click', () => { location.href = '/profile-edit.html?new=1'; });
document.getElementById('newFfBtn').addEventListener('click', () => { location.href = '/ffmpeg-edit.html?new=1'; });

// ---- "Duplicate as ffmpeg": what carried over, and what ffmpeg can't express ---------------------------

function showConversion(source, result) {
  const box = document.getElementById('conversionBox');
  document.getElementById('conversionTitle').textContent = `"${source}" is now an ffmpeg profile named "${result.name}"`;
  document.getElementById('conversionCarried').innerHTML = result.carried.map(n => `<li>${escapeHtml(n)}</li>`).join('');
  const lost = result.notCarried || [];
  document.getElementById('conversionLostWrap').classList.toggle('hidden', lost.length === 0);
  document.getElementById('conversionLost').innerHTML = lost.map(n => `<li>${escapeHtml(n)}</li>`).join('');
  document.getElementById('conversionOpen').onclick = () => { location.href = `/ffmpeg-edit.html?name=${encodeURIComponent(result.name)}`; };
  box.classList.remove('hidden');
  box.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

document.getElementById('conversionClose').addEventListener('click', () => document.getElementById('conversionBox').classList.add('hidden'));

// ---- Import dialog (HandBrake profiles) ----------------------------------------------------------------

const overlay = document.getElementById('importOverlay');
let importPath = '';
let candidates = [];
const selected = new Set();

function openImport() {
  overlay.classList.remove('hidden');
  showTab('installed');
}

function closeImport() { overlay.classList.add('hidden'); }

let currentImportTab = 'installed';

function showTab(tab) {
  currentImportTab = tab;
  for (const b of document.querySelectorAll('#importTabs button')) b.classList.toggle('on', b.dataset.t === tab);
  document.getElementById('tabInstalled').classList.toggle('hidden', tab !== 'installed');
  document.getElementById('tabCatalog').classList.toggle('hidden', tab !== 'catalog');
  document.getElementById('tabFile').classList.toggle('hidden', tab !== 'file');
  resetPicker();
  if (tab === 'installed') loadInstalled();
  if (tab === 'catalog') loadCatalog();
}

function resetPicker() {
  candidates = [];
  selected.clear();
  document.getElementById('importPicker').classList.add('hidden');
  updateCount();
}

async function loadInstalled() {
  const note = document.getElementById('installedNote');
  note.textContent = 'Looking for HandBrake\'s presets...';
  const res = await fetch('/api/profiles/import/installed');
  const dto = await res.json();
  if (!dto.found) {
    note.textContent = dto.error || 'No HandBrake presets file was found.';
    return;
  }
  note.innerHTML = `Found HandBrake's presets at <code>${escapeHtml(dto.path)}</code> (${dto.candidates.length} preset${dto.candidates.length === 1 ? '' : 's'}). Pick the ones to copy into Compressarr.`;
  showCandidates(dto);
}

async function readFile() {
  const path = document.getElementById('importFilePath').value;
  const res = await fetch('/api/profiles/import/read', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path })
  });
  const dto = await res.json();
  if (!dto.found) {
    resetPicker();
    setStatus(dto.error || 'Could not read that file.', 'error');
    return;
  }
  setStatus(`Read ${dto.candidates.length} preset${dto.candidates.length === 1 ? '' : 's'} from the file.`, true);
  showCandidates(dto);
}

function showCandidates(dto) {
  importPath = dto.path;
  candidates = dto.candidates;
  selected.clear();
  document.getElementById('importPicker').classList.remove('hidden');
  document.getElementById('importSearch').value = '';
  renderCandidates();
}

function renderCandidates() {
  const q = document.getElementById('importSearch').value.trim().toLowerCase();
  const rows = candidates
    .map((c, i) => ({ c, i }))
    .filter(({ c }) => !q || c.name.toLowerCase().includes(q) || (c.group || '').toLowerCase().includes(q));

  document.querySelector('#importTable tbody').innerHTML = rows.length ? rows.map(({ c, i }) => {
    const note = c.status === 'alreadyBuiltIn' ? '<span class="prof-chip muted">already built in</span>'
      : c.status === 'nameUsed' ? '<span class="prof-chip warn">name already used</span>' : '';
    return `<tr>
      <td style="width:28px"><input type="checkbox" data-i="${i}" ${c.status === 'alreadyBuiltIn' ? 'disabled' : ''} ${selected.has(i) ? 'checked' : ''} /></td>
      <td class="prof-name">${escapeHtml(c.name)}</td><td>${escapeHtml(c.group || '')}</td><td>${escapeHtml(c.video)}</td><td>${note}</td></tr>`;
  }).join('') : '<tr><td class="prof-sub">Nothing matches.</td></tr>';
  updateCount();
}

function updateCount() {
  const n = selected.size;
  document.getElementById('importCount').textContent = n ? `${n} selected - will be saved to handbrake-profiles.json` : '';
  const go = document.getElementById('importGo');
  go.disabled = n === 0;
  go.textContent = n ? `Import ${n} profile${n === 1 ? '' : 's'}` : 'Import';
}

document.getElementById('importBtn').addEventListener('click', openImport);
document.getElementById('importCancel').addEventListener('click', closeImport);
overlay.addEventListener('click', e => { if (e.target === overlay) closeImport(); });
document.getElementById('importTabs').addEventListener('click', e => {
  const b = e.target.closest('button[data-t]');
  if (b) showTab(b.dataset.t);
});
document.getElementById('readFileBtn').addEventListener('click', readFile);
// Browse... picks the presets file with the app's own file browser (a web page can't see the real path of a file
// chosen with the system dialog), then reads it straight away.
document.getElementById('browseFileBtn').addEventListener('click', () => {
  const field = document.getElementById('importFilePath');
  openFileBrowser(field.value, chosen => { field.value = chosen; readFile(); }, '.json');
});
document.getElementById('importFilePath').addEventListener('keydown', e => { if (e.key === 'Enter') readFile(); });
document.getElementById('importSearch').addEventListener('input', renderCandidates);
document.getElementById('importTable').addEventListener('change', e => {
  const i = Number(e.target.dataset.i);
  if (e.target.checked) selected.add(i); else selected.delete(i);
  updateCount();
});
document.getElementById('importSelectShown').addEventListener('click', () => {
  for (const cb of document.querySelectorAll('#importTable input[type=checkbox]:not(:disabled)')) selected.add(Number(cb.dataset.i));
  renderCandidates();
});
document.getElementById('importClear').addEventListener('click', () => { selected.clear(); renderCandidates(); });

document.getElementById('importGo').addEventListener('click', async () => {
  if (currentImportTab === 'catalog') { await installFromCatalog(); return; }
  const onConflict = document.querySelector('input[name=onConflict]:checked').value;
  const names = [...selected].map(i => candidates[i].name);
  const res = await fetch('/api/profiles/import', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path: importPath, names, onConflict })
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) {
    setStatus(body.message || 'Import failed.', 'error');
    return;
  }
  closeImport();
  const parts = [];
  if (body.imported.length) parts.push(`${body.imported.length} imported`);
  if (body.replaced.length) parts.push(`${body.replaced.length} replaced`);
  if (body.skipped.length) parts.push(`${body.skipped.length} skipped`);
  setStatus(parts.join(', ') + '.', true);
  await loadProfiles();
});

loadProfiles();
