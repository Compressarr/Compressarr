renderNav('about');

const versionText = document.getElementById('versionText');
const updateStatus = document.getElementById('updateStatus');
const checkUpdateBtn = document.getElementById('checkUpdateBtn');

const downloadUpdateBtn = document.getElementById('downloadUpdateBtn');

const hbVersionText = document.getElementById('hbVersionText');
const hbUpdateStatus = document.getElementById('hbUpdateStatus');
const hbCheckUpdateBtn = document.getElementById('hbCheckUpdateBtn');
const hbDownloadUpdateBtn = document.getElementById('hbDownloadUpdateBtn');

const ffVersionText = document.getElementById('ffVersionText');
const ffUpdateStatus = document.getElementById('ffUpdateStatus');
const ffCheckUpdateBtn = document.getElementById('ffCheckUpdateBtn');
const ffDownloadUpdateBtn = document.getElementById('ffDownloadUpdateBtn');

// Same fade-after-a-few-seconds convention Settings uses for its own status messages
// (setPresetStatus/runMaintenanceAction) - a transient confirmation ("You're up to date") clears
// itself; anything the user still needs to act on (an error, or "a new version is available"
// sitting next to the now-visible Download Update button) stays up until the next check.
let updateStatusClearTimer = null;
let hbUpdateStatusClearTimer = null;
let ffUpdateStatusClearTimer = null;

async function loadAbout() {
  const res = await fetch('/api/about');
  const dto = await res.json();
  versionText.textContent = `Version ${dto.version}`;
}

async function loadHandBrakeVersion() {
  const res = await fetch('/api/handbrake/installed-version');
  const dto = await res.json();
  hbVersionText.textContent = dto.version || 'Not found';
}

function ffChannelLabel(channel, branch) {
  return channel === 'latest' ? 'Latest (development)' : `Stable${branch ? ' ' + branch : ''}`;
}

async function loadFfmpegVersion() {
  const res = await fetch('/api/ffmpeg/installed-version');
  const dto = await res.json();
  ffVersionText.textContent = dto.version
    ? `${dto.version}${dto.buildNote ? ` (${dto.buildNote})` : ''}${dto.installedBuild ? ` - ${dto.installedBuild}` : ''}`
    : 'Not found';
}

// ffmpeg has no version number to compare (its version is a commit id), so a newer build is recognised from the build
// Compressarr recorded when it installed ffmpeg; an ffmpeg installed some other way can only be shown the latest build.
ffCheckUpdateBtn.addEventListener('click', async () => {
  ffCheckUpdateBtn.disabled = true;
  clearTimeout(ffUpdateStatusClearTimer);
  ffUpdateStatus.textContent = 'Checking...';
  ffDownloadUpdateBtn.classList.add('hidden');

  try {
    const res = await fetch('/api/ffmpeg/update-check');
    const dto = await res.json();

    if (dto.status === 'error') {
      ffUpdateStatus.textContent = `Couldn't check for updates: ${dto.error}`;
    } else if (dto.status === 'unavailable') {
      ffUpdateStatus.textContent = "Couldn't check for updates on this platform - install ffmpeg with your package manager.";
    } else if (dto.status === 'uptodate') {
      ffUpdateStatus.textContent = `You're up to date (${dto.latestBuild}).`;
      ffUpdateStatusClearTimer = setTimeout(() => { ffUpdateStatus.textContent = ''; }, 4000);
    } else {
      ffUpdateStatus.textContent = dto.status === 'newer'
        ? `A newer build is available: ${dto.latestBuild}. Check/Install on the Encoder page updates it.`
        : dto.status === 'otherchannel'
          ? `This ffmpeg came from the ${ffChannelLabel(dto.installedChannel)} channel; ${ffChannelLabel(dto.channel, dto.branch)} is selected. Check/Install on the Encoder page switches it.`
        : dto.status === 'notinstalled'
          ? `ffmpeg isn't installed. Latest available: ${dto.latestBuild}.`
          : `Latest build: ${dto.latestBuild}. This ffmpeg wasn't installed by Compressarr, so it can't be compared.`;
      ffDownloadUpdateBtn.href = dto.releaseUrl;
      ffDownloadUpdateBtn.classList.remove('hidden');
    }
  } catch (err) {
    ffUpdateStatus.textContent = `Couldn't check for updates: ${err.message}`;
  } finally {
    ffCheckUpdateBtn.disabled = false;
  }
});

hbCheckUpdateBtn.addEventListener('click', async () => {
  hbCheckUpdateBtn.disabled = true;
  clearTimeout(hbUpdateStatusClearTimer);
  hbUpdateStatus.textContent = 'Checking...';
  hbDownloadUpdateBtn.classList.add('hidden');

  try {
    const res = await fetch('/api/handbrake/latest-release');
    const dto = await res.json();

    if (!dto.available) {
      hbUpdateStatus.textContent = "Couldn't check for updates on this platform.";
    } else {
      const installedRes = await fetch('/api/handbrake/installed-version');
      const installedDto = await installedRes.json();
      const installed = installedDto.version;

      if (installed && installed === dto.version) {
        hbUpdateStatus.textContent = `You're up to date (latest: ${dto.version}).`;
        hbUpdateStatusClearTimer = setTimeout(() => { hbUpdateStatus.textContent = ''; }, 4000);
      } else {
        hbUpdateStatus.textContent = installed
          ? `A newer version is available: ${dto.version} (installed: ${installed}).`
          : `Latest available: ${dto.version}.`;
        hbDownloadUpdateBtn.href = dto.releaseUrl;
        hbDownloadUpdateBtn.classList.remove('hidden');
      }
    }
  } catch (err) {
    hbUpdateStatus.textContent = `Couldn't check for updates: ${err.message}`;
  } finally {
    hbCheckUpdateBtn.disabled = false;
  }
});

checkUpdateBtn.addEventListener('click', async () => {
  checkUpdateBtn.disabled = true;
  clearTimeout(updateStatusClearTimer);
  updateStatus.textContent = 'Checking...';
  downloadUpdateBtn.classList.add('hidden');

  try {
    const res = await fetch('/api/about/check-update');
    const dto = await res.json();

    if (!dto.checkedOk) {
      updateStatus.textContent = `Couldn't check for updates: ${dto.error}`;
    } else if (dto.hasUpdate) {
      updateStatus.textContent = `A new version is available: ${dto.latestVersion}${dto.latestVersion.includes('-') ? ' (beta)' : ''}.`;
      downloadUpdateBtn.href = dto.releaseUrl;
      downloadUpdateBtn.classList.remove('hidden');
    } else {
      updateStatus.textContent = `You're up to date (latest ${dto.channel === 'Beta' ? 'release or beta' : 'release'}: ${dto.latestVersion}).`;
      updateStatusClearTimer = setTimeout(() => { updateStatus.textContent = ''; }, 4000);
    }
  } catch (err) {
    updateStatus.textContent = `Couldn't check for updates: ${err.message}`;
  } finally {
    checkUpdateBtn.disabled = false;
  }
});

// ---- Preset catalog ---------------------------------------------------------------------------------------------
// Never checked by itself: the source is shown from the start, and the catalog is read only when Check for Updates is pressed.
const catSourceText = document.getElementById('catSourceText');
const catStatus = document.getElementById('catStatus');
const catCheckBtn = document.getElementById('catCheckBtn');
const catOpenLink = document.getElementById('catOpenLink');

function escapeAboutHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

async function loadCatalogSource() {
  try {
    const source = await (await fetch('/api/catalog/source')).json();
    catSourceText.innerHTML = `<strong>${escapeAboutHtml(source.name)}</strong> - <a href="${escapeAboutHtml(source.url)}" target="_blank" rel="noopener">${escapeAboutHtml(source.url)}</a>`;
  } catch { catSourceText.textContent = '-'; }
}

catCheckBtn.addEventListener('click', async () => {
  catCheckBtn.disabled = true;
  catStatus.textContent = 'Checking...';
  catOpenLink.classList.add('hidden');
  try {
    const dto = await (await fetch('/api/catalog')).json();
    if (!dto.ok) {
      catStatus.textContent = `Couldn't check the catalog: ${dto.error}`;
    } else {
      const c = dto.catalog;
      const total = c.families.reduce((n, f) => n + f.versions.length, 0);
      const updated = c.updated ? ` (updated ${c.updated})` : '';
      catStatus.textContent = c.notInstalled === 0
        ? `You have every profile in the catalog (${total} published${updated}).`
        : `${c.notInstalled} of ${total} profile${total === 1 ? '' : 's'} in the catalog you haven't added${updated}.`;
      // The Profiles page shows a quiet note about it, only because a check was just run (nothing runs on a timer).
      try { if (c.notInstalled > 0) sessionStorage.setItem('compressarr.catalogNew', String(c.notInstalled)); else sessionStorage.removeItem('compressarr.catalogNew'); } catch { /* a nicety */ }
      catOpenLink.classList.remove('hidden');
    }
  } catch (err) {
    catStatus.textContent = `Couldn't check the catalog: ${err.message}`;
  } finally {
    catCheckBtn.disabled = false;
  }
});

loadAbout();
loadCatalogSource();
loadHandBrakeVersion();
loadFfmpegVersion();

// Whether announcements are on, and when they were last checked - the checkbox itself is on the Settings page.
(async () => {
  const el = document.getElementById('announcementsStatus');
  if (!el) return;
  try {
    const view = await (await fetch('/api/announcements')).json();
    el.textContent = '';
    if (!view.enabled) {
      el.append('Off. ');
    } else {
      el.append(view.lastCheckedUtc ? `On - last checked ${new Date(view.lastCheckedUtc).toLocaleString()}. ` : 'On - not checked yet. ');
      if (view.needsNewerApp) el.append('A newer Compressarr is needed to read the latest ones. ');
    }
    const link = document.createElement('a');
    link.href = '/index.html';
    link.textContent = 'Change in Settings';
    el.append(link);
  } catch {
    el.textContent = '-';
  }
})();
