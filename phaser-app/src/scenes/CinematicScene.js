/* ================================================================
   CinematicScene — FLI/FLC cinematic player.
   Uses a DOM panel with a sidebar list and HTML5 <video> element.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';
import { showDomPanel, htmlEsc, formatBytes } from '../utils.js';

export default class CinematicScene extends Phaser.Scene {
  constructor() {
    super('CinematicScene');
    this._built = false;
    this._list = [];
    this._listGame = null;
  }

  create() {
    const panel = showDomPanel('CinematicScene');
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');

    if (this._listGame !== state.game) {
      this._listGame = state.game;
      this._loadList(panel);
    }
  }

  shutdown() {
    const panel = document.getElementById('panel-CinematicScene');
    if (panel) {
      const video = panel.querySelector('video');
      if (video) { video.pause(); video.src = ''; }
    }
  }

  _build(panel) {
    panel.innerHTML = `
      <div class="cinematic-view">
        <div class="cinematic-sidebar">
          <div class="cinematic-sidebar-header">
            <span class="music-section-label">Cinematics</span>
            <span class="cinematic-count dim"></span>
          </div>
          <div class="cinematic-list"></div>
        </div>
        <div class="cinematic-player-area">
          <div class="cinematic-player-empty">
            <div style="font-size:48px;opacity:.5">&#127909;</div>
            <p>Select a cinematic to play it.</p>
            <p class="dim" style="font-size:12px">Files are converted to MP4 on first play.</p>
          </div>
          <video class="hidden" controls playsinline></video>
        </div>
      </div>
    `;
  }

  async _loadList(panel) {
    const listEl = panel.querySelector('.cinematic-list');
    const countEl = panel.querySelector('.cinematic-count');
    listEl.innerHTML = '<div class="music-loading"><div class="spinner"></div> Loading\u2026</div>';

    this._list = await api.fetchCinematics();
    countEl.textContent = `${this._list.length} file${this._list.length !== 1 ? 's' : ''}`;

    if (this._list.length === 0) {
      listEl.innerHTML = '<div class="music-error">No cinematic files found for this game.</div>';
      return;
    }

    listEl.innerHTML = '';
    const frag = document.createDocumentFragment();
    this._list.forEach((item, i) => {
      const row = document.createElement('div');
      row.className = 'music-track-row';
      row.innerHTML = `
        <span class="music-track-icon">&#127909;</span>
        <span class="music-track-label">${htmlEsc(item.name)}</span>
        <span class="music-track-size dim">${formatBytes(item.size)}</span>
      `;
      row.addEventListener('click', () => this._play(panel, i));
      frag.appendChild(row);
    });
    listEl.appendChild(frag);
  }

  _play(panel, idx) {
    const item = this._list[idx];
    if (!item) return;

    panel.querySelectorAll('.music-track-row').forEach((r, i) => {
      r.classList.toggle('active', i === idx);
    });

    const emptyEl = panel.querySelector('.cinematic-player-empty');
    const video = panel.querySelector('video');

    emptyEl.classList.add('hidden');
    video.classList.remove('hidden');

    video.src = `/api/cinematic/${encodeURIComponent(item.filename)}?game=${state.game}`;
    video.load();
    video.play().catch(() => {});
  }
}
