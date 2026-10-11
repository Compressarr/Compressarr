renderNav('scheduler');

// success: true = green, 'error' = red, anything else = plain (progress such as "Saving...").
function setStatus(text, success) { setStatusMessage(text, success === 'error' ? 'error' : success ? 'success' : ''); }

const DAY_NAMES = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
// The order the "Each day" rows are shown in (the saved list itself is Sunday-first, index = DayOfWeek).
const DAY_DISPLAY_ORDER = [1, 2, 3, 4, 5, 6, 0];

const modeSelect = document.getElementById('schedMode');
const windowsEl = document.getElementById('schedWindows');

// ---- window rows ---------------------------------------------------------------------------------
// One row = a label, a start time, an end time, and a "Run Off-hours Priority All Day" box. A day with no window is
// stored as start == end, so that box simply writes the same time twice.

const rows = {}; // key -> { start, end, none, el }

function makeRow(key, label) {
  const el = document.createElement('div');
  el.className = 'sched-window';
  el.dataset.key = key;
  el.innerHTML = `
    <span class="sched-window-label">${label}</span>
    <input type="time" id="w-${key}-start" value="08:00" aria-label="${label} starts" />
    <span class="sched-window-to">to</span>
    <input type="time" id="w-${key}-end" value="22:00" aria-label="${label} ends" />
    <label class="sched-window-none"><input type="checkbox" id="w-${key}-none" />Run Off-hours Priority All Day</label>
  `;
  const row = {
    el,
    start: el.querySelector(`#w-${key}-start`),
    end: el.querySelector(`#w-${key}-end`),
    none: el.querySelector(`#w-${key}-none`)
  };
  row.none.addEventListener('change', () => syncRowDisabled(row));
  rows[key] = row;
  return row;
}

function syncRowDisabled(row) {
  row.start.disabled = row.none.checked;
  row.end.disabled = row.none.checked;
  row.el.classList.toggle('none', row.none.checked);
}

function setRow(key, start, end) {
  const row = rows[key];
  row.start.value = start;
  row.end.value = end;
  row.none.checked = start === end;
  syncRowDisabled(row);
}

// {start, end} the way it is saved: "no daytime window" is the start time written twice.
function readRow(key) {
  const row = rows[key];
  const start = row.start.value || '08:00';
  const end = row.none.checked ? start : (row.end.value || '22:00');
  return { start, end };
}

function copyRow(from, to) {
  const { start, end } = readRow(from);
  setRow(to, start, end);
}

makeRow('everyday', 'Every day');
makeRow('weekdays', 'Weekdays (Mon-Fri)');
makeRow('weekends', 'Weekends (Sat-Sun)');
for (const i of DAY_DISPLAY_ORDER) makeRow(`day${i}`, DAY_NAMES[i]);

// Which rows each layout shows.
const MODE_ROWS = {
  Everyday: ['everyday'],
  WeekdaysAndWeekends: ['weekdays', 'weekends'],
  EachDay: DAY_DISPLAY_ORDER.map(i => `day${i}`)
};

let currentMode = 'Everyday';
let eachDayTouched = false;

function renderWindows() {
  windowsEl.replaceChildren(...MODE_ROWS[currentMode].map(k => rows[k].el));
}

// Switching layout carries the times across, so changing from "same every day" to "each day" doesn't
// throw away what was just typed: every day starts from the window it was already using.
function switchMode(next) {
  const previous = currentMode;
  if (previous === next) return;

  if (previous === 'Everyday' && next === 'WeekdaysAndWeekends') {
    copyRow('everyday', 'weekdays');
  } else if (previous === 'WeekdaysAndWeekends' && next === 'Everyday') {
    copyRow('weekdays', 'everyday');
  }

  if (next === 'EachDay' && !eachDayTouched) {
    for (const i of DAY_DISPLAY_ORDER) {
      const source = previous === 'WeekdaysAndWeekends' ? (i === 0 || i === 6 ? 'weekends' : 'weekdays') : (previous === 'Everyday' ? 'everyday' : null);
      if (source) copyRow(source, `day${i}`);
    }
  }
  if (previous === 'EachDay' && next === 'Everyday') copyRow('day1', 'everyday');
  if (previous === 'EachDay' && next === 'WeekdaysAndWeekends') {
    copyRow('day1', 'weekdays');
    copyRow('day6', 'weekends');
  }

  currentMode = next;
  renderWindows();
}

modeSelect.addEventListener('change', () => switchMode(modeSelect.value));
windowsEl.addEventListener('change', e => {
  if (e.target.closest('[data-key^="day"]')) eachDayTouched = true;
});

// ---- the rest of the form -------------------------------------------------------------------------

function updateVisibility() {
  const enabled = document.getElementById('schedEnabled').checked;
  const body = document.getElementById('schedBody');
  body.style.opacity = enabled ? '' : '0.5';
  for (const el of body.querySelectorAll('input, select')) {
    el.disabled = !enabled || (el.type === 'time' && rows[el.id.split('-')[1]]?.none.checked);
  }
  // These two sit above the body (beside the master switch) so they are dimmed and disabled by hand.
  const onlyOffHours = document.getElementById('schedOnlyOffHours');
  onlyOffHours.disabled = !enabled;
  onlyOffHours.closest('.check-row').style.opacity = enabled ? '' : '0.5';
  const whenRow = document.getElementById('schedWhenDayStartsRow');
  document.getElementById('schedWhenDayStarts').disabled = !enabled;
  whenRow.style.display = onlyOffHours.checked ? '' : 'none';
  whenRow.style.opacity = enabled ? '' : '0.5';
  updatePriorityHints();
}

// One line under each priority dropdown saying what the chosen level does to CPU use - the longer
// explanation is in "What each setting does" at the bottom of the page.
const PRIORITY_HINTS = {
  Low: 'Only uses CPU nothing else wants. Other programs are unaffected; the encode can crawl while the PC is busy.',
  BelowNormal: 'Yields to ordinary programs. Everyday use stays smooth; the encode takes what is left.',
  Normal: 'Shares the CPU equally with other programs. Near 100% CPU use; a busy PC can feel slower.',
  AboveNormal: 'Wins most CPU contests with ordinary programs. Faster encodes; other programs may stutter.',
  High: 'Strongly favored over ordinary programs. Expect a sluggish PC while it runs.',
  Realtime: 'Outranks nearly everything and can freeze the PC. Needs administrator rights, otherwise treated as High.'
};

function updatePriorityHints() {
  document.getElementById('schedDayPriorityHint').textContent = PRIORITY_HINTS[document.getElementById('schedDayPriority').value] || '';
  document.getElementById('schedNightPriorityHint').textContent = PRIORITY_HINTS[document.getElementById('schedNightPriority').value] || '';
}
for (const id of ['schedDayPriority', 'schedNightPriority']) {
  document.getElementById(id).addEventListener('change', updatePriorityHints);
}

for (const id of ['schedEnabled', 'schedOnlyOffHours']) {
  document.getElementById(id).addEventListener('change', updateVisibility);
}
windowsEl.addEventListener('change', updateVisibility);
modeSelect.addEventListener('change', updateVisibility);

function fillForm(dto) {
  document.getElementById('schedEnabled').checked = dto.enabled;
  modeSelect.value = dto.mode;
  currentMode = dto.mode;
  setRow('everyday', dto.dayStart, dto.dayEnd);
  setRow('weekdays', dto.dayStart, dto.dayEnd);
  setRow('weekends', dto.weekendDayStart, dto.weekendDayEnd);
  dto.days.forEach((d, i) => setRow(`day${i}`, d.start, d.end));
  eachDayTouched = dto.mode === 'EachDay';
  document.getElementById('schedDayPriority').value = dto.dayPriority;
  document.getElementById('schedNightPriority').value = dto.nightPriority;
  document.getElementById('schedOnlyOffHours').checked = dto.onlyEncodeOffHours;
  document.getElementById('schedWhenDayStarts').value = dto.whenDayStarts;
  renderWindows();
  updateVisibility();
}

function readForm() {
  // DayStart/DayEnd is "the" window in Everyday mode and the weekday window in the weekday/weekend layout.
  const main = readRow(currentMode === 'Everyday' ? 'everyday' : 'weekdays');
  const weekend = readRow('weekends');
  const days = [];
  for (let i = 0; i < 7; i++) {
    const w = readRow(`day${i}`);
    days.push({ day: DAY_NAMES[i], start: w.start, end: w.end });
  }
  return {
    enabled: document.getElementById('schedEnabled').checked,
    mode: currentMode,
    dayStart: main.start,
    dayEnd: main.end,
    weekendDayStart: weekend.start,
    weekendDayEnd: weekend.end,
    days,
    dayPriority: document.getElementById('schedDayPriority').value,
    nightPriority: document.getElementById('schedNightPriority').value,
    onlyEncodeOffHours: document.getElementById('schedOnlyOffHours').checked,
    whenDayStarts: document.getElementById('schedWhenDayStarts').value,
    validationIssues: []
  };
}

// Server-side issue field -> the input it belongs to, for the layout currently shown.
function fieldMap() {
  const map = {};
  const first = currentMode === 'Everyday' ? 'everyday' : 'weekdays';
  map.dayStart = `#w-${first}-start`;
  map.dayEnd = `#w-${first}-end`;
  map.weekendDayStart = '#w-weekends-start';
  map.weekendDayEnd = '#w-weekends-end';
  for (let i = 0; i < 7; i++) {
    map[`day${i}Start`] = `#w-day${i}-start`;
    map[`day${i}End`] = `#w-day${i}-end`;
  }
  return map;
}

function applyValidation(issues) {
  return applyValidationIssues(document, issues, fieldMap(), 'A time on the schedule is not valid - check the highlighted fields.');
}

let dirty = false;

async function load() {
  const res = await fetch('/api/schedule');
  const dto = await res.json();
  fillForm(dto);
  dirty = false;
  applyValidation(dto.validationIssues);
}

document.getElementById('saveBtn').addEventListener('click', async () => {
  setStatus('Saving...');
  const res = await fetch('/api/schedule', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(readForm())
  });
  if (!res.ok) {
    setStatus('Failed to save the schedule.', 'error');
    return;
  }
  const dto = await res.json();
  dirty = false;
  if (!applyValidation(dto.validationIssues)) setStatus('Schedule saved.', true);
  pollStatus();
});

document.getElementById('clearChangesBtn').addEventListener('click', async () => {
  if (dirty && !confirm('Discard unsaved changes and reload the last saved schedule?')) return;
  await load();
  setStatus('Changes cleared.', true);
});

const main = document.querySelector('main');
for (const eventName of ['input', 'change']) {
  main.addEventListener(eventName, () => { dirty = true; });
}
window.addEventListener('beforeunload', e => {
  if (!dirty) return;
  e.preventDefault();
  e.returnValue = '';
});

// ---- "Right now" ----------------------------------------------------------------------------------
// Read from the same /api/run/status the top bar polls, so this card, the tag and the Monitor page can
// never disagree about whether the queue is held.

const runAnywayBtn = document.getElementById('runAnywayBtn');

function renderNow(s) {
  const schedule = s.schedule;
  const modeEl = document.getElementById('schedNowMode');
  const changeEl = document.getElementById('schedNowChange');
  const queueEl = document.getElementById('schedNowQueue');
  const waiting = (s.upNext || []).length;

  const info = scheduleTagInfo(schedule);
  if (!info) {
    modeEl.textContent = 'The schedule is off';
    modeEl.className = 'sched-now-mode off';
    changeEl.textContent = 'Encodes run at normal priority at all hours.';
    queueEl.textContent = '';
    runAnywayBtn.classList.add('hidden');
    return;
  }

  modeEl.textContent = info.text;
  modeEl.className = `sched-now-mode ${info.kind}`;

  const until = schedule.nextChange ? formatScheduleMoment(schedule.nextChange) : null;
  if (schedule.isDaytime) {
    changeEl.textContent = until ? `Daytime window until ${until}. Encodes run at ${schedulePriorityName(schedule.priority)} priority.` : `Daytime. Encodes run at ${schedulePriorityName(schedule.priority)} priority.`;
  } else {
    changeEl.textContent = until ? `Off-hours until ${until}. Encodes run at ${schedulePriorityName(schedule.priority)} priority.` : `Off-hours, and no daytime window is configured. Encodes run at ${schedulePriorityName(schedule.priority)} priority.`;
  }

  if (schedule.isHeld) {
    queueEl.textContent = waiting > 0 ? `The queue is held: ${waiting} file${waiting === 1 ? '' : 's'} waiting for off-hours.` : 'The queue is held for the daytime window (nothing is waiting).';
  } else if (schedule.overrideActive) {
    queueEl.textContent = 'Run anyway is in effect: the daytime hold is released until the window ends.';
  } else if (schedule.onlyOffHours) {
    queueEl.textContent = 'Off-hours: the queue is running normally.';
  } else {
    queueEl.textContent = 'Only encode during off-hours is off, so the queue is never held.';
  }

  runAnywayBtn.classList.toggle('hidden', !(schedule.isHeld && (waiting > 0 || s.isPaused)));
}

async function pollStatus() {
  try {
    const res = await fetch('/api/run/status');
    renderNow(await res.json());
  } catch {
    // best-effort - leave whatever is showing
  }
}

runAnywayBtn.addEventListener('click', async () => {
  runAnywayBtn.disabled = true;
  await fetch('/api/run/run-anyway', { method: 'POST' });
  await fetch('/api/run/trigger-now', { method: 'POST' });
  runAnywayBtn.disabled = false;
  pollStatus();
});

load();
pollStatus();
setInterval(pollStatus, 2000);
