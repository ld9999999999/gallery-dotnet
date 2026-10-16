// Photo editor (docs/raw-editor.md, phase 3): sliders, histogram, RGB curves, crop and straighten, before/after, 4-step undo, save.
// Gallery.Develop renders the picture in a Web Worker (develop-worker.js) from a linear editing copy.

const DEFAULTS = Object.freeze({
  version: 1,
  whiteBalance: { temperature: 0, tint: 0 },
  exposure: 0, contrast: 0, brightness: 0, highlights: 0, shadows: 0, saturation: 0, vibrance: 0,
  curves: { rgb: [[0, 0], [1, 1]], red: [[0, 0], [1, 1]], green: [[0, 0], [1, 1]], blue: [[0, 0], [1, 1]] },
  noiseReduction: { luminance: 0, color: 25 },
  sharpening: { amount: 40, radius: 1, masking: 0 },
  crop: { angle: 0, left: 0, top: 0, right: 1, bottom: 1 },
});
const DEFAULT_JSON = JSON.stringify(DEFAULTS);
const UNDO_STEPS = 4;

const signed = v => (v > 0 ? '+' : '') + Math.round(v);
const SLIDERS = [
  { group: 'White balance', key: 'whiteBalance.temperature', label: 'Temperature', min: -100, max: 100, step: 1, fmt: signed },
  { key: 'whiteBalance.tint', label: 'Tint', min: -100, max: 100, step: 1, fmt: signed },
  { group: 'Tone', key: 'exposure', label: 'Exposure', min: -5, max: 5, step: 0.05, fmt: v => (v > 0 ? '+' : '') + v.toFixed(2) },
  { key: 'contrast', label: 'Contrast', min: -100, max: 100, step: 1, fmt: signed },
  { key: 'brightness', label: 'Brightness', min: -100, max: 100, step: 1, fmt: signed },
  { key: 'highlights', label: 'Highlights', min: -100, max: 100, step: 1, fmt: signed },
  { key: 'shadows', label: 'Shadows', min: -100, max: 100, step: 1, fmt: signed },
  { group: 'Colour', key: 'saturation', label: 'Saturation', min: -100, max: 100, step: 1, fmt: signed },
  { key: 'vibrance', label: 'Vibrance', min: -100, max: 100, step: 1, fmt: signed },
  { group: 'Detail', key: 'noiseReduction.luminance', label: 'Noise reduction', min: 0, max: 100, step: 1, fmt: v => Math.round(v) },
  { key: 'noiseReduction.color', label: 'Colour noise reduction', min: 0, max: 100, step: 1, fmt: v => Math.round(v) },
  { key: 'sharpening.amount', label: 'Sharpening', min: 0, max: 150, step: 1, fmt: v => Math.round(v) },
  { key: 'sharpening.radius', label: 'Sharpening radius', min: 0.5, max: 3, step: 0.1, fmt: v => (+v).toFixed(1) },
  { key: 'sharpening.masking', label: 'Sharpening masking', min: 0, max: 100, step: 1, fmt: v => Math.round(v) },
];
const CHANNELS = [['rgb', 'RGB', '#e6e8ee'], ['red', 'Red', '#ff6b6b'], ['green', 'Green', '#5fd068'], ['blue', 'Blue', '#5b9dff']];

const $ = s => document.querySelector(s);
const clone = o => JSON.parse(JSON.stringify(o));
const get = (o, path) => path.split('.').reduce((v, k) => v[k], o);
const set = (o, path, value) => { const ks = path.split('.'); const last = ks.pop(); ks.reduce((v, k) => v[k], o)[last] = value; };

let worker = null, nextId = 1;
const waiting = new Map();
function call(msg) {
  if (!worker) {
    worker = new Worker('/develop-worker.js', { type: 'module' });
    worker.onmessage = e => { const p = waiting.get(e.data.id); if (!p) return; waiting.delete(e.data.id); e.data.type === 'error' ? p.reject(new Error(e.data.message)) : p.resolve(e.data); };
    worker.onerror = e => { for (const p of waiting.values()) p.reject(new Error(e.message || 'the editor could not start')); waiting.clear(); };
  }
  const id = nextId++;
  return new Promise((resolve, reject) => { waiting.set(id, { resolve, reject }); worker.postMessage({ ...msg, id }); });
}

let ed = null;
let generations = 0; // numbers each opening, so late answers for an earlier picture are ignored

/**
 * Opens the editor over the page. host: { api(path, opts), toast(msg), title, close() }; close() leaves the editor
 * (the app pops the history entry).
 */
export async function openEditor(assetId, host) {
  if (ed?.assetId === assetId) return;
  if (ed) closeEditor();
  const root = $('#editor');
  ed = {
    assetId, host, settings: clone(DEFAULTS), defaults: clone(DEFAULTS), savedJson: DEFAULT_JSON, undo: [], redo: [], dragFrom: null, zoom: null,
    hasDetail: false, fitted: null, loaded: false, busy: false, wanted: null, compare: false, channel: 'rgb', drag: null, frame: null, lastHist: null, cropping: null,
    generation: ++generations,
  };
  const gen = ed.generation;
  root.hidden = false;
  document.body.classList.add('editing');
  $('#edTitle').textContent = host.title || 'Edit photo';
  for (const id of ['#edSub', '#edTiming', '#edClip']) $(id).textContent = '';
  const hist = $('#edHist'); hist.getContext('2d').clearRect(0, 0, hist.width, hist.height);
  const canvas = $('#edCanvas'); canvas.width = canvas.height = 0;
  $('#edCropPanel').hidden = $('#edCropBox').hidden = true;
  buildControls();
  message('Opening the editing copy…');
  updateButtons();
  try {
    const [info, saved] = await Promise.all([host.api(`/api/assets/${assetId}/develop`, { quiet: true }), host.api(`/api/assets/${assetId}/edit`, { quiet: true })]);
    if (ed?.generation !== gen) return;
    ed.info = info;
    ed.settings = saved.settings;
    ed.defaults = saved.defaults; // where this photo starts (RAWs: some noise reduction and sharpening)
    ed.savedJson = JSON.stringify(saved.settings);
    ed.hasSaved = saved.saved;
    $('#edSub').textContent = [info.camera, `${info.width} × ${info.height}`, info.iso ? `ISO ${info.iso}` : null, info.shutter, info.aperture].filter(Boolean).join(' · ');
    syncControls();
    loadRecipes();
    const wide = Math.max(innerWidth, innerHeight) * devicePixelRatio > 1800 && !matchMedia('(max-width: 900px)').matches;
    const size = info.sizes.find(s => s >= (wide ? 2560 : 1600)) ?? info.sizes[info.sizes.length - 1];
    message(info.sourceKind === 'raw' ? 'Decoding the RAW file…' : 'Preparing the photo…');
    const loaded = await call({ type: 'load', url: `/api/assets/${assetId}/develop/source?size=${size}` });
    if (ed?.generation !== gen) return;
    ed.loaded = true;
    ed.header = loaded.header;
    await fit();
    message('');
    render();
    updateButtons();
  } catch (e) {
    if (ed?.generation === gen) message(e.message.includes('wasm') || e.message.includes('editor') ? `The editor could not start: ${e.message}` : e.message, true);
  }
}

export function closeEditor() {
  if (!ed) return;
  for (const d of document.querySelectorAll('#editor .ed-dialog')) d.hidden = true;
  ed = null;
  $('#editor').hidden = true;
  document.body.classList.remove('editing');
  const c = $('#edCanvas');
  c.width = c.height = 0;
  pan = null; c.style.transform = c.style.cursor = '';
  $('#edCropPanel').hidden = $('#edCropBox').hidden = true;
}

export const editorOpen = () => !!ed;

/** Leaves from the editor's own controls, asking first if there are unsaved changes. */
function leave() {
  if (!ed) return;
  if (dirty() && !confirm('Leave without saving your changes?')) return;
  ed.host.close();
}

function message(text, error = false) {
  const m = $('#edMsg');
  m.textContent = text;
  m.hidden = !text;
  m.classList.toggle('error', error);
}

const dirty = () => ed && JSON.stringify(ed.settings) !== ed.savedJson;

// ---------- rendering ----------
/** Room for the picture inside the stage's padding, in whole device pixels, so frames never need rescaling to fit. */
function stagePixels() {
  const stage = $('#edStage'), s = getComputedStyle(stage);
  const w = stage.clientWidth - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight);
  const h = stage.clientHeight - parseFloat(s.paddingTop) - parseFloat(s.paddingBottom);
  return [Math.max(64, Math.floor(w * devicePixelRatio)), Math.max(64, Math.floor(h * devicePixelRatio))];
}

async function fit() {
  const [w, h] = stagePixels();
  const r = await call({ type: 'fit', w, h });
  if (!ed) return;
  const c = $('#edCanvas');
  c.width = r.w; c.height = r.h;
  // Fixed display size, so half-size frames while dragging are simply scaled up.
  ed.fitted = { w: r.w, h: r.h };
  c.style.width = `${r.w / devicePixelRatio}px`; c.style.height = `${r.h / devicePixelRatio}px`;
  ed.frame = null;
}

/**
 * Requests a render of the current settings; once the worker is free, only the latest request runs. While a slider or
 * curve point is being dragged (quick), frames are half size, and a full-size frame follows on release.
 */
function render(quick = false) {
  if (!ed?.loaded || !ed.fitted) return;
  ed.wanted = { settings: ed.compare ? JSON.stringify(ed.defaults) : JSON.stringify(ed.settings), quick: quick && !ed.zoom, whole: !!ed.cropping };
  pump();
}

async function pump() {
  if (!ed || ed.busy || ed.wanted === null) return;
  const { settings, quick, whole } = ed.wanted, gen = ed.generation;
  ed.wanted = null;
  ed.busy = true;
  try {
    // A zoomed view keeps its current crop on screen while the next one loads (the worker says which it drew).
    const f = ed.zoom && ed.hasDetail ? await call({ type: 'renderDetail', settings }) : await call({ type: 'render', settings, quick, whole });
    if (ed?.generation !== gen) return;
    if (!f.detail && f.whole !== !!ed.cropping) return; // crop mode changed meanwhile; the next frame is the right one
    const c = $('#edCanvas');
    if (c.width !== f.w || c.height !== f.h) { c.width = f.w; c.height = f.h; }
    // The whole picture is shown at its fitted size (half-size frames scaled up); a crop at 1 or 2 screen pixels per
    // picture pixel, with sharp squares at 200%. The fitted size changes with the crop.
    if (!f.detail) ed.fitted = { w: f.viewW, h: f.viewH };
    const [w, h] = f.detail ? [f.w * f.scale, f.h * f.scale] : [ed.fitted.w, ed.fitted.h];
    c.style.width = `${w / devicePixelRatio}px`; c.style.height = `${h / devicePixelRatio}px`;
    c.classList.toggle('pixelated', !!f.detail && f.scale > 1);
    if (ed.cropping) layoutCrop(); // turned under the crop frame
    else if (!pan) c.style.transform = ''; // a new frame replaces whatever the last drag shifted
    c.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(f.pixels), f.w, f.h), 0, 0);
    if (f.histogram) { ed.lastHist = f.histogram; drawHistogram(f.histogram); }
    $('#edBefore').hidden = !ed.compare;
    $('#edTiming').textContent = `${f.w}×${f.h}${f.detail ? ` at ${f.scale * 100}%` : ''} in ${Math.round(f.ms)} ms`;
  } catch (e) {
    if (ed?.generation === gen) message(e.message, true);
  } finally {
    if (ed?.generation === gen) { ed.busy = false; pump(); }
  }
}

function drawHistogram(h) {
  const c = $('#edHist'), g = c.getContext('2d');
  const W = c.width, H = c.height;
  g.clearRect(0, 0, W, H);
  let max = 1;
  for (let i = 1; i < 255; i++) max = Math.max(max, h[768 + i], h[i], h[256 + i], h[512 + i]);
  const y = v => H - Math.min(1, Math.sqrt(v / max)) * (H - 2);
  const plot = (offset, fill) => {
    g.beginPath(); g.moveTo(0, H);
    for (let i = 0; i < 256; i++) g.lineTo(i / 255 * W, y(h[offset + i]));
    g.lineTo(W, H); g.closePath(); g.fillStyle = fill; g.fill();
  };
  plot(768, 'rgba(160,165,180,.35)');
  g.globalCompositeOperation = 'lighter';
  plot(0, 'rgba(200,60,60,.45)'); plot(256, 'rgba(60,170,70,.45)'); plot(512, 'rgba(60,110,220,.45)');
  g.globalCompositeOperation = 'source-over';
  const pixels = h[1024] || 1, pct = n => (n * 100 / pixels).toFixed(n * 100 / pixels < 1 ? 2 : 1);
  $('#edClip').textContent = `Clipped highlights ${pct(h[1025])} % · shadows ${pct(h[1026])} %`;
}

// ---------- controls ----------
function buildControls() {
  const box = $('#edControls');
  box.innerHTML = '';
  for (const s of SLIDERS) {
    if (s.group) box.insertAdjacentHTML('beforeend', `<h4>${s.group}</h4>`);
    box.insertAdjacentHTML('beforeend', `<label class="ed-slider"><span>${s.label}</span><output data-out="${s.key}"></output>
      <input type="range" min="${s.min}" max="${s.max}" step="${s.step}" data-key="${s.key}"></label>`);
  }
  for (const input of box.querySelectorAll('input[type=range]')) {
    const s = SLIDERS.find(x => x.key === input.dataset.key);
    input.addEventListener('input', () => {
      if (!ed) return;
      ed.dragFrom ??= clone(ed.settings);
      set(ed.settings, s.key, +input.value);
      box.querySelector(`[data-out="${s.key}"]`).textContent = s.fmt(+input.value);
      render(true);
    });
    input.addEventListener('change', () => { commitDrag(); render(); });
  }
  const tabs = $('#edCurveTabs');
  tabs.innerHTML = CHANNELS.map(([k, label]) => `<button class="ghost small${k === 'rgb' ? ' on' : ''}" data-ch="${k}">${label}</button>`).join('');
  for (const b of tabs.querySelectorAll('button')) b.onclick = () => {
    ed.channel = b.dataset.ch;
    for (const x of tabs.querySelectorAll('button')) x.classList.toggle('on', x === b);
    drawCurve();
  };
}

// ---------- recipes ----------
const SECTION_FIELDS = {
  whiteBalance: ['whiteBalance'],
  tone: ['exposure', 'contrast', 'brightness', 'highlights', 'shadows'],
  color: ['saturation', 'vibrance'],
  curves: ['curves'],
  detail: ['noiseReduction', 'sharpening'],
};
let recipes = [];

async function loadRecipes() {
  if (!ed) return;
  try { recipes = await ed.host.api('/api/recipes', { quiet: true }); } catch { recipes = []; }
  const sel = $('#edRecipe'), keep = sel.value;
  sel.innerHTML = '<option value="">Choose a recipe…</option>' + recipes.map(r => `<option value="${r.id}">${escapeHtml(r.name)}</option>`).join('');
  sel.value = recipes.some(r => String(r.id) === keep) ? keep : '';
  $('#edRecipeApply').disabled = !sel.value;
}

const escapeHtml = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/** The recipe's sections replace those of the current settings: one undo step. */
function applyRecipe() {
  const r = recipes.find(x => String(x.id) === $('#edRecipe').value);
  if (!ed?.loaded || !r) return;
  const before = clone(ed.settings);
  for (const section of r.sections) for (const field of SECTION_FIELDS[section] || []) ed.settings[field] = clone(r.settings[field]);
  commit(before);
  syncControls(); render();
  ed.host.toast(`Applied "${r.name}"`);
}

function openRecipeForm() {
  const f = $('#edRecipeForm');
  f.reset(); f.querySelector('[data-state]').textContent = '';
  f.hidden = false;
  f.elements.name.focus();
}

async function saveRecipe(e) {
  e.preventDefault();
  const f = $('#edRecipeForm'), state = f.querySelector('[data-state]');
  const sections = Object.keys(SECTION_FIELDS).filter(k => f.elements[k].checked);
  try {
    await ed.host.api('/api/recipes', { method: 'POST', body: { name: f.elements.name.value, settings: ed.settings, sections } });
    f.hidden = true;
    ed.host.toast(`Recipe "${f.elements.name.value.trim()}" saved`);
    await loadRecipes();
  } catch (err) { state.textContent = err.message; }
}

async function openRecipeList() {
  await loadRecipes();
  const box = $('#edRecipeList'), list = box.querySelector('[data-list]');
  list.innerHTML = recipes.length ? recipes.map(r => `<div class="ed-recipe-row" data-id="${r.id}"><span title="${escapeHtml(r.sections.join(', '))}">${escapeHtml(r.name)}</span>
      <button class="ghost small" data-rename>Rename</button><button class="ghost small danger" data-delete>Delete</button></div>`).join('')
    : '<div class="muted small">No recipes yet. Use "Save as recipe…" to keep the current settings.</div>';
  for (const row of list.querySelectorAll('[data-id]')) {
    const r = recipes.find(x => String(x.id) === row.dataset.id);
    row.querySelector('[data-rename]').onclick = async () => {
      const name = prompt('Recipe name', r.name);
      if (!name || name === r.name) return;
      try { await ed.host.api(`/api/recipes/${r.id}`, { method: 'PUT', body: { name } }); await openRecipeList(); } catch (err) { ed.host.toast(err.message); }
    };
    row.querySelector('[data-delete]').onclick = async () => {
      if (!confirm(`Delete the recipe "${r.name}"? Photos it was applied to keep their edits.`)) return;
      await ed.host.api(`/api/recipes/${r.id}`, { method: 'DELETE' });
      await openRecipeList();
    };
  }
  box.hidden = false;
}

function syncControls() {
  for (const input of document.querySelectorAll('#edControls input[type=range]')) {
    const s = SLIDERS.find(x => x.key === input.dataset.key);
    const v = get(ed.settings, s.key) ?? 0;
    input.value = v;
    document.querySelector(`#edControls [data-out="${s.key}"]`).textContent = s.fmt(v);
  }
  drawCurve();
  updateCropPanel();
  if (ed?.cropping) layoutCrop();
  updateButtons();
}

function updateButtons() {
  const has = !!ed;
  $('#edUndo').disabled = !has || ed.undo.length === 0;
  $('#edRedo').disabled = !has || ed.redo.length === 0;
  $('#edReset').disabled = !has || JSON.stringify(ed.settings) === JSON.stringify(ed.defaults);
  $('#edZoom').classList.toggle('on', ed?.zoom?.scale === 1);
  $('#edZoom200').classList.toggle('on', ed?.zoom?.scale === 2);
  $('#edCropBtn').classList.toggle('on', !!ed?.cropping);
  $('#edCropBtn').disabled = !ed?.loaded;
  $('#edSave').disabled = !has || !dirty();
  $('#edState').textContent = has && dirty() ? 'Not saved' : has && ed.hasSaved ? 'Saved' : '';
}

// ---------- undo (last 4 changes) ----------
function commit(previous) {
  if (!ed || JSON.stringify(previous) === JSON.stringify(ed.settings)) { updateButtons(); return; }
  ed.undo.push(previous);
  if (ed.undo.length > UNDO_STEPS) ed.undo.shift();
  ed.redo = [];
  updateButtons();
}

function commitDrag() {
  if (!ed?.dragFrom) return;
  const from = ed.dragFrom;
  ed.dragFrom = null;
  commit(from);
}

function undo() {
  if (!ed?.undo.length) return;
  ed.redo.push(clone(ed.settings));
  ed.settings = ed.undo.pop();
  syncControls(); render();
}

function redo() {
  if (!ed?.redo.length) return;
  ed.undo.push(clone(ed.settings));
  ed.settings = ed.redo.pop();
  syncControls(); render();
}

function reset() {
  if (!ed) return;
  const before = clone(ed.settings);
  ed.settings = clone(ed.defaults);
  commit(before);
  syncControls(); render();
}

async function save() {
  if (!ed || !dirty()) return;
  const gen = ed.generation;
  $('#edSave').disabled = true;
  try {
    const r = await ed.host.api(`/api/assets/${ed.assetId}/edit`, { method: 'PUT', body: ed.settings });
    if (ed?.generation !== gen) return;
    ed.savedJson = JSON.stringify(r.settings);
    ed.settings = r.settings;
    ed.hasSaved = true;
    ed.host.toast('Edit saved');
  } catch (e) { ed?.host.toast(`Not saved: ${e.message}`); }
  updateButtons();
}

// ---------- curve editor ----------
/** Monotone cubic through the points (Fritsch–Carlson), as Gallery.Develop draws it; returns y for x in 0..1. */
function spline(points) {
  const n = points.length, x = points.map(p => p[0]), y = points.map(p => p[1]);
  const d = [], m = new Array(n);
  for (let i = 0; i < n - 1; i++) d.push((y[i + 1] - y[i]) / Math.max(1e-9, x[i + 1] - x[i]));
  m[0] = d[0]; m[n - 1] = d[n - 2];
  for (let i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
  for (let i = 0; i < n - 1; i++) {
    if (d[i] === 0) { m[i] = m[i + 1] = 0; continue; }
    const a = m[i] / d[i], b = m[i + 1] / d[i], h = a * a + b * b;
    if (h > 9) { const t = 3 / Math.sqrt(h); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
  }
  return t => {
    if (t <= x[0]) return y[0];
    if (t >= x[n - 1]) return y[n - 1];
    let k = 0; while (k < n - 2 && t > x[k + 1]) k++;
    const w = x[k + 1] - x[k], s = (t - x[k]) / w, s2 = s * s, s3 = s2 * s;
    return Math.min(1, Math.max(0, (2 * s3 - 3 * s2 + 1) * y[k] + (s3 - 2 * s2 + s) * w * m[k] + (-2 * s3 + 3 * s2) * y[k + 1] + (s3 - s2) * w * m[k + 1]));
  };
}

const curveCanvas = () => $('#edCurve');
const PAD = 10;
function toCanvas(p) { const c = curveCanvas(), s = c.width - 2 * PAD; return [PAD + p[0] * s, PAD + (1 - p[1]) * s]; }
function fromEvent(e) {
  const c = curveCanvas(), r = c.getBoundingClientRect(), s = c.width - 2 * PAD;
  const px = (e.clientX - r.left) * c.width / r.width, py = (e.clientY - r.top) * c.height / r.height;
  return [Math.min(1, Math.max(0, (px - PAD) / s)), Math.min(1, Math.max(0, 1 - (py - PAD) / s))];
}

function drawCurve() {
  const c = curveCanvas(), g = c.getContext('2d'), W = c.width, s = W - 2 * PAD;
  g.clearRect(0, 0, W, W);
  g.strokeStyle = '#2f3542'; g.lineWidth = 1;
  for (let i = 0; i <= 4; i++) {
    const v = PAD + i * s / 4;
    g.beginPath(); g.moveTo(v, PAD); g.lineTo(v, PAD + s); g.moveTo(PAD, v); g.lineTo(PAD + s, v); g.stroke();
  }
  if (ed?.lastHist) {
    const h = ed.lastHist; let max = 1; for (let i = 1; i < 255; i++) max = Math.max(max, h[768 + i]);
    g.fillStyle = 'rgba(140,147,167,.18)';
    for (let i = 0; i < 256; i++) { const bh = Math.sqrt(h[768 + i] / max) * s; g.fillRect(PAD + i / 256 * s, PAD + s - bh, s / 256 + 0.5, bh); }
  }
  g.strokeStyle = '#3a4150'; g.beginPath(); g.moveTo(PAD, PAD + s); g.lineTo(PAD + s, PAD); g.stroke();
  if (!ed) return;
  const [, , colour] = CHANNELS.find(c => c[0] === ed.channel);
  const pts = ed.settings.curves[ed.channel];
  const f = spline(pts);
  g.strokeStyle = colour; g.lineWidth = 2 * devicePixelRatio; g.beginPath();
  for (let i = 0; i <= 200; i++) { const [x, y] = toCanvas([i / 200, f(i / 200)]); i ? g.lineTo(x, y) : g.moveTo(x, y); }
  g.stroke();
  g.fillStyle = colour;
  for (const p of pts) { const [x, y] = toCanvas(p); g.beginPath(); g.arc(x, y, 4.5 * devicePixelRatio, 0, Math.PI * 2); g.fill(); }
}

function nearestPoint(e) {
  const pts = ed.settings.curves[ed.channel], c = curveCanvas(), r = c.getBoundingClientRect();
  const px = (e.clientX - r.left) * c.width / r.width, py = (e.clientY - r.top) * c.height / r.height;
  let best = -1, dist = 12 * devicePixelRatio;
  pts.forEach((p, i) => { const [x, y] = toCanvas(p); const d = Math.hypot(x - px, y - py); if (d < dist) { dist = d; best = i; } });
  return best;
}

function wireCurve() {
  const c = curveCanvas();
  const size = () => { const css = Math.min(256, c.parentElement.clientWidth); c.style.width = c.style.height = css + 'px'; c.width = c.height = Math.round(css * devicePixelRatio); drawCurve(); };
  new ResizeObserver(size).observe(c.parentElement);
  c.addEventListener('pointerdown', e => {
    if (!ed?.loaded) return;
    const pts = ed.settings.curves[ed.channel];
    let i = nearestPoint(e);
    const before = clone(ed.settings);
    if (i < 0) {
      const [x, y] = fromEvent(e);
      if (x <= 0.01 || x >= 0.99) return;
      pts.push([x, y]); pts.sort((a, b) => a[0] - b[0]);
      i = pts.findIndex(p => p[0] === x);
      render(); drawCurve();
    }
    ed.drag = { index: i, before };
    c.setPointerCapture(e.pointerId);
  });
  c.addEventListener('pointermove', e => {
    if (!ed?.drag) return;
    const pts = ed.settings.curves[ed.channel], i = ed.drag.index;
    let [x, y] = fromEvent(e);
    if (i === 0) x = 0; else if (i === pts.length - 1) x = 1;
    else x = Math.min(pts[i + 1][0] - 0.01, Math.max(pts[i - 1][0] + 0.01, x));
    pts[i] = [+x.toFixed(4), +y.toFixed(4)];
    drawCurve(); render(true);
  });
  const end = () => { if (!ed?.drag) return; const { before } = ed.drag; ed.drag = null; commit(before); render(); };
  c.addEventListener('pointerup', end);
  c.addEventListener('pointercancel', end);
  c.addEventListener('dblclick', e => {
    if (!ed?.loaded) return;
    const pts = ed.settings.curves[ed.channel], i = nearestPoint(e);
    if (i <= 0 || i >= pts.length - 1) return;
    const before = clone(ed.settings);
    pts.splice(i, 1);
    commit(before); drawCurve(); render();
  });
}

// ---------- 100% and 200% views ----------
const MAX_ZOOM = 2;
let pan = null; // where a drag of a zoomed view started

/**
 * Shows full-resolution pixels around (cx, cy), in full-size coordinates of the cropped picture, at `scale` screen
 * pixels per picture pixel: 1 for 100%, 2 for 200% (default: the current zoom, or 100%). The crop is whatever fills the
 * stage, half as wide and high at 200%. The centre is kept exactly (the crop is in whole pixels), so switching levels
 * doesn't drift.
 */
async function zoomTo(cx, cy, scale = ed?.zoom?.scale ?? 1) {
  if (!ed?.loaded || !ed.info) return;
  scale = Math.min(Math.max(1, scale), MAX_ZOOM);
  const gen = ed.generation, [W, H] = croppedSize(), [sw, sh] = stagePixels();
  const w = Math.min(W, Math.floor(sw / scale), 2400), h = Math.min(H, Math.floor(sh / scale), 1800);
  // Near an edge the crop stops, and its middle becomes the centre.
  cx = Math.min(Math.max(cx, w / 2), W - w / 2);
  cy = Math.min(Math.max(cy, h / 2), H - h / 2);
  const zoom = ed.zoom = { x: Math.round(cx - w / 2), y: Math.round(cy - h / 2), w, h, scale, cx, cy, loaded: false };
  updateButtons();
  if (!ed.hasDetail) message(`Loading the ${scale * 100}% view…`);
  try {
    const c = ed.settings.crop, cut = isWhole(c) ? '' : `&angle=${c.angle}&left=${c.left}&top=${c.top}&right=${c.right}&bottom=${c.bottom}`;
    const r = await call({ type: 'loadDetail', url: `/api/assets/${ed.assetId}/develop/crop?x=${zoom.x}&y=${zoom.y}&w=${w}&h=${h}${cut}`, scale });
    if (ed?.generation !== gen || ed.zoom !== zoom || r.type !== 'detailLoaded') return;
    zoom.loaded = ed.hasDetail = true;
    message('');
    render();
  } catch (e) { if (ed?.generation === gen && ed.zoom === zoom) message(e.message, true); }
}

/** Zooms to 100% (1) or 200% (2), keeping the middle of what is on screen. */
function setZoom(scale) {
  const z = ed?.zoom;
  if (z) zoomTo(z.cx, z.cy, scale);
  else if (ed?.info) { if (ed.cropping) exitCrop(); const [W, H] = croppedSize(); zoomTo(W / 2, H / 2, scale); }
}

async function unzoom() {
  if (!ed?.zoom) return;
  ed.zoom = null;
  ed.hasDetail = false;
  message('');
  updateButtons();
  await fit();
  render();
}

/** A level's button: that view, or the whole picture again when it is already showing. */
const zoomButton = scale => ed?.zoom?.scale === scale ? unzoom() : setZoom(scale);
/** Ctrl/⌘ + and −: whole picture → 100% → 200%, and back. */
const zoomIn = () => { const level = ed?.zoom?.scale ?? 0; if (level < MAX_ZOOM) setZoom(level + 1); };
const zoomOut = () => { const level = ed?.zoom?.scale ?? 0; if (level > 1) setZoom(level - 1); else unzoom(); };

/** Full-size coordinates of a point on the canvas. */
function pointOf(e) {
  const c = $('#edCanvas'), r = c.getBoundingClientRect();
  const fx = (e.clientX - r.left) / r.width, fy = (e.clientY - r.top) / r.height;
  if (ed.zoom) return [ed.zoom.x + fx * ed.zoom.w, ed.zoom.y + fy * ed.zoom.h];
  const [W, H] = croppedSize();
  return [fx * W, fy * H];
}

function wireZoom() {
  const c = $('#edCanvas');
  $('#edZoom').onclick = () => zoomButton(1);
  $('#edZoom200').onclick = () => zoomButton(2);
  c.addEventListener('dblclick', e => { if (!ed?.loaded || ed.cropping) return; ed.zoom ? unzoom() : zoomTo(...pointOf(e), 1); });
  c.addEventListener('pointerdown', e => { if (!ed?.zoom?.loaded) return; pan = { x: e.clientX, y: e.clientY }; c.setPointerCapture(e.pointerId); c.style.cursor = 'grabbing'; });
  c.addEventListener('pointermove', e => { if (pan) c.style.transform = `translate(${e.clientX - pan.x}px, ${e.clientY - pan.y}px)`; });
  const end = e => {
    if (!pan) return;
    const dx = (e.clientX - pan.x) * devicePixelRatio, dy = (e.clientY - pan.y) * devicePixelRatio;
    pan = null; c.style.cursor = '';
    const z = ed?.zoom;
    if (!z || Math.abs(dx) + Math.abs(dy) < 4) { c.style.transform = ''; return; }
    // The picture follows the pointer: half as many picture pixels as screen pixels at 200%.
    zoomTo(z.cx - dx / z.scale, z.cy - dy / z.scale, z.scale);
  };
  c.addEventListener('pointerup', end);
  c.addEventListener('pointercancel', end);
}

// ---------- crop and straighten ----------
// The crop is a screen-aligned rectangle cut from the picture after it is turned about its centre by the angle.
// Gallery.Develop's Geometry does the same for the preview, the 100% view and the export. In crop mode the whole
// picture is shown turned (a CSS transform, so the Straighten slider follows at once) under a frame that can be moved
// and resized but stays inside the picture. The frame is a box { x, y, w, h } in picture pixels (the upright W × H
// picture as edited); the settings store it as fractions of W and H.
const RATIOS = [['free', 'Free'], ['original', 'Original'], ['1:1', 'Square'], ['3:2', '3:2'], ['4:3', '4:3'], ['5:4', '5:4'], ['7:5', '7:5'], ['16:9', '16:9']];
const isWhole = c => !c || (c.angle === 0 && c.left <= 0 && c.top <= 0 && c.right >= 1 && c.bottom >= 1);
const picSize = () => [ed.info.width, ed.info.height];
const boxOf = c => { const [W, H] = picSize(); return { x: c.left * W, y: c.top * H, w: (c.right - c.left) * W, h: (c.bottom - c.top) * H }; };
const lerpBox = (a, b, t) => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, w: a.w + (b.w - a.w) * t, h: a.h + (b.h - a.h) * t });
const minSide = () => { const [W, H] = picSize(); return [0.02 * W, 0.02 * H]; };

function setBox(b) {
  const [W, H] = picSize(), f = v => Math.min(1, Math.max(0, +v.toFixed(6)));
  Object.assign(ed.settings.crop, { left: f(b.x / W), top: f(b.y / H), right: f((b.x + b.w) / W), bottom: f((b.y + b.h) / H) });
}

/** The box's corners in the picture's own axes, measured from its centre (turned back by the angle). */
function cornersBack(b, angle) {
  const [W, H] = picSize(), r = angle * Math.PI / 180, cos = Math.cos(r), sin = Math.sin(r);
  return [[b.x, b.y], [b.x + b.w, b.y], [b.x, b.y + b.h], [b.x + b.w, b.y + b.h]].map(([px, py]) => {
    const dx = px - W / 2, dy = py - H / 2;
    return [cos * dx + sin * dy, -sin * dx + cos * dy];
  });
}
const inside = (b, angle) => { const [W, H] = picSize(); return cornersBack(b, angle).every(([x, y]) => Math.abs(x) <= W / 2 + 0.01 && Math.abs(y) <= H / 2 + 0.01); };

/** The box moved toward the picture's centre and shrunk about its own until it is inside the turned picture (as Geometry.Fit). */
function fitBox(b, angle) {
  const [W, H] = picSize(), r = angle * Math.PI / 180, cos = Math.cos(r), sin = Math.sin(r), hw = W / 2, hh = H / 2;
  const back = (dx, dy) => [cos * dx + sin * dy, -sin * dx + cos * dy];
  let mx = b.x + b.w / 2 - hw, my = b.y + b.h / 2 - hh, [cx, cy] = back(mx, my);
  const k = Math.min(Math.abs(cx) > hw ? hw / Math.abs(cx) : 1, Math.abs(cy) > hh ? hh / Math.abs(cy) : 1);
  mx *= k; my *= k; cx *= k; cy *= k;
  let t = 1;
  for (const [sx, sy] of [[1, 1], [1, -1], [-1, 1], [-1, -1]]) {
    const [ux, uy] = back(sx * b.w / 2, sy * b.h / 2);
    if (ux > 0) t = Math.min(t, (hw - cx) / ux); else if (ux < 0) t = Math.min(t, (hw + cx) / -ux);
    if (uy > 0) t = Math.min(t, (hh - cy) / uy); else if (uy < 0) t = Math.min(t, (hh + cy) / -uy);
  }
  t = Math.max(0, t);
  return { x: hw + mx - b.w * t / 2, y: hh + my - b.h * t / 2, w: b.w * t, h: b.h * t };
}

/** As far from `from` toward `to` as the box can go and stay inside the turned picture. */
function toward(from, to, angle) {
  if (inside(to, angle)) return to;
  let lo = 0, hi = 1;
  for (let i = 0; i < 24; i++) { const t = (lo + hi) / 2; if (inside(lerpBox(from, to, t), angle)) lo = t; else hi = t; }
  return lerpBox(from, to, lo);
}

/** The size of the picture as cropped, in full-size pixels (as Geometry.Size). */
function croppedSize() {
  const c = ed.settings.crop, [W, H] = picSize();
  if (isWhole(c)) return [W, H];
  if (c.angle === 0) {
    const x0 = Math.min(Math.round(c.left * W), W - 1), y0 = Math.min(Math.round(c.top * H), H - 1);
    return [Math.min(Math.max(Math.round(c.right * W), x0 + 1), W) - x0, Math.min(Math.max(Math.round(c.bottom * H), y0 + 1), H) - y0];
  }
  const b = fitBox(boxOf(c), c.angle);
  return [Math.max(1, Math.round(b.w)), Math.max(1, Math.round(b.h))];
}

/** Width ÷ height the frame keeps, the way round the frame is now; null when free. */
function lockedRatio() {
  const c = ed.settings.crop, [W, H] = picSize();
  const m = /^(\d+):(\d+)$/.exec(c.aspect || '');
  let r = c.aspect === 'original' ? W / H : m ? m[1] / m[2] : null;
  if (!r) return null;
  if (r < 1) r = 1 / r;
  const b = boxOf(c);
  return b.w >= b.h ? r : 1 / r;
}

/** The frame dragged by (dx, dy) picture pixels, sliding along the picture's edges. */
function movedBox(s, dx, dy) {
  const a = ed.settings.crop.angle;
  let b = toward(s, { ...s, x: s.x + dx, y: s.y + dy }, a);
  b = toward(b, { ...b, x: s.x + dx }, a);
  return toward(b, { ...b, y: s.y + dy }, a);
}

/** The frame with the edges of `handle` (n, e, s, w or a corner) dragged by (dx, dy) picture pixels. */
function resizedBox(s, handle, dx, dy) {
  const a = ed.settings.crop.angle, [mw, mh] = minSide(), ratio = lockedRatio();
  let l = s.x, t = s.y, r = s.x + s.w, b = s.y + s.h;
  if (handle.includes('w')) l = Math.min(l + dx, r - mw);
  if (handle.includes('e')) r = Math.max(r + dx, l + mw);
  if (handle.includes('n')) t = Math.min(t + dy, b - mh);
  if (handle.includes('s')) b = Math.max(b + dy, t + mh);
  if (!ratio) {
    // Across, then up and down: each stops at the picture's edge on its own.
    const across = toward(s, { ...s, x: l, w: r - l }, a);
    return toward(across, { ...across, y: t, h: b - t }, a);
  }
  let w = r - l, h = b - t, box;
  if (handle.length === 2) {
    // A corner: the side that moved further sets the size; the opposite corner stays.
    const f = Math.abs(w / s.w - 1) >= Math.abs(h / s.h - 1) ? w / s.w : h / s.h;
    w = Math.max(mw, mh * ratio, s.w * f); h = w / ratio;
    box = { x: handle.includes('w') ? s.x + s.w - w : s.x, y: handle.includes('n') ? s.y + s.h - h : s.y, w, h };
  } else if (handle === 'e' || handle === 'w') { h = w / ratio; box = { x: l, y: s.y + (s.h - h) / 2, w, h }; }
  else { w = h * ratio; box = { x: s.x + (s.w - w) / 2, y: t, w, h }; }
  return toward(s, box, a);
}

function enterCrop() {
  if (!ed?.loaded || ed.cropping) return;
  if (ed.zoom) { ed.zoom = null; ed.hasDetail = false; }
  ed.cropping = { drag: null, straighten: null, scale: 1 };
  $('#edCropPanel').hidden = false;
  updateCropPanel(); updateButtons();
  render();
}

function exitCrop() {
  if (!ed?.cropping) return;
  // Enter or Esc in the middle of a drag still makes that drag one undo step.
  const gesture = ed.cropping.drag ?? ed.cropping.straighten;
  ed.cropping = null;
  if (gesture) commit(gesture.before);
  $('#edCropBox').classList.remove('dragging', 'fine');
  $('#edCropPanel').hidden = true;
  $('#edCropBox').hidden = true;
  $('#edCanvas').style.transform = '';
  updateButtons();
  render();
}

/** Turns the shown picture to the angle, small enough that all of it fits, and puts the frame over it. */
function layoutCrop() {
  const frame = $('#edCropBox'), c = $('#edCanvas');
  if (!ed?.cropping || !c.width) { frame.hidden = true; return; }
  const crop = ed.settings.crop, [W, H] = picSize();
  const cw = c.offsetWidth, ch = c.offsetHeight, [rw, rh] = stagePixels().map(v => v / devicePixelRatio);
  const r = crop.angle * Math.PI / 180, cos = Math.abs(Math.cos(r)), sin = Math.abs(Math.sin(r));
  const k = Math.min(1, rw / (cw * cos + ch * sin), rh / (cw * sin + ch * cos));
  c.style.transform = `rotate(${crop.angle}deg) scale(${k})`;
  const s = ed.cropping.scale = k * cw / W, mx = c.offsetLeft + cw / 2, my = c.offsetTop + ch / 2, b = boxOf(crop);
  Object.assign(frame.style, { left: `${mx + (b.x - W / 2) * s}px`, top: `${my + (b.y - H / 2) * s}px`, width: `${b.w * s}px`, height: `${b.h * s}px` });
  frame.hidden = false;
}

function updateCropPanel() {
  if (!ed) return;
  const c = ed.settings.crop;
  $('#edAspect').value = RATIOS.some(([v]) => v === c.aspect) ? c.aspect : 'free';
  $('#edAspectTurn').disabled = c.aspect === '1:1';
  $('#edAngle').value = c.angle;
  $('#edAngleOut').textContent = `${c.angle > 0 ? '+' : ''}${(+c.angle).toFixed(1)}°`;
  $('#edCropReset').disabled = isWhole(c);
}

/** A ratio from the menu: the largest frame of that shape inside the current one, about its middle. */
function setAspect(value) {
  if (!ed?.loaded) return;
  const before = clone(ed.settings), c = ed.settings.crop;
  c.aspect = value;
  const r = lockedRatio();
  if (r) {
    const b = boxOf(c);
    let w = b.w, h = b.h;
    if (w / h > r) w = h * r; else h = w / r;
    setBox(fitBox({ x: b.x + (b.w - w) / 2, y: b.y + (b.h - h) / 2, w, h }, c.angle));
  }
  commit(before); updateCropPanel(); layoutCrop();
}

/** Upright ↔ across: the frame's sides swapped about its middle, shrunk if it no longer fits. */
function turnAspect() {
  if (!ed?.loaded) return;
  const before = clone(ed.settings), c = ed.settings.crop, b = boxOf(c);
  setBox(fitBox({ x: b.x + (b.w - b.h) / 2, y: b.y + (b.h - b.w) / 2, w: b.h, h: b.w }, c.angle));
  commit(before); updateCropPanel(); layoutCrop();
}

/** The whole picture, level; a locked ratio keeps its largest frame. */
function resetCrop() {
  if (!ed?.loaded) return;
  const before = clone(ed.settings);
  ed.settings.crop = { angle: 0, left: 0, top: 0, right: 1, bottom: 1, ...(ed.settings.crop.aspect ? { aspect: ed.settings.crop.aspect } : {}) };
  const r = lockedRatio(), [W, H] = picSize();
  if (r && Math.abs(r - W / H) > 1e-6) { let w = W, h = H; if (w / h > r) w = h * r; else h = w / r; setBox({ x: (W - w) / 2, y: (H - h) / 2, w, h }); }
  commit(before); syncControls(); layoutCrop();
}

function wireCrop() {
  const frame = $('#edCropBox');
  $('#edCropBtn').onclick = () => ed?.cropping ? exitCrop() : enterCrop();
  $('#edCropDone').onclick = exitCrop;
  $('#edCropReset').onclick = resetCrop;
  $('#edAspect').innerHTML = RATIOS.map(([v, label]) => `<option value="${v}">${label}</option>`).join('');
  $('#edAspect').onchange = e => setAspect(e.target.value);
  $('#edAspectTurn').onclick = turnAspect;
  frame.addEventListener('pointerdown', e => {
    if (!ed?.cropping) return;
    e.preventDefault();
    // A frame pushed against an edge can end up a hair outside after rounding for the settings, and then no step counts
    // as inside, so the drag would be stuck. Start from just inside instead.
    const c = ed.settings.crop, box = boxOf(c), start = inside(box, c.angle) ? box : fitBox(box, c.angle);
    ed.cropping.drag = { handle: e.target.dataset.h || null, x: e.clientX, y: e.clientY, start, before: clone(ed.settings) };
    frame.setPointerCapture(e.pointerId);
    frame.classList.add('dragging');
  });
  frame.addEventListener('pointermove', e => {
    const d = ed?.cropping?.drag; if (!d) return;
    const s = ed.cropping.scale, dx = (e.clientX - d.x) / s, dy = (e.clientY - d.y) / s;
    setBox(d.handle ? resizedBox(d.start, d.handle, dx, dy) : movedBox(d.start, dx, dy));
    layoutCrop();
  });
  const end = () => {
    const d = ed?.cropping?.drag; if (!d) return;
    ed.cropping.drag = null;
    frame.classList.remove('dragging');
    commit(d.before); updateCropPanel();
  };
  frame.addEventListener('pointerup', end);
  frame.addEventListener('pointercancel', end);
  // Straighten: the picture turns under the frame, which shrinks about its middle just enough to stay inside.
  // Within one drag it is refitted from where the drag began, so turning back restores the room.
  const angle = $('#edAngle');
  angle.addEventListener('input', () => {
    if (!ed?.cropping) return;
    const c = ed.settings.crop;
    const st = ed.cropping.straighten ??= { before: clone(ed.settings), box: boxOf(c) };
    c.angle = +(+angle.value).toFixed(1);
    setBox(fitBox(st.box, c.angle));
    frame.classList.add('fine');
    updateCropPanel(); layoutCrop();
  });
  angle.addEventListener('change', () => {
    const st = ed?.cropping?.straighten; if (!st) return;
    ed.cropping.straighten = null;
    frame.classList.remove('fine');
    commit(st.before); updateCropPanel();
  });
  // Double-click the slider for level again.
  angle.addEventListener('dblclick', () => { angle.value = 0; angle.dispatchEvent(new Event('input')); angle.dispatchEvent(new Event('change')); });
}

// ---------- export ----------
const EXPORT_PREFS = 'gallery.exportOptions';
function exportForm() { return $('#edExport'); }

function readExportOptions() {
  const f = exportForm().elements;
  const size = f.size.value, value = +f.sizeValue.value || 0;
  return {
    longEdge: size === 'original' || size === 'percent' ? null : size === 'custom' ? value || null : +size,
    percent: size === 'percent' ? Math.min(100, Math.max(1, value || 50)) : null,
    quality: +f.quality.value,
    fullChroma: f.chroma.value === 'full',
    keepLocation: f.keepLocation.checked,
    saveToGallery: f.gallery.checked,
    useAsPrimary: f.gallery.checked && f.primary.checked,
    download: f.download.checked,
  };
}

function syncExportForm() {
  const f = exportForm().elements;
  f.sizeValue.hidden = !['custom', 'percent'].includes(f.size.value);
  f.sizeValue.placeholder = f.size.value === 'percent' ? 'Percent, 1–100' : 'Long edge in pixels';
  f.qualityOut.value = f.quality.value;
  f.primary.disabled = !f.gallery.checked;
  try { localStorage.setItem(EXPORT_PREFS, JSON.stringify({ size: f.size.value, sizeValue: f.sizeValue.value, quality: f.quality.value, chroma: f.chroma.value, keepLocation: f.keepLocation.checked, gallery: f.gallery.checked, download: f.download.checked })); } catch { }
}

let estimateT, estimateSeq = 0;
function estimate() {
  clearTimeout(estimateT);
  const out = $('#edEstimate');
  out.textContent = 'Estimating the file size…';
  estimateT = setTimeout(async () => {
    if (!ed) return;
    const seq = ++estimateSeq;
    try {
      const r = await ed.host.api(`/api/assets/${ed.assetId}/export/estimate`, { method: 'POST', body: { settings: ed.settings, options: readExportOptions() }, quiet: true });
      if (seq === estimateSeq) out.textContent = `About ${(r.bytes / 1048576).toFixed(r.bytes < 10485760 ? 1 : 0)} MB · ${r.width} × ${r.height}`;
    } catch (e) { if (seq === estimateSeq) out.textContent = ''; }
  }, 300);
}

function openExport() {
  if (!ed?.loaded) return;
  const form = exportForm();
  try {
    const p = JSON.parse(localStorage.getItem(EXPORT_PREFS) || 'null');
    if (p) {
      const f = form.elements;
      f.size.value = p.size; f.sizeValue.value = p.sizeValue; f.quality.value = p.quality; f.chroma.value = p.chroma;
      f.keepLocation.checked = p.keepLocation; f.gallery.checked = p.gallery; f.download.checked = p.download;
    }
  } catch { }
  form.elements.primary.checked = false; // not remembered: making an export the main file is a choice each time
  const original = form.elements.size.querySelector('option[value=original]');
  if (ed.info) { const [w, h] = croppedSize(); original.textContent = `${isWhole(ed.settings.crop) ? 'Original' : 'Full size, cropped'} (${w} × ${h})`; }
  $('#edExportState').textContent = '';
  form.hidden = false;
  syncExportForm();
  estimate();
}

async function runExport(e) {
  e.preventDefault();
  if (!ed) return;
  const options = readExportOptions();
  if (!options.saveToGallery && !options.download) { $('#edExportState').textContent = 'Pick the gallery, a download, or both.'; return; }
  const gen = ed.generation, state = $('#edExportState'), button = exportForm().querySelector('button[type=submit]');
  button.disabled = true;
  try {
    if (dirty()) { state.textContent = 'Saving the edit…'; await save(); }
    state.textContent = 'Exporting…';
    let job = await ed.host.api(`/api/assets/${ed.assetId}/export`, { method: 'POST', body: { settings: ed.settings, options } });
    while (job.state === 'queued' || job.state === 'running') {
      await new Promise(r => setTimeout(r, 500));
      job = await ed.host.api(`/api/exports/${job.id}`, { quiet: true });
    }
    if (job.state !== 'done') throw new Error(job.message || 'the export failed');
    const size = `${job.width} × ${job.height}, ${(job.bytes / 1048576).toFixed(1)} MB`;
    if (job.download) { const a = document.createElement('a'); a.href = job.download; a.download = job.fileName; document.body.appendChild(a); a.click(); a.remove(); }
    if (ed?.generation === gen) { exportForm().hidden = true; }
    ed?.host.toast(options.saveToGallery ? `Exported ${size}; added to the photo's files` : `Exported ${size}`);
    if (options.saveToGallery) ed?.host.exported?.(job.resultAssetId);
  } catch (err) {
    if (ed?.generation === gen) state.textContent = `Not exported: ${err.message}`;
  } finally { button.disabled = false; }
}

// ---------- wiring ----------
let resizeT;
export function initEditor() {
  $('#edClose').onclick = leave;
  $('#edUndo').onclick = undo;
  $('#edRedo').onclick = redo;
  $('#edReset').onclick = reset;
  $('#edSave').onclick = save;
  $('#edExportBtn').onclick = () => exportForm().hidden ? openExport() : (exportForm().hidden = true);
  $('#edRecipe').onchange = () => { $('#edRecipeApply').disabled = !$('#edRecipe').value; };
  $('#edRecipeApply').onclick = applyRecipe;
  $('#edRecipeSave').onclick = openRecipeForm;
  $('#edRecipeManage').onclick = openRecipeList;
  $('#edRecipeForm').addEventListener('submit', saveRecipe);
  for (const b of document.querySelectorAll('#edRecipeForm [data-cancel], #edRecipeList [data-cancel]')) b.onclick = () => { b.closest('.ed-dialog').hidden = true; };
  $('#edExportCancel').onclick = () => { exportForm().hidden = true; };
  exportForm().addEventListener('submit', runExport);
  exportForm().addEventListener('input', () => { syncExportForm(); estimate(); });
  const cmp = $('#edCompare');
  const compare = on => { if (!ed || ed.compare === on) return; ed.compare = on; render(); };
  cmp.addEventListener('pointerdown', () => compare(true));
  for (const ev of ['pointerup', 'pointerleave', 'pointercancel']) cmp.addEventListener(ev, () => compare(false));
  wireCurve();
  wireZoom();
  wireCrop();
  addEventListener('keydown', e => {
    if (!ed || e.target.matches?.('input[type=text], input:not([type]), textarea')) return;
    const mod = e.metaKey || e.ctrlKey;
    if (e.key === 'Escape') {
      e.preventDefault();
      const open = [...document.querySelectorAll('#editor .ed-dialog')].find(d => !d.hidden);
      if (open) open.hidden = true; else if (ed.cropping) exitCrop(); else leave();
    }
    else if (e.key === 'Enter' && ed.cropping && !e.target.matches?.('button, select')) { e.preventDefault(); exitCrop(); }
    else if (e.key === 'c' && !mod && !e.target.matches?.('input, select')) { e.preventDefault(); ed.cropping ? exitCrop() : enterCrop(); }
    else if (e.key === '\\') { e.preventDefault(); compare(true); }
    else if (mod && e.key.toLowerCase() === 'z') { e.preventDefault(); e.shiftKey ? redo() : undo(); }
    else if (mod && e.key.toLowerCase() === 's') { e.preventDefault(); save(); }
    else if (mod && (e.key === '=' || e.key === '+')) { e.preventDefault(); zoomIn(); }
    else if (mod && (e.key === '-' || e.key === '_')) { e.preventDefault(); zoomOut(); }
    else if (mod && e.key === '0') { e.preventDefault(); unzoom(); }
    else if (e.key === 'z' && !mod && !e.target.matches?.('input')) { e.preventDefault(); ed.zoom ? unzoom() : setZoom(1); }
  }, true);
  addEventListener('keyup', e => { if (ed && e.key === '\\') compare(false); });
  // A zoomed view reloads a crop sized for the new stage, around the same point.
  addEventListener('resize', () => {
    clearTimeout(resizeT);
    resizeT = setTimeout(async () => { if (!ed?.loaded) return; if (ed.zoom) zoomTo(ed.zoom.cx, ed.zoom.cy); else { await fit(); render(); } }, 150);
  });
  addEventListener('beforeunload', e => { if (dirty()) { e.preventDefault(); e.returnValue = ''; } });
}
