/* ================================================================
   Dark Sun GFF Viewer — app.js
   Core: state, DOM refs, mode navigation, PAL selector, content
   routing, URL helpers, and shared utilities.
   View logic lives in view-images.js, view-map.js, view-spells.js.
   ================================================================ */

'use strict';

// ---------------------------------------------------------------------------
// Global preferences (persisted to localStorage)
// ---------------------------------------------------------------------------
const PREFS_KEY = 'yuan-ti-prefs-v2';
const PREFS_DEFAULTS = {
  spriteScale: 1,
};
const prefs = (() => {
  try {
    const saved = JSON.parse(localStorage.getItem(PREFS_KEY) || '{}');
    return { ...PREFS_DEFAULTS, ...saved };
  } catch { return { ...PREFS_DEFAULTS }; }
})();
function savePrefs() {
  try { localStorage.setItem(PREFS_KEY, JSON.stringify(prefs)); } catch {}
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
const state = {
  mode: 'spells',
  game: 'dark_sun',         // currently active game id
  games: [],                // [{id, label, supported}] from /api/games
  gameUnsupported: false,   // true when selected game cannot be browsed
  // Per-mode selected GFF (spells is always locked to RESOURCE.GFF)
  gffByMode:    { spells: 'RESOURCE.GFF', map: null, resources: null, images: null, text: null, palette: null, music: 'RESOURCE.GFF', sfx: 'RESOURCE.GFF', cinematic: null, data: null, scripts: null, sprite: null },
  // Per-mode fetched GFF lists
  gffListByMode:{ spells: ['RESOURCE.GFF'], map: [], resources: [], images: [], text: [], palette: [], music: [], sfx: [], cinematic: [], data: [], scripts: [], sprite: [] },

  resources:   [],
  pals:        [],    // per-GFF pals (for non-images modes)
  allPals:     [],    // all pals across all GFFs (for images mode)
  selectedPal: null,
  imageView:      'card',
  imageTagFilter: '',
  imageCardZoom:  2,   // index into CARD_ZOOM_PRESETS
  imageScale:  1,
  detail: {
    resource:   null,
    frames:     [],
    frameIndex: 0,
    preloaded:  [],   // HTMLImageElement per frame, keyed by frameIndex
  },
  get currentGff() { return this.gffByMode[this.mode]; },
};

// ---------------------------------------------------------------------------
// DOM refs
// ---------------------------------------------------------------------------
const $ = id => document.getElementById(id);

const dom = {
  // Header
  gameSelect:     $('game-select'),

  // Mode nav
  modeBtns:       document.querySelectorAll('.mode-btn'),
  modeGffGroup:   $('mode-gff-group'),
  modeGffSelect:  $('mode-gff-select'),
  imgViewGroup:   $('img-view-group'),
  imgCardBtn:     $('img-card-btn'),
  imgListBtn:     $('img-list-btn'),
  imgSpritesBtn:  $('img-sprites-btn'),
  imgZoomOut:     $('img-zoom-out'),
  imgZoomLabel:   $('img-zoom-label'),
  imgZoomIn:      $('img-zoom-in'),
  imgFilterBar:   $('img-filter-bar'),
  imgSpriteView:  $('img-sprite-view'),
  palGroup:       $('pal-group'),
  palSelect:      $('pal-select'),

  // Content area
  content:        $('content'),
  emptyState:     $('empty-state'),
  emptyMsg:       $('empty-msg'),
  imageGrid:      $('image-grid'),
  imageListWrap:  $('image-list-wrap'),
  imageListTbody: $('image-list-tbody'),
  spellGrid:      $('spell-grid'),
  textView:       $('text-view'),
  textSearch:     $('text-search'),
  textList:       $('text-list'),
  textSearchCount:$('text-search-count'),
  textReaderEmpty:$('text-reader-empty'),
  textReaderContent:$('text-reader-content'),
  textReaderBadge:$('text-reader-badge'),
  textReaderNum:  $('text-reader-num'),
  textReaderBody: $('text-reader-body'),
  tableWrap:      $('resource-table-wrap'),
  resFilterBar:   $('res-filter-bar'),
  tableTbody:     $('resource-tbody'),

  // Palette view
  paletteView:      $('palette-view'),

  // Map view
  mapView:          $('rmap-view'),
  mapRenderImg:     $('map-render-img'),
  mapCanvasWrap:    $('map-canvas-wrap'),
  mapWidthInput:    $('map-width-input'),
  mapSizeLabel:     $('map-size-label'),
  mapZoomLabel:     $('map-zoom-label'),
  mapZoomIn:        $('map-zoom-in'),
  mapZoomOut:       $('map-zoom-out'),
  mapResetPan:      $('map-reset-pan'),
  rmapSelect:       $('rmap-select'),
  mapSpinner:       $('map-spinner'),

  // Resource table filters
  resFilterTag:     $('res-filter-tag'),
  resFilterCount:   $('res-filter-count'),

  // Detail pane
  detailPane:       $('detail-pane'),
  detailResizer:    $('detail-resizer'),
  detailToggle:     $('detail-toggle'),
  detailInner:      $('detail-inner'),
  detailTag:        $('detail-tag'),
  detailNum:        $('detail-num'),
  detailSize:       $('detail-size'),
  detailDesc:       $('detail-desc'),
  detailClose:      $('detail-close'),
  detailImageView:  $('detail-image-view'),
  detailPrev:       $('detail-prev'),
  detailNext:       $('detail-next'),
  detailFrameInfo:  $('detail-frame-info'),
  detailImgWrap:    $('detail-img-wrap'),
  detailImg:        $('detail-img'),
  detailImgSpinner: $('detail-img-spinner'),
  detailImgDims:    $('detail-img-dims'),
  detailScale:      $('detail-scale'),

  // Animation controls
  animControls:     $('anim-controls'),
  animPlay:         $('anim-play'),
  animStop:         $('anim-stop'),
  animDuration:     $('anim-duration'),
  animStatus:       $('anim-status'),
  animRange:        $('anim-range'),
  animRangeStart:   $('anim-range-start'),
  animRangeEnd:     $('anim-range-end'),

  detailHexView:    $('detail-hex-view'),
  hexInfo:          $('hex-info'),
  hexTruncated:     $('hex-truncated'),
  hexPre:           $('hex-pre'),
};

// Card size presets: [px, label]
const CARD_ZOOM_PRESETS = [
  [80,  'XS'],
  [120, 'S'],
  [148, 'M'],
  [200, 'L'],
  [280, 'XL'],
  [400, '2X'],
];

function applyCardZoom(idx) {
  idx = Math.max(0, Math.min(idx, CARD_ZOOM_PRESETS.length - 1));
  state.imageCardZoom = idx;
  const [px, label] = CARD_ZOOM_PRESETS[idx];
  const cssVal = px + 'px';
  dom.imageGrid.style.setProperty('--card-size', cssVal);
  dom.imgSpriteView.style.setProperty('--card-size', cssVal);
  dom.imgZoomLabel.textContent = label;
  dom.imgZoomOut.disabled = (idx === 0);
  dom.imgZoomIn.disabled  = (idx === CARD_ZOOM_PRESETS.length - 1);
}

// ---------------------------------------------------------------------------
// Cache-bust suffix — incremented on every palette change
let palVersion = 0;

// ---------------------------------------------------------------------------
// URL bookmark helpers
// ---------------------------------------------------------------------------

/** Parse location.hash into a plain object of key→value strings. */
function readUrlState() {
  const raw = location.hash.slice(1); // strip leading '#'
  if (!raw) return {};
  return Object.fromEntries(
    raw.split('&')
       .map(part => part.split('=').map(decodeURIComponent))
       .filter(([k]) => k)
  );
}

/**
 * Push current navigation state into location.hash without triggering a
 * full page reload. Only the keys that are meaningful are written.
 */
function pushUrlState() {
  const params = { game: state.game, mode: state.mode };
  const gff = state.gffByMode[state.mode];
  if (gff) params.gff = gff;
  if (state.mode === 'images') params.view = state.imageView;
  if (state.mode === 'sprite') {
    if (typeof selectedBmpNum !== 'undefined' && selectedBmpNum >= 0)
      params.bmp = selectedBmpNum;
    if (typeof spriteFilter !== 'undefined' && spriteFilter.type)
      params.sprType = spriteFilter.type;
    if (typeof spriteFilter !== 'undefined' && spriteFilter.animated && spriteFilter.animated !== 'all')
      params.sprAnim = spriteFilter.animated;
    if (typeof _selectedGroupRef !== 'undefined' && _selectedGroupRef)
      params.sprGroup = _selectedGroupRef;
  }
  location.hash = Object.entries(params)
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
    .join('&');
}

// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------
document.addEventListener('DOMContentLoaded', () => {
  bindMapEvents();
  bindDetailPaneEvents();
  bindResourceTableEvents();
  init();
});

async function init() {
  bindEvents();

  // Restore bookmarked state before fetching data so the first load goes
  // straight to the right mode / file.
  const saved = readUrlState();
  if (saved.game) state.game = saved.game;
  // compat: old bookmarks may use 'npc'
  if (saved.mode) state.mode = saved.mode === 'npc' ? 'sprite' : saved.mode;
  if (saved.view) state.imageView = saved.view;
  if (saved.gff && saved.mode)
    state.gffByMode[saved.mode] = saved.gff;
  if (saved.bmp && state.mode === 'sprite')
    if (typeof _pendingSelectBmp !== 'undefined') _pendingSelectBmp = Number(saved.bmp);
  if (state.mode === 'sprite') {
    if (saved.sprType && typeof spriteFilter !== 'undefined') spriteFilter.type = saved.sprType;
    if (saved.sprAnim && typeof spriteFilter !== 'undefined') spriteFilter.animated = saved.sprAnim;
    if (saved.sprGroup && typeof _pendingSelectGroup !== 'undefined') _pendingSelectGroup = saved.sprGroup;
  }

  await populateGameSelect();
  // Sync the game <select> to the (possibly restored) state
  dom.gameSelect.value = state.game;
  const selectedGame = state.games.find(g => g.id === state.game);
  state.gameUnsupported = selectedGame ? !selectedGame.supported : false;

  await refreshGameData();

  // Keep the browser back/forward buttons working
  window.addEventListener('hashchange', async () => {
    const s = readUrlState();
    // Only react when something genuinely navigated (not our own push)
    const newGame = s.game || 'dark_sun';
    const newMode = s.mode === 'npc' ? 'sprite' : (s.mode || 'spells');
    const newGff  = s.gff  || null;
    const newView = s.view || 'card';
    const newBmp  = s.bmp  ? Number(s.bmp) : null;
    const newSprType  = s.sprType  || null;
    const newSprAnim  = s.sprAnim  || null;
    const newSprGroup = s.sprGroup || null;

    let needsRefresh = false;
    if (newGame !== state.game) {
      state.game = newGame;
      dom.gameSelect.value = state.game;
      const g = state.games.find(g => g.id === state.game);
      state.gameUnsupported = g ? !g.supported : false;
      needsRefresh = true;
    }

    if (needsRefresh) {
      if (newGff)  state.gffByMode[newMode] = newGff;
      if (newView) state.imageView = newView;
      state.mode = newMode;
      await refreshGameData();
      return;
    }

    if (newView !== state.imageView) state.imageView = newView;
    if (newGff && newGff !== state.gffByMode[newMode])
      state.gffByMode[newMode] = newGff;
    if (newBmp !== null && newMode === 'sprite' && typeof _pendingSelectBmp !== 'undefined')
      _pendingSelectBmp = newBmp;
    if (newMode === 'sprite') {
      if (newSprType && typeof spriteFilter !== 'undefined' && newSprType !== spriteFilter.type) {
        spriteFilter.type = newSprType;
        if (typeof _applyFilter === 'function') _applyFilter();
      }
      if (newSprAnim !== null && typeof spriteFilter !== 'undefined' && (newSprAnim || 'all') !== spriteFilter.animated) {
        spriteFilter.animated = newSprAnim || 'all';
        if (typeof _applyFilter === 'function') _applyFilter();
      }
      if (newSprGroup && typeof _pendingSelectGroup !== 'undefined') _pendingSelectGroup = newSprGroup;
    }

    if (newMode !== state.mode) {
      await activateMode(newMode);
    } else if (newGff && newGff !== state.currentGff) {
      await loadGffForMode(newGff, state.mode);
    } else if (newBmp !== null && newMode === 'sprite') {
      // Only jump to sprite if it's a different BMP than what's already selected
      // (avoids re-rendering the detail pane when _selectEntry triggered this hashchange)
      if (typeof selectedBmpNum === 'undefined' || selectedBmpNum !== newBmp) {
        if (typeof _selectEntry === 'function') _selectEntry(newBmp);
      }
    }
  });
}

async function populateGameSelect() {
  try {
    const r = await fetch('/api/games');
    state.games = await r.json();
    dom.gameSelect.innerHTML = '';
    state.games.forEach(g => {
      const opt = document.createElement('option');
      opt.value = g.id;
      opt.textContent = g.supported ? g.label : `${g.label}  — not supported`;      if (!g.supported) opt.dataset.unsupported = '1';      dom.gameSelect.appendChild(opt);
    });
    dom.gameSelect.value = state.game;
  } catch {
    // /api/games failed — keep fallback HTML option
  }
  const selected = state.games.find(g => g.id === state.game);
  state.gameUnsupported = selected ? !selected.supported : false;
}

async function refreshGameData() {
  // Reset all cached GFF lists so they reload for the active game
  state.gffListByMode = { spells: ['RESOURCE.GFF'], map: [], resources: [], images: [], text: [], palette: [], music: [], sfx: [], cinematic: [], data: [], scripts: [], sprite: [] };
  state.gffByMode     = { spells: 'RESOURCE.GFF', map: null, resources: null, images: null, text: null, palette: null, music: 'RESOURCE.GFF', sfx: 'RESOURCE.GFF', cinematic: null, data: null, scripts: null, sprite: null };
  state.allPals       = [];
  state.resources     = [];
  state.pals          = [];
  state.selectedPal   = null;

  // Bust view-level caches that key by GFF name (same filename across different games)
  if (typeof spellData   !== 'undefined') spellData   = [];
  if (typeof textDataGff !== 'undefined') textDataGff = null;
  if (typeof mapState    !== 'undefined') { mapState.tiles = null; mapState.tileCache.clear(); }
  if (typeof musicViewBuilt !== 'undefined') { musicViewBuilt = false; musicDataGff = null;
    if (typeof stopPlayback === 'function') stopPlayback(); }
  if (typeof sfxViewBuilt !== 'undefined') { sfxViewBuilt = false; sfxDataGff = null; }
  if (typeof cinematicViewBuilt !== 'undefined') cinematicViewBuilt = false;
  if (typeof teardownDataView    === 'function')  teardownDataView();
  if (typeof teardownScriptsView === 'function')  teardownScriptsView();
  if (typeof teardownSpritesView === 'function')  teardownSpritesView();

  closeDetailPane();
  hideAllViews();

  if (state.gameUnsupported) {
    const gameInfo = state.games.find(g => g.id === state.game);
    dom.emptyMsg.textContent = `${gameInfo?.label || state.game} — not currently supported.`;
    dom.emptyState.classList.remove('hidden');
    return;
  }

  await Promise.all([
    ensureGffList('map'),
    ensureGffList('resources'),
    ensureGffList('images'),
    ensureGffList('text'),
    ensureGffList('music'),
    ensureGffList('sfx'),
    fetchAllPals(),
  ]);
  await activateMode(state.mode);
}

// ---------------------------------------------------------------------------
// Event binding
// ---------------------------------------------------------------------------
function bindEvents() {
  dom.modeBtns.forEach(btn => {
    btn.addEventListener('click', () => activateMode(btn.dataset.mode));
  });

  dom.modeGffSelect.addEventListener('change', () => {
    const gff = dom.modeGffSelect.value;
    if (gff) { loadGffForMode(gff, state.mode); pushUrlState(); }
  });

  dom.imgCardBtn.addEventListener('click', () => { setImageView('card');    pushUrlState(); });
  dom.imgListBtn.addEventListener('click', () => { setImageView('list');    pushUrlState(); });
  dom.imgSpritesBtn.addEventListener('click', () => { setImageView('sprites'); pushUrlState(); });
  dom.imgZoomOut.addEventListener('click', () => applyCardZoom(state.imageCardZoom - 1));
  dom.imgZoomIn.addEventListener('click',  () => applyCardZoom(state.imageCardZoom + 1));

  dom.palSelect.addEventListener('change', onPalChange);

  dom.detailClose.addEventListener('click', closeDetailPane);
  dom.detailPrev.addEventListener('click', () => navigateFrame(-1));
  dom.detailNext.addEventListener('click', () => navigateFrame(1));
  dom.detailScale.addEventListener('change', () => {
    state.imageScale = parseInt(dom.detailScale.value, 10) || 1;
    if (state.detail.resource?.is_image) renderFrameInPane(state.detail.frameIndex);
  });

  document.addEventListener('keydown', e => {
    if (!state.detail.resource) return;
    if (e.key === 'Escape')     closeDetailPane();
    if (e.key === 'ArrowLeft')  navigateFrame(-1);
    if (e.key === 'ArrowRight') navigateFrame(1);
  });

  dom.gameSelect.addEventListener('change', async () => {
    state.game = dom.gameSelect.value;
    const selected = state.games.find(g => g.id === state.game);
    state.gameUnsupported = selected ? !selected.supported : false;
    await refreshGameData();
    pushUrlState();
  });
}

// ---------------------------------------------------------------------------
// Mode activation
// ---------------------------------------------------------------------------
async function activateMode(mode) {
  state.mode = mode;
  pushUrlState();

  // Highlight active mode button
  dom.modeBtns.forEach(b => b.classList.toggle('active', b.dataset.mode === mode));

  closeDetailPane();

  // Show/hide toolbar groups
  dom.modeGffGroup.classList.toggle('hidden', mode === 'spells' || mode === 'palette' || mode === 'cinematic' || mode === 'data' || mode === 'scripts' || mode === 'sprite' || mode === 'prefs');
  dom.imgViewGroup.classList.toggle('hidden', mode !== 'images');
  // Palette selector is not useful in text or palette or music/sfx/cinematic/data/scripts/sprite modes
  dom.palGroup.classList.toggle('hidden', mode === 'text' || mode === 'palette' || mode === 'music' || mode === 'sfx' || mode === 'cinematic' || mode === 'data' || mode === 'scripts' || mode === 'sprite' || mode === 'prefs');

  // Tear down music player when leaving music or sfx mode
  if (mode !== 'music' && mode !== 'sfx' && typeof teardownMusicView === 'function') teardownMusicView();
  if (mode !== 'sfx' && typeof teardownSfxView === 'function') teardownSfxView();

  // Palette mode renders directly from state.allPals — no GFF selection needed
  if (mode === 'palette') {
    renderContent();
    return;
  }

  // Cinematic mode — no GFF files, just standalone FLI files
  if (mode === 'cinematic') {
    renderContent();
    return;
  }

  // Data mode — items/monsters/spells from dedicated API endpoints
  if (mode === 'data') {
    renderContent();
    return;
  }

  // Scripts mode — GPL/MAS browser with its own API
  if (mode === 'scripts') {
    renderContent();
    return;
  }

  // Sprite viewer
  if (mode === 'sprite') {
    renderContent();
    return;
  }
  // Preferences page
  if (mode === 'prefs') {
    renderContent();
    return;
  }
  // Music mode uses its own GFF selector (defaults to RESOURCE.GFF)
  if (mode === 'music') {
    await ensureGffList('music');
    populateModeGffSelect('music');
    renderContent();
    return;
  }

  // SFX mode — same pattern as music
  if (mode === 'sfx') {
    await ensureGffList('sfx');
    populateModeGffSelect('sfx');
    renderContent();
    return;
  }

  if (mode !== 'spells') {
    await ensureGffList(mode);
    populateModeGffSelect(mode);
  }

  const gff = state.gffByMode[mode];
  if (gff) {
    await loadGffForMode(gff, mode);
  } else {
    state.resources = [];
    state.pals      = [];
    const msgs = {
      map:       'Select a region file to view its map.',
      resources: 'Select a GFF file to browse resources.',
      images:    'Select a GFF file to browse images.',
      text:      'Select a GFF file to browse text resources.',
    };
    dom.emptyMsg.textContent = msgs[mode] || 'Select a mode to begin.';
    buildPalSelectorAll(state.allPals);
    hideAllViews();
    dom.emptyState.classList.remove('hidden');
  }
}

// ---------------------------------------------------------------------------
// GFF list management
// ---------------------------------------------------------------------------
async function ensureGffList(mode) {
  if (state.gffListByMode[mode].length > 0) return;
  const capMap = { map: 'maps', images: 'images', text: 'texts', music: 'music', sfx: 'sfx' };
  const cap = capMap[mode] ?? 'resources';
  try {
    const r = await fetch(`/api/gffs?cap=${cap}&game=${state.game}`);
    state.gffListByMode[mode] = await r.json();
  } catch {
    state.gffListByMode[mode] = [];
  }
}

function populateModeGffSelect(mode) {
  const list    = state.gffListByMode[mode];
  const current = state.gffByMode[mode];
  dom.modeGffSelect.innerHTML = `<option value="">&#8212; Select file &#8212;</option>`;
  list.forEach(name => {
    const opt = document.createElement('option');
    opt.value = name;
    opt.textContent = name;
    dom.modeGffSelect.appendChild(opt);
  });
  dom.modeGffSelect.value = current || '';
}

// ---------------------------------------------------------------------------
// GFF loading
// ---------------------------------------------------------------------------
async function loadGffForMode(gff, mode) {
  if (!gff) return;

  // Music mode has its own loader — just update state and re-render
  if (mode === 'music') {
    state.gffByMode.music = gff;
    if (typeof musicDataGff !== 'undefined') musicDataGff = null; // force track-list reload
    renderContent();
    return;
  }

  // SFX mode — same pattern
  if (mode === 'sfx') {
    state.gffByMode.sfx = gff;
    if (typeof sfxDataGff !== 'undefined') sfxDataGff = null; // force track-list reload
    renderContent();
    return;
  }

  state.gffByMode[mode]  = gff;
  state.resources        = [];
  state.pals             = [];
  state.selectedPal      = null;
  state.detail.resource  = null;
  state.imageTagFilter   = '';

  dom.resFilterTag.value         = '';
  dom.resFilterCount.textContent = '';

  // Reset spell view state
  if (typeof spellData    !== 'undefined') spellData    = [];
  if (typeof spellFilter  !== 'undefined') spellFilter  = '';
  if (typeof activeSpellN !== 'undefined') activeSpellN = null;
  if (dom.spellGrid) dom.spellGrid.innerHTML = '';

  showLoadingState();

  const [resources, pals] = await Promise.all([
    fetch(`/api/gff/${encodeURIComponent(gff)}/resources?game=${state.game}`).then(r => r.json()).catch(() => []),
    fetch(`/api/gff/${encodeURIComponent(gff)}/pals?game=${state.game}`).then(r => r.json()).catch(() => []),
  ]);

  state.resources = resources;
  state.pals      = pals;

  // Seed the client frame-count cache from resources (avoids per-image /info requests later)
  resources.forEach(res => {
    if (res.is_image && res.frame_count != null) {
      imageFrameCache.set(`${gff}|${res.tag_url}|${res.number}`, res.frame_count);
    }
  });

  // Supply map state
  const rmapResources   = resources.filter(r => r.is_rmap);
  mapState.rmapList     = rmapResources;
  mapState.rmapNumber   = rmapResources.length > 0 ? rmapResources[0].number : null;
  mapState.tiles        = null;

  // Always show all PALs from all GFFs
  buildPalSelectorAll(state.allPals);

  buildFilterDataLists();
  renderContent();
}

// ---------------------------------------------------------------------------
// All-pals fetch (for images mode)
// ---------------------------------------------------------------------------
async function fetchAllPals() {
  try {
    const r = await fetch(`/api/pals/all?game=${state.game}`);
    state.allPals = await r.json();
  } catch {
    state.allPals = [];
  }
}

// ---------------------------------------------------------------------------
// PAL selector
// ---------------------------------------------------------------------------
function buildPalSelector(pals) {
  dom.palSelect.innerHTML = '<option value="auto">Auto\u2011detect</option>';
  pals.forEach((p, i) => {
    const opt = document.createElement('option');
    opt.value = i;
    opt.textContent = `${p.gff}  \u2014  PAL #${p.number}`;
    dom.palSelect.appendChild(opt);
  });
  dom.palSelect.value = 'auto';
  state.selectedPal = null;
}

function buildPalSelectorAll(allPals) {
  dom.palSelect.innerHTML = '<option value="auto">Auto\u2011detect</option>';
  allPals.forEach((p, i) => {
    const opt = document.createElement('option');
    opt.value = 'a' + i;
    const tagLabel = (p.tag || 'PAL').trim() === 'CPAL' ? 'CPAL' : 'PAL';
    opt.textContent = `${p.gff}  \u2014  ${tagLabel} #${p.number}`;
    dom.palSelect.appendChild(opt);
  });
  dom.palSelect.value = 'auto';
  state.selectedPal = null;
}

function onPalChange() {
  const val = dom.palSelect.value;
  if (val === 'auto') {
    state.selectedPal = null;
  } else if (val.startsWith('a')) {
    state.selectedPal = state.allPals[parseInt(val.slice(1))];
  } else {
    state.selectedPal = state.pals[parseInt(val)];
  }
  palVersion++;
  // Clear tile image cache so tiles reload with new palette
  mapState.tileCache.clear();
  mapState.dirty = true;
  renderContent();
  if (state.detail.resource?.is_image) {
    renderFrameInPane(state.detail.frameIndex);
  }
}

function buildFilterDataLists() {
  const tags  = [...new Set(state.resources.map(r => r.tag.trim()))].sort();
  const saved = dom.resFilterTag.value;
  dom.resFilterTag.innerHTML = `<option value="">All types</option>` +
    tags.map(t => {
      const desc = (state.resources.find(r => r.tag.trim() === t)?.description || '').trim();
      const label = desc ? `${t} \u2014 ${desc}` : t;
      return `<option value="${htmlEsc(t)}">${htmlEsc(label)}</option>`;
    }).join('');
  dom.resFilterTag.value = tags.includes(saved) ? saved : '';
}

// ---------------------------------------------------------------------------
// Image view toggle
// ---------------------------------------------------------------------------
function setImageView(view) {
  state.imageView = view;
  dom.imgCardBtn.classList.toggle('active',    view === 'card');
  dom.imgListBtn.classList.toggle('active',    view === 'list');
  dom.imgSpritesBtn.classList.toggle('active', view === 'sprites');
  // Apply current zoom whenever we enter a card/sprite view
  if (view !== 'list') applyCardZoom(state.imageCardZoom);
  if (state.mode === 'images') renderContent();
}

// ---------------------------------------------------------------------------
// Loading / view helpers
// ---------------------------------------------------------------------------
function showLoadingState() {
  hideAllViews();
  let loading = $('content-loading');
  if (!loading) {
    loading = document.createElement('div');
    loading.id = 'content-loading';
    loading.innerHTML = '<div class="spinner"></div><span>Loading\u2026</span>';
    dom.content.appendChild(loading);
  }
  loading.classList.remove('hidden');
}

function hideLoadingState() {
  const loading = $('content-loading');
  if (loading) loading.classList.add('hidden');
}

function hideAllViews() {
  dom.emptyState.classList.add('hidden');
  dom.imageGrid.classList.add('hidden');
  dom.imageListWrap.classList.add('hidden');
  dom.imgFilterBar.classList.add('hidden');
  dom.imgSpriteView.classList.add('hidden');
  dom.spellGrid.classList.add('hidden');
  dom.textView.classList.add('hidden');
  dom.tableWrap.classList.add('hidden');
  dom.resFilterBar.classList.add('hidden');
  dom.paletteView.classList.add('hidden');
  const mv = document.getElementById('music-view');
  if (mv) mv.classList.add('hidden');
  const sv = document.getElementById('sfx-view');
  if (sv) sv.classList.add('hidden');
  const cv = document.getElementById('cinematic-view');
  if (cv) cv.classList.add('hidden');
  const dv = document.getElementById('data-view');
  if (dv) dv.classList.add('hidden');
  const scv = document.getElementById('scripts-view');
  if (scv) scv.classList.add('hidden');
  const nv = document.getElementById('sprite-view');
  if (nv) nv.classList.add('hidden');
  const pv = document.getElementById('prefs-view');
  if (pv) pv.classList.add('hidden');
  hideMapView();
}

// ---------------------------------------------------------------------------
// Content rendering
// ---------------------------------------------------------------------------
function renderContent() {
  hideLoadingState();
  hideAllViews();

  // Palette mode renders directly from state.allPals
  if (state.mode === 'palette') {
    renderPaletteView();
    return;
  }

  // Music mode has its own loader — bypass the resources guard
  if (state.mode === 'music') {
    renderMusicView();
    return;
  }

  // SFX mode — bypass the resources guard
  if (state.mode === 'sfx') {
    renderSfxView();
    return;
  }

  // Cinematic mode — no GFF resources
  if (state.mode === 'cinematic') {
    renderCinematicView();
    return;
  }

  // Data browser — items / monsters / spells
  if (state.mode === 'data') {
    renderDataView();
    return;
  }

  // Scripts browser — GPL / MAS disassembly
  if (state.mode === 'scripts') {
    renderScriptsView();
    return;
  }

  // Sprite viewer
  if (state.mode === 'sprite') {
    if (typeof renderSpritesView === 'function') renderSpritesView();
    return;
  }

  // Preferences page
  if (state.mode === 'prefs') {
    if (typeof renderPrefsView === 'function') renderPrefsView();
    return;
  }

  if (!state.currentGff || state.resources.length === 0) {
    dom.emptyState.classList.remove('hidden');
    dom.emptyMsg.textContent = state.currentGff
      ? 'No resources found in this file.'
      : (dom.emptyMsg.textContent || 'Select a mode to begin.');
    return;
  }

  switch (state.mode) {
    case 'spells':    renderSpellGrid(); break;
    case 'map':       showMapView(); break;
    case 'resources': renderResourceTable(); break;
    case 'text':      renderTextView(); break;
    case 'music':     renderMusicView(); break;
    case 'images':
      buildImageFilterBar();
      applyCardZoom(state.imageCardZoom);
      if (state.imageView === 'list')         renderImageList();
      else if (state.imageView === 'sprites') renderSpriteView();
      else                                    renderImageGrid();
      break;
  }
}

// ---------------------------------------------------------------------------
// PAL / URL helpers
// ---------------------------------------------------------------------------
function palParams() {
  if (!state.selectedPal) return '';
  const p   = state.selectedPal;
  const tag = (p.tag || 'PAL').trim() || 'PAL';
  return `&pal_gff=${encodeURIComponent(p.gff)}&pal_num=${p.number}&pal_tag=${encodeURIComponent(tag)}`;
}

function imageUrl(gff, tagUrl, number, frame = -1, scale = 1) {
  const s = (scale > 1) ? `&scale=${scale}` : '';
  return `/api/gff/${encodeURIComponent(gff)}/image/${tagUrl}/${number}`
       + `?frame=${frame}&game=${state.game}${palParams()}${s}&v=${palVersion}`;
}

function rawUrl(gff, tagUrl, number) {
  return `/api/gff/${encodeURIComponent(gff)}/raw/${tagUrl}/${number}?game=${state.game}`;
}

// ---------------------------------------------------------------------------
// Utilities
// ---------------------------------------------------------------------------
function formatBytes(n) {
  if (n < 1024)    return `${n} B`;
  if (n < 1048576) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1048576).toFixed(2)} MB`;
}

function htmlEsc(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
