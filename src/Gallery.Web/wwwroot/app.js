// Gallery SPA – vanilla ES module, no build step.
import { openEditor, closeEditor, editorOpen, initEditor } from './editor.js';
import { Panorama } from './panorama.js';
import { openCollage } from './collage.js';
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtBytes = n => n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(0) + ' KB' : n < 1073741824 ? (n / 1048576).toFixed(1) + ' MB' : (n / 1073741824).toFixed(2) + ' GB';
const fmtDur = s => { if (s == null) return ''; s = Math.round(s); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60; return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(x).padStart(2, '0'); };
const fmtDate = iso => iso ? iso.replace('T', ' ') : '—';
const fmtNum = n => (n ?? 0).toLocaleString();
const fmtMonth = iso => iso ? new Date(iso.slice(0, 7) + '-01T00:00:00').toLocaleDateString(undefined, { month: 'short', year: 'numeric' }) : '';
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
/** The fixed top bar and layout tabs: grid rows under them are out of sight. */
const headerHeight = () => $('#topbar').offsetHeight + $('#layoutTabs').offsetHeight;
// Thumbnail URL. v changes with the file's content, with the item's manual rotation and with the thumbnail generation
// (bumped when old thumbnails were wrong), so the browser can cache forever. It must match Rotation.Version on the server.
const thumbUrl = (assetId, hash, rotation, size, squeeze = 1) =>
  `/api/assets/${assetId}/thumb?size=${size}&v=${hash}${rotation ? `.r${rotation}` : ''}${squeeze > 1 ? `.a${fmtSqueeze(squeeze)}` : ''}.t3`;
/** An anamorphic lens's factor as the server writes it: 1.5, 1.33, 2. */
const fmtSqueeze = s => String(Math.round(s * 100) / 100);
const SQUEEZES = [1.25, 1.33, 1.5, 1.55, 1.6, 1.65, 1.8, 2];
/** The most photos in one collage (Collage.MaxPhotos on the server). */
const COLLAGE_MAX = 9;
const previewSize = () => Math.min(2048, Math.max(1024, Math.ceil(Math.max(innerWidth, innerHeight) * devicePixelRatio)));

/**
 * Grid thumbnails and covers download through this queue: at most THUMB_LOADS at a time, those nearest the screen
 * first. A download that has started runs to the end; an image that leaves the page before its turn just leaves the
 * queue. Each cancelled download is a reset HTTP/2 stream, and a quick scroll used to cancel hundreds a second: with the
 * server busy (finding faces) or a slow link, that overflowed Kestrel's output queue and it dropped the whole connection
 * ("exceeded the output operations maximum queue size").
 */
const THUMB_LOADS = 16;
const thumbs = {
  wanted: new Map(),  // img -> URL, waiting for a slot
  loading: new Set(), // downloading; held here so one taken off the page still finishes and frees its slot
  queued: false,
  /** Shows url in img when a slot is free. */
  load(img, url) { this.wanted.set(img, url); this.schedule(); },
  /** Takes img out of the queue. A download already running finishes. */
  forget(img) { this.wanted.delete(img); },
  /** Whether img has, or is getting, a picture. */
  has(img) { return this.wanted.has(img) || this.loading.has(img) || img.hasAttribute('src'); },
  // Later in the task, so images just made are in the page and can be ranked by where they are.
  schedule() { if (!this.queued) { this.queued = true; queueMicrotask(() => { this.queued = false; this.pump(); }); } },
  pump() {
    if (this.loading.size >= THUMB_LOADS || !this.wanted.size) return;
    const ranked = [];
    for (const [img, url] of this.wanted) {
      if (!img.isConnected) { this.wanted.delete(img); continue; }
      if (this.loading.has(img)) continue; // a new URL for it waits until this one is done
      const r = img.getBoundingClientRect();
      ranked.push([Math.max(0, r.top - innerHeight, -r.bottom), img, url]); // px outside the window; 0 on screen
    }
    ranked.sort((a, b) => a[0] - b[0]);
    for (const [, img, url] of ranked.slice(0, THUMB_LOADS - this.loading.size)) { this.wanted.delete(img); this.start(img, url); }
  },
  start(img, url) {
    this.loading.add(img);
    let timer = 0;
    const done = () => {
      clearTimeout(timer); img.removeEventListener('load', done); img.removeEventListener('error', done);
      this.loading.delete(img); this.schedule();
    };
    timer = setTimeout(done, 30000); // give the slot up if the download stalls
    img.addEventListener('load', done); img.addEventListener('error', done);
    img.src = url;
  },
};

/** The message in an error response: plain text, a JSON string (Results.BadRequest("…")) or a problem's detail. */
function errorText(body) {
  try { const j = JSON.parse(body); return typeof j === 'string' ? j : j?.detail || j?.title || body; } catch { return body; }
}
async function api(path, opts = {}) {
  const r = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...opts, body: opts.body != null ? JSON.stringify(opts.body) : undefined });
  if (r.status === 401) { state.needLogin = true; renderUser(); if (!opts.quiet) showLogin(); throw new Error('login required'); }
  if (!r.ok) throw new Error(errorText(await r.text()) || r.statusText);
  const ct = r.headers.get('content-type') || '';
  return ct.includes('json') ? r.json() : r.text();
}

const state = {
  years: [], albumQ: '', overviewScroll: {},
  albumSort: (() => { try { return localStorage.getItem('albumSort') || 'name'; } catch { return 'name'; } })(),
  me: null, needLogin: false,
  filter: { q: '', kind: '', year: '', tagKey: '', tagVal: '', album: '', person: '', dups: false, variations: false, sort: 'date_desc' },
  people: [], peopleHidden: false, personDetail: null,
  total: 0,
  selecting: false, selected: new Set(), lastClicked: null,
  // sphere: a 360° photo is on screen as one; flatItem: the item a 360° photo is shown flat for (the 360°/flat switch).
  lb: { open: false, index: -1, detail: null, assetId: null, hasEntry: false, viewRot: 0, pendingRot: 0, panel: false, sphere: false, flatItem: null },
  albums: [], view: 'grid',
};

// ---------- auth ----------
async function loadMe() {
  state.me = await api('/api/auth/me', { quiet: true });
  document.body.classList.toggle('admin', state.me.role === 'admin');
  document.title = state.me.title; $('#title').textContent = state.me.title;
  renderUser();
}
function renderUser() {
  const box = $('#userBox');
  if (state.me?.authenticated) box.innerHTML = `<span class="muted">${esc(state.me.username)}</span><button class="ghost small" id="logoutBtn">Log out</button>`;
  else box.innerHTML = `<button class="ghost small" id="loginBtn">Log in</button>`;
  $('#logoutBtn')?.addEventListener('click', async () => { await api('/api/auth/logout', { method: 'POST' }); location.hash = '#/'; await loadMe(); reload(); });
  $('#loginBtn')?.addEventListener('click', showLogin);
}
function showLogin() {
  modal(`<h2>Log in</h2><form id="loginForm" class="form">
    <label>Username <input name="username" value="admin" autocomplete="username" required></label>
    <label>Password <input name="password" type="password" autocomplete="current-password"></label>
    <div class="row"><button>Log in</button><button type="button" class="ghost" data-close>Cancel</button><span class="error" id="loginErr"></span></div>
    <small class="muted">Viewers: username <code>viewer</code>, password if the admin set one.</small></form>`);
  $('#loginForm').addEventListener('submit', async e => {
    e.preventDefault();
    const fd = new FormData(e.target);
    try {
      await api('/api/auth/login', { method: 'POST', body: { username: fd.get('username'), password: fd.get('password') }, quiet: true });
      closeModal(); state.needLogin = false; await loadMe(); await bootstrapData(); reload(); route();
    } catch { $('#loginErr').textContent = 'Wrong username or password'; }
  });
}

// ---------- modal / toast ----------
/** Shows a dialog; cls styles its box (e.g. 'collage' for a wide one). */
function modal(html, cls = '') { $('#modalBox').className = cls; $('#modalBox').innerHTML = html; $('#modal').hidden = false; $$('[data-close]', $('#modalBox')).forEach(b => b.onclick = closeModal); $('#modalBox input:not([type=hidden])')?.focus(); }
function closeModal() { $('#modal').hidden = true; }
$('#modal').addEventListener('click', e => { if (e.target.id === 'modal') closeModal(); });
/** A short message; with an action ({label, run}) it shows a button and stays longer. */
let toastT; function toast(msg, action) {
  const t = $('#toast'); t.textContent = msg; t.hidden = false; clearTimeout(toastT);
  if (action) { const b = document.createElement('button'); b.className = 'small'; b.textContent = action.label; b.onclick = () => { t.hidden = true; action.run(); }; t.appendChild(b); }
  toastT = setTimeout(() => t.hidden = true, action ? 10000 : 2500);
}

// ---------- routing: #/  #/albums  #/album/ID  #/years  #/year/YYYY  #/people  #/person/ID[/faces]  #/faces  #/item/ID  #/admin ----------
// History model: gallery views (#/, #/album/ID) are history entries. Opening the full-size viewer pushes one
// #/item/ID entry on top of the gallery; stepping through photos replaces it, so Back (or ×/Esc, which call
// history.back()) always returns to the gallery the photo was opened from, with its filters and scroll position intact.
let galleryHash = null; // the hash the current grid was built for; null when no grid is showing
if ('scrollRestoration' in history) history.scrollRestoration = 'manual'; // we keep the grid's scroll position ourselves
function route() {
  const h = location.hash || '#/';
  const m = h.match(/^#\/(albums|album|years|year|people|person|faces|item|admin|edit)?\/?(\d+)?(\/faces)?/);
  const kind = m?.[1];
  // Coming back to an overview from one of its albums, years or people finds it where it was left.
  if (state.view === 'albums' || state.view === 'years' || state.view === 'people') state.overviewScroll[state.view] = scrollY;
  if (kind !== 'edit' && editorOpen()) closeEditor(); // Back from the editor: the viewer (or gallery) underneath is still there
  if (kind === 'edit') {
    // The editor opens over the viewer; on a deep link, put the gallery underneath as the entry Back returns to.
    if (state.me?.role !== 'admin') { showLogin(); return; }
    if (state.view !== 'grid' || galleryHash === null) { history.replaceState(null, '', '#/'); buildGallery('#/'); history.pushState(null, '', h); }
    openEditor(+m[2], editorHost(+m[2]));
    return;
  }
  if (kind === 'item') {
    const id = +m[2];
    if (state.view === 'grid' && galleryHash !== null) {
      // Forward into a viewer entry (or a re-route after login): show it over the existing grid.
      if (!state.lb.open || state.lb.detail?.item.id !== id) openItemById(id, { fromHistory: true });
      return;
    }
    // Deep link straight to a photo: put the gallery underneath as the entry Back returns to.
    history.replaceState(null, '', '#/');
    buildGallery('#/');
    openItemById(id);
    return;
  }
  if (state.lb.open) closeLightbox({ fromHistory: true });
  if (kind === 'admin') { if (state.me?.role !== 'admin') { showLogin(); return; } galleryHash = null; showAdmin(); return; }
  if (kind === 'albums') { showAlbums(); return; }
  if (kind === 'years') { showYears(); return; }
  if (kind === 'people') { showPeople(); return; }
  if (kind === 'faces') { if (state.me?.role !== 'admin') { showLogin(); return; } showUnassigned(); return; }
  if (kind === 'person' && m[3]) { if (state.me?.role !== 'admin') { showLogin(); return; } showPersonFaces(+m[2]); return; }
  if (state.view === 'grid' && galleryHash === h) return; // Back from the viewer: same gallery, keep it as it is
  buildGallery(h);
}
/** What the photo editor needs from the app: the API, messages, a title, and how to leave (pop its history entry). */
function editorHost(assetId) {
  const d = state.lb.detail;
  const asset = d?.assets.find(a => a.id === assetId);
  return {
    api, toast,
    title: asset?.fileName || d?.item.title || 'Edit photo',
    close: () => { if (location.hash.startsWith('#/edit/')) history.back(); else closeEditor(); },
    // An export joined the photo's files: show it in the viewer underneath, and refresh the grid's thumbnail.
    exported: () => { if (state.lb.detail) showItem(state.lb.detail.item.id, state.lb.assetId); reload({ keepScroll: true }); },
  };
}
function buildGallery(h) {
  const m = h.match(/^#\/(album|year|person)\/(\d+)/);
  state.view = 'grid'; galleryHash = h;
  state.filter.album = m?.[1] === 'album' ? m[2] : '';
  state.filter.year = m?.[1] === 'year' ? m[2] : '';
  state.filter.person = m?.[1] === 'person' ? m[2] : '';
  if (state.filter.person && String(state.personDetail?.id) !== state.filter.person) { state.personDetail = null; loadPersonDetail(); }
  setLayout(m?.[1] === 'album' ? 'albums' : m?.[1] === 'year' ? 'years' : m?.[1] === 'person' ? 'people' : 'all');
  $('#main').innerHTML = mainHtml(); bindMain();
  renderBanner(); reload();
  scrollTo(0, 0);
}
/**
 * Marks the layout tab (all, albums, years; none for the admin page) and fits the top bar to the page: the item
 * filters only make sense over a grid of items, and on the Albums overview the search box finds albums by name.
 */
function setLayout(layout, overview = false) {
  $$('#layoutTabs a').forEach(a => a.classList.toggle('on', a.dataset.layout === layout));
  document.body.classList.toggle('overview', overview);
  document.body.classList.toggle('no-search', overview && state.view !== 'albums');
  const search = $('#search'), albums = state.view === 'albums';
  search.placeholder = albums ? 'Find an album…' : 'Search name, folder, tag…';
  search.value = albums ? state.albumQ : state.filter.q;
}
/** Leaves the virtual grid: its cells and observers go, so another page can use the main area. */
function leaveGrid(view) {
  state.view = view; galleryHash = null;
  if (state.selecting) setSelecting(false);
  gridRO?.disconnect(); clearCells(); $('#scrollPill').hidden = true; $('#monthNav').hidden = true;
}
function mainHtml() {
  return `<div id="albumBanner" hidden></div><button id="secPrev" class="section-bar" hidden></button><div id="grid" class="grid"></div>
    <button id="secNext" class="section-bar" hidden></button><div id="empty" hidden>Nothing here.</div><div id="count" class="count"></div>`;
}
let gridRO;
function bindMain() {
  const g = $('#grid');
  vg.cells.clear(); vg.heads.clear(); vg.width = 0;
  g.addEventListener('click', onGridClick);
  bindPinch(g);
  $('#secPrev').onclick = () => moveSection(-1);
  $('#secNext').onclick = () => moveSection(1);
  gridRO?.disconnect();
  gridRO = new ResizeObserver(() => { if (g.clientWidth !== vg.width) relayout(); });
  gridRO.observe(g);
}
window.addEventListener('hashchange', route);

// ---------- filters ----------
function filterQuery(extra = {}) {
  const f = state.filter, p = new URLSearchParams();
  if (f.q) p.set('q', f.q); if (f.kind) p.set('kind', f.kind); if (f.year) p.set('year', f.year);
  if (f.tagKey) p.append('tag', f.tagVal ? `${f.tagKey}:${f.tagVal}` : f.tagKey);
  if (f.album) p.set('album', f.album); if (f.person) p.set('person', f.person); if (f.dups) p.set('dups', '1'); if (f.variations) p.set('variations', '1');
  p.set('sort', f.sort);
  for (const [k, v] of Object.entries(extra)) p.set(k, v);
  return p.toString();
}
// ---------- gallery grid ----------
// Only the rows on screen, plus about OVERSCAN thumbnails above and below, are in the page, so scrolling through a
// big library keeps memory flat; a phone browser would otherwise hold every thumbnail it had shown. The grid is as
// tall as the whole result, so the browser's scrollbar and touch flings move through all of it. Item data is fetched
// a page at a time as rows come near the screen and dropped again once far away.
// Browsers cannot lay out anything taller than about 17 million pixels (Firefox; Chrome and Safari about 33M), so a
// result taller than vg.maxSpanPx is laid out a section at a time. A bar above or below the section moves to the
// next one, as does scrolling into its last screen; the items on screen stay where they are.
// A year sorted by date is laid out a month at a time instead, each under a heading (vg.groups, from /api/items/months),
// with buttons at the bottom right to jump between months; a year is short enough to need no sections.
// On a touch screen, pinching the grid changes how many thumbnails fit across (always square, as the photo apps do).
const PAGE = 200;      // items per request
const KEEP_PAGES = 12; // pages of item data kept around the view
const OVERSCAN = 25;   // thumbnails kept above and below the screen
const MONTH_HEAD = 40; // a month's heading, above its first row
// A heading this close under the top bar is the month on screen: a jump to it can stop a pixel short, as phones
// round where a scroll ends, and ▼ then took the month just jumped to for the next one.
const MONTH_SLACK = 4;
const MIN_CELL = 16;   // the smallest thumbnail pinching goes down to
const PINCH_COLS = [1, 2, 3, 4, 5, 6, 8, 10, 12, 15];
const PHONE_COLS = 4;  // across a phone until pinched (the size slider's 180 px made it one)
const narrow = matchMedia('(max-width: 900px)'); // a phone: no size slider, so the columns pinched are kept instead
let pinchCols = +localStorage.getItem('gridCols') || 0;
const vg = {
  cols: 1, cell: 0, gap: 6, rowH: 1, thumb: 256, width: 0,
  rows: 0, base: 0, span: 0, maxSpanPx: 12_000_000, // the section: first row and number of rows laid out
  months: null, monthsP: null, groups: null, groupsH: 0, heads: new Map(), // by month: [{ month, label, start, count, top, rows }]
  pages: new Map(), loading: new Map(), cells: new Map(), gone: new Set(),
  first: 0, last: -1, known: false, gen: 0, failedAt: 0,
  lastY: 0, fast: false, frame: 0, settleT: 0, pillT: 0,
};
const itemAt = i => vg.pages.get(Math.floor(i / PAGE))?.[i % PAGE];
function* loadedItems() { for (const items of vg.pages.values()) yield* items; }
function indexOfId(id) {
  for (const [p, items] of vg.pages) { const k = items.findIndex(x => x.id === id); if (k >= 0) return p * PAGE + k; }
  return -1;
}
/** The item at index i, fetching its page first when it is not loaded. */
async function ensureItem(i) { if (!itemAt(i)) await loadPage(Math.floor(i / PAGE)); return itemAt(i); }

/** Starts the grid over for the current filters: back at the top, or where it was with keepScroll (a refresh). */
function reload({ keepScroll = false } = {}) {
  if (state.view !== 'grid' || !$('#grid')) return;
  vg.gen++;
  for (const l of vg.loading.values()) l.ctl.abort();
  vg.loading.clear(); vg.pages.clear(); vg.gone.clear(); vg.failedAt = 0;
  clearCells();
  if (keepScroll && vg.known) { // keeps the old height until the new total arrives
    const gen = vg.gen;
    render(); loadMonths().then(() => { if (gen === vg.gen) relayout(); });
    return;
  }
  vg.known = false; vg.base = 0; state.total = 0; vg.months = null;
  layout(); scrollTo(0, 0); vg.lastY = 0;
  loadMonths(); loadPage(0);
}
/** Fetches how many items each month has, when the view is grouped by month (a year sorted by date). */
function loadMonths() {
  const gen = vg.gen;
  if (!state.filter.year || !state.filter.sort.startsWith('date')) { vg.months = null; return vg.monthsP = Promise.resolve(); }
  return vg.monthsP = api(`/api/items/months?${filterQuery()}`)
    .then(m => { if (gen === vg.gen) vg.months = m; }, () => { if (gen === vg.gen) vg.months = null; }); // ungrouped, then
}
function loadPage(p) {
  const l = vg.loading.get(p);
  if (l) return l.done;
  if (vg.pages.has(p)) return Promise.resolve();
  const gen = vg.gen, ctl = new AbortController();
  const done = (async () => {
    try {
      const r = await api(`/api/items?${filterQuery({ offset: p * PAGE, limit: PAGE })}`, { signal: ctl.signal });
      if (gen !== vg.gen) return;
      vg.pages.set(p, r.items);
      if (!vg.known) {
        await vg.monthsP; if (gen !== vg.gen) return; // laid out once, with its months
        vg.known = true; state.total = r.total;
        layout(); render();
      } else if (r.total !== state.total) { // the result grew or shrank, e.g. during a scan
        state.total = r.total; relayout();
        if (vg.months) loadMonths().then(() => { if (gen === vg.gen) relayout(); });
      } else fillPage(p);
      if (state.lb.open && state.lb.index < 0 && state.lb.detail) {
        const k = r.items.findIndex(x => x.id === state.lb.detail.item.id);
        if (k >= 0) state.lb.index = p * PAGE + k;
      }
      $('#empty').hidden = r.total > 0;
      $('#count').textContent = r.total ? `${fmtNum(r.total)} items` : '';
      if (!$('#scrollPill').hidden) showScrollPill(false);
      evictPages();
    } catch (e) {
      if (e.name === 'AbortError' || gen !== vg.gen) return;
      vg.failedAt = Date.now();
      if (!state.needLogin) toast(e.message);
    } finally { if (vg.loading.get(p)?.ctl === ctl) vg.loading.delete(p); }
  })();
  vg.loading.set(p, { ctl, done });
  return done;
}
/** Drops the item data furthest from the view, keeping what the grid shows and what the viewer is stepping through. */
function evictPages() {
  if (vg.pages.size <= KEEP_PAGES) return;
  const keep = new Set();
  for (let p = Math.floor(vg.first / PAGE) - 1; p <= Math.floor(vg.last / PAGE) + 1; p++) keep.add(p);
  if (state.lb.open && state.lb.index >= 0) { const q = Math.floor(state.lb.index / PAGE); keep.add(q - 1).add(q).add(q + 1); }
  const centre = Math.floor(vg.first / PAGE);
  for (const p of [...vg.pages.keys()].sort((a, b) => Math.abs(b - centre) - Math.abs(a - centre))) {
    if (vg.pages.size <= KEEP_PAGES) break;
    if (!keep.has(p)) vg.pages.delete(p);
  }
}

/** Works out the columns and row height from the grid's width and the thumbnail size, and places the cells again. */
function layout() {
  const g = $('#grid'); if (!g) return;
  const W = g.clientWidth, thumb = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--thumb')) || 180;
  vg.width = W; vg.gap = 6;
  vg.cols = narrow.matches ? Math.min(pinchCols || PHONE_COLS, Math.max(1, Math.floor((W + vg.gap) / (MIN_CELL + vg.gap))))
    : Math.max(1, Math.floor((W + vg.gap) / (thumb + vg.gap)));
  if (narrow.matches && vg.cols >= 4) vg.gap = 2; // a phone's dense grid, as its own photo app has it
  vg.cell = Math.max(1, (W - (vg.cols - 1) * vg.gap) / vg.cols);
  vg.rowH = vg.cell + vg.gap;
  const size = vg.cell * devicePixelRatio > 256 ? 512 : 256, sizeChanged = size !== vg.thumb;
  vg.thumb = size;
  clearHeads(); groupByMonth();
  for (const b of $$('#monthNav button')) delete b.dataset.to; // the months may be new: titles again
  if (vg.groups) { vg.rows = vg.span = vg.groups.reduce((n, m) => n + m.rows, 0); vg.base = 0; }
  else {
    vg.rows = Math.ceil(state.total / vg.cols);
    vg.span = Math.min(vg.rows, Math.max(1, Math.floor(vg.maxSpanPx / vg.rowH)));
    vg.base = clamp(vg.base, 0, vg.rows - vg.span);
  }
  g.style.setProperty('--cell', vg.cell + 'px');
  g.style.height = !vg.span ? '0' : `${vg.groups ? vg.groupsH : vg.span * vg.rowH - vg.gap}px`;
  g.classList.toggle('tight', vg.gap < 6);
  g.classList.toggle('small', vg.cell < 64); // too small for badges and names
  for (const [i, el] of vg.cells) {
    if (i >= state.total) { dropCell(i); continue; }
    place(el, i);
    const img = el.firstElementChild;
    if (sizeChanged && img?.tagName === 'IMG' && thumbs.has(img)) setSrc(el, itemAt(i));
  }
  const prev = $('#secPrev'), next = $('#secNext'), shown0 = vg.base * vg.cols, shown1 = Math.min(state.total, (vg.base + vg.span) * vg.cols);
  prev.hidden = vg.base === 0;
  next.hidden = vg.base + vg.span >= vg.rows;
  prev.textContent = `▲ Previous items · 1–${fmtNum(shown0)} of ${fmtNum(state.total)}`;
  next.textContent = `▼ More items · ${fmtNum(shown1 + 1)}–${fmtNum(state.total)} of ${fmtNum(state.total)}`;
}
/** layout(), keeping the first item on screen where it was. */
function relayout() { const a = anchor(); layout(); restoreAnchor(a); }
/**
 * Splits the result into its months (vg.groups), each starting a new row under its heading, when /api/items/months
 * gave them. Months that came back with more items than there are (the result shrank meanwhile) are cut short, and
 * items in none of them go last, until the counts are fetched again.
 */
function groupByMonth() {
  vg.groups = null;
  if (!vg.months?.length || !state.total) return;
  const groups = [];
  let start = 0, top = 0;
  const add = (month, n) => {
    const count = Math.min(n, state.total - start); if (count <= 0) return;
    const rows = Math.ceil(count / vg.cols);
    groups.push({ month, label: month ? fmtMonthLong(month) : 'Undated', start, count, top, rows });
    start += count; top += MONTH_HEAD + rows * vg.rowH;
  };
  for (const m of vg.months) add(m.month, m.count);
  add(null, state.total - start);
  if (top - vg.gap > vg.maxSpanPx) return; // too tall for one layout: in sections, without headings
  vg.groups = groups; vg.groupsH = top - vg.gap;
}
const fmtMonthLong = ym => new Date(ym + '-01T00:00:00').toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
/** The month group (index into vg.groups) that item i is in. */
function groupOf(i) {
  const gs = vg.groups; let lo = 0, hi = gs.length - 1;
  while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (gs[mid].start <= i) lo = mid; else hi = mid - 1; }
  return lo;
}
/** The month group whose heading or rows are at y (px from the top of the grid). */
function groupAtY(y) {
  const gs = vg.groups; let lo = 0, hi = gs.length - 1;
  while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (gs[mid].top <= y) lo = mid; else hi = mid - 1; }
  return lo;
}
/** Where item i's cell goes, in px from the grid's top left. A column is as wide as a row is tall: cell + gap. */
function itemTop(i) {
  if (!vg.groups) return (Math.floor(i / vg.cols) - vg.base) * vg.rowH;
  const m = vg.groups[groupOf(i)];
  return m.top + MONTH_HEAD + Math.floor((i - m.start) / vg.cols) * vg.rowH;
}
const itemLeft = i => ((vg.groups ? i - vg.groups[groupOf(i)].start : i) % vg.cols) * vg.rowH;
/** The first item in the row at y (a month's first under its heading), within the section laid out. */
function firstAtY(y) {
  if (!vg.groups) return (vg.base + clamp(Math.floor(y / vg.rowH), 0, vg.span - 1)) * vg.cols;
  const m = vg.groups[groupAtY(y)], dy = y - m.top - MONTH_HEAD;
  return dy < 0 ? m.start : m.start + Math.min(Math.floor(dy / vg.rowH), m.rows - 1) * vg.cols;
}
/** The last item in the row at y (above a month's heading: the month before's last). */
function lastAtY(y) {
  if (!vg.groups) return Math.min(state.total - 1, (vg.base + clamp(Math.floor(y / vg.rowH), 0, vg.span - 1) + 1) * vg.cols - 1);
  const m = vg.groups[groupAtY(y)], dy = y - m.top - MONTH_HEAD;
  return dy < 0 ? m.start - 1 : Math.min(m.start + m.count, m.start + (Math.min(Math.floor(dy / vg.rowH), m.rows - 1) + 1) * vg.cols) - 1;
}
function place(el, i) { el.style.left = `${itemLeft(i)}px`; el.style.top = `${itemTop(i)}px`; }
/** The first row on screen below the top bar (as an item index) and its distance from the top of the window. */
function anchor() {
  const g = $('#grid'); if (!g || !vg.span) return null;
  const top = g.getBoundingClientRect().top, index = firstAtY(headerHeight() - top);
  return { index, at: top + itemTop(index) };
}
function restoreAnchor(a) { if (a) scrollToIndex(a.index, a.at); else render(); }
/**
 * Scrolls so item i is `at` px below the top of the window; without `at`, just far enough to bring it on screen.
 * Moves the section first when i is outside it.
 */
function scrollToIndex(i, at) {
  const g = $('#grid'); if (!g || i < 0 || i >= state.total) return;
  const row = Math.floor(i / vg.cols);
  if (row < vg.base || row >= vg.base + vg.span) { vg.base = clamp(row - Math.floor(vg.span / 2), 0, vg.rows - vg.span); layout(); }
  const y = g.getBoundingClientRect().top + scrollY + itemTop(i); // the row's top in the document
  const bar = headerHeight();
  let to = scrollY;
  if (at != null) to = y - at;
  else if (y < scrollY + bar) to = y - bar - 12;
  else if (y + vg.cell > scrollY + innerHeight) to = y + vg.cell - innerHeight + 12;
  if (to !== scrollY) scrollTo(0, to);
  vg.lastY = scrollY; // a jump we made is not a fast scroll
  render();
}
/** Moves the section half its length up (-1) or down (1). */
function moveSection(dir) {
  const a = anchor();
  vg.base = clamp(vg.base + dir * Math.floor(vg.span / 2), 0, vg.rows - vg.span);
  layout(); restoreAnchor(a);
}

/** Brings the cells in line with the scroll position: adds the ones coming near the screen, removes the rest. */
function render() {
  const g = $('#grid'); if (!g || state.view !== 'grid') return;
  if (!state.total || !vg.span) { clearCells(); vg.first = 0; vg.last = -1; updateMonthNav(); return; }
  const y0 = -g.getBoundingClientRect().top, y1 = y0 + innerHeight;
  updateMonthNav(); // reads the layout, so before the cells change it
  const extra = Math.ceil(OVERSCAN / vg.cols) * vg.rowH;
  const first = firstAtY(y0 - extra), last = Math.max(first - 1, lastAtY(y1 + extra));
  vg.first = first; vg.last = last;
  for (const i of [...vg.cells.keys()]) if (i < first || i > last) dropCell(i);
  const frag = document.createDocumentFragment();
  for (let i = first; i <= last; i++) if (!vg.cells.has(i)) { const el = makeCell(i); vg.cells.set(i, el); frag.appendChild(el); }
  if (vg.groups) {
    const k0 = groupAtY(y0 - extra), k1 = groupAtY(y1 + extra);
    for (const [k, el] of vg.heads) if (k < k0 || k > k1) { el.remove(); vg.heads.delete(k); }
    for (let k = k0; k <= k1; k++) if (!vg.heads.has(k)) { const el = makeHead(vg.groups[k]); vg.heads.set(k, el); frag.appendChild(el); }
  }
  g.appendChild(frag);
  if (vg.fast || Date.now() - vg.failedAt < 5000) return; // dragging the scrollbar: fetch where it stops
  // Item data for these rows, and for a page either side so ordinary scrolling never meets an empty cell.
  const p0 = Math.max(0, Math.floor(first / PAGE) - 1), p1 = Math.min(Math.floor((state.total - 1) / PAGE), Math.floor(last / PAGE) + 1);
  const lbPage = state.lb.open && state.lb.index >= 0 ? Math.floor(state.lb.index / PAGE) : -9;
  for (const [p, l] of vg.loading) if ((p < p0 || p > p1) && Math.abs(p - lbPage) > 1) { l.ctl.abort(); vg.loading.delete(p); }
  for (let p = p0; p <= p1; p++) loadPage(p);
}
function makeCell(i) {
  const it = itemAt(i), d = document.createElement('div');
  d.dataset.index = i; place(d, i);
  if (!it) { d.className = 'cell ph'; return d; }
  d.className = 'cell' + (state.selected.has(it.id) ? ' sel' : '') + (vg.gone.has(it.id) ? ' gone' : '') + (it.hiddenFromViewers || it.inHiddenAlbum ? ' vhidden' : '');
  d.dataset.id = it.id;
  const kindIcon = it.kind === 'video' ? '▶' : it.kind === 'audio' ? '♪' : '';
  d.innerHTML = `<img alt="">
    <span class="tick"></span>
    ${it.isRaw ? '<span class="badge b-raw">RAW</span>' : ''}
    ${it.projection ? '<span class="badge b-360" title="360° photo">360°</span>' : ''}
    ${it.assetCount > 1 ? `<span class="badge b-var" title="${it.assetCount} variations">${it.assetCount}×</span>` : ''}
    ${kindIcon ? `<span class="badge b-kind">${kindIcon}</span>` : ''}
    ${it.durationSec ? `<span class="badge b-dur">${fmtDur(it.durationSec)}</span>` : ''}
    ${it.edited ? '<span class="badge b-edit" title="Edited in the photo editor">✎</span>' : ''}
    ${hiddenBadge(it)}
    <span class="label">${esc(it.title)} · ${esc((it.takenAt || '').slice(0, 10))}</span>`;
  const img = d.firstElementChild;
  img.onload = () => img.classList.add('loaded');
  img.onerror = () => { if (!img.src.includes('/img/')) img.src = it.kind === 'audio' ? '/img/audio.svg' : '/img/broken.svg'; };
  if (!vg.fast) setSrc(d, it); // while the scrollbar is dragged, thumbnails wait until it stops
  return d;
}
function setSrc(el, it) {
  const img = el.firstElementChild; if (!it || img?.tagName !== 'IMG') return;
  if (it.primaryAssetId) thumbs.load(img, thumbUrl(it.primaryAssetId, it.hash, it.rotation, vg.thumb, it.squeeze));
  else img.src = '/img/broken.svg';
}
/** Swaps the placeholder cells of page p for real ones now that its items have arrived. */
function fillPage(p) {
  for (let i = p * PAGE, end = i + PAGE; i < end; i++) {
    const el = vg.cells.get(i);
    if (el?.classList.contains('ph')) { const n = makeCell(i); el.replaceWith(n); vg.cells.set(i, n); }
  }
}
function dropCell(i) {
  const el = vg.cells.get(i); if (!el) return;
  const img = el.firstElementChild;
  // A download that has started finishes (see thumbs); one still waiting is dropped.
  if (img?.tagName === 'IMG') { thumbs.forget(img); if (!img.complete) img.onload = img.onerror = null; }
  el.remove(); vg.cells.delete(i);
}
function clearCells() { for (const i of [...vg.cells.keys()]) dropCell(i); clearHeads(); }
function makeHead(m) {
  const el = document.createElement('div');
  el.className = 'month-head'; el.style.top = `${m.top}px`;
  el.innerHTML = `${esc(m.label)}<span>${fmtNum(m.count)}</span>`;
  return el;
}
function clearHeads() { for (const el of vg.heads.values()) el.remove(); vg.heads.clear(); }

/** The ▲ ▼ buttons of a view by month: to the start of this month (or the one before, when at it), and the next. */
function updateMonthNav() {
  const nav = $('#monthNav'), g = $('#grid'), gs = vg.groups, hide = !(g && gs?.length > 1 && state.view === 'grid');
  if (nav.hidden !== hide) nav.hidden = hide;
  if (hide) return;
  const y = headerHeight() - g.getBoundingClientRect().top, k = groupAtY(y + MONTH_SLACK);
  const atEnd = scrollY + innerHeight >= document.documentElement.scrollHeight - 2; // the next month can't come any higher
  const set = (b, to) => { // on every scroll frame: only what changed
    if (to < 0 || to >= gs.length) to = -1;
    if (b.dataset.to === String(to)) return;
    b.dataset.to = to; b.disabled = to < 0; b.title = to < 0 ? '' : gs[to].label;
  };
  set($('#monthPrev'), y - gs[k].top > MONTH_SLACK ? k : k - 1);
  set($('#monthNext'), atEnd ? -1 : k + 1);
}
/** Scrolls month group k's heading up to the top bar. */
function jumpToMonth(k) {
  const g = $('#grid'), m = vg.groups?.[k]; if (!g || !m) return;
  scrollTo(0, Math.max(0, g.getBoundingClientRect().top + scrollY + m.top - headerHeight()));
  vg.lastY = scrollY; // a jump, not a fast scroll
  render(); showScrollPill(true);
}
$('#monthPrev').onclick = $('#monthNext').onclick = e => jumpToMonth(+e.currentTarget.dataset.to);

// ---------- pinch: more or fewer thumbnails across ----------
const pinch = { d: 0 };
const fingerGap = t => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);
function bindPinch(g) {
  g.addEventListener('touchstart', e => { if (e.touches.length === 2) pinch.d = fingerGap(e.touches); }, { passive: true });
  g.addEventListener('touchmove', e => {
    if (e.touches.length !== 2 || !pinch.d) return;
    if (e.cancelable) e.preventDefault(); // not the browser's own zoom
    const d = fingerGap(e.touches), ratio = d / pinch.d;
    if (ratio > 0.8 && ratio < 1.25) return;
    pinch.d = d; // each step is a fresh stretch of the fingers
    stepCols(ratio > 1 ? -1 : 1, (e.touches[0].clientX + e.touches[1].clientX) / 2, (e.touches[0].clientY + e.touches[1].clientY) / 2);
  }, { passive: false });
  const end = e => { if (e.touches.length < 2) pinch.d = 0; };
  g.addEventListener('touchend', end); g.addEventListener('touchcancel', end);
  g.addEventListener('gesturestart', e => e.preventDefault()); // Safari's page zoom
}
/**
 * Shows the next step of more (dir 1) or fewer (-1) columns, keeping the thumbnail under (x, y) under it. On a phone
 * the columns are kept as such; elsewhere they set the size slider, which the album and year cards follow too.
 */
function stepCols(dir, x, y) {
  if (state.view !== 'grid' || !vg.width) return false;
  const most = Math.max(1, Math.floor((vg.width + vg.gap) / (MIN_CELL + vg.gap)));
  const cols = dir > 0 ? PINCH_COLS.find(c => c > vg.cols) : PINCH_COLS.filter(c => c < vg.cols).pop();
  if (!cols || cols > most) return false;
  const cell = document.elementFromPoint(x, y)?.closest('.cell');
  const r = cell && vg.cells.get(+cell.dataset.index) === cell ? cell.getBoundingClientRect() : null;
  const a = r ? { index: +cell.dataset.index, frac: (y - r.top) / r.height } : anchor();
  if (narrow.matches) { pinchCols = cols; localStorage.setItem('gridCols', cols); }
  else {
    const px = clamp(Math.floor((vg.width + 6) / cols - 6), +sizeRange.min, +sizeRange.max);
    if (Math.floor((vg.width + 6) / (px + 6)) === vg.cols) return false; // the slider goes no further
    setThumb(px);
  }
  layout();
  if (r) scrollToIndex(a.index, y - a.frac * vg.cell); else restoreAnchor(a);
  return true;
}

addEventListener('scroll', () => {
  if (state.view !== 'grid' || vg.frame) return;
  vg.frame = requestAnimationFrame(() => {
    vg.frame = 0;
    const dy = Math.abs(scrollY - vg.lastY); vg.lastY = scrollY;
    if (dy > innerHeight * 0.75) vg.fast = true; // most of a screen in one frame: the scrollbar is being dragged
    clearTimeout(vg.settleT);
    vg.settleT = setTimeout(() => {
      vg.fast = false; render();
      for (const [i, el] of vg.cells) if (el.firstElementChild?.tagName === 'IMG' && !thumbs.has(el.firstElementChild)) setSrc(el, itemAt(i));
    }, 150);
    render();
    if (!vg.fast) followSection();
    showScrollPill(true);
  });
}, { passive: true });
/** Moves to the neighbouring section when scrolling reaches the last screen of this one (very large results only). */
function followSection() {
  const g = $('#grid'); if (!g || vg.span >= vg.rows) return;
  const y0 = -g.getBoundingClientRect().top;
  if (vg.base + vg.span < vg.rows && y0 + 2 * innerHeight > vg.span * vg.rowH) moveSection(1);
  else if (vg.base > 0 && y0 < innerHeight) moveSection(-1);
}
/** While scrolling a long view: the date (or initial, when sorted by name) and position of the first row on screen. */
function showScrollPill(restart) {
  const pill = $('#scrollPill'), g = $('#grid');
  if (!g || state.view !== 'grid' || (vg.groups ? vg.groupsH : vg.rows * vg.rowH) < 3 * innerHeight) { pill.hidden = true; return; }
  const i = firstAtY(headerHeight() - g.getBoundingClientRect().top + MONTH_SLACK), it = itemAt(i), sort = state.filter.sort;
  const label = vg.groups ? vg.groups[groupOf(i)].label
    : !it || sort === 'added' ? '' : sort === 'name' ? (it.title || '?').charAt(0).toUpperCase() : fmtMonth(it.takenAt);
  pill.innerHTML = `${label ? `<b>${esc(label)}</b>` : ''}<span>${fmtNum(i + 1)} of ${fmtNum(state.total)}</span>`;
  pill.hidden = false;
  if (restart) { clearTimeout(vg.pillT); vg.pillT = setTimeout(() => pill.hidden = true, 1200); }
}

async function onGridClick(e) {
  const cell = e.target.closest('.cell'); if (!cell || !cell.dataset.id) return;
  const id = +cell.dataset.id, idx = +cell.dataset.index;
  if (state.selecting || e.metaKey || e.ctrlKey) {
    if (!state.selecting) setSelecting(true);
    if (e.shiftKey && state.lastClicked != null) {
      const [a, b] = [state.lastClicked, idx].sort((x, y) => x - y);
      let ids = [];
      for (let i = a; i <= b && ids; i++) ids = itemAt(i) ? [...ids, itemAt(i).id] : null;
      ids ??= (await api(`/api/items/ids?${filterQuery()}`)).slice(a, b + 1); // part of the range is not loaded
      ids.forEach(i => state.selected.add(i));
    } else if (state.selected.has(id)) state.selected.delete(id); else state.selected.add(id);
    state.lastClicked = idx; refreshSelection(); return;
  }
  openLightbox(idx);
}

// ---------- selection ----------
function setSelecting(on) { state.selecting = on; document.body.classList.toggle('selecting', on); $('#selbar').hidden = !on; $('#selectBtn').textContent = on ? 'Cancel' : 'Select'; if (!on) { state.selected.clear(); refreshSelection(); } }
function refreshSelection() {
  $$('.cell').forEach(c => c.classList.toggle('sel', state.selected.has(+c.dataset.id)));
  $('#selCount').textContent = `${state.selected.size} selected`;
  $('#selRemoveAlbumBtn').hidden = !state.filter.album;
  $('#selCoverBtn').hidden = !(state.filter.album || state.filter.year) || state.selected.size !== 1;
}
$('#selectBtn').onclick = () => setSelecting(!state.selecting);
$('#selDoneBtn').onclick = () => setSelecting(false);
$('#selClearBtn').onclick = () => { state.selected.clear(); refreshSelection(); };
$('#selAllBtn').onclick = async () => { const ids = await api(`/api/items/ids?${filterQuery()}`); ids.forEach(i => state.selected.add(i)); refreshSelection(); toast(`${ids.length} selected`); };
$('#selTagBtn').onclick = () => tagDialog([...state.selected]);
$('#selAlbumBtn').onclick = () => albumPicker([...state.selected]);
$('#selRecipeBtn').onclick = () => recipeDialog([...state.selected]);
$('#selCoverBtn').onclick = async () => {
  const [itemId] = state.selected;
  if (await setCover(state.filter.album ? 'album' : 'year', itemId)) setSelecting(false);
};
$('#selHideBtn').onclick = () => setHidden([...state.selected], true);
$('#selShowBtn').onclick = () => setHidden([...state.selected], false);
$('#selSqueezeBtn').onclick = () => squeezeDialog([...state.selected]);
$('#selCollageBtn').onclick = () => {
  if (!state.selected.size) { toast(`Select up to ${COLLAGE_MAX} photos first`); return; }
  openCollage([...state.selected], { api, modal, thumbUrl, errorText, fmtBytes });
};

// ---------- hidden from viewers ----------
// An admin can hide items, or whole albums (and so everything in them), from viewers. The server withholds them from
// viewers; admins see everything, with hidden things dimmed and marked.
const EYE_OFF = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" aria-hidden="true"><path d="M3 3l18 18M10.6 5.1A10 10 0 0 1 12 5c5 0 9 4.5 10 7a13 13 0 0 1-3 4.2M6.1 6.1A13 13 0 0 0 2 12c1 2.5 5 7 10 7 1.9 0 3.6-.6 5-1.5M9.9 9.9a3 3 0 0 0 4.2 4.2"/></svg>';
function hiddenBadge(it) {
  if (!it.hiddenFromViewers && !it.inHiddenAlbum) return '';
  const why = it.hiddenFromViewers ? 'Hidden from viewers' : 'Hidden from viewers: it is in a hidden album';
  return `<span class="badge b-hidden" title="${why}">${EYE_OFF}</span>`;
}
async function setHidden(itemIds, hidden) {
  if (!itemIds.length) return;
  const r = await api('/api/items/hidden', { method: 'POST', body: { itemIds, hidden } });
  toast(hidden ? `${fmtNum(itemIds.length)} hidden from viewers` : `${fmtNum(itemIds.length)} shown to viewers${r.changed < itemIds.length ? ' (a hidden album still hides some)' : ''}`);
  setSelecting(false); reload({ keepScroll: true }); loadAlbums(); loadYears();
}

// ---------- anamorphic lenses ----------
// A photo taken through an anamorphic lens is squeezed along its long side; the item's squeeze (the "anamorphic" tag)
// stretches it back wherever it is shown, edited and exported. The files stay as they are.
/** Sets the squeeze (1: none) of items, and shows them stretched. */
async function setSqueeze(itemIds, factor) {
  if (!itemIds.length) return;
  const r = await api('/api/items/anamorphic', { method: 'POST', body: { itemIds, factor } });
  const ids = new Set(itemIds);
  for (const it of loadedItems()) if (ids.has(it.id)) it.squeeze = r.squeeze;
  for (const [i, el] of vg.cells) if (ids.has(+el.dataset.id)) { const fresh = makeCell(i); el.replaceWith(fresh); vg.cells.set(i, fresh); }
  const d = state.lb.detail;
  if (state.lb.open && d && ids.has(d.item.id)) showItem(d.item.id, state.lb.assetId);
  loadTagKeys();
  const n = `${fmtNum(itemIds.length)} item${itemIds.length === 1 ? '' : 's'}`;
  toast(r.squeeze > 1 ? `Stretched ${fmtSqueeze(r.squeeze)}× (anamorphic): ${n}` : `Anamorphic stretch removed: ${n}`);
}
/** Asks for the factor for the selected items. */
function squeezeDialog(itemIds) {
  if (!itemIds.length) return;
  const n = itemIds.length;
  modal(`<h2>Anamorphic lens</h2><form id="squeezeForm" class="form">
    <p class="muted small">An anamorphic lens squeezes the picture along its long side. Stretching it back by the lens's factor
      shows ${n === 1 ? 'the photo' : 'these photos'} as ${n === 1 ? 'it was' : 'they were'} seen: in the gallery, the viewer, the photo
      editor and exports. Your files stay as they are.</p>
    <label>Stretch the long side by <select name="factor">
      <option value="1">None (1×)</option>${SQUEEZES.map(s => `<option value="${s}"${s === 1.33 ? ' selected' : ''}>${fmtSqueeze(s)}×</option>`).join('')}
      <option value="other">Other…</option></select></label>
    <label id="squeezeOther" hidden>Factor <input name="other" type="number" min="1" max="3" step="0.01" value="1.5"></label>
    <div class="row"><button>Apply to ${fmtNum(n)} item${n === 1 ? '' : 's'}</button><button type="button" class="ghost" data-close>Cancel</button></div></form>`);
  const f = $('#squeezeForm');
  f.factor.onchange = () => { $('#squeezeOther').hidden = f.factor.value !== 'other'; };
  f.onsubmit = async e => {
    e.preventDefault();
    const factor = f.factor.value === 'other' ? +f.other.value : +f.factor.value;
    if (!(factor >= 1 && factor <= 3)) { toast('Use a factor between 1 and 3'); return; }
    closeModal();
    try { await setSqueeze(itemIds, factor); setSelecting(false); } catch (err) { toast(err.message); }
  };
}

/** Applies a recipe to the selected items: the RAW of each, optionally other photos too, optionally exporting JPEGs. */
async function recipeDialog(itemIds) {
  const [recipes, counts] = await Promise.all([api('/api/recipes'), api('/api/recipes/targets', { method: 'POST', body: { itemIds } })]);
  if (!recipes.length) { toast('No recipes yet: open a photo in the editor and use "Save as recipe…"'); return; }
  let prefs = {}; try { prefs = JSON.parse(localStorage.getItem('gallery.exportOptions') || '{}'); } catch { }
  modal(`<h2>Apply a recipe</h2><form id="recipeForm" class="form">
    <label>Recipe <select name="recipe">${recipes.map(r => `<option value="${r.id}">${esc(r.name)}</option>`).join('')}</select></label>
    <div class="muted small" id="recipeParts"></div>
    <div>${fmtNum(counts.raw)} of the ${fmtNum(counts.selected)} selected item${counts.selected === 1 ? '' : 's'} ${counts.raw === 1 ? 'has' : 'have'} a RAW file${counts.other ? `; ${fmtNum(counts.other)} ${counts.other === 1 ? 'is a photo' : 'are photos'} without one` : ''}.</div>
    <label class="chk"><input type="checkbox" name="nonRaw" ${counts.raw === 0 && counts.other ? 'checked' : ''}> Also photos without a RAW (${fmtNum(counts.other)})</label>
    <label class="chk"><input type="checkbox" name="export"> Export JPEGs too, to the gallery (${esc(prefs.size && prefs.size !== 'original' ? (prefs.size === 'percent' ? (prefs.sizeValue || 50) + ' %' : (prefs.size === 'custom' ? prefs.sizeValue : prefs.size) + ' px') : 'original size')}, quality ${esc(prefs.quality || 90)})</label>
    <div class="row"><button>Apply</button><button type="button" class="ghost" data-close>Cancel</button></div>
    <small class="muted">Only the recipe's parts change; each photo keeps the rest of its edit. You can undo this right after.</small></form>`);
  const f = $('#recipeForm');
  const parts = () => { const r = recipes.find(x => String(x.id) === f.recipe.value); $('#recipeParts').textContent = 'Changes: ' + r.sections.map(s => ({ whiteBalance: 'white balance', tone: 'tone', color: 'colour', curves: 'curves', detail: 'detail' }[s])).join(', '); };
  f.recipe.onchange = parts; parts();
  f.addEventListener('submit', async e => {
    e.preventDefault();
    const size = prefs.size || 'original', value = +prefs.sizeValue || 0;
    const exportOptions = f.export.checked ? {
      longEdge: size === 'original' || size === 'percent' ? null : size === 'custom' ? value || null : +size,
      percent: size === 'percent' ? value || 50 : null, quality: +prefs.quality || 90, fullChroma: (prefs.chroma || 'full') === 'full',
      keepLocation: prefs.keepLocation !== false, saveToGallery: true, download: false, useAsPrimary: false,
    } : null;
    const r = await api(`/api/recipes/${f.recipe.value}/apply`, { method: 'POST', body: { itemIds, includeNonRaw: f.nonRaw.checked, export: exportOptions } });
    closeModal(); setSelecting(false); reload({ keepScroll: true });
    const what = `Applied to ${fmtNum(r.applied)} photo${r.applied === 1 ? '' : 's'}${r.skipped ? ` (${fmtNum(r.skipped)} skipped)` : ''}${r.exportJobs.length ? `; exporting ${fmtNum(r.exportJobs.length)}` : ''}`;
    toast(what, r.applied ? { label: 'Undo', run: async () => { const u = await api('/api/edits/revert', { method: 'POST', body: { assetIds: r.assetIds } }); toast(`Undone for ${fmtNum(u.reverted)} photo${u.reverted === 1 ? '' : 's'}${r.exportJobs.length ? ' (exports already made stay)' : ''}`); reload({ keepScroll: true }); } } : null);
    pollStatus();
  });
}

$('#selRemoveAlbumBtn').onclick = async () => { await api(`/api/albums/${state.filter.album}/items`, { method: 'DELETE', body: { itemIds: [...state.selected] } }); toast('Removed from album'); setSelecting(false); await loadAlbums(); reload({ keepScroll: true }); };

async function tagDialog(itemIds, detailRefresh) {
  const keys = await api('/api/tags/keys');
  modal(`<h2>Tag ${itemIds.length} item${itemIds.length > 1 ? 's' : ''}</h2>
    <form id="tagForm" class="form">
      <label>Key <input name="key" list="tagKeys" placeholder="e.g. people, place, event" required><datalist id="tagKeys">${keys.map(k => `<option value="${esc(k.key)}">`).join('')}</datalist></label>
      <label>Value <input name="value" list="tagVals" placeholder="e.g. Alice" required><datalist id="tagVals"></datalist></label>
      <div class="row"><button>Add tag</button><button type="button" class="ghost" data-close>Close</button></div>
      <small class="muted">Tags are key/value pairs. Items can have many. Use the tag filter in the top bar to find them again.</small>
    </form>`);
  const keyIn = $('#tagForm [name=key]');
  keyIn.addEventListener('change', async () => { const vs = await api(`/api/tags/values?key=${encodeURIComponent(keyIn.value)}`); $('#tagVals').innerHTML = vs.map(v => `<option value="${esc(v.value)}">`).join(''); });
  $('#tagForm').addEventListener('submit', async e => {
    e.preventDefault(); const fd = new FormData(e.target);
    await api('/api/items/tags', { method: 'POST', body: { itemIds, add: [{ key: fd.get('key'), value: fd.get('value') }] } });
    toast(`Tagged ${plural(itemIds.length, 'item')}`); e.target.value.value = '';
    loadTagKeys(); detailRefresh?.();
  });
}
async function albumPicker(itemIds) {
  await loadAlbums();
  modal(`<h2>Add ${plural(itemIds.length, 'item')} to album</h2><form id="albForm" class="form">
    <label>Existing album <select name="album"><option value="">— new album —</option>${state.albums.map(a => `<option value="${a.id}">${esc(a.name)} (${a.itemCount})</option>`).join('')}</select></label>
    <label>New album name <input name="name" placeholder="Holiday 2024"></label>
    <div class="row"><button>Add</button><button type="button" class="ghost" data-close>Cancel</button></div></form>`);
  $('#albForm').addEventListener('submit', async e => {
    e.preventDefault(); const fd = new FormData(e.target);
    if (fd.get('album')) await api(`/api/albums/${fd.get('album')}/items`, { method: 'POST', body: { itemIds } });
    else if (fd.get('name')) await api('/api/albums', { method: 'POST', body: { name: fd.get('name'), itemIds } });
    else return;
    closeModal(); toast('Added to album'); setSelecting(false); await loadAlbums();
  });
}

// ---------- sidebar ----------
// ---------- albums and years ----------
const byName = (a, b) => a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
async function loadAlbums() {
  try { state.albums = (await api('/api/albums', { quiet: true })).sort(byName); } catch { state.albums = []; }
  if (state.view === 'albums') renderAlbumCards();
  if (state.view === 'grid' && state.filter.album) renderBanner();
}
async function loadYears() {
  try { state.years = await api('/api/items/years', { quiet: true }); } catch { state.years = []; }
  if (state.view === 'years') renderYearCards();
  if (state.view === 'grid' && state.filter.year) renderBanner();
}
const fmtDay = iso => iso ? iso.slice(0, 10) : '';
function albumDates(a) {
  const from = fmtDay(a.dateFrom), to = fmtDay(a.dateTo);
  return from ? (to && to !== from ? `${from} – ${to}` : from) : '';
}
/** Thumbnail size for the cards at the current card width and screen density. */
const coverSize = () => (parseInt(getComputedStyle(document.documentElement).getPropertyValue('--thumb')) || 180) * devicePixelRatio > 256 ? 512 : 256;
function cardHtml({ href, src, name, meta, hidden, hiddenTitle }) {
  return `<a class="card${hidden ? ' vhidden' : ''}" href="${href}" title="${esc(name)}">
    <div class="cover">${src ? `<img alt="" data-src="${src}">` : ''}${hidden ? `<span class="badge b-hidden" title="${hiddenTitle}">${EYE_OFF} Hidden</span>` : ''}</div>
    <div class="name">${esc(name)}</div><div class="meta">${meta}</div></a>`;
}
// Covers load as they come near the screen and are let go again far from it, so a long list of albums never holds
// more than a few screens of thumbnails (as the item grid does).
let coverIO;
function observeCovers(root) {
  coverIO?.disconnect();
  coverIO = new IntersectionObserver(entries => {
    for (const e of entries) {
      const img = e.target;
      if (e.isIntersecting) {
        delete img.dataset.far;
        if (!thumbs.has(img)) {
          // One that went far away while downloading is let go once it arrives.
          img.onload = () => { if (img.dataset.far) img.removeAttribute('src'); else img.classList.add('loaded'); };
          thumbs.load(img, img.dataset.src);
        }
      } else {
        img.dataset.far = '1'; thumbs.forget(img);
        if (img.complete && img.getAttribute('src')) { img.removeAttribute('src'); img.classList.remove('loaded'); }
      }
    }
  }, { rootMargin: '1200px 0px' });
  $$('img[data-src]', root).forEach(img => coverIO.observe(img));
}
const ALBUM_SORTS = { name: 'Name', newest: 'Newest', oldest: 'Oldest' };
function sortedAlbums(list) {
  const date = a => a.dateTo || a.dateFrom || a.createdAt || '';
  if (state.albumSort === 'newest') return [...list].sort((a, b) => date(b).localeCompare(date(a)) || byName(a, b));
  if (state.albumSort === 'oldest') return [...list].sort((a, b) => (a.dateFrom || date(a)).localeCompare(b.dateFrom || date(b)) || byName(a, b));
  return list; // state.albums is kept in name order
}
async function showAlbums() {
  leaveGrid('albums');
  setLayout('albums', true);
  $('#main').innerHTML = `<div class="ov-head"><h2>Albums <span class="muted" id="albumCount"></span></h2>
      <div class="seg" id="albumSort">${Object.entries(ALBUM_SORTS).map(([k, l]) => `<button data-sort="${k}"${state.albumSort === k ? ' class="on"' : ''}>${l}</button>`).join('')}</div>
      <span class="admin-only"><button class="ghost small" id="newAlbumBtn">+ New album</button>
        <button class="ghost small" id="autoAlbumBtn" title="Create or update albums from the folders photos are in">Albums from folders</button></span>
    </div><div id="albumGrid" class="card-grid"></div>`;
  $$('#albumSort button').forEach(b => b.onclick = () => {
    state.albumSort = b.dataset.sort; try { localStorage.setItem('albumSort', state.albumSort); } catch { }
    $$('#albumSort button').forEach(x => x.classList.toggle('on', x === b)); renderAlbumCards();
  });
  $('#newAlbumBtn').onclick = newAlbum;
  $('#autoAlbumBtn').onclick = autoAlbums;
  renderAlbumCards();
  scrollTo(0, state.overviewScroll.albums || 0);
  await loadAlbums();
}
function renderAlbumCards() {
  const grid = $('#albumGrid'); if (!grid) return;
  const q = state.albumQ.toLowerCase();
  const list = sortedAlbums(q ? state.albums.filter(a => a.name.toLowerCase().includes(q)) : state.albums);
  $('#albumCount').textContent = q ? `${fmtNum(list.length)} of ${fmtNum(state.albums.length)}` : fmtNum(state.albums.length);
  const size = coverSize();
  grid.innerHTML = list.map(a => cardHtml({
    href: `#/album/${a.id}`,
    src: a.coverAssetId ? thumbUrl(a.coverAssetId, a.coverHash, a.coverRotation, size, a.coverSqueeze) : null,
    name: a.name,
    meta: `${fmtNum(a.itemCount)} item${a.itemCount === 1 ? '' : 's'}${albumDates(a) ? ' · ' + albumDates(a) : ''}`,
    hidden: !!a.viewerHidden, hiddenTitle: 'Hidden from viewers, with everything in it',
  })).join('') || `<div class="muted">${q ? 'No album has that in its name.' : 'No albums yet.'}</div>`;
  observeCovers(grid);
}
async function showYears() {
  leaveGrid('years');
  setLayout('years', true);
  $('#main').innerHTML = `<div class="ov-head"><h2>Years <span class="muted" id="yearCount"></span></h2></div><div id="yearGrid" class="card-grid"></div>`;
  renderYearCards();
  scrollTo(0, state.overviewScroll.years || 0);
  await loadYears();
}
function renderYearCards() {
  const grid = $('#yearGrid'); if (!grid) return;
  const size = coverSize();
  $('#yearCount').textContent = fmtNum(state.years.length);
  grid.innerHTML = state.years.map(y => cardHtml({
    href: `#/year/${y.year}`,
    src: y.cover ? thumbUrl(y.cover.assetId, y.cover.hash, y.cover.rotation, size, y.cover.squeeze) : null,
    name: String(y.year), meta: `${fmtNum(y.count)} item${y.count === 1 ? '' : 's'}`,
  })).join('') || '<div class="muted">No dated items yet.</div>';
  observeCovers(grid);
}
// ---------- people ----------
// #/people lists the people found in the photos (named ones; admins also see suggestions to name); #/person/ID is the
// item grid filtered by a person; #/person/ID/faces and #/faces are where admins correct faces.
const cropUrl = (faceId, size) => `/api/faces/${faceId}/crop?size=${size}`;
const cropSize = () => coverSize() > 256 ? 320 : 160;
const plural = (n, one, many = one + 's') => `${fmtNum(n)} ${n === 1 ? one : many}`;
async function loadPeople() {
  try { state.people = await api(`/api/people${state.peopleHidden ? '?hidden=true' : ''}`, { quiet: true }); } catch { state.people = []; }
  $('#peopleNames').innerHTML = state.people.filter(p => p.name).map(p => `<option value="${esc(p.name)}">`).join('');
  if (state.view === 'people') renderPeopleCards();
}
async function showPeople() {
  leaveGrid('people');
  setLayout('people', true);
  $('#main').innerHTML = `<div class="ov-head"><h2>People <span class="muted" id="peopleCount"></span></h2>
      <span class="admin-only row"><a class="btnlink ghost small" href="#/faces">Faces nobody is assigned to</a>
        <label class="chk"><input type="checkbox" id="peopleHiddenChk" ${state.peopleHidden ? 'checked' : ''}> Hidden people</label></span>
    </div><div id="peopleGrid" class="card-grid"></div>
    <section class="admin-only" id="sugWrap" hidden><h3>Suggestions <span class="muted" id="sugCount"></span></h3>
      <p class="muted small">Faces that look like one person. Give them a name (an existing name adds them to that person), open one to
        check its faces first, or dismiss it.</p>
      <div id="sugGrid" class="card-grid"></div></section>`;
  $('#peopleHiddenChk')?.addEventListener('change', e => { state.peopleHidden = e.target.checked; loadPeople(); });
  renderPeopleCards();
  scrollTo(0, state.overviewScroll.people || 0);
  await loadPeople();
}
function renderPeopleCards() {
  const grid = $('#peopleGrid'); if (!grid) return;
  const size = cropSize(), admin = state.me?.role === 'admin';
  const named = state.people.filter(p => p.name), unnamed = state.people.filter(p => !p.name);
  $('#peopleCount').textContent = fmtNum(named.length);
  grid.innerHTML = named.map(p => cardHtml({
    href: `#/person/${p.id}`, src: p.coverFaceId ? cropUrl(p.coverFaceId, size) : null, name: p.name,
    meta: plural(p.itemCount, 'photo'), hidden: p.hidden, hiddenTitle: 'Hidden from the People view',
  })).join('') || `<div class="muted">${!admin ? 'No people yet.' : unnamed.length ? 'Nobody is named yet: name the suggestions below.'
    : 'No people yet. Faces are grouped into people once they have been found (Admin › Faces).'}</div>`;
  const sg = $('#sugGrid');
  if (sg && admin) {
    $('#sugWrap').hidden = !unnamed.length;
    $('#sugCount').textContent = fmtNum(unnamed.length);
    sg.innerHTML = unnamed.map(p => `<div class="card sug" data-id="${p.id}">
        <a class="cover" href="#/person/${p.id}" title="Check these faces">${p.coverFaceId ? `<img alt="" data-src="${cropUrl(p.coverFaceId, size)}">` : ''}</a>
        <div class="meta">${plural(p.faceCount, 'face')} in ${plural(p.itemCount, 'photo')}</div>
        <form class="name-form"><input placeholder="Name…" list="peopleNames" aria-label="Name" required><button class="small">OK</button></form>
        <button class="ghost small" data-dismiss title="Not one person, or nobody to name: these faces stay unnamed">Dismiss</button></div>`).join('');
    $$('.card.sug', sg).forEach(card => {
      const id = +card.dataset.id;
      $('form', card).onsubmit = async e => { e.preventDefault(); if (await namePerson(id, $('input', card).value)) loadPeople(); };
      $('[data-dismiss]', card).onclick = async () => { await api(`/api/people/${id}`, { method: 'DELETE' }); toast('Dismissed: these faces stay unnamed'); loadPeople(); };
    });
  }
  observeCovers($('#main'));
}
/** Names a person. When someone already has the name, offers to add the faces to them instead. Returns the person's id, or 0. */
async function namePerson(id, name) {
  name = name.trim(); if (!name) return 0;
  const r = await fetch(`/api/people/${id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) });
  if (r.status === 409) {
    const j = await r.json();
    if (!confirm(`Someone is already called ${name}. Add these faces to them?`)) return 0;
    await api(`/api/people/${id}/merge`, { method: 'POST', body: { into: j.existingId } });
    toast(`Added to ${name}`); return j.existingId;
  }
  if (!r.ok) { toast(errorText(await r.text())); return 0; }
  toast(`Named ${name}`); return id;
}
async function loadPersonDetail() {
  const id = state.filter.person; if (!id) return;
  try { state.personDetail = await api(`/api/people/${id}`); } catch { state.personDetail = { id: +id, name: null, missing: true, itemCount: 0 }; }
  if (state.view === 'grid' && state.filter.person === id) renderBanner();
}
/** Over a person's photos: who it is and (admins) naming, merging, hiding, their faces. */
function renderPersonBanner(b) {
  const p = state.personDetail;
  b.hidden = false;
  if (!p) { b.innerHTML = '<a class="back" href="#/people">‹ People</a><h2>…</h2>'; return; }
  if (p.missing) { b.innerHTML = '<a class="back" href="#/people">‹ People</a><h2>Person not found</h2>'; return; }
  b.innerHTML = `<a class="back" href="#/people">‹ People</a>
    <h2>${p.coverFaceId ? `<img class="face-mini" src="${cropUrl(p.coverFaceId, 96)}" alt="">` : ''}${esc(p.name || 'Unnamed')}
      <span class="muted" style="font-weight:normal;font-size:13px">${plural(p.itemCount, 'photo')}</span></h2>
    ${p.hidden ? '<span class="pill vhidden">Hidden from the People view</span>' : ''}
    <span class="admin-only row"><button class="ghost small" id="perName">${p.name ? 'Rename' : 'Name'}</button>
      <button class="ghost small" id="perMerge">Merge into…</button>
      <a class="btnlink ghost small" href="#/person/${p.id}/faces">Faces</a>
      <button class="ghost small" id="perHide">${p.hidden ? 'Show in People' : 'Hide'}</button>
      <button class="ghost small danger" id="perDelete">${p.name ? 'Delete' : 'Dismiss'}</button></span>`;
  $('#perName')?.addEventListener('click', async () => {
    const n = prompt(p.name ? 'Name' : 'Who is this?', p.name || ''); if (!n || n === p.name) return;
    const id = await namePerson(p.id, n); if (!id) return;
    await loadPeople();
    if (id !== p.id) location.hash = `#/person/${id}`; else loadPersonDetail();
  });
  $('#perMerge')?.addEventListener('click', () => mergeDialog(p));
  $('#perHide')?.addEventListener('click', async () => {
    await api(`/api/people/${p.id}`, { method: 'PUT', body: { hidden: !p.hidden } });
    toast(p.hidden ? 'Shown in People again' : 'Hidden from the People view (for viewers too)'); loadPersonDetail(); loadPeople();
  });
  $('#perDelete')?.addEventListener('click', async () => {
    if (!confirm(p.name ? `Delete ${p.name}? Their faces stay but become unnamed; photos are left alone.` : 'Dismiss this suggestion? Its faces stay unnamed.')) return;
    await api(`/api/people/${p.id}`, { method: 'DELETE' }); await loadPeople(); location.hash = '#/people';
  });
}
function mergeDialog(p) {
  modal(`<h2>Merge ${esc(p.name || 'this person')} into…</h2><form id="mergeForm" class="form">
    <label>Person <input name="name" list="peopleNames" required autocomplete="off"></label>
    <small class="muted">All faces of ${esc(p.name || 'this person')} move to them, and ${esc(p.name || 'this person')} is removed.</small>
    <div class="row"><button>Merge</button><button type="button" class="ghost" data-close>Cancel</button><span class="error" id="mergeErr"></span></div></form>`);
  $('#mergeForm').onsubmit = async e => {
    e.preventDefault();
    const name = e.target.name.value.trim();
    const into = state.people.find(x => x.name && x.name.toLowerCase() === name.toLowerCase());
    if (!into) { $('#mergeErr').textContent = 'Nobody has that name'; return; }
    if (into.id === p.id) { $('#mergeErr').textContent = 'That is the same person'; return; }
    await api(`/api/people/${p.id}/merge`, { method: 'POST', body: { into: into.id } });
    closeModal(); toast(`Merged into ${into.name}`); await loadPeople(); location.hash = `#/person/${into.id}`;
  };
}

// Admin pages of face pictures: a person's faces, or faces nobody is assigned to. Click to select; act on the selection.
const facePage = { faces: [], total: 0, sel: new Set(), load: null };
function faceTilesHtml(list, withSuggestion) {
  return list.map(x => {
    const f = x.face || x;
    // Who it looks most like, with their face: an unnamed group has nothing else to recognise it by.
    const cover = withSuggestion && x.personId ? state.people.find(p => p.id === x.personId)?.coverFaceId : null;
    const sug = withSuggestion && x.personId ? `<button class="small sugbtn" data-person="${x.personId}" title="Looks like ${esc(x.personName || 'an unnamed group')} (similarity ${x.similarity.toFixed(2)}): click to agree">${cover ? `<img class="sug-face" src="${cropUrl(cover, 96)}" alt="">` : ''}${esc(x.personName || 'Same as these')}?</button>` : '';
    return `<div class="face-tile${facePage.sel.has(f.id) ? ' sel' : ''}" data-id="${f.id}">
      <img alt="" data-src="${cropUrl(f.id, cropSize())}"><a class="open" href="/#/item/${f.itemId}" target="_blank" rel="noopener" title="Open the photo in a new tab">↗</a>${sug}</div>`;
  }).join('');
}
function facePageHtml(back, title, help) {
  return `<div class="ov-head">${back}<h2>${title} <span class="muted" id="facesCount"></span></h2></div>
    <p class="muted small">${help}</p>
    <div class="face-bar" id="faceBar" hidden><span id="faceSelCount"></span>
      <form id="fMove" class="name-form"><input placeholder="Who is this?" list="peopleNames" aria-label="Name" required><button class="small">Assign</button></form>
      <span id="faceBarExtra"></span><button class="ghost small" id="fClear">Clear</button></div>
    <div id="faceGrid" class="face-grid"></div><div class="row center"><button class="ghost" id="faceMore" hidden>Show more</button></div>`;
}
function bindFacePage(extra) {
  const grid = $('#faceGrid');
  grid.onclick = async e => {
    const tile = e.target.closest('.face-tile'); if (!tile || e.target.closest('a')) return;
    const id = +tile.dataset.id;
    const sug = e.target.closest('.sugbtn');
    if (sug) { await api('/api/faces/assign', { method: 'POST', body: { faceIds: [id], personId: +sug.dataset.person } }); removeFaces([id]); loadPeople(); return; }
    if (facePage.sel.has(id)) facePage.sel.delete(id); else facePage.sel.add(id);
    tile.classList.toggle('sel', facePage.sel.has(id)); updateFaceBar();
  };
  $('#fMove').onsubmit = async e => {
    e.preventDefault();
    const ids = [...facePage.sel], name = e.target.querySelector('input').value.trim(); if (!ids.length || !name) return;
    await api('/api/faces/assign', { method: 'POST', body: { faceIds: ids, name } });
    toast(`${plural(ids.length, 'face')} assigned to ${name}`); e.target.reset(); removeFaces(ids); loadPeople();
  };
  $('#fClear').onclick = () => { facePage.sel.clear(); $$('#faceGrid .face-tile.sel').forEach(t => t.classList.remove('sel')); updateFaceBar(); };
  $('#faceMore').onclick = () => facePage.load(facePage.faces.length);
  $('#faceBarExtra').innerHTML = extra || '';
}
function updateFaceBar() {
  const n = facePage.sel.size;
  $('#faceBar').hidden = !n;
  $('#faceSelCount').textContent = `${plural(n, 'face')} selected`;
}
function removeFaces(ids) {
  for (const id of ids) { facePage.sel.delete(id); $(`#faceGrid .face-tile[data-id="${id}"]`)?.remove(); }
  facePage.faces = facePage.faces.filter(x => !ids.includes((x.face || x).id));
  facePage.total = Math.max(0, facePage.total - ids.length);
  $('#facesCount').textContent = fmtNum(facePage.total);
  updateFaceBar();
}
function appendFaces(list, withSuggestion) {
  facePage.faces.push(...list);
  $('#faceGrid').insertAdjacentHTML('beforeend', faceTilesHtml(list, withSuggestion));
  $('#facesCount').textContent = fmtNum(facePage.total);
  $('#faceMore').hidden = facePage.faces.length >= facePage.total;
  observeCovers($('#faceGrid'));
  if (!facePage.total) $('#faceGrid').innerHTML = '<div class="muted">No faces here.</div>';
}
async function showPersonFaces(id) {
  leaveGrid('faces'); setLayout('people', true); closeModal();
  Object.assign(facePage, { faces: [], total: 0, sel: new Set() });
  let p; try { p = await api(`/api/people/${id}`); } catch { p = { id, name: null }; }
  $('#main').innerHTML = facePageHtml(`<a class="back" href="#/person/${id}">‹ Photos</a>`, `Faces of ${esc(p.name || 'this unnamed person')}`,
    'Click faces to select them, then assign them to someone else or remove them. Faces you correct stay where you put them.');
  bindFacePage(`<button class="ghost small" id="fNot">Not ${esc(p.name || 'this person')}</button><button class="ghost small" id="fCover">Use as cover</button>`);
  $('#fNot').onclick = async () => { const ids = [...facePage.sel]; await api('/api/faces/unassign', { method: 'POST', body: { faceIds: ids } }); toast(`${plural(ids.length, 'face')} removed`); removeFaces(ids); loadPeople(); };
  $('#fCover').onclick = async () => {
    const [face] = facePage.sel; if (facePage.sel.size !== 1) { toast('Select one face for the cover'); return; }
    await api(`/api/people/${id}`, { method: 'PUT', body: { coverFaceId: face } }); toast('Cover chosen'); loadPeople();
  };
  facePage.load = async offset => {
    const r = await api(`/api/people/${id}/faces?offset=${offset}&limit=200`);
    facePage.total = r.total; appendFaces(r.faces, false);
  };
  facePage.load(0);
}
async function showUnassigned() {
  leaveGrid('faces'); setLayout('people', true);
  Object.assign(facePage, { faces: [], total: 0, sel: new Set() });
  const all = state.unassignedAll;
  $('#main').innerHTML = facePageHtml('<a class="back" href="#/people">‹ People</a>', 'Faces nobody is assigned to',
    `Clearest first. A name under a face is who it looks most like: click it to agree. Or select faces and type a name.
     <label class="chk"><input type="checkbox" id="allFacesChk" ${all ? 'checked' : ''}> Include small and unsure faces</label>`);
  bindFacePage('');
  $('#allFacesChk').onchange = e => { state.unassignedAll = e.target.checked; showUnassigned(); };
  facePage.load = async offset => {
    const r = await api(`/api/faces/unassigned?offset=${offset}&limit=100${all ? '&all=true' : ''}`);
    facePage.total = r.total; appendFaces(r.faces, true);
  };
  facePage.load(0);
}

// In the viewer: the people in the photo (info panel) and boxes over their faces (f). Admins name or correct a face by
// clicking its box.
async function loadItemFaces(itemId) {
  let faces; try { faces = await api(`/api/items/${itemId}/faces`, { quiet: true }); } catch { faces = []; }
  if (state.lb.detail?.item.id !== itemId) return;
  state.lb.faces = faces;
  $('#lbFaces').classList.toggle('on', !!state.lb.showFaces);
  renderPanel(); renderFaceLayer();
}
function peopleSectionHtml() {
  const faces = state.lb.faces; if (!faces) return '';
  const seen = new Map(); let unnamed = 0;
  for (const f of faces) { if (f.personId && f.personName) seen.set(f.personId, f.personName); else if (f.personId) seen.set(f.personId, null); else unnamed++; }
  const chips = [...seen].map(([id, name]) => `<a class="tag" href="#/person/${id}">${esc(name || 'Unnamed')}</a>`).join('');
  return `<h4>People ${faces.length && !state.lb.sphere ? `<button class="ghost small" id="pFaces">${state.lb.showFaces ? 'Hide faces' : 'Show faces'}</button>` : ''}</h4>
    <div class="tags">${chips}${unnamed ? `<span class="muted small">${chips ? '+' : ''}${plural(unnamed, 'face')} nobody is assigned to</span>` : ''}${!faces.length ? '<span class="muted">no faces found</span>' : ''}</div>`;
}
function bindPeopleSection() { $('#pFaces')?.addEventListener('click', toggleFaces); }
function toggleFaces() {
  if (!state.lb.open || $('#lbFaces').hidden) return;
  state.lb.showFaces = !state.lb.showFaces;
  $('#lbFaces').classList.toggle('on', state.lb.showFaces);
  renderFaceLayer(); renderPanel();
}
function renderFaceLayer() {
  $('#lbFaceLayer')?.remove(); closeFacePop();
  const img = $('#lbStage img.main'), faces = state.lb.faces;
  if (!state.lb.showFaces || !img || !faces?.length || state.lb.viewRot || !img.naturalWidth) return;
  const stage = $('#lbStage'), r = img.getBoundingClientRect(), s = stage.getBoundingClientRect();
  const layer = document.createElement('div');
  layer.id = 'lbFaceLayer'; layer.className = 'face-layer';
  Object.assign(layer.style, { left: `${r.left - s.left + stage.scrollLeft}px`, top: `${r.top - s.top + stage.scrollTop}px`, width: `${r.width}px`, height: `${r.height}px` });
  layer.innerHTML = faces.map(f => `<div class="face-box${f.personId ? '' : ' unknown'}" data-face="${f.id}" title="${esc(f.personName || (f.personId ? 'Unnamed person' : 'Nobody yet'))}"
    style="left:${f.x1 * 100}%;top:${f.y1 * 100}%;width:${(f.x2 - f.x1) * 100}%;height:${(f.y2 - f.y1) * 100}%">${f.personName ? `<span>${esc(f.personName)}</span>` : ''}</div>`).join('');
  stage.appendChild(layer);
  $$('.face-box', layer).forEach(el => el.onclick = e => {
    e.stopPropagation();
    const f = faces.find(x => x.id === +el.dataset.face);
    if (state.me?.role === 'admin') facePopover(f, el);
    else if (f.personId) { closeLightbox(); location.hash = `#/person/${f.personId}`; }
  });
}
function closeFacePop() { $('#facePop')?.remove(); }
function facePopover(f, box) {
  closeFacePop();
  const pop = document.createElement('form');
  pop.id = 'facePop'; pop.className = 'face-pop';
  pop.innerHTML = `<div class="who">${esc(f.personName || (f.personId ? 'Unnamed person' : 'Nobody yet'))}</div>
    <input placeholder="${f.personId ? 'Someone else…' : 'Who is this?'}" list="peopleNames" aria-label="Name" required autocomplete="off">
    <div class="row"><button class="small">${f.personId ? 'Move' : 'Name'}</button>
      ${f.personId ? `<button type="button" class="ghost small" data-not>Not ${esc(f.personName || 'this person')}</button><a class="btnlink ghost small" href="#/person/${f.personId}">Open</a>` : ''}</div>`;
  const stage = $('#lbStage'), b = box.getBoundingClientRect(), s = stage.getBoundingClientRect();
  pop.style.left = `${clamp(b.left - s.left + b.width / 2 - 120, 8, s.width - 248)}px`;
  pop.style.top = `${clamp(b.bottom - s.top + 8, 8, s.height - 130)}px`;
  pop.addEventListener('click', e => e.stopPropagation());
  pop.onsubmit = async e => {
    e.preventDefault();
    const name = $('input', pop).value.trim(); if (!name) return;
    await api('/api/faces/assign', { method: 'POST', body: { faceIds: [f.id], name } });
    toast(f.personId ? `Moved to ${name}` : `Named ${name}`); await loadPeople(); loadItemFaces(state.lb.detail.item.id);
  };
  $('[data-not]', pop)?.addEventListener('click', async () => {
    await api('/api/faces/unassign', { method: 'POST', body: { faceIds: [f.id] } });
    toast(`Not ${f.personName || 'this person'}`); await loadPeople(); loadItemFaces(state.lb.detail.item.id);
  });
  stage.appendChild(pop);
  $('input', pop).focus();
}
async function loadTagKeys() {
  const ks = await api('/api/tags/keys', { quiet: true });
  $('#tagKeySel').innerHTML = '<option value="">Any tag</option>' + ks.map(k => `<option value="${esc(k.key)}">${esc(k.key)} (${k.count})</option>`).join('');
  $('#tagKeySel').value = state.filter.tagKey;
}
/** Over an album's or a year's items: where they come from, a way back to the overview, and (admins) the album's controls. */
function renderBanner() {
  const b = $('#albumBanner'); if (!b) return;
  if (state.filter.person) { renderPersonBanner(b); return; }
  const year = state.filter.year && state.years.find(y => String(y.year) === state.filter.year);
  const a = state.filter.album && state.albums.find(x => String(x.id) === state.filter.album);
  b.hidden = !a && !state.filter.year;
  if (state.filter.year) {
    b.innerHTML = `<a class="back" href="#/years">‹ Years</a><h2>${esc(state.filter.year)} <span class="muted" style="font-weight:normal;font-size:13px">${year ? fmtNum(year.count) + ' items' : ''}</span></h2>`;
    return;
  }
  if (!a) return;
  const hidden = !!a.viewerHidden;
  b.innerHTML = `<a class="back" href="#/albums">‹ Albums</a>
    <h2>${esc(a.name)} <span class="muted" style="font-weight:normal;font-size:13px">${a.kind === 'auto' ? 'from a folder · ' : ''}${albumDates(a)}</span></h2>
    ${hidden ? `<span class="pill vhidden" title="Viewers can't see the album or anything in it">${EYE_OFF} Hidden from viewers</span>` : ''}
    <span class="admin-only"><button class="ghost small" id="albHide">${hidden ? 'Show to viewers' : 'Hide from viewers'}</button>
      <button class="ghost small" id="albRename">Rename</button><button class="ghost small danger" id="albDelete">Delete album</button></span>`;
  $('#albHide')?.addEventListener('click', async () => {
    if (!hidden && !confirm(`Hide "${a.name}" from viewers? Everything in it is hidden from them too, wherever it appears. Admins still see it all.`)) return;
    await api(`/api/albums/${a.id}/hidden`, { method: 'PUT', body: { hidden: !hidden } });
    toast(hidden ? 'Viewers can see the album again' : 'Hidden from viewers, with everything in it');
    await loadAlbums(); reload({ keepScroll: true }); loadYears();
  });
  $('#albRename')?.addEventListener('click', async () => { const n = prompt('Album name', a.name); if (n && n !== a.name) { await api(`/api/albums/${a.id}`, { method: 'PUT', body: { name: n } }); await loadAlbums(); } });
  $('#albDelete')?.addEventListener('click', async () => { if (confirm(`Delete album "${a.name}"? Your files stay where they are.`)) { await api(`/api/albums/${a.id}`, { method: 'DELETE' }); location.hash = '#/albums'; await loadAlbums(); } });
}
async function newAlbum() { const n = prompt('New album name'); if (n) { const a = await api('/api/albums', { method: 'POST', body: { name: n } }); await loadAlbums(); location.hash = `#/album/${a.id}`; } }
async function autoAlbums() {
  if (!confirm('Create albums from folder names? Existing auto-albums are updated; albums you made by hand are left alone.')) return;
  const r = await api('/api/albums/auto', { method: 'POST', body: { gapDays: 3 } });
  toast(`Auto-albums: ${r.created} created, ${r.updated} updated, ${r.itemsAssigned} items assigned${leftOut(r)}`); await loadAlbums();
  showUnplaced(r);
}
/** The toast's note about items no folder album took. */
const leftOut = r => r.unplaced ? ` · ${fmtNum(r.unplaced)} left out (see Admin › Albums)` : '';
/** Admin › Albums: the items building albums from folders left out, and their folders. */
function showUnplaced(r) {
  const el = $('#albumUnplaced'); if (!el) return;
  if (!r.unplaced) { el.innerHTML = ''; return; }
  const one = r.unplaced === 1, shown = r.unplacedFolders.reduce((n, f) => n + f.items, 0);
  el.innerHTML = `<p>${fmtNum(r.unplaced)} item${one ? ' is' : 's are'} left out of the folder albums: ${one ? 'its folder has' : 'their folders have'}
    a generic name (DCIM, Camera, RAW, JPG, Photos, a year…) and none of the folders above ${one ? 'it' : 'them'} in the source has a proper name.
    Give one of the folders a name, or move the photos into a named folder, and build the albums again.</p>
    <ul>${r.unplacedFolders.map(f => `<li>${esc(f.folder)} (${fmtNum(f.items)})</li>`).join('')}${r.unplaced > shown ? '<li>…</li>' : ''}</ul>`;
}
$('#menuBtn').onclick = () => { document.body.classList.toggle('side-closed'); document.body.classList.toggle('side-open'); };

// top bar bindings
let searchT;
$('#search').addEventListener('input', e => {
  clearTimeout(searchT);
  // On the Albums overview it finds albums by name, at once; elsewhere it searches the items.
  if (state.view === 'albums') { state.albumQ = e.target.value.trim(); renderAlbumCards(); return; }
  searchT = setTimeout(() => { state.filter.q = e.target.value.trim(); if (state.view === 'grid') reload(); }, 250);
});
$$('#kindSeg button').forEach(b => b.onclick = () => { $$('#kindSeg button').forEach(x => x.classList.remove('on')); b.classList.add('on'); state.filter.kind = b.dataset.kind; reload(); });
$('#tagKeySel').onchange = async e => {
  state.filter.tagKey = e.target.value; state.filter.tagVal = '';
  const vs = $('#tagValSel'); vs.hidden = !state.filter.tagKey;
  if (state.filter.tagKey) { const vals = await api(`/api/tags/values?key=${encodeURIComponent(state.filter.tagKey)}`); vs.innerHTML = '<option value="">Any value</option>' + vals.map(v => `<option value="${esc(v.value)}">${esc(v.value)} (${v.count})</option>`).join(''); }
  reload();
};
$('#tagValSel').onchange = e => { state.filter.tagVal = e.target.value; reload(); };
$('#sortSel').onchange = e => { state.filter.sort = e.target.value; reload(); };
$('#dupsChk').onchange = e => { state.filter.dups = e.target.checked; reload(); };
$('#varChk').onchange = e => { state.filter.variations = e.target.checked; reload(); };
const sizeRange = $('#sizeRange');
sizeRange.value = localStorage.getItem('thumb') || 180;
document.documentElement.style.setProperty('--thumb', sizeRange.value + 'px');
function setThumb(px) { sizeRange.value = px; document.documentElement.style.setProperty('--thumb', px + 'px'); localStorage.setItem('thumb', px); }
sizeRange.oninput = e => { setThumb(e.target.value); if (state.view === 'grid') relayout(); };
// Covers fetched for the card size: a larger size once the cards grow past it.
sizeRange.onchange = () => { if (state.view === 'albums') renderAlbumCards(); else if (state.view === 'years') renderYearCards(); };

// ---------- lightbox ----------
const lb = $('#lightbox');
/** Puts #/item/ID in the address bar: a new history entry when the viewer opens, a replacement while stepping. */
function setViewerUrl(id, fromHistory) {
  const url = `#/item/${id}`;
  if (!fromHistory) {
    if (state.lb.hasEntry) history.replaceState({ viewer: true }, '', url);
    else history.pushState({ viewer: true }, '', url);
  }
  state.lb.hasEntry = true; // the entry below this one is the gallery
}
function showLightboxChrome() {
  // A photo opens on its own; the info panel (and its full metadata list) only when asked for. Stepping through
  // photos keeps the panel as it is.
  if (!state.lb.open) { state.lb.panel = false; state.lb.metaOpen = false; }
  state.lb.open = true; lb.hidden = false;
  lb.classList.toggle('panel-open', state.lb.panel); $('#lbPanel').hidden = !state.lb.panel;
}
/** Shows or hides the viewer's info panel (the i button and key, and the panel's own ×). */
function setPanel(on) {
  state.lb.panel = on;
  lb.classList.toggle('panel-open', on); $('#lbPanel').hidden = !on;
  if (on) renderPanel();
}
async function openLightbox(index, { fromHistory = false } = {}) {
  const it = itemAt(index) ?? await ensureItem(index); if (!it) return;
  state.lb.index = index;
  setViewerUrl(it.id, fromHistory);
  showLightboxChrome();
  for (const p of new Set([Math.floor((index - 5) / PAGE), Math.floor((index + 10) / PAGE)])) if (p >= 0) loadPage(p); // the next steps
  await showItem(it.id);
}
async function openItemById(id, opts = {}) {
  const idx = indexOfId(id);
  if (idx >= 0) return openLightbox(idx, opts);
  state.lb.index = -1; // not in the loaded part of the grid (yet): no prev/next until it is
  setViewerUrl(id, opts.fromHistory);
  showLightboxChrome();
  await showItem(id);
}
async function showItem(id, assetId = null) {
  let d; try { d = await api(`/api/items/${id}`); } catch (e) { toast(e.message); return; }
  state.lb.detail = d;
  const asset = d.assets.find(a => a.id === assetId) || d.assets.find(a => a.id === d.item.primaryAssetId) || d.assets[0];
  state.lb.assetId = asset?.id;
  pano?.hide();
  const stage = $('#lbStage'); stage.innerHTML = '';
  // Opening an item re-checks its files on the server; if they are all gone it has just left the gallery.
  const deleted = d.assets.length > 0 && d.assets.every(a => a.missing);
  if (deleted) { vg.gone.add(id); $(`.cell[data-id="${id}"]`)?.classList.add('gone'); }
  state.lb.faces = null;
  state.lb.sphere = !deleted && showsSphere(asset, id);
  viewerTools(asset, deleted);
  // Photos come from the server already turned by the item's saved rotation; videos are turned in the browser.
  state.lb.viewRot = asset?.kind === 'video' ? (d.item.rotation || 0) : 0;
  if (!asset) { stage.innerHTML = '<p class="muted">No files</p>'; }
  else if (deleted) {
    stage.innerHTML = `<div class="gone-msg"><h3>File deleted</h3><p class="muted">${d.assets.length > 1 ? 'These files are' : 'This file is'} no longer on disk, so the item has been removed from the gallery.</p></div>`;
  }
  else if (asset.kind === 'image') showImage(asset);
  else {
    const info = await api(`/api/assets/${asset.id}/playinfo`);
    if (asset.kind === 'video') {
      const v = document.createElement('video'); v.controls = true; v.autoplay = true; v.playsInline = true;
      const live = () => stage.insertAdjacentHTML('beforeend', '<span class="live">live transcoding · seeking limited until the saved copy is ready</span>');
      // A video sent as it is that this browser can't decode (H.265 without a hardware decoder, 10-bit H.264…) is
      // converted on the server instead: on an error, or when only the sound plays.
      let converting = false;
      const convert = () => {
        if (converting || !info.fallback || !v.isConnected) return;
        converting = true; v.src = info.fallback;
        if (!info.persisted) live();
      };
      if (info.fallback) {
        v.addEventListener('error', convert);
        v.addEventListener('loadedmetadata', () => { if (!converting && !v.videoWidth) convert(); });
      }
      v.addEventListener('loadedmetadata', applyViewRotation);
      stage.appendChild(v);
      const hevc = asset.codec === 'hevc';
      if (info.fallback && hevc && !v.canPlayType('video/mp4; codecs="hvc1.1.6.L93.B0"')) convert(); // it can't decode this: go straight to the converted copy
      else v.src = info.url;
      if (!info.direct && !info.persisted) live();
    } else {
      const wrap = document.createElement('div'); wrap.className = 'audio';
      wrap.innerHTML = `<img src="${thumbUrl(asset.id, asset.hash, d.item.rotation, 1024, d.item.squeeze)}" onerror="this.src='/img/audio.svg'" alt=""><div>${esc(asset.fileName)}</div>`;
      const a = document.createElement('audio'); a.controls = true; a.autoplay = true; a.src = info.url; wrap.appendChild(a); stage.appendChild(wrap);
    }
  }
  renderPanel();
  if (asset?.kind === 'image' && !deleted) loadItemFaces(id);
}
/** The viewer's buttons for the file on screen. */
function viewerTools(asset, deleted) {
  const d = state.lb.detail, sphere = state.lb.sphere;
  // Edit opens the item's RAW when it has one (the most to work with), otherwise the photo on screen.
  const editable = state.me?.role === 'admin' && asset?.kind === 'image' && !deleted;
  state.lb.editAsset = editable ? (d.assets.find(a => a.isRaw && !a.missing) || asset).id : null;
  $('#lbEdit').hidden = !editable;
  // Face boxes and quarter turns belong to the flat picture.
  $('#lbFaces').hidden = asset?.kind !== 'image' || deleted || sphere;
  $('#lbRotL').hidden = $('#lbRotR').hidden = !asset || asset.kind === 'audio' || deleted || sphere;
  const saves = state.me?.role === 'admin' ? ' · saved for everyone' : ' · this view only';
  $('#lbRotL').title = `Rotate counter-clockwise ([)${saves}`; $('#lbRotR').title = `Rotate clockwise (])${saves}`;
  const pb = $('#lbPano');
  pb.hidden = deleted || !viewable360(asset) || !panorama();
  pb.classList.toggle('on', sphere);
  pb.title = sphere ? 'Show flat, the whole picture at once (v)' : 'Show as 360°, to look around in (v)';
  $('#lbMotion').hidden = !sphere || !Panorama.motionPossible();
  $('#lbZoom').hidden = asset?.kind !== 'image' || deleted || sphere;
  $('#lbZoom').value = String(viewZoom);
  $('#lbMotion').classList.toggle('on', sphere && !!pano?.motionOn);
}
/** A photo on the stage: a 360° view when state.lb.sphere, otherwise the picture as it is. */
function showImage(asset) {
  const d = state.lb.detail, stage = $('#lbStage');
  pano?.hide(); stage.innerHTML = ''; stage.classList.remove('zoomed'); closeFacePop();
  if (state.lb.sphere) {
    // The 2048 preview at once (often cached already), then a sharper one: the view shows a small part of the sphere.
    const sharper = pano.sharperSize(asset.width);
    // Two fisheye circles come with their lenses, and the distances along the seam follow (s3: SeamService.Version).
    const circles = asset.projection === 'dual-fisheye';
    pano.show(stage, thumbUrl(asset.id, asset.hash, d.item.rotation, 2048, d.item.squeeze), sharper && thumbUrl(asset.id, asset.hash, d.item.rotation, sharper, d.item.squeeze),
      circles ? asset.lenses : null, circles ? `/api/assets/${asset.id}/seam?v=${asset.hash}.s3` : null);
    return;
  }
  const img = document.createElement('img');
  img.className = 'main';
  img.onload = () => applyZoom();
  img.src = thumbUrl(asset.id, asset.hash, d.item.rotation, previewSize(), d.item.squeeze);
  img.alt = asset.fileName; stage.appendChild(img);
}

// ---------- zoom ----------
// A photo is shown fitted to the window, or at 50–200% of its own pixels (100%: one photo pixel per screen pixel, as
// in the editor). The level is remembered in this browser, so photos open at it, and it stays while stepping through
// them. Zoomed, the stage scrolls: drag, scroll or swipe to move about; double-click goes to 100% where clicked and back;
// ⌘/Ctrl + scroll (a trackpad pinch) steps the level. Where the level shows more detail than the preview on screen has,
// a larger one (4096 or 8192, never larger than the photo) replaces it.
const ZOOMS = [0.5, 0.75, 1, 1.5, 2];
let viewZoom = (() => { try { const z = +localStorage.getItem('gallery.viewerZoom'); return ZOOMS.includes(z) ? z : 'fit'; } catch { return 'fit'; } })();

/** The photo's own size in pixels, in the shape of the preview on screen (turned and stretched as shown). */
function fullSize(img) {
  const d = state.lb.detail, a = d?.assets.find(x => x.id === state.lb.assetId);
  const nw = img.naturalWidth, nh = img.naturalHeight;
  if (!nw || !nh) return null;
  if (!a?.width || !a?.height) return { w: nw, h: nh };
  const f = Math.max(a.width, a.height) * (d.item.squeeze || 1) / Math.max(nw, nh);
  return { w: nw * f, h: nh * f };
}
/** Where on the picture the stage's centre is, as fractions (for keeping it there across a change of level). */
function zoomCentre() {
  const stage = $('#lbStage'), img = $('#lbStage img.main');
  const r = img.getBoundingClientRect(), s = stage.getBoundingClientRect();
  if (!r.width || !r.height) return { x: .5, y: .5, sx: .5, sy: .5 };
  return { x: clamp((s.left + s.width / 2 - r.left) / r.width, 0, 1), y: clamp((s.top + s.height / 2 - r.top) / r.height, 0, 1), sx: .5, sy: .5 };
}
/** Sets the level ('fit' or a factor), keeping the point at <at> (fractions of the picture and of the stage) in place. */
function setZoom(z, at) {
  const img = $('#lbStage img.main');
  const keep = at ?? (img && $('#lbStage').classList.contains('zoomed') ? zoomCentre() : null);
  viewZoom = z;
  try { localStorage.setItem('gallery.viewerZoom', String(z)); } catch { }
  $('#lbZoom').value = String(z);
  applyZoom(keep);
}
function zoomStep(dir) {
  const levels = ['fit', ...ZOOMS], i = levels.indexOf(viewZoom);
  setZoom(levels[clamp(i + dir, 0, levels.length - 1)]);
}
/** Lays the photo out at the level; <at> as for setZoom (default: the centre). */
function applyZoom(at) {
  const stage = $('#lbStage'), img = $('#lbStage img.main');
  if (!img || state.lb.sphere) return;
  const full = fullSize(img);
  if (viewZoom === 'fit' || !full) {
    const wrap = $('.zoom-wrap', stage);
    if (wrap) { stage.appendChild(img); wrap.remove(); }
    stage.classList.remove('zoomed');
    img.classList.remove('pixels');
    for (const p of ['position', 'left', 'top', 'width', 'height', 'maxWidth', 'maxHeight', 'transform']) img.style[p] = '';
    applyViewRotation(); // the fitted picture, turned in the browser if need be
    showFitLevel(img, full);
    return;
  }
  let box = img.parentElement.classList.contains('zoom-box') ? img.parentElement : null;
  if (!box) {
    const wrap = document.createElement('div'); wrap.className = 'zoom-wrap';
    box = document.createElement('div'); box.className = 'zoom-box';
    wrap.appendChild(box); stage.insertBefore(wrap, img); box.appendChild(img);
  }
  const dpr = devicePixelRatio || 1, w = full.w * viewZoom / dpr, h = full.h * viewZoom / dpr;
  const rot = ((state.lb.viewRot % 360) + 360) % 360, sideways = rot % 180 === 90; // a turn in the browser only (videos, viewers)
  const bw = sideways ? h : w, bh = sideways ? w : h;
  Object.assign(box.style, { width: `${bw}px`, height: `${bh}px` });
  Object.assign(img.style, { position: 'absolute', left: `${(bw - w) / 2}px`, top: `${(bh - h) / 2}px`, width: `${w}px`, height: `${h}px`,
    maxWidth: 'none', maxHeight: 'none', transform: rot ? `rotate(${rot}deg)` : '' });
  img.classList.toggle('pixels', viewZoom >= 2);
  stage.classList.add('zoomed');
  const keep = at ?? { x: .5, y: .5, sx: .5, sy: .5 };
  const br = box.getBoundingClientRect(), sr = stage.getBoundingClientRect();
  stage.scrollLeft += br.left + keep.x * br.width - (sr.left + keep.sx * sr.width);
  stage.scrollTop += br.top + keep.y * br.height - (sr.top + keep.sy * sr.height);
  sharperForZoom(img, full);
  renderFaceLayer();
}
/** The fit level's own percentage in the menu ("Fit · 23%"). */
function showFitLevel(img, full) {
  const opt = $('#lbZoom option[value="fit"]'); if (!opt) return;
  const r = img.getBoundingClientRect();
  const pct = full && r.width ? Math.round(Math.max(r.width, r.height) * (devicePixelRatio || 1) / Math.max(full.w, full.h) * 100) : 0;
  opt.textContent = pct ? `Fit · ${pct}%` : 'Fit';
}
/** Swaps in a larger preview when the level shows more detail than the one on screen has. */
function sharperForZoom(img, full) {
  const need = Math.max(full.w, full.h) * Math.min(viewZoom, 1); // beyond 100% the photo's own pixels are enlarged
  if (need <= Math.max(img.naturalWidth, img.naturalHeight) * 1.05) return;
  const d = state.lb.detail, a = d.assets.find(x => x.id === state.lb.assetId);
  const size = need <= 2048 ? 2048 : need <= 4096 ? 4096 : 8192;
  const url = thumbUrl(a.id, a.hash, d.item.rotation, size, d.item.squeeze);
  if (img.dataset.sharper === url) return;
  img.dataset.sharper = url;
  const pre = new Image();
  pre.onload = () => { if ($('#lbStage img.main') === img) img.src = url; }; // same shape: the layout stays as it is
  pre.src = url;
}
$('#lbZoom').onchange = e => { setZoom(e.target.value === 'fit' ? 'fit' : +e.target.value); e.target.blur(); };
// Double-click: 100% where clicked, and back to fitted.
$('#lbStage').addEventListener('dblclick', e => {
  const img = $('#lbStage img.main');
  if (!img || state.lb.sphere || !e.target.closest('img.main')) return;
  if (viewZoom !== 'fit') { setZoom('fit'); return; }
  const r = img.getBoundingClientRect(), s = $('#lbStage').getBoundingClientRect();
  setZoom(1, { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height, sx: (e.clientX - s.left) / s.width, sy: (e.clientY - s.top) / s.height });
});
// ⌘/Ctrl + scroll (and a trackpad pinch, which browsers send as it): a level in or out, about the pointer.
$('#lbStage').addEventListener('wheel', e => {
  const img = $('#lbStage img.main');
  if (!(e.ctrlKey || e.metaKey) || !img || state.lb.sphere) return;
  e.preventDefault();
  const levels = ['fit', ...ZOOMS], i = levels.indexOf(viewZoom), next = levels[clamp(i + (e.deltaY < 0 ? 1 : -1), 0, levels.length - 1)];
  if (next === viewZoom) return;
  const r = img.getBoundingClientRect(), s = $('#lbStage').getBoundingClientRect();
  setZoom(next, { x: clamp((e.clientX - r.left) / r.width, 0, 1), y: clamp((e.clientY - r.top) / r.height, 0, 1), sx: (e.clientX - s.left) / s.width, sy: (e.clientY - s.top) / s.height });
}, { passive: false });
// Dragging with the mouse moves about a zoomed photo (touch and trackpads scroll it themselves).
let zoomDrag = null;
$('#lbStage').addEventListener('pointerdown', e => {
  const stage = $('#lbStage');
  if (!stage.classList.contains('zoomed') || e.pointerType !== 'mouse' || e.button !== 0 || e.target.closest('.face-box, .face-pop')) return;
  zoomDrag = { x: e.clientX, y: e.clientY, left: stage.scrollLeft, top: stage.scrollTop, moved: false };
  stage.setPointerCapture(e.pointerId);
});
$('#lbStage').addEventListener('pointermove', e => {
  if (!zoomDrag) return;
  const stage = $('#lbStage'), dx = e.clientX - zoomDrag.x, dy = e.clientY - zoomDrag.y;
  if (!zoomDrag.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
  zoomDrag.moved = true; stage.classList.add('dragging');
  stage.scrollLeft = zoomDrag.left - dx; stage.scrollTop = zoomDrag.top - dy;
});
const endZoomDrag = () => { if (zoomDrag?.moved) setTimeout(() => { zoomDrag = null; }, 0); else zoomDrag = null; $('#lbStage').classList.remove('dragging'); };
$('#lbStage').addEventListener('pointerup', endZoomDrag);
$('#lbStage').addEventListener('pointercancel', endZoomDrag);
// ---------- 360° photos (panorama.js) ----------
let pano; // the 360° viewer: made the first time one is shown, one WebGL context for the page; null without WebGL
function panorama() { if (pano === undefined) pano = Panorama.create(); return pano; }
/** A stitched 360° photo, or two fisheye circles the server sent the lens calibration for. */
const viewable360 = asset => asset?.kind === 'image' && (asset.projection === 'equirectangular' || (asset.projection === 'dual-fisheye' && !!asset.lenses));
const showsSphere = (asset, itemId) => viewable360(asset) && state.lb.flatItem !== itemId && !!panorama();
/** The 360°/flat switch: for this item, until the viewer moves on to another. */
function toggleSphere() {
  const d = state.lb.detail, asset = d?.assets.find(a => a.id === state.lb.assetId);
  if (!state.lb.open || !asset || $('#lbPano').hidden) return;
  state.lb.flatItem = state.lb.sphere ? d.item.id : null;
  state.lb.sphere = showsSphere(asset, d.item.id);
  state.lb.viewRot = 0;
  showImage(asset);
  viewerTools(asset, false);
  renderPanel();
}
$('#lbPano').onclick = () => toggleSphere();
$('#lbMotion').onclick = async () => {
  if (!pano?.shown) return;
  const want = !pano.motionOn;
  const on = await pano.setMotion(want); // straight from the tap: iOS asks for permission only then
  $('#lbMotion').classList.toggle('on', on);
  if (want && !on) toast('Device motion is unavailable (no sensor, or permission denied)');
};
function renderPanel() {
  const d = state.lb.detail; if (!d) return;
  const it = d.item, cur = d.assets.find(a => a.id === state.lb.assetId) || {};
  const isAdmin = state.me?.role === 'admin';
  $('#lbPanel').innerHTML = `
    <div class="lb-panel-head"><h3>${esc(it.title)}</h3>
      <button class="icon" id="lbPanelClose" title="Close the details (i)" aria-label="Close the details">&times;</button></div>
    <dl>
      <dt>Taken</dt><dd>${fmtDate(cur.takenAt || it.takenAt)}${cur.takenSource === 'filename' ? ' <span class="muted">· from file name</span>' : cur.takenSource === 'file' ? ' <span class="muted" title="No date in the file\'s metadata or name; this is the file\'s own timestamp">· file date</span>' : ''}</dd>
      <dt>Folder</dt><dd>${esc(it.dirName)}</dd>
      ${cur.width ? `<dt>Size</dt><dd>${cur.width} × ${cur.height}${cur.durationSec ? ' · ' + fmtDur(cur.durationSec) : ''}</dd>` : cur.durationSec ? `<dt>Length</dt><dd>${fmtDur(cur.durationSec)}</dd>` : ''}
      ${projectionHtml(d, cur, isAdmin)}
      ${squeezeHtml(it, cur, isAdmin)}
      ${cur.codec ? `<dt>Codec</dt><dd>${esc(cur.codec)}${cur.audioCodec ? ' / ' + esc(cur.audioCodec) : ''}</dd>` : ''}
      <dt>File</dt><dd>${fmtBytes(cur.size || 0)} · ${esc((cur.ext || '').toUpperCase())}</dd>
    </dl>
    ${isAdmin ? coverHtml(d) : ''}
    ${isAdmin ? `<div class="vis">${it.hiddenFromViewers
      ? `<span class="pill vhidden">${EYE_OFF} Hidden from viewers</span><button class="ghost small" data-hide="0">Show to viewers</button>`
      : `<button class="ghost small" data-hide="1" title="Viewers can't see it; admins still can">Hide from viewers</button>`}
      ${it.inHiddenAlbum ? '<span class="muted small">It is in a hidden album, so viewers can\'t see it either way.</span>' : ''}</div>` : ''}
    <div id="pMeta"></div>
    <h4>Tags ${isAdmin ? '<button class="ghost small" id="pTag">+ Add</button>' : ''}</h4>
    <div class="tags">${d.tags.map(t => `<span class="tag" data-k="${esc(t.key)}" data-v="${esc(t.value)}" title="Filter by this tag"><b>${esc(t.key)}</b> ${esc(t.value)} ${isAdmin ? '<span class="x" title="Remove">×</span>' : ''}</span>`).join('') || '<span class="muted">none</span>'}</div>
    <h4>Albums ${isAdmin ? '<button class="ghost small" id="pAlbum">+ Add</button>' : ''}</h4>
    <div class="tags">${d.albums.map(a => `<a class="tag" href="#/album/${a.id}"${a.viewerHidden ? ' title="Hidden from viewers"' : ''}>${a.viewerHidden ? EYE_OFF + ' ' : ''}${esc(a.name)}</a>`).join('') || '<span class="muted">none</span>'}</div>
    <h4>Variations (${d.assets.length})</h4>
    ${d.assets.map(a => `<div class="var ${a.id === cur.id ? 'cur' : ''}">
      <div class="fn">${esc(a.fileName)}${a.isRaw ? '<span class="pill raw">RAW</span>' : ''}${a.identical ? '<span class="pill dup" title="Byte-identical copy of another file in this item">identical</span>' : ''}${a.missing ? '<span class="pill missing">missing</span>' : ''}${a.editOf ? '<span class="pill" title="Exported from the photo editor">edited</span>' : ''}${a.id === it.primaryAssetId ? '<span class="pill">primary</span>' : ''}</div>
      <div class="meta">${a.width ? a.width + '×' + a.height + ' · ' : ''}${fmtBytes(a.size)}${a.takenAt ? ' · ' + fmtDate(a.takenAt) : ''}</div>
      <div class="path">${esc(a.path)}</div>
      <div class="acts">
        ${a.id !== cur.id && !a.missing ? `<button class="ghost small" data-show="${a.id}">Show</button>` : ''}
        ${a.missing ? '' : `<a class="ghost small" href="/api/assets/${a.id}/file" target="_blank" rel="noopener" title="Open the original file in a new tab (one the browser can't show is downloaded)" style="padding:2px 8px;background:var(--bg2);border-radius:6px">Open</a>`}
        ${a.missing ? '' : `<a class="ghost small" href="/api/assets/${a.id}/file?download=true" download style="padding:2px 8px;background:var(--bg2);border-radius:6px">Download</a>`}
        ${isAdmin && a.id !== it.primaryAssetId && !a.missing ? `<button class="ghost small" data-primary="${a.id}">Make primary</button>` : ''}
        ${isAdmin && a.editOf ? `<button class="ghost small danger" data-delete-edit="${a.id}" title="Delete this copy made in the photo editor; export again for a new one">Delete</button>` : ''}
      </div></div>`).join('')}`;
  $('#pMeta').insertAdjacentHTML('beforebegin', peopleSectionHtml());
  bindPeopleSection();
  if (state.lb.panel && cur.id) loadMeta(cur);
  $('#lbPanelClose').onclick = () => setPanel(false);
  $$('#lbPanel [data-show]').forEach(b => b.onclick = () => showItem(it.id, +b.dataset.show));
  $$('#lbPanel [data-primary]').forEach(b => b.onclick = async () => { await api(`/api/items/${it.id}/primary`, { method: 'PUT', body: { assetId: +b.dataset.primary } }); await showItem(it.id, +b.dataset.primary); toast('Primary variation updated'); });
  $$('#lbPanel [data-delete-edit]').forEach(b => b.onclick = async () => {
    const a = d.assets.find(x => x.id === +b.dataset.deleteEdit);
    if (!confirm(`Delete ${a.fileName}?\n\nThis copy was made in the photo editor; the original stays as it is. Export again from the editor for a new copy, with the original's metadata.`)) return;
    try {
      const r = await api(`/api/assets/${a.id}/file`, { method: 'DELETE' });
      toast('Edited copy deleted');
      if (r.primaryAssetId) await showItem(it.id, state.lb.assetId === a.id ? r.primaryAssetId : state.lb.assetId); else closeLightbox();
      reload({ keepScroll: true });
    } catch (err) { toast(err.message); }
  });
  $('#pTag')?.addEventListener('click', () => tagDialog([it.id], () => showItem(it.id, state.lb.assetId)));
  $('#lbPanel [data-hide]')?.addEventListener('click', async e => {
    const hidden = e.currentTarget.dataset.hide === '1';
    await api('/api/items/hidden', { method: 'POST', body: { itemIds: [it.id], hidden } });
    toast(hidden ? 'Hidden from viewers' : 'Viewers can see it again');
    for (const x of loadedItems()) if (x.id === it.id) x.hiddenFromViewers = hidden;
    const cell = $(`.cell[data-id="${it.id}"]`);
    if (cell) { const i = +cell.dataset.index, fresh = makeCell(i); cell.replaceWith(fresh); vg.cells.set(i, fresh); }
    showItem(it.id, state.lb.assetId); loadAlbums(); loadYears();
  });
  $('#pAlbum')?.addEventListener('click', () => albumPicker([it.id]));
  $('#lbPanel [data-cover-use]')?.addEventListener('click', () => setCover(coverTarget().kind, it.id));
  $('#lbPanel [data-cover-auto]')?.addEventListener('click', () => setCover(coverTarget().kind, null));
  const sq = $('#pSqueeze');
  if (sq) {
    sq.value = String(it.squeeze || 1);
    sq.onchange = async () => {
      let factor = +sq.value;
      if (sq.value === 'other') {
        const v = prompt('Stretch the long side by (from 1 to 3)', fmtSqueeze(it.squeeze > 1 ? it.squeeze : 1.5));
        factor = v === null ? NaN : +v.replace('×', '').replace(',', '.');
        if (!(factor >= 1 && factor <= 3)) { sq.value = String(it.squeeze || 1); if (v !== null) toast('Use a factor between 1 and 3'); return; }
      }
      try { await setSqueeze([it.id], factor); } catch (err) { toast(err.message); }
    };
  }
  const proj = $('#pProj');
  if (proj) {
    proj.value = d.tags.some(t => t.key === 'projection') ? (cur.projection ? '1' : '0') : '';
    proj.onchange = async () => {
      const sphere = proj.value === '' ? null : proj.value === '1';
      const r = await api(`/api/items/${it.id}/projection`, { method: 'POST', body: { sphere } });
      for (const x of loadedItems()) if (x.id === it.id) x.projection = r.projection;
      const cell = $(`.cell[data-id="${it.id}"]`);
      if (cell) { const i = +cell.dataset.index, fresh = makeCell(i); cell.replaceWith(fresh); vg.cells.set(i, fresh); }
      toast(sphere === null ? 'Shown as detected' : sphere ? 'Shown as a 360° photo, for everyone' : 'Shown as a flat photo, for everyone');
      state.lb.flatItem = null;
      showItem(it.id, state.lb.assetId); loadTagKeys();
    };
  }
  $$('#lbPanel .tag .x').forEach(x => x.onclick = async e => { e.stopPropagation(); const t = x.closest('.tag'); await api('/api/items/tags', { method: 'POST', body: { itemIds: [it.id], remove: [{ key: t.dataset.k, value: t.dataset.v }] } }); showItem(it.id, state.lb.assetId); loadTagKeys(); });
  $$('#lbPanel .tag[data-k]').forEach(t => t.onclick = () => { closeLightbox(); state.filter.tagKey = t.dataset.k; state.filter.tagVal = t.dataset.v; $('#tagKeySel').value = t.dataset.k; $('#tagValSel').hidden = false; $('#tagValSel').innerHTML = `<option value="${esc(t.dataset.v)}">${esc(t.dataset.v)}</option>`; reload(); });
}
// ---------- covers ----------
// The photo the Albums and Years views show for an album or a year: an admin's choice, else the album's earliest or the
// year's newest. Chosen from the photo, opened from that album or year (or one selected there).
/** The album or year the gallery shows now, with its cover: { kind, id, label, coverItemId, chosen }. */
function coverTarget() {
  if (state.filter.album) {
    const a = state.albums.find(x => String(x.id) === state.filter.album);
    return a && { kind: 'album', id: a.id, label: 'album', coverItemId: a.coverItemId, chosen: a.chosenCoverId != null };
  }
  if (state.filter.year) {
    const y = state.years?.find(x => String(x.year) === state.filter.year);
    return y && { kind: 'year', id: y.year, label: String(y.year), coverItemId: y.cover?.itemId, chosen: !!y.cover?.chosen };
  }
  return null;
}
/** Sets (itemId) or clears (null: automatic) the cover of the album or year on screen. */
async function setCover(kind, itemId) {
  const t = coverTarget(); if (!t || t.kind !== kind) return false;
  try {
    await api(kind === 'album' ? `/api/albums/${t.id}/cover` : `/api/items/years/${t.id}/cover`, { method: 'PUT', body: { itemId } });
  } catch (err) { toast(err.message); return false; }
  const what = kind === 'album' ? 'the album' : t.label;
  toast(itemId == null ? `Cover of ${what}: its ${kind === 'album' ? 'earliest' : 'newest'} photo again` : `Cover of ${what} chosen`);
  await (kind === 'album' ? loadAlbums() : loadYears());
  if (state.lb.open) renderPanel();
  return true;
}
/** Admins, in the info panel: use the photo as the cover of the album or year it was opened from. */
function coverHtml(d) {
  const t = coverTarget(), it = d.item; if (!t) return '';
  if (t.kind === 'album' && !d.albums.some(a => a.id === t.id)) return '';
  if (t.kind === 'year' && !(it.takenAt || '').startsWith(t.label)) return '';
  const what = t.kind === 'album' ? 'this album' : t.label;
  const auto = t.chosen ? `<button class="ghost small" data-cover-auto title="Show the ${t.kind === 'album' ? 'earliest' : 'newest'} photo instead">Automatic cover</button>` : '';
  return `<div class="vis">${t.coverItemId === it.id
    ? `<span class="pill" title="The Albums and Years views show this photo for ${esc(what)}">Cover of ${esc(what)}${t.chosen ? '' : ' (automatic)'}</span>${auto}`
    : `<button class="ghost small" data-cover-use title="Show this photo for ${esc(what)} in the ${t.kind === 'album' ? 'Albums' : 'Years'} view">Use as cover of ${esc(what)}</button>${auto}`}</div>`;
}

/** An anamorphic lens's stretch: admins choose it here; others see it when there is one. */
function squeezeHtml(it, cur, isAdmin) {
  if (cur.kind !== 'image' && cur.kind !== 'video') return '';
  const s = it.squeeze || 1;
  if (!isAdmin) return s > 1 ? `<dt>Anamorphic</dt><dd>Stretched ${fmtSqueeze(s)}×</dd>` : '';
  const known = s === 1 || SQUEEZES.includes(s);
  return `<dt>Anamorphic</dt><dd><select id="pSqueeze" title="An anamorphic lens's factor: the long side is stretched by it wherever the photo is shown, edited or exported">
    <option value="1">None</option>${SQUEEZES.map(v => `<option value="${v}">${fmtSqueeze(v)}×</option>`).join('')}
    ${known ? '' : `<option value="${s}">${fmtSqueeze(s)}×</option>`}<option value="other">Other…</option></select></dd>`;
}
function projectionHtml(d, cur, isAdmin) {
  if (cur.kind !== 'image') return '';
  const sphere = !!cur.projection, detected = !!cur.detectedProjection;
  if (!isAdmin) return sphere ? '<dt>360°</dt><dd>A 360° photo</dd>' : '';
  // Offered for photos that are, were detected as, or could be 360° ones (twice as wide as high).
  const tagged = d.tags.some(t => t.key === 'projection');
  if (!sphere && !detected && !tagged && !(cur.width && cur.width === 2 * cur.height)) return '';
  return `<dt>360°</dt><dd><select id="pProj" title="How the viewer shows it, for everyone">
    <option value="">Automatic (${detected ? '360°' : 'flat'})</option><option value="1">360° photo</option><option value="0">Flat photo</option></select></dd>`;
}
// ---------- rotation ----------
// Admins: each click saves a quarter turn on the item (the rotate-degrees tag). The picture turns at once in the
// browser, then is swapped for the server's rotated rendering, which the grid and every later visit use too.
// Everyone else: the picture turns for this view only.
async function rotateView(delta) {
  const d = state.lb.detail; if (!d || !state.lb.open) return;
  const asset = d.assets.find(a => a.id === state.lb.assetId);
  if (!asset || asset.kind === 'audio' || state.lb.sphere) return;
  state.lb.viewRot += delta; applyViewRotation();
  if (state.me?.role !== 'admin') return;
  const itemId = d.item.id;
  state.lb.pendingRot++;
  try {
    const r = await api(`/api/items/${itemId}/rotate`, { method: 'POST', body: { by: delta } });
    onItemRotated(itemId, r.rotation);
  } catch (e) {
    toast(`Rotation not saved: ${e.message}`);
    if (state.lb.detail?.item.id === itemId) { state.lb.viewRot -= delta; applyViewRotation(); }
  } finally { state.lb.pendingRot--; }
  if (state.lb.pendingRot === 0 && state.lb.detail?.item.id === itemId) swapToServerRotation();
}
function onItemRotated(itemId, rotation) {
  for (const it of loadedItems()) if (it.id === itemId) it.rotation = rotation;
  const cell = $(`.cell[data-id="${itemId}"]`);
  if (cell) setSrc(cell, itemAt(+cell.dataset.index));
  const d = state.lb.detail;
  if (d?.item.id === itemId) {
    d.item.rotation = rotation;
    d.tags = d.tags.filter(t => t.key !== 'rotate-degrees');
    if (rotation) d.tags.push({ itemId, key: 'rotate-degrees', value: String(rotation) });
    d.tags.sort((a, b) => a.key.localeCompare(b.key));
    renderPanel();
  }
  loadTagKeys();
}
/** Replaces the browser-turned photo with the server's rotated rendering, without a visible jump. */
function swapToServerRotation() {
  const d = state.lb.detail, asset = d?.assets.find(a => a.id === state.lb.assetId);
  const el = $('#lbStage img.main');
  if (!el || asset?.kind !== 'image') return; // videos stay turned in the browser
  const url = thumbUrl(asset.id, asset.hash, d.item.rotation, previewSize(), d.item.squeeze);
  const pre = new Image();
  pre.onload = () => {
    if ($('#lbStage img.main') !== el || state.lb.pendingRot > 0) return;
    el.onload = () => {
      el.style.transition = 'none';
      for (const p of ['width', 'height', 'maxWidth', 'maxHeight', 'transform']) el.style[p] = '';
      state.lb.viewRot = 0;
      void el.offsetWidth; // apply without animating, then restore the transition
      el.style.transition = '';
      applyZoom();
      el.onload = () => applyZoom();
    };
    el.src = url;
  };
  pre.src = url;
}
/** Turns the photo or video on screen by state.lb.viewRot degrees, sized so the turned box fits the stage. */
function applyViewRotation() {
  const el = $('#lbStage img.main, #lbStage video'); if (!el) return;
  if (el.tagName === 'IMG' && viewZoom !== 'fit' && !state.lb.sphere) { applyZoom(zoomCentre()); return; } // zoomed: laid out there
  const rot = state.lb.viewRot;
  renderFaceLayer(); // boxes only fit the picture as the server turned it
  // A video's anamorphic squeeze is stretched here; a photo's preview comes stretched from the server.
  const squeeze = el.tagName === 'VIDEO' ? (state.lb.detail?.item.squeeze || 1) : 1;
  if (!rot && squeeze === 1) { for (const p of ['width', 'height', 'maxWidth', 'maxHeight', 'transform', 'objectFit']) el.style[p] = ''; return; }
  let nw = el.naturalWidth || el.videoWidth, nh = el.naturalHeight || el.videoHeight;
  if (!nw || !nh) return; // not loaded yet; called again on load
  if (nw >= nh) nw *= squeeze; else nh *= squeeze;
  const stage = $('#lbStage'), W = stage.clientWidth, H = stage.clientHeight;
  const sideways = ((rot % 180) + 180) % 180 === 90;
  const scale = Math.min(1, W / (sideways ? nh : nw), H / (sideways ? nw : nh));
  Object.assign(el.style, { maxWidth: 'none', maxHeight: 'none', width: `${nw * scale}px`, height: `${nh * scale}px`,
    objectFit: squeeze === 1 ? '' : 'fill', transform: rot ? `rotate(${rot}deg)` : '' });
}
$('#lbRotL').onclick = () => rotateView(-90);
$('#lbRotR').onclick = () => rotateView(90);
addEventListener('resize', () => {
  if (state.lb.open && (state.lb.viewRot || state.lb.detail?.item.squeeze > 1)) applyViewRotation();
  if (state.lb.open) renderFaceLayer();
  const img = state.lb.open && viewZoom === 'fit' && $('#lbStage img.main');
  if (img) showFitLevel(img, fullSize(img));
});

// EXIF / container metadata for the asset on screen. Fetched only while the info panel is open; cached per asset.
const metaCache = new Map();
async function loadMeta(asset) {
  let m = metaCache.get(asset.id);
  if (m === undefined) {
    $('#pMeta').innerHTML = '<h4>Details</h4><div class="muted small">Reading metadata…</div>';
    try { m = await api(`/api/assets/${asset.id}/metadata`, { quiet: true }); } catch { m = null; }
    metaCache.set(asset.id, m);
  }
  if (state.lb.assetId !== asset.id || !$('#pMeta')) return; // the panel moved on to another file
  renderMeta(m, asset);
}
function renderMeta(m, asset) {
  const box = $('#pMeta');
  if (!m) { box.innerHTML = ''; return; }
  const rows = m.summary.map(f => `<dt>${esc(f.label)}</dt><dd>${esc(f.value)}</dd>`);
  if (m.location) {
    const { lat, lon, altitudeM } = m.location;
    rows.push(`<dt>Location</dt><dd><a href="https://www.openstreetmap.org/?mlat=${lat}&mlon=${lon}#map=15/${lat}/${lon}" target="_blank" rel="noopener noreferrer" title="Open in OpenStreetMap">${lat.toFixed(5)}, ${lon.toFixed(5)}</a>${altitudeM != null ? ` · ${Math.round(altitudeM)} m` : ''}</dd>`);
  }
  const total = m.groups.reduce((n, g) => n + g.tags.length, 0);
  const groups = m.groups.map(g => `<div class="mgroup"><h5>${esc(g.name)} <span class="muted">${g.tags.length}</span></h5><dl>${g.tags.map(t => `<dt title="${esc(t.name)}">${esc(t.name)}</dt><dd>${esc(t.value)}</dd>`).join('')}</dl></div>`).join('');
  box.innerHTML = `<h4>${asset.kind === 'image' ? 'Camera &amp; EXIF' : 'Media info'}</h4>
    ${rows.length ? `<dl>${rows.join('')}</dl>` : `<div class="muted small">${asset.kind === 'image' ? 'No camera metadata in this file' : 'No details available'}</div>`}
    ${total ? `<details class="allmeta" ${state.lb.metaOpen ? 'open' : ''}><summary>All metadata (${total} tags)</summary>
      <input type="search" class="mfilter" placeholder="Filter tags…"><div class="mgroups">${groups}</div></details>` : ''}`;
  const det = $('details.allmeta', box);
  det?.addEventListener('toggle', () => { state.lb.metaOpen = det.open; });
  $('.mfilter', box)?.addEventListener('input', e => {
    const q = e.target.value.trim().toLowerCase();
    $$('.mgroup', box).forEach(g => {
      let any = false;
      $$('dt', g).forEach(dt => { const dd = dt.nextElementSibling; const hit = !q || (dt.textContent + ' ' + dd.textContent).toLowerCase().includes(q); dt.hidden = dd.hidden = !hit; any ||= hit; });
      g.hidden = !any;
    });
  });
}
/**
 * Hides the viewer. Unless the browser already navigated (Back button), pops the viewer's history entry so the
 * address bar, Back and Forward stay in step with what is on screen.
 */
function closeLightbox({ fromHistory = false } = {}) {
  if (!state.lb.open) return;
  state.lb.open = false; lb.hidden = true; pano?.hide(); $('#lbStage').innerHTML = ''; closeFacePop();
  const hadEntry = state.lb.hasEntry; state.lb.hasEntry = false;
  if (!fromHistory && hadEntry && location.hash.startsWith('#/item/')) history.back();
  // Keep the last photo viewed on screen, in case the arrows moved far from where the viewer was opened.
  if (state.lb.index >= 0) scrollToIndex(state.lb.index);
}
function lbStep(d) { if (state.lb.index < 0) return; const n = state.lb.index + d; if (n < 0 || n >= state.total) return; openLightbox(n); }
$('#lbClose').onclick = closeLightbox; $('#lbPrev').onclick = () => lbStep(-1); $('#lbNext').onclick = () => lbStep(1);
$('#lbInfoBtn').onclick = () => setPanel(!state.lb.panel);
$('#lbStage').addEventListener('click', e => {
  if (zoomDrag?.moved) return; // the end of a drag, not a click
  if (e.target === $('#lbStage') || e.target.classList.contains('zoom-wrap')) closeLightbox();
});
$('#lbEdit').onclick = () => { if (state.lb.editAsset) location.hash = `#/edit/${state.lb.editAsset}`; };
$('#lbFaces').onclick = () => toggleFaces();
document.addEventListener('keydown', e => {
  if (editorOpen()) return; // the editor has its own keys
  if (!$('#modal').hidden) { if (e.key === 'Escape') closeModal(); return; }
  if (state.lb.open) {
    if (e.target.closest?.('#facePop')) { if (e.key === 'Escape') closeFacePop(); return; } // typing a name
    // A 360° photo takes ↑ ↓, Shift+← →, + − and 0 to look around (panorama.js); ← → still step between items.
    if (state.lb.sphere && pano?.shown && !e.ctrlKey && !e.metaKey && !e.altKey && !e.target.closest?.('input, select, textarea') && pano.key(e)) { e.preventDefault(); return; }
    if (e.target === $('#lbZoom')) return; // its own arrow keys
    const zoomable = !!$('#lbStage img.main') && !state.lb.sphere; // a flat photo
    if (zoomable && e.key === 'z') setZoom(viewZoom === 'fit' ? 1 : 'fit');
    else if (zoomable && (e.key === '+' || e.key === '=')) zoomStep(1);
    else if (zoomable && (e.key === '-' || e.key === '_')) zoomStep(-1);
    else if (zoomable && e.key === '0') setZoom('fit');
    else if (e.key === 'v') toggleSphere();
    else if (e.key === 'Escape') closeLightbox(); else if (e.key === 'f') toggleFaces(); else if (e.key === 'ArrowLeft') lbStep(-1); else if (e.key === 'ArrowRight') lbStep(1); else if (e.key === 'i') $('#lbInfoBtn').click(); else if (e.key === '[') rotateView(-90); else if (e.key === ']') rotateView(90); else if (e.key === 'e') $('#lbEdit').click();
    return;
  }
  if (e.key === 'Escape' && state.selecting) setSelecting(false);
  if (e.key === '/' && document.activeElement !== $('#search')) { e.preventDefault(); $('#search').focus(); }
});

// ---------- library status & activity (polled) ----------
const fmtEta = s => s == null || !isFinite(s) ? '' : s < 60 ? `~${Math.max(1, Math.round(s))}s left` : s < 3600 ? `~${Math.round(s / 60)} min left` : `~${(s / 3600).toFixed(1)} h left`;
const statusState = { timer: null, wasScanning: false, lastItems: null, lastGridRefresh: 0, finished: null, last: null };

async function pollStatus() {
  clearTimeout(statusState.timer);
  if (!state.me?.role) return;
  let st;
  try { st = await api('/api/status', { quiet: true }); }
  catch { statusState.timer = setTimeout(pollStatus, 15000); return; }
  statusState.last = st;
  renderStats(st.stats);
  onScanTransitions(st);
  // Batch exports from a recipe add JPEG variations: refresh the grid once they are all done.
  const exporting = st.exports?.queued > 0;
  if (statusState.wasExporting && !exporting && state.view === 'grid' && !state.selecting) reload({ keepScroll: true });
  statusState.wasExporting = exporting;
  renderActivity(st);
  if (state.view === 'admin') renderAdminStats(st.stats);
  if (state.view === 'admin' && !$('.tab[data-tab="faces"]')?.hidden) loadFaces();
  statusState.timer = setTimeout(pollStatus, st.busy ? 1500 : 15000);
}
document.addEventListener('visibilitychange', () => { if (!document.hidden) pollStatus(); else clearTimeout(statusState.timer); });

function renderStats(s) {
  const f = state.filter;
  const tiles = [
    `<div class="stat" style="grid-column: span 2"><div class="v">${fmtNum(s.items)} items</div><div class="sub">${fmtNum(s.images)} photos · ${fmtNum(s.videos)} videos · ${fmtNum(s.audio)} audio</div></div>`,
    `<div class="stat" title="Files on disk, including duplicates and RAW files"><div class="v">${fmtNum(s.assets)}</div><div class="k">files · ${fmtBytes(s.bytes)}</div></div>`,
    `<div class="stat link ${f.dups ? 'on' : ''}" data-stat="dups" title="${fmtNum(s.duplicateAssets)} extra copies of ${fmtNum(s.duplicateGroups)} files, using ${fmtBytes(s.duplicateBytes)}. Click to show them."><div class="v">${fmtNum(s.duplicateAssets)}</div><div class="k">duplicates</div><div class="sub">${fmtBytes(s.duplicateBytes)} wasted</div></div>`,
    `<div class="stat link ${f.variations ? 'on' : ''}" data-stat="variations" title="Items with more than one distinct file, such as a JPEG and its RAW. Click to show them."><div class="v">${fmtNum(s.variationItems)}</div><div class="k">with variations</div><div class="sub">${fmtNum(s.raw)} RAW files</div></div>`,
  ];
  if (s.missing > 0) tiles.push(`<div class="stat warn" title="Indexed files no longer found on disk"><div class="v">${fmtNum(s.missing)}</div><div class="k">missing files</div></div>`);
  $('#libStats').innerHTML = tiles.join('');
  $$('#libStats [data-stat]').forEach(t => t.onclick = () => {
    const chk = t.dataset.stat === 'dups' ? $('#dupsChk') : $('#varChk');
    chk.checked = !chk.checked; chk.dispatchEvent(new Event('change'));
    if (state.view !== 'grid') location.hash = '#/';
    renderStats(s);
  });
}

function onScanTransitions(st) {
  const sc = st.scan, items = st.stats.items;
  const gridIsSmall = state.view === 'grid' && vg.known && state.total < PAGE && !state.selecting && !state.lb.open;
  if (sc.running) {
    statusState.finished = null;
    // While indexing a small view, let new items appear as they are committed.
    if (gridIsSmall && items !== statusState.lastItems && Date.now() - statusState.lastGridRefresh > 4000) { statusState.lastGridRefresh = Date.now(); reload({ keepScroll: true }); }
  } else if (statusState.wasScanning) {
    statusState.finished = { added: sc.added, updated: sc.updated, missing: sc.missing, failed: sc.failed, status: sc.lastStatus };
    loadYears(); loadAlbums(); loadTagKeys();
    if (gridIsSmall) { reload({ keepScroll: true }); statusState.finished.refreshed = true; }
  }
  statusState.wasScanning = sc.running;
  statusState.lastItems = items;
}

function renderActivity(st) {
  const sc = st.scan, th = st.thumbnails, tc = st.transcodes, blocks = [];
  let pill = '';
  if (sc.running) {
    if (sc.phase === 'checking') {
      blocks.push(`<div class="row1"><span>Checking for deleted files…</span></div><div class="bar indet"><i></i></div><div class="detail">Files no longer on disk are removed from the gallery</div>`);
      pill = 'Checking for deleted files…';
    } else if (sc.phase === 'discovering') {
      blocks.push(`<div class="row1"><span>Discovering files…</span><span class="n">${fmtNum(sc.discovered)}</span></div>
        <div class="bar indet"><i></i></div>
        <div class="detail">${fmtNum(sc.queued)} new or changed · ${fmtNum(sc.unchanged)} unchanged</div>`);
      pill = `Scanning · ${fmtNum(sc.discovered)} found`;
    } else if (sc.phase === 'indexing') {
      const pct = sc.queued ? Math.floor(sc.processed * 100 / sc.queued) : 100;
      blocks.push(`<div class="row1"><span>Indexing</span><span class="n">${fmtNum(sc.processed)} / ${fmtNum(sc.queued)}</span></div>
        <div class="bar"><i style="width:${pct}%"></i></div>
        <div class="detail"><b>${fmtNum(sc.pending)}</b> pending${sc.filesPerSecond ? ` · ${sc.filesPerSecond.toFixed(sc.filesPerSecond < 10 ? 1 : 0)} files/s` : ''}${sc.etaSeconds != null ? ' · ' + fmtEta(sc.etaSeconds) : ''}${sc.failed ? ` · <span class="error">${fmtNum(sc.failed)} failed</span>` : ''}</div>`);
      pill = `Indexing ${pct}% · ${fmtNum(sc.pending)} pending`;
    } else {
      blocks.push(`<div class="row1"><span>Finalizing scan…</span></div><div class="bar indet"><i></i></div><div class="detail">Updating items and checking for missing files</div>`);
      pill = 'Finalizing scan…';
    }
    if (sc.currentPath) blocks.push(`<div class="path" title="${esc(sc.currentPath)}">&lrm;${esc(sc.currentPath)}</div>`);
  }
  if (th.running) {
    if (th.total > 0) {
      const pct = Math.floor(th.done * 100 / th.total);
      blocks.push(`<div class="row1"><span>Thumbnails</span><span class="n">${fmtNum(th.done)} / ${fmtNum(th.total)}</span></div>
        <div class="bar"><i style="width:${pct}%"></i></div><div class="detail"><b>${fmtNum(th.pending)}</b> pending</div>`);
      pill ||= `Thumbnails · ${fmtNum(th.pending)} pending`;
    } else {
      blocks.push(`<div class="row1"><span>Thumbnails</span></div><div class="bar indet"><i></i></div><div class="detail">Checking which thumbnails are missing…</div>`);
      pill ||= 'Checking thumbnails…';
    }
  }
  const fa = st.faces;
  if (fa?.running) {
    if (fa.pending > 0) {
      const total = fa.done + fa.pending, pct = Math.floor(fa.done * 100 / total);
      blocks.push(`<div class="row1"><span>Finding faces</span><span class="n">${fmtNum(fa.done)} / ${fmtNum(total)}</span></div>
        <div class="bar"><i style="width:${pct}%"></i></div><div class="detail"><b>${fmtNum(fa.pending)}</b> pending · ${fmtNum(fa.faces)} faces found</div>`);
      pill ||= `Faces · ${fmtNum(fa.pending)} pending`;
    } else {
      blocks.push(`<div class="row1"><span>Finding faces</span></div><div class="bar indet"><i></i></div><div class="detail">Checking which photos need searching…</div>`);
      pill ||= 'Checking faces…';
    }
  } else if (fa?.grouping) {
    blocks.push(`<div class="row1"><span>Grouping faces into people…</span></div><div class="bar indet"><i></i></div>`);
    pill ||= 'Grouping faces…';
  } else if (fa?.state === 'downloading') {
    blocks.push(`<div class="row1"><span>Downloading face models</span></div><div class="bar indet"><i></i></div>`);
    pill ||= 'Downloading face models';
  }
  if (st.exports?.queued > 0) {
    blocks.push(`<div class="row1"><span>Exporting JPEGs</span><span class="n">${fmtNum(st.exports.queued)} to go</span></div>
      <div class="bar indet"><i></i></div>${st.exports.active ? `<div class="detail">${esc(st.exports.active)}</div>` : ''}`);
    pill ||= `Exporting · ${fmtNum(st.exports.queued)} to go`;
  }
  if (tc.queued > 0) {
    blocks.push(`<div class="row1"><span>Transcoding</span><span class="n">${fmtNum(tc.queued)} queued</span></div>
      <div class="bar indet"><i></i></div>${tc.active ? `<div class="detail">${esc(tc.active)}</div>` : ''}`);
    pill ||= `Transcoding · ${fmtNum(tc.queued)} queued`;
  } else if (tc.idle?.state === 'converting') {
    blocks.push(`<div class="row1"><span>Converting videos while idle</span><span class="n">${fmtNum(tc.idle.left)} to go</span></div>
      <div class="bar indet"><i></i></div><div class="detail">${tc.idle.file ? esc(tc.idle.file) + ' · ' : ''}lowest priority</div>`);
    pill ||= `Converting videos · ${fmtNum(tc.idle.left)} to go`;
  }
  const fin = statusState.finished;
  if (fin && !sc.running) {
    const parts = [`${fmtNum(fin.added)} added`]; if (fin.updated) parts.push(`${fmtNum(fin.updated)} updated`); if (fin.missing) parts.push(`${fmtNum(fin.missing)} missing`); if (fin.failed) parts.push(`${fmtNum(fin.failed)} failed`);
    blocks.push(`<div class="done"><span>Scan ${fin.status === 'done' ? 'finished' : esc(fin.status || 'ended')}: ${parts.join(', ')}</span>
      ${fin.refreshed ? '<button class="ghost small" id="actDismiss">OK</button>' : '<button class="small" id="actRefresh">Refresh view</button>'}</div>`);
  }
  const box = $('#activity');
  box.hidden = blocks.length === 0;
  box.innerHTML = blocks.join('');
  $('#actRefresh')?.addEventListener('click', () => { statusState.finished = null; reload({ keepScroll: true }); renderActivity(statusState.last); });
  $('#actDismiss')?.addEventListener('click', () => { statusState.finished = null; renderActivity(statusState.last); });
  $('#activityPill').hidden = !pill;
  $('#activityPillText').textContent = pill;
  const last = sc.lastFinishedAt ? `scanned ${sc.lastFinishedAt.replace('T', ' ').slice(0, 16)}` : '';
  $('#libUpdated').textContent = sc.running ? '' : last;
}
$('#activityPill').onclick = () => { document.body.classList.remove('side-closed'); document.body.classList.add('side-open'); $('#sidebar').scrollTop = 0; };

function renderAdminStats(s) {
  const el = $('#stats'); if (!el) return;
  el.textContent = `${fmtNum(s.items)} items (${fmtNum(s.images)} photos, ${fmtNum(s.videos)} videos, ${fmtNum(s.audio)} audio)
${fmtNum(s.assets)} files · ${fmtBytes(s.bytes)} · ${fmtNum(s.raw)} RAW
${fmtNum(s.duplicateAssets)} duplicate copies of ${fmtNum(s.duplicateGroups)} files, wasting ${fmtBytes(s.duplicateBytes)}
${fmtNum(s.variationItems)} items with variations · ${fmtNum(s.missing)} missing files`;
}

// ---------- admin ----------
async function showAdmin() {
  leaveGrid('admin');
  setLayout(null, true);
  const main = $('#main'); main.innerHTML = ''; main.appendChild($('#adminTpl').content.cloneNode(true));
  $$('.tabs button').forEach(b => b.onclick = () => { $$('.tabs button').forEach(x => x.classList.toggle('on', x === b)); $$('.tab').forEach(t => t.hidden = t.dataset.tab !== b.dataset.tab); if (b.dataset.tab === 'dups') loadDups(); if (b.dataset.tab === 'transcodes') loadTranscodes(); if (b.dataset.tab === 'albums') loadAlbumAdmin(); if (b.dataset.tab === 'faces') loadFaces(); memoryPolling(b.dataset.tab === 'memory'); });
  $('#memFreeBtn').onclick = freeMemory;
  $('#pruneAlbumsBtn').onclick = pruneAlbums;
  $('#autoAlbumsAdminBtn').onclick = async () => { await autoAlbums(); loadAlbumAdmin(); };
  $('#regenAlbumsBtn').onclick = regenerateAutoAlbums;
  await Promise.all([loadSources(), loadScan(), loadSettings(), loadUsers()]);
  if (statusState.last) renderAdminStats(statusState.last.stats);
  $('#srcForm').onsubmit = async e => { e.preventDefault(); try { await api('/api/admin/sources', { method: 'POST', body: { path: $('#srcPath').value } }); $('#srcPath').value = ''; loadSources(); } catch (err) { toast(err.message); } };
  $('#scanBtn').onclick = async () => { await api('/api/admin/scan', { method: 'POST' }); toast('Scan started'); pollScan(); setTimeout(pollStatus, 300); };
  $('#scanCancelBtn').onclick = () => api('/api/admin/scan/cancel', { method: 'POST' });
  $('#rebuildBtn').onclick = async () => { await api('/api/admin/rebuild', { method: 'POST' }); toast('Items rebuilt'); pollStatus(); };
  $('#thumbsBtn').onclick = async () => {
    const r = await api('/api/admin/thumbnails', { method: 'POST' });
    toast(r.queued || r.repaired ? `${r.repaired ? `Repaired ${fmtNum(r.repaired)} items · ` : ''}${fmtNum(r.queued)} thumbnails queued` : 'No missing thumbnails');
    pollStatus();
  };
  $('#settingsForm').onsubmit = async e => {
    e.preventDefault(); const f = e.target; const values = {};
    for (const el of f.elements) if (el.name) values[el.name] = el.type === 'checkbox' ? String(el.checked) : el.value;
    try { await api('/api/admin/settings', { method: 'PUT', body: { values } }); toast('Settings saved'); loadSettings(); loadMe(); } catch (err) { toast(err.message); }
  };
  $('#facesForm').onchange = async e => {
    const f = e.currentTarget;
    try { await api('/api/admin/faces', { method: 'PUT', body: { enabled: f.enabled.checked, pack: f.pack.value } }); } catch (err) { toast(err.message); }
    loadFaces(); pollStatus();
  };
  $('#faceDownloadBtn').onclick = async () => {
    try { await api('/api/admin/faces/download', { method: 'POST', body: { pack: $('#facesForm').pack.value } }); toast('Downloading the face models'); } catch (err) { toast(err.message); }
    loadFaces(); pollStatus();
  };
  $('#faceRetryBtn').onclick = async () => {
    const r = await api('/api/admin/faces/find-again', { method: 'POST', body: { failedOnly: true } });
    toast(r.forgotten ? `Trying ${fmtNum(r.forgotten)} photo${r.forgotten === 1 ? '' : 's'} again` : 'No photos failed'); loadFaces(); pollStatus();
  };
  $('#faceGroupBtn').onclick = async () => { await api('/api/admin/faces/group', { method: 'POST' }); toast('Grouping faces…'); setTimeout(() => { loadFaces(); loadPeople(); }, 3000); pollStatus(); };
  $('#faceAgainBtn').onclick = async () => {
    if (!confirm('Search every photo for faces again? Faces found so far stay until each photo is done again.')) return;
    await api('/api/admin/faces/find-again', { method: 'POST', body: { failedOnly: false } }); toast('Finding faces again in every photo'); loadFaces(); pollStatus();
  };
  $('#viewerPwForm').onsubmit = async e => { e.preventDefault(); await api('/api/admin/users/viewer-password', { method: 'PUT', body: { password: e.target.password.value } }); toast(e.target.password.value ? 'Viewer password set' : 'Gallery is open to anonymous viewers'); e.target.reset(); loadUsers(); };
  $('#adminPwForm').onsubmit = async e => { e.preventDefault(); try { await api('/api/admin/users/admin-password', { method: 'PUT', body: { password: e.target.password.value } }); toast('Admin password changed'); e.target.reset(); } catch (err) { toast(err.message); } };
  pollScan();
}
async function loadSources() {
  const s = await api('/api/admin/sources');
  $('#srcList').innerHTML = s.map(x => `<li><input type="checkbox" ${x.enabled ? 'checked' : ''} data-en="${x.id}" title="Enabled"><span>${esc(x.path)} ${x.exists ? '' : '<span class="pill missing">not found</span>'}</span><button class="ghost small danger" data-del="${x.id}">Remove</button></li>`).join('') || '<li class="muted">No sources yet. Add the directory that holds your photos.</li>';
  $$('#srcList [data-del]').forEach(b => b.onclick = async () => { if (confirm('Remove this source? Indexed entries stay until the next scan marks them missing.')) { await api(`/api/admin/sources/${b.dataset.del}`, { method: 'DELETE' }); loadSources(); } });
  $$('#srcList [data-en]').forEach(c => c.onchange = () => api(`/api/admin/sources/${c.dataset.en}/enabled?enabled=${c.checked}`, { method: 'PUT' }));
}
let scanTimer;
async function loadScan() {
  if (state.view !== 'admin') return;
  const s = await api('/api/admin/scan/status');
  const p = s.progress;
  const lines = [p.running
    ? `RUNNING · ${p.phase} · since ${p.startedAt}\n  found ${fmtNum(p.filesSeen)} · unchanged ${fmtNum(p.filesSkipped)} · to index ${fmtNum(p.filesQueued)} · indexed ${fmtNum(p.filesProcessed)} · pending ${fmtNum(p.pending)}\n  added ${fmtNum(p.filesAdded)} · updated ${fmtNum(p.filesUpdated)} · failed ${fmtNum(p.filesFailed)}\n  ${p.currentPath || ''}`
    : `idle${p.message ? ' (last: ' + p.message + ')' : ''}`, '', 'Recent runs:',
    ...s.runs.map(r => `  #${r.id} ${r.startedAt} → ${r.finishedAt || '…'}  ${r.status}  seen ${r.filesSeen} added ${r.filesAdded} updated ${r.filesUpdated} missing ${r.filesMissing}${r.message ? '  ' + r.message : ''}`)];
  $('#scanStatus').textContent = lines.join('\n');
  return p.running;
}
async function pollScan() { clearTimeout(scanTimer); const running = await loadScan(); if (running) scanTimer = setTimeout(pollScan, 1500); }
async function loadSettings() {
  const r = await api('/api/admin/settings'); const f = $('#settingsForm');
  for (const el of f.elements) if (el.name && r.values[el.name] != null) { if (el.type === 'checkbox') el.checked = r.values[el.name] === 'true'; else el.value = r.values[el.name]; }
  $('#settingsInfo').textContent = `Data dir: ${r.dataDir} · derived: ${r.derivedDir}`;
}
/** Admin › Faces: on or off, the model pack (and downloading it), progress, searching again. */
async function loadFaces() {
  if (state.view !== 'admin') return;
  let r;
  try { r = await api('/api/admin/faces', { quiet: true }); } catch { return; }
  const f = $('#facesForm'); if (!f) return;
  f.enabled.checked = r.enabled;
  const mb = n => `${Math.round(n / 1e6)} MB`;
  f.pack.innerHTML = r.packs.map(p => `<option value="${esc(p.name)}" ${p.name === r.pack ? 'selected' : ''}>${esc(p.name)}: ${esc(p.description)}</option>`).join('');
  const pack = r.packs.find(p => p.name === r.pack), dl = r.download, downloading = dl?.running;
  $('#facePackInfo').textContent = pack.present
    ? `${pack.files.join(' and ')} (${mb(pack.filesBytes)}) are in ${r.modelsDir}/${pack.name}`
    : `Not downloaded yet: ${pack.files.join(' and ')} from InsightFace's release, a ${mb(pack.downloadBytes)} download. Or copy the files to ${r.modelsDir}/${pack.name}.`;
  const btn = $('#faceDownloadBtn');
  btn.hidden = pack.present && !downloading;
  btn.disabled = downloading;
  btn.textContent = downloading ? `Downloading ${dl.pack}… ${mb(dl.done)} of ${mb(dl.total)}` : `Download ${pack.name} (${mb(pack.downloadBytes)})`;
  const a = r.activity, c = r.counts;
  const label = { off: 'Off', 'no-models': 'Waiting for the model files', downloading: 'Downloading the model files', ready: a.running ? 'Finding faces' : 'Ready', error: 'Stopped by an error' }[a.state] || a.state;
  $('#faceStatus').textContent = [
    `${label}${r.backends ? ' · ' + r.backends : ''}`,
    a.detail ? `  ${a.detail}` : null,
    dl?.error ? `Download of ${dl.pack} failed: ${dl.error}` : null,
    '',
    `Photos: ${fmtNum(c.photos)} · searched ${fmtNum(c.done)} · to do ${fmtNum(c.pending)}${c.failed ? ` · could not be read ${fmtNum(c.failed)}` : ''}`,
    `Faces: ${fmtNum(c.faces)} in ${fmtNum(c.photosWithFaces)} photos`,
    r.lastPass ? `Last grouping (${r.lastPass.finishedAt.replace('T', ' ')}): ${fmtNum(r.lastPass.considered)} unassigned faces, ${fmtNum(r.lastPass.matched)} joined a person, ${fmtNum(r.lastPass.grouped)} formed ${fmtNum(r.lastPass.newPeople)} suggestions` : null,
  ].filter(x => x != null).join('\n');
  $('#faceRetryBtn').disabled = !c.failed;
}
async function loadUsers() { const us = await api('/api/admin/users'); $('#userList').innerHTML = us.map(u => `<li><span>${esc(u.username)}</span><span class="pill">${u.role}</span><span class="muted">${u.passwordHash ? 'password set' : 'no password'}</span></li>`).join(''); }
/** Admin › Albums: the albums with 0 items, and a button to remove them. */
async function loadAlbumAdmin() {
  await loadAlbums();
  if (state.view !== 'admin') return;
  const empty = state.albums.filter(a => a.itemCount === 0);
  const auto = state.albums.filter(a => a.kind === 'auto').length;
  $('#albumAdminInfo').textContent = `${fmtNum(state.albums.length)} album${state.albums.length === 1 ? '' : 's'} (${fmtNum(auto)} from folders), ${fmtNum(empty.length)} empty`;
  const btn = $('#pruneAlbumsBtn');
  btn.disabled = !empty.length;
  btn.textContent = empty.length ? `Remove ${fmtNum(empty.length)} empty album${empty.length === 1 ? '' : 's'}` : 'No empty albums';
  $('#emptyAlbumTable tbody').innerHTML = empty.map(a => `<tr><td><a href="#/album/${a.id}">${esc(a.name)}</a></td>
      <td>${a.kind === 'auto' ? 'From a folder' : 'By hand'}</td><td>${albumDates(a) || '—'}</td><td>${fmtDay(a.createdAt)}</td></tr>`).join('')
    || '<tr><td colspan="4" class="muted">Every album has something in it.</td></tr>';
}
async function pruneAlbums() {
  const n = state.albums.filter(a => a.itemCount === 0).length;
  if (!n || !confirm(`Remove ${n} empty album${n === 1 ? '' : 's'}? Photos and files stay as they are.`)) return;
  const r = await api('/api/albums/prune', { method: 'POST' });
  toast(`Removed ${fmtNum(r.removed)} empty album${r.removed === 1 ? '' : 's'}`);
  await loadAlbumAdmin();
}
async function regenerateAutoAlbums() {
  const n = state.albums.filter(a => a.kind === 'auto').length;
  if (!confirm(`Delete ${n ? `all ${fmtNum(n)}` : 'the'} albums made from folders and make them again, one per folder? Albums you made by hand are left alone. `
    + 'Albums you hid from viewers stay hidden; photos added to them by hand, and links to them, are lost.')) return;
  const btn = $('#regenAlbumsBtn'); btn.disabled = true;
  try {
    const r = await api('/api/albums/auto/regenerate', { method: 'POST', body: { gapDays: 3 } });
    toast(`Auto-albums regenerated: ${fmtNum(r.removed)} removed, ${fmtNum(r.created)} made`
      + (r.keptHidden ? `, ${fmtNum(r.keptHidden)} kept hidden from viewers` : '') + leftOut(r));
    showUnplaced(r);
  } catch (err) { toast(err.message); }
  finally { btn.disabled = false; }
  await loadAlbumAdmin();
}
async function loadDups() {
  const rows = await api('/api/admin/duplicates?limit=300');
  $('#dupTable tbody').innerHTML = rows.map(r => `<tr><td>${r.copies}</td><td>${fmtBytes(r.size)}</td><td>${fmtBytes(r.wasted)}</td><td class="paths"><a href="#/item/${r.item_id}">${esc(r.paths)}</a></td></tr>`).join('') || '<tr><td colspan=4 class="muted">No duplicates found</td></tr>';
}
// ---------- memory (admin) ----------
// The server's memory report (MemoryReport.cs), refreshed every 5 s while the Memory tab is open.
let memoryTimer = 0;
function memoryPolling(on) {
  clearInterval(memoryTimer); memoryTimer = 0;
  if (!on) return;
  loadMemory();
  memoryTimer = setInterval(() => { if (!$('.tab[data-tab="memory"]') || $('.tab[data-tab="memory"]').hidden) memoryPolling(false); else loadMemory(); }, 5000);
}
async function loadMemory() {
  try { renderMemory(await api('/api/admin/memory', { quiet: true })); } catch { }
}
function renderMemory(m, before) {
  const box = $('#memReport'); if (!box) return;
  const was = before ? new Map(before.sections.flatMap(s => s.lines.map(l => [s.title + '|' + l.label, l]))) : null;
  const amount = (l, title) => {
    if (l.bytes == null) return esc(l.text ?? '');
    const old = was?.get(title + '|' + l.label)?.bytes;
    const change = old != null && Math.abs(old - l.bytes) >= 1048576 ? ` <span class="${l.bytes < old ? 'mem-down' : 'mem-up'}">${l.bytes < old ? '−' : '+'}${fmtBytes(Math.abs(old - l.bytes))}</span>` : '';
    return `${fmtBytes(l.bytes)}${l.text ? ` · ${esc(l.text)}` : ''}${change}`;
  };
  const share = (n) => m.resident ? `${Math.round(n * 100 / m.resident)} %` : '';
  box.innerHTML = `<div class="mem-bar" title="Resident memory: the .NET heap and the rest">
      <span style="flex:${m.dotNet}" class="mem-dotnet"></span><span style="flex:${m.outside}" class="mem-outside"></span></div>
    <div class="mem-legend"><b>${fmtBytes(m.resident)}</b> resident · <span class="mem-dotnet-key"></span>.NET heap ${fmtBytes(m.dotNet)} (${share(m.dotNet)})
      · <span class="mem-outside-key"></span>outside it ${fmtBytes(m.outside)} (${share(m.outside)})</div>
    ${m.sections.map(s => `<h3>${esc(s.title)}</h3>${s.note ? `<p class="muted small">${esc(s.note)}</p>` : ''}
      <table class="table mem-table"><tbody>${s.lines.map(l => `<tr><td>${esc(l.label)}</td><td class="num">${amount(l, s.title)}</td>
        <td class="muted">${esc(l.note ?? '')}</td></tr>`).join('')}</tbody></table>`).join('')}
    <p class="muted small">As of ${esc(m.at.replace('T', ' '))}.</p>`;
}
async function freeMemory() {
  const b = $('#memFreeBtn'), info = $('#memInfo');
  b.disabled = true; info.textContent = 'Freeing memory…';
  try {
    const r = await api('/api/admin/memory/free', { method: 'POST' });
    const freed = r.before.resident - r.after.resident;
    info.textContent = `${freed > 0 ? `${fmtBytes(freed)} freed` : 'Nothing more to free'}: ${r.done.join(', ')}. Changes are shown beside each amount.`;
    renderMemory(r.after, r.before);
    memoryPolling(false); // keep the comparison on screen; it refreshes again when the tab is opened
  } catch (e) { info.textContent = e.message; }
  finally { b.disabled = false; }
}

async function loadTranscodes() {
  const [rows, st] = await Promise.all([api('/api/admin/transcodes'), api('/api/status', { quiet: true }).catch(() => null)]);
  $('#tcIdle').textContent = idleText(st?.transcodes?.idle);
  $('#tcTable tbody').innerHTML = rows.map(r => `<tr><td><a href="#/item/${r.assetId}">#${r.assetId}</a></td><td class="paths">${esc(r.path)}</td><td>${fmtBytes(r.size)}</td><td><button class="ghost small danger" data-del="${r.assetId}">Delete</button></td></tr>`).join('') || '<tr><td colspan=4 class="muted">No saved transcodes</td></tr>';
  $$('#tcTable [data-del]').forEach(b => b.onclick = async () => { await api(`/api/admin/transcodes/${b.dataset.del}`, { method: 'DELETE' }); loadTranscodes(); });
}
/** Admin › Transcodes: what converting while idle is doing. */
function idleText(idle) {
  const n = v => `${fmtNum(v)} video${v === 1 ? '' : 's'}`;
  switch (idle?.state) {
    case 'converting': return `Converting ${idle.file} while idle, at the lowest priority · ${n(idle.left)} still to convert.`;
    case 'waiting': return `${idle.left ? n(idle.left) + ' to convert while idle; ' : ''}waiting: ${idle.waiting}.`;
    case 'done': return 'Converting while idle: every video that browsers can\'t play as it is has a saved copy.';
    default: return 'Converting while idle is off (Settings). Videos are converted when someone plays them.';
  }
}

// ---------- boot ----------
initEditor();
async function bootstrapData() { await Promise.all([loadAlbums(), loadYears(), loadTagKeys(), loadPeople()]); pollStatus(); }
(async () => {
  await loadMe();
  if (state.me.role) { try { await bootstrapData(); } catch { } }
  route();
  if (!state.me.role) showLogin();
})();
