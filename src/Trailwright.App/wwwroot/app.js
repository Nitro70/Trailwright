import * as THREE from 'three';
import { OrbitControls } from './vendor/OrbitControls.js';

// ---------- small helpers ----------
const $ = (id) => document.getElementById(id);
const el = (tag, attrs = {}, ...kids) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === 'class') e.className = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else if (v !== undefined && v !== null) e.setAttribute(k, v);
  }
  for (const k of kids) if (k !== null && k !== undefined) e.append(k.nodeType ? k : document.createTextNode(k));
  return e;
};
const keep = (...xs) => xs.filter((x) => x !== null && x !== undefined && x !== false);

async function api(path, body) {
  const r = await fetch('/api/' + path, body === undefined ? {} : {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  });
  const text = await r.text();
  const data = text ? JSON.parse(text) : {};
  if (!r.ok) throw new Error(data.error || r.statusText);
  return data;
}

let toastTimer;
function toast(msg, ok = false) {
  const t = $('toast');
  t.textContent = msg;
  t.className = 'toast' + (ok ? ' ok' : '');
  t.style.display = 'block';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.style.display = 'none'), ok ? 2200 : 5000);
}
async function act(fn, okMsg) {
  try { const r = await fn(); if (okMsg) toast(okMsg, true); await refreshScene(); return r; }
  catch (e) { toast(e.message); }
}

// ---------- rotations (same convention as the game: M = Ry * Rx * Rz, quarter turns) ----------
const cs = [1, 0, -1, 0], sn = [0, 1, 0, -1];
function axisM(a, q) {
  q = ((q % 4) + 4) % 4; const c = cs[q], s = sn[q];
  if (a === 'x') return [1, 0, 0, 0, c, -s, 0, s, c];
  if (a === 'y') return [c, 0, s, 0, 1, 0, -s, 0, c];
  return [c, -s, 0, s, c, 0, 0, 0, 1];
}
function mul(a, b) {
  const r = new Array(9);
  for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++)
    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
  return r;
}
const fromEuler = (x, y, z) => mul(axisM('y', y), mul(axisM('x', x), axisM('z', z)));
const applyM = (m, v) => [m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]];
function eulerOf(m) {
  for (const x of [0, 1, 3, 2]) for (let y = 0; y < 4; y++) for (let z = 0; z < 4; z++) {
    const t = fromEuler(x, y, z);
    if (t.every((v, i) => v === m[i])) return [x, y, z];
  }
  return [0, 0, 0];
}
function rotatedBox(gmin, gmax, m) {
  const mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
  for (const x of [gmin[0], gmax[0]]) for (const y of [gmin[1], gmax[1]]) for (const z of [gmin[2], gmax[2]]) {
    const p = applyM(m, [x, y, z]);
    for (let i = 0; i < 3; i++) { mn[i] = Math.min(mn[i], p[i]); mx[i] = Math.max(mx[i], p[i]); }
  }
  return [mn, mx];
}

// ---------- state ----------
const S = {
  catalog: null, byGuid: new Map(), scene: { blocks: [] }, revision: -1, state: null,
  tool: 'select', part: null, rot: fromEuler(0, 0, 0), color1: 22, color2: null,
  selected: null, category: 'All', occupied: new Map(),
};

// ---------- three.js ----------
const canvas = $('view');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, preserveDrawingBuffer: false });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
const scene3 = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 4000);
camera.position.set(18, 16, -22);
const controls = new OrbitControls(camera, canvas);
controls.mouseButtons = { LEFT: null, MIDDLE: THREE.MOUSE.PAN, RIGHT: THREE.MOUSE.ROTATE };
controls.enableDamping = true;
controls.dampingFactor = 0.12;
controls.screenSpacePanning = true;

scene3.add(new THREE.HemisphereLight(0xdfe8ff, 0x2a2622, 1.25));
const sun = new THREE.DirectionalLight(0xffffff, 1.6);
sun.position.set(30, 60, -20);
scene3.add(sun);
const fill = new THREE.DirectionalLight(0x9db8ff, 0.45);
fill.position.set(-40, 20, 40);
scene3.add(fill);

let grid;
function makeGrid(size, y) {
  if (grid) scene3.remove(grid);
  grid = new THREE.Group();
  const g1 = new THREE.GridHelper(size, size, 0x3a4150, 0x2a2f39);
  const g2 = new THREE.GridHelper(size, size / 4, 0x4b5465, 0x4b5465);
  g1.position.y = g2.position.y = y;
  g1.material.transparent = g2.material.transparent = true;
  g1.material.opacity = 0.55; g2.material.opacity = 0.5;
  const plate = new THREE.Mesh(new THREE.PlaneGeometry(size, size), new THREE.MeshBasicMaterial({ color: 0x1b1e25, transparent: true, opacity: 0.6, depthWrite: false }));
  plate.rotation.x = -Math.PI / 2; plate.position.y = y - 0.01;
  grid.add(plate, g1, g2);
  grid.userData.y = y;
  scene3.add(grid);
}
makeGrid(64, -0.5);

const boxGeo = new THREE.BoxGeometry(1, 1, 1);
const blockMat = new THREE.MeshStandardMaterial({ roughness: 0.55, metalness: 0.08 });
let blocksMesh = null;
let outlines = null;
let instanceToBlock = [];
const selBox = new THREE.Box3Helper(new THREE.Box3(), 0xff8c1a);
selBox.visible = false;
scene3.add(selBox);
const hoverBox = new THREE.Box3Helper(new THREE.Box3(), 0x409cff);
hoverBox.visible = false;
scene3.add(hoverBox);
const ghost = new THREE.Mesh(boxGeo, new THREE.MeshStandardMaterial({ color: 0x3fcf7a, transparent: true, opacity: 0.45, depthWrite: false }));
ghost.visible = false;
scene3.add(ghost);
const ghostEdges = new THREE.Box3Helper(new THREE.Box3(), 0x3fcf7a);
ghostEdges.visible = false;
scene3.add(ghostEdges);

function paletteColor(i) {
  const p = S.catalog?.palette;
  return new THREE.Color(p && i !== null && i !== undefined && p[i] ? p[i] : '#9e9e9e');
}

function rebuildBlocks() {
  if (blocksMesh) { scene3.remove(blocksMesh); blocksMesh.dispose(); }
  const list = S.scene.blocks;
  S.occupied = new Map();
  instanceToBlock = [];
  blocksMesh = new THREE.InstancedMesh(boxGeo, blockMat, Math.max(1, list.length));
  const m = new THREE.Matrix4(), q = new THREE.Quaternion();
  list.forEach((b, i) => {
    const mn = b.cellsMin, mx = b.cellsMax;
    const c = new THREE.Vector3((mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2);
    const s = new THREE.Vector3(mx[0] - mn[0] + 1 - 0.06, mx[1] - mn[1] + 1 - 0.06, mx[2] - mn[2] + 1 - 0.06);
    m.compose(c, q, s);
    blocksMesh.setMatrixAt(i, m);
    const col = paletteColor(b.color);
    col.setRGB(0.05 + col.r * 0.95, 0.05 + col.g * 0.95, 0.055 + col.b * 0.945);
    blocksMesh.setColorAt(i, col);
    instanceToBlock.push(b);
    for (let x = mn[0]; x <= mx[0]; x++) for (let y = mn[1]; y <= mx[1]; y++) for (let z = mn[2]; z <= mx[2]; z++)
      S.occupied.set(`${x},${y},${z}`, b.id);
  });
  blocksMesh.count = list.length;
  blocksMesh.instanceMatrix.needsUpdate = true;
  if (blocksMesh.instanceColor) blocksMesh.instanceColor.needsUpdate = true;
  scene3.add(blocksMesh);
  // outlines (one merged line set) so dark blocks still read against the dark background
  if (outlines) { scene3.remove(outlines); outlines.geometry.dispose(); outlines = null; }
  if (list.length && list.length <= 12000) {
    const pts = new Float32Array(list.length * 24 * 3);
    let o = 0;
    for (const b of list) {
      const a = [b.cellsMin[0] - 0.47, b.cellsMin[1] - 0.47, b.cellsMin[2] - 0.47], z = [b.cellsMax[0] + 0.47, b.cellsMax[1] + 0.47, b.cellsMax[2] + 0.47];
      const c = [[a[0], a[1], a[2]], [z[0], a[1], a[2]], [z[0], z[1], a[2]], [a[0], z[1], a[2]], [a[0], a[1], z[2]], [z[0], a[1], z[2]], [z[0], z[1], z[2]], [a[0], z[1], z[2]]];
      for (const [i, j] of [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]]) {
        pts.set(c[i], o); pts.set(c[j], o + 3); o += 6;
      }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pts, 3));
    outlines = new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: 0x6b7384, transparent: true, opacity: 0.55 }));
    scene3.add(outlines);
  }
  const lowest = list.length ? Math.min(...list.map((b) => b.cellsMin[1])) : 0;
  const span = list.length ? Math.max(...list.map((b) => Math.max(Math.abs(b.cellsMin[0]), Math.abs(b.cellsMax[0]), Math.abs(b.cellsMin[2]), Math.abs(b.cellsMax[2])))) : 8;
  const size = Math.max(32, Math.ceil((span * 2 + 16) / 8) * 8);
  if (!grid || grid.userData.y !== lowest - 0.5 || grid.children[1].geometry.parameters?.size !== size) makeGrid(size, lowest - 0.5);
  updateSelection();
}

function boxOf(b) {
  return new THREE.Box3(new THREE.Vector3(b.cellsMin[0] - 0.5, b.cellsMin[1] - 0.5, b.cellsMin[2] - 0.5),
    new THREE.Vector3(b.cellsMax[0] + 0.5, b.cellsMax[1] + 0.5, b.cellsMax[2] + 0.5));
}

function frameAll() {
  const list = S.scene.blocks;
  if (!list.length) { controls.target.set(0, 0, 0); camera.position.set(14, 12, -18); return; }
  const box = new THREE.Box3();
  list.forEach((b) => box.union(boxOf(b)));
  const c = box.getCenter(new THREE.Vector3());
  const r = box.getSize(new THREE.Vector3()).length() * 0.5 + 2;
  controls.target.copy(c);
  camera.position.copy(c).add(new THREE.Vector3(1, 0.8, -1.2).normalize().multiplyScalar(r * 2.4));
}

function resize() {
  const vp = $('viewport');
  renderer.setSize(vp.clientWidth, vp.clientHeight, false);
  camera.aspect = vp.clientWidth / Math.max(1, vp.clientHeight);
  camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe($('viewport'));

(function loop() {
  controls.update();
  renderer.render(scene3, camera);
  requestAnimationFrame(loop);
})();

// ---------- picking & placement ----------
const ray = new THREE.Raycaster();
const ndc = new THREE.Vector2();
let lastHit = null;

function pick(ev) {
  const r = canvas.getBoundingClientRect();
  ndc.set(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1);
  ray.setFromCamera(ndc, camera);
  if (blocksMesh && S.scene.blocks.length) {
    const hits = ray.intersectObject(blocksMesh);
    if (hits.length) {
      const h = hits[0];
      const b = instanceToBlock[h.instanceId];
      const n = h.face.normal.clone().round();
      // the cell that was hit: step half a cell back into the block from the hit point
      const p = h.point.clone().addScaledVector(n, -0.25);
      return { block: b, cell: [Math.round(p.x), Math.round(p.y), Math.round(p.z)], normal: [n.x, n.y, n.z] };
    }
  }
  const plane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -(grid?.userData.y ?? -0.5));
  const pt = new THREE.Vector3();
  if (ray.ray.intersectPlane(plane, pt)) return { block: null, cell: [Math.round(pt.x), Math.round(pt.y - 0.5) + 1 - 1, Math.round(pt.z)], normal: [0, 1, 0], ground: true };
  return null;
}

function placementFor(hit) {
  const t = S.part;
  if (!t || !hit) return null;
  const [mn, mx] = rotatedBox(t.gridMin, t.gridMax, S.rot);
  const target = hit.ground ? [hit.cell[0], (grid?.userData.y ?? -0.5) + 0.5, hit.cell[2]] : hit.cell.map((v, i) => v + hit.normal[i]);
  const pos = target.slice();
  for (let i = 0; i < 3; i++) {
    if (hit.normal[i] > 0) pos[i] = target[i] - mn[i];
    else if (hit.normal[i] < 0) pos[i] = target[i] - mx[i];
  }
  const cmin = [pos[0] + mn[0], pos[1] + mn[1], pos[2] + mn[2]], cmax = [pos[0] + mx[0], pos[1] + mx[1], pos[2] + mx[2]];
  let free = true;
  for (let x = cmin[0]; x <= cmax[0] && free; x++) for (let y = cmin[1]; y <= cmax[1] && free; y++) for (let z = cmin[2]; z <= cmax[2] && free; z++)
    if (S.occupied.has(`${x},${y},${z}`)) free = false;
  return { pos, cmin, cmax, free };
}

canvas.addEventListener('pointermove', (ev) => {
  const hit = pick(ev);
  lastHit = hit;
  $('cursor').textContent = hit ? `cell ${hit.cell.join(', ')}${hit.block ? ` · #${hit.block.id} ${hit.block.type}` : ''}` : '';
  hoverBox.visible = false; ghost.visible = false; ghostEdges.visible = false;
  if (!hit) return;
  if (S.tool === 'build') {
    const pl = placementFor(hit);
    if (pl) {
      const box = new THREE.Box3(new THREE.Vector3(pl.cmin[0] - 0.5, pl.cmin[1] - 0.5, pl.cmin[2] - 0.5), new THREE.Vector3(pl.cmax[0] + 0.5, pl.cmax[1] + 0.5, pl.cmax[2] + 0.5));
      box.getCenter(ghost.position); box.getSize(ghost.scale);
      const col = pl.free ? 0x3fcf7a : 0xff5a52;
      ghost.material.color.setHex(col); ghostEdges.material.color.setHex(col);
      ghostEdges.box.copy(box);
      ghost.visible = ghostEdges.visible = true;
    }
  } else if (hit.block) {
    hoverBox.box.copy(boxOf(hit.block));
    hoverBox.material.color.setHex(S.tool === 'erase' ? 0xff5a52 : S.tool === 'paint' ? 0xffffff : 0x409cff);
    hoverBox.visible = true;
  }
});
canvas.addEventListener('pointerleave', () => { hoverBox.visible = ghost.visible = ghostEdges.visible = false; });

let downAt = null;
canvas.addEventListener('pointerdown', (ev) => { if (ev.button === 0) downAt = [ev.clientX, ev.clientY]; });
canvas.addEventListener('pointerup', async (ev) => {
  if (ev.button !== 0 || !downAt) return;
  const moved = Math.hypot(ev.clientX - downAt[0], ev.clientY - downAt[1]) > 5;
  downAt = null;
  if (moved) return;
  const hit = pick(ev);
  if (S.tool === 'select') { select(hit?.block?.id ?? null); return; }
  if (!hit) return;
  if (S.tool === 'build') {
    if (!S.part) { toast('Pick a part on the left first'); return; }
    const pl = placementFor(hit);
    if (!pl.free) { toast('That space is taken'); return; }
    const res = await act(() => api('place', {
      guid: S.part.guid, pos: pl.pos, rot: eulerOf(S.rot), color: S.part.paintable ? S.color1 : null,
      color2: S.part.paintable && S.part.color2 ? S.color2 : null, allowFloating: ev.shiftKey,
    }));
    if (res?.warnings?.length) toast(res.warnings.join('; '));
  } else if (S.tool === 'paint' && hit.block) {
    await act(() => api('color', { ids: [hit.block.id], color: ev.shiftKey ? null : S.color1, color2: ev.shiftKey ? (S.color2 ?? S.color1) : null }));
  } else if (S.tool === 'erase' && hit.block) {
    await act(() => api('remove', { ids: [hit.block.id] }));
    if (S.selected === hit.block.id) select(null);
  }
});
canvas.addEventListener('contextmenu', (e) => e.preventDefault());

// ---------- tools & keys ----------
function setTool(t) {
  S.tool = t;
  document.querySelectorAll('[data-tool]').forEach((b) => b.classList.toggle('on', b.dataset.tool === t));
  ghost.visible = ghostEdges.visible = hoverBox.visible = false;
  $('status').textContent = { select: 'Select: click a block', build: 'Build: click a face to place the part (Shift = allow floating)', paint: 'Paint: click = main colour, Shift+click = second colour', erase: 'Erase: click a block to remove it' }[t];
}
document.querySelectorAll('[data-tool]').forEach((b) => b.addEventListener('click', () => setTool(b.dataset.tool)));

async function rotate(axis) {
  const q = axisM(axis, 1);
  if (S.tool === 'select' && S.selected != null) {
    const b = S.scene.blocks.find((x) => x.id === S.selected);
    if (!b) return;
    const m = mul(q, b.matrix);
    await act(() => api('transform', { id: b.id, rot: eulerOf(m), allowFloating: true }));
    return;
  }
  S.rot = mul(q, S.rot);
  canvas.dispatchEvent(new PointerEvent('pointermove', { clientX: lastMouse[0], clientY: lastMouse[1] }));
}
let lastMouse = [0, 0];
window.addEventListener('pointermove', (e) => (lastMouse = [e.clientX, e.clientY]));
document.querySelectorAll('[data-rot]').forEach((b) => b.addEventListener('click', () => rotate(b.dataset.rot)));

async function nudge(dx, dy, dz) {
  const b = S.scene.blocks.find((x) => x.id === S.selected);
  if (!b) return;
  await act(() => api('transform', { id: b.id, pos: [b.pos[0] + dx, b.pos[1] + dy, b.pos[2] + dz], allowFloating: true }));
}

window.addEventListener('keydown', (e) => {
  if (e.target.closest('input, textarea, select') || document.querySelector('dialog[open]')) return;
  const k = e.key.toLowerCase();
  if (e.ctrlKey && k === 'z') { e.preventDefault(); act(() => api('undo', {})); return; }
  if (e.ctrlKey && (k === 'y' || (e.shiftKey && k === 'z'))) { e.preventDefault(); act(() => api('redo', {})); return; }
  if (e.ctrlKey && k === 's') { e.preventDefault(); save(false); return; }
  if (e.ctrlKey && k === 'o') { e.preventDefault(); openLibrary(); return; }
  if (e.ctrlKey && k === 'n') { e.preventDefault(); newBuild(); return; }
  if (e.ctrlKey) return;
  const map = { v: 'select', b: 'build', p: 'paint', e: 'erase' };
  if (map[k]) { setTool(map[k]); return; }
  if (k === 'r') rotate('y');
  else if (k === 't') rotate('x');
  else if (k === 'f') rotate('z');
  else if (k === 'escape') { setTool('select'); select(null); }
  else if ((k === 'delete' || k === 'backspace') && S.selected != null) { const id = S.selected; select(null); act(() => api('remove', { ids: [id] })); }
  else if (k === 'home') frameAll();
  else if (S.selected != null && S.tool === 'select') {
    const step = { arrowleft: [-1, 0, 0], arrowright: [1, 0, 0], arrowup: [0, 0, 1], arrowdown: [0, 0, -1], pageup: [0, 1, 0], pagedown: [0, -1, 0] }[k];
    if (step) { e.preventDefault(); nudge(...step); }
  }
});

// ---------- parts palette ----------
const catOrder = ['All', 'FRAME', 'SEAT', 'WHEEL', 'PROPULSION', 'MECHANIC', 'AERODYNAMIC', 'GADGETS', 'LIGHTS'];
function renderCats() {
  const cats = new Set(S.catalog.blocks.map((b) => b.category));
  const ordered = [...catOrder.filter((c) => c === 'All' || cats.has(c)), ...[...cats].filter((c) => !catOrder.includes(c)).sort()];
  $('cats').replaceChildren(...ordered.map((c) => el('button', {
    class: c === S.category ? 'on' : '', onclick: () => { S.category = c; renderCats(); renderParts(); },
  }, prettyCat(c))));
}
const prettyCat = (c) => c === 'All' ? 'All' : c.replace(/^ACCESSORY_/, '').toLowerCase().replace(/_/g, ' ').replace(/^\w/, (m) => m.toUpperCase());

function drawPartIcon(cv, t) {
  const ctx = cv.getContext('2d');
  const w = cv.width = 120, h = cv.height = 54;
  const sx = t.gridMax[0] - t.gridMin[0] + 1, sy = t.gridMax[1] - t.gridMin[1] + 1, sz = t.gridMax[2] - t.gridMin[2] + 1;
  const k = Math.min(16, 40 / Math.max(sx + sz, sy * 1.3 + (sx + sz) * 0.5));
  const C = 0.866 * k, Sh = 0.5 * k;
  const P = (x, y, z) => [w / 2 + (x - z) * C, h / 2 + (x + z) * Sh * 0.5 - y * k + ((sy * k) / 2) - ((sx + sz) * Sh) / 4];
  const col = new THREE.Color(t.paintable ? paletteHex(S.color1) : '#9aa3b2');
  const face = (pts, shade) => {
    ctx.beginPath(); pts.forEach((p, i) => (i ? ctx.lineTo(...p) : ctx.moveTo(...p))); ctx.closePath();
    ctx.fillStyle = `rgb(${col.r * 255 * shade | 0},${col.g * 255 * shade | 0},${col.b * 255 * shade | 0})`;
    ctx.fill(); ctx.strokeStyle = '#0006'; ctx.lineWidth = 1; ctx.stroke();
  };
  const ox = -sx / 2, oz = -sz / 2;
  face([P(ox, sy, oz), P(ox + sx, sy, oz), P(ox + sx, sy, oz + sz), P(ox, sy, oz + sz)], 1);
  face([P(ox + sx, 0, oz), P(ox + sx, sy, oz), P(ox + sx, sy, oz + sz), P(ox + sx, 0, oz + sz)], 0.75);
  face([P(ox, 0, oz + sz), P(ox + sx, 0, oz + sz), P(ox + sx, sy, oz + sz), P(ox, sy, oz + sz)], 0.6);
}
const paletteHex = (i) => S.catalog.palette[i] ?? '#9e9e9e';

function renderParts() {
  const q = $('partSearch').value.trim().toLowerCase();
  const list = S.catalog.blocks
    .filter((b) => (S.category === 'All' || b.category === S.category) && (!q || b.name.toLowerCase().includes(q)))
    .sort((a, b) => a.category.localeCompare(b.category) || a.sort - b.sort || a.name.localeCompare(b.name))
    .slice(0, 400);
  $('partGrid').replaceChildren(...list.map((t) => {
    const cv = el('canvas');
    drawPartIcon(cv, t);
    const sz = [0, 1, 2].map((i) => t.gridMax[i] - t.gridMin[i] + 1).join('×');
    return el('div', {
      class: 'part' + (S.part?.guid === t.guid ? ' on' : ''), title: t.name,
      onclick: () => { S.part = t; S.rot = fromEuler(0, 0, 0); setTool('build'); renderParts(); },
    }, cv, el('div', { class: 'nm' }, t.name), el('div', { class: 'sz' }, sz + (t.sliders?.length ? ' · settings' : '')));
  }));
}
$('partSearch').addEventListener('input', renderParts);

function renderSwatches() {
  $('swatches').replaceChildren(...S.catalog.palette.map((c, i) => el('i', {
    style: `background:${c}`, title: `${i}: ${c}`,
    class: (i === S.color1 ? 'on1' : '') + (i === S.color2 ? ' on2' : ''),
    onclick: () => { S.color1 = i; renderSwatches(); if (S.part) renderParts(); },
    oncontextmenu: (e) => { e.preventDefault(); S.color2 = i; renderSwatches(); },
  })));
  $('chosen1').style.background = paletteHex(S.color1);
  $('chosen2').style.background = S.color2 == null ? 'transparent' : paletteHex(S.color2);
}

// ---------- selection & properties ----------
function select(id) {
  S.selected = id;
  updateSelection();
  renderProps();
}
function updateSelection() {
  const b = S.scene.blocks.find((x) => x.id === S.selected);
  if (!b) { selBox.visible = false; if (S.selected != null) { S.selected = null; renderProps(); } return; }
  selBox.box.copy(boxOf(b)).expandByScalar(0.03);
  selBox.visible = true;
}

async function renderProps() {
  const box = $('props');
  if (S.selected == null) { box.className = 'props empty'; box.textContent = 'Select a block to see its settings.'; return; }
  let info;
  try { info = await api('block/' + S.selected); } catch { return; }
  const b = info.block;
  const unr = !!S.state?.unrestricted;
  const posIn = b.pos.map((v, i) => el('input', { type: 'number', value: v, step: 1, onchange: (e) => {
    const p = b.pos.slice(); p[i] = parseInt(e.target.value, 10) || 0;
    act(() => api('transform', { id: b.id, pos: p, allowFloating: true }));
  } }));
  const settings = (b.settings || []).map((s) => {
    const min = s.min ?? 0, max = s.max ?? 1;
    const val = s.value ?? null;
    const over = val != null && (val < min || val > max);
    const num = el('input', { type: 'number', value: val ?? '', placeholder: 'game default', step: 'any' });
    const rng = el('input', { type: 'range', min, max, step: (max - min) / 200 || 0.01, value: val ?? (min + max) / 2 });
    const commit = async (v) => {
      const r = await act(() => api('slider', { id: b.id, index: s.index, value: v }));
      if (r?.capped) toast(`Limited to the in-game range (${min} to ${max}). Tick Unrestricted to go past it.`);
    };
    num.addEventListener('change', () => commit(parseFloat(num.value)));
    rng.addEventListener('input', () => (num.value = rng.value));
    rng.addEventListener('change', () => commit(parseFloat(rng.value)));
    return el('div', { class: 'setting' + (over ? ' over' : '') },
      el('div', { class: 'top' }, el('span', {}, s.label), el('span', { class: 'range' }, `game range ${fmt(min)} to ${fmt(max)}`)),
      rng, num,
      over ? el('div', { class: 'warn' }, 'Past the game range. The game clamps this when it loads the blueprint.') : null,
      unr ? el('div', { class: 'range' }, 'Unrestricted: any value is written') : null);
  });
  const actionOpts = (cur) => ['None', ...Object.values(S.catalog.actions).filter((a) => /^(Structure|Custom|Boost|AltBoost|Shoot|AltShoot|Brake|Extend|Compress|RotateCW|RotateCCW|Detach)/.test(a))]
    .map((a) => el('option', { value: a, ...(a === cur ? { selected: '' } : {}) }, a));
  const keys = b.settings || b.keys ? [
    el('label', { class: 'field' }, 'Key (positive)', el('select', { onchange: (e) => act(() => api('keys', { id: b.id, positive: e.target.value })) }, ...actionOpts(b.keys?.positive ?? 'None'))),
    el('label', { class: 'field' }, 'Key (negative)', el('select', { onchange: (e) => act(() => api('keys', { id: b.id, negative: e.target.value })) }, ...actionOpts(b.keys?.negative ?? 'None'))),
  ] : [];
  box.className = 'props';
  box.replaceChildren(...keep(
    el('h3', {}, b.type),
    el('div', { class: 'sub' }, `#${b.id} · rotation ${b.rot.map((r) => r * 90 + '°').join(' ')} · connected to ${info.connectedTo.length} block${info.connectedTo.length === 1 ? '' : 's'}`),
    el('label', { class: 'field' }, 'Position (x, y, z)', el('div', { class: 'row3' }, ...posIn)),
    el('div', { class: 'chosen' }, el('span', {}, 'Colour'), el('i', { style: `background:${b.color == null ? 'transparent' : paletteHex(b.color)}` }),
      el('span', {}, 'Second'), el('i', { style: `background:${b.color2 == null ? 'transparent' : paletteHex(b.color2)}` })),
    ...settings, ...keys,
    el('div', { class: 'actions' },
      el('button', { onclick: () => act(() => api('color', { ids: [b.id], color: S.color1 })) }, 'Paint main'),
      el('button', { onclick: () => act(() => api('color', { ids: [b.id], color2: S.color2 ?? S.color1 })) }, 'Paint second'),
      el('button', { onclick: () => { const id = b.id; select(null); act(() => api('remove', { ids: [id] })); } }, 'Remove')),
  ));
}
const fmt = (v) => (Math.abs(v) >= 100 ? v.toFixed(0) : +v.toFixed(3)).toString();

// ---------- build info, save, open ----------
$('buildName').addEventListener('change', (e) => act(() => api('info', { name: e.target.value })));
$('buildDesc').addEventListener('change', (e) => act(() => api('info', { description: e.target.value })));

async function save(asNew) {
  try {
    const r = await api('save', { asNew: asNew || !S.state?.source });
    toast('Saved ' + r.path.split(/[\\/]/).pop() + '. Reopen the blueprint menu in the game to see it.', true);
    await refreshState();
  } catch (e) { toast(e.message); }
}

function confirmBox(text) {
  return new Promise((resolve) => {
    const d = $('confirm');
    $('confirmText').textContent = text;
    $('confirmYes').onclick = () => { d.close(); resolve(true); };
    $('confirmNo').onclick = () => { d.close(); resolve(false); };
    d.showModal();
  });
}

async function newBuild() {
  let r = await api('new', { name: 'New build', discard: false });
  if (r.needsConfirm) { if (!(await confirmBox('Discard the unsaved changes and start a new build?'))) return; r = await api('new', { name: 'New build', discard: true }); }
  select(null); S.rot = fromEuler(0, 0, 0);
  await refreshScene(true);
}

let libCache = [];
async function openLibrary() {
  const d = $('library');
  d.showModal();
  $('libGrid').replaceChildren(el('div', { class: 'small' }, 'Reading blueprints...'));
  try { libCache = await api('library'); } catch (e) { toast(e.message); return; }
  const folders = [...new Set(libCache.map((e) => e.folder))];
  $('libFolder').replaceChildren(el('option', { value: '' }, 'All folders'), ...folders.map((f) => el('option', { value: f }, f)));
  renderLibrary();
}
function renderLibrary() {
  const q = $('libSearch').value.trim().toLowerCase(), f = $('libFolder').value;
  const list = libCache.filter((e) => (!f || e.folder === f) && (!q || e.name.toLowerCase().includes(q) || e.file.toLowerCase().includes(q)));
  $('libGrid').replaceChildren(...list.map((e) => el('div', { class: 'card', title: e.error ? 'Cannot read: ' + e.error : e.file, onclick: () => openFile(e) },
    el('img', { src: 'api/library/picture?path=' + encodeURIComponent(e.path), loading: 'lazy', alt: '' }),
    el('div', { class: 'meta' }, el('div', { class: 'nm' }, e.name || '(no name)'),
      el('div', { class: 'sub' }, e.error ? 'unreadable' : `${e.blocks} blocks · ${new Date(e.modified).toLocaleDateString()}`),
      el('div', { class: 'sub' }, e.folder)))));
}
$('libSearch').addEventListener('input', renderLibrary);
$('libFolder').addEventListener('change', renderLibrary);
document.querySelectorAll('[data-close]').forEach((b) => b.addEventListener('click', () => b.closest('dialog').close()));

async function openFile(e) {
  if (e.error) { toast('That file cannot be read: ' + e.error); return; }
  let r = await api('open', { path: e.path, discard: false });
  if (r.needsConfirm) { if (!(await confirmBox('Discard the unsaved changes and open this blueprint?'))) return; r = await api('open', { path: e.path, discard: true }); }
  $('library').close();
  select(null);
  await refreshScene(true);
}

document.querySelectorAll('[data-act]').forEach((b) => b.addEventListener('click', () => ({
  new: newBuild, open: openLibrary, save: () => save(false), saveNew: () => save(true),
  undo: () => act(() => api('undo', {})), redo: () => act(() => api('redo', {})),
})[b.dataset.act]()));

// ---------- settings: unrestricted + AI port ----------
$('unrestricted').addEventListener('change', async (e) => {
  if (e.target.checked && !(await confirmBox('Unrestricted lets block settings go past the game\'s own limits. Note: the game clamps most settings back into range when it loads a blueprint, so values past the limit may have no effect in game. Turn it on?'))) {
    e.target.checked = false; return;
  }
  await api('settings', { unrestricted: e.target.checked });
  await refreshState(); renderProps();
});
$('aiEnabled').addEventListener('change', async (e) => { await api('settings', { aiEnabled: e.target.checked, aiPort: parseInt($('aiPort').value, 10) }); refreshState(); });
$('aiPort').addEventListener('change', async (e) => { await api('settings', { aiPort: parseInt(e.target.value, 10) }); refreshState(); });
$('aiCanSave').addEventListener('change', async (e) => { await api('settings', { aiCanSave: e.target.checked }); refreshState(); });

function renderState() {
  const s = S.state;
  $('unrestricted').checked = s.unrestricted;
  $('aiEnabled').checked = s.ai.enabled;
  if (document.activeElement !== $('aiPort')) $('aiPort').value = s.ai.port;
  $('aiCanSave').checked = s.ai.canSave;
  const pill = $('aiPill');
  pill.textContent = s.ai.running ? 'listening' : s.ai.error ? 'error' : 'off';
  pill.className = 'pill' + (s.ai.running ? ' live' : s.ai.error ? ' err' : '');
  $('aiInfo').replaceChildren(...keep(
    s.ai.running ? el('div', {}, 'MCP endpoint: ', el('code', {}, s.ai.url)) : el('div', {}, s.ai.error ? 'Could not start: ' + s.ai.error : 'Off. Tick "AI port" to let an AI model build in this window.'),
    s.ai.running ? el('div', {}, 'Claude Code: ', el('code', {}, `claude mcp add --transport http trailwright ${s.ai.url}`)) : null,
    s.ai.lastActivity ? el('div', {}, 'Last AI action: ' + s.ai.lastActivity) : null));
  if (document.activeElement !== $('buildName')) $('buildName').value = s.name;
  if (document.activeElement !== $('buildDesc')) $('buildDesc').value = s.description;
  $('buildStats').textContent = `${s.blocks} blocks${s.dirty ? ' · unsaved changes' : ''}`;
  $('buildTitle').textContent = (s.name || 'Untitled') + (s.dirty ? ' *' : '');
  $('source').textContent = s.source ? s.source.split(/[\\/]/).slice(-2).join('/') : 'not saved yet';
  document.title = 'Trailwright - ' + (s.name || 'Untitled');
}

async function refreshState() {
  S.state = await api('state');
  renderState();
  return S.state;
}

async function refreshScene(reframe = false) {
  S.scene = await api('scene');
  S.revision = S.scene.revision;
  rebuildBlocks();
  if (reframe) frameAll();
  await refreshState();
  if (S.selected != null) renderProps();
}

// live view of changes made through the AI port
setInterval(async () => {
  try {
    const st = await refreshState();
    if (st.revision !== S.revision) await refreshScene(S.scene.blocks.length === 0);
  } catch { /* app closing */ }
}, 700);

// ---------- start ----------
(async () => {
  S.catalog = await api('catalog');
  S.catalog.blocks.forEach((b) => S.byGuid.set(b.guid, b));
  renderCats(); renderSwatches(); renderParts(); setTool('select');
  resize();
  await refreshScene(true);
})();
