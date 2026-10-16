// 360° viewer: draws the view from the middle of a sphere around the camera, which can be turned and zoomed. Plain
// WebGL, no library: one triangle covers the canvas, and a fragment shader works out which spot of the picture each
// pixel's viewing direction sees. Two kinds of picture:
// - Equirectangular: the whole sphere, 360° across and 180° from top to bottom. The direction's longitude and latitude
//   are the spot.
// - Two fisheye circles side by side, as an Insta360 camera saves them before stitching. Each lens's calibration (sent
//   with the item, see DualFisheye.cs) says where it sees the direction; the two are crossfaded over a narrow band at
//   the seam between them, as Insta360 Studio does. The lenses are 3 cm apart, so near the seam each is aimed at the
//   point the direction meets at the distance worked out for that part of the seam (fetched after the picture, see
//   DualFisheye.EstimateSeam); until it arrives, everything is taken to be far away. The distance counts fully over
//   the blend and fades out over uFade beyond it: further in, each circle is drawn exactly as its lens saw it.

const FOV = { start: 90, min: 30, max: 100 }; // degrees across the longer side of the canvas
const DEG = Math.PI / 180;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

const VERTEX1 = `
attribute vec2 aPos;
varying vec2 vPos;
void main() { vPos = aPos; gl_Position = vec4(aPos, 0.0, 1.0); }`;

const VERTEX2 = `#version 300 es
in vec2 aPos;
out vec2 vPos;
void main() { vPos = aPos; gl_Position = vec4(aPos, 0.0, 1.0); }`;

// The view: forward, right and up directions of the camera (y is up, the start view looks along -z), and the tangents
// of half the field of view across and down. Longitude 0, straight ahead at the start, is the middle of the picture.
// For two fisheye circles, per lens: the turn from the view's frame into the lens's (x right, y down, z along the
// lens), the circle's centre and focal length in fractions of the picture, and the unified sphere model's xi and
// distortion (13 terms, d0-d12, see DualFisheye.cs), and the way from the lens to the middle between the lenses
// (metres). uDepth holds 1/distance (0-3, 1/metres) for 256 places around the seam, the first at the right going up.
// TEX is the texture lookup of the GLSL version.
const SEAM_PLACES = 256, MAX_INVERSE_DISTANCE = 3;
const COMMON = `
uniform sampler2D uTex, uDepth;
uniform vec3 uShift0, uShift1;
uniform vec3 uFwd, uRight, uUp;
uniform vec2 uTan;
uniform bool uFisheye;
uniform mat3 uRot0, uRot1;
uniform vec2 uC0, uC1, uF0, uF1, uXi;
uniform vec4 uD0[4], uD1[4];
uniform float uBlend, uFade;
const float PI = 3.141592653589793;
vec3 viewDir() { return normalize(uFwd + vPos.x * uTan.x * uRight + vPos.y * uTan.y * uUp); }
vec2 lonLat(vec3 d) { return vec2(atan(d.x, -d.z) / (2.0 * PI) + 0.5, acos(clamp(d.y, -1.0, 1.0)) / PI); }
vec2 fisheye(vec3 d, mat3 rot, vec2 c, vec2 f, float xi, vec4 k[4], out float angle) {
  vec3 p = normalize(rot * d);
  angle = acos(clamp(p.z, -1.0, 1.0));
  vec2 m = p.xy / (p.z + xi);
  float r2 = dot(m, m);
  float radial = 1.0 + r2 * (k[0].x + r2 * (k[0].y + r2 * (k[0].z + r2 * (k[0].w + r2 * k[1].x))));
  float a = k[1].y + k[1].w * r2, b = k[1].z + k[2].x * r2;
  return c + f * (m * radial + vec2(
    (r2 + 2.0 * m.x * m.x) * a + 2.0 * m.x * m.y * b + k[2].y * r2 + k[2].w * r2 * r2,
    (r2 + 2.0 * m.y * m.y) * b + 2.0 * m.x * m.y * a + k[2].z * r2 + k[3].x * r2 * r2));
}
vec4 stitched(vec3 d) {
  float phi = abs(d.x) + abs(d.y) < 1e-6 ? 0.0 : atan(d.y, d.x);
  float rho = TEX(uDepth, vec2(phi / (2.0 * PI) + 0.5 / ${SEAM_PLACES}.0, 0.5)).r * ${MAX_INVERSE_DISTANCE}.0
    * (1.0 - smoothstep(0.5 * uBlend, 0.5 * uBlend + uFade, abs(asin(clamp(d.z, -1.0, 1.0)))));
  float a0, a1;
  vec2 uv0 = fisheye(d + rho * uShift0, uRot0, uC0, uF0, uXi.x, uD0, a0);
  vec2 uv1 = fisheye(d + rho * uShift1, uRot1, uC1, uF1, uXi.y, uD1, a1);
  // The first lens alone up to blend/2 short of the seam (where both are 90° away), the second alone past it.
  return mix(TEX(uTex, uv1), TEX(uTex, uv0), clamp(0.5 + (a1 - a0) * 0.5 / uBlend, 0.0, 1.0));
}`;

// WebGL 1: no mipmaps (the picture is seldom a power of two in size), so nothing to get wrong at the seam.
const FRAGMENT1 = `
#ifdef GL_FRAGMENT_PRECISION_HIGH
precision highp float;
#else
precision mediump float;
#endif
#define TEX texture2D
varying vec2 vPos;
${COMMON}
void main() {
  vec3 d = viewDir();
  gl_FragColor = uFisheye ? stitched(d) : texture2D(uTex, lonLat(d));
}`;

// WebGL 2, with mipmaps. The longitude jumps from 1 back to 0 behind the start view. The mipmap level comes from how
// fast the coordinates change between neighbouring pixels, and the jump would pick the smallest level there: a visible
// line. A copy of the longitude that jumps in front instead is smooth there, and its rate of change is used. The
// fisheye circles have no such jump.
const FRAGMENT2 = `#version 300 es
precision highp float;
#define TEX texture
in vec2 vPos;
out vec4 color;
${COMMON}
void main() {
  vec3 d = viewDir();
  if (uFisheye) { color = stitched(d); return; }
  vec2 uv = lonLat(d);
  vec2 uv2 = vec2(fract(uv.x + 0.5), uv.y);
  vec2 dx = dFdx(uv), dy = dFdy(uv), dx2 = dFdx(uv2), dy2 = dFdy(uv2);
  if (abs(dx2.x) + abs(dy2.x) < abs(dx.x) + abs(dy.x)) { dx = dx2; dy = dy2; }
  color = textureGrad(uTex, uv, dx, dy);
}`;

// Quaternions as [x, y, z, w], for the phone's orientation.
const qMul = ([ax, ay, az, aw], [bx, by, bz, bw]) => [
  ax * bw + aw * bx + ay * bz - az * by,
  ay * bw + aw * by + az * bx - ax * bz,
  az * bw + aw * bz + ax * by - ay * bx,
  aw * bw - ax * bx - ay * by - az * bz];
const qAxis = ([x, y, z], a) => { const s = Math.sin(a / 2); return [x * s, y * s, z * s, Math.cos(a / 2)]; };
function qRot([qx, qy, qz, qw], [vx, vy, vz]) {
  const tx = 2 * (qy * vz - qz * vy), ty = 2 * (qz * vx - qx * vz), tz = 2 * (qx * vy - qy * vx);
  return [vx + qw * tx + qy * tz - qz * ty, vy + qw * ty + qz * tx - qx * tz, vz + qw * tz + qx * ty - qy * tx];
}
/** Turns a direction about the vertical axis, by a radians to the right. */
const yawBy = ([x, y, z], a) => [x * Math.cos(a) - z * Math.sin(a), y, x * Math.sin(a) + z * Math.cos(a)];
/**
 * Where a camera looking out of the back of the phone points, from a deviceorientation event (degrees) and the screen's
 * turn. The same sums as three.js's DeviceOrientationControls: the device's own turn, then a quarter turn about x
 * (the camera looks out of the back, not the top), then the screen's orientation.
 */
function deviceQuaternion(alpha, beta, gamma, screenAngle) {
  let q = qMul(qMul(qAxis([0, 1, 0], alpha * DEG), qAxis([1, 0, 0], beta * DEG)), qAxis([0, 0, 1], -gamma * DEG));
  q = qMul(q, [-Math.SQRT1_2, 0, 0, Math.SQRT1_2]);
  return qMul(q, qAxis([0, 0, 1], -screenAngle * DEG));
}

const touchScreen = () => matchMedia('(hover: none) and (pointer: coarse)').matches;
let hintShown = false;

export class Panorama {
  /** The viewer, or null when the browser cannot do WebGL. One per page: browsers allow only a few WebGL contexts. */
  static create() {
    const canvas = document.createElement('canvas');
    canvas.className = 'pano';
    const opts = { alpha: false, antialias: false, depth: false, stencil: false, preserveDrawingBuffer: false };
    const gl = canvas.getContext('webgl2', opts) || canvas.getContext('webgl', opts);
    if (!gl) return null;
    try { return new Panorama(canvas, gl); } catch (e) { console.warn('360° viewer:', e); return null; }
  }

  /** Whether the phone's movement can turn the view: a touch screen with a motion sensor, on https. */
  static motionPossible() { return isSecureContext && 'DeviceOrientationEvent' in window && touchScreen(); }

  constructor(canvas, gl) {
    this.canvas = canvas;
    this.gl = gl;
    this.webgl2 = typeof WebGL2RenderingContext !== 'undefined' && gl instanceof WebGL2RenderingContext;
    this.maxTexture = gl.getParameter(gl.MAX_TEXTURE_SIZE);
    canvas.dataset.maxTexture = this.maxTexture;
    this.yaw = 0; this.pitch = 0; this.fov = FOV.start; // degrees; yaw to the right, pitch up
    this.texture = null;
    this.lenses = null;   // the fisheye circles' calibration, or null for an equirectangular picture
    this.seam = null;     // 1/distance around the seam (DualFisheye.EstimateSeam), or null: all far
    this.src = null;      // the picture in the texture, to load again if the context is lost
    this.token = 0;       // bumped by show() and hide(), so a picture that arrives late is dropped
    this.downloads = new AbortController();
    this.frame = 0;
    this.pointers = new Map();
    this.pinch = null;
    this.motion = null;   // while the phone's movement turns the view: { q, offset, handler }
    this.setup();
    canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); this.lost = true; });
    canvas.addEventListener('webglcontextrestored', () => {
      this.lost = false; this.texture = null; this.setup();
      if (this.src) this.load(this.src, this.token);
    });
    this.bindInput();
    new ResizeObserver(() => this.draw()).observe(canvas);
  }

  setup() {
    const gl = this.gl, prog = gl.createProgram();
    for (const [type, src] of [[gl.VERTEX_SHADER, this.webgl2 ? VERTEX2 : VERTEX1], [gl.FRAGMENT_SHADER, this.webgl2 ? FRAGMENT2 : FRAGMENT1]]) {
      const sh = gl.createShader(type);
      gl.shaderSource(sh, src); gl.compileShader(sh);
      if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(sh));
      gl.attachShader(prog, sh);
    }
    gl.bindAttribLocation(prog, 0, 'aPos');
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
    gl.useProgram(prog);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW); // one triangle that covers the canvas
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    this.u = Object.fromEntries(['uFwd', 'uRight', 'uUp', 'uTan', 'uTex', 'uDepth', 'uFisheye', 'uRot0', 'uRot1', 'uC0', 'uC1', 'uF0', 'uF1', 'uXi',
      'uD0', 'uD1', 'uShift0', 'uShift1', 'uBlend', 'uFade'].map(n => [n, gl.getUniformLocation(prog, n)]));
    gl.uniform1i(this.u.uTex, 0);
    gl.uniform1i(this.u.uDepth, 1);
    this.depth = gl.createTexture();
    this.uploadSeam();
    this.aniso = gl.getExtension('EXT_texture_filter_anisotropic');
  }

  /**
   * The preview width worth loading after the 2048 one, for a picture fullWidth pixels wide, or null when 2048 is all
   * there is. Enough for the screen at the starting field of view, within what the graphics card takes, and at most
   * 4096 on phones and tablets (8192 × 4096 is 128 MB of graphics memory; the full 71 MP of an Insta360 X5 would be
   * about 280 MB).
   */
  sharperSize(fullWidth) {
    const cap = Math.min(this.maxTexture, touchScreen() ? 4096 : 8192);
    const screenPx = Math.max(screen.width, screen.height) * Math.min(devicePixelRatio || 1, 2);
    const want = Math.min(fullWidth || Infinity, screenPx * 360 / FOV.start);
    let size = 2048;
    for (const s of [4096, 8192]) if (s <= cap && size < want) size = s;
    return size > 2048 ? size : null;
  }

  /**
   * Puts the canvas in stage, looking straight ahead, with the picture at url, then the one at sharperUrl once loaded.
   * lenses: for two fisheye circles, their calibration (the item's lenses, from DualFisheye.cs); null for an
   * equirectangular picture.
   */
  show(stage, url, sharperUrl, lenses = null, seamUrl = null) {
    this.hide();
    this.lenses = lenses;
    const token = this.token;
    this.uploadSeam();
    if (lenses && seamUrl) {
      fetch(seamUrl, { signal: this.downloads.signal }).then(r => r.ok ? r.json() : null).then(j => {
        if (token !== this.token || this.lost || j?.inverseDistance?.length !== SEAM_PLACES) return;
        this.seam = j.inverseDistance;
        this.uploadSeam();
        this.draw();
      }).catch(() => { });
    }
    this.yaw = 0; this.pitch = 0; this.fov = FOV.start;
    stage.appendChild(this.canvas);
    if (!hintShown) {
      hintShown = true;
      this.note(stage, 'pano-hint', touchScreen() ? 'Drag to look around · pinch to zoom' : 'Drag to look around · scroll to zoom · double-click to reset', 5000);
    }
    this.draw();
    this.load(url, token).then(ok => {
      if (!ok || !sharperUrl || token !== this.token) return;
      const busy = this.note(stage, 'pano-loading', 'Loading the full picture…');
      this.load(sharperUrl, token).finally(() => busy.remove());
    });
  }

  /** A line of text over the picture; gone after ms, or when the view is first moved. */
  note(stage, cls, text, ms) {
    const el = document.createElement('div');
    el.className = cls; el.textContent = text;
    stage.appendChild(el);
    if (ms) { setTimeout(() => el.remove(), ms); this.canvas.addEventListener('pointerdown', () => el.remove(), { once: true }); }
    return el;
  }

  /** Takes the canvas off the page and frees the picture's graphics memory. */
  hide() {
    this.token++;
    this.downloads.abort(); // cancel a download still in flight
    this.downloads = new AbortController();
    this.stopMotion();
    if (this.texture && !this.lost) this.gl.deleteTexture(this.texture);
    this.texture = null; this.src = null; this.lenses = null; this.seam = null;
    delete this.canvas.dataset.texture;
    delete this.canvas.dataset.seam;
    this.pointers.clear(); this.pinch = null;
    this.canvas.classList.remove('dragging');
    this.canvas.remove();
  }

  get shown() { return this.canvas.isConnected; }

  async load(url, token) {
    // Decoded off the main thread, and let go of as soon as it is in the graphics card: 8192 × 4096 is 128 MB.
    let pic;
    try {
      const r = await fetch(url, { signal: this.downloads.signal });
      if (!r.ok) return false;
      pic = await createImageBitmap(await r.blob());
    } catch { return false; }
    try {
      if (token !== this.token || this.lost) return false;
      this.upload(pic);
      this.src = url;
      return true;
    } finally { pic.close(); }
  }

  /** The seam distances into their texture (unit 1): this.seam, or all far. */
  uploadSeam() {
    const gl = this.gl, data = new Uint8Array(SEAM_PLACES * 4);
    for (let i = 0; i < SEAM_PLACES; i++) {
      data[i * 4] = Math.round(clamp((this.seam?.[i] ?? 0) / MAX_INVERSE_DISTANCE, 0, 1) * 255);
      data[i * 4 + 3] = 255;
    }
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, this.depth);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, SEAM_PLACES, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, data);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT); // round the circle
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.activeTexture(gl.TEXTURE0);
    if (this.seam) this.canvas.dataset.seam = this.seam.map(r => r.toFixed(2)).join(' ');
  }

  upload(pic) {
    const gl = this.gl;
    gl.activeTexture(gl.TEXTURE0);
    if (this.texture) gl.deleteTexture(this.texture);
    this.texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, this.texture);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, pic);
    if (this.webgl2) {
      // An equirectangular picture's left and right edges meet; the fisheye circles' edges are read by neither lens.
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, this.lenses ? gl.CLAMP_TO_EDGE : gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      gl.generateMipmap(gl.TEXTURE_2D);
      // Sharper near the top and bottom, where the picture is squeezed across.
      if (this.aniso) gl.texParameterf(gl.TEXTURE_2D, this.aniso.TEXTURE_MAX_ANISOTROPY_EXT, Math.min(8, gl.getParameter(this.aniso.MAX_TEXTURE_MAX_ANISOTROPY_EXT)));
    } else {
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    }
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    this.canvas.dataset.texture = `${pic.width}x${pic.height}`;
    this.draw();
  }

  /** Schedules a draw for the next frame; repeated calls before then draw once. */
  draw() {
    if (!this.frame) this.frame = requestAnimationFrame(() => { this.frame = 0; this.render(); });
  }

  render() {
    const gl = this.gl, c = this.canvas;
    if (this.lost || !c.isConnected) return;
    const dpr = Math.min(devicePixelRatio || 1, 2);
    const w = Math.max(1, Math.round(c.clientWidth * dpr)), h = Math.max(1, Math.round(c.clientHeight * dpr));
    if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
    gl.viewport(0, 0, w, h);
    gl.clearColor(0, 0, 0, 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
    if (!this.texture) return;
    const [right, up, fwd] = this.basis();
    const t = Math.tan(this.fov * DEG / 2);
    gl.uniform3fv(this.u.uRight, right); gl.uniform3fv(this.u.uUp, up); gl.uniform3fv(this.u.uFwd, fwd);
    gl.uniform2f(this.u.uTan, w >= h ? t : t * w / h, w >= h ? t * h / w : t);
    this.setLenses();
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.texture);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  setLenses() {
    const gl = this.gl, u = this.u, rig = this.lenses;
    gl.uniform1i(u.uFisheye, rig ? 1 : 0);
    if (!rig) return;
    const [a, b] = rig.lenses;
    // The calibration is row-major; GLSL matrices are column-major.
    const columns = r => [r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8]];
    const terms = d => { const t = new Float32Array(16); t.set(d.slice(0, 13)); return t; };
    gl.uniformMatrix3fv(u.uRot0, false, columns(a.rotation)); gl.uniformMatrix3fv(u.uRot1, false, columns(b.rotation));
    gl.uniform2fv(u.uC0, a.centre); gl.uniform2fv(u.uC1, b.centre);
    gl.uniform2fv(u.uF0, a.focal); gl.uniform2fv(u.uF1, b.focal);
    gl.uniform2f(u.uXi, a.xi, b.xi);
    gl.uniform3fv(u.uShift0, a.shift); gl.uniform3fv(u.uShift1, b.shift);
    gl.uniform4fv(u.uD0, terms(a.distortion)); gl.uniform4fv(u.uD1, terms(b.distortion));
    gl.uniform1f(u.uBlend, rig.blend * DEG);
    gl.uniform1f(u.uFade, rig.fade * DEG);
  }

  /** The camera's right, up and forward directions. */
  basis() {
    const m = this.motion;
    if (m?.q) return [[1, 0, 0], [0, 1, 0], [0, 0, -1]].map(v => yawBy(qRot(m.q, v), m.offset));
    const cy = Math.cos(this.yaw * DEG), sy = Math.sin(this.yaw * DEG), cp = Math.cos(this.pitch * DEG), sp = Math.sin(this.pitch * DEG);
    return [[cy, 0, sy], [-sp * sy, cp, sp * cy], [cp * sy, sp, -cp * cy]];
  }

  /** Turns the view by degrees: right, and up. With phone motion on, only the turn to the right applies. */
  turn(right, up) {
    if (this.motion?.q) this.motion.offset += right * DEG;
    else {
      this.yaw = ((this.yaw + right) % 360 + 540) % 360 - 180;
      this.pitch = clamp(this.pitch + up, -90, 90);
    }
    this.draw();
  }

  zoomTo(fov) { this.fov = clamp(fov, FOV.min, FOV.max); this.draw(); }

  reset() {
    if (this.motion?.q) this.motion.offset = null; // straight ahead is where the phone points now
    this.yaw = 0; this.pitch = 0; this.fov = FOV.start;
    this.draw();
  }

  /**
   * Keys while a 360° photo is shown: ↑ ↓ look up and down, Shift+← → turn (← → alone still step between items),
   * + − zoom, 0 back to the start. Returns whether the key was used.
   */
  key(e) {
    const step = this.fov / 15;
    switch (e.key) {
      case 'ArrowUp': this.turn(0, step); return true;
      case 'ArrowDown': this.turn(0, -step); return true;
      case 'ArrowLeft': if (!e.shiftKey) return false; this.turn(-step, 0); return true;
      case 'ArrowRight': if (!e.shiftKey) return false; this.turn(step, 0); return true;
      case '+': case '=': this.zoomTo(this.fov / 1.15); return true;
      case '-': case '_': this.zoomTo(this.fov * 1.15); return true;
      case '0': this.reset(); return true;
    }
    return false;
  }

  bindInput() {
    const c = this.canvas;
    const spread = () => { const [a, b] = [...this.pointers.values()]; return Math.hypot(a.x - b.x, a.y - b.y); };
    const startPinch = () => { this.pinch = this.pointers.size === 2 ? { spread: Math.max(1, spread()), fov: this.fov } : null; };
    c.addEventListener('pointerdown', e => {
      if (e.pointerType === 'mouse' && e.button !== 0) return;
      try { c.setPointerCapture(e.pointerId); } catch { } // keeps the drag when the pointer leaves the canvas
      this.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      startPinch();
      c.classList.add('dragging');
    });
    c.addEventListener('pointermove', e => {
      const p = this.pointers.get(e.pointerId); if (!p) return;
      const dx = e.clientX - p.x, dy = e.clientY - p.y;
      p.x = e.clientX; p.y = e.clientY;
      if (this.pointers.size === 2) { if (this.pinch) this.zoomTo(this.pinch.fov * this.pinch.spread / Math.max(1, spread())); return; }
      if (this.pointers.size > 2) return;
      // The picture follows the pointer: degrees per CSS pixel at the middle of the view.
      const k = this.fov / Math.max(c.clientWidth, c.clientHeight, 1);
      this.turn(-dx * k, dy * k);
    });
    const end = e => {
      if (!this.pointers.delete(e.pointerId)) return;
      startPinch();
      if (!this.pointers.size) c.classList.remove('dragging');
    };
    c.addEventListener('pointerup', end);
    c.addEventListener('pointercancel', end);
    c.addEventListener('wheel', e => {
      e.preventDefault();
      const px = e.deltaY * (e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? 400 : 1);
      this.zoomTo(this.fov * Math.exp(px * 0.002));
    }, { passive: false });
    c.addEventListener('dblclick', e => { e.preventDefault(); this.reset(); });
  }

  get motionOn() { return !!this.motion; }

  /**
   * Turns the view as the phone is turned, or stops. Resolves to whether it is on. iOS asks the user first, which it
   * allows only from a tap: call this straight from the click handler.
   */
  async setMotion(on) {
    if (!on) { this.stopMotion(); return false; }
    if (this.motion) return true;
    const D = window.DeviceOrientationEvent;
    if (!D) return false;
    if (typeof D.requestPermission === 'function') {
      try { if (await D.requestPermission() !== 'granted') return false; } catch { return false; }
    }
    const m = this.motion = { q: null, offset: null, handler: null };
    m.handler = e => {
      if (e.alpha == null) return;
      m.q = deviceQuaternion(e.alpha, e.beta, e.gamma, screen.orientation?.angle ?? window.orientation ?? 0);
      if (m.offset == null) { // carry on from the view on screen
        const f = qRot(m.q, [0, 0, -1]);
        m.offset = this.yaw * DEG - Math.atan2(f[0], -f[2]);
      }
      this.draw();
    };
    addEventListener('deviceorientation', m.handler);
    // A device that has the event but no sensor never sends one.
    await new Promise(r => setTimeout(r, 1000));
    if (this.motion === m && !m.q) this.stopMotion();
    return this.motion === m;
  }

  stopMotion() {
    const m = this.motion; if (!m) return;
    removeEventListener('deviceorientation', m.handler);
    if (m.q) { // keep looking where the phone pointed
      const f = this.basis()[2];
      this.yaw = Math.atan2(f[0], -f[2]) / DEG;
      this.pitch = Math.asin(clamp(f[1], -1, 1)) / DEG;
    }
    this.motion = null;
    this.draw();
  }
}
