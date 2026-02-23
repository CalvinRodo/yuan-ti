/* ================================================================
   view-cinematic.js  —  FLI/FLC cinematic player for GFF Viewer
   Fetches cinematic files from the server (which converts FLI→MP4
   via ffmpeg on first request) and plays them in a <video> element.
   Depends on globals from app.js: state, htmlEsc(), formatBytes()
   ================================================================ */

'use strict';

let cinematicViewBuilt = false;
let cinematicList      = [];   // [{filename, name, size}]
let cinematicGame      = null; // game id when list was last fetched

// ---------------------------------------------------------------------------
// Entry point called by app.js renderContent()
// ---------------------------------------------------------------------------
async function renderCinematicView() {
  const view = document.getElementById('cinematic-view');
  if (!view) return;
  view.classList.remove('hidden');

  if (!cinematicViewBuilt) {
    buildCinematicDom(view);
    cinematicViewBuilt = true;
  }

  if (cinematicGame !== state.game) {
    cinematicGame = state.game;
    await loadCinematicList();
  }
}

// ---------------------------------------------------------------------------
// Build the DOM structure (once)
// ---------------------------------------------------------------------------
function buildCinematicDom(view) {
  view.innerHTML = `
    <div id="cinematic-layout">

      <!-- Sidebar: list of cinematics -->
      <div id="cinematic-sidebar">
        <div id="cinematic-sidebar-header">
          <span class="music-section-label">Cinematics</span>
          <span id="cinematic-count" class="dim"></span>
        </div>
        <div id="cinematic-list"></div>
      </div>

      <!-- Player panel -->
      <div id="cinematic-player">
        <div id="cinematic-empty">
          <div class="empty-icon">&#127909;</div>
          <p>Select a cinematic to play it.</p>
          <p class="dim" id="cinematic-convert-note"></p>
        </div>

        <div id="cinematic-player-inner" class="hidden">
          <div id="cinematic-title-bar">
            <span id="cinematic-title"></span>
            <button id="cinematic-close-btn" title="Close">&#10005;</button>
          </div>
          <div id="cinematic-video-wrap">
            <div id="cinematic-spinner" class="hidden">
              <div class="spinner"></div>
              <span id="cinematic-spinner-msg">Converting&hellip; (first load may take a moment)</span>
            </div>
            <video id="cinematic-video" controls playsinline></video>
          </div>
        </div>
      </div>

    </div>
  `;

  // Close button
  document.getElementById('cinematic-close-btn').addEventListener('click', () => {
    const video = document.getElementById('cinematic-video');
    video.pause();
    video.src = '';
    document.getElementById('cinematic-player-inner').classList.add('hidden');
    document.getElementById('cinematic-empty').classList.remove('hidden');
    document.querySelectorAll('.cinematic-row.active').forEach(r => r.classList.remove('active'));
  });
}

// ---------------------------------------------------------------------------
// Fetch the list of available cinematic files from the server
// ---------------------------------------------------------------------------
async function loadCinematicList() {
  const listEl  = document.getElementById('cinematic-list');
  const countEl = document.getElementById('cinematic-count');
  if (!listEl) return;

  listEl.innerHTML = '<div class="music-loading"><div class="spinner"></div> Loading&hellip;</div>';

  try {
    const r = await fetch(`/api/cinematics?game=${state.game}`);
    cinematicList = await r.json();
  } catch {
    listEl.innerHTML = '<div class="music-error">Failed to load cinematic list.</div>';
    return;
  }

  if (countEl) countEl.textContent =
    `${cinematicList.length} file${cinematicList.length !== 1 ? 's' : ''}`;

  if (cinematicList.length === 0) {
    listEl.innerHTML = '<div class="music-error">No cinematic files found for this game.</div>';
    const note = document.getElementById('cinematic-convert-note');
    if (note) note.textContent = '';
    return;
  }

  const note = document.getElementById('cinematic-convert-note');
  if (note) note.textContent = 'Files are converted to MP4 on first play — this may take a moment.';

  const frag = document.createDocumentFragment();
  cinematicList.forEach((item, i) => {
    const row = document.createElement('div');
    row.className   = 'cinematic-row music-track-row';
    row.dataset.idx = i;
    row.innerHTML = `
      <span class="music-track-icon">&#127909;</span>
      <span class="music-track-label">${htmlEsc(item.name)}</span>
      <span class="music-track-size dim">${formatBytes(item.size)}</span>
    `;
    row.addEventListener('click', () => playCinematic(i));
    frag.appendChild(row);
  });

  listEl.innerHTML = '';
  listEl.appendChild(frag);
}

// ---------------------------------------------------------------------------
// Load and play a cinematic
// ---------------------------------------------------------------------------
function playCinematic(idx) {
  const item  = cinematicList[idx];
  if (!item) return;

  // Highlight row
  document.querySelectorAll('.cinematic-row').forEach((r, i) => {
    r.classList.toggle('active', i === idx);
  });

  const emptyEl   = document.getElementById('cinematic-empty');
  const playerEl  = document.getElementById('cinematic-player-inner');
  const spinnerEl = document.getElementById('cinematic-spinner');
  const video     = document.getElementById('cinematic-video');
  const titleEl   = document.getElementById('cinematic-title');

  emptyEl.classList.add('hidden');
  playerEl.classList.remove('hidden');
  spinnerEl.classList.remove('hidden');
  video.classList.add('hidden');

  if (titleEl) titleEl.textContent = item.name;

  // Stop any existing playback
  video.pause();
  video.removeAttribute('src');
  video.load();

  const url = `/api/cinematic/${encodeURIComponent(item.filename)}?game=${state.game}`;

  video.oncanplay = () => {
    spinnerEl.classList.add('hidden');
    video.classList.remove('hidden');
    video.play().catch(() => { /* user gesture required on some browsers */ });
  };

  video.onerror = () => {
    spinnerEl.classList.add('hidden');
    video.classList.remove('hidden');
    const spinMsg = document.getElementById('cinematic-spinner-msg');
    if (spinMsg) spinMsg.textContent = 'Failed to load cinematic.';
  };

  video.src = url;
  video.load();
}

// ---------------------------------------------------------------------------
// Keyboard shortcut: Escape closes video
// ---------------------------------------------------------------------------
document.addEventListener('keydown', e => {
  if (state.mode !== 'cinematic') return;
  if (e.code === 'Escape') {
    document.getElementById('cinematic-close-btn')?.click();
  }
});
