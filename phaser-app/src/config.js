/* ================================================================
   GFF Viewer — Shared state, preferences, and event bus
   ================================================================ */

// ---------------------------------------------------------------------------
// Preferences (persisted to localStorage)
// ---------------------------------------------------------------------------
const PREFS_KEY = 'yuan-ti-prefs';
const PREFS_DEFAULTS = { spriteScale: 3 };

export const prefs = (() => {
  try {
    const saved = JSON.parse(localStorage.getItem(PREFS_KEY) || '{}');
    return { ...PREFS_DEFAULTS, ...saved };
  } catch { return { ...PREFS_DEFAULTS }; }
})();

export function savePrefs() {
  try { localStorage.setItem(PREFS_KEY, JSON.stringify(prefs)); } catch {}
}

// ---------------------------------------------------------------------------
// Application state
// ---------------------------------------------------------------------------
export const state = {
  mode: 'spells',
  game: 'dark_sun',
  games: [],
  gameUnsupported: false,
  palVersion: 0,

  gffByMode: {
    spells: 'RESOURCE.GFF', map: null, resources: null, images: null,
    text: null, palette: null, music: 'RESOURCE.GFF', sfx: 'RESOURCE.GFF',
    cinematic: null, data: null, scripts: null, sprite: null,
  },
  gffListByMode: {
    spells: ['RESOURCE.GFF'], map: [], resources: [], images: [],
    text: [], palette: [], music: [], sfx: [], cinematic: [],
    data: [], scripts: [], sprite: [],
  },

  resources: [],
  pals: [],
  allPals: [],
  selectedPal: null,
  imageView: 'card',
  imageTagFilter: '',
  imageCardZoom: 2,
  imageScale: 1,
  detail: { resource: null, frames: [], frameIndex: 0 },

  get currentGff() { return this.gffByMode[this.mode]; },
};

// ---------------------------------------------------------------------------
// Event bus
// ---------------------------------------------------------------------------
class EventBus {
  constructor() { this._handlers = {}; }
  on(name, fn) {
    (this._handlers[name] ||= []).push(fn);
  }
  off(name, fn) {
    const h = this._handlers[name];
    if (h) this._handlers[name] = h.filter(f => f !== fn);
  }
  emit(name, detail) {
    (this._handlers[name] || []).forEach(fn => fn(detail));
  }
}

export const events = new EventBus();

// ---------------------------------------------------------------------------
// URL state helpers
// ---------------------------------------------------------------------------
export function readUrlState() {
  const raw = location.hash.slice(1);
  if (!raw) return {};
  return Object.fromEntries(
    raw.split('&')
       .map(part => part.split('=').map(decodeURIComponent))
       .filter(([k]) => k)
  );
}

export function pushUrlState() {
  const params = { game: state.game, mode: state.mode };
  const gff = state.gffByMode[state.mode];
  if (gff) params.gff = gff;
  location.hash = Object.entries(params)
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
    .join('&');
}

// ---------------------------------------------------------------------------
// Reset per-game data
// ---------------------------------------------------------------------------
export function resetGameData() {
  state.gffListByMode = {
    spells: ['RESOURCE.GFF'], map: [], resources: [], images: [],
    text: [], palette: [], music: [], sfx: [], cinematic: [],
    data: [], scripts: [], sprite: [],
  };
  state.gffByMode = {
    spells: 'RESOURCE.GFF', map: null, resources: null, images: null,
    text: null, palette: null, music: 'RESOURCE.GFF', sfx: 'RESOURCE.GFF',
    cinematic: null, data: null, scripts: null, sprite: null,
  };
  state.allPals = [];
  state.resources = [];
  state.pals = [];
  state.selectedPal = null;
}
