renderNav('notifications');

const channelsList = document.getElementById('channelsList');
const addChannelType = document.getElementById('addChannelType');

let notifierTypes = []; // [{type, displayName, fields: [{key,label,inputType,required,secret,options}]}]

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

// ---- Toast toggle - a single setting, saved immediately on change rather than needing its own
// Save button, same "act right away" feel Reload/Test buttons elsewhere in the app already have.
async function loadToastSetting() {
  const res = await fetch('/api/notifications/settings');
  const dto = await res.json();
  document.getElementById('toastEnabled').checked = dto.toastEnabled;
  document.getElementById('toastDigestDaily').checked = dto.toastDigestDailyEnabled;
  document.getElementById('toastDigestWeekly').checked = dto.toastDigestWeeklyEnabled;
  document.getElementById('toastDigestDailyTime').value = dto.toastDigestDailyTime;
  document.getElementById('toastDigestWeeklyTime').value = dto.toastDigestWeeklyTime;
  document.getElementById('toastDigestWeeklyDay').value = dto.toastDigestWeeklyDay;
}

async function saveToastSettings(message) {
  // The PUT endpoint replaces the whole NotificationSettingsDto, not just the fields named here -
  // start from the current saved settings so this save can't silently reset MessageStyle/
  // CustomTitleTemplate/CustomBodyTemplate back to their defaults (see saveMessageFormatSettings,
  // which has the identical concern in reverse).
  const currentRes = await fetch('/api/notifications/settings');
  const current = await currentRes.json();

  await fetch('/api/notifications/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ...current,
      toastEnabled: document.getElementById('toastEnabled').checked,
      toastDigestDailyEnabled: document.getElementById('toastDigestDaily').checked,
      toastDigestWeeklyEnabled: document.getElementById('toastDigestWeekly').checked,
      toastDigestDailyTime: document.getElementById('toastDigestDailyTime').value || '09:00',
      toastDigestWeeklyTime: document.getElementById('toastDigestWeeklyTime').value || '09:00',
      toastDigestWeeklyDay: document.getElementById('toastDigestWeeklyDay').value
    })
  });
  setStatusMessage(message, 'success');
}

document.getElementById('toastEnabled').addEventListener('change', e => {
  saveToastSettings(e.target.checked ? 'Toast notifications enabled.' : 'Toast notifications disabled.');
});

document.getElementById('toastDigestSaveBtn').addEventListener('click', () => saveToastSettings('Digest settings saved.'));

document.getElementById('toastDigestClearBtn').addEventListener('click', () => {
  document.getElementById('toastDigestDaily').checked = false;
  document.getElementById('toastDigestWeekly').checked = false;
});

// ---- Message Format - which Title/Body pair the pluggable Channels use. Built-in presets'
// actual wording comes from the server (single source of truth, see NotificationMessagePresets)
// rather than being duplicated here; only the Custom template fields are ever user-edited.
let messagePresets = {}; // { Minimal: {title, body}, Standard: {...}, Detailed: {...} }

// Illustrative numbers only, for the live preview - never sent anywhere, never real run data.
const PREVIEW_SAMPLE = {
  run_number: '47', files: '12', saved_gb: '8.42', saved_pct: '31.6',
  before_gb: '26.7', after_gb: '18.3', duration: '1h 12m', outcome: 'Success',
  error_count: '0', warning_count: '1', retries_succeeded: '2',
  report_path: 'C:\\Compressarr\\Reports\\Compressarr_2026-09-11_Report.html',
  today_files: '15', today_saved_gb: '10.1', month_files: '212', month_saved_gb: '143.8',
  year_files: '1904', year_saved_gb: '1207.5'
};

// The computer's own name, fetched once, used only to pre-fill an empty Machine name field when the
// user ticks Include machine name (they can edit it, and nothing is sent until they save).
let suggestedMachineName = '';
async function loadSuggestedMachineName() {
  try {
    const res = await fetch('/api/notifications/machine-name-suggestion');
    if (res.ok) suggestedMachineName = (await res.json()).name || '';
  } catch { /* the field just starts empty */ }
}

// What the machine name field holds right now, or '' when the box is unticked - the same rule the
// server applies (it also removes square brackets and caps the length).
function currentMachineName() {
  if (!document.getElementById('includeMachineName').checked) return '';
  return document.getElementById('machineName').value.replace(/[\[\]]/g, '').replace(/\s+/g, ' ').trim().slice(0, 40);
}

function renderPreviewTemplate(template) {
  let result = template || '';
  for (const [token, value] of Object.entries(PREVIEW_SAMPLE)) {
    result = result.split(`{${token}}`).join(value);
  }
  return result.split('{machine}').join(currentMachineName());
}

function updateMessagePreview() {
  const style = document.getElementById('messageStyle').value;
  const isCustom = style === 'Custom';
  document.getElementById('customTemplateFields').hidden = !isCustom;

  document.getElementById('machineNameFields').hidden = !document.getElementById('includeMachineName').checked;

  const { title, body } = isCustom
    ? { title: document.getElementById('customTitleTemplate').value, body: document.getElementById('customBodyTemplate').value }
    : (messagePresets[style] || { title: '', body: '' });

  // The built-in styles get a "[Name] " prefix; a Custom template places {machine} itself.
  const machine = currentMachineName();
  const prefix = !isCustom && machine ? `[${machine}] ` : '';

  document.getElementById('messagePreview').textContent =
    `${prefix}${renderPreviewTemplate(title)}\n${renderPreviewTemplate(body)}`;
}

async function loadMessageFormatSettings() {
  const [presetsRes, settingsRes] = await Promise.all([
    fetch('/api/notifications/message-presets'),
    fetch('/api/notifications/settings')
  ]);
  messagePresets = await presetsRes.json();
  const dto = await settingsRes.json();

  document.getElementById('messageStyle').value = dto.messageStyle;
  document.getElementById('customTitleTemplate').value = dto.customTitleTemplate;
  document.getElementById('customBodyTemplate').value = dto.customBodyTemplate;
  document.getElementById('includeMachineName').checked = !!dto.includeMachineName;
  document.getElementById('machineName').value = dto.machineName || '';
  updateMessagePreview();
}

async function saveMessageFormatSettings() {
  const settingsRes = await fetch('/api/notifications/settings');
  const current = await settingsRes.json();

  await fetch('/api/notifications/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ...current,
      messageStyle: document.getElementById('messageStyle').value,
      customTitleTemplate: document.getElementById('customTitleTemplate').value,
      customBodyTemplate: document.getElementById('customBodyTemplate').value,
      includeMachineName: document.getElementById('includeMachineName').checked,
      machineName: document.getElementById('machineName').value
    })
  });
  setStatusMessage('Message format saved.', 'success');
}

document.getElementById('messageStyle').addEventListener('change', updateMessagePreview);
document.getElementById('customTitleTemplate').addEventListener('input', updateMessagePreview);
document.getElementById('customBodyTemplate').addEventListener('input', updateMessagePreview);
document.getElementById('machineName').addEventListener('input', updateMessagePreview);
document.getElementById('includeMachineName').addEventListener('change', () => {
  // Ticking the box pre-fills an empty name with this computer's name as a starting point (editable).
  const field = document.getElementById('machineName');
  if (document.getElementById('includeMachineName').checked && !field.value.trim()) field.value = suggestedMachineName;
  updateMessagePreview();
});
document.getElementById('messageFormatSaveBtn').addEventListener('click', saveMessageFormatSettings);
loadSuggestedMachineName();

document.getElementById('toastDigestTestBtn').addEventListener('click', async () => {
  const weeklyFlags = [];
  if (document.getElementById('toastDigestDaily').checked) weeklyFlags.push(false);
  if (document.getElementById('toastDigestWeekly').checked) weeklyFlags.push(true);
  if (weeklyFlags.length === 0) weeklyFlags.push(false);

  setStatusMessage('Testing...', '');

  const results = [];
  for (const weekly of weeklyFlags) {
    const res = await fetch('/api/notifications/digest-test/toast', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ weekly })
    });
    results.push(await res.json());
  }
  setStatusMessage(results.map(r => r.message).join(' | '), results.every(r => r.success) ? 'success' : 'error');
});

// ---- Notification channels - a dynamic list, each channel's own field set driven entirely by
// notifierTypes (fetched once from the server) rather than any hardcoded per-type HTML, so a
// future channel type needs no frontend changes at all.

function helpTagHtml(helpText) {
  if (!helpText) return '';
  const esc = escapeHtml(helpText);
  return `<span class="help-tag" tabindex="0" title="${esc}">?<span class="help-tip">${esc}</span></span>`;
}

function fieldInputHtml(field, value) {
  const val = value || '';
  const placeholder = field.placeholder ? ` placeholder="${escapeHtml(field.placeholder)}"` : '';
  if (field.inputType === 'textarea') {
    return `<textarea class="f-setting" data-key="${field.key}" rows="3"${placeholder}>${escapeHtml(val)}</textarea>`;
  }
  if (field.inputType === 'select') {
    const options = (field.options || []).map(o => `<option value="${escapeHtml(o)}" ${o === val ? 'selected' : ''}>${escapeHtml(o)}</option>`).join('');
    return `<select class="f-setting" data-key="${field.key}">${options}</select>`;
  }
  const inputType = field.inputType === 'password' || field.secret ? 'password' : 'text';
  return `<input type="${inputType}" class="f-setting" data-key="${field.key}" value="${escapeHtml(val)}"${placeholder} />`;
}

function channelCardFromDto(dto) {
  const typeInfo = notifierTypes.find(t => t.type === dto.type);
  const fields = typeInfo ? typeInfo.fields : [];
  const typeLabel = typeInfo ? typeInfo.displayName : dto.type;

  const node = document.createElement('div');
  node.className = 'lane-card';
  node.dataset.id = dto.id;
  node.dataset.type = dto.type;

  const fieldBlocks = fields.map(f => `
    <div class="field-block">
      <label>${escapeHtml(f.label)}${helpTagHtml(f.helpText)}</label>
      ${fieldInputHtml(f, dto.settings[f.key])}
    </div>
  `).join('');

  node.innerHTML = `
    <div class="lane-head">
      <div class="channel-head-field">
        <label>Trigger${helpTagHtml('When this channel fires: on every run, only when a run has errors or warnings, or never (disabled without deleting it).')}</label>
        <select class="f-trigger">
          <option value="Always">Always</option>
          <option value="OnError">On error or warning</option>
          <option value="Never">Never (disabled)</option>
        </select>
      </div>
      <div class="channel-head-field">
        <label>Name${helpTagHtml('A friendly name to tell channels of the same type apart, e.g. two different Discord servers.')}</label>
        <input type="text" class="f-displayName lane-name-input" placeholder="Channel name" />
      </div>
      <span style="color: var(--text-dim); font-size: 11.5px; white-space: nowrap;">${escapeHtml(typeLabel)}</span>
      <div class="lane-head-spacer"></div>
      <div class="lane-head-actions">
        <button type="button" class="test-channel-btn icon-btn">Test</button>
        <button type="button" class="save-channel-btn icon-btn save">Save</button>
        <button type="button" class="remove-channel-btn icon-btn remove">Remove</button>
      </div>
    </div>
    <div class="lane-body">
      <div class="digest-divider"></div>
      <div class="digest-row">
        <span class="digest-row-label">Receive Digest:</span>
        <label class="check-row inline"><input type="checkbox" class="f-digest-daily" /> Daily</label>
        <span>at</span>
        <input type="time" class="f-digest-daily-time" />
        <label class="check-row inline"><input type="checkbox" class="f-digest-weekly" /> Weekly</label>
        <span>on</span>
        <select class="f-digest-weekly-day">
          <option value="Sunday">Sunday</option>
          <option value="Monday">Monday</option>
          <option value="Tuesday">Tuesday</option>
          <option value="Wednesday">Wednesday</option>
          <option value="Thursday">Thursday</option>
          <option value="Friday">Friday</option>
          <option value="Saturday">Saturday</option>
        </select>
        <span>at</span>
        <input type="time" class="f-digest-weekly-time" />
        <div class="digest-row-spacer"></div>
        <button type="button" class="digest-test-btn icon-btn">Test</button>
        <button type="button" class="digest-save-btn icon-btn save">Save</button>
        <button type="button" class="digest-clear-btn icon-btn">Clear</button>
      </div>
      <div class="digest-divider"></div>
      ${fieldBlocks}
    </div>
  `;

  node.querySelector('.f-displayName').value = dto.displayName;
  node.querySelector('.f-trigger').value = dto.trigger;
  node.querySelector('.f-digest-daily').checked = dto.digestDailyEnabled;
  node.querySelector('.f-digest-weekly').checked = dto.digestWeeklyEnabled;
  node.querySelector('.f-digest-daily-time').value = dto.digestDailyTime || '09:00';
  node.querySelector('.f-digest-weekly-time').value = dto.digestWeeklyTime || '09:00';
  node.querySelector('.f-digest-weekly-day').value = dto.digestWeeklyDay || 'Monday';

  node.querySelector('.save-channel-btn').addEventListener('click', () => saveChannel(node));
  node.querySelector('.remove-channel-btn').addEventListener('click', () => removeChannel(node));
  node.querySelector('.test-channel-btn').addEventListener('click', () => testChannel(node));

  node.querySelector('.digest-save-btn').addEventListener('click', () => saveChannel(node));
  node.querySelector('.digest-test-btn').addEventListener('click', () => testChannelDigest(node));
  node.querySelector('.digest-clear-btn').addEventListener('click', () => {
    node.querySelector('.f-digest-daily').checked = false;
    node.querySelector('.f-digest-weekly').checked = false;
  });

  return node;
}

function readChannelCard(node) {
  const settings = {};
  for (const el of node.querySelectorAll('.f-setting')) {
    settings[el.dataset.key] = el.value;
  }
  return {
    id: node.dataset.id,
    type: node.dataset.type,
    displayName: node.querySelector('.f-displayName').value,
    trigger: node.querySelector('.f-trigger').value,
    settings,
    digestDailyEnabled: node.querySelector('.f-digest-daily').checked,
    digestWeeklyEnabled: node.querySelector('.f-digest-weekly').checked,
    digestDailyTime: node.querySelector('.f-digest-daily-time').value || '09:00',
    digestWeeklyTime: node.querySelector('.f-digest-weekly-time').value || '09:00',
    digestWeeklyDay: node.querySelector('.f-digest-weekly-day').value
  };
}

// Every notifier field already carries its own `required` flag (NotifierFieldDto), sent to the
// browser but never read until now. A missing required value is knowable without a round trip -
// unlike a HandBrake/FileBot path's existence, which can only be checked server-side - so this
// runs client-side, right before the PUT that would otherwise just fail the same check on the
// far end. Warn, don't block, same as everywhere else: the save below still happens either way.
function requiredChannelFieldIssues(dto) {
  const typeInfo = notifierTypes.find(t => t.type === dto.type);
  const fieldMap = {};
  const issues = [];
  for (const field of (typeInfo ? typeInfo.fields : [])) {
    if (!field.required) continue;
    fieldMap[field.key] = `.f-setting[data-key="${field.key}"]`;
    if (!(dto.settings[field.key] || '').trim()) {
      issues.push({ field: field.key, message: `${field.label} is required.` });
    }
  }
  return { issues, fieldMap };
}

async function saveChannel(node) {
  const dto = readChannelCard(node);

  const { issues, fieldMap } = requiredChannelFieldIssues(dto);
  const hasIssues = applyValidationIssues(node, issues, fieldMap, '');

  setStatusMessage(`Saving "${dto.displayName}"...`, '');
  const res = await fetch(`/api/notifications/channels/${dto.id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(dto)
  });

  if (!res.ok) {
    setStatusMessage('Failed to save channel.', 'error');
  } else if (hasIssues) {
    setStatusMessage(`"${dto.displayName}" saved, but a required field is missing - check fields below.`, 'error');
  } else {
    setStatusMessage(`"${dto.displayName}" saved.`, 'success');
  }
}

async function removeChannel(node) {
  const dto = readChannelCard(node);
  if (!confirm(`Remove "${dto.displayName}"?`)) return;

  const res = await fetch(`/api/notifications/channels/${dto.id}`, { method: 'DELETE' });
  if (res.ok) {
    node.remove();
    setStatusMessage(`"${dto.displayName}" removed.`, 'success');
  } else {
    setStatusMessage('Failed to remove channel.', 'error');
  }
}

async function testChannel(node) {
  const dto = readChannelCard(node);
  setStatusMessage(`Testing "${dto.displayName}"...`, '');

  const res = await fetch('/api/notifications/test', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ type: dto.type, settings: dto.settings })
  });
  const body = await res.json();
  setStatusMessage(body.message, body.success ? 'success' : 'error');
}

// Fires a real digest immediately (today's actual numbers, bypassing the schedule entirely) - one
// send per digest type currently checked on this row, so Daily+Weekly both enabled sends two
// distinctly-labeled tests, not two identical "Daily Digest" ones. Falls back to a single Daily-
// style test if neither box is checked yet, so Test still does something useful before saving.
async function testChannelDigest(node) {
  const dto = readChannelCard(node);
  const weeklyFlags = [];
  if (dto.digestDailyEnabled) weeklyFlags.push(false);
  if (dto.digestWeeklyEnabled) weeklyFlags.push(true);
  if (weeklyFlags.length === 0) weeklyFlags.push(false);

  setStatusMessage(`Testing "${dto.displayName}" digest...`, '');

  const results = [];
  for (const weekly of weeklyFlags) {
    const res = await fetch('/api/notifications/digest-test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ type: dto.type, settings: dto.settings, weekly })
    });
    results.push(await res.json());
  }
  setStatusMessage(results.map(r => r.message).join(' | '), results.every(r => r.success) ? 'success' : 'error');
}

async function loadNotifierTypes() {
  const res = await fetch('/api/notifications/types');
  notifierTypes = await res.json();
  const sorted = [...notifierTypes].sort((a, b) => a.displayName.localeCompare(b.displayName));
  const options = sorted.map(t => `<option value="${escapeHtml(t.type)}">${escapeHtml(t.displayName)}</option>`).join('');
  addChannelType.innerHTML = `<option value="" selected disabled>Select Service</option>${options}`;
}

async function loadChannels() {
  const res = await fetch('/api/notifications/channels');
  const channels = await res.json();
  channelsList.innerHTML = '';
  for (const dto of channels) {
    channelsList.appendChild(channelCardFromDto(dto));
  }
}

document.getElementById('addChannelBtn').addEventListener('click', async () => {
  const type = addChannelType.value;
  if (!type) return;

  const res = await fetch('/api/notifications/channels', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ type })
  });
  const dto = await res.json();
  channelsList.appendChild(channelCardFromDto(dto));
  setStatusMessage(`"${dto.displayName}" added.`, 'success');
});

loadToastSetting();
loadMessageFormatSettings();
loadNotifierTypes().then(loadChannels);
