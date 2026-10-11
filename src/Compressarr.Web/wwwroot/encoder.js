renderNav('encoder');

// success: true = green, 'error' = red, anything else = plain (progress such as "Saving...").
function setStatus(text, success) { setStatusMessage(text, success === 'error' ? 'error' : success ? 'success' : ''); }

// Logical validation-issue field key -> the actual input it lives on.
const ENCODER_FIELD_MAP = {
  handBrakeCliPath: '#hbCliPath',
  handBrakeOptions: '#hbOptions',
  ffmpegPath: '#ffPath',
  ffmpegProbePath: '#ffProbePath',
  ffmpegOptions: '#ffOptions'
};

function applyEncoderValidation(issues) {
  return applyValidationIssues(document, issues, ENCODER_FIELD_MAP, 'Error in the encoder configuration, check fields below.');
}

let encoderDirty = false;
let lastDto = null;

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function plural(n, word) { return `${n} ${word}${n === 1 ? '' : 's'}`; }

function fillForm(dto) {
  lastDto = dto;
  document.getElementById('hbCliPath').value = dto.handBrakeCliPath;
  document.getElementById('hbOptions').value = dto.handBrakeOptions;
  document.getElementById('ffPath').value = dto.ffmpegPath || '';
  document.getElementById('ffProbePath').value = dto.ffmpegProbePath || '';
  document.getElementById('ffOptions').value = dto.ffmpegOptions || '';
  document.getElementById('ffChannel').value = dto.ffmpegChannel || 'stable';
  updateChannelNote();
  renderGlance(dto);
}

// Stable is the default and needs no mention. A non-default channel is stated plainly where Advanced is off, so a hidden
// setting is never silently in effect (with Advanced on, the select itself shows it).
function updateChannelNote() {
  const note = document.getElementById('ffChannelNote');
  const latest = document.getElementById('ffChannel').value === 'latest';
  note.classList.toggle('hidden', !latest);
  note.textContent = latest ? 'Download channel: Latest (development build).' : '';
}
document.getElementById('ffChannel').addEventListener('change', updateChannelNote);

function channelLabel(channel, branch) {
  return channel === 'latest' ? 'Latest (development)' : `Stable${branch ? ' ' + branch : ''}`;
}

function readForm() {
  // The read-only fields (profile counts, lanes) are ignored by the server on PUT; the record just needs values.
  return {
    handBrakeCliPath: document.getElementById('hbCliPath').value,
    handBrakeOptions: document.getElementById('hbOptions').value,
    builtInProfileCount: lastDto ? lastDto.builtInProfileCount : 0,
    userProfileCount: lastDto ? lastDto.userProfileCount : 0,
    handBrakeLanes: [],
    validationIssues: [],
    ffmpegPath: document.getElementById('ffPath').value,
    ffmpegProbePath: document.getElementById('ffProbePath').value,
    ffmpegOptions: document.getElementById('ffOptions').value,
    ffmpegChannel: document.getElementById('ffChannel').value
  };
}

function usedBy(el, lanes) {
  el.innerHTML = lanes.length
    ? `Used by: ${lanes.map(l => `<b>${escapeHtml(l)}</b>`).join(', ')}`
    : 'Not used by any enabled lane.';
}

async function renderGlance(dto) {
  const total = dto.builtInProfileCount + dto.userProfileCount;
  usedBy(document.getElementById('hbUsedBy'), dto.handBrakeLanes || []);
  usedBy(document.getElementById('ffUsedBy'), dto.ffmpegLanes || []);

  const dot = document.getElementById('hbDot');
  const line = document.getElementById('hbVersionLine');
  const found = !dto.validationIssues.some(i => i.field === 'handBrakeCliPath');
  dot.classList.toggle('ok', found);
  dot.classList.toggle('bad', !found);
  if (!found) {
    line.textContent = `HandBrakeCLI not found · ${plural(total, 'profile')}`;
  } else {
    line.textContent = plural(total, 'profile');
    try {
      const res = await fetch('/api/handbrake/installed-version');
      const body = await res.json();
      if (body.version) line.textContent = `HandBrakeCLI ${body.version} · ${plural(total, 'profile')}`;
    } catch { /* the version is a nicety - the profile count above already says enough */ }
  }

  loadFfmpegCapabilities(dto);
}

// What the installed ffmpeg can do - asked separately from the settings (it runs a few short processes).
async function loadFfmpegCapabilities(dto) {
  const dot = document.getElementById('ffDot');
  const line = document.getElementById('ffVersionLine');
  const detected = document.getElementById('ffDetected');
  const chips = document.getElementById('ffChips');
  const profiles = plural(dto.ffmpegProfileCount || 0, 'profile');

  try {
    const res = await fetch('/api/ffmpeg/capabilities');
    const caps = await res.json();
    if (!caps.found) {
      dot.classList.remove('ok');
      dot.classList.toggle('bad', (dto.ffmpegLanes || []).length > 0);
      line.textContent = `Not installed · ${profiles}`;
      detected.textContent = 'ffmpeg was not found at the path above, or the file there is not ffmpeg. It is optional - use Check/Install if you want to try it.';
      chips.innerHTML = '';
      return;
    }

    dot.classList.add('ok');
    dot.classList.remove('bad');
    line.textContent = `ffmpeg ${caps.version || ''} · ${profiles}`;
    detected.innerHTML = `<strong>ffmpeg ${escapeHtml(caps.version || '')}</strong>${caps.buildNote ? ` (${escapeHtml(caps.buildNote)})` : ''}`;
    chips.innerHTML = caps.chips.map(c =>
      `<span class="enc-chip ${c.ok ? 'ok' : 'no'}" title="${c.note ? escapeHtml(c.note) : ''}">${escapeHtml(c.name)} ${c.ok ? '&#10003;' : '&#10007;'}${c.note ? ` <span style="font-weight:400">(${escapeHtml(c.note)})</span>` : ''}</span>`).join('');

    const flagged = Object.entries(caps.profileWarnings || {});
    if (flagged.length) {
      detected.innerHTML += `<div class="enc-warn">Not usable with this build: ${flagged.map(([n, m]) => `<b>${escapeHtml(n)}</b> (needs ${escapeHtml(m.join(', '))})`).join('; ')}</div>`;
    }
  } catch {
    detected.textContent = 'Could not check ffmpeg just now.';
  }
}

async function loadEncoder() {
  const res = await fetch('/api/encoder');
  const dto = await res.json();
  fillForm(dto);
  encoderDirty = false;
  applyEncoderValidation(dto.validationIssues);
}

document.getElementById('saveBtn').addEventListener('click', async () => {
  setStatus('Saving...');
  const res = await fetch('/api/encoder', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(readForm())
  });
  if (res.ok) {
    const dto = await res.json();
    fillForm(dto);
    encoderDirty = false;
    if (!applyEncoderValidation(dto.validationIssues)) {
      setStatus('Encoder settings saved.', true);
    }
  } else {
    setStatus('Failed to save encoder settings.', 'error');
  }
});

document.getElementById('clearChangesBtn').addEventListener('click', async () => {
  if (encoderDirty && !confirm('Discard unsaved changes and reload the last saved settings?')) return;
  await loadEncoder();
  setStatus('Changes cleared.', true);
});

// Warn before leaving with unsaved edits, same as the Settings page.
const encoderMain = document.querySelector('main');
for (const eventName of ['input', 'change']) {
  encoderMain.addEventListener(eventName, () => { encoderDirty = true; });
}
window.addEventListener('beforeunload', e => {
  if (!encoderDirty) return;
  e.preventDefault();
  e.returnValue = '';
});

// Browse... next to each tool path: pick the program from this computer's folders (the app's own folder
// browser - a web page can't see the real path of a file chosen with the system dialog).
for (const btn of document.querySelectorAll('.browse-btn')) {
  btn.addEventListener('click', () => {
    const field = document.getElementById(btn.dataset.target);
    openFileBrowser(field.value, chosen => {
      field.value = chosen;
      field.dispatchEvent(new Event('input', { bubbles: true }));
    });
  });
}

document.getElementById('checkHandBrakeBtn').addEventListener('click', async () => {
  setStatus('Checking HandBrakeCLI...');
  const statusRes = await fetch('/api/handbrake/status');
  const statusBody = await statusRes.json();
  if (statusBody.exists) {
    setStatus('HandBrakeCLI already found at the configured path.', true);
    return;
  }

  const releaseRes = await fetch('/api/handbrake/latest-release');
  const release = await releaseRes.json();
  if (!release.available) {
    setStatus('No downloadable HandBrakeCLI build for this platform - on Linux, install it via your package manager or Flatpak.', 'error');
    return;
  }

  const confirmed = confirm(
    `Download and install HandBrakeCLI ${release.version}?\n\nFile: ${release.assetName}\nSize: ${release.sizeMb} MB\n\nInstalls into Compressarr's own folder - won't touch any existing HandBrake install.`
  );
  if (!confirmed) return;

  setStatus('Downloading and installing HandBrakeCLI...');
  const installRes = await fetch('/api/handbrake/install', { method: 'POST' });
  const installBody = await installRes.json();
  if (installRes.ok) {
    // The server already saved the new path; reload so the glance card and validation match.
    await loadEncoder();
    setStatus(`HandBrakeCLI ${installBody.version} installed.`, true);
  } else {
    setStatus('HandBrakeCLI install failed.', 'error');
  }
});

// ffmpeg: use the saved one if it works; else offer one already on this computer; else offer the
// managed download (after saying exactly what it is) - never a download without asking.
document.getElementById('checkFfmpegBtn').addEventListener('click', async () => {
  setStatus('Checking ffmpeg...');
  const channel = document.getElementById('ffChannel').value;
  const status = await (await fetch('/api/ffmpeg/status')).json();
  let switching = false;
  if (status.exists && status.probeExists) {
    // A file being there is not enough - ask it, so a wrong program (notepad.exe...) isn't called ffmpeg.
    const caps = await (await fetch('/api/ffmpeg/capabilities')).json().catch(() => ({ found: false }));
    if (!caps.found) {
      setStatus('There is a file at the configured ffmpeg path, but it did not answer like ffmpeg - check the path.', 'error');
      loadFfmpegCapabilities(lastDto || { ffmpegProfileCount: 0, ffmpegLanes: [] });
      return;
    }

    // One Compressarr installed that is on the other channel than the one selected, or has a newer build published:
    // offer the download, rather than just saying "found" and leaving the selection (or the update) with no effect.
    const upd = await (await fetch(`/api/ffmpeg/update-check?channel=${channel}`)).json().catch(() => ({}));
    if (upd.status !== 'otherchannel' && upd.status !== 'newer') {
      setStatus(`ffmpeg ${caps.version || ''} found at the configured path.`, true);
      loadFfmpegCapabilities(lastDto || { ffmpegProfileCount: 0, ffmpegLanes: [] });
      return;
    }
    const to = channelLabel(upd.channel, upd.branch);
    const question = upd.status === 'newer'
      ? `ffmpeg ${caps.version || ''} is installed, and a newer ${to} build is available (${upd.latestBuild}).\n\nDownload it and update?`
      : `The ffmpeg Compressarr installed came from the ${channelLabel(upd.installedChannel)} channel, but ${to} is selected.\n\nDownload the ${to} build and switch to it?`;
    if (!confirm(question)) {
      setStatus(`ffmpeg ${caps.version || ''} found at the configured path.`, true);
      loadFfmpegCapabilities(lastDto || { ffmpegProfileCount: 0, ffmpegLanes: [] });
      return;
    }
    switching = true;
  }

  const existing = switching ? { found: false } : await (await fetch('/api/ffmpeg/find')).json();
  if (existing.found) {
    if (confirm(`ffmpeg is already on this computer:\n\n${existing.path}\n${existing.probePath}\n\nUse it?`)) {
      document.getElementById('ffPath').value = existing.path;
      document.getElementById('ffProbePath').value = existing.probePath;
      encoderDirty = true;
      setStatus('Paths filled in - press Save Encoder Settings to keep them.', true);
      return;
    }
  }

  const release = await (await fetch(`/api/ffmpeg/latest-release?channel=${channel}`)).json();
  if (!release.available) {
    setStatus(release.error || 'No downloadable ffmpeg build for this platform - install ffmpeg with your package manager and set its path here.', 'error');
    return;
  }

  const confirmed = confirm(
    `Download ffmpeg?\n\nChannel: ${channelLabel(release.channel, release.branch)}\nBuild: ${release.name}\nFile: ${release.assetName}\nSize: about ${release.sizeMb} MB\nSource: ${release.releaseUrl}\n\n` +
    `${release.verified ? 'The download is checked against the checksum GitHub publishes for it.' : 'GitHub published no checksum for this file, so it cannot be verified and will not be installed.'}\n` +
    `It is installed into Compressarr's own folder (no administrator rights) - only ffmpeg, ffprobe and the license are kept. ffmpeg is licensed under the GPL.`
  );
  if (!confirmed) return;

  setStatus('Downloading ffmpeg - this can take a few minutes...');
  const res = await fetch(`/api/ffmpeg/install?channel=${channel}`, { method: 'POST' });
  const body = await res.json().catch(() => ({}));
  if (res.ok) {
    await loadEncoder();
    setStatus('ffmpeg installed.', true);
  } else {
    setStatus(body.message || 'The ffmpeg install failed.', 'error');
  }
});

loadEncoder();
