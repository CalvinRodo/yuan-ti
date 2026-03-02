/* ================================================================
   GFF Viewer — Phaser Application Entry Point
   Boots the Phaser game, sets up navigation, coordinates scenes.
   ================================================================ */

import Phaser from 'phaser';
import './styles.css';

import { state, events, readUrlState, pushUrlState, resetGameData } from './config.js';
import * as api from './api.js';
import { $, hideDomPanels } from './utils.js';

// Import all scenes
import BootScene from './scenes/BootScene.js';
import SpellScene from './scenes/SpellScene.js';
import MapScene from './scenes/MapScene.js';
import ImageScene from './scenes/ImageScene.js';
import PaletteScene from './scenes/PaletteScene.js';
import TextScene from './scenes/TextScene.js';
import MusicScene from './scenes/MusicScene.js';
import SfxScene from './scenes/SfxScene.js';
import CinematicScene from './scenes/CinematicScene.js';
import DataScene from './scenes/DataScene.js';
import ScriptsScene from './scenes/ScriptsScene.js';
import SpritesScene from './scenes/SpritesScene.js';
import ResourceScene from './scenes/ResourceScene.js';
import PrefsScene from './scenes/PrefsScene.js';

// ---------------------------------------------------------------------------
// Mode → Scene key mapping
// ---------------------------------------------------------------------------
const MODE_SCENES = {
  spells:    'SpellScene',
  map:       'MapScene',
  images:    'ImageScene',
  palette:   'PaletteScene',
  text:      'TextScene',
  music:     'MusicScene',
  sfx:       'SfxScene',
  cinematic: 'CinematicScene',
  data:      'DataScene',
  scripts:   'ScriptsScene',
  sprite:    'SpritesScene',
  resources: 'ResourceScene',
  prefs:     'PrefsScene',
};

// Modes that render in DOM panels (not on the Phaser canvas)
const DOM_MODES = new Set([
  'text', 'music', 'sfx', 'cinematic', 'data', 'scripts', 'resources', 'prefs', 'sprite',
]);

// Modes that don't need a GFF file selector in the toolbar
const NO_GFF_MODES = new Set([
  'spells', 'palette', 'cinematic', 'data', 'scripts', 'sprite', 'prefs', 'sfx',
]);

// Modes that don't need the palette selector
const NO_PAL_MODES = new Set([
  'text', 'palette', 'music', 'sfx', 'cinematic', 'data', 'scripts', 'sprite', 'prefs',
]);

let game;
let currentSceneKey = null;

// ---------------------------------------------------------------------------
// Phaser game configuration
// ---------------------------------------------------------------------------
const config = {
  type: Phaser.AUTO,
  parent: 'game-container',
  backgroundColor: '#0d0c14',
  pixelArt: true,
  dom: { createContainer: true },
  scale: {
    mode: Phaser.Scale.RESIZE,
    autoCenter: Phaser.Scale.NO_CENTER,
  },
  scene: [
    BootScene, SpellScene, MapScene, ImageScene, PaletteScene,
    TextScene, MusicScene, SfxScene, CinematicScene, DataScene,
    ScriptsScene, SpritesScene, ResourceScene, PrefsScene,
  ],
  audio: { disableWebAudio: false },
};

// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------
document.addEventListener('DOMContentLoaded', () => {
  game = new Phaser.Game(config);

  // Phaser auto-starts the first scene. We only use BootScene as a placeholder;
  // the real initialisation happens below once the DOM is ready.
  init();
});

async function init() {
  // Restore bookmarked state
  const saved = readUrlState();
  if (saved.game) state.game = saved.game;
  if (saved.mode) state.mode = saved.mode === 'npc' ? 'sprite' : saved.mode;
  if (saved.gff && saved.mode) state.gffByMode[saved.mode] = saved.gff;

  // Apply persisted UI collapse state
  initCollapseToggles();

  // Fetch game list & populate selector
  await api.fetchGames();
  const gameSelect = $('game-select');
  gameSelect.innerHTML = '';
  state.games.forEach(g => {
    const opt = document.createElement('option');
    opt.value = g.id;
    opt.textContent = g.supported ? g.label : `${g.label} — not supported`;
    if (!g.supported) opt.dataset.unsupported = '1';
    gameSelect.appendChild(opt);
  });
  gameSelect.value = state.game;
  const selectedGame = state.games.find(g => g.id === state.game);
  state.gameUnsupported = selectedGame ? !selectedGame.supported : false;

  // Bind events
  bindEvents();

  // Load initial game data and activate first mode
  await refreshGameData();

  // Browser back/forward
  window.addEventListener('hashchange', async () => {
    const s = readUrlState();
    const newMode = s.mode === 'npc' ? 'sprite' : (s.mode || 'spells');
    const newGame = s.game || 'dark_sun';
    const newGff = s.gff || null;

    if (newGame !== state.game) {
      state.game = newGame;
      gameSelect.value = state.game;
      const g = state.games.find(g => g.id === state.game);
      state.gameUnsupported = g ? !g.supported : false;
      if (newGff) state.gffByMode[newMode] = newGff;
      state.mode = newMode;
      await refreshGameData();
      return;
    }

    if (newGff) state.gffByMode[newMode] = newGff;
    if (newMode !== state.mode) {
      await activateMode(newMode);
    }
  });
}

// ---------------------------------------------------------------------------
// Event binding
// ---------------------------------------------------------------------------
function bindEvents() {
  // Mode nav buttons
  document.querySelectorAll('.mode-btn').forEach(btn => {
    btn.addEventListener('click', () => activateMode(btn.dataset.mode));
  });

  // Game selector
  $('game-select').addEventListener('change', async () => {
    state.game = $('game-select').value;
    const g = state.games.find(g => g.id === state.game);
    state.gameUnsupported = g ? !g.supported : false;
    await refreshGameData();
    pushUrlState();
  });

  // GFF file selector
  $('mode-gff-select').addEventListener('change', async () => {
    const gff = $('mode-gff-select').value;
    if (gff) {
      state.gffByMode[state.mode] = gff;
      // Reload current mode
      await activateMode(state.mode);
      pushUrlState();
    }
  });

  // Palette selector
  $('pal-select').addEventListener('change', () => {
    const val = $('pal-select').value;
    if (val === 'auto') {
      state.selectedPal = null;
    } else if (val.startsWith('a')) {
      state.selectedPal = state.allPals[parseInt(val.slice(1))];
    }
    state.palVersion++;
    events.emit('palette-changed');
    // Re-activate current mode to refresh
    activateMode(state.mode);
  });
}

// ---------------------------------------------------------------------------
// Refresh data when game changes
// ---------------------------------------------------------------------------
async function refreshGameData() {
  resetGameData();

  if (state.gameUnsupported) {
    // Stop scenes and show nothing useful
    if (currentSceneKey) {
      game.scene.stop(currentSceneKey);
      currentSceneKey = null;
    }
    return;
  }

  await Promise.all([
    api.fetchGffList('map'),
    api.fetchGffList('resources'),
    api.fetchGffList('images'),
    api.fetchGffList('text'),
    api.fetchGffList('music'),
    api.fetchGffList('sfx'),
    api.fetchAllPals(),
  ]);

  buildPalSelector(state.allPals);
  await activateMode(state.mode);
}

// ---------------------------------------------------------------------------
// Mode activation
// ---------------------------------------------------------------------------
export async function activateMode(mode) {
  state.mode = mode;
  pushUrlState();

  // Highlight active nav button
  document.querySelectorAll('.mode-btn').forEach(b =>
    b.classList.toggle('active', b.dataset.mode === mode));

  // Update toolbar visibility
  $('mode-gff-group').classList.toggle('hidden', NO_GFF_MODES.has(mode));
  $('pal-group').classList.toggle('hidden', NO_PAL_MODES.has(mode));

  // Populate GFF selector if needed
  if (!NO_GFF_MODES.has(mode)) {
    await api.fetchGffList(mode);
    // Auto-select first GFF if none chosen yet
    if (!state.gffByMode[mode] && state.gffListByMode[mode]?.length > 0) {
      state.gffByMode[mode] = state.gffListByMode[mode][0];
    }
    populateGffSelect(mode);
  }

  // Load resources for modes that need them
  if (['resources', 'images'].includes(mode)) {
    const gff = state.gffByMode[mode];
    if (gff) {
      const [resources, pals] = await Promise.all([
        api.fetchResources(gff),
        api.fetchPals(gff),
      ]);
      state.resources = resources;
      state.pals = pals;
    }
  }

  // Stop current Phaser scene
  if (currentSceneKey) {
    game.scene.stop(currentSceneKey);
    currentSceneKey = null;
  }

  // Toggle canvas vs DOM views
  const isDom = DOM_MODES.has(mode);
  $('game-container').style.display = isDom ? 'none' : '';
  if (!isDom) hideDomPanels();

  // Start the new scene
  const sceneKey = MODE_SCENES[mode];
  if (sceneKey) {
    currentSceneKey = sceneKey;
    game.scene.start(sceneKey, { mode });
  }
}

// ---------------------------------------------------------------------------
// GFF selector
// ---------------------------------------------------------------------------
function populateGffSelect(mode) {
  const select = $('mode-gff-select');
  const list = state.gffListByMode[mode] || [];
  const current = state.gffByMode[mode];
  select.innerHTML = '<option value="">\u2014 Select file \u2014</option>';
  list.forEach(name => {
    const opt = document.createElement('option');
    opt.value = name;
    opt.textContent = name;
    select.appendChild(opt);
  });
  select.value = current || '';
}

// ---------------------------------------------------------------------------
// Palette selector
// ---------------------------------------------------------------------------
function buildPalSelector(allPals) {
  const sel = $('pal-select');
  sel.innerHTML = '<option value="auto">Auto\u2011detect</option>';
  allPals.forEach((p, i) => {
    const opt = document.createElement('option');
    opt.value = 'a' + i;
    const tag = (p.tag || 'PAL').trim();
    opt.textContent = `${p.gff} \u2014 ${tag} #${p.number}`;
    sel.appendChild(opt);
  });
  sel.value = 'auto';
  state.selectedPal = null;
}

// ---------------------------------------------------------------------------
// Collapse toggles — header, sidebar, toolbar
// ---------------------------------------------------------------------------
const UI_PREFS_KEY = 'yuan-ti-ui-v1';

function initCollapseToggles() {
  const app  = document.getElementById('app');
  const prefs = (() => {
    try { return JSON.parse(localStorage.getItem(UI_PREFS_KEY) || '{}'); }
    catch { return {}; }
  })();

  // Restore persisted states
  if (prefs.headerMinimized) app.classList.add('header-minimized');
  if (prefs.navCondensed)    app.classList.add('nav-condensed');
  if (prefs.toolbarHidden)   app.classList.add('toolbar-hidden');

  function savePrefs() {
    localStorage.setItem(UI_PREFS_KEY, JSON.stringify(prefs));
  }

  // ---- Header toggle ----
  const btnHeader = document.getElementById('btn-toggle-header');
  if (btnHeader) {
    const updateHeader = () => {
      const on = app.classList.contains('header-minimized');
      btnHeader.textContent = on ? '\u2304' : '\u2303'; // ⌄ / ⌃
      btnHeader.title = on ? 'Expand title bar' : 'Collapse title bar';
    };
    updateHeader();
    btnHeader.addEventListener('click', () => {
      app.classList.toggle('header-minimized');
      prefs.headerMinimized = app.classList.contains('header-minimized');
      savePrefs();
      updateHeader();
    });
  }

  // ---- Nav toggle ----
  const btnNav = document.getElementById('btn-toggle-nav');
  if (btnNav) {
    const updateNav = () => {
      const on = app.classList.contains('nav-condensed');
      btnNav.textContent = on ? '\u25b6' : '\u25c4'; // ▶ / ◀
      btnNav.title = on ? 'Expand sidebar' : 'Collapse sidebar';
    };
    updateNav();
    btnNav.addEventListener('click', () => {
      app.classList.toggle('nav-condensed');
      prefs.navCondensed = app.classList.contains('nav-condensed');
      savePrefs();
      updateNav();
    });
  }

  // ---- Toolbar toggle ----
  const btnToolbar    = document.getElementById('btn-toggle-toolbar');
  const restoreTab    = document.getElementById('toolbar-restore-tab');
  if (btnToolbar) {
    btnToolbar.addEventListener('click', () => {
      app.classList.add('toolbar-hidden');
      prefs.toolbarHidden = true;
      savePrefs();
    });
  }
  if (restoreTab) {
    restoreTab.addEventListener('click', () => {
      app.classList.remove('toolbar-hidden');
      prefs.toolbarHidden = false;
      savePrefs();
    });
  }
}
