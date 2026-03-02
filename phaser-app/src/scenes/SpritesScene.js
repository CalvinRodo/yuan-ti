/* ================================================================
   SpritesScene — 3-pane sprite catalog viewer.
   
   Pane 1 (left):   Compact sprite list with type/search filters,
                     animated-vs-static badges, linked sprite groups.
   Pane 2 (middle): Frame grouping/section management, patterns.
   Pane 3 (right):  Zoomed animation preview with frame controls.
   ================================================================ */

import Phaser from 'phaser';
import { state, prefs } from '../config.js';
import * as api from '../api.js';
import { showDomPanel, htmlEsc, formatBytes } from '../utils.js';

const SPRITE_TYPES = ['All', 'PC', 'NPC', 'Monster', 'Scenery', 'Effect', 'Spell', 'Item', 'Unknown'];

const ANCHOR_GRID = [
  ['top-left', 'top-center', 'top-right'],
  ['middle-left', 'center', 'middle-right'],
  ['bottom-left', 'bottom-center', 'bottom-right'],
];
const ANCHOR_ARROWS = {
  'top-left': '\u2196', 'top-center': '\u2191', 'top-right': '\u2197',
  'middle-left': '\u2190', 'center': '\u25CF', 'middle-right': '\u2192',
  'bottom-left': '\u2199', 'bottom-center': '\u2193', 'bottom-right': '\u2198',
};

const ANIM_INTERVAL = 150;

export default class SpritesScene extends Phaser.Scene {
  constructor() {
    super('SpritesScene');
    this._built = false;
    this._catalog = null;
    this._game = null;
    this._filter = '';
    this._typeFilter = 'All';
    this._selected = null;
    this._spriteGff = 'SEGOBJEX.GFF';
    this._patternsCache = null;
    this._imgCache = new Map();
    this._savePending = {};
    this._saveTimer = null;

    // Pane 2 state
    this._selMode = false;
    this._selFrames = new Set();
    this._editingSec = null;

    // Pane 3 state
    this._previewAnim = null;
    this._previewFrameIdx = 0;
    this._previewFrameCount = 0;
    this._previewSection = null;

    // Linked groups
    this._linkedGroups = new Map();
    this._collapsed = new Set();
  }

  create() {
    const panel = showDomPanel('SpritesScene');
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');

    if (this._game !== state.game || !this._catalog) {
      this._game = state.game;
      this._catalog = null;
      this._selected = null;
      this._patternsCache = null;
      this._stopPreview();
      this._loadCatalog(panel);
    }
  }

  shutdown() { this._stopPreview(); }

  /* ================================================================
     BUILD — initial DOM structure (3-pane)
     ================================================================ */
  _build(panel) {
    panel.innerHTML = `
      <div class="spr3-view">
        <!-- Pane 1: Sprite list -->
        <div class="spr3-list-pane">
          <div class="spr3-search">
            <input type="search" placeholder="Search sprites\u2026" autocomplete="off" spellcheck="false">
          </div>
          <div class="spr3-type-tabs"></div>
          <div class="spr3-list-status dim"></div>
          <ul class="spr3-list"></ul>
        </div>

        <!-- Pane 2: Grouping / Pattern management -->
        <div class="spr3-group-pane">
          <div class="spr3-group-empty">Select a sprite to manage its frame groups.</div>
          <div class="spr3-group-content hidden">
            <div class="spr3-group-header">
              <span class="spr3-group-title"></span>
              <span class="spr3-group-meta dim"></span>
            </div>
            <div class="spr3-meta-row">
              <label>Type <select class="spr3-type-sel"></select></label>
              <label>Name <input class="spr3-name-input" type="text" placeholder="name"></label>
              <span class="spr3-save-status dim"></span>
            </div>
            <div class="spr3-link-row">
              <button class="spr-btn spr3-link-btn" title="Link another BMP to this sprite">\u26D3 Link sheet</button>
              <span class="spr3-linked-info dim"></span>
            </div>
            <div class="spr3-toolbar">
              <button class="spr-btn spr3-new-group">\u2795 New group</button>
              <button class="spr-btn spr3-save-pat">\uD83D\uDCBE Save pattern</button>
              <button class="spr-btn spr3-apply-pat">\uD83D\uDCCB Apply pattern</button>
            </div>
            <div class="spr3-pattern-panel hidden"></div>
            <div class="spr3-sections"></div>
          </div>
        </div>

        <!-- Pane 3: Animation / Zoomed preview -->
        <div class="spr3-preview-pane">
          <div class="spr3-preview-empty">
            <div class="spr3-preview-icon">\uD83C\uDFAE</div>
            <div>Select a sprite, then click a section<br>or play all frames to preview.</div>
          </div>
          <div class="spr3-preview-active hidden">
            <canvas class="spr3-preview-canvas"></canvas>
            <div class="spr3-preview-controls">
              <button class="spr-btn spr3-prev-frame">\u25C0</button>
              <button class="spr-btn spr3-play-btn">\u25B6</button>
              <button class="spr-btn spr3-next-frame">\u25B6</button>
              <span class="spr3-frame-info dim">Frame 1 / 1</span>
            </div>
            <div class="spr3-preview-label dim"></div>
          </div>
        </div>
      </div>`;

    // --- Search ---
    panel.querySelector('.spr3-search input').addEventListener('input', e => {
      this._filter = e.target.value.toLowerCase();
      this._renderList(panel);
    });

    // --- Preview controls ---
    panel.querySelector('.spr3-prev-frame').addEventListener('click', () => this._stepPreview(-1));
    panel.querySelector('.spr3-next-frame').addEventListener('click', () => this._stepPreview(1));
    panel.querySelector('.spr3-play-btn').addEventListener('click', () => this._togglePreviewAnim(panel));
  }

  /* ================================================================
     CATALOG LOADING
     ================================================================ */
  async _loadCatalog(panel) {
    const listEl = panel.querySelector('.spr3-list');
    listEl.innerHTML = '<li class="spr3-loading"><div class="spinner"></div> Loading catalog\u2026</li>';
    this._catalog = await api.fetchBmpCatalog();
    if (this._catalog.length > 0 && this._catalog[0].gff_name) {
      this._spriteGff = this._catalog[0].gff_name;
    }
    this._buildLinkedGroups();
    this._buildTypeTabs(panel);
    this._renderList(panel);
  }

  /* ================================================================
     LINKED GROUPS
     ================================================================ */
  _buildLinkedGroups() {
    this._linkedGroups.clear();
    if (!this._catalog) return;
    const claimed = new Set();
    for (const sp of this._catalog) {
      if (!sp.linked_bmps?.length) continue;
      const all = [sp.bmp_num, ...sp.linked_bmps].sort((a, b) => a - b);
      const primary = all[0];
      if (claimed.has(primary)) continue;
      const linked = new Set(all.filter(n => n !== primary));
      this._linkedGroups.set(primary, linked);
      all.forEach(n => claimed.add(n));
    }
  }

  _isSecondaryLinked(bmpNum) {
    for (const [, linked] of this._linkedGroups) {
      if (linked.has(bmpNum)) return true;
    }
    return false;
  }

  _getGroupFrameCount(sprite) {
    const linked = this._linkedGroups.get(sprite.bmp_num);
    if (!linked) return sprite.frame_count || 1;
    let total = sprite.frame_count || 1;
    for (const n of linked) {
      const s = this._catalog.find(c => c.bmp_num === n);
      if (s) total += (s.frame_count || 1);
    }
    return total;
  }

  /* ================================================================
     TYPE TABS
     ================================================================ */
  _buildTypeTabs(panel) {
    const container = panel.querySelector('.spr3-type-tabs');
    container.innerHTML = '';
    SPRITE_TYPES.forEach(t => {
      const btn = document.createElement('button');
      btn.className = 'spr3-tab' + (t === this._typeFilter ? ' active' : '');
      btn.dataset.type = t;
      btn.textContent = t;
      btn.addEventListener('click', () => {
        this._typeFilter = t;
        container.querySelectorAll('.spr3-tab').forEach(b =>
          b.classList.toggle('active', b.dataset.type === t));
        this._renderList(panel);
      });
      container.appendChild(btn);
    });
  }

  /* ================================================================
     PANE 1 — Sprite list
     ================================================================ */
  _renderList(panel) {
    const listEl = panel.querySelector('.spr3-list');
    const statusEl = panel.querySelector('.spr3-list-status');
    if (!this._catalog) return;

    let items = this._catalog;
    // Filter out secondary linked sprites
    items = items.filter(s => !this._isSecondaryLinked(s.bmp_num));

    if (this._typeFilter !== 'All')
      items = items.filter(s => (s.type || 'Unknown') === this._typeFilter);
    if (this._filter)
      items = items.filter(s =>
        (s.name || '').toLowerCase().includes(this._filter) ||
        String(s.bmp_num).includes(this._filter) ||
        (s.rdff_names || []).some(n => n.toLowerCase().includes(this._filter)));

    statusEl.textContent = `${items.length} sprite${items.length !== 1 ? 's' : ''}`;

    listEl.innerHTML = '';
    const frag = document.createDocumentFragment();
    items.forEach(sprite => {
      const li = document.createElement('li');
      li.className = 'spr3-item';
      if (this._selected?.bmp_num === sprite.bmp_num) li.classList.add('active');

      const gff = sprite.gff_name || this._spriteGff;
      const thumbUrl = api.imageUrl(gff, 'BMP_', sprite.bmp_num, 0);
      const fc = sprite.frame_count || 1;
      const isAnim = fc > 1;
      const groupSize = this._linkedGroups.has(sprite.bmp_num)
        ? this._linkedGroups.get(sprite.bmp_num).size + 1 : 0;
      const totalFrames = groupSize > 0 ? this._getGroupFrameCount(sprite) : fc;

      let badges = '';
      if (isAnim) badges += '<span class="spr3-badge spr3-anim" title="Animated">\u25B6</span>';
      else badges += '<span class="spr3-badge spr3-static" title="Static">\u25A0</span>';
      if (sprite.pattern_name) badges += '<span class="spr3-badge spr3-pat" title="Has pattern">\u25C9</span>';
      if (groupSize > 0) badges += '<span class="spr3-badge spr3-link" title="' + (groupSize + 1) + ' linked sheets">\u26D3' + (groupSize + 1) + '</span>';

      const displayName = sprite.name || (sprite.rdff_names?.length ? sprite.rdff_names[0] : 'BMP #' + sprite.bmp_num);

      li.innerHTML =
        '<img src="' + thumbUrl + '" alt="" loading="lazy">' +
        '<div class="spr3-item-info">' +
        '<div class="spr3-item-name">' + htmlEsc(displayName) + ' ' + badges + '</div>' +
        '<div class="spr3-item-meta">#' + sprite.bmp_num + ' \u2022 ' + totalFrames + 'f \u2022 ' + htmlEsc(sprite.type || '?') + '</div>' +
        '</div>';

      li.addEventListener('click', () => {
        this._stopPreview();
        this._selected = sprite;
        this._selMode = false;
        this._selFrames.clear();
        this._editingSec = null;
        this._previewSection = null;
        listEl.querySelectorAll('.spr3-item').forEach(el => el.classList.remove('active'));
        li.classList.add('active');
        this._showGroupPane(panel, sprite);
        this._showPreviewForAllFrames(panel, sprite);
      });

      frag.appendChild(li);
    });
    listEl.appendChild(frag);
  }

  /* ================================================================
     PANE 2 — Frame grouping
     ================================================================ */
  async _showGroupPane(panel, sprite) {
    const emptyEl = panel.querySelector('.spr3-group-empty');
    const contentEl = panel.querySelector('.spr3-group-content');
    emptyEl.classList.add('hidden');
    contentEl.classList.remove('hidden');

    const gff = sprite.gff_name || this._spriteGff;
    const scale = prefs.spriteScale || 2;

    const displayName = sprite.name || (sprite.rdff_names?.length ? sprite.rdff_names[0] : 'BMP #' + sprite.bmp_num);
    panel.querySelector('.spr3-group-title').textContent = displayName;
    panel.querySelector('.spr3-group-meta').textContent =
      'BMP #' + sprite.bmp_num + ' \u2022 ' + (sprite.frame_count || 1) + ' frames \u2022 ' + gff;

    // Type selector
    const typeSel = panel.querySelector('.spr3-type-sel');
    typeSel.innerHTML = SPRITE_TYPES.filter(t => t !== 'All').map(t =>
      '<option value="' + t + '"' + (sprite.type === t ? ' selected' : '') + '>' + t + '</option>').join('');
    typeSel.onchange = e => {
      sprite.type = e.target.value;
      this._queueSave(sprite.bmp_num, { type: sprite.type });
    };

    // Name input
    const nameInput = panel.querySelector('.spr3-name-input');
    nameInput.value = sprite.name || '';
    nameInput.placeholder = (sprite.rdff_names || []).join(', ') || 'name';
    let nameTimer;
    nameInput.oninput = e => {
      clearTimeout(nameTimer);
      nameTimer = setTimeout(() => {
        sprite.name = e.target.value;
        this._queueSave(sprite.bmp_num, { name: sprite.name });
      }, 400);
    };

    // Linked sheets info
    const linkedInfo = panel.querySelector('.spr3-linked-info');
    const linked = this._linkedGroups.get(sprite.bmp_num);
    linkedInfo.textContent = linked?.size > 0
      ? 'Linked: ' + [...linked].map(n => '#' + n).join(', ') : '';

    // Buttons
    panel.querySelector('.spr3-link-btn').onclick = () => this._promptLink(panel, sprite);
    panel.querySelector('.spr3-new-group').onclick = () => {
      this._selMode = true;
      this._selFrames.clear();
      this._editingSec = null;
      this._renderSections(panel, sprite, gff, scale);
    };
    panel.querySelector('.spr3-save-pat').onclick = () => this._openSavePatternPanel(panel, sprite);
    panel.querySelector('.spr3-apply-pat').onclick = () => this._openApplyPatternPanel(panel, sprite);

    await this._renderSections(panel, sprite, gff, scale);
  }

  /* ---- Render sections ---- */
  async _renderSections(panel, sprite, gff, scale) {
    const container = panel.querySelector('.spr3-sections');
    if (!container) return;
    container.innerHTML = '<div class="spr3-loading"><div class="spinner"></div></div>';

    const info = await api.fetchImageInfo(gff, 'BMP_', sprite.bmp_num);
    const frameCount = info?.frame_count || sprite.frame_count || 1;

    const sections = sprite.custom_sections || [];
    const sorted = [...sections].sort((a, b) => (a.frames?.[0] ?? Infinity) - (b.frames?.[0] ?? Infinity));

    const usedFrames = new Set();
    sections.forEach(s => (s.frames || []).forEach(f => usedFrames.add(f)));
    const ungrouped = [];
    for (let i = 0; i < frameCount; i++) if (!usedFrames.has(i)) ungrouped.push(i);

    container.innerHTML = '';

    if (ungrouped.length > 0 && sections.length > 0)
      this._renderSection(container, panel, sprite, gff, scale, ungrouped,
        'Ungrouped (' + ungrouped.length + ')', null);

    if (sorted.length > 0) {
      sorted.forEach((sec, idx) =>
        this._renderSection(container, panel, sprite, gff, scale,
          sec.frames || [], sec.name || 'Group ' + (idx + 1), sec));
    } else {
      const allFrames = Array.from({ length: frameCount }, (_, i) => i);
      this._renderSection(container, panel, sprite, gff, scale, allFrames, 'All Frames (' + frameCount + ')', null);
    }

    if (this._selMode)
      this._addSelBar(container, sprite, panel, gff, scale);
  }

  _renderSection(container, panel, sprite, gff, scale, frames, title, section) {
    const strip = document.createElement('div');
    strip.className = 'spr3-strip';

    const hdr = document.createElement('div');
    hdr.className = 'spr3-strip-hdr';

    const titleSpan = document.createElement('span');
    titleSpan.className = 'spr3-strip-title';
    titleSpan.textContent = title;
    hdr.appendChild(titleSpan);

    if (section) {
      const fc = document.createElement('span');
      fc.className = 'spr3-strip-fc dim';
      fc.textContent = frames.length + 'f';
      hdr.appendChild(fc);
      if (section.mirrored) {
        const m = document.createElement('span');
        m.className = 'spr3-mirror-badge';
        m.textContent = '\u21D4';
        hdr.appendChild(m);
      }
    }

    // Preview button
    if (frames.length > 0) {
      const pvBtn = document.createElement('button');
      pvBtn.className = 'spr-btn spr3-preview-sec';
      pvBtn.textContent = frames.length > 1 ? '\u25B6 Preview' : '\uD83D\uDD0D View';
      pvBtn.addEventListener('click', () => {
        this._previewSection = section;
        this._showPreviewForFrames(panel, sprite, frames, section?.anchor || 'bottom-center', !!section?.mirrored);
      });
      hdr.appendChild(pvBtn);
    }

    // Section action buttons (edit, mirror, delete)
    if (section && !section.mirrored) {
      const editBtn = document.createElement('button');
      editBtn.className = 'spr-btn spr3-sec-btn';
      editBtn.textContent = '\u270E';
      editBtn.title = 'Edit frames';
      editBtn.addEventListener('click', () => {
        this._editingSec = section;
        this._selMode = true;
        this._selFrames = new Set(section.frames || []);
        this._renderSections(panel, sprite, gff, scale);
      });
      hdr.appendChild(editBtn);

      const mirrorBtn = document.createElement('button');
      mirrorBtn.className = 'spr-btn spr3-sec-btn';
      mirrorBtn.textContent = '\u21C4';
      mirrorBtn.title = 'Create mirrored copy';
      mirrorBtn.addEventListener('click', () => {
        const secs = sprite.custom_sections || [];
        const existing = secs.find(s => s.mirrorOf === section.id);
        if (existing) {
          sprite.custom_sections = secs.filter(s => s.id !== existing.id);
        } else {
          const idx = secs.indexOf(section);
          secs.splice(idx + 1, 0, {
            id: this._nextSectionId(sprite),
            name: (section.name || 'Group') + ' \u21D4',
            frames: [...(section.frames || [])],
            mirrored: true, mirrorOf: section.id,
            anchor: section.anchor || 'bottom-center',
          });
        }
        this._saveCustomSections(sprite);
        this._renderSections(panel, sprite, gff, scale);
      });
      hdr.appendChild(mirrorBtn);

      const delBtn = document.createElement('button');
      delBtn.className = 'spr-btn spr3-sec-btn spr3-sec-del';
      delBtn.textContent = '\uD83D\uDDD1';
      delBtn.title = 'Delete group';
      delBtn.addEventListener('click', () => {
        sprite.custom_sections = (sprite.custom_sections || [])
          .filter(s => s.id !== section.id && s.mirrorOf !== section.id);
        sprite.pattern_name = null;
        this._saveCustomSections(sprite);
        this._renderSections(panel, sprite, gff, scale);
      });
      hdr.appendChild(delBtn);
    }

    strip.appendChild(hdr);

    // Anchor picker (compact)
    if (section && frames.length > 1) {
      const aw = document.createElement('div');
      aw.className = 'spr3-anchor-row';
      aw.appendChild(this._buildAnchorPicker(section.anchor || 'bottom-center', newAnc => {
        section.anchor = newAnc;
        this._saveCustomSections(sprite);
      }));
      strip.appendChild(aw);
    }

    // Frame thumbnails
    const row = document.createElement('div');
    row.className = 'spr3-frame-row';

    frames.forEach(f => {
      const cell = document.createElement('div');
      cell.className = 'spr3-fcell';
      cell.dataset.frame = f;
      cell.title = 'Frame ' + f;
      const img = document.createElement('img');
      img.src = api.imageUrl(gff, 'BMP_', sprite.bmp_num, f, scale);
      img.loading = 'lazy';
      if (section?.mirrored) img.style.transform = 'scaleX(-1)';
      cell.appendChild(img);
      const lbl = document.createElement('span');
      lbl.className = 'spr3-fcell-num';
      lbl.textContent = f;
      cell.appendChild(lbl);

      if (this._selMode) {
        cell.classList.add('spr3-fcell-selectable');
        if (this._selFrames.has(f)) cell.classList.add('spr3-fcell-sel');
        cell.addEventListener('click', () => {
          this._selFrames.has(f) ? this._selFrames.delete(f) : this._selFrames.add(f);
          cell.classList.toggle('spr3-fcell-sel');
          this._updateSelBar(container);
        });
      } else {
        cell.addEventListener('click', () => {
          this._stopPreview();
          this._showPreviewForFrames(panel, sprite, [f], 'center', !!section?.mirrored);
        });
      }

      row.appendChild(cell);
    });

    strip.appendChild(row);
    container.appendChild(strip);
  }

  /* ---- Selection bar ---- */
  _addSelBar(container, sprite, panel, gff, scale) {
    let bar = container.querySelector('.spr3-sel-bar');
    if (bar) bar.remove();
    bar = document.createElement('div');
    bar.className = 'spr3-sel-bar';
    bar.innerHTML =
      '<span class="spr3-sel-count">' + this._selFrames.size + ' selected</span>' +
      '<input class="spr3-sel-name" type="text" placeholder="Group name"' +
      ' value="' + (this._editingSec ? htmlEsc(this._editingSec.name || '') : '') + '">' +
      '<button class="spr-btn spr3-sel-create">' + (this._editingSec ? 'Save' : 'Create') + '</button>' +
      '<button class="spr-btn spr3-sel-cancel">Cancel</button>';
    container.appendChild(bar);

    bar.querySelector('.spr3-sel-create').addEventListener('click', () => {
      if (this._selFrames.size === 0) return;
      const name = bar.querySelector('.spr3-sel-name').value || 'New group';
      if (!sprite.custom_sections) sprite.custom_sections = [];
      if (this._editingSec) {
        this._editingSec.name = name;
        this._editingSec.frames = [...this._selFrames].sort((a, b) => a - b);
        sprite.pattern_name = null;
      } else {
        sprite.custom_sections.push({
          id: this._nextSectionId(sprite), name,
          frames: [...this._selFrames].sort((a, b) => a - b),
          mirrored: false,
        });
        sprite.pattern_name = null;
      }
      this._selMode = false;
      this._selFrames.clear();
      this._editingSec = null;
      this._saveCustomSections(sprite);
      this._renderSections(panel, sprite, gff, scale);
    });

    bar.querySelector('.spr3-sel-cancel').addEventListener('click', () => {
      this._selMode = false;
      this._selFrames.clear();
      this._editingSec = null;
      this._renderSections(panel, sprite, gff, scale);
    });
  }

  _updateSelBar(container) {
    const c = container.querySelector('.spr3-sel-count');
    if (c) c.textContent = this._selFrames.size + ' selected';
  }

  /* ================================================================
     PANE 3 — Animation / Zoomed preview
     ================================================================ */
  async _showPreviewForAllFrames(panel, sprite) {
    const gff = sprite.gff_name || this._spriteGff;
    const info = await api.fetchImageInfo(gff, 'BMP_', sprite.bmp_num);
    const fc = info?.frame_count || sprite.frame_count || 1;
    const frames = Array.from({ length: fc }, (_, i) => i);
    this._showPreviewForFrames(panel, sprite, frames, 'bottom-center', false);
  }

  async _showPreviewForFrames(panel, sprite, frames, anchor, mirrored) {
    this._stopPreview();

    const emptyEl = panel.querySelector('.spr3-preview-empty');
    const activeEl = panel.querySelector('.spr3-preview-active');
    const canvas = panel.querySelector('.spr3-preview-canvas');
    const labelEl = panel.querySelector('.spr3-preview-label');
    const infoEl = panel.querySelector('.spr3-frame-info');
    const playBtn = panel.querySelector('.spr3-play-btn');

    emptyEl.classList.add('hidden');
    activeEl.classList.remove('hidden');

    const gff = sprite.gff_name || this._spriteGff;
    const scale = Math.max(prefs.spriteScale || 3, 3);

    const urls = frames.map(f => api.imageUrl(gff, 'BMP_', sprite.bmp_num, f, scale));
    const images = await Promise.all(urls.map(u => this._loadImage(u)));

    let maxW = 0, maxH = 0;
    images.forEach(img => {
      maxW = Math.max(maxW, img.naturalWidth || 1);
      maxH = Math.max(maxH, img.naturalHeight || 1);
    });

    const maxDim = 280;
    const displayScale = Math.min(maxDim / maxW, maxDim / maxH, 1);
    canvas.width = maxW;
    canvas.height = maxH;
    canvas.style.width = Math.round(maxW * displayScale) + 'px';
    canvas.style.height = Math.round(maxH * displayScale) + 'px';

    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingEnabled = false;

    this._previewFrameCount = frames.length;
    this._previewFrameIdx = 0;

    const drawFrame = (idx) => {
      ctx.clearRect(0, 0, maxW, maxH);
      const img = images[idx];
      if (!img || !img.naturalWidth) return;
      const [ox, oy] = this._anchorOffset(anchor, maxW, maxH, img.naturalWidth, img.naturalHeight);
      ctx.save();
      if (mirrored) { ctx.translate(maxW, 0); ctx.scale(-1, 1); }
      ctx.drawImage(img, ox, oy);
      ctx.restore();
    };

    drawFrame(0);
    infoEl.textContent = 'Frame ' + frames[0] + ' \u2022 1 / ' + frames.length;
    labelEl.textContent = this._previewSection?.name || (frames.length === 1 ? 'Frame ' + frames[0] : 'All frames');
    playBtn.textContent = '\u25B6';

    this._previewAnim = {
      timer: null, frames, images, pos: 0, anchor, mirrored,
      drawFrame, canvas, infoEl, playBtn, maxW, maxH,
    };
  }

  _stepPreview(dir) {
    if (!this._previewAnim) return;
    const pa = this._previewAnim;
    pa.pos = (pa.pos + dir + pa.frames.length) % pa.frames.length;
    pa.drawFrame(pa.pos);
    pa.infoEl.textContent = 'Frame ' + pa.frames[pa.pos] + ' \u2022 ' + (pa.pos + 1) + ' / ' + pa.frames.length;
  }

  _togglePreviewAnim(panel) {
    if (!this._previewAnim) return;
    const pa = this._previewAnim;
    if (pa.timer) {
      clearInterval(pa.timer);
      pa.timer = null;
      pa.playBtn.textContent = '\u25B6';
    } else {
      if (pa.frames.length < 2) return;
      pa.playBtn.textContent = '\u23F8';
      pa.timer = setInterval(() => {
        pa.pos = (pa.pos + 1) % pa.frames.length;
        pa.drawFrame(pa.pos);
        pa.infoEl.textContent = 'Frame ' + pa.frames[pa.pos] + ' \u2022 ' + (pa.pos + 1) + ' / ' + pa.frames.length;
      }, ANIM_INTERVAL);
    }
  }

  _stopPreview() {
    if (this._previewAnim?.timer) {
      clearInterval(this._previewAnim.timer);
    }
    this._previewAnim = null;
  }

  /* ================================================================
     LINKING
     ================================================================ */
  _promptLink(panel, sprite) {
    const pp = panel.querySelector('.spr3-pattern-panel');
    pp.classList.remove('hidden');
    pp.innerHTML =
      '<div class="spr3-link-panel">' +
      '<h3>\u26D3 Link Sprite Sheets</h3>' +
      '<p class="dim">Enter BMP numbers to link (comma-separated):</p>' +
      '<input class="spr3-link-input" type="text" placeholder="e.g. 50, 51, 52"' +
      ' value="' + (sprite.linked_bmps || []).join(', ') + '">' +
      '<div class="spr3-link-btns">' +
      '<button class="spr-btn spr3-link-save">Save</button>' +
      '<button class="spr-btn spr3-link-cancel">Cancel</button>' +
      '</div></div>';

    pp.querySelector('.spr3-link-cancel').addEventListener('click', () => pp.classList.add('hidden'));
    pp.querySelector('.spr3-link-save').addEventListener('click', () => {
      const val = pp.querySelector('.spr3-link-input').value;
      const nums = val.split(',').map(s => parseInt(s.trim())).filter(n => !isNaN(n) && n !== sprite.bmp_num);
      sprite.linked_bmps = nums;
      this._queueSave(sprite.bmp_num, { linked_bmps: nums });
      this._buildLinkedGroups();
      pp.classList.add('hidden');
      this._renderList(panel);
      this._showGroupPane(panel, sprite);
    });
  }

  /* ================================================================
     PATTERN MANAGEMENT
     ================================================================ */
  async _loadPatterns(fc) {
    if (!this._patternsCache) {
      try { this._patternsCache = await (await fetch('/api/sprite-patterns')).json(); }
      catch { this._patternsCache = []; }
    }
    return fc != null ? this._patternsCache.filter(p => p.frame_count === fc) : this._patternsCache;
  }

  async _openSavePatternPanel(panel, sprite) {
    const pp = panel.querySelector('.spr3-pattern-panel');
    pp.classList.remove('hidden');
    const secs = sprite.custom_sections || [];
    pp.innerHTML =
      '<div class="spr3-pat-save">' +
      '<h3>\uD83D\uDCBE Save Pattern</h3>' +
      '<p class="dim">' + secs.length + ' group(s), ' + sprite.frame_count + ' frames</p>' +
      '<input class="spr3-pat-name" type="text" placeholder="Pattern name"' +
      ' value="' + htmlEsc(sprite.pattern_name || '') + '">' +
      '<div class="spr3-pat-btns">' +
      '<button class="spr-btn spr3-pat-do-save">Save</button>' +
      '<button class="spr-btn spr3-pat-cancel">Cancel</button>' +
      '</div></div>';
    pp.querySelector('.spr3-pat-cancel').addEventListener('click', () => pp.classList.add('hidden'));
    pp.querySelector('.spr3-pat-do-save').addEventListener('click', async () => {
      const name = pp.querySelector('.spr3-pat-name').value.trim();
      if (!name) return;
      try {
        const r = await fetch('/api/sprite-patterns', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ action: 'save', pattern: {
            name, frame_count: sprite.frame_count,
            sections: secs.map(s => ({ id: s.id, name: s.name, frames: [...s.frames],
              mirrored: !!s.mirrored, mirrorOf: s.mirrorOf || null })),
          }}),
        });
        const res = await r.json();
        if (res.ok) {
          sprite.pattern_name = name;
          this._patternsCache = null;
          this._saveCustomSections(sprite);
          pp.classList.add('hidden');
        }
      } catch (err) { console.error('Pattern save error:', err); }
    });
  }

  async _openApplyPatternPanel(panel, sprite) {
    const pp = panel.querySelector('.spr3-pattern-panel');
    pp.classList.remove('hidden');
    pp.innerHTML = '<div class="spr3-loading"><div class="spinner"></div></div>';
    const patterns = await this._loadPatterns(sprite.frame_count);
    if (patterns.length === 0) {
      pp.innerHTML = '<p class="dim">No patterns for ' + sprite.frame_count + ' frames.</p>' +
        '<button class="spr-btn spr3-pat-cancel">Close</button>';
      pp.querySelector('.spr3-pat-cancel').addEventListener('click', () => pp.classList.add('hidden'));
      return;
    }
    let html = '<div class="spr3-pat-apply"><h3>\uD83D\uDCCB Apply Pattern</h3><div class="spr3-pat-list">';
    patterns.forEach((p, i) => {
      const mc = (p.sections || []).filter(s => s.mirrored).length;
      html += '<div class="spr3-pat-item" data-i="' + i + '">' +
        '<span>' + htmlEsc(p.name) + '</span> <span class="dim">' + (p.sections || []).length + 'g' + (mc ? ', ' + mc + 'm' : '') + '</span>' +
        '<button class="spr-btn spr3-pat-apply-btn">Apply</button></div>';
    });
    html += '</div><button class="spr-btn spr3-pat-cancel">Cancel</button></div>';
    pp.innerHTML = html;
    pp.querySelector('.spr3-pat-cancel').addEventListener('click', () => pp.classList.add('hidden'));
    pp.querySelectorAll('.spr3-pat-apply-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        const pat = patterns[parseInt(btn.closest('.spr3-pat-item').dataset.i)];
        if (!pat) return;
        sprite.custom_sections = (pat.sections || []).map((s, i) => ({
          id: String(i + 1), name: s.name, frames: [...s.frames],
          mirrored: !!s.mirrored, mirrorOf: s.mirrorOf || null,
          anchor: s.anchor || 'bottom-center',
        }));
        sprite.pattern_name = pat.name;
        this._saveCustomSections(sprite);
        pp.classList.add('hidden');
        this._showGroupPane(panel, sprite);
      });
    });
  }

  /* ================================================================
     HELPERS
     ================================================================ */
  _loadImage(url) {
    if (this._imgCache.has(url)) return this._imgCache.get(url);
    const p = new Promise(resolve => {
      const img = new Image();
      img.crossOrigin = 'anonymous';
      img.onload = () => resolve(img);
      img.onerror = () => resolve(img);
      img.src = url;
    });
    this._imgCache.set(url, p);
    return p;
  }

  _anchorOffset(anchor, cw, ch, iw, ih) {
    let x = (cw - iw) / 2, y = (ch - ih) / 2;
    if (anchor.includes('left'))   x = 0;
    if (anchor.includes('right'))  x = cw - iw;
    if (anchor.includes('top'))    y = 0;
    if (anchor.includes('bottom')) y = ch - ih;
    return [x, y];
  }

  _buildAnchorPicker(current, onChange) {
    const wrap = document.createElement('div');
    wrap.className = 'spr3-anchor-grid';
    ANCHOR_GRID.forEach(row => row.forEach(pos => {
      const btn = document.createElement('button');
      btn.className = 'spr3-anchor-cell' + (pos === current ? ' active' : '');
      btn.textContent = ANCHOR_ARROWS[pos] || '\u25CF';
      btn.title = pos;
      btn.addEventListener('click', () => {
        wrap.querySelectorAll('.spr3-anchor-cell').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        onChange(pos);
      });
      wrap.appendChild(btn);
    }));
    return wrap;
  }

  _nextSectionId(sprite) {
    return String((sprite.custom_sections || []).reduce((m, s) => Math.max(m, parseInt(s.id) || 0), 0) + 1);
  }

  _saveCustomSections(sprite) {
    this._queueSave(sprite.bmp_num, {
      custom_sections: (sprite.custom_sections || []).map(s => ({ ...s })),
      pattern_name: sprite.pattern_name || null,
      plain_anchor: sprite.plain_anchor || null,
    });
  }

  _queueSave(bmpNum, patch) {
    if (!this._savePending[bmpNum]) this._savePending[bmpNum] = {};
    Object.assign(this._savePending[bmpNum], patch);
    const st = document.querySelector('.spr3-save-status');
    if (st) st.textContent = 'Unsaved\u2026';
    clearTimeout(this._saveTimer);
    this._saveTimer = setTimeout(() => this._flushSave(), 800);
  }

  async _flushSave() {
    const pending = this._savePending;
    this._savePending = {};
    const sprites = {};
    for (const [k, v] of Object.entries(pending)) sprites[k] = v;
    if (!Object.keys(sprites).length) return;
    const st = document.querySelector('.spr3-save-status');
    if (st) st.textContent = 'Saving\u2026';
    try {
      const r = await fetch('/api/sprite-metadata?game=' + state.game, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sprites }),
      });
      const res = await r.json();
      if (res.ok) { if (st) st.textContent = 'Saved'; if (res.meta) this._syncCatalog(res.meta); }
      else { if (st) st.textContent = 'Save failed'; }
    } catch { if (st) st.textContent = 'Save failed'; Object.assign(this._savePending, pending); }
  }

  _syncCatalog(meta) {
    if (!this._catalog) return;
    for (const entry of this._catalog) {
      const m = meta[String(entry.bmp_num)];
      if (!m) continue;
      for (const k of ['type', 'name', 'custom_sections', 'pattern_name', 'notes', 'linked_bmps', 'plain_anchor'])
        if (m[k] !== undefined) entry[k] = m[k];
    }
  }
}
