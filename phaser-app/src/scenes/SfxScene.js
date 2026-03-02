/* ================================================================
   SfxScene — Sound effects player.
   Uses a DOM panel with track list and HTML5 audio for playback.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';
import { showDomPanel, htmlEsc, formatBytes } from '../utils.js';

export default class SfxScene extends Phaser.Scene {
  constructor() {
    super('SfxScene');
    this._built = false;
    this._tracks = [];
    this._audio = null;
  }

  create() {
    const panel = showDomPanel('SfxScene');
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');
    this._loadTracks(panel);
  }

  shutdown() {
    if (this._audio) { this._audio.pause(); this._audio.src = ''; }
  }

  _build(panel) {
    panel.innerHTML = `
      <div class="music-view">
        <div class="music-sidebar">
          <div class="music-sidebar-header">
            <span class="music-section-label">Sound Effects</span>
            <span class="sfx-track-count dim"></span>
          </div>
          <div class="sfx-track-list"></div>
        </div>
        <div class="music-player">
          <div class="sfx-player-empty">
            <div style="font-size:48px;opacity:.5">&#128266;</div>
            <p>Select a sound effect to play it.</p>
          </div>
          <div class="sfx-player-active hidden">
            <div class="sfx-now-playing"></div>
            <audio controls></audio>
          </div>
        </div>
      </div>
    `;
  }

  async _loadTracks(panel) {
    const listEl = panel.querySelector('.sfx-track-list');
    const countEl = panel.querySelector('.sfx-track-count');
    listEl.innerHTML = '<div class="music-loading"><div class="spinner"></div> Loading\u2026</div>';

    let tracks = [];

    // Fetch SFX list (includes both GFF-embedded BVOC and loose VOC files)
    try {
      const sfxItems = await api.fetchSfx();
      sfxItems.forEach(f => {
        if (f.source === 'gff') {
          // GFF-embedded BVOC resource — play via /api/gff/.../audio/{tag_url}/{number}
          tracks.push({
            label: f.name || `${(f.tag || '').trim()} #${f.number}`,
            size: f.size,
            type: 'gff',
            gff: f.gff,
            tag_url: f.tag_url,
            number: f.number,
          });
        } else {
          // Standalone VOC file — play via /api/sfx-file/{filename}
          tracks.push({
            label: f.name || f.filename,
            size: f.size,
            type: 'sfx',
            sfx_file: f.filename,
          });
        }
      });
    } catch { /* empty */ }

    // Fetch OGG files
    try {
      const ogg = await api.fetchOggFiles();
      ogg.forEach(f => tracks.push({
        label: f.name || f.filename,
        size: f.size,
        type: 'ogg', ogg_file: f.filename,
      }));
    } catch { /* empty */ }

    this._tracks = tracks;
    countEl.textContent = `${tracks.length} sound${tracks.length !== 1 ? 's' : ''}`;

    if (tracks.length === 0) {
      listEl.innerHTML = '<div class="music-error">No sound effects found.</div>';
      return;
    }

    listEl.innerHTML = '';
    const frag = document.createDocumentFragment();
    tracks.forEach((track, i) => {
      const row = document.createElement('div');
      row.className = 'music-track-row';
      row.innerHTML = `
        <span class="music-track-icon">&#128266;</span>
        <span class="music-track-label">${htmlEsc(track.label)}</span>
        <span class="music-track-size dim">${formatBytes(track.size || 0)}</span>
      `;
      row.addEventListener('click', () => this._playTrack(panel, i));
      frag.appendChild(row);
    });
    listEl.appendChild(frag);
  }

  _playTrack(panel, idx) {
    const track = this._tracks[idx];
    if (!track) return;

    panel.querySelectorAll('.music-track-row').forEach((r, i) => {
      r.classList.toggle('active', i === idx);
    });

    const emptyEl = panel.querySelector('.sfx-player-empty');
    const activeEl = panel.querySelector('.sfx-player-active');
    const audioEl = panel.querySelector('audio');
    const nowPlaying = panel.querySelector('.sfx-now-playing');

    emptyEl.classList.add('hidden');
    activeEl.classList.remove('hidden');
    nowPlaying.textContent = `Now playing: ${track.label}`;

    let url;
    if (track.type === 'gff') {
      url = `/api/gff/${encodeURIComponent(track.gff)}/audio/${track.tag_url}/${track.number}?game=${state.game}`;
    } else if (track.type === 'sfx') {
      url = api.sfxFileUrl(track.sfx_file);
    } else if (track.type === 'ogg') {
      url = api.oggFileUrl(track.ogg_file);
    }

    if (this._audio) this._audio.pause();
    this._audio = audioEl;
    audioEl.src = url;
    audioEl.load();
    audioEl.play().catch(() => {});
  }
}
