/* ================================================================
   Dark Sun GFF Viewer — view-sprites.js
   Full sprite catalog viewer with metadata editing (DS1)
   ================================================================ */

'use strict';

// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------
let spritesBuilt    = false;
let spriteCatalog   = null;   // full list from /api/bmp-catalog
let spritesByBmp    = {};     // bmp_num -> catalog entry (fast lookup)
let spriteFilter    = { type: 'NPC', q: '', animated: 'all' };
let spriteFiltered  = [];
let selectedBmpNum  = -1;

let animTimers      = {};     // key -> {timer, previewEl, btn}
let spriteScale     = 3;

let savePending     = {};     // str(bmp_num) -> patch object
let saveTimer       = null;
let saveStatus      = 'saved'; // 'saved' | 'saving' | 'unsaved'

let linkObserver    = null;   // IntersectionObserver for list thumb lazy load
let _itemsCache     = null;   // items data from /api/items (lazy loaded)
let _pendingSelectBmp = null; // set externally to auto-select a BMP after catalog loads
let spriteGffName   = 'SEGOBJEX.GFF'; // actual GFF file for this game's sprites

const SPRITE_TYPES  = ['PC', 'NPC', 'Monster', 'Scenery', 'Effect', 'Spell', 'Item', 'Unknown'];
const ALL_TABS      = ['PC', 'NPC', 'Monster', 'Scenery', 'Effect', 'Spell', 'Item', 'Unknown', 'All'];

// ---------------------------------------------------------------------------
// Entry / teardown
// ---------------------------------------------------------------------------

function renderSpritesView() {
  const view = document.getElementById('npc-view');
  if (!view) return;
  view.classList.remove('hidden');

  if (!spritesBuilt) {
    _buildSpritesDOM(view);
    spritesBuilt = true;
  }

  if (spriteCatalog === null) {
    _loadCatalog();
  } else {
    _applyFilter();
  }
}

function teardownSpritesView() {
  _stopAllAnims();
  if (linkObserver) { linkObserver.disconnect(); linkObserver = null; }
  spriteCatalog  = null;
  spritesByBmp   = {};
  spriteFiltered = [];
  selectedBmpNum = -1;
  savePending    = {};
  saveStatus     = 'saved';
  spriteGffName  = 'SEGOBJEX.GFF';
  // Keep spritesBuilt — DOM survives; data re-fetched per game change
}

// ---------------------------------------------------------------------------
// DOM skeleton
// ---------------------------------------------------------------------------

function _buildSpritesDOM(view) {
  view.innerHTML = `
<div id="spr-layout">
  <div id="spr-sidebar">
    <div id="spr-toolbar">
      <input id="spr-search" type="search" placeholder="Filter by name or BMP#…"
             autocomplete="off" spellcheck="false">
      <div id="spr-tabs" role="tablist"></div>
      <div id="spr-anim-filter" class="spr-anim-filter-row" role="group" aria-label="Animation filter">
        <button class="spr-anim-btn active" data-anim="all">All</button>
        <button class="spr-anim-btn" data-anim="animated">&#9654; Animated</button>
        <button class="spr-anim-btn" data-anim="static">&#9646; Static</button>
      </div>
    </div>
    <div id="spr-list-wrap">
      <div id="spr-loading" class="spr-status-msg hidden">
        <div class="spinner"></div><span>Loading catalog…</span>
      </div>
      <div id="spr-load-error" class="spr-status-msg spr-error-msg hidden"></div>
      <ul id="spr-list"></ul>
    </div>
  </div>

  <div id="spr-detail-col">
    <div id="spr-save-bar">
      <span id="spr-save-status" class="spr-save-status"></span>
      <button id="spr-save-btn" class="spr-btn spr-btn-primary" disabled>Save now</button>
    </div>
    <div id="spr-detail-empty" class="spr-detail-placeholder">
      <span class="spr-ph-icon">&#9812;</span>
      <p>Select a sprite from the list</p>
    </div>
    <div id="spr-detail-content" class="hidden"></div>
  </div>
</div>`;

  // Search
  view.querySelector('#spr-search').addEventListener('input', e => {
    spriteFilter.q = e.target.value.toLowerCase().trim();
    _applyFilter();
  });

  // Animated filter buttons
  view.querySelector('#spr-anim-filter').addEventListener('click', ev => {
    const btn = ev.target.closest('.spr-anim-btn');
    if (!btn) return;
    spriteFilter.animated = btn.dataset.anim;
    view.querySelectorAll('#spr-anim-filter .spr-anim-btn')
      .forEach(b => b.classList.toggle('active', b === btn));
    _applyFilter();
  });

  // Tab buttons (built after data loads, populated in _updateTabs)
  // Save button
  view.querySelector('#spr-save-btn').addEventListener('click', _flushSave);

  // Event delegation for list clicks
  view.querySelector('#spr-list').addEventListener('click', e => {
    const li = e.target.closest('.spr-li');
    if (li) _selectEntry(Number(li.dataset.bmp));
  });
}

// ---------------------------------------------------------------------------
// Data loading
// ---------------------------------------------------------------------------

async function _loadCatalog() {
  _showLoadMsg(true, false, '');
  try {
    const r = await fetch(`/api/bmp-catalog?game=${state.game}`);
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    spriteCatalog = await r.json();
    spritesByBmp  = {};
    spriteCatalog.forEach(e => { spritesByBmp[e.bmp_num] = e; });
    spriteGffName = spriteCatalog[0]?.gff_name || 'SEGOBJEX.GFF';
  } catch (err) {
    _showLoadMsg(false, true, `Failed to load catalog: ${err.message}`);
    return;
  }
  _showLoadMsg(false, false, '');
  _buildTabs();
  _applyFilter();

  // Auto-select a BMP if navigation came from another view
  if (_pendingSelectBmp !== null) {
    const tgt = _pendingSelectBmp;
    _pendingSelectBmp = null;
    _selectEntry(tgt);
  }
}

function _showLoadMsg(loading, error, msg) {
  const loadEl = document.getElementById('spr-loading');
  const errEl  = document.getElementById('spr-load-error');
  if (loadEl) loadEl.classList.toggle('hidden', !loading);
  if (errEl) {
    errEl.classList.toggle('hidden', !error);
    if (error) errEl.textContent = msg;
  }
}

// ---------------------------------------------------------------------------
// Tabs
// ---------------------------------------------------------------------------

function _buildTabs() {
  const container = document.getElementById('spr-tabs');
  if (!container || !spriteCatalog) return;

  const counts = {};
  ALL_TABS.forEach(t => { counts[t] = 0; });
  spriteCatalog.forEach(e => {
    counts[e.type] = (counts[e.type] || 0) + 1;
    counts['All']++;
  });

  container.innerHTML = '';
  ALL_TABS.forEach(tab => {
    const btn = document.createElement('button');
    btn.className = 'spr-tab' + (tab === spriteFilter.type ? ' active' : '');
    btn.dataset.tab = tab;
    btn.innerHTML = `${_esc(tab)}<span class="spr-tab-count">${counts[tab] || 0}</span>`;
    btn.addEventListener('click', () => {
      spriteFilter.type = tab;
      container.querySelectorAll('.spr-tab').forEach(b =>
        b.classList.toggle('active', b === btn)
      );
      _applyFilter();
    });
    container.appendChild(btn);
  });
}

function _updateTabCounts() {
  if (!spriteCatalog) return;
  const counts = {};
  ALL_TABS.forEach(t => { counts[t] = 0; });
  spriteCatalog.forEach(e => {
    counts[e.type] = (counts[e.type] || 0) + 1;
    counts['All']++;
  });
  document.querySelectorAll('#spr-tabs .spr-tab').forEach(btn => {
    const n = btn.dataset.tab;
    const badge = btn.querySelector('.spr-tab-count');
    if (badge) badge.textContent = counts[n] || 0;
  });
}

// ---------------------------------------------------------------------------
// Filter + list rendering
// ---------------------------------------------------------------------------

function _applyFilter() {
  if (!spriteCatalog) return;
  const { type, q, animated } = spriteFilter;

  spriteFiltered = spriteCatalog.filter(e => {
    if (type !== 'All' && e.type !== type) return false;
    if (animated === 'animated' && e.frame_count <= 1) return false;
    if (animated === 'static'   && e.frame_count > 1)  return false;
    if (!q) return true;
    if (String(e.bmp_num).includes(q)) return true;
    const name = (e.name || '').toLowerCase();
    if (name.includes(q)) return true;
    const rdff = (e.rdff_names || []).join(' ').toLowerCase();
    return rdff.includes(q);
  });

  _buildList();

  // Re-select or auto-select first
  if (selectedBmpNum >= 0 && spritesByBmp[selectedBmpNum]) {
    const stillVisible = spriteFiltered.some(e => e.bmp_num === selectedBmpNum);
    if (stillVisible) {
      _highlightListItem(selectedBmpNum);
      return;
    }
  }
  if (spriteFiltered.length > 0) {
    _selectEntry(spriteFiltered[0].bmp_num);
  } else {
    _showEmptyDetail(true);
  }
}

function _buildList() {
  const ul = document.getElementById('spr-list');
  if (!ul) return;

  // Disconnect old observer
  if (linkObserver) { linkObserver.disconnect(); }

  if (spriteFiltered.length === 0) {
    ul.innerHTML = `<li class="spr-li-empty">${_esc(
      spriteFilter.q ? `No results for "${spriteFilter.q}"` :
      `No ${spriteFilter.type === 'All' ? '' : spriteFilter.type + ' '}sprites found.`
    )}</li>`;
    return;
  }

  // Build all items at once with deferred image loading
  const frag = document.createDocumentFragment();
  spriteFiltered.forEach(e => {
    const li = _buildListItem(e);
    frag.appendChild(li);
  });
  ul.innerHTML = '';
  ul.appendChild(frag);

  // IntersectionObserver for lazy thumbnail loading
  linkObserver = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      if (!entry.isIntersecting) return;
      const img = entry.target.querySelector('.spr-li-img[data-src]');
      if (img) {
        img.src = img.dataset.src;
        delete img.dataset.src;
        linkObserver.unobserve(entry.target);
      }
    });
  }, { rootMargin: '100px' });

  ul.querySelectorAll('.spr-li').forEach(li => linkObserver.observe(li));
}

function _buildListItem(e) {
  const li = document.createElement('li');
  li.className = 'spr-li' + (e.bmp_num === selectedBmpNum ? ' active' : '');
  li.dataset.bmp = e.bmp_num;

  const thumbUrl = `/api/gff/${spriteGffName}/image/BMP_/${e.bmp_num}?frame=0&game=${state.game}&scale=2`;

  li.innerHTML = `
    <div class="spr-li-thumb">
      <img class="spr-li-img" data-src="${thumbUrl}" alt="" loading="lazy"
           onerror="this.style.display='none'">
    </div>
    <div class="spr-li-info">
      <div class="spr-li-name">${_esc(_displayName(e))}</div>
      <div class="spr-li-meta">
        <span class="spr-type-badge spr-type-${e.type.toLowerCase()}">${_esc(e.type)}</span>
        ${e.sheet_type ? `<span class="spr-sheet-badge">${_esc(_sheetLabel(e.sheet_type))}</span>` : ''}
        ${e.linked_bmps && e.linked_bmps.length ? `<span class="spr-link-badge">&#128279; ${e.linked_bmps.length}</span>` : ''}
        ${e.frame_count > 1 ? `<span class="spr-li-frames-badge" title="${e.frame_count} frames">&#9654;&thinsp;${e.frame_count}</span>` : ''}
        <span class="spr-li-num">#${e.bmp_num}</span>
      </div>
    </div>`;

  return li;
}

function _updateListItem(bmpNum) {
  const li = document.querySelector(`#spr-list .spr-li[data-bmp="${bmpNum}"]`);
  if (!li) return;
  const e = spritesByBmp[bmpNum];
  if (!e) return;
  const nameEl  = li.querySelector('.spr-li-name');
  const metaEl  = li.querySelector('.spr-li-meta');
  if (nameEl) nameEl.textContent = _displayName(e);
  if (metaEl) metaEl.innerHTML = `
    <span class="spr-type-badge spr-type-${e.type.toLowerCase()}">${_esc(e.type)}</span>
    ${e.sheet_type ? `<span class="spr-sheet-badge">${_esc(_sheetLabel(e.sheet_type))}</span>` : ''}
    ${e.linked_bmps && e.linked_bmps.length ? `<span class="spr-link-badge">&#128279; ${e.linked_bmps.length}</span>` : ''}
    ${e.frame_count > 1 ? `<span class="spr-li-frames-badge" title="${e.frame_count} frames">&#9654;&thinsp;${e.frame_count}</span>` : ''}
    <span class="spr-li-num">#${e.bmp_num}</span>`;
}

// ---------------------------------------------------------------------------
// Detail panel
// ---------------------------------------------------------------------------

function _selectEntry(bmpNum) {
  selectedBmpNum = bmpNum;
  _highlightListItem(bmpNum);
  const e = spritesByBmp[bmpNum];
  if (!e) { _showEmptyDetail(true); return; }
  _showEmptyDetail(false);
  _renderDetail(e);
}

function _highlightListItem(bmpNum) {
  document.querySelectorAll('#spr-list .spr-li').forEach(li =>
    li.classList.toggle('active', Number(li.dataset.bmp) === bmpNum)
  );
  // Scroll into view if needed
  const active = document.querySelector(`#spr-list .spr-li.active`);
  if (active) active.scrollIntoView({ block: 'nearest' });
}

function _showEmptyDetail(on) {
  document.getElementById('spr-detail-empty')?.classList.toggle('hidden', !on);
  document.getElementById('spr-detail-content')?.classList.toggle('hidden', on);
}

function _renderDetail(e) {
  _stopAllAnims();
  const content = document.getElementById('spr-detail-content');
  if (!content) return;

  const rdffHint = e.rdff_names && e.rdff_names.length
    ? `<div class="spr-rdff-hint dim">RDFF names: ${_esc(e.rdff_names.join(', '))}</div>`
    : '';

  const linkedHtml = _buildLinkedHtml(e);

  content.innerHTML = `
<div class="spr-detail-header">
  <div class="spr-detail-bmp-num">BMP #${e.bmp_num} &nbsp;
    <span class="spr-auto-type dim">(auto: ${_esc(e.auto_type)})</span>
  </div>
  <div class="spr-scale-ctl">
    <label class="dim">Scale
      <select id="spr-scale-sel">
        <option value="2">2×</option>
        <option value="3" selected>3×</option>
        <option value="4">4×</option>
        <option value="6">6×</option>
      </select>
    </label>
  </div>
</div>

<div class="spr-edit-grid">
  <label class="spr-field-label">Type</label>
  <select id="spr-type-sel" class="spr-input spr-type-sel">
    ${SPRITE_TYPES.map(t =>
      `<option value="${t}" ${e.type === t ? 'selected' : ''}>${t}</option>`
    ).join('')}
  </select>

  <label class="spr-field-label">Name</label>
  <div class="spr-name-wrap">
    <input id="spr-name-inp" type="text" class="spr-input"
           value="${_esc(e.name)}"
           placeholder="${_esc((e.rdff_names||[]).join(', ') || 'No RDFF name')}">
    ${rdffHint}
  </div>

  <label class="spr-field-label spr-linked-lbl${e.type === 'Item' ? ' hidden' : ''}">Linked sheets</label>
  <div id="spr-links-wrap" class="spr-links-wrap${e.type === 'Item' ? ' hidden' : ''}">
    ${linkedHtml}
  </div>

  <label class="spr-field-label">Notes</label>
  <textarea id="spr-notes-ta" class="spr-input spr-notes-ta"
            rows="2">${_esc(e.notes)}</textarea>

  <label class="spr-field-label spr-item-icon-row${e.type === 'Item' ? '' : ' hidden'}" id="spr-item-icon-lbl">Linked item</label>
  <div id="spr-item-icon-wrap" class="spr-item-icon-wrap${e.type === 'Item' ? '' : ' hidden'}">
    <div id="spr-item-picker-inner"><span class="dim" style="font-size:11px">Loading items…</span></div>
  </div>
</div>

<div id="spr-frames-area"></div>`;

  // Set correct scale
  const scaleSel = content.querySelector('#spr-scale-sel');
  scaleSel.value = String(spriteScale);
  scaleSel.addEventListener('change', ev => {
    spriteScale = Number(ev.target.value);
    _renderFrames(e);
  });

  // Type change
  content.querySelector('#spr-type-sel').addEventListener('change', ev => {
    const oldType = e.type;
    e.type = ev.target.value;
    _queueSave(e.bmp_num, { type: e.type });
    _updateListItem(e.bmp_num);
    // Rebuild tabs counts
    _updateTabCounts();
    // Show/hide item picker row
    const itemIconLbl  = content.querySelector('#spr-item-icon-lbl');
    const itemIconWrap = content.querySelector('#spr-item-icon-wrap');
    if (itemIconLbl)  itemIconLbl.classList.toggle('hidden', e.type !== 'Item');
    if (itemIconWrap) itemIconWrap.classList.toggle('hidden', e.type !== 'Item');
    // Hide linked sheets for Item-type sprites
    const linkedLbl  = content.querySelector('.spr-linked-lbl');
    const linkedWrap = content.querySelector('#spr-links-wrap');
    if (linkedLbl)  linkedLbl.classList.toggle('hidden', e.type === 'Item');
    if (linkedWrap) linkedWrap.classList.toggle('hidden', e.type === 'Item');
    if (e.type === 'Item') _setupItemPicker(e);
    // If no longer matches filter, keep showing it anyway (don't navigate away)
  });

  // Name change (debounced)
  let nameTimer = null;
  content.querySelector('#spr-name-inp').addEventListener('input', ev => {
    clearTimeout(nameTimer);
    nameTimer = setTimeout(() => {
      e.name = ev.target.value.trim();
      _queueSave(e.bmp_num, { name: e.name });
      _updateListItem(e.bmp_num);
    }, 400);
  });

  // Notes change
  let notesTimer = null;
  content.querySelector('#spr-notes-ta').addEventListener('input', ev => {
    clearTimeout(notesTimer);
    notesTimer = setTimeout(() => {
      e.notes = ev.target.value;
      _queueSave(e.bmp_num, { notes: e.notes });
    }, 600);
  });

  // Item picker (async, only visible when type === 'Item')
  if (e.type === 'Item') _setupItemPicker(e);

  _renderFrames(e);
  _setupLinkEditor(e);
}

// ---------------------------------------------------------------------------
// Item picker (for Item-type sprites)
// ---------------------------------------------------------------------------

async function _ensureItems() {
  if (_itemsCache) return _itemsCache;
  const r = await fetch(`/api/items?game=${state.game}`);
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  _itemsCache = await r.json();
  return _itemsCache;
}

async function _setupItemPicker(e) {
  const wrap = document.getElementById('spr-item-picker-inner');
  if (!wrap) return;
  wrap.innerHTML = '<span class="dim" style="font-size:11px">Loading items\u2026</span>';
  let items;
  try {
    items = await _ensureItems();
  } catch (err) {
    const w2 = document.getElementById('spr-item-picker-inner');
    if (w2) w2.innerHTML = '<span class="spr-error-msg" style="font-size:11px">Failed to load items</span>';
    return;
  }
  const stillWrap = document.getElementById('spr-item-picker-inner');
  if (!stillWrap) return;

  const sel = document.createElement('select');
  sel.id        = 'spr-item-sel';
  sel.className = 'spr-input spr-item-sel';
  const noneOpt = document.createElement('option');
  noneOpt.value       = '';
  noneOpt.textContent = '\u2014 None \u2014';
  sel.appendChild(noneOpt);
  items.forEach(item => {
    const opt       = document.createElement('option');
    opt.value       = String(item.index);
    opt.textContent = item.name ? `#${item.index} ${item.name}` : `Item #${item.index}`;
    if (e.item_index === item.index) opt.selected = true;
    sel.appendChild(opt);
  });

  const pv = document.createElement('div');
  pv.className = 'spr-item-icon-pv';
  const _updatePv = idx => {
    if (idx == null) { pv.classList.add('hidden'); pv.innerHTML = ''; return; }
    const item = items.find(x => x.index === idx);
    if (!item) { pv.classList.add('hidden'); return; }
    // Show the sprite itself (current BMP) alongside the item name — no PORT portrait
    pv.innerHTML = `
      <img src="${_frameUrl(e.bmp_num, 0, 3)}" alt=""
           style="image-rendering:pixelated;max-height:48px;max-width:48px">
      <span class="dim" style="font-size:11px">${_esc(item.name || `Item #${item.index}`)}</span>`;
    pv.classList.remove('hidden');
  };
  _updatePv(e.item_index);

  sel.addEventListener('change', () => {
    const val = sel.value === '' ? null : Number(sel.value);
    e.item_index = val;
    _queueSave(e.bmp_num, { item_index: val });
    _updatePv(val);
  });

  stillWrap.innerHTML = '';
  stillWrap.appendChild(sel);
  stillWrap.appendChild(pv);
}

/** Navigate to a specific BMP in the sprite viewer (callable from view-data.js). */
function jumpToSprite(bmpNum) {
  _pendingSelectBmp = bmpNum;
  state.mode = 'npc';
  renderContent();
  if (spriteCatalog !== null && spritesByBmp[bmpNum]) {
    _pendingSelectBmp = null;
    _selectEntry(bmpNum);
  }
}

// ---------------------------------------------------------------------------
// Frame sections viewer
// ---------------------------------------------------------------------------

function _renderFrames(e) {
  const area = document.getElementById('spr-frames-area');
  if (!area) return;
  area.innerHTML = '';

  if (!e.sections || e.sections.length === 0) {
    // Non-section sprite: show all frames in a strip
    const key = `${e.bmp_num}_plain`;
    const strip = document.createElement('div');
    strip.className = 'spr-plain-strip';

    const titleDiv = document.createElement('div');
    titleDiv.className = 'spr-section-title';
    titleDiv.textContent = `Frames (${e.frame_count})`;
    if (e.frame_count > 1) {
      const frames   = Array.from({ length: e.frame_count }, (_, i) => i);
      const playBtn  = document.createElement('button');
      playBtn.className = 'spr-play-btn';
      playBtn.id        = `spr-play-${key}`;
      playBtn.innerHTML = '&#9654;';
      playBtn.title     = 'Animate';
      playBtn.addEventListener('click', () => _toggleAnim(key, e.bmp_num, frames, playBtn));
      titleDiv.appendChild(playBtn);
    }
    strip.appendChild(titleDiv);

    const row = document.createElement('div');
    row.className = 'spr-frame-row';
    row.id        = `spr-row-${key}`;
    for (let i = 0; i < e.frame_count; i++) {
      row.appendChild(_makeFrameCell(e.bmp_num, i, key));
    }
    strip.appendChild(row);

    if (e.frame_count > 1) {
      const preview = document.createElement('div');
      preview.className = 'spr-anim-wrap hidden';
      preview.id        = `spr-anim-${key}`;
      const pImg = document.createElement('img');
      pImg.className = 'spr-anim-img';
      pImg.id        = `spr-anim-img-${key}`;
      pImg.src       = _frameUrl(e.bmp_num, 0);
      preview.appendChild(pImg);
      strip.appendChild(preview);
    }

    area.appendChild(strip);
    return;
  }

  // Build sheet label
  const sheetHead = document.createElement('div');
  sheetHead.className = 'spr-sheet-heading';
  sheetHead.textContent = _sheetLabel(e.sheet_type || '') + ` (${e.frame_count} frames)`;
  area.appendChild(sheetHead);

  e.sections.forEach((sec, secIdx) => {
    const secKey = `${e.bmp_num}_${secIdx}`;
    const secEl  = document.createElement('div');
    secEl.className = 'spr-sec';

    const hdr = document.createElement('div');
    hdr.className = 'spr-sec-hdr';
    hdr.innerHTML = `
      <span class="spr-sec-name">${_esc(sec.name)}</span>
      <span class="spr-sec-desc dim">${_esc(sec.desc || '')}</span>`;

    if (sec.frames.length > 1) {
      const playBtn = document.createElement('button');
      playBtn.className = 'spr-play-btn';
      playBtn.id        = `spr-play-${secKey}`;
      playBtn.innerHTML = '&#9654;';
      playBtn.title     = 'Animate';
      playBtn.addEventListener('click', () =>
        _toggleAnim(secKey, e.bmp_num, sec.frames, playBtn)
      );
      hdr.appendChild(playBtn);
    }
    secEl.appendChild(hdr);

    const row = document.createElement('div');
    row.className = 'spr-frame-row';
    row.id        = `spr-row-${secKey}`;
    sec.frames.forEach(fi => row.appendChild(_makeFrameCell(e.bmp_num, fi, secKey)));
    secEl.appendChild(row);

    if (sec.frames.length > 1) {
      const preview = document.createElement('div');
      preview.className = 'spr-anim-wrap hidden';
      preview.id        = `spr-anim-${secKey}`;
      const pImg        = document.createElement('img');
      pImg.className    = 'spr-anim-img';
      pImg.id           = `spr-anim-img-${secKey}`;
      pImg.src          = _frameUrl(e.bmp_num, sec.frames[0]);
      preview.appendChild(pImg);
      secEl.appendChild(preview);
    }

    area.appendChild(secEl);
  });

  // Items don't have linked walk/attack sheets — skip entirely
  if (e.type === 'Item') return;

  // Also show linked-sheet frames
  (e.linked_bmps || []).forEach(linkedNum => {
    const le = spritesByBmp[linkedNum];
    if (!le) return;
    const linkHead = document.createElement('div');
    linkHead.className = 'spr-link-sheet-head';
    linkHead.innerHTML = `<span>Linked sheet: BMP #${linkedNum}</span>
      <span class="dim">${_esc(_displayName(le))} — ${_esc(_sheetLabel(le.sheet_type || ''))}</span>`;
    area.appendChild(linkHead);

    if (le.sections && le.sections.length) {
      le.sections.forEach((sec, secIdx) => {
        const secKey = `${linkedNum}_ls_${secIdx}`;
        const secEl  = document.createElement('div');
        secEl.className = 'spr-sec spr-sec-linked';
        const hdr = document.createElement('div');
        hdr.className = 'spr-sec-hdr';
        hdr.innerHTML = `<span class="spr-sec-name">${_esc(sec.name)}</span>
                          <span class="spr-sec-desc dim">${_esc(sec.desc || '')}</span>`;
        if (sec.frames.length > 1) {
          const playBtn = document.createElement('button');
          playBtn.className = 'spr-play-btn';
          playBtn.innerHTML = '&#9654;';
          playBtn.addEventListener('click', () =>
            _toggleAnim(secKey, linkedNum, sec.frames, playBtn)
          );
          hdr.appendChild(playBtn);
        }
        secEl.appendChild(hdr);
        const row = document.createElement('div');
        row.className = 'spr-frame-row';
        row.id        = `spr-row-${secKey}`;
        sec.frames.forEach(fi => row.appendChild(_makeFrameCell(linkedNum, fi, secKey)));
        secEl.appendChild(row);

        if (sec.frames.length > 1) {
          const preview = document.createElement('div');
          preview.className = 'spr-anim-wrap hidden';
          preview.id        = `spr-anim-${secKey}`;
          const pImg        = document.createElement('img');
          pImg.className    = 'spr-anim-img';
          pImg.id           = `spr-anim-img-${secKey}`;
          pImg.src          = _frameUrl(linkedNum, sec.frames[0]);
          preview.appendChild(pImg);
          secEl.appendChild(preview);
        }
        area.appendChild(secEl);
      });
    } else {
      const row = document.createElement('div');
      row.className = 'spr-frame-row';
      for (let i = 0; i < le.frame_count; i++) row.appendChild(_makeFrameCell(linkedNum, i));
      area.appendChild(row);
    }
  });
}

function _makeFrameCell(bmpNum, frameIdx, secKey) {
  const wrap = document.createElement('div');
  wrap.className = 'spr-fcell';
  wrap.dataset.secKey = secKey || '';
  wrap.dataset.pos    = frameIdx;

  const img = document.createElement('img');
  img.src   = _frameUrl(bmpNum, frameIdx);
  img.alt   = `F${frameIdx}`;
  img.className = 'spr-fimg';
  img.onerror = () => { wrap.classList.add('spr-fcell-err'); img.style.display = 'none'; };

  const lbl = document.createElement('div');
  lbl.className = 'spr-flbl dim';
  lbl.textContent = frameIdx;

  wrap.appendChild(img);
  wrap.appendChild(lbl);
  return wrap;
}

// ---------------------------------------------------------------------------
// Link editor
// ---------------------------------------------------------------------------

function _buildLinkedHtml(e) {
  const chips = (e.linked_bmps || []).map(n => {
    const le = spritesByBmp[n];
    const nm = le ? _displayName(le) : `#${n}`;
    return `<span class="spr-link-chip" data-link="${n}">
      <button class="spr-link-chip-goto" data-link="${n}" title="Go to BMP #${n}">&#128279;</button>
      <span>#${n} ${_esc(nm)}</span>
      <button class="spr-link-chip-rm" data-link="${n}" title="Remove link">&#10005;</button>
    </span>`;
  }).join('');

  return `
    <div id="spr-link-chips">${chips}</div>
    <div class="spr-link-add-row">
      <input id="spr-link-inp" type="number" class="spr-input spr-link-inp"
             placeholder="BMP number to link…" min="1">
      <button id="spr-link-add-btn" class="spr-btn">Add link</button>
    </div>
    <div id="spr-link-preview" class="spr-link-preview hidden"></div>`;
}

function _setupLinkEditor(e) {
  const wrap = document.getElementById('spr-links-wrap');
  if (!wrap) return;

  // Remove link chip clicks
  wrap.addEventListener('click', ev => {
    const rmBtn   = ev.target.closest('.spr-link-chip-rm');
    const gotoBtn = ev.target.closest('.spr-link-chip-goto');

    if (rmBtn) {
      const n = Number(rmBtn.dataset.link);
      e.linked_bmps = (e.linked_bmps || []).filter(x => x !== n);
      _queueSave(e.bmp_num, { linked_bmps: [...e.linked_bmps] });
      _updateListItem(e.bmp_num);
      // Refresh link chips in the UI
      const chipsEl = document.getElementById('spr-link-chips');
      if (chipsEl) {
        chipsEl.innerHTML = (e.linked_bmps).map(n2 => {
          const le = spritesByBmp[n2];
          const nm = le ? _displayName(le) : `#${n2}`;
          return `<span class="spr-link-chip" data-link="${n2}">
            <button class="spr-link-chip-goto" data-link="${n2}" title="Go to BMP #${n2}">&#128279;</button>
            <span>#${n2} ${_esc(nm)}</span>
            <button class="spr-link-chip-rm" data-link="${n2}" title="Remove link">&#10005;</button>
          </span>`;
        }).join('');
      }
      // Re-render frames to remove linked sheet view
      _renderFrames(e);
    }

    if (gotoBtn) {
      const n = Number(gotoBtn.dataset.link);
      // Switch filter to All if needed and select
      if (!spritesByBmp[n]) return;
      const targetType = spritesByBmp[n].type;
      if (spriteFilter.type !== 'All' && spriteFilter.type !== targetType) {
        spriteFilter.type = 'All';
        document.querySelectorAll('#spr-tabs .spr-tab').forEach(b =>
          b.classList.toggle('active', b.dataset.tab === 'All')
        );
        _applyFilter();
      }
      _selectEntry(n);
    }
  });

  // Link input preview
  const inp = document.getElementById('spr-link-inp');
  const prevEl = document.getElementById('spr-link-preview');
  inp.addEventListener('input', () => {
    const n = Number(inp.value);
    if (!n || !spritesByBmp[n]) {
      if (prevEl) prevEl.classList.add('hidden');
      return;
    }
    const le = spritesByBmp[n];
    if (prevEl) {
      prevEl.classList.remove('hidden');
      prevEl.innerHTML = `
        <img src="${_frameUrl(n, 0, 1)}" alt="" onerror="this.style.display='none'" class="spr-link-prev-img">
        <span>#${n} — ${_esc(_displayName(le))}
          <span class="spr-type-badge spr-type-${le.type.toLowerCase()}">${_esc(le.type)}</span>
        </span>`;
    }
  });

  document.getElementById('spr-link-add-btn').addEventListener('click', () => {
    const n = Number(inp.value);
    if (!n || !spritesByBmp[n]) {
      inp.focus();
      return;
    }
    if ((e.linked_bmps || []).includes(n)) {
      inp.value = '';
      if (prevEl) prevEl.classList.add('hidden');
      return;
    }
    if (!e.linked_bmps) e.linked_bmps = [];
    e.linked_bmps.push(n);
    e.linked_bmps.sort((a, b) => a - b);
    _queueSave(e.bmp_num, { linked_bmps: [...e.linked_bmps] });
    _updateListItem(e.bmp_num);

    // Refresh chips
    const chipsEl = document.getElementById('spr-link-chips');
    if (chipsEl) {
      chipsEl.innerHTML = e.linked_bmps.map(n2 => {
        const le2 = spritesByBmp[n2];
        const nm2 = le2 ? _displayName(le2) : `#${n2}`;
        return `<span class="spr-link-chip" data-link="${n2}">
          <button class="spr-link-chip-goto" data-link="${n2}" title="Go to BMP #${n2}">&#128279;</button>
          <span>#${n2} ${_esc(nm2)}</span>
          <button class="spr-link-chip-rm" data-link="${n2}" title="Remove link">&#10005;</button>
        </span>`;
      }).join('');
    }
    inp.value = '';
    if (prevEl) prevEl.classList.add('hidden');
    _renderFrames(e);
  });
}

// ---------------------------------------------------------------------------
// Animation
// ---------------------------------------------------------------------------

function _toggleAnim(key, bmpNum, frames, btn) {
  if (animTimers[key]) {
    _stopAnim(key);
  } else {
    _startAnim(key, bmpNum, frames, btn);
  }
}

function _startAnim(key, bmpNum, frames, btn) {
  const previewWrap = document.getElementById(`spr-anim-${key}`);
  const previewImg  = document.getElementById(`spr-anim-img-${key}`);
  if (!previewWrap || !previewImg) return;

  previewWrap.classList.remove('hidden');
  btn.innerHTML = '&#9646;&#9646;';
  btn.classList.add('active');

  let pos = 0;
  const tick = () => {
    const row = document.getElementById(`spr-row-${key}`);
    if (row) {
      row.querySelectorAll('.spr-fcell').forEach((c, i) =>
        c.classList.toggle('active', i === pos)
      );
    }
    previewImg.src = _frameUrl(bmpNum, frames[pos]);
    pos = (pos + 1) % frames.length;
  };
  tick();
  animTimers[key] = { timer: setInterval(tick, 150), btn, previewWrap };
}

function _stopAnim(key) {
  const entry = animTimers[key];
  if (!entry) return;
  clearInterval(entry.timer);
  delete animTimers[key];
  if (entry.previewWrap) entry.previewWrap.classList.add('hidden');
  if (entry.btn) { entry.btn.innerHTML = '&#9654;'; entry.btn.classList.remove('active'); }
  const row = document.getElementById(`spr-row-${key}`);
  if (row) row.querySelectorAll('.spr-fcell').forEach(c => c.classList.remove('active'));
}

function _stopAllAnims() {
  Object.keys(animTimers).forEach(_stopAnim);
}

// ---------------------------------------------------------------------------
// Save / metadata
// ---------------------------------------------------------------------------

function _queueSave(bmpNum, patch) {
  const key = String(bmpNum);
  if (!savePending[key]) {
    const e = spritesByBmp[bmpNum];
    savePending[key] = {
      type:        e.type,
      name:        e.name        || '',
      linked_bmps: e.linked_bmps || [],
      notes:       e.notes       || '',
      item_index:  e.item_index  ?? null,
    };
  }
  Object.assign(savePending[key], patch);
  _setSaveStatus('unsaved');
  clearTimeout(saveTimer);
  saveTimer = setTimeout(_flushSave, 800);
}

async function _flushSave() {
  clearTimeout(saveTimer);
  if (Object.keys(savePending).length === 0) return;
  const toSend = savePending;
  savePending   = {};
  _setSaveStatus('saving');

  try {
    const r = await fetch(`/api/sprite-metadata?game=${state.game}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sprites: toSend }),
    });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);

    // Merge server-returned bidirectional links back into local catalog
    // (server handles bidirectional sync; reload metadata to stay in sync)
    const metaR = await fetch(`/api/sprite-metadata?game=${state.game}`);
    if (metaR.ok) {
      const metaFull = await metaR.json();
      // Apply to catalog entries
      Object.entries(metaFull).forEach(([k, v]) => {
        const n = Number(k);
        const e = spritesByBmp[n];
        if (e && v) {
          if (v.type)             e.type        = v.type;
          if (v.name != null)     e.name        = v.name;
          if (v.linked_bmps)      e.linked_bmps = v.linked_bmps;
          if (v.notes != null)    e.notes       = v.notes;
          if ('item_index' in v)  e.item_index  = v.item_index;
        }
      });
    }
    _setSaveStatus('saved');
  } catch (err) {
    // Re-queue on error
    Object.assign(savePending, toSend);
    _setSaveStatus('error', err.message);
  }
}

function _setSaveStatus(status, msg) {
  saveStatus = status;
  const el  = document.getElementById('spr-save-status');
  const btn = document.getElementById('spr-save-btn');
  if (!el) return;

  const labels = {
    saved:   { text: 'All changes saved', cls: 'spr-saved' },
    saving:  { text: 'Saving…',           cls: 'spr-saving' },
    unsaved: { text: 'Unsaved changes',   cls: 'spr-unsaved' },
    error:   { text: `Save failed: ${msg}`, cls: 'spr-error-msg' },
  };
  const info = labels[status] || labels.error;
  el.textContent  = info.text;
  el.className    = `spr-save-status ${info.cls}`;
  if (btn) btn.disabled = (status === 'saved' || status === 'saving');
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function _frameUrl(bmpNum, frameIdx, scale) {
  const s = (scale ?? spriteScale);
  return `/api/gff/${spriteGffName}/image/BMP_/${bmpNum}?frame=${frameIdx}&game=${state.game}&scale=${s}`;
}

function _sheetLabel(type) {
  const m = { walk15: 'Walking', attack9: 'Attack', attack12: 'Attack+Ranged',
               attack14: 'Atk+Ranged+Death', combined24: 'Atk+Walk' };
  return m[type] || type || '';
}

function _displayName(e) {
  if (e.name) return e.name;
  if (e.rdff_names && e.rdff_names.length) return e.rdff_names.join(', ');
  return `BMP #${e.bmp_num}`;
}

function _esc(s) {
  if (s == null) return '';
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
