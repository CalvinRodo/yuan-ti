/* ================================================================
   ResourceScene — Resource table viewer with detail pane.
   Uses a DOM panel with a filterable, sortable table of all
   resources in the currently selected GFF file.
   Click a row to see image preview (with frame nav) or hex dump.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';
import { showDomPanel, htmlEsc, formatBytes } from '../utils.js';

export default class ResourceScene extends Phaser.Scene {
  constructor() {
    super('ResourceScene');
    this._built = false;
    this._sortCol = null;
    this._sortDir = 1;
    this._tagFilter = '';
    this._selectedRes = null;
    this._frameIdx = 0;
    this._frameCount = 1;
    this._animTimer = null;
    this._animPlaying = false;
  }

  create() {
    const panel = showDomPanel('ResourceScene');
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');
    this._render(panel);
  }

  shutdown() { this._stopAnim(); }

  _build(panel) {
    panel.innerHTML = `
      <div class="resource-view">
        <div class="resource-filter-bar">
          <select class="resource-tag-filter">
            <option value="">All types</option>
          </select>
          <span class="resource-filter-count dim"></span>
        </div>
        <div class="resource-main">
          <div class="resource-table-wrap">
            <table class="resource-table">
              <thead>
                <tr>
                  <th data-col="tag">Tag</th>
                  <th data-col="number">Number</th>
                  <th data-col="size">Size</th>
                  <th data-col="description">Description</th>
                </tr>
              </thead>
              <tbody></tbody>
            </table>
          </div>
          <div class="resource-detail hidden">
            <div class="resource-detail-header">
              <span class="res-dtl-title"></span>
              <button class="res-dtl-close">&times;</button>
            </div>
            <div class="resource-detail-body"></div>
          </div>
        </div>
      </div>
    `;

    // Sort by column header click
    panel.querySelectorAll('.resource-table th[data-col]').forEach(th => {
      th.addEventListener('click', () => {
        const col = th.dataset.col;
        if (this._sortCol === col) {
          this._sortDir *= -1;
        } else {
          this._sortCol = col;
          this._sortDir = 1;
        }
        this._render(panel);
      });
    });

    // Filter by tag
    panel.querySelector('.resource-tag-filter').addEventListener('change', (e) => {
      this._tagFilter = e.target.value;
      this._render(panel);
    });

    // Close detail
    panel.querySelector('.res-dtl-close').addEventListener('click', () => {
      this._stopAnim();
      panel.querySelector('.resource-detail').classList.add('hidden');
      this._selectedRes = null;
    });
  }

  _render(panel) {
    const resources = state.resources;
    const filterSel = panel.querySelector('.resource-tag-filter');
    const countEl = panel.querySelector('.resource-filter-count');
    const tbody = panel.querySelector('.resource-table tbody');

    // Build tag filter options
    const tags = [...new Set(resources.map(r => r.tag.trim()))].sort();
    const savedTag = this._tagFilter;
    filterSel.innerHTML = '<option value="">All types</option>' +
      tags.map(t => `<option value="${htmlEsc(t)}">${htmlEsc(t)}</option>`).join('');
    filterSel.value = tags.includes(savedTag) ? savedTag : '';

    // Filter
    let filtered = this._tagFilter
      ? resources.filter(r => r.tag.trim() === this._tagFilter)
      : resources;

    // Sort
    if (this._sortCol) {
      const col = this._sortCol;
      const dir = this._sortDir;
      filtered = [...filtered].sort((a, b) => {
        let va = a[col], vb = b[col];
        if (col === 'tag') { va = va.trim(); vb = vb.trim(); }
        if (typeof va === 'string') return va.localeCompare(vb) * dir;
        return ((va || 0) - (vb || 0)) * dir;
      });
    }

    countEl.textContent = `${filtered.length} / ${resources.length} resources`;

    // Render
    tbody.innerHTML = '';
    const frag = document.createDocumentFragment();
    filtered.forEach(res => {
      const tr = document.createElement('tr');
      tr.className = this._selectedRes === res ? 'active' : '';
      tr.innerHTML = `
        <td class="tag-cell">${htmlEsc(res.tag.trim())}</td>
        <td>${res.number}</td>
        <td>${formatBytes(res.size)}</td>
        <td class="dim">${htmlEsc(res.description || '')}</td>
      `;
      tr.addEventListener('click', () => this._openDetail(panel, res));
      tr.style.cursor = 'pointer';
      frag.appendChild(tr);
    });
    tbody.appendChild(frag);
  }

  /* ---- Detail pane ---- */
  async _openDetail(panel, res) {
    this._stopAnim();
    this._selectedRes = res;
    this._frameIdx = 0;
    this._frameCount = 1;
    const gff = state.gffByMode.resources;

    const detailEl = panel.querySelector('.resource-detail');
    const titleEl = panel.querySelector('.res-dtl-title');
    const bodyEl = panel.querySelector('.resource-detail-body');

    detailEl.classList.remove('hidden');
    titleEl.textContent = `${res.tag.trim()} #${res.number} — ${formatBytes(res.size)}`;
    bodyEl.innerHTML = '<div class="music-loading"><div class="spinner"></div> Loading\u2026</div>';

    // Highlight table row
    panel.querySelectorAll('.resource-table tbody tr').forEach(tr => {
      tr.classList.toggle('active', tr === event?.currentTarget);
    });

    if (res.is_image) {
      await this._showImageDetail(bodyEl, gff, res);
    } else {
      await this._showHexDetail(bodyEl, gff, res);
    }
  }

  /* ---- Image detail ---- */
  async _showImageDetail(bodyEl, gff, res) {
    const tagUrl = (res.tag_url || res.tag).trim() + '_'.repeat(Math.max(0, 4 - (res.tag_url || res.tag).trim().length));
    const info = await api.fetchImageInfo(gff, tagUrl, res.number);
    this._frameCount = info?.frame_count || 1;

    bodyEl.innerHTML = `
      <div class="res-img-preview">
        <img class="res-img" alt="">
      </div>
      <div class="res-img-nav">
        <button class="spr-btn res-prev-frame">&laquo; Prev</button>
        <span class="res-frame-info">Frame 1 / ${this._frameCount}</span>
        <button class="spr-btn res-next-frame">Next &raquo;</button>
        ${this._frameCount > 1 ? '<button class="spr-btn res-anim-btn">&#9654; Animate</button>' : ''}
      </div>
      <div class="res-img-meta dim"></div>
      <div class="res-dl-row">
        <a class="spr-btn res-dl-raw" download>&#11015; Download raw</a>
      </div>`;

    const imgEl = bodyEl.querySelector('.res-img');
    const infoEl = bodyEl.querySelector('.res-frame-info');
    const metaEl = bodyEl.querySelector('.res-img-meta');
    const rawLink = bodyEl.querySelector('.res-dl-raw');
    rawLink.href = api.rawUrl(gff, tagUrl, res.number);

    const showFrame = (idx) => {
      this._frameIdx = idx;
      imgEl.src = api.imageUrl(gff, tagUrl, res.number, idx, 3);
      infoEl.textContent = `Frame ${idx + 1} / ${this._frameCount}`;
      if (info) {
        const fi = info.frames?.[idx];
        metaEl.textContent = fi
          ? `${fi.width}×${fi.height}px`
          : `${info.width || '?'}×${info.height || '?'}px`;
      }
    };
    showFrame(0);

    bodyEl.querySelector('.res-prev-frame').addEventListener('click', () => {
      showFrame((this._frameIdx - 1 + this._frameCount) % this._frameCount);
    });
    bodyEl.querySelector('.res-next-frame').addEventListener('click', () => {
      showFrame((this._frameIdx + 1) % this._frameCount);
    });

    const animBtn = bodyEl.querySelector('.res-anim-btn');
    if (animBtn) {
      animBtn.addEventListener('click', () => {
        if (this._animPlaying) {
          this._stopAnim();
          animBtn.innerHTML = '&#9654; Animate';
        } else {
          this._animPlaying = true;
          animBtn.innerHTML = '&#9208; Stop';
          this._animTimer = setInterval(() => {
            showFrame((this._frameIdx + 1) % this._frameCount);
          }, 150);
        }
      });
    }
  }

  /* ---- Hex dump detail ---- */
  async _showHexDetail(bodyEl, gff, res) {
    const tagUrl = (res.tag_url || res.tag).trim() + '_'.repeat(Math.max(0, 4 - (res.tag_url || res.tag).trim().length));
    try {
      const raw = await api.fetchRaw(gff, tagUrl, res.number);
      const maxBytes = 8192;
      const truncated = raw.length > maxBytes;
      const bytes = truncated ? raw.slice(0, maxBytes) : raw;

      let lines = [];
      for (let off = 0; off < bytes.length; off += 16) {
        const hex = [];
        const ascii = [];
        for (let i = 0; i < 16; i++) {
          if (off + i < bytes.length) {
            const b = bytes[off + i];
            hex.push(b.toString(16).padStart(2, '0'));
            ascii.push(b >= 0x20 && b <= 0x7e ? String.fromCharCode(b) : '.');
          } else {
            hex.push('  ');
            ascii.push(' ');
          }
        }
        const offStr = off.toString(16).padStart(8, '0');
        lines.push(`${offStr}  ${hex.slice(0,8).join(' ')}  ${hex.slice(8).join(' ')}  |${ascii.join('')}|`);
      }

      bodyEl.innerHTML = `
        <div class="res-hex-dump">
          <pre>${htmlEsc(lines.join('\n'))}</pre>
          ${truncated ? `<div class="dim">Showing first ${formatBytes(maxBytes)} of ${formatBytes(raw.length)}</div>` : ''}
        </div>
        <div class="res-dl-row">
          <a class="spr-btn res-dl-raw" href="${api.rawUrl(gff, tagUrl, res.number)}" download>&#11015; Download raw</a>
        </div>`;
    } catch (e) {
      bodyEl.innerHTML = `<div class="music-error">Error loading resource: ${htmlEsc(e.message)}</div>`;
    }
  }

  _stopAnim() {
    if (this._animTimer) { clearInterval(this._animTimer); this._animTimer = null; }
    this._animPlaying = false;
  }
}
