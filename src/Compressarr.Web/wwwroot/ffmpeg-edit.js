renderNav('profiles');

// success: true = green, 'error' = red, anything else = plain (progress such as "Saving...").
function setStatus(text, success) { setStatusMessage(text, success === 'error' ? 'error' : success ? 'success' : ''); }

const params = new URLSearchParams(location.search);
const isNew = params.get('new') === '1';
const originalName = params.get('name');
const $ = id => document.getElementById(id);

// ---- Option tables ---------------------------------------------------------------------------------
const CODECS = [
  ['libx265', 'H.265 (x265)'], ['libx264', 'H.264 (x264)'], ['libsvtav1', 'AV1 (SVT-AV1)'],
  ['hevc_nvenc', 'H.265 (NVIDIA NVENC)'], ['h264_nvenc', 'H.264 (NVIDIA NVENC)'], ['av1_nvenc', 'AV1 (NVIDIA NVENC)']
];
const X26_SPEEDS = ['ultrafast', 'superfast', 'veryfast', 'faster', 'fast', 'medium', 'slow', 'slower', 'veryslow', 'placebo'];

function codecInfo(codec) {
  switch (codec) {
    case 'libx264': return { speeds: X26_SPEEDS, defSpeed: 'medium', tunes: ['film', 'animation', 'grain', 'stillimage', 'psnr', 'ssim', 'fastdecode', 'zerolatency'], profiles: ['baseline', 'main', 'high', 'high10'], pix: ['yuv420p', 'yuv420p10le'], qMax: 51, defQ: 23 };
    case 'libx265': return { speeds: X26_SPEEDS, defSpeed: 'medium', tunes: ['psnr', 'ssim', 'grain', 'zerolatency', 'fastdecode', 'animation'], profiles: ['main', 'main10', 'main12'], pix: ['yuv420p', 'yuv420p10le', 'yuv420p12le'], qMax: 51, defQ: 24 };
    case 'libsvtav1': return { speeds: ['13', '12', '11', '10', '9', '8', '7', '6', '5', '4', '3', '2', '1', '0'], defSpeed: '6', tunes: ['vq', 'psnr', 'ssim', 'iq', 'ms-ssim', 'fastdecode'], profiles: [], pix: ['yuv420p', 'yuv420p10le'], qMax: 63, defQ: 30 };
    case 'hevc_nvenc': return { speeds: ['p1', 'p2', 'p3', 'p4', 'p5', 'p6', 'p7'], defSpeed: 'p5', tunes: [], profiles: ['main', 'main10'], pix: ['yuv420p', 'p010le'], qMax: 51, defQ: 28 };
    case 'h264_nvenc': return { speeds: ['p1', 'p2', 'p3', 'p4', 'p5', 'p6', 'p7'], defSpeed: 'p5', tunes: [], profiles: ['baseline', 'main', 'high'], pix: ['yuv420p'], qMax: 51, defQ: 26 };
    case 'av1_nvenc': return { speeds: ['p1', 'p2', 'p3', 'p4', 'p5', 'p6', 'p7'], defSpeed: 'p5', tunes: [], profiles: [], pix: ['yuv420p', 'p010le'], qMax: 63, defQ: 30 };
    default: return { speeds: [], defSpeed: '', tunes: [], profiles: [], pix: ['yuv420p'], qMax: 63, defQ: 24 };
  }
}

const FRAMERATES = ['5', '10', '12', '15', '23.976', '24', '25', '29.97', '30', '50', '59.94', '60'];
const AUDIO_CODECS = [['aac', 'AAC'], ['ac3', 'AC3'], ['eac3', 'E-AC3'], ['opus', 'Opus'], ['mp3', 'MP3'], ['flac', 'FLAC']];
const MIXDOWNS = [['same', 'Same as source'], ['mono', 'Mono'], ['stereo', 'Stereo'], ['5.1', '5.1'], ['7.1', '7.1']];
const PASS_CODECS = ['aac', 'ac3', 'eac3', 'dts', 'dtshd', 'truehd', 'flac', 'mp3', 'opus'];

// ---- Helpers ---------------------------------------------------------------------------------------
function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function fillSelect(el, pairs, current) {
  const options = pairs.map(p => Array.isArray(p) ? p : [p, p]);
  if (current !== undefined && current !== null && !options.some(([v]) => String(v) === String(current))) {
    options.push([current, `${current === '' ? '(none)' : current} (current)`]);
  }
  el.innerHTML = options.map(([v, l]) => `<option value="${escapeHtml(v)}">${escapeHtml(l)}</option>`).join('');
  if (current !== undefined && current !== null) el.value = String(current);
}

function splitList(text) {
  return text.split(/[,;\s]+/).map(s => s.trim()).filter(Boolean);
}

function refillCodecSelects(p) {
  const info = codecInfo($('videoCodec').value);
  const keep = p || { preset: $('preset').value, videoProfile: $('videoProfile').value, pixFmt: $('pixFmt').value, tune: $('tune').value };

  fillSelect($('preset'), info.speeds, p || info.speeds.includes(keep.preset) ? keep.preset : info.defSpeed);
  fillSelect($('videoProfile'), [['', 'encoder default'], ...info.profiles], p || ['', ...info.profiles].includes(keep.videoProfile) ? keep.videoProfile : '');
  fillSelect($('pixFmt'), [['', 'encoder default'], ...info.pix], p || ['', ...info.pix].includes(keep.pixFmt) ? keep.pixFmt : '');
  fillSelect($('tune'), [['', info.tunes.length ? 'none' : 'n/a'], ...info.tunes], p || ['', ...info.tunes].includes(keep.tune) ? keep.tune : '');

  const q = $('quality');
  q.max = info.qMax;
  if (Number(q.value) > info.qMax) q.value = info.qMax;
  $('qualityOut').textContent = q.value;
}

function syncVisibility() {
  const bitrate = $('qualityMode').value === 'bitrate';
  $('qualityBlock').classList.toggle('hidden', bitrate);
  $('bitrateBlock').classList.toggle('hidden', !bitrate);
  $('framerateBlock').classList.toggle('hidden', $('framerateMode').value !== 'cfr');
  const pass = $('audioMode').value === 'passthru';
  $('passBlock').classList.toggle('hidden', !pass);
  $('audioCodecLabel').textContent = pass ? 'Otherwise encode as' : 'Encode as';
  $('optimizeRow').classList.toggle('hidden', $('container').value !== 'mp4');
}

let hbPresets = [];

function fillForm(p) {
  $('name').value = p.name;
  $('description').value = p.description;
  $('container').value = p.container === 'mp4' ? 'mp4' : 'mkv';
  $('optimize').checked = p.optimize;

  fillSelect($('videoCodec'), CODECS, p.videoCodec);
  refillCodecSelects(p);
  $('qualityMode').value = p.qualityMode;
  $('quality').value = p.quality;
  $('qualityOut').textContent = $('quality').value;
  $('videoBitrate').value = p.videoBitrateKbps || '';
  $('framerateMode').value = p.framerateMode === 'cfr' ? 'cfr' : 'vfr';
  fillSelect($('framerate'), FRAMERATES, p.framerate ? String(p.framerate) : '30');

  $('deinterlace').value = p.deinterlace;
  $('crop').value = p.crop;

  $('audioLanguages').value = p.audioLanguages.join(', ');
  $('audioTracks').value = p.audioTracks;
  $('audioMode').value = p.audioMode;
  const codecs = [...new Set([...PASS_CODECS, ...p.passthroughCodecs])];
  $('passthrough').innerHTML = codecs.map(c => `<label><input type="checkbox" value="${escapeHtml(c)}" ${p.passthroughCodecs.includes(c) ? 'checked' : ''} />${escapeHtml(c)}</label>`).join('');
  fillSelect($('audioCodec'), AUDIO_CODECS, p.audioCodec);
  $('audioBitrate').value = p.audioBitrateKbps;
  fillSelect($('audioMixdown'), MIXDOWNS, p.audioMixdown);

  $('subtitleLanguages').value = p.subtitleLanguages.join(', ');
  $('subtitleTracks').value = p.subtitleTracks;
  $('chapters').checked = p.chapters;

  fillSelect($('fallbackHandBrakePreset'), [['', 'None - refuse such files'], ...hbPresets], p.fallbackHandBrakePreset || '');
  $('extraArgs').value = p.extraArgs;

  syncVisibility();
}

function readForm() {
  return {
    name: $('name').value,
    description: $('description').value,
    container: $('container').value,
    optimize: $('optimize').checked,
    videoCodec: $('videoCodec').value,
    preset: $('preset').value,
    tune: $('tune').value,
    videoProfile: $('videoProfile').value,
    pixFmt: $('pixFmt').value,
    qualityMode: $('qualityMode').value,
    quality: Number($('quality').value),
    videoBitrateKbps: parseInt($('videoBitrate').value, 10) || 0,
    framerateMode: $('framerateMode').value,
    framerate: $('framerateMode').value === 'cfr' ? Number($('framerate').value) : 0,
    deinterlace: $('deinterlace').value,
    crop: $('crop').value,
    audioLanguages: splitList($('audioLanguages').value),
    audioTracks: $('audioTracks').value,
    audioMode: $('audioMode').value,
    passthroughCodecs: [...document.querySelectorAll('#passthrough input:checked')].map(i => i.value),
    audioCodec: $('audioCodec').value,
    audioBitrateKbps: parseInt($('audioBitrate').value, 10) || 0,
    audioMixdown: $('audioMixdown').value,
    subtitleLanguages: splitList($('subtitleLanguages').value),
    subtitleTracks: $('subtitleTracks').value,
    chapters: $('chapters').checked,
    fallbackHandBrakePreset: $('fallbackHandBrakePreset').value,
    extraArgs: $('extraArgs').value
  };
}

const FIELD_MAP = {
  name: '#name', container: '#container', videoCodec: '#videoCodec', videoBitrate: '#videoBitrate', quality: '#quality',
  framerate: '#framerate', audioLanguages: '#audioLanguages', passthrough: '#passthrough', audioCodec: '#audioCodec',
  audioBitrate: '#audioBitrate', subtitleLanguages: '#subtitleLanguages'
};

let builtIn = false;
let dirty = false;

function setTitle(text) {
  const h = document.querySelector('.toolbar h1');
  if (h) h.textContent = text;
}

function showBanner(text) {
  const b = $('edBanner');
  b.textContent = text;
  b.classList.remove('hidden');
}

async function loadProfile(name) {
  const res = await fetch(`/api/profiles/ffmpeg/${encodeURIComponent(name)}`);
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    setStatus(body.message || 'Could not load that profile.', 'error');
    return null;
  }
  return res.json();
}

async function init() {
  hbPresets = await (await fetch('/api/presets')).json();

  if (isNew) {
    setTitle('New ffmpeg profile');
    $('cardTitle').textContent = 'New ffmpeg profile';
    $('startFromRow').classList.remove('hidden');
    const names = await (await fetch('/api/presets?engine=ffmpeg')).json();
    fillSelect($('startFrom'), names, names.includes('Compressarr SD-HD') ? 'Compressarr SD-HD' : names[0]);
    await startFrom($('startFrom').value);
    $('name').focus();
    return;
  }

  if (!originalName) { location.href = '/profiles.html'; return; }
  const dto = await loadProfile(originalName);
  if (!dto) return;
  builtIn = dto.builtIn;
  setTitle(builtIn ? 'ffmpeg profile' : 'Edit ffmpeg profile');
  $('cardTitle').textContent = `Profile: ${dto.profile.name}`;
  fillForm(dto.profile);
  if (builtIn) {
    $('formFields').disabled = true;
    $('saveBtn').classList.add('hidden');
    $('dupBtn').classList.remove('hidden');
    showBanner('This is a built-in profile: it ships with Compressarr and is locked. Duplicate it to make your own copy to change.');
  } else if (dto.usedBy.length) {
    showBanner(`Used by: ${dto.usedBy.join(', ')}. Changes apply to the next file each of those lanes encodes.`);
  }
}

async function startFrom(base) {
  const dto = await loadProfile(base);
  if (!dto) return;
  dto.profile.name = '';
  dto.profile.description = '';
  fillForm(dto.profile);
  dirty = false;
}

// ---- Events ----------------------------------------------------------------------------------------
$('startFrom').addEventListener('change', e => startFrom(e.target.value));
$('videoCodec').addEventListener('change', () => { refillCodecSelects(); $('quality').value = codecInfo($('videoCodec').value).defQ; $('qualityOut').textContent = $('quality').value; });
for (const id of ['qualityMode', 'framerateMode', 'audioMode', 'container']) $(id).addEventListener('change', syncVisibility);
$('quality').addEventListener('input', () => { $('qualityOut').textContent = $('quality').value; });
document.querySelector('main').addEventListener('input', () => { dirty = true; });
document.querySelector('main').addEventListener('change', () => { dirty = true; });
window.addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });
$('cancelBtn').addEventListener('click', () => { location.href = '/profiles.html'; });

$('dupBtn').addEventListener('click', async () => {
  const res = await fetch(`/api/profiles/ffmpeg/${encodeURIComponent(originalName)}/duplicate`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}'
  });
  const body = await res.json().catch(() => ({}));
  if (res.ok) location.href = `/ffmpeg-edit.html?name=${encodeURIComponent(body.name)}`;
  else setStatus(body.message || 'Could not duplicate the profile.', 'error');
});

$('saveBtn').addEventListener('click', async () => {
  const profile = readForm();
  setStatus('Saving...');
  const res = isNew
    ? await fetch('/api/profiles/ffmpeg', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(profile) })
    : await fetch(`/api/profiles/ffmpeg/${encodeURIComponent(originalName)}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(profile) });
  const body = await res.json().catch(() => ({}));

  if (res.ok) {
    dirty = false;
    location.href = '/profiles.html';
    return;
  }

  applyValidationIssues(document, body.validationIssues || [], FIELD_MAP, '');
  setStatus(body.message || 'Could not save the profile.', 'error');
});

// ---- Preview decisions -------------------------------------------------------------------------------
const ACTION_LABEL = { copy: 'copy', encode: 'encode', drop: 'drop' };

function decisionRows(items, kind) {
  return items.map(d => {
    const detail = [d.language, d.codec, d.channels ? `${d.channels}ch` : '', d.title].filter(Boolean).join(' · ');
    return `<tr class="act-${d.action}"><td>${kind} #${d.index}</td><td>${escapeHtml(detail)}</td><td><span class="prev-act ${d.action}">${ACTION_LABEL[d.action]}</span></td><td>${escapeHtml(d.reason)}</td></tr>`;
  }).join('');
}

$('previewBtn').addEventListener('click', async () => {
  const out = $('previewOut');
  out.innerHTML = '<div class="ed-hint">Reading the file&hellip;</div>';
  const res = await fetch('/api/profiles/ffmpeg/preview', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ profile: readForm(), path: $('previewPath').value })
  });
  const r = await res.json();
  if (!r.ok) {
    out.innerHTML = `<div class="banner err">${escapeHtml(r.error || 'Could not preview that file.')}</div>`;
    return;
  }

  const hdr = r.video.dynamicHdr
    ? `<div class="banner err">${escapeHtml(r.video.dynamicHdr)} found - ffmpeg would drop it. ${r.video.hasFallback ? 'This profile hands such files to HandBrake (see the fallback profile below).' : 'This profile has no HandBrake fallback, so such files are refused and left untouched.'}</div>`
    : (r.video.hdr ? '<div class="ed-hint">HDR10 - colour information (and mastering-display data when present) is carried over.</div>' : '');

  out.innerHTML = `
    <div class="prev-file"><strong>${escapeHtml(r.file)}</strong>${r.duration ? ` · ${escapeHtml(r.duration)}` : ''}</div>
    <div class="ed-hint">Video #${r.video.index}: ${escapeHtml(r.video.codec)} ${escapeHtml(r.video.size)} ${escapeHtml(r.video.pixFmt)}</div>
    ${hdr}
    <table class="prev-table"><tbody>
      ${decisionRows(r.audio, 'Audio')}${decisionRows(r.subtitles, 'Subtitle')}${decisionRows(r.dropped, 'Stream')}
    </tbody></table>
    <div class="ed-hint"><strong>Deinterlace:</strong> ${r.deinterlace.apply ? 'yes' : 'no'} - ${escapeHtml(r.deinterlace.reason)}</div>
    <div class="ed-hint"><strong>Crop:</strong> ${r.crop.rect ? escapeHtml(r.crop.rect) + ' - ' : ''}${escapeHtml(r.crop.reason)}</div>
    <div class="ed-hint"><strong>Chapters:</strong> ${r.chapters ? 'kept' : 'none to keep (or turned off)'}</div>
    ${r.notes.map(n => `<div class="ed-hint">${escapeHtml(n)}</div>`).join('')}
    <div class="ed-hint" style="margin-top:8px"><strong>Command that would run</strong></div>
    <pre class="ed-cmd">${escapeHtml(r.command)}</pre>`;
});

init();
