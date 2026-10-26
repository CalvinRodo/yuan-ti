/* ================================================================
   Dark Sun GFF Viewer — view-map.js
   Region map viewer: loads a server-rendered PNG of the full map
   (RMAP tiles + ETAB objects + WALL sprites) and provides
   CSS-transform pan/zoom interaction. A list of regions (with the
   names saved in the Regions view) sits beside the map, and debug
   layers like the game's own (grid, GMAP collision, script triggers,
   ETAB objects) draw on a canvas over it, with the cells painted as
   water or sand for the engine's splashes and dust.

   Depends on globals from app.js: state, dom, palParams(), formatBytes()
   ================================================================ */

'use strict';

// Zoom steps: effective tile sizes in pixels (native render is 16 px/tile).
const TILE_SIZES = [4, 8, 12, 16, 24, 32, 48, 64];
const NATIVE_TILE_PX = 16;

const mapState = {
  loadedUrl:   null,   // URL of the currently displayed render
  imgW:        0,      // natural width of the loaded image
  imgH:        0,      // natural height of the loaded image
  zoom:        1.0,    // CSS scale multiplier (1.0 = native 16 px/tile)
  zoomIdx:     TILE_SIZES.indexOf(NATIVE_TILE_PX),
  pan:         { x: 0, y: 0 },
  drag:        { active: false, startX: 0, startY: 0, startPanX: 0, startPanY: 0 },
  // kept so app.js code that resets these doesn't error
  tiles:       null,
  tileCache:   { clear() {} },
  dirty:       false,
  rmapList:    [],
  rmapNumber:  null,
  environment: 'outdoors',
  environmentRequest: 0,
  names:        {},     // custom region names by file, from /api/map-names
  regionListKey: null,  // what the region list was last built from
  debug:        null,   // /api/gff/<name>/map-debug for debugFor
  debugFor:     null,   // `${game}|${gff}` the debug data belongs to
  debugRequest: 0,
  hoverCell:    null,   // {x, y} under the pointer while a debug layer is on
  terrain:      new Map(), // 'x,y' -> 'water' | 'sand' for debugFor
  paint:        null,   // {start, end} cells of the rectangle being painted
  terrainRequest: 0,
};

const MAP_DEBUG_COLORS = {
  blocked:  'rgba(255, 38, 26, 0.35)',
  sight:    'rgba(77, 153, 255, 0.9)',
  occupied: 'rgba(255, 230, 26, 0.45)',
  trigger:  'rgba(255, 51, 255, 0.8)',
  tile:     'rgba(255, 51, 255, 0.4)',
  grid:     'rgba(255, 255, 255, 0.12)',
  major:    'rgba(255, 255, 255, 0.3)',
  hover:    'rgba(255, 255, 255, 0.95)',
  creature: '#ffe64d',
  door:     '#66e6ff',
  thing:    '#ffffff',
  water:    'rgba(40, 120, 255, 0.45)',
  sand:     'rgba(240, 200, 80, 0.45)',
  erase:    'rgba(255, 255, 255, 0.9)',
};

// ---------------------------------------------------------------------------
// Show / hide
// ---------------------------------------------------------------------------

function showMapView() {
  dom.content.classList.add('map-active');
  dom.mapView.classList.remove('hidden');
  applyTransform();
  loadMapEnvironment();
  renderMapRegionList();
  loadMapDebug();

  const url = _buildRenderUrl();
  if (url && url !== mapState.loadedUrl) {
    loadMapRender(url);
  }
}

function hideMapView() {
  dom.content.classList.remove('map-active');
  dom.mapView.classList.add('hidden');
}

// ---------------------------------------------------------------------------
// URL builder
// ---------------------------------------------------------------------------

function _buildRenderUrl() {
  if (!state.currentGff) return null;
  return `/api/gff/${encodeURIComponent(state.currentGff)}/map-render`
       + `?game=${state.game}${palParams()}`;
}

function _mapEnvironmentUrl() {
  return `/api/gff/${encodeURIComponent(state.currentGff)}/map-environment?game=${encodeURIComponent(state.game)}`;
}

async function loadMapEnvironment() {
  const id = ++mapState.environmentRequest;
  dom.mapEnvironment.disabled = true;
  dom.mapEnvironmentStatus.textContent = 'Loading...';
  try {
    const res = await fetch(_mapEnvironmentUrl(), { cache: 'no-store' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    if (id !== mapState.environmentRequest) return;
    mapState.environment = data.environment;
    dom.mapEnvironment.value = data.environment;
    dom.mapEnvironment.disabled = false;
    dom.mapEnvironmentStatus.textContent = data.configured ? 'Saved' : 'Default: outdoors';
  } catch (err) {
    if (id !== mapState.environmentRequest) return;
    dom.mapEnvironmentStatus.textContent = `Unable to load environment: ${err.message}`;
    console.error('Failed to load map environment:', err);
  }
}

async function saveMapEnvironment() {
  const id = ++mapState.environmentRequest;
  const environment = dom.mapEnvironment.value;
  dom.mapEnvironment.disabled = true;
  dom.mapEnvironmentStatus.textContent = 'Saving...';
  try {
    const res = await fetch(_mapEnvironmentUrl(), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ environment }),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    if (id !== mapState.environmentRequest) return;
    mapState.environment = data.environment;
    dom.mapEnvironmentStatus.textContent = 'Saved';
  } catch (err) {
    if (id !== mapState.environmentRequest) return;
    dom.mapEnvironment.value = mapState.environment;
    dom.mapEnvironmentStatus.textContent = `Unable to save environment: ${err.message}`;
    console.error('Failed to save map environment:', err);
  } finally {
    if (id === mapState.environmentRequest) dom.mapEnvironment.disabled = false;
  }
}

// ---------------------------------------------------------------------------
// Load rendered map image from server
// ---------------------------------------------------------------------------

async function loadMapRender(url) {
  dom.mapSpinner.classList.remove('hidden');
  dom.mapRenderImg.style.display = 'none';
  mapState.loadedUrl = null;

  try {
    const res = await fetch(url, { cache: 'no-cache' });
    if (!res.ok) {
      console.error(`map-render failed: HTTP ${res.status}`);
      return;
    }
    const blob = await res.blob();
    const objUrl = URL.createObjectURL(blob);

    // Clean up previous object URL
    if (dom.mapRenderImg._objUrl) URL.revokeObjectURL(dom.mapRenderImg._objUrl);
    dom.mapRenderImg._objUrl = objUrl;

    await new Promise((resolve, reject) => {
      dom.mapRenderImg.onload  = resolve;
      dom.mapRenderImg.onerror = reject;
      dom.mapRenderImg.src = objUrl;
    });

    mapState.imgW      = dom.mapRenderImg.naturalWidth;
    mapState.imgH      = dom.mapRenderImg.naturalHeight;
    mapState.loadedUrl = url;  // store without cache-bust for comparison
    mapState.pan       = { x: 0, y: 0 };

    dom.mapRenderImg.style.display = 'block';
    applyTransform();
  } catch (err) {
    console.error('Failed to load map render:', err);
  } finally {
    dom.mapSpinner.classList.add('hidden');
  }
}

// ---------------------------------------------------------------------------
// Region list (second pane)
// ---------------------------------------------------------------------------

async function renderMapRegionList() {
  const game = state.game;
  try {
    const res = await fetch(`/api/map-names?game=${encodeURIComponent(game)}`, { cache: 'no-store' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const names = await res.json();
    if (game === state.game) mapState.names = names;
  } catch (err) {
    console.error('Failed to load map names:', err);
  }
  if (game !== state.game || state.mode !== 'map') return;

  const files = state.gffListByMode.map;
  const key = JSON.stringify([game, files, mapState.names]);
  if (key !== mapState.regionListKey) {
    mapState.regionListKey = key;
    dom.mapRegionList.replaceChildren(...files.map(file => {
      const item = document.createElement('li');
      const button = document.createElement('button');
      button.type = 'button';
      button.dataset.file = file;
      const name = mapState.names[file.toUpperCase()];
      button.textContent = name || file;
      if (name) {
        const small = document.createElement('span');
        small.className = 'map-region-file';
        small.textContent = file;
        button.appendChild(small);
      }
      button.addEventListener('click', () => {
        if (file === state.currentGff) return;
        loadGffForMode(file, 'map');
        pushUrlState();
      });
      item.appendChild(button);
      return item;
    }));
    filterMapRegionList();
  }
  dom.mapRegionList.querySelectorAll('button').forEach(button => {
    const current = button.dataset.file === state.currentGff;
    button.setAttribute('aria-current', current);
    if (current) button.scrollIntoView({ block: 'nearest' });
  });
}

function filterMapRegionList() {
  const query = dom.mapRegionFilter.value.trim().toLowerCase();
  dom.mapRegionList.querySelectorAll('li').forEach(item => {
    item.classList.toggle('hidden', Boolean(query) && !item.textContent.toLowerCase().includes(query));
  });
}

// ---------------------------------------------------------------------------
// Debug layers: grid, GMAP collision, script triggers, ETAB objects, terrain
// ---------------------------------------------------------------------------

function mapDebugActive() {
  return Object.values(prefs.mapDebugLayers).some(Boolean);
}

async function loadMapDebug() {
  dom.mapDebugLayers.querySelectorAll('input').forEach(input => {
    input.checked = Boolean(prefs.mapDebugLayers[input.dataset.layer]);
  });
  const key = `${state.game}|${state.currentGff}`;
  if (!mapDebugActive() || mapState.debugFor === key) {
    drawMapDebug();
    return;
  }
  const id = ++mapState.debugRequest;
  const url = endpoint => `/api/gff/${encodeURIComponent(state.currentGff)}/${endpoint}?game=${encodeURIComponent(state.game)}`;
  try {
    const [data, terrain] = await Promise.all(['map-debug', 'map-terrain'].map(async endpoint => {
      const res = await fetch(url(endpoint), { cache: 'no-store' });
      if (!res.ok) throw new Error(`${endpoint}: HTTP ${res.status}`);
      return res.json();
    }));
    if (id !== mapState.debugRequest) return;
    mapState.debug = data;
    mapState.terrain = terrainCells(terrain.cells);
    mapState.debugFor = key;
  } catch (err) {
    if (id !== mapState.debugRequest) return;
    mapState.debug = null;
    mapState.debugFor = null;
    console.error('Failed to load map debug data:', err);
  }
  drawMapDebug();
}

function drawMapDebug() {
  const layers = prefs.mapDebugLayers;
  const data = mapState.debugFor === `${state.game}|${state.currentGff}` ? mapState.debug : null;
  const canvas = dom.mapDebugCanvas;
  canvas.classList.toggle('hidden', !data || !mapDebugActive());
  if (canvas.classList.contains('hidden')) {
    dom.mapDebugInfo.classList.add('hidden');
    return;
  }
  const S = NATIVE_TILE_PX;
  canvas.width = data.width * S;
  canvas.height = data.height * S;
  const ctx = canvas.getContext('2d');

  if (layers.terrain) {
    mapState.terrain.forEach((kind, cell) => {
      const [x, y] = cell.split(',').map(Number);
      ctx.fillStyle = MAP_DEBUG_COLORS[kind];
      ctx.fillRect(x * S, y * S, S, S);
    });
  }

  if (layers.collision) {
    for (let y = 0; y < data.height; y++) {
      for (let x = 0; x < data.width; x++) {
        const flags = data.gmap[y * data.width + x];
        if (flags & 0x40) { ctx.fillStyle = MAP_DEBUG_COLORS.blocked; ctx.fillRect(x * S, y * S, S, S); }
        if (flags & 0x20) { ctx.fillStyle = MAP_DEBUG_COLORS.occupied; ctx.fillRect(x * S, y * S, S, S); }
        if (flags & 0x80) { ctx.strokeStyle = MAP_DEBUG_COLORS.sight; ctx.lineWidth = 1; ctx.strokeRect(x * S + 1.5, y * S + 1.5, S - 3, S - 3); }
      }
    }
  }

  if (layers.triggers) {
    data.triggers.filter(t => t.x != null && t.y != null && t.width != null && t.height != null).forEach(t => {
      if (t.kind.endsWith('tile')) {
        ctx.fillStyle = MAP_DEBUG_COLORS.tile;
        ctx.fillRect(t.x * S, t.y * S, S, S);
      } else {
        ctx.strokeStyle = MAP_DEBUG_COLORS.trigger;
        ctx.lineWidth = 2;
        ctx.strokeRect(t.x * S + 1, t.y * S + 1, t.width * S - 2, t.height * S - 2);
      }
    });
  }

  if (layers.grid) {
    ctx.lineWidth = 1;
    for (let x = 0; x <= data.width; x++) {
      ctx.strokeStyle = x % 8 ? MAP_DEBUG_COLORS.grid : MAP_DEBUG_COLORS.major;
      ctx.beginPath(); ctx.moveTo(x * S + 0.5, 0); ctx.lineTo(x * S + 0.5, canvas.height); ctx.stroke();
    }
    for (let y = 0; y <= data.height; y++) {
      ctx.strokeStyle = y % 8 ? MAP_DEBUG_COLORS.grid : MAP_DEBUG_COLORS.major;
      ctx.beginPath(); ctx.moveTo(0, y * S + 0.5); ctx.lineTo(canvas.width, y * S + 0.5); ctx.stroke();
    }
    ctx.font = '7px monospace';
    ctx.textBaseline = 'top';
    ctx.fillStyle = 'rgba(255, 255, 255, 0.7)';
    for (let y = 0; y < data.height; y += 8) {
      for (let x = 0; x < data.width; x += 8) ctx.fillText(`${x},${y}`, x * S + 2, y * S + 2);
    }
  }

  if (layers.objects) {
    ctx.font = '7px monospace';
    ctx.textBaseline = 'bottom';
    // Stack labels that would overprint a neighbour's, as the engine's overlay does.
    const placed = [];
    [...data.objects].sort((a, b) => a.cell[1] - b.cell[1] || a.cell[0] - b.cell[0]).forEach(obj => {
      ctx.fillStyle = MAP_DEBUG_COLORS[obj.kind];
      ctx.fillRect(obj.x - 1, obj.y - 1, 3, 3);
      const text = `${obj.index}:${obj.ojff}`;
      const box = { x: obj.cell[0] * S + 1, y: obj.cell[1] * S + S - 9, w: ctx.measureText(text).width, h: 8 };
      const overlaps = () => placed.some(p => p.x < box.x + box.w && box.x < p.x + p.w && p.y < box.y + box.h && box.y < p.y + p.h);
      for (let n = 0; n < 6 && overlaps(); n++) box.y += 8;
      placed.push(box);
      ctx.fillStyle = '#000';
      ctx.fillText(text, box.x + 1, box.y + box.h + 1);
      ctx.fillStyle = MAP_DEBUG_COLORS[obj.kind];
      ctx.fillText(text, box.x, box.y + box.h);
    });
  }

  if (mapState.paint) {
    const { x0, y0, x1, y1 } = paintRect(mapState.paint);
    ctx.strokeStyle = MAP_DEBUG_COLORS[dom.mapTerrainTool.value];
    ctx.lineWidth = 2;
    ctx.strokeRect(x0 * S + 1, y0 * S + 1, (x1 - x0 + 1) * S - 2, (y1 - y0 + 1) * S - 2);
  }

  const hover = mapState.hoverCell;
  dom.mapDebugInfo.classList.toggle('hidden', !hover);
  if (hover) {
    ctx.strokeStyle = MAP_DEBUG_COLORS.hover;
    ctx.lineWidth = 1;
    ctx.strokeRect(hover.x * S + 0.5, hover.y * S + 0.5, S - 1, S - 1);
    dom.mapDebugInfo.textContent = describeMapCell(data, hover);
  }
}

function describeMapCell(data, c) {
  const hex = n => (n == null ? '?' : n.toString(16).toUpperCase().padStart(4, '0'));
  const runs = t => `GPL ${t.file ?? '?'} @${hex(t.address)} (from ${t.source})`;
  const flags = data.gmap[c.y * data.width + c.x];
  const named = [];
  if (flags & 0x40) named.push('blocked');
  if (flags & 0x80) named.push('blocks sight');
  if (flags & 0x20) named.push('occupied');
  if (flags & 0x1F) named.push(`wall ${flags & 0x1F}`);
  const lines = [
    `Cell (${c.x},${c.y}) · pixel (${c.x * NATIVE_TILE_PX},${c.y * NATIVE_TILE_PX}) · region ${data.region}`,
    `GMAP 0x${flags.toString(16).padStart(2, '0')}: ${named.join(', ') || 'open'}`,
    `Terrain: ${mapState.terrain.get(`${c.x},${c.y}`) || 'unpainted'}`,
  ];
  data.objects.filter(o => o.cell[0] === c.x && o.cell[1] === c.y).forEach(obj => {
    lines.push(`Object #${obj.index} OJFF ${obj.ojff} (${obj.kind}) at (${obj.x},${obj.y})`);
    data.triggers.filter(t => t.objects?.includes(obj.ojff)).forEach(t => lines.push(`  ${t.kind} → ${runs(t)}`));
  });
  data.triggers.filter(t => t.x != null && t.y != null && t.width != null && t.height != null
    && c.x >= t.x && c.x < t.x + t.width && c.y >= t.y && c.y < t.y + t.height).forEach(t => {
    const where = t.kind.endsWith('tile') ? `(${t.x},${t.y})` : `(${t.x},${t.y}) ${t.width}×${t.height}`;
    lines.push(`${t.kind} ${where}${t.who === 0 ? ', active member only' : ''} → ${runs(t)}`);
  });
  return lines.join('\n');
}

// The map cell under a mouse event; clamped to the map, or null outside it.
function mapCellAt(e, clamp = false) {
  const rect = dom.mapCanvasWrap.getBoundingClientRect();
  const S = NATIVE_TILE_PX * mapState.zoom;
  let x = Math.floor((e.clientX - rect.left - mapState.pan.x) / S);
  let y = Math.floor((e.clientY - rect.top - mapState.pan.y) / S);
  const { width, height } = mapState.debug;
  if (clamp) {
    x = Math.max(0, Math.min(width - 1, x));
    y = Math.max(0, Math.min(height - 1, y));
  }
  return x >= 0 && y >= 0 && x < width && y < height ? { x, y } : null;
}

function updateMapHover(e) {
  if (!mapDebugActive() || !mapState.debug || mapState.drag.active) return;
  const cell = mapCellAt(e);
  if (cell?.x === mapState.hoverCell?.x && cell?.y === mapState.hoverCell?.y) return;
  mapState.hoverCell = cell;
  drawMapDebug();
}

// ---------------------------------------------------------------------------
// Terrain painting: drag a rectangle of cells as water or sand, or erase it
// ---------------------------------------------------------------------------

function terrainCells(cells) {
  return new Map(Object.entries(cells).flatMap(([kind, points]) => points.map(([x, y]) => [`${x},${y}`, kind])));
}

function paintRect({ start, end }) {
  return {
    x0: Math.min(start.x, end.x), y0: Math.min(start.y, end.y),
    x1: Math.max(start.x, end.x), y1: Math.max(start.y, end.y),
  };
}

function selectTerrainTool() {
  if (dom.mapTerrainTool.value && !prefs.mapDebugLayers.terrain) {
    prefs.mapDebugLayers = { ...prefs.mapDebugLayers, terrain: true };
    savePrefs();
    loadMapDebug();
  }
  dom.mapCanvasWrap.classList.toggle('painting', Boolean(dom.mapTerrainTool.value));
}

async function finishPaint() {
  const { x0, y0, x1, y1 } = paintRect(mapState.paint);
  mapState.paint = null;
  const tool = dom.mapTerrainTool.value;
  const terrain = new Map(mapState.terrain);
  for (let y = y0; y <= y1; y++) {
    for (let x = x0; x <= x1; x++) {
      if (tool === 'erase') terrain.delete(`${x},${y}`);
      else terrain.set(`${x},${y}`, tool);
    }
  }
  const key = mapState.debugFor;
  mapState.terrain = terrain;
  drawMapDebug();

  const cells = {};
  terrain.forEach((kind, cell) => (cells[kind] ||= []).push(cell.split(',').map(Number)));
  const id = ++mapState.terrainRequest;
  dom.mapTerrainStatus.textContent = 'Saving...';
  const [game, gff] = key.split('|');
  try {
    const res = await fetch(`/api/gff/${encodeURIComponent(gff)}/map-terrain?game=${encodeURIComponent(game)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cells }),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}: ${await res.text()}`);
    const saved = terrainCells((await res.json()).cells);
    if (id !== mapState.terrainRequest) return;
    if (mapState.debugFor === key) {
      mapState.terrain = saved;
      drawMapDebug();
    }
    dom.mapTerrainStatus.textContent = `Saved (${saved.size} cells)`;
  } catch (err) {
    if (id !== mapState.terrainRequest) return;
    dom.mapTerrainStatus.textContent = `Unable to save terrain: ${err.message}`;
    console.error('Failed to save map terrain:', err);
    // Show what is saved, not the stroke that failed.
    mapState.debugFor = null;
    if (`${state.game}|${state.currentGff}` === key) loadMapDebug();
  }
}

// ---------------------------------------------------------------------------
// CSS transform
// ---------------------------------------------------------------------------

function applyTransform() {
  const { pan, zoom } = mapState;
  dom.mapRenderImg.style.transform =
    `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`;
  dom.mapDebugCanvas.style.transform = dom.mapRenderImg.style.transform;
}

function clampPan() {
  if (!mapState.imgW) return;
  const scaledW = mapState.imgW * mapState.zoom;
  const scaledH = mapState.imgH * mapState.zoom;
  const ww = dom.mapCanvasWrap.clientWidth;
  const wh = dom.mapCanvasWrap.clientHeight;

  // Allow panning so at least one tile-row/col remains visible
  const margin = NATIVE_TILE_PX * mapState.zoom;
  mapState.pan.x = Math.max(-(scaledW - margin), Math.min(ww - margin, mapState.pan.x));
  mapState.pan.y = Math.max(-(scaledH - margin), Math.min(wh - margin, mapState.pan.y));
}

// ---------------------------------------------------------------------------
// Zoom helpers
// ---------------------------------------------------------------------------

function _applyZoom(newIdx, pivotX, pivotY) {
  // Pivot-aware zoom: keep the canvas-relative point (pivotX, pivotY) fixed.
  // Before zoom: point in image coords = (pivotX - pan.x) / oldZoom
  // After  zoom: same image coord must map back to same canvas point.
  const oldZoom = mapState.zoom;
  mapState.zoomIdx = Math.max(0, Math.min(TILE_SIZES.length - 1, newIdx));
  mapState.zoom    = TILE_SIZES[mapState.zoomIdx] / NATIVE_TILE_PX;

  // Anchor the pivot point
  const imgX = (pivotX - mapState.pan.x) / oldZoom;
  const imgY = (pivotY - mapState.pan.y) / oldZoom;
  mapState.pan.x = pivotX - imgX * mapState.zoom;
  mapState.pan.y = pivotY - imgY * mapState.zoom;

  clampPan();
  applyTransform();
  updateZoomLabel();
}

function updateZoomLabel() {
  const px = Math.round(TILE_SIZES[mapState.zoomIdx]);
  dom.mapZoomLabel.textContent = `${px}px`;
}

// ---------------------------------------------------------------------------
// Map control events
// ---------------------------------------------------------------------------

function bindMapEvents() {
  dom.mapEnvironment.addEventListener('change', saveMapEnvironment);
  dom.mapRegionFilter.addEventListener('input', filterMapRegionList);

  dom.mapDebugLayers.addEventListener('change', e => {
    prefs.mapDebugLayers = { ...prefs.mapDebugLayers, [e.target.dataset.layer]: e.target.checked };
    savePrefs();
    if (!mapDebugActive()) mapState.hoverCell = null;
    loadMapDebug();
  });
  dom.mapCanvasWrap.addEventListener('mousemove', updateMapHover);
  dom.mapCanvasWrap.addEventListener('mouseleave', () => {
    mapState.hoverCell = null;
    drawMapDebug();
  });

  dom.mapZoomIn.addEventListener('click', () => {
    const cx = dom.mapCanvasWrap.clientWidth  / 2;
    const cy = dom.mapCanvasWrap.clientHeight / 2;
    _applyZoom(mapState.zoomIdx + 1, cx, cy);
  });

  dom.mapZoomOut.addEventListener('click', () => {
    const cx = dom.mapCanvasWrap.clientWidth  / 2;
    const cy = dom.mapCanvasWrap.clientHeight / 2;
    _applyZoom(mapState.zoomIdx - 1, cx, cy);
  });

  dom.mapResetPan.addEventListener('click', () => {
    mapState.pan = { x: 0, y: 0 };
    applyTransform();
  });

  // Scroll-to-zoom
  dom.mapCanvasWrap.addEventListener('wheel', e => {
    e.preventDefault();
    const rect = dom.mapCanvasWrap.getBoundingClientRect();
    const px   = e.clientX - rect.left;
    const py   = e.clientY - rect.top;
    _applyZoom(mapState.zoomIdx + (e.deltaY < 0 ? 1 : -1), px, py);
  }, { passive: false });

  dom.mapTerrainTool.addEventListener('change', selectTerrainTool);

  // Mouse drag: paints with a terrain tool chosen (left button), else pans
  dom.mapCanvasWrap.addEventListener('mousedown', e => {
    if (e.button === 0 && dom.mapTerrainTool.value && mapState.debug) {
      const start = mapCellAt(e);
      if (start) mapState.paint = { start, end: start };
      drawMapDebug();
      e.preventDefault();
      return;
    }
    mapState.drag = {
      active:    true,
      startX:    e.clientX,
      startY:    e.clientY,
      startPanX: mapState.pan.x,
      startPanY: mapState.pan.y,
    };
    dom.mapCanvasWrap.classList.add('dragging');
    e.preventDefault();
  });
  document.addEventListener('mousemove', e => {
    if (mapState.paint) {
      const end = mapCellAt(e, true);
      if (end.x !== mapState.paint.end.x || end.y !== mapState.paint.end.y) {
        mapState.paint.end = end;
        drawMapDebug();
      }
      return;
    }
    if (!mapState.drag.active) return;
    mapState.pan.x = mapState.drag.startPanX + (e.clientX - mapState.drag.startX);
    mapState.pan.y = mapState.drag.startPanY + (e.clientY - mapState.drag.startY);
    clampPan();
    applyTransform();
  });
  document.addEventListener('mouseup', () => {
    if (mapState.paint) finishPaint();
    if (!mapState.drag.active) return;
    mapState.drag.active = false;
    dom.mapCanvasWrap.classList.remove('dragging');
  });

  // Touch drag
  dom.mapCanvasWrap.addEventListener('touchstart', e => {
    const t = e.touches[0];
    mapState.drag = {
      active:    true,
      startX:    t.clientX,
      startY:    t.clientY,
      startPanX: mapState.pan.x,
      startPanY: mapState.pan.y,
    };
    e.preventDefault();
  }, { passive: false });
  dom.mapCanvasWrap.addEventListener('touchmove', e => {
    if (!mapState.drag.active) return;
    const t = e.touches[0];
    mapState.pan.x = mapState.drag.startPanX + (t.clientX - mapState.drag.startX);
    mapState.pan.y = mapState.drag.startPanY + (t.clientY - mapState.drag.startY);
    clampPan();
    applyTransform();
    e.preventDefault();
  }, { passive: false });
  dom.mapCanvasWrap.addEventListener('touchend', () => {
    mapState.drag.active = false;
  });
}
