/* ================================================================
   Dark Sun GFF Viewer — view-map.js
   Region map viewer: loads a server-rendered PNG of the full map
   (RMAP tiles + ETAB objects + WALL sprites) and provides
   CSS-transform pan/zoom interaction.

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
};

// ---------------------------------------------------------------------------
// Show / hide
// ---------------------------------------------------------------------------

function showMapView() {
  dom.content.classList.add('map-active');
  dom.mapView.classList.remove('hidden');
  applyTransform();
  loadMapEnvironment();

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
// CSS transform
// ---------------------------------------------------------------------------

function applyTransform() {
  const { pan, zoom } = mapState;
  dom.mapRenderImg.style.transform =
    `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`;
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

  // Mouse drag
  dom.mapCanvasWrap.addEventListener('mousedown', e => {
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
    if (!mapState.drag.active) return;
    mapState.pan.x = mapState.drag.startPanX + (e.clientX - mapState.drag.startX);
    mapState.pan.y = mapState.drag.startPanY + (e.clientY - mapState.drag.startY);
    clampPan();
    applyTransform();
  });
  document.addEventListener('mouseup', () => {
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
