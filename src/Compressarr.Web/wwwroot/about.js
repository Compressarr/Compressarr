renderNav('about');

const versionText = document.getElementById('versionText');
const updateStatus = document.getElementById('updateStatus');
const checkUpdateBtn = document.getElementById('checkUpdateBtn');

const downloadUpdateBtn = document.getElementById('downloadUpdateBtn');

const hbVersionText = document.getElementById('hbVersionText');
const hbUpdateStatus = document.getElementById('hbUpdateStatus');
const hbCheckUpdateBtn = document.getElementById('hbCheckUpdateBtn');
const hbDownloadUpdateBtn = document.getElementById('hbDownloadUpdateBtn');

// Same fade-after-a-few-seconds convention Settings uses for its own status messages
// (setPresetStatus/runMaintenanceAction) - a transient confirmation ("You're up to date") clears
// itself; anything the user still needs to act on (an error, or "a new version is available"
// sitting next to the now-visible Download Update button) stays up until the next check.
let updateStatusClearTimer = null;
let hbUpdateStatusClearTimer = null;

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
      updateStatus.textContent = `A new version is available: ${dto.latestVersion}.`;
      downloadUpdateBtn.href = dto.releaseUrl;
      downloadUpdateBtn.classList.remove('hidden');
    } else {
      updateStatus.textContent = `You're up to date (latest release: ${dto.latestVersion}).`;
      updateStatusClearTimer = setTimeout(() => { updateStatus.textContent = ''; }, 4000);
    }
  } catch (err) {
    updateStatus.textContent = `Couldn't check for updates: ${err.message}`;
  } finally {
    checkUpdateBtn.disabled = false;
  }
});

loadAbout();
loadHandBrakeVersion();

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
