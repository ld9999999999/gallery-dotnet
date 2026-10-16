// Collage maker: up to nine selected photos in one uncropped picture. The server lays them out (Collage.cs) and
// draws the download from the originals; the preview here uses thumbnails.

const $ = (s, el = document) => el.querySelector(s);
const THUMB_SIZES = [256, 512, 1024, 2048];
const DEFAULTS = { width: 3000, spaced: true, gap: 20, background: '#ffffff', bordered: false, border: 12, borderColour: '#ffffff' };
const STORE = 'collageOptions';

function loadOptions() {
  try { return { ...DEFAULTS, ...JSON.parse(localStorage.getItem(STORE) || '{}') }; } catch { return { ...DEFAULTS }; }
}
function saveOptions(o) { try { localStorage.setItem(STORE, JSON.stringify(o)); } catch { /* private window */ } }

/**
 * Opens the collage maker for these items (in the order they were picked; only photos are used).
 * host: { api, modal, thumbUrl, errorText, fmtBytes } from app.js.
 */
export function openCollage(itemIds, host) {
  const o = loadOptions();
  let variant = 0, layout = null, asked = 0, timer = 0;
  host.modal(`<h2>Collage</h2>
    <div class="cl-body">
      <div class="cl-preview"><div class="cl-frame" hidden></div><div class="cl-msg muted">Laying out the photos…</div></div>
      <form class="cl-options" autocomplete="off">
        <label class="cl-field">Width <span><input name="width" type="number" min="100" max="10000" step="10" required> px</span></label>
        <div class="muted small" data-size></div>
        <fieldset class="cl-set"><legend>Spacing</legend>
          <label class="chk"><input type="radio" name="spacing" value="seamless"> Seamless</label>
          <label class="chk"><input type="radio" name="spacing" value="spaced"> Spaced</label>
          <div class="cl-sub" data-for="spaced">
            <label class="cl-field">Gap <span><input name="gap" type="number" min="1" max="2000" step="1" required> px</span></label>
            <label class="cl-field">Background <input name="background" type="color"></label>
          </div>
        </fieldset>
        <fieldset class="cl-set"><legend>Each photo</legend>
          <label class="chk"><input type="radio" name="frame" value="borderless"> Borderless</label>
          <label class="chk"><input type="radio" name="frame" value="bordered"> Bordered</label>
          <div class="cl-sub" data-for="bordered">
            <label class="cl-field">Border <span><input name="border" type="number" min="1" max="2000" step="1" required> px</span></label>
            <label class="cl-field">Colour <input name="borderColour" type="color"></label>
          </div>
        </fieldset>
        <div class="row"><button type="button" class="ghost" data-rearrange title="Another layout of the same photos">Rearrange</button><span class="muted small" data-variant></span></div>
        <div class="row"><button type="button" data-download>Download</button><button type="button" class="ghost" data-close>Close</button></div>
        <div class="small" data-state></div>
      </form>
    </div>`, 'collage');
  const box = $('#modalBox'), form = $('.cl-options', box), frame = $('.cl-frame', box), msg = $('.cl-msg', box);
  /** Whether this dialog is still on screen (another dialog replaces its contents). */
  const open = () => frame.isConnected && !$('#modal').hidden;
  const tiles = new Map(); // item id -> its element in the preview, kept so a new layout moves photos instead of reloading them

  form.width.value = o.width; form.gap.value = o.gap; form.border.value = o.border;
  form.background.value = o.background; form.borderColour.value = o.borderColour;
  form.spacing.value = o.spaced ? 'spaced' : 'seamless';
  form.frame.value = o.bordered ? 'bordered' : 'borderless';

  /** The request body, or null while a number is invalid. */
  function request() {
    for (const f of [form.width, form.gap, form.border]) if (!f.checkValidity()) return null;
    return {
      itemIds, width: o.width, gap: o.spaced ? o.gap : 0, border: o.bordered ? o.border : 0, variant,
      background: o.background, borderColour: o.borderColour,
    };
  }

  function readForm() {
    o.width = +form.width.value; o.gap = +form.gap.value; o.border = +form.border.value;
    o.background = form.background.value; o.borderColour = form.borderColour.value;
    o.spaced = form.spacing.value === 'spaced'; o.bordered = form.frame.value === 'bordered';
    $('[data-for=spaced]', form).hidden = !o.spaced;
    $('[data-for=bordered]', form).hidden = !o.bordered;
    saveOptions(o);
  }

  async function lay() {
    clearTimeout(timer);
    const body = request();
    if (!body) { say('Width 100 to 10000 px; gap and border 1 to 2000 px.'); return; }
    const mine = ++asked;
    try {
      const r = await host.api('/api/collage/layout', { method: 'POST', body });
      if (mine !== asked || !open()) return;
      layout = r; variant = r.variant;
      draw();
    } catch (e) {
      if (mine === asked) { layout = null; say(e.message); }
    }
  }

  function say(text) {
    msg.textContent = text; msg.hidden = false; frame.hidden = true;
    $('[data-size]', form).textContent = ''; $('[data-variant]', form).textContent = '';
    $('[data-download]', form).disabled = true; $('[data-rearrange]', form).disabled = true;
  }

  /** Draws the layout as large as the dialog allows. Photos keep their elements, so Rearrange moves them. */
  function draw() {
    if (!layout || !open()) return;
    const preview = $('.cl-preview', box);
    const maxH = innerWidth < 720 ? innerHeight * 0.45 : Math.min(innerHeight * 0.75, 900);
    const s = Math.min(preview.clientWidth / layout.width, maxH / layout.height);
    const px = v => Math.round(v * s);
    frame.hidden = false; msg.hidden = true;
    frame.style.width = `${px(layout.width)}px`; frame.style.height = `${px(layout.height)}px`;
    frame.style.background = o.spaced ? o.background : o.bordered ? o.borderColour : '#000';
    const b = o.bordered ? o.border : 0;
    const shown = new Set();
    for (const t of layout.tiles) {
      shown.add(t.itemId);
      let el = tiles.get(t.itemId);
      if (!el) {
        el = document.createElement('div'); el.className = 'cl-tile'; el.innerHTML = '<img alt="">';
        frame.appendChild(el); tiles.set(t.itemId, el);
      }
      // Edges rounded, not sizes, so photos that touch leave no hairline between them.
      const x0 = px(t.x - b), y0 = px(t.y - b), x1 = px(t.x + t.width + b), y1 = px(t.y + t.height + b);
      Object.assign(el.style, { left: `${x0}px`, top: `${y0}px`, width: `${x1 - x0}px`, height: `${y1 - y0}px`,
        padding: `${b * s}px`, background: b ? o.borderColour : 'transparent' });
      const img = el.firstChild;
      const need = Math.max(t.width, t.height) * s * devicePixelRatio;
      const size = THUMB_SIZES.find(z => z >= need) ?? THUMB_SIZES.at(-1);
      if (!(+img.dataset.thumb >= size)) { img.dataset.thumb = size; img.src = host.thumbUrl(t.assetId, t.hash, t.rotation, size, t.squeeze); }
    }
    for (const [id, el] of tiles) if (!shown.has(id)) { el.remove(); tiles.delete(id); }

    const pixels = layout.width * layout.height;
    const tooBig = pixels > layout.maxPixels;
    $('[data-size]', form).textContent = `${layout.width} × ${layout.height} px` +
      (tooBig ? ` — over ${Math.floor(layout.maxPixels / 1e6)} megapixels, so make it narrower` : '') +
      (layout.leftOut ? ` · ${layout.leftOut} selected item${layout.leftOut === 1 ? ' is' : 's are'} not a photo and left out` : '');
    $('[data-size]', form).classList.toggle('error', tooBig);
    $('[data-variant]', form).textContent = layout.variants > 1 ? `Layout ${layout.variant + 1} of ${layout.variants}` : 'The only layout';
    $('[data-download]', form).disabled = tooBig;
    $('[data-rearrange]', form).disabled = layout.variants < 2;
  }

  form.addEventListener('input', e => {
    readForm();
    if (e.target.type === 'color') { draw(); return; }
    // Typed numbers wait for a pause; switches apply at once.
    clearTimeout(timer);
    if (e.target.type === 'number') timer = setTimeout(lay, 250); else lay();
  });
  $('[data-rearrange]', form).onclick = () => { variant++; lay(); };

  // Enter in a field just re-lays out; only the button downloads.
  form.addEventListener('submit', e => { e.preventDefault(); lay(); });
  $('[data-download]', form).addEventListener('click', async () => {
    const body = request(); if (!body || !layout) return;
    const button = $('[data-download]', form), state = $('[data-state]', form);
    button.disabled = true; state.className = 'small muted'; state.textContent = 'Making the collage from the originals…';
    try {
      const r = await fetch('/api/collage', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      if (!r.ok) throw new Error(host.errorText(await r.text()) || r.statusText);
      const blob = await r.blob();
      const name = /filename="?([^";]+)/.exec(r.headers.get('content-disposition') || '')?.[1] || 'collage.jpg';
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob); a.download = name;
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 60000);
      state.textContent = `Saved ${name} (${host.fmtBytes(blob.size)})`;
    } catch (err) {
      state.className = 'small error'; state.textContent = err.message;
    } finally { button.disabled = false; }
  });

  const onResize = () => { if (open()) draw(); else removeEventListener('resize', onResize); };
  addEventListener('resize', onResize);
  readForm();
  lay();
}
