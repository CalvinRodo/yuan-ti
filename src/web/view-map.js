/* ================================================================
   Dark Sun GFF Viewer — view-map.js
   Region tile-map canvas viewer (RMAP resources).
   Depends on globals from app.js: state, dom, palParams(),
   formatBytes()
   ================================================================ */

'use strict';

const TILE_SIZES = [8, 12, 16, 24, 32, 48, 64];

const mapState = {
  tiles:       null,         // flat Array of tile numbers
  mapWidth:    128,          // columns (set from RMAP or user input)
  mapHeight:   112,          // rows
  tileSize:    16,           // rendered pixels per tile
  tileSizeIdx: TILE_SIZES.indexOf(16),
  pan:         { x: 0, y: 0 },
  drag:        { active: false, startX: 0, startY: 0, startPanX: 0, startPanY: 0 },
  tileCache:   new Map(),    // tileNum -> { status, img }
  rafId:       null,
  dirty:       false,
  rmapList:    [],
  rmapNumber:  null,
  resizeObs:   null,
};

// ---------------------------------------------------------------------------
// Show / hide
// ---------------------------------------------------------------------------

function showMapView() {
  dom.content.classList.add('map-active');
  dom.mapView.classList.remove('hidden');
  buildRmapSelector();
  startMapRaf();
  resizeMapCanvas();
  if (!mapState.tiles) {
    loadRmap();
  } else {
    mapState.dirty = true;
  }
  if (!mapState.resizeObs) {
    mapState.resizeObs = new ResizeObserver(() => {
      resizeMapCanvas();
      mapState.dirty = true;
    });
    mapState.resizeObs.observe(dom.mapCanvasWrap);
  }
}

function hideMapView() {
  dom.content.classList.remove('map-active');
  dom.mapView.classList.add('hidden');
  stopMapRaf();
}

// ---------------------------------------------------------------------------
// RMAP selector
// ---------------------------------------------------------------------------

function buildRmapSelector() {
  dom.rmapSelect.innerHTML = '';
  mapState.rmapList.forEach(r => {
    const opt = document.createElement('option');
    opt.value       = r.number;
    opt.textContent = `RMAP #${r.number} (${formatBytes(r.size)})`;
    if (r.number === mapState.rmapNumber) opt.selected = true;
    dom.rmapSelect.appendChild(opt);
  });
}

// ---------------------------------------------------------------------------
// Load RMAP from server
// ---------------------------------------------------------------------------

async function loadRmap() {
  if (!state.currentGff || !mapState.rmapNumber) return;
  dom.mapSpinner.classList.remove('hidden');
  try {
    const r = await fetch(
      `/api/gff/${encodeURIComponent(state.currentGff)}/rmap/${mapState.rmapNumber}`);
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const data = await r.json();

    mapState.tiles     = data.tiles;
    mapState.mapHeight = data.height;
    mapState.tileCache.clear();
    mapState.pan       = { x: 0, y: 0 };
    mapState.dirty     = true;

    dom.mapWidthInput.value = mapState.mapWidth;
    updateMapSizeLabel();
  } catch { /* ignore */ } finally {
    dom.mapSpinner.classList.add('hidden');
  }
}

// ---------------------------------------------------------------------------
// Canvas resize
// ---------------------------------------------------------------------------

function resizeMapCanvas() {
  const rect = dom.mapCanvasWrap.getBoundingClientRect();
  const w = Math.floor(rect.width);
  const h = Math.floor(rect.height);
  if (w > 0 && h > 0 &&
      (dom.mapCanvas.width !== w || dom.mapCanvas.height !== h)) {
    dom.mapCanvas.width  = w;
    dom.mapCanvas.height = h;
    mapState.dirty = true;
  }
}

// ---------------------------------------------------------------------------
// RAF draw loop
// ---------------------------------------------------------------------------

function startMapRaf() {
  if (mapState.rafId) return;
  (function loop() {
    resizeMapCanvas();
    if (mapState.dirty) {
      drawMap();
      mapState.dirty = false;
    }
    mapState.rafId = requestAnimationFrame(loop);
  })();
}

function stopMapRaf() {
  if (mapState.rafId) { cancelAnimationFrame(mapState.rafId); mapState.rafId = null; }
}

// ---------------------------------------------------------------------------
// Tile image cache
// ---------------------------------------------------------------------------

function getTileImage(tileNum) {
  let entry = mapState.tileCache.get(tileNum);
  if (entry) return entry;
  entry = { status: 'loading', img: null };
  mapState.tileCache.set(tileNum, entry);
  const url = `/api/gff/${encodeURIComponent(state.currentGff)}/image/TILE/${tileNum}?frame=0${palParams()}`;
  const img = new Image();
  img.onload  = () => { entry.status = 'loaded'; entry.img = img; mapState.dirty = true; };
  img.onerror = () => { entry.status = 'error';                   mapState.dirty = true; };
  img.src = url;
  return entry;
}

// ---------------------------------------------------------------------------
// Canvas draw
// ---------------------------------------------------------------------------

function drawMap() {
  const canvas = dom.mapCanvas;
  const ctx    = canvas.getContext('2d');
  const { tiles, mapWidth, tileSize, pan } = mapState;

  ctx.fillStyle = '#0d0c14';
  ctx.fillRect(0, 0, canvas.width, canvas.height);

  if (!tiles || mapWidth < 1) return;

  const mapHeight = Math.ceil(tiles.length / mapWidth);
  const colStart  = Math.max(0, Math.floor(-pan.x / tileSize));
  const rowStart  = Math.max(0, Math.floor(-pan.y / tileSize));
  const colEnd    = Math.min(mapWidth,  Math.ceil((-pan.x + canvas.width)  / tileSize) + 1);
  const rowEnd    = Math.min(mapHeight, Math.ceil((-pan.y + canvas.height) / tileSize) + 1);

  ctx.imageSmoothingEnabled = false;

  for (let row = rowStart; row < rowEnd; row++) {
    for (let col = colStart; col < colEnd; col++) {
      const tileNum = tiles[row * mapWidth + col];
      if (!tileNum) continue;

      const x = Math.round(pan.x + col * tileSize);
      const y = Math.round(pan.y + row * tileSize);

      const entry = getTileImage(tileNum);
      if (entry.status === 'loaded' && entry.img) {
        ctx.drawImage(entry.img, x, y, tileSize, tileSize);
      } else {
        ctx.fillStyle = entry.status === 'error' ? '#3a0f0f' : '#131223';
        ctx.fillRect(x, y, tileSize, tileSize);
        if (tileSize >= 14) {
          ctx.fillStyle = '#4e4b64';
          ctx.font = `${Math.max(7, Math.floor(tileSize * 0.38))}px monospace`;
          ctx.textAlign    = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(tileNum, x + tileSize / 2, y + tileSize / 2);
        }
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Pan clamping
// ---------------------------------------------------------------------------

function clampPan() {
  if (!mapState.tiles || mapState.mapWidth < 1) return;
  const totalW = mapState.mapWidth  * mapState.tileSize;
  const totalH = Math.ceil(mapState.tiles.length / mapState.mapWidth) * mapState.tileSize;
  const cw = dom.mapCanvas.width;
  const ch = dom.mapCanvas.height;
  const maxX = Math.max(0, cw - mapState.tileSize);
  const maxY = Math.max(0, ch - mapState.tileSize);
  mapState.pan.x = Math.max(-(totalW - mapState.tileSize), Math.min(maxX, mapState.pan.x));
  mapState.pan.y = Math.max(-(totalH - mapState.tileSize), Math.min(maxY, mapState.pan.y));
}

// ---------------------------------------------------------------------------
// UI helpers
// ---------------------------------------------------------------------------

function updateMapSizeLabel() {
  const w = mapState.mapWidth;
  const h = mapState.tiles ? Math.ceil(mapState.tiles.length / w) : 0;
  dom.mapSizeLabel.textContent = `${w} \u00d7 ${h}`;
}

function updateZoomLabel() {
  dom.mapZoomLabel.textContent = `${mapState.tileSize}px`;
}

// ---------------------------------------------------------------------------
// Map control events
// ---------------------------------------------------------------------------

function bindMapEvents() {
  dom.rmapSelect.addEventListener('change', () => {
    mapState.rmapNumber = parseInt(dom.rmapSelect.value);
    mapState.tiles      = null;
    mapState.tileCache.clear();
    loadRmap();
  });

  dom.mapWidthInput.addEventListener('change', () => {
    const v = parseInt(dom.mapWidthInput.value);
    if (!Number.isFinite(v) || v < 1) return;
    mapState.mapWidth = v;
    mapState.pan      = { x: 0, y: 0 };
    mapState.dirty    = true;
    updateMapSizeLabel();
  });

  dom.mapZoomIn.addEventListener('click', () => {
    mapState.tileSizeIdx = Math.min(TILE_SIZES.length - 1, mapState.tileSizeIdx + 1);
    mapState.tileSize    = TILE_SIZES[mapState.tileSizeIdx];
    mapState.dirty       = true;
    updateZoomLabel();
  });

  dom.mapZoomOut.addEventListener('click', () => {
    mapState.tileSizeIdx = Math.max(0, mapState.tileSizeIdx - 1);
    mapState.tileSize    = TILE_SIZES[mapState.tileSizeIdx];
    mapState.dirty       = true;
    updateZoomLabel();
  });

  dom.mapResetPan.addEventListener('click', () => {
    mapState.pan   = { x: 0, y: 0 };
    mapState.dirty = true;
  });

  // Mouse drag
  dom.mapCanvas.addEventListener('mousedown', e => {
    mapState.drag = {
      active:    true,
      startX:    e.clientX,
      startY:    e.clientY,
      startPanX: mapState.pan.x,
      startPanY: mapState.pan.y,
    };
    dom.mapCanvas.classList.add('dragging');
    e.preventDefault();
  });
  document.addEventListener('mousemove', e => {
    if (!mapState.drag.active) return;
    mapState.pan.x = mapState.drag.startPanX + (e.clientX - mapState.drag.startX);
    mapState.pan.y = mapState.drag.startPanY + (e.clientY - mapState.drag.startY);
    clampPan();
    mapState.dirty = true;
  });
  document.addEventListener('mouseup', () => {
    if (!mapState.drag.active) return;
    mapState.drag.active = false;
    dom.mapCanvas.classList.remove('dragging');
  });

  // Touch drag
  dom.mapCanvas.addEventListener('touchstart', e => {
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
  dom.mapCanvas.addEventListener('touchmove', e => {
    if (!mapState.drag.active) return;
    const t = e.touches[0];
    mapState.pan.x = mapState.drag.startPanX + (t.clientX - mapState.drag.startX);
    mapState.pan.y = mapState.drag.startPanY + (t.clientY - mapState.drag.startY);
    clampPan();
    mapState.dirty = true;
    e.preventDefault();
  }, { passive: false });
  dom.mapCanvas.addEventListener('touchend', () => {
    mapState.drag.active = false;
  });
}
