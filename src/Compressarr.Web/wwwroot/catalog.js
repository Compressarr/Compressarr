// The "From Catalog" tab of the Profiles page's import dialog, and the note above the profile list.
// Shares the page's globals with profiles.js (setStatus, escapeHtml, loadProfiles, overlay, closeImport, showTab).
//
// Nothing here runs by itself: the catalog is read when the user opens this tab, and a profile is added only when the user
// ticks it and presses Import.

const CATALOG_NEW_KEY = 'compressarr.catalogNew'; // set by a catalog check (this tab, the About page); cleared once the list has been seen

let catalogView = null;
const catalogPicks = new Map();      // "family|version|engine" -> { family, version, engine, importAs, taken }
const catalogOlderShown = new Set(); // families whose earlier versions are expanded

function catalogKey(family, version, engine) { return `${family}|${version}|${engine || ""}`; }

function showCatalogNote() {
  const note = document.getElementById('catalogNote');
  if (!note) return;
  let n = 0;
  try { n = Number(sessionStorage.getItem(CATALOG_NEW_KEY) || 0); } catch { n = 0; }
  if (n > 0) {
    note.innerHTML = `${n} preset${n === 1 ? '' : 's'} in the catalog you haven't added. <a href="#" id="catalogNoteLink">Browse the catalog</a>`;
    note.classList.remove('hidden');
    document.getElementById('catalogNoteLink').addEventListener('click', e => {
      e.preventDefault();
      overlay.classList.remove('hidden');
      showTab('catalog');
    });
  } else {
    note.classList.add('hidden');
  }
}

async function loadCatalog() {
  const sourceEl = document.getElementById('catalogSource');
  const statusEl = document.getElementById('catalogStatus');
  const picker = document.getElementById('catalogPicker');
  picker.classList.add('hidden');
  catalogPicks.clear();
  updateCatalogCount();

  // Who the profiles come from is shown before anything is fetched, and stays on screen.
  try {
    const source = await (await fetch('/api/catalog/source')).json();
    sourceEl.innerHTML = `Importing from: <strong>${escapeHtml(source.name)}</strong> - <a href="${escapeHtml(source.url)}" target="_blank" rel="noopener">${escapeHtml(source.url)}</a>`;
  } catch {
    sourceEl.textContent = 'Importing from: the Compressarr catalog';
  }

  statusEl.textContent = 'Loading the catalog...';
  let dto;
  try {
    dto = await (await fetch('/api/catalog')).json();
  } catch {
    statusEl.textContent = 'Could not load the catalog.';
    return;
  }
  if (!dto.ok) {
    statusEl.textContent = dto.error || 'Could not load the catalog.';
    return;
  }

  catalogView = dto.catalog;
  try { sessionStorage.removeItem(CATALOG_NEW_KEY); } catch { /* the note is a nicety */ }
  showCatalogNote();

  const profileCount = catalogView.families.reduce((n, f) => n + f.versions.length, 0);
  const updated = catalogView.updated ? `, updated ${escapeHtml(catalogView.updated)}` : '';
  statusEl.innerHTML = `${profileCount} profile version${profileCount === 1 ? '' : 's'} published${updated} (release <code>${escapeHtml(catalogView.release)}</code>). Nothing is added until you tick it. They are locked like the built-ins (Duplicate one to make your own) but you can delete them; a published version never changes, so a newer version is added next to the old one.`;
  picker.classList.remove('hidden');
  document.getElementById('catalogSearch').value = '';
  renderCatalog();
}

function catalogChips(item) {
  const chips = [];
  if (item.status === 'installed') chips.push('<span class="prof-chip muted">Installed</span>');
  if (item.status === 'damaged') chips.push('<span class="prof-chip warn" title="The stored copy no longer matches what was installed. Delete it on the list and add it again.">damaged</span>');
  if (item.status === 'nameUsed') chips.push('<span class="prof-chip warn" title="A built-in profile, one of yours or another catalog profile already has this name.">name already used</span>');
  if (item.status === 'needsApp') chips.push(`<span class="prof-chip muted">Needs Compressarr ${escapeHtml(item.minAppVersion)}</span>`);
  if (item.newerThanInstalled) chips.push('<span class="prof-chip" title="An earlier version of this profile is installed. Nothing is updated: adding this one puts it next to the old one.">newer version</span>');
  if (item.deprecated) chips.push(`<span class="prof-chip warn" title="${escapeHtml(item.deprecated)}">deprecated</span>`);
  return chips.join(' ');
}

function catalogRow(family, item) {
  const key = catalogKey(item.family, item.version, item.engine);
  const pick = catalogPicks.get(key);
  const selectable = item.status === 'new' || item.status === 'nameUsed';
  const engineChip = `<span class="prof-chip ${item.engine === 'ffmpeg' ? 'ff' : ''}">${item.engine === 'ffmpeg' ? 'ffmpeg' : 'HandBrake'}</span>`;
  const meta = [item.changelog, item.author ? `by ${item.author}` : '', item.requires.length ? `needs ${item.requires.join(', ')}` : '', item.published].filter(Boolean).map(escapeHtml).join(' · ');
  const importAs = pick && item.status === 'nameUsed'
    ? `<div class="cat-importas"><label>Import as: <input type="text" data-k="${escapeHtml(key)}" class="cat-importas-input" value="${escapeHtml(pick.importAs)}" /></label> <span class="prof-sub">saved as one of your own profiles</span></div>`
    : '';
  return `<tr>
    <td style="width:28px"><input type="checkbox" class="cat-check" data-k="${escapeHtml(key)}" ${selectable ? '' : 'disabled'} ${pick ? 'checked' : ''} /></td>
    <td><div class="prof-name">${escapeHtml(item.name)} ${engineChip}</div>
      ${item.description ? `<div class="prof-sub">${escapeHtml(item.description)}</div>` : ''}
      <div class="prof-sub">${meta}</div>${importAs}</td>
    <td>${catalogChips(item)}</td></tr>`;
}

function renderCatalog() {
  if (!catalogView) return;
  const q = document.getElementById('catalogSearch').value.trim().toLowerCase();
  const html = [];
  for (const family of catalogView.families) {
    const versions = family.versions.filter(v => !q || v.name.toLowerCase().includes(q) || v.description.toLowerCase().includes(q));
    if (versions.length === 0) continue;

    const expanded = catalogOlderShown.has(family.family) || !!q;
    html.push(catalogRow(family, versions[0]));
    if (versions.length > 1) {
      html.push(`<tr><td></td><td colspan="2"><a href="#" class="cat-older" data-family="${escapeHtml(family.family)}">${expanded ? 'hide' : 'show'} ${versions.length - 1} earlier version${versions.length === 2 ? '' : 's'}</a></td></tr>`);
      if (expanded) for (const v of versions.slice(1)) html.push(catalogRow(family, v));
    }
  }
  document.querySelector('#catalogTable tbody').innerHTML = html.length ? html.join('') : '<tr><td class="prof-sub">Nothing matches.</td></tr>';
  updateCatalogCount();
}

function updateCatalogCount() {
  const n = catalogPicks.size;
  document.getElementById('importCount').textContent = n ? `${n} selected` : '';
  const go = document.getElementById('importGo');
  go.disabled = n === 0;
  go.textContent = n ? `Import ${n} profile${n === 1 ? '' : 's'}` : 'Import';
}

function findCatalogItem(key) {
  for (const f of catalogView.families) for (const v of f.versions) if (catalogKey(v.family, v.version, v.engine) === key) return v;
  return null;
}

document.getElementById('catalogTable').addEventListener('change', e => {
  const key = e.target.dataset.k;
  if (!key || !e.target.classList.contains('cat-check')) return;
  const item = findCatalogItem(key);
  if (!item) return;
  if (e.target.checked) {
    // A taken name offers "Import as" with a suggested new name; the profile is then one of the user's own.
    catalogPicks.set(key, { family: item.family, version: item.version, engine: item.engine, importAs: item.status === 'nameUsed' ? item.suggestedName : '', taken: item.status === 'nameUsed' });
  } else {
    catalogPicks.delete(key);
  }
  renderCatalog();
});

document.getElementById('catalogTable').addEventListener('input', e => {
  if (!e.target.classList.contains('cat-importas-input')) return;
  const pick = catalogPicks.get(e.target.dataset.k);
  if (pick) pick.importAs = e.target.value;
});

document.getElementById('catalogTable').addEventListener('click', e => {
  const link = e.target.closest('a.cat-older');
  if (!link) return;
  e.preventDefault();
  const family = link.dataset.family;
  if (catalogOlderShown.has(family)) catalogOlderShown.delete(family); else catalogOlderShown.add(family);
  renderCatalog();
});

document.getElementById('catalogSearch').addEventListener('input', renderCatalog);
document.getElementById('catalogSelectShown').addEventListener('click', () => {
  // Only profiles that can simply be added: not ones already installed, with a taken name, deprecated, or needing a newer Compressarr.
  for (const f of catalogView?.families || []) {
    for (const v of f.versions) {
      if (v.status === 'new' && !v.deprecated) catalogPicks.set(catalogKey(v.family, v.version, v.engine), { family: v.family, version: v.version, engine: v.engine, importAs: '', taken: false });
    }
  }
  renderCatalog();
});
document.getElementById('catalogClear').addEventListener('click', () => { catalogPicks.clear(); renderCatalog(); });

async function installFromCatalog() {
  const picks = [...catalogPicks.values()];
  const blank = picks.find(p => p.taken && !p.importAs.trim());
  if (blank) {
    setStatus(`Type a name to import "${blank.family} v${blank.version}" under.`, 'error');
    return;
  }

  const items = picks.map(p => ({ family: p.family, version: p.version, engine: p.engine, importAs: p.taken ? p.importAs.trim() : null }));
  const res = await fetch('/api/catalog/install', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ items }) });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) {
    setStatus(body.message || 'Could not add the profiles.', 'error');
    return;
  }

  const added = body.results.filter(r => r.status !== 'failed');
  const failed = body.results.filter(r => r.status === 'failed');
  if (added.length) await loadProfiles();
  if (failed.length) {
    // Stay in the dialog: the profiles that did not go in are still ticked-able, with the reason on screen.
    setStatus(failed.map(r => `${r.name}: ${r.message}`).join(' '), 'error');
    await loadCatalog();
    return;
  }

  closeImport();
  setStatus(`Added ${added.length} profile${added.length === 1 ? '' : 's'}${added.some(r => r.status === 'importedAsUser') ? ' (renamed ones are now your own profiles)' : ' from the catalog'}.`, true);
}

// A link from the About page (or the note above the list) opens the import dialog straight on the catalog.
if (new URLSearchParams(location.search).get('catalog') === '1') {
  overlay.classList.remove('hidden');
  showTab('catalog');
}
