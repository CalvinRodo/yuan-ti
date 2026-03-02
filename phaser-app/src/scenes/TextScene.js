/* ================================================================
   TextScene — Text resource viewer (TEXT, MERR, SPIN, NAME).
   Uses a DOM panel for the searchable text list + reader.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';
import { showDomPanel, htmlEsc } from '../utils.js';

export default class TextScene extends Phaser.Scene {
  constructor() {
    super('TextScene');
    this._built = false;
    this._data = [];
    this._dataGff = null;
    this._filter = '';
    this._activeIdx = null;
  }

  create() {
    const panel = showDomPanel('TextScene');
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');
    this._loadData();
  }

  shutdown() {
    // Panel stays in DOM (cached), just hidden by the next mode switch
  }

  _build(panel) {
    panel.innerHTML = `
      <div class="text-view">
        <div class="text-sidebar">
          <div class="search-wrap">
            <input class="text-search-input" type="search" placeholder="Search\u2026" autocomplete="off" spellcheck="false">
            <span class="search-count dim"></span>
          </div>
          <ul class="text-list"></ul>
        </div>
        <div class="text-reader">
          <div class="text-reader-empty">Select an entry to read it.</div>
          <div class="text-reader-content hidden">
            <div class="text-reader-header">
              <span class="text-tag-badge"></span>
              <span class="text-reader-num dim"></span>
            </div>
            <div class="text-reader-body"></div>
          </div>
        </div>
      </div>
    `;

    panel.querySelector('.text-search-input').addEventListener('input', (e) => {
      this._filter = e.target.value.toLowerCase();
      this._renderList(panel);
    });
  }

  async _loadData() {
    const gff = state.gffByMode.text;
    if (!gff) return;
    const panel = document.getElementById('panel-TextScene');
    if (!panel) return;

    if (this._dataGff !== gff) {
      this._dataGff = gff;
      this._data = [];
      this._activeIdx = null;
      this._filter = '';
      const input = panel.querySelector('.text-search-input');
      if (input) input.value = '';
      this._clearReader(panel);
      this._data = await api.fetchTexts(gff);
    }
    this._renderList(panel);
  }

  _renderList(panel) {
    const listEl = panel.querySelector('.text-list');
    const countEl = panel.querySelector('.search-count');
    if (!listEl) return;

    const filtered = this._filter
      ? this._data.filter(e => {
          const body = Array.isArray(e.content)
            ? e.content.map(x => typeof x === 'object' ? x.text + ' ' + x.path : x).join(' ')
            : e.content;
          return e.tag.toLowerCase().includes(this._filter) ||
                 String(e.number).includes(this._filter) ||
                 body.toLowerCase().includes(this._filter);
        })
      : this._data;

    countEl.textContent = this._filter
      ? `${filtered.length} / ${this._data.length}`
      : `${this._data.length} entries`;

    listEl.innerHTML = '';
    const frag = document.createDocumentFragment();

    filtered.forEach((entry) => {
      const origIdx = this._data.indexOf(entry);
      const li = document.createElement('li');
      li.className = 'text-list-item';
      if (origIdx === this._activeIdx) li.classList.add('active');

      const previewSrc = Array.isArray(entry.content)
        ? entry.content.map(e => typeof e === 'object' ? e.text : e).join(' \u00b7 ')
        : entry.content;
      const preview = previewSrc.trim().replace(/\s+/g, ' ').slice(0, 90);
      const ellipsis = previewSrc.trim().length > 90 ? '\u2026' : '';

      li.innerHTML =
        `<div class="text-list-header">` +
          `<span class="text-tag-badge">${htmlEsc(entry.tag)}</span>` +
          `<span class="text-list-num dim">#${entry.number}</span>` +
        `</div>` +
        `<div class="text-list-preview">${htmlEsc(preview)}${ellipsis}</div>`;

      li.addEventListener('click', () => {
        this._activeIdx = origIdx;
        listEl.querySelectorAll('.text-list-item.active').forEach(el => el.classList.remove('active'));
        li.classList.add('active');
        this._showEntry(panel, this._data[origIdx]);
      });

      frag.appendChild(li);
    });

    listEl.appendChild(frag);
  }

  _showEntry(panel, entry) {
    const emptyEl = panel.querySelector('.text-reader-empty');
    const contentEl = panel.querySelector('.text-reader-content');
    const badgeEl = panel.querySelector('.text-tag-badge');
    const numEl = panel.querySelector('.text-reader-num');
    const bodyEl = panel.querySelector('.text-reader-body');

    emptyEl.classList.add('hidden');
    contentEl.classList.remove('hidden');
    badgeEl.textContent = entry.tag;
    numEl.textContent = `#${entry.number}`;

    if (Array.isArray(entry.content)) {
      bodyEl.innerHTML = entry.content.map(item => {
        if (typeof item === 'object') {
          return `<div class="text-name-entry"><strong>${htmlEsc(item.text)}</strong><br><span class="dim">${htmlEsc(item.path || '')}</span></div>`;
        }
        return `<p>${htmlEsc(item)}</p>`;
      }).join('');
    } else {
      bodyEl.innerHTML = `<p>${htmlEsc(entry.content)}</p>`;
    }
  }

  _clearReader(panel) {
    const emptyEl = panel.querySelector('.text-reader-empty');
    const contentEl = panel.querySelector('.text-reader-content');
    if (emptyEl) emptyEl.classList.remove('hidden');
    if (contentEl) contentEl.classList.add('hidden');
  }
}
