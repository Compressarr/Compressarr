renderNav('profiles');

// success: true = green, 'error' = red, anything else = plain (progress such as "Saving...").
function setStatus(text, success) { setStatusMessage(text, success === 'error' ? 'error' : success ? 'success' : ''); }

const params = new URLSearchParams(location.search);
const isNew = params.get('new') === '1';
const originalName = params.get('name');

const $ = id => document.getElementById(id);

// ---- Option tables (checked against HandBrakeCLI --encoder-*-list) --------------------------------
const VIDEO_ENCODERS = [
  ['x264', 'H.264 (x264)'], ['x264_10bit', 'H.264 10-bit (x264)'],
  ['x265', 'H.265 8-bit (x265)'], ['x265_10bit', 'H.265 10-bit (x265)'], ['x265_12bit', 'H.265 12-bit (x265)'],
  ['svt_av1', 'AV1 (SVT-AV1)'], ['svt_av1_10bit', 'AV1 10-bit (SVT-AV1)'],
  ['nvenc_h264', 'H.264 (NVIDIA NVENC)'], ['nvenc_h265', 'H.265 (NVIDIA NVENC)'], ['nvenc_h265_10bit', 'H.265 10-bit (NVIDIA NVENC)'],
  ['nvenc_av1', 'AV1 (NVIDIA NVENC)'], ['nvenc_av1_10bit', 'AV1 10-bit (NVIDIA NVENC)']
];

const X26_SPEEDS = ['ultrafast', 'superfast', 'veryfast', 'faster', 'fast', 'medium', 'slow', 'slower', 'veryslow', 'placebo'];
const NVENC_SPEEDS = ['fastest', 'faster', 'fast', 'medium', 'slow', 'slower', 'slowest'];
const SVT_SPEEDS = ['10', '9', '8', '7', '6', '5', '4', '3', '2', '1', '0', '-1'];
const X26_TUNES = { x264: ['film', 'animation', 'grain', 'stillimage', 'psnr', 'ssim', 'fastdecode', 'zerolatency'], x265: ['psnr', 'ssim', 'grain', 'zerolatency', 'fastdecode', 'animation'] };
const LEVELS_H26 = ['1.0', '1b', '1.1', '1.2', '1.3', '2.0', '2.1', '2.2', '3.0', '3.1', '3.2', '4.0', '4.1', '4.2', '5.0', '5.1', '5.2', '6.0', '6.1', '6.2'];
const LEVELS_H265 = ['1.0', '2.0', '2.1', '3.0', '3.1', '4.0', '4.1', '5.0', '5.1', '5.2', '6.0', '6.1', '6.2'];
const LEVELS_AV1 = ['2.0', '2.1', '2.2', '2.3', '3.0', '3.1', '3.2', '3.3', '4.0', '4.1', '4.2', '4.3', '5.0', '5.1', '5.2', '5.3', '6.0', '6.1', '6.2', '6.3'];

// Per encoder: speeds, profiles, levels, tunes ('' = none/default), the default speed for a new choice, and the RF range.
function encoderInfo(enc) {
  if (enc === 'x264') return { speeds: X26_SPEEDS, profiles: ['baseline', 'main', 'high', 'high422', 'high444'], levels: LEVELS_H26, tunes: X26_TUNES.x264, noneTune: true, defSpeed: 'medium', rfMax: 51 };
  if (enc === 'x264_10bit') return { speeds: X26_SPEEDS, profiles: ['high10', 'high422', 'high444'], levels: LEVELS_H26, tunes: X26_TUNES.x264, noneTune: true, defSpeed: 'medium', rfMax: 51 };
  if (enc === 'x265') return { speeds: X26_SPEEDS, profiles: ['main', 'mainstillpicture', 'main444-8', 'main444-intra'], levels: LEVELS_H265, tunes: X26_TUNES.x265, noneTune: true, defSpeed: 'medium', rfMax: 51 };
  if (enc === 'x265_10bit') return { speeds: X26_SPEEDS, profiles: ['main10', 'main10-intra', 'main422-10', 'main422-10-intra', 'main444-10', 'main444-10-intra'], levels: LEVELS_H265, tunes: X26_TUNES.x265, noneTune: true, defSpeed: 'medium', rfMax: 51 };
  if (enc === 'x265_12bit') return { speeds: X26_SPEEDS, profiles: ['main12', 'main12-intra', 'main422-12', 'main422-12-intra', 'main444-12', 'main444-12-intra'], levels: LEVELS_H265, tunes: X26_TUNES.x265, noneTune: true, defSpeed: 'medium', rfMax: 51 };
  if (enc.startsWith('svt_av1')) return { speeds: SVT_SPEEDS, profiles: ['main'], levels: LEVELS_AV1, tunes: ['vq', 'psnr', 'ssim', 'iq', 'ms-ssim', 'fastdecode'], noneTune: false, defSpeed: '6', rfMax: 63 };
  if (enc === 'nvenc_h264') return { speeds: NVENC_SPEEDS, profiles: ['baseline', 'main', 'high'], levels: LEVELS_H26, tunes: [], noneTune: true, defSpeed: 'medium', rfMax: 51 };
  if (enc === 'nvenc_h265') return { speeds: NVENC_SPEEDS, profiles: ['main'], levels: LEVELS_H265, tunes: [], noneTune: true, defSpeed: 'medium', rfMax: 51 };
  if (enc === 'nvenc_h265_10bit') return { speeds: NVENC_SPEEDS, profiles: ['main10'], levels: LEVELS_H265, tunes: [], noneTune: true, defSpeed: 'medium', rfMax: 51 };
  if (enc.startsWith('nvenc_av1')) return { speeds: NVENC_SPEEDS, profiles: [], levels: LEVELS_AV1, tunes: [], noneTune: true, defSpeed: 'medium', rfMax: 63 };
  return { speeds: [], profiles: [], levels: [], tunes: [], noneTune: true, defSpeed: '', rfMax: 70 };
}

const FRAMERATES = ['5', '10', '12', '15', '23.976', '24', '25', '29.97', '30', '50', '59.94', '60'];
const AUDIO_ENCODERS = [['av_aac', 'AAC'], ['ac3', 'AC3'], ['eac3', 'E-AC3'], ['opus', 'Opus'], ['mp3', 'MP3'], ['flac16', 'FLAC 16-bit'], ['flac24', 'FLAC 24-bit'], ['vorbis', 'Vorbis'], ['truehd', 'TrueHD']];
const MIXDOWNS = [['mono', 'Mono'], ['stereo', 'Stereo'], ['dpl1', 'Dolby Surround'], ['dpl2', 'Dolby Pro Logic II'], ['5point1', '5.1'], ['6point1', '6.1'], ['7point1', '7.1'], ['5_2_lfe', '5.2 (LFE)']];
const PASS_CODECS = ['aac', 'ac3', 'eac3', 'dts', 'dtshd', 'truehd', 'flac', 'mp3', 'mp2', 'opus', 'vorbis'];
const CONTAINERS = [['av_mkv', 'MKV'], ['av_mp4', 'MP4']];
const CROP_MODES = [[0, 'Automatic'], [2, 'None']];

// ---- Helpers ---------------------------------------------------------------------------------------
function fillSelect(el, pairs, current) {
  const options = pairs.map(p => Array.isArray(p) ? p : [p, p]);
  if (current !== undefined && current !== null && !options.some(([v]) => String(v) === String(current))) {
    options.push([current, `${current} (current)`]);
  }
  el.innerHTML = options.map(([v, l]) => `<option value="${escapeAttr(v)}">${escapeHtml(l)}</option>`).join('');
  if (current !== undefined && current !== null) el.value = String(current);
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function escapeAttr(s) { return escapeHtml(s); }

function refillEncoderSelects(form) {
  const info = encoderInfo($('videoEncoder').value);
  const keep = form || {
    videoPreset: $('videoPreset').value, videoProfile: $('videoProfile').value, videoLevel: $('videoLevel').value, videoTune: $('videoTune').value
  };

  fillSelect($('videoPreset'), info.speeds, info.speeds.includes(keep.videoPreset) || form ? keep.videoPreset : info.defSpeed);
  fillSelect($('videoProfile'), [['auto', 'auto'], ...info.profiles], form || ['auto', ...info.profiles].includes(keep.videoProfile) ? keep.videoProfile : 'auto');
  fillSelect($('videoLevel'), [['auto', 'auto'], ...info.levels], form || ['auto', ...info.levels].includes(keep.videoLevel) ? keep.videoLevel : 'auto');
  const tunes = [[ '', info.noneTune ? 'none' : 'default' ], ...info.tunes.map(t => [t, t])];
  fillSelect($('videoTune'), tunes, form || tunes.some(([v]) => v === keep.videoTune) ? keep.videoTune : '');

  const rf = $('rf');
  rf.max = info.rfMax;
  if (Number(rf.value) > info.rfMax) rf.value = info.rfMax;
  $('rfOut').textContent = rf.value;
}

function syncVisibility() {
  const bitrate = $('qualityMode').value === 'bitrate';
  $('rfBlock').classList.toggle('hidden', bitrate);
  $('bitrateBlock').classList.toggle('hidden', !bitrate);

  $('framerateBlock').classList.toggle('hidden', $('framerateMode').value === 'vfr');

  const pass = $('audioMode').value === 'passthru';
  $('passBlock').classList.toggle('hidden', !pass);
  $('audioEncoderLabel').textContent = pass ? 'Otherwise encode as' : 'Encode as';

  const mp4 = $('fileFormat').value === 'av_mp4';
  $('optimize').disabled = !mp4 && !$('optimize').checked;
}

function fillForm(f) {
  $('name').value = f.name;
  $('description').value = f.description;
  fillSelect($('fileFormat'), CONTAINERS, f.fileFormat);
  $('optimize').checked = f.optimize;

  fillSelect($('videoEncoder'), VIDEO_ENCODERS, f.videoEncoder);
  refillEncoderSelects(f);
  $('qualityMode').value = f.qualityMode;
  $('rf').value = f.rf;
  $('rfOut').textContent = $('rf').value;
  $('videoBitrate').value = f.videoBitrate || '';
  $('framerateMode').value = f.framerateMode;
  fillSelect($('framerate'), FRAMERATES, f.framerate || '30');
  $('extraVideoOptions').value = f.extraVideoOptions;

  $('deinterlace').value = f.deinterlace;
  fillSelect($('cropMode'), CROP_MODES, f.cropMode);

  $('audioLanguages').value = f.audioLanguages;
  $('audioTracks').value = f.audioTracks;
  $('audioMode').value = f.audioMode;
  const codecs = [...new Set([...PASS_CODECS, ...f.passthroughCodecs])];
  $('passthrough').innerHTML = codecs.map(c => `<label><input type="checkbox" value="${escapeAttr(c)}" ${f.passthroughCodecs.includes(c) ? 'checked' : ''} />${escapeHtml(c)}</label>`).join('');
  fillSelect($('audioEncoder'), AUDIO_ENCODERS, f.audioEncoder);
  $('audioBitrate').value = f.audioBitrate;
  fillSelect($('audioMixdown'), MIXDOWNS, f.audioMixdown);

  $('subtitleLanguages').value = f.subtitleLanguages;
  $('subtitleTracks').value = f.subtitleTracks;
  $('chapterMarkers').checked = f.chapterMarkers;

  syncVisibility();
  buildCommand();
}

function readForm() {
  return {
    name: $('name').value,
    description: $('description').value,
    fileFormat: $('fileFormat').value,
    optimize: $('optimize').checked,
    videoEncoder: $('videoEncoder').value,
    videoPreset: $('videoPreset').value,
    videoProfile: $('videoProfile').value,
    videoLevel: $('videoLevel').value,
    videoTune: $('videoTune').value,
    extraVideoOptions: $('extraVideoOptions').value,
    qualityMode: $('qualityMode').value,
    rf: Number($('rf').value),
    videoBitrate: parseInt($('videoBitrate').value, 10) || 0,
    framerateMode: $('framerateMode').value,
    framerate: $('framerateMode').value === 'vfr' ? '' : $('framerate').value,
    deinterlace: $('deinterlace').value,
    cropMode: Number($('cropMode').value),
    audioLanguages: $('audioLanguages').value,
    audioTracks: $('audioTracks').value,
    audioMode: $('audioMode').value,
    passthroughCodecs: [...document.querySelectorAll('#passthrough input:checked')].map(i => i.value),
    audioEncoder: $('audioEncoder').value,
    audioBitrate: parseInt($('audioBitrate').value, 10) || 0,
    audioMixdown: $('audioMixdown').value,
    subtitleLanguages: $('subtitleLanguages').value,
    subtitleTracks: $('subtitleTracks').value,
    chapterMarkers: $('chapterMarkers').checked
  };
}

let activePath = '%AppData%\\Compressarr\\Profiles\\handbrake-active.json';

function buildCommand() {
  const f = readForm();
  const quality = f.qualityMode === 'bitrate' ? `${f.videoBitrate} kbps` : `RF ${f.rf}`;
  const name = f.name.trim() || '(unnamed)';
  $('cmd').textContent =
`HandBrakeCLI -i "<source file>" -t 1
  -o "<output file>"
  --preset-import-file "${activePath}"
  --preset "${name}"
  (this profile: ${f.videoEncoder}, ${quality}${f.videoPreset ? ', ' + f.videoPreset : ''}${f.videoTune ? ', tune ' + f.videoTune : ''})`;
}

const FIELD_MAP = {
  name: '#name', videoEncoder: '#videoEncoder', videoBitrate: '#videoBitrate', rf: '#rf', framerate: '#framerate',
  audioLanguages: '#audioLanguages', passthrough: '#passthrough', audioEncoder: '#audioEncoder',
  audioBitrate: '#audioBitrate', subtitleLanguages: '#subtitleLanguages'
};

let builtIn = false;
let dirty = false;
let loadedName = null;

async function loadProfile(name) {
  const res = await fetch(`/api/profiles/handbrake/${encodeURIComponent(name)}`);
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    setStatus(body.message || 'Could not load that profile.', 'error');
    return null;
  }
  return res.json();
}

// nav.js moves the page's own heading into the toolbar, so the title lives there once the page is built.
function setTitle(text) {
  const h = document.querySelector('.toolbar h1');
  if (h) h.textContent = text;
}

function showBanner(text) {
  const b = $('edBanner');
  b.textContent = text;
  b.classList.remove('hidden');
}

async function init() {
  if (isNew) {
    setTitle('New HandBrake profile');
    $('cardTitle').textContent = 'New profile';
    $('startFromRow').classList.remove('hidden');
    const list = await (await fetch('/api/profiles')).json();
    activePath = list.activePresetsPath || activePath;
    $('userFilePath').textContent = list.userFilePath;
    const names = list.profiles.map(p => p.name);
    fillSelect($('startFrom'), names, names.includes('Compressarr SD-HD') ? 'Compressarr SD-HD' : names[0]);
    await startFrom($('startFrom').value);
    $('name').focus();
    return;
  }

  if (!originalName) { location.href = '/profiles.html'; return; }
  const dto = await loadProfile(originalName);
  if (!dto) return;
  activePath = dto.activePresetsPath || activePath;
  builtIn = dto.builtIn;
  loadedName = dto.form.name;
  setTitle(builtIn ? 'HandBrake profile' : 'Edit HandBrake profile');
  $('cardTitle').innerHTML = `Profile: ${escapeHtml(dto.form.name)}`;
  fillForm(dto.form);
  if (builtIn) {
    $('formFields').disabled = true;
    $('saveBtn').classList.add('hidden');
    $('dupBtn').classList.remove('hidden');
    showBanner('This is a built-in profile: it ships with Compressarr and is locked. Duplicate it to make your own copy to change.');
  } else if (dto.usedBy.length) {
    showBanner(`Used by: ${dto.usedBy.join(', ')}. Changes apply to the next file each of those lanes encodes.`);
  }
  syncVisibility();
}

async function startFrom(base) {
  const dto = await loadProfile(base);
  if (!dto) return;
  dto.form.name = '';
  dto.form.description = '';
  fillForm(dto.form);
  dirty = false;
}

// ---- Events ----------------------------------------------------------------------------------------
$('startFrom').addEventListener('change', e => startFrom(e.target.value));

$('videoEncoder').addEventListener('change', () => { refillEncoderSelects(); buildCommand(); });
for (const id of ['qualityMode', 'framerateMode', 'audioMode', 'fileFormat']) $(id).addEventListener('change', syncVisibility);
$('rf').addEventListener('input', () => { $('rfOut').textContent = $('rf').value; });

document.querySelector('main').addEventListener('input', () => { dirty = true; buildCommand(); });
document.querySelector('main').addEventListener('change', () => { dirty = true; buildCommand(); });
window.addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });

$('cancelBtn').addEventListener('click', () => { location.href = '/profiles.html'; });

$('seeStored').addEventListener('click', async e => {
  e.preventDefault();
  const pre = $('storedJson');
  if (!pre.classList.contains('hidden')) { pre.classList.add('hidden'); return; }
  const name = isNew ? $('startFrom').value : originalName;
  const res = await fetch(`/api/profiles/handbrake/${encodeURIComponent(name)}/stored`);
  pre.textContent = res.ok ? JSON.stringify(await res.json(), null, 2) : 'Could not load it.';
  pre.classList.remove('hidden');
});

$('dupBtn').addEventListener('click', async () => {
  const res = await fetch(`/api/profiles/handbrake/${encodeURIComponent(originalName)}/duplicate`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}'
  });
  const body = await res.json().catch(() => ({}));
  if (res.ok) location.href = `/profile-edit.html?name=${encodeURIComponent(body.name)}`;
  else setStatus(body.message || 'Could not duplicate the profile.', 'error');
});

$('saveBtn').addEventListener('click', async () => {
  const form = readForm();
  setStatus('Saving...');
  const res = isNew
    ? await fetch('/api/profiles/handbrake', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ form, baseName: $('startFrom').value })
      })
    : await fetch(`/api/profiles/handbrake/${encodeURIComponent(originalName)}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ form, baseName: null })
      });
  const body = await res.json().catch(() => ({}));

  if (res.ok) {
    dirty = false;
    applyValidationIssues(document, [], FIELD_MAP, '');
    location.href = '/profiles.html';
    return;
  }

  if (body.validationIssues) applyValidationIssues(document, body.validationIssues, FIELD_MAP, '');
  setStatus(body.message || 'Could not save the profile.', 'error');
});

init();
