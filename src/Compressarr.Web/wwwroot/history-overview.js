// History > Overview: the period selector, the stat cards (chosen with the toolbar's Stats button), the period table, and the
// "History at a glance" group (ring, weekly bars, last runs) that always covers the whole history.
//
// Data: /api/history gives the period table's rollups (the server's own windows); /api/history/runs gives one compact array per run, from
// which every card and chart is worked out here, using the same windows measured from the server's date. A run's movie/TV counts are null
// when unknown (older runs), and those files are shown as "not split" rather than guessed.

const OV_DAY = 86400000;
const OV_MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const OV_PERIODS = { all: 'All time', year: 'Last year', d30: 'Last 30 days', d7: 'Last 7 days', today: 'Today' };
const OV_CARDS_KEY = 'compressarr.history.cards';
const OV_PERIOD_KEY = 'compressarr.history.period';
const OV_PURPLE = 'var(--notice-text)';
const OV_GREY = 'color-mix(in srgb, var(--text-dim) 35%, var(--bg-alt))';
const OV_ORCHID = '#b957d6';   // movies, on the Movies vs TV card only
const OV_TEAL = '#1aa6a6';     // TV, on the Movies vs TV card only
const OV_HB = '#4a7fd1';       // HandBrake, on the HandBrake vs ffmpeg card only
const OV_FF = '#d99a2b';       // ffmpeg, the same
const OV_SERIES = ['var(--accent)', OV_ORCHID, OV_TEAL, 'var(--warn)'];   // the biggest few lanes / profiles; everything else is grey

let ovRuns = [];          // oldest first
let ovToday = 0;          // the server's date, as UTC milliseconds
let ovNow = 0;            // the server's date and time, the same way
let ovRollups = null;
let ovChosen = null;
let ovPeriod = 'all';

function ovRead(key, fallback) {
  try { const v = JSON.parse(localStorage.getItem(key)); return v === null || v === undefined ? fallback : v; } catch { return fallback; }
}
function ovWrite(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* remembering is a convenience only */ }
}

// ---- formatting ------------------------------------------------------------------------------------------------------------------
const ovN = n => n.toLocaleString('en-US');
const ovGb = gb => gb >= 1000 ? (gb / 1000).toFixed(2) + ' TB' : gb.toFixed(2) + ' GB';
const ovPct = x => x.b > 0 ? Math.round((1 - x.a / x.b) * 1000) / 10 : 0;
const ovHours = s => Math.floor(s / 3600) + 'h ' + String(Math.floor(s % 3600 / 60)).padStart(2, '0') + 'm';
const ovMovies = n => n + (n === 1 ? ' movie' : ' movies');
const ovDayName = t => OV_MONTHS[new Date(t).getUTCMonth()] + ' ' + new Date(t).getUTCDate();
const ovWeekStart = t => t - ((new Date(t).getUTCDay() + 6) % 7) * OV_DAY;
const ovDot = c => `<i class="ov-dot" style="background:${c}"></i>`;
const ovEsc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');   // lane and profile names are the user's own text

// ---- the periods: the same windows /api/history uses, measured from the server's date ----------------------------------------------------
function ovYearAgo() {
  const d = new Date(ovToday), y = d.getUTCFullYear() - 1, m = d.getUTCMonth();
  return Date.UTC(y, m, Math.min(d.getUTCDate(), new Date(Date.UTC(y, m + 1, 0)).getUTCDate()));   // Feb 29 -> Feb 28
}
function ovIn(period) {
  switch (period) {
    case 'today': return r => r.t === ovToday;
    case 'd7': return r => r.t > ovToday - 7 * OV_DAY;
    case 'd30': return r => r.t > ovToday - 30 * OV_DAY;
    case 'year': { const cut = ovYearAgo(); return r => r.t > cut; }
    default: return () => true;
  }
}
function ovSum(rs) {
  return {
    f: rs.reduce((x, r) => x + r.f, 0), b: rs.reduce((x, r) => x + r.b, 0), a: rs.reduce((x, r) => x + r.a, 0),
    s: rs.reduce((x, r) => x + r.s, 0), v: rs.reduce((x, r) => x + r.v, 0), runs: rs.length,
    mv: rs.reduce((x, r) => x + (r.mv ?? 0), 0), tv: rs.reduce((x, r) => x + (r.tv ?? 0), 0),
    unk: rs.reduce((x, r) => x + (r.mv === null ? r.f : 0), 0)
  };
}

// One period's runs, and the buckets its little charts are drawn from: per run (Today), per day (30 / 7 days), per week (Last year / All time).
function ovContext(period) {
  const rs = ovRuns.filter(ovIn(period));
  let buckets = [];
  if (period === 'today') {
    buckets = rs.map(r => ({ name: 'run #' + r.n, rows: [r] }));
  } else if (period === 'd7' || period === 'd30') {
    const n = period === 'd7' ? 7 : 30;
    for (let i = n - 1; i >= 0; i--) { const t = ovToday - i * OV_DAY; buckets.push({ name: ovDayName(t), rows: rs.filter(r => r.t === t) }); }
  } else if (rs.length) {
    const first = ovWeekStart(period === 'year' ? Math.max(rs[0].t, ovToday - 364 * OV_DAY) : rs[0].t);
    for (let t = first; t <= ovToday; t += 7 * OV_DAY) buckets.push({ name: 'week of ' + ovDayName(t), rows: rs.filter(r => ovWeekStart(r.t) === t) });
  }
  for (const b of buckets) Object.assign(b, ovSum(b.rows));
  return { p: period, rs, tot: ovSum(rs), buckets };
}

// Today's run count is drawn as the last 24 hours in eight three-hour blocks (12-3 AM, 3-6 AM ...), the last one being the block that
// is running now. A run belongs to the block its END falls in, the same end that decides which day it counts for. The blocks reach back
// into yesterday, so they can add up to more than the Today total. A run with no known time (older rows) is left out of the blocks.
const OV_HOUR = 3600000;
const ovClock = h => (h % 12 || 12);
const ovAmPm = h => (h % 24 < 12 ? 'AM' : 'PM');
function ovBlockName(start) {
  const h0 = new Date(start).getUTCHours(), h1 = h0 + 3;
  const span = ovAmPm(h0) === ovAmPm(h1) ? `${ovClock(h0)}-${ovClock(h1)} ${ovAmPm(h0)}` : `${ovClock(h0)} ${ovAmPm(h0)}-${ovClock(h1)} ${ovAmPm(h1)}`;
  return start >= ovToday ? span : ovDayName(start) + ', ' + span;
}
function ovHourBlocks() {
  const nowStart = Math.floor(ovNow / (3 * OV_HOUR)) * 3 * OV_HOUR;
  const blocks = [];
  for (let i = 7; i >= 0; i--) {
    const start = nowStart - i * 3 * OV_HOUR;
    blocks.push({ name: ovBlockName(start) + (i === 0 ? ' (so far)' : ''), runs: ovRuns.filter(r => r.end !== null && r.end >= start && r.end < start + 3 * OV_HOUR).length });
  }
  return blocks;
}

// ---- little graphics -------------------------------------------------------------------------------------------------------------------
const ovIcon = p => `<svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${p}</svg>`;
const OV_ICONS = {
  runs: '<polygon points="5 3 19 12 5 21 5 3"></polygon>',
  files: '<rect x="2" y="3" width="20" height="18" rx="2"></rect><line x1="7" y1="3" x2="7" y2="21"></line><line x1="17" y1="3" x2="17" y2="21"></line><line x1="2" y1="9" x2="7" y2="9"></line><line x1="2" y1="15" x2="7" y2="15"></line><line x1="17" y1="9" x2="22" y2="9"></line><line x1="17" y1="15" x2="22" y2="15"></line>',
  saved: '<line x1="22" y1="12" x2="2" y2="12"></line><path d="M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"></path><line x1="6" y1="16" x2="6.01" y2="16"></line>',
  time: '<circle cx="12" cy="12" r="10"></circle><polyline points="12 6 12 12 16 14"></polyline>',
  clean: '<path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"></path><polyline points="22 4 12 14.01 9 11.01"></polyline>',
  avg: '<polyline points="23 18 13.5 8.5 8.5 13.5 1 6"></polyline><polyline points="17 18 23 18 23 12"></polyline>',
  biggest: '<polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"></polygon>',
  bestmo: '<rect x="3" y="4" width="18" height="18" rx="2"></rect><line x1="16" y1="2" x2="16" y2="6"></line><line x1="8" y1="2" x2="8" y2="6"></line><line x1="3" y1="10" x2="21" y2="10"></line>',
  streak: '<polyline points="22 12 18 12 15 21 9 3 6 12 2 12"></polyline>',
  split: '<rect x="2" y="7" width="20" height="15" rx="2" ry="2"></rect><polyline points="17 2 12 7 7 2"></polyline>',
  lane: '<polygon points="12 2 2 7 12 12 22 7 12 2"></polygon><polyline points="2 17 12 22 22 17"></polyline><polyline points="2 12 12 17 22 12"></polyline>',
  engine: '<rect x="4" y="4" width="16" height="16" rx="2" ry="2"></rect><rect x="9" y="9" width="6" height="6"></rect><line x1="9" y1="1" x2="9" y2="4"></line><line x1="15" y1="1" x2="15" y2="4"></line><line x1="9" y1="20" x2="9" y2="23"></line><line x1="15" y1="20" x2="15" y2="23"></line><line x1="20" y1="9" x2="23" y2="9"></line><line x1="20" y1="14" x2="23" y2="14"></line><line x1="1" y1="9" x2="4" y2="9"></line><line x1="1" y1="14" x2="4" y2="14"></line>',
  pass: '<rect x="9" y="9" width="13" height="13" rx="2" ry="2"></rect><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path>',
  profile: '<line x1="4" y1="21" x2="4" y2="14"></line><line x1="4" y1="10" x2="4" y2="3"></line><line x1="12" y1="21" x2="12" y2="12"></line><line x1="12" y1="8" x2="12" y2="3"></line><line x1="20" y1="21" x2="20" y2="16"></line><line x1="20" y1="12" x2="20" y2="3"></line><line x1="1" y1="14" x2="7" y2="14"></line><line x1="9" y1="8" x2="15" y2="8"></line><line x1="17" y1="16" x2="23" y2="16"></line>',
  speed: '<polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"></polygon>',
  redir: '<polyline points="15 3 21 3 21 9"></polyline><line x1="10" y1="14" x2="21" y2="3"></line><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"></path>'
};
function ovBars(vals, hi, color, names, show = v => +v.toFixed(2)) {
  const m = Math.max(...vals, 0.0001);
  return `<div class="ov-mb" style="--c:${color}">${vals.map((v, i) => `<i class="${i === hi ? 'hi' : ''}" style="height:${Math.max(6, Math.round(v / m * 100))}%" title="${names ? names[i] + ': ' : ''}${show(v)}"></i>`).join('')}</div>`;
}
const ovRunBars = blocks => ovBars(blocks.map(b => b.runs), blocks.length - 1, 'var(--accent)', blocks.map(b => b.name), n => n + (n === 1 ? ' run' : ' runs'));
const ovBucketBars = (c, key, color) => ovBars(c.buckets.map(b => b[key]), c.buckets.length - 1, color, c.buckets.map(b => b.name));
function ovSeg(parts) {
  return `<div class="ov-seg">${parts.map(p => `<i style="flex:${Math.max(p.v, 0.0001)};background:${p.c}" title="${p.t || ''}"></i>`).join('')}</div>`;
}
const ovDots = arr => `<div class="ov-dots">${arr.map(a => `<i class="${a ? 'on' : ''}"></i>`).join('')}</div>`;

// What the runs of a period handled, from the per-run [[name, files], ...] lists: the names biggest first, the files they cover, and the files of
// runs recorded before the lists existed (left out, never guessed).
function ovTally(rs, key) {
  const m = new Map(); let total = 0, unk = 0;
  for (const r of rs) {
    const list = r[key];
    if (!list) { unk += r.f; continue; }
    for (const [name, n] of list) { m.set(name, (m.get(name) || 0) + n); total += n; }
  }
  return { list: [...m.entries()].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1)), total, unk };
}
// A segmented bar of the biggest four names, the rest together in grey.
function ovSeries(t, noun) {
  const top = t.list.slice(0, 4), rest = t.list.slice(4).reduce((x, e) => x + e[1], 0);
  const parts = top.map((e, i) => ({ v: e[1], c: OV_SERIES[i], t: ovEsc(e[0]) + ": " + e[1] + " " + noun }));
  if (rest) parts.push({ v: rest, c: OV_GREY, t: "all others: " + rest + " " + noun });
  return ovSeg(parts);
}
const ovShare = x => (x < 10 || x > 90) && x > 0 && x < 100 ? x.toFixed(1) : String(Math.round(x));
const ovKnownNote = unk => unk ? '<span class="ov-note">' + ovN(unk) + ' older files not recorded</span>' : '';
// One rough speed for some runs: each run's average weighted by how long it took (total frames over total time); runs with no reading are left out.
function ovFps(rows) {
  let w = 0, s = 0;
  for (const r of rows) if (r.fps) { w += r.fps * Math.max(r.s, 1); s += Math.max(r.s, 1); }
  return s ? w / s : null;
}

// ---- the stats: the five defaults first, then the ones the Stats button adds ----------------------------------------------------------------
const OV_CARDS = [
  { id: 'runs', def: true, icon: 'runs', label: 'Total Runs', make: c => ({
      value: ovN(c.rs.length),
      sub: c.p === 'all' && ovRuns.length ? 'latest is run #' + ovRuns[ovRuns.length - 1].n : c.p === 'today' ? 'runs today &middot; chart: last 24 hours' : 'runs in this period',
      viz: c.p === 'today' ? ovRunBars(ovHourBlocks()) : ovBucketBars(c, 'runs', 'var(--accent)') }) },
  { id: 'files', def: true, icon: 'files', label: 'Files Compressed', make: c => ({
      value: ovN(c.tot.f),
      sub: c.tot.mv + c.tot.tv === 0 && c.tot.f ? 'movies and TV are counted separately from now on'
         : ovDot('var(--accent)') + ovMovies(c.tot.mv) + ' &middot; ' + ovDot(OV_PURPLE) + c.tot.tv + ' TV' + (c.tot.unk ? ' &middot; ' + ovDot(OV_GREY) + c.tot.unk + ' not split' : ''),
      viz: ovSeg([{ v: c.tot.mv, c: 'var(--accent)', t: c.tot.mv + ' movies' }, { v: c.tot.tv, c: OV_PURPLE, t: c.tot.tv + ' TV files' }, { v: c.tot.unk, c: OV_GREY, t: c.tot.unk + ' files from runs with no movie/TV split (older runs)' }]) }) },
  { id: 'bestmo', def: true, icon: 'bestmo', make: c => {
      // the "best" stat follows the period: a month for All time / Last year, a week for 30 days, a day for 7 days, a run for Today
      const unit = c.p === 'all' || c.p === 'year' ? 'Month' : c.p === 'd30' ? 'Week' : c.p === 'd7' ? 'Day' : 'Run';
      let groups;
      if (unit === 'Month') {
        const o = new Map();
        for (const r of c.rs) { const k = r.y * 100 + r.m; const g = o.get(k) || { name: OV_MONTHS[r.m - 1] + ' ' + r.y, v: 0 }; g.v += r.v; o.set(k, g); }
        groups = [...o.entries()].sort((x, y) => x[0] - y[0]).map(x => x[1]);
      } else if (unit === 'Week') {
        const o = new Map();
        for (const r of c.rs) { const k = ovWeekStart(r.t); const g = o.get(k) || { name: 'week of ' + ovDayName(k), v: 0 }; g.v += r.v; o.set(k, g); }
        groups = [...o.entries()].sort((x, y) => x[0] - y[0]).map(x => x[1]);
      } else groups = c.buckets.map(b => ({ name: b.name, v: b.v }));
      const best = groups.reduce((x, y) => y.v > x.v ? y : x, { v: -1, name: '-' });
      const thisMonth = OV_MONTHS[new Date(ovToday).getUTCMonth()] + ' ' + new Date(ovToday).getUTCFullYear();
      return { label: 'Best ' + unit, value: best.v < 0 ? '-' : ovGb(best.v), sub: best.name + (unit === 'Month' && best.name === thisMonth ? ' (so far)' : ''),
               viz: groups.length ? ovBars(groups.map(g => Math.max(g.v, 0)), groups.indexOf(best), 'var(--ok)', groups.map(g => g.name)) : '' }; } },
  { id: 'time', def: true, icon: 'time', label: 'Time Spent Encoding', make: c => ({
      value: ovHours(c.tot.s), sub: c.tot.f ? 'about ' + Math.floor(c.tot.s / c.tot.f / 60) + 'm ' + Math.round(c.tot.s / c.tot.f % 60) + 's per file' : 'no files', viz: ovBucketBars(c, 's', 'var(--warn)') }) },
  { id: 'clean', def: true, icon: 'clean', label: 'Clean Runs', make: c => {
      const clean = c.rs.filter(r => r.e === 0 && r.w === 0).length, warn = c.rs.filter(r => r.e === 0 && r.w > 0).length, err = c.rs.filter(r => r.e > 0).length, n = clean + warn + err;
      return { value: n ? (clean / n * 100).toFixed(1) + '%' : '-',
               sub: ovDot('var(--ok)') + clean + ' clean &middot; ' + ovDot('var(--warn)') + warn + ' warnings &middot; ' + ovDot('var(--err)') + err + ' errors',
               viz: ovSeg([{ v: clean, c: 'var(--ok)', t: clean + ' clean runs' }, { v: warn, c: 'var(--warn)', t: warn + ' runs with warnings' }, { v: err, c: 'var(--err)', t: err + ' runs with errors' }]) }; } },
  // ---- more stats ----
  { id: 'saved', def: false, icon: 'saved', label: 'Space Saved', make: c => ({
      value: ovGb(c.tot.v), sub: '<b>' + ovPct(c.tot) + '%</b> smaller', viz: ovBucketBars(c, 'v', 'var(--ok)') }) },
  { id: 'split', def: false, icon: 'split', label: 'Movies vs TV', make: c => {
      const known = c.tot.mv + c.tot.tv, share = x => (x < 10 || x > 90) ? x.toFixed(1) : String(Math.round(x));
      const pm = known ? c.tot.mv / known * 100 : 0, pt = known ? 100 - pm : 0;
      return { value: known ? share(pm) + '% / ' + share(pt) + '%' : '-',
               sub: known ? ovDot(OV_ORCHID) + 'movies &middot; ' + ovDot(OV_TEAL) + 'TV' + (c.tot.unk ? '<span class="ov-note">of ' + ovN(known) + ' known files</span>' : '') : 'no split recorded yet',
               viz: known ? ovSeg([{ v: c.tot.mv, c: OV_ORCHID, t: c.tot.mv + ' movies (' + share(pm) + '%)' }, { v: c.tot.tv, c: OV_TEAL, t: c.tot.tv + ' TV files (' + share(pt) + '%)' }]) : '' }; } },
  { id: 'avg', def: false, icon: 'avg', label: 'Average Saved / File', make: c => {
      const f = c.tot.f || 1;
      return { value: c.tot.f ? ovGb(c.tot.v / f) : '-', sub: ovDot('var(--accent)') + (c.tot.f ? ovGb(c.tot.a / f) : '-') + ' kept &middot; ' + ovDot('var(--ok)') + (c.tot.f ? ovGb(c.tot.v / f) : '-') + ' saved',
               viz: ovSeg([{ v: c.tot.a / f, c: 'var(--accent)', t: 'kept on average' }, { v: c.tot.v / f, c: 'var(--ok)', t: 'saved on average' }]) }; } },
  { id: 'biggest', def: false, icon: 'biggest', label: 'Biggest Run', make: c => {
      const top = c.rs.map(r => ({ n: r.n, v: r.v, f: r.f })).sort((x, y) => y.v - x.v).slice(0, 8);
      return { value: top.length ? ovGb(top[0].v) : '-', sub: top.length ? 'saved in run #' + top[0].n + ', ' + top[0].f + ' files' : 'no runs',
               viz: top.length ? ovBars(top.map(r => Math.max(r.v, 0)), 0, 'var(--warn)', top.map(r => 'run #' + r.n)) : '' }; } },
  { id: 'streak', def: false, icon: 'streak', label: 'Active Days', make: c => {
      if (!ovRuns.length) return { value: '-', sub: 'no runs', viz: '' };
      const days = new Set(c.rs.map(r => r.t));
      const from = c.p === 'all' || c.p === 'year' ? Math.max(ovRuns[0].t, c.p === 'year' ? ovToday - 364 * OV_DAY : 0) : ovToday - ({ d30: 29, d7: 6, today: 0 }[c.p]) * OV_DAY;
      const span = Math.max(1, Math.round((ovToday - from) / OV_DAY) + 1);
      let cur = 0, best = 0;
      for (let t = from; t <= ovToday; t += OV_DAY) { if (days.has(t)) { cur++; best = Math.max(best, cur); } else cur = 0; }
      return { value: Math.round(days.size / span * 100) + '%', sub: days.size + ' of ' + span + ' days &middot; longest streak ' + best,
               viz: ovDots(Array.from({ length: 7 }, (_, i) => days.has(ovToday - (6 - i) * OV_DAY) ? 1 : 0)) }; } },
  { id: 'redir', def: false, icon: 'redir', label: 'Redirected Files', make: c => ({
      value: String(c.rs.reduce((x, r) => x + r.r, 0)), sub: 'filed in another lane\'s library', viz: '' }) },
  { id: 'lane', def: false, icon: 'lane', label: 'Busiest Lane', make: c => {
      const t = ovTally(c.rs, 'lanes');
      if (!t.total) return { value: '-', sub: 'no lane recorded yet', viz: '' };
      const [name, n] = t.list[0];
      return { value: ovEsc(name), sub: n + ' of ' + ovN(t.total) + ' files (' + ovShare(n / t.total * 100) + '%)' + ovKnownNote(t.unk, t.total), viz: ovSeries(t, 'files') }; } },
  { id: 'engine', def: false, icon: 'engine', label: 'HandBrake vs ffmpeg', make: c => {
      const rs = c.rs.filter(r => r.hb !== null && r.ff !== null), hb = rs.reduce((x, r) => x + r.hb, 0), ff = rs.reduce((x, r) => x + r.ff, 0), n = hb + ff;
      const unk = c.rs.filter(r => r.hb === null || r.ff === null).reduce((x, r) => x + r.f, 0);
      if (!n) return { value: '-', sub: 'no encodes recorded yet', viz: '' };
      const ph = hb / n * 100, pf = 100 - ph;
      return { value: ovShare(ph) + '% / ' + ovShare(pf) + '%', sub: ovDot(OV_HB) + 'HandBrake &middot; ' + ovDot(OV_FF) + 'ffmpeg' + ovKnownNote(unk, n),
               viz: ovSeg([{ v: hb, c: OV_HB, t: hb + ' files by HandBrake (' + ovShare(ph) + '%)' }, { v: ff, c: OV_FF, t: ff + ' files by ffmpeg (' + ovShare(pf) + '%)' }]) }; } },
  { id: 'pass', def: false, icon: 'pass', label: 'Passed Through', make: c => {
      const rs = c.rs.filter(r => r.pass !== null), pass = rs.reduce((x, r) => x + r.pass, 0), files = rs.reduce((x, r) => x + r.f, 0);
      const unk = c.rs.filter(r => r.pass === null).reduce((x, r) => x + r.f, 0);
      if (!files) return { value: '-', sub: 'nothing recorded yet', viz: '' };
      return { value: ovN(pass), sub: (pass ? ovShare(pass / files * 100) + '% of ' + ovN(files) + ' files, copied not re-encoded' : 'none of ' + ovN(files) + ' files was copied unchanged') + ovKnownNote(unk, files),
               viz: ovSeg([{ v: pass, c: OV_TEAL, t: pass + ' files passed through' }, { v: files - pass, c: OV_GREY, t: (files - pass) + ' files re-encoded' }]) }; } },
  { id: 'profile', def: false, icon: 'profile', label: 'Most Used Profile', make: c => {
      const t = ovTally(c.rs, 'profiles');
      if (!t.total) return { value: '-', sub: 'no profile recorded yet', viz: '' };
      const [name, n] = t.list[0];
      return { value: ovEsc(name), sub: n + ' of ' + ovN(t.total) + ' encoded files (' + ovShare(n / t.total * 100) + '%)' + ovKnownNote(t.unk, t.total), viz: ovSeries(t, 'files') }; } },
  { id: 'fps', def: false, icon: 'speed', label: 'Average Speed', make: c => {
      const rate = ovFps(c.rs), n = c.rs.filter(r => r.fps).length;
      if (rate === null) return { value: '-', sub: 'no speed recorded yet', viz: '' };
      const per = c.buckets.map(b => ovFps(b.rows) || 0);
      return { value: rate.toFixed(1) + ' fps', sub: 'frames per second, across ' + n + (n === 1 ? ' run' : ' runs'), viz: ovBars(per, per.length - 1, 'var(--accent)', c.buckets.map(b => b.name), v => v ? v.toFixed(1) + ' fps' : 'no reading') }; } }
];

function ovTile(card, d) {
  const label = d.label || card.label;
  return `<div class="stat ov-card" title="${label}"><div class="ov-head"><span class="label">${label}</span><span class="ov-ico">${ovIcon(OV_ICONS[card.icon])}</span></div>` +
         `<div class="value">${d.value}</div><div class="sub">${d.sub}</div><div class="ov-viz">${d.viz}</div></div>`;
}

// Rows as even as possible: as many columns as fit (170px minimum each), then the cards are shared out so no row differs from another by
// more than one. 5 or 10 cards fill whole rows; 7 becomes 4 + 3, 9 becomes 5 + 4, 11 becomes 4 + 4 + 3 - never one lonely card on the last row.
function ovRenderStrip(tiles) {
  const strip = document.getElementById('ovStrip');
  const width = strip.clientWidth || 1000, maxCols = Math.max(1, Math.min(6, Math.floor((width + 12) / (170 + 12))));
  const n = tiles.length, rows = Math.max(1, Math.ceil(n / maxCols)), base = Math.floor(n / rows), extra = n % rows;
  let html = '', at = 0;
  for (let r = 0; r < rows; r++) {
    const size = base + (r < extra ? 1 : 0);
    html += `<div class="ov-row${rows === 1 && size < 4 ? ' few' : ''}" style="--n:${size}">${tiles.slice(at, at + size).join('')}</div>`;
    at += size;
  }
  strip.innerHTML = html;
}

// ---- the period table (the server's own rollups) ---------------------------------------------------------------------------------------------
function ovSplitText(b) {
  const mv = b.movieCount || 0, tv = b.tvCount || 0, unk = b.unknownCount || 0;
  if (!b.fileCount) return '';
  if (!mv && !tv) return 'not split';
  return ovMovies(mv) + ' &middot; ' + tv + ' TV' + (unk ? ' &middot; ' + unk + ' not split' : '');
}
function ovTableRow(label, period, b) {
  const saved = b.beforeGb > 0 ? Math.round((1 - b.afterGb / b.beforeGb) * 1000) / 10 : 0;
  const split = ovSplitText(b);
  return `<tr data-p="${period}" class="${period === ovPeriod ? 'sel' : ''}"><td>${label}</td><td>${b.fileCount}${split ? `<span class="ov-note">${split}</span>` : ''}</td>` +
         `<td>${b.beforeGb.toFixed(2)} GB</td><td>${b.afterGb.toFixed(2)} GB</td><td>${saved}%</td><td>${formatDuration(b.totalTimeSeconds)}</td></tr>`;
}

// ---- the whole-history group (never follows the period) ------------------------------------------------------------------------------------------
function ovRing(all) {
  const r = 40, c = 2 * Math.PI * r, p = all.b > 0 ? 1 - all.a / all.b : 0;
  return `<div class="stat ov-ring-tile"><div class="ov-title"><span>Space saved</span><span>all time</span></div><div class="ov-ring"><svg width="100" height="100" viewBox="0 0 100 100" role="img" aria-label="${ovPct(all)}% smaller">` +
    `<circle cx="50" cy="50" r="${r}" fill="none" stroke="color-mix(in srgb, var(--text-dim) 22%, var(--bg-alt))" stroke-width="12"></circle>` +
    `<circle cx="50" cy="50" r="${r}" fill="none" stroke="var(--ok)" stroke-width="12" stroke-linecap="round" stroke-dasharray="${(c * p).toFixed(1)} ${c.toFixed(1)}" transform="rotate(-90 50 50)"></circle>` +
    `<text x="50" y="49" text-anchor="middle" font-size="17" font-weight="700" fill="currentColor">${ovPct(all)}%</text><text x="50" y="64" text-anchor="middle" font-size="9" fill="var(--text-dim)">smaller</text></svg></div>` +
    `<div class="sub" style="text-align:center;margin-top:8px"><b>${ovGb(all.v)}</b> of ${ovGb(all.b)}</div></div>`;
}
function ovWeeklyBars() {
  const o = new Map();
  for (const r of ovRuns) { const k = ovWeekStart(r.t); const w = o.get(k) || { t: k, saved: 0 }; w.saved += r.v; o.set(k, w); }
  const weeks = [...o.values()].sort((x, y) => x.t - y.t);
  const shown = weeks.slice(-12), max = Math.max(...shown.map(w => w.saved), 0.0001), best = weeks.reduce((x, y) => y.saved > x.saved ? y : x);
  return `<div class="stat ov-wide"><div class="ov-title"><span>Space saved per week</span><span>${weeks.length} week${weeks.length === 1 ? '' : 's'} of history</span></div>` +
    `<div class="ov-bars">${shown.map((w, i) => `<div class="ov-bar${i === shown.length - 1 ? ' now' : ''}" style="height:${Math.max(3, Math.round(Math.max(w.saved, 0) / max * 100))}%" title="week of ${ovDayName(w.t)}: ${ovGb(w.saved)} saved${i === shown.length - 1 ? ' (so far)' : ''}"></div>`).join('')}</div>` +
    `<div class="ov-bar-labels">${shown.map(w => `<span>${ovDayName(w.t)}</span>`).join('')}</div>` +
    `<div class="sub" style="margin-top:8px">This week <b>${ovGb(weeks[weeks.length - 1].saved)}</b> so far &middot; best week <b style="color:var(--text)">${ovDayName(best.t)}, ${ovGb(best.saved)}</b></div></div>`;
}
function ovRibbon() {
  const rs = ovRuns.slice(-48).map(r => ({ n: r.n, s: r.e > 0 ? 'e' : r.w > 0 ? 'w' : 'ok', files: r.f, pct: r.b > 0 && r.a > 0 ? Math.round((1 - r.a / r.b) * 100) : null, e: r.e, w: r.w, report: r.report }));
  const ok = rs.filter(x => x.s === 'ok').length, w = rs.filter(x => x.s === 'w').length, e = rs.filter(x => x.s === 'e').length;
  const vals = rs.filter(x => x.pct !== null);
  const pts = vals.length > 1 ? vals.map((x, i) => (i / (vals.length - 1) * 300).toFixed(1) + ',' + (36 - Math.max(0, Math.min(1, (x.pct - 40) / 50)) * 32).toFixed(1)).join(' ') : '';
  const block = x => {
    const tip = `Run #${x.n}${x.files ? ' - ' + x.files + ' files, ' + x.pct + '% smaller' : ' - nothing produced'}${x.e ? ' - ' + x.e + ' error(s)' : ''}${x.w ? ' - ' + x.w + ' warning(s)' : ''}`;
    const cls = x.s === 'ok' ? '' : x.s;
    return x.report
      ? `<a class="${cls}" href="/api/reports/${encodeURIComponent(x.report)}" target="_blank" rel="noopener" title="${tip}"></a>`
      : `<span class="nolink ${cls}" title="${tip} (its report is no longer kept)"></span>`;
  };
  return `<div class="stat"><div class="ov-title"><span>Last ${rs.length} runs</span><span>click one to open its report</span></div><div class="ov-ribbon">${rs.map(block).join('')}</div>` +
    (pts ? `<svg class="ov-spark" viewBox="0 0 300 40" width="100%" height="36" preserveAspectRatio="none"><polyline points="${pts}" fill="none" stroke="var(--accent)" stroke-width="1.6" vector-effect="non-scaling-stroke"></polyline></svg>` : '') +
    `<div class="ov-ribbon-foot"><span>${ovDot('var(--ok)')}${ok} clean</span><span>${ovDot('var(--warn)')}${w} with warnings</span><span>${ovDot('var(--err)')}${e} with errors</span><span>line: % smaller per run</span></div></div>`;
}

// ---- drawing -----------------------------------------------------------------------------------------------------------------------------------
function ovPopRow(card, c) {
  const d = card.make(c);
  return `<label><input type="checkbox" data-c="${card.id}" ${ovChosen.includes(card.id) ? 'checked' : ''} /><span class="ov-ico">${ovIcon(OV_ICONS[card.icon])}</span>${d.label || card.label}<small>${d.value}</small></label>`;
}
function ovDraw() {
  const c = ovContext(ovPeriod);
  const hasRuns = ovRuns.length > 0;
  document.getElementById('ovGroupTitle').textContent = 'Stats for ' + OV_PERIODS[ovPeriod].toLowerCase();
  if (hasRuns) ovRenderStrip(OV_CARDS.filter(x => ovChosen.includes(x.id)).map(x => ovTile(x, x.make(c))));
  else document.getElementById('ovStrip').innerHTML = '<p class="muted">No runs recorded yet. The stats appear after the first run that converts a file.</p>';
  document.getElementById('ovGlobal').classList.toggle('hidden', !hasRuns);
  if (hasRuns) {
    document.getElementById('ovMid').innerHTML = ovRing(ovSum(ovRuns)) + ovWeeklyBars();
    document.getElementById('ovRibbon').innerHTML = ovRibbon();
  }
  if (ovRollups) {
    document.getElementById('historyRows').innerHTML = [
      ovTableRow('Today', 'today', ovRollups.today), ovTableRow('Last 7 Days', 'd7', ovRollups.last7Days), ovTableRow('Last 30 Days', 'd30', ovRollups.last30Days),
      ovTableRow('Last Year', 'year', ovRollups.lastYear), ovTableRow('All Time', 'all', ovRollups.allTime)].join('');
  }
  document.getElementById('ovPopDefault').innerHTML = OV_CARDS.filter(x => x.def).map(x => ovPopRow(x, c)).join('');
  document.getElementById('ovPopMore').innerHTML = OV_CARDS.filter(x => !x.def).map(x => ovPopRow(x, c)).join('');
  document.getElementById('ovPopCount').textContent = ovChosen.length + ' of ' + OV_CARDS.length + ' stats shown';
}

function ovSetPeriod(p) {
  if (!OV_PERIODS[p]) return;
  ovPeriod = p;
  ovWrite(OV_PERIOD_KEY, p);
  const radio = document.querySelector(`#ovPeriods [value="${p}"]`);
  if (radio) radio.checked = true;
  ovDraw();
}

function ovWire() {
  const pop = document.getElementById('ovPop'), btn = document.getElementById('statsBtn');
  const setOpen = open => { pop.classList.toggle('hidden', !open); if (btn) btn.classList.toggle('on-pop', open); };
  if (btn) btn.addEventListener('click', e => { e.stopPropagation(); setOpen(pop.classList.contains('hidden')); });
  document.addEventListener('click', e => { if (!pop.contains(e.target) && !(btn && btn.contains(e.target))) setOpen(false); });
  document.addEventListener('keydown', e => { if (e.key === 'Escape') setOpen(false); });
  pop.addEventListener('change', e => {
    const id = e.target.dataset.c;
    if (!id) return;
    ovChosen = e.target.checked ? [...ovChosen, id] : ovChosen.filter(x => x !== id);
    ovWrite(OV_CARDS_KEY, ovChosen);
    ovDraw();
  });
  document.getElementById('ovPopReset').addEventListener('click', () => { ovChosen = OV_CARDS.filter(c => c.def).map(c => c.id); ovWrite(OV_CARDS_KEY, ovChosen); ovDraw(); });
  document.getElementById('ovPeriods').addEventListener('change', e => { if (e.target.name === 'ovPeriod') ovSetPeriod(e.target.value); });
  document.getElementById('historyRows').addEventListener('click', e => { const tr = e.target.closest('tr'); if (tr && tr.dataset.p) ovSetPeriod(tr.dataset.p); });
  let timer;
  window.addEventListener('resize', () => { clearTimeout(timer); timer = setTimeout(ovDraw, 120); });
}

async function loadOverview() {
  const known = new Set(OV_CARDS.map(c => c.id));
  const saved = ovRead(OV_CARDS_KEY, null);
  ovChosen = Array.isArray(saved) ? saved.filter(id => known.has(id)) : OV_CARDS.filter(c => c.def).map(c => c.id);
  const savedPeriod = ovRead(OV_PERIOD_KEY, 'all');
  ovPeriod = OV_PERIODS[savedPeriod] ? savedPeriod : 'all';
  document.querySelector(`#ovPeriods [value="${ovPeriod}"]`).checked = true;
  ovWire();

  try {
    const [history, runs] = await Promise.all([fetch('/api/history').then(r => r.json()), fetch('/api/history/runs').then(r => r.json())]);
    ovRollups = history;
    const [ty, tm, td] = runs.today.split('-').map(Number);
    ovToday = Date.UTC(ty, tm - 1, td);
    const [nowDate, nowTime = '23:59'] = (runs.now || runs.today).split('T'), [ny, nm, nd] = nowDate.split('-').map(Number), [nh, nmin] = nowTime.split(':').map(Number);
    ovNow = Date.UTC(ny, nm - 1, nd, nh, nmin);   // the server's wall clock, as milliseconds, like every other date here
    // fps, hb, ff, pass, lanes and profiles are null for a run recorded before they existed; those files are left out of the cards that use them.
    ovRuns = runs.runs.map(([y, m, d, b, a, f, s, n, e, w, r, mv, tv, report, endMinute, fps, hb, ff, pass, lanes, profiles]) => ({ y, m, d, b, a, f, s, n, e, w, r, mv, tv, report, t: Date.UTC(y, m - 1, d), v: b - a,
      end: endMinute === null || endMinute === undefined ? null : Date.UTC(y, m - 1, d) + endMinute * 60000,
      fps: fps ?? null, hb: hb ?? null, ff: ff ?? null, pass: pass ?? null, lanes: lanes ?? null, profiles: profiles ?? null }));
  } catch {
    ovRuns = [];   // the page still shows what it can; the next load tries again
  }
  ovDraw();
}
