// Photo editor worker: runs Gallery.Develop (WebAssembly, built into /editor) off the page's thread.
// Messages in: {id, type: 'load', url} | {id, type: 'fit', w, h} | {id, type: 'render', settings (JSON), quick, whole}
//   | {id, type: 'loadDetail', url, scale} | {id, type: 'renderDetail', settings}.
// Messages out: {id, type: 'loaded', header} | {id, type: 'fitted', w, h} | {id, type: 'frame', w, h, viewW, viewH, pixels, histogram, ms}
//   | {id, type: 'detailLoaded', w, h} | {id, type: 'detailSkipped'} | {id, type: 'frame', w, h, scale, detail: true, pixels, ms}
//   | {id, type: 'error', message}.
import { dotnet } from './editor/_framework/dotnet.js';

const ready = (async () => {
  const { getAssemblyExports, getConfig } = await dotnet.create();
  const exports = await getAssemblyExports(getConfig().mainAssemblyName);
  return exports.Gallery.Develop.Wasm.EditorApi;
})();

let size = { w: 0, h: 0 };
// The crop for a zoomed view and the screen pixels each of its pixels takes (1 for 100%, 2 for 200%), echoed with its frames.
let detail = null, detailRequests = 0;

async function problem(r) {
  const text = await r.text();
  try { const j = JSON.parse(text); return j.detail || j.title || text; } catch { return text || r.statusText; }
}

self.onmessage = async e => {
  const m = e.data;
  try {
    const api = await ready;
    if (m.type === 'load') {
      const r = await fetch(m.url, { credentials: 'same-origin' });
      if (!r.ok) throw new Error(await problem(r));
      const bytes = new Uint8Array(await r.arrayBuffer()); // the browser has already gunzipped it
      const header = JSON.parse(api.Load(bytes));
      size = { w: header.width, h: header.height };
      self.postMessage({ id: m.id, type: 'loaded', header, bytes: bytes.length });
    } else if (m.type === 'fit') {
      const [w, h] = api.Fit(m.w, m.h).split(',').map(Number);
      size = { w, h };
      self.postMessage({ id: m.id, type: 'fitted', w, h });
    } else if (m.type === 'loadDetail') {
      const n = ++detailRequests;
      const r = await fetch(m.url, { credentials: 'same-origin' });
      if (!r.ok) throw new Error(await problem(r));
      const bytes = new Uint8Array(await r.arrayBuffer());
      // Crops can arrive out of order: one asked for since replaces this one.
      if (n !== detailRequests) { self.postMessage({ id: m.id, type: 'detailSkipped' }); return; }
      const [w, h] = api.LoadDetail(bytes).split(',').map(Number);
      detail = { w, h, scale: m.scale || 1 };
      self.postMessage({ id: m.id, type: 'detailLoaded', w, h });
    } else if (m.type === 'renderDetail') {
      const t0 = performance.now();
      const pixels = new Uint8ClampedArray(api.RenderDetail(m.settings));
      self.postMessage({ id: m.id, type: 'frame', ...detail, detail: true, pixels: pixels.buffer, ms: performance.now() - t0 }, [pixels.buffer]);
    } else if (m.type === 'render') {
      const t0 = performance.now();
      // whole: the picture uncropped and not turned, while the crop is being set; viewW × viewH is the full frame's size.
      const rendered = api.Render(m.settings, !!m.quick, !!m.whole);
      const [w, h] = api.LastSize().split(',').map(Number), [viewW, viewH] = api.ViewSize().split(',').map(Number);
      const pixels = new Uint8ClampedArray(rendered); // a copy to transfer to the page; WASM memory itself can't be transferred
      const histogram = api.LastHistogram();
      self.postMessage({ id: m.id, type: 'frame', w, h, viewW, viewH, quick: !!m.quick, whole: !!m.whole, pixels: pixels.buffer, histogram, ms: performance.now() - t0 }, [pixels.buffer]);
    }
  } catch (err) {
    self.postMessage({ id: m.id, type: 'error', message: String(err?.message || err) });
  }
};
