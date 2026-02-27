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

let savePending     = {};     // str(bmp_num) -> patch object
let saveTimer       = null;
let saveStatus      = 'saved'; // 'saved' | 'saving' | 'unsaved'

let linkObserver    = null;   // IntersectionObserver for list thumb lazy load
let _itemsCache     = null;   // items data from /api/items (lazy loaded)
let _pendingSelectBmp = null; // set externally to auto-select a BMP after catalog loads
let spriteGffName   = 'SEGOBJEX.GFF'; // actual GFF file for this game's sprites

let _selMode        = false;      // frame-selection mode active
let _selBmpNum      = null;       // which BMP is being selected
let _selFrames      = new Set();  // selected frame indices
let _editingSecIdx  = null;       // null = creating new group, N = editing group at index N
let _editingEntry   = null;       // which entry owns the section being edited (may differ from primary)
let _displayedEntry = null;       // primary entry currently shown in the frames panel
let _canonicalMap   = {};         // bmpNum -> canonical (primary) bmpNum for its linked group

let _patternsCache  = null;       // array of saved patterns (loaded lazily)
let _patternPanelBmp = null;      // bmpNum whose pattern panel is currently open

const SPRITE_TYPES  = ['PC', 'NPC', 'Monster', 'Scenery', 'Effect', 'Spell', 'Item', 'Unknown'];
const ALL_TABS      = ['PC', 'NPC', 'Monster', 'Scenery', 'Effect', 'Spell', 'Item', 'Unknown', 'All'];

// ---------------------------------------------------------------------------
// Entry / teardown
// ---------------------------------------------------------------------------

function renderSpritesView() {
  const view = document.getElementById('sprite-view');
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
  _selMode        = false;
  _selBmpNum      = null;
  _selFrames      = new Set();
  _editingSecIdx  = null;
  _editingEntry   = null;
  _displayedEntry = null;
  _canonicalMap   = {};
  _patternsCache  = null;
  _patternPanelBmp = null;
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
    _buildCanonicalMap();
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

// Assign canonical (primary) bmpNum for each linked group.
// Processes catalog in ascending bmpNum order; the first unclaimed entry
// becomes canonical for itself and all its linked peers.
function _buildCanonicalMap() {
  _canonicalMap = {};
  const claimed = new Set();
  spriteCatalog.forEach(e => {
    if (claimed.has(e.bmp_num)) return;
    _canonicalMap[e.bmp_num] = e.bmp_num;  // canonical of itself
    claimed.add(e.bmp_num);
    (e.linked_bmps || []).forEach(n => {
      if (!claimed.has(n)) {
        _canonicalMap[n] = e.bmp_num;       // subordinate points to this primary
        claimed.add(n);
      }
    });
  });
}

function _applyFilter() {
  if (!spriteCatalog) return;
  const { type, q, animated } = spriteFilter;

  // Raw pass — a canonical entry is included if IT matches, OR any of its
  // subordinate linked peers would match (so searching a linked sheet's
  // name still surfaces the combined entry).
  const rawFiltered = spriteCatalog.filter(e => {
    // Type check — pass if this entry OR any linked peer matches the type
    if (type !== 'All' && e.type !== type) {
      const linkedTypeMatch = (e.linked_bmps || []).some(n => {
        const le = spritesByBmp[n];
        return le && le.type === type;
      });
      if (!linkedTypeMatch) return false;
    }
    if (animated === 'animated' && e.frame_count <= 1) return false;
    if (animated === 'static'   && e.frame_count > 1)  return false;
    if (!q) return true;
    // Text search — also search linked peers' names/numbers
    const matchesQuery = entry => {
      if (String(entry.bmp_num).includes(q)) return true;
      if ((entry.name || '').toLowerCase().includes(q)) return true;
      return (entry.rdff_names || []).join(' ').toLowerCase().includes(q);
    };
    if (matchesQuery(e)) return true;
    return (e.linked_bmps || []).some(n => {
      const le = spritesByBmp[n];
      return le && matchesQuery(le);
    });
  });

  // Collect the canonical bmpNum for every raw match
  const canonicalSet = new Set(
    rawFiltered.map(e => _canonicalMap[e.bmp_num] ?? e.bmp_num)
  );

  // Keep only canonical entries (deduplicated — subordinates collapsed)
  spriteFiltered = spriteCatalog.filter(
    e => (_canonicalMap[e.bmp_num] ?? e.bmp_num) === e.bmp_num && canonicalSet.has(e.bmp_num)
  );

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
  const isActive  = (_canonicalMap[selectedBmpNum] ?? selectedBmpNum) === e.bmp_num;
  const li = document.createElement('li');
  li.className = 'spr-li' + (isActive ? ' active' : '');
  li.dataset.bmp = e.bmp_num;

  const thumbUrl  = `/api/gff/${spriteGffName}/image/BMP_/${e.bmp_num}?frame=0&game=${state.game}&scale=2`;
  const linked    = (e.linked_bmps || []).filter(n => spritesByBmp[n]);
  const totalFrames = e.frame_count +
    linked.reduce((s, n) => s + (spritesByBmp[n]?.frame_count || 0), 0);

  // Mini thumbs for linked sheets
  const linkedThumbsHtml = linked.map(n => {
    const url = `/api/gff/${spriteGffName}/image/BMP_/${n}?frame=0&game=${state.game}&scale=2`;
    return `<img class="spr-li-linked-img" src="${url}" alt="" onerror="this.style.display='none'"
      title="BMP #${n}${spritesByBmp[n]?.name ? ' – ' + spritesByBmp[n].name : ''}">`;
  }).join('');

  li.innerHTML = `
    <div class="spr-li-thumb${linked.length ? ' spr-li-thumb-group' : ''}">
      <img class="spr-li-img" data-src="${thumbUrl}" alt="" loading="lazy"
           onerror="this.style.display='none'">
      ${linked.length ? `<div class="spr-li-linked-thumbs">${linkedThumbsHtml}</div>` : ''}
    </div>
    <div class="spr-li-info">
      <div class="spr-li-name">${_esc(_displayName(e))}</div>
      <div class="spr-li-meta">
        <span class="spr-type-badge spr-type-${e.type.toLowerCase()}">${_esc(e.type)}</span>
        ${e.sheet_type ? `<span class="spr-sheet-badge">${_esc(_sheetLabel(e.sheet_type))}</span>` : ''}
        ${linked.length ? `<span class="spr-link-badge">&#128279;&thinsp;${linked.length + 1}</span>` : ''}
        ${totalFrames > 1 ? `<span class="spr-li-frames-badge" title="${totalFrames} frames total">&#9654;&thinsp;${totalFrames}</span>` : ''}
        <span class="spr-li-num">#${e.bmp_num}</span>
      </div>
    </div>`;

  return li;
}

function _updateListItem(bmpNum) {
  // Redirect to canonical — the list only contains canonical entries
  const canonical = _canonicalMap[bmpNum] ?? bmpNum;
  const li = document.querySelector(`#spr-list .spr-li[data-bmp="${canonical}"]`);
  if (!li) return;
  const e = spritesByBmp[canonical];
  if (!e) return;
  // Rebuild the entire item in place
  const replacement = _buildListItem(e);
  // Lazy-load img if not already loaded
  replacement.querySelectorAll('.spr-li-img[data-src]').forEach(img => {
    img.src = img.dataset.src;
    delete img.dataset.src;
  });
  li.replaceWith(replacement);
}

// ---------------------------------------------------------------------------
// Detail panel
// ---------------------------------------------------------------------------

function _selectEntry(bmpNum) {
  // Redirect subordinates to their canonical primary entry
  const canonical = _canonicalMap[bmpNum] ?? bmpNum;
  selectedBmpNum = canonical;
  _highlightListItem(canonical);
  pushUrlState();
  const e = spritesByBmp[canonical];
  if (!e) { _showEmptyDetail(true); return; }
  _showEmptyDetail(false);
  _renderDetail(e);
}

function _highlightListItem(bmpNum) {
  // bmpNum is already canonical here; match it directly
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
  state.mode = 'sprite';
  renderContent();
  if (spriteCatalog !== null && spritesByBmp[bmpNum]) {
    _pendingSelectBmp = null;
    _selectEntry(bmpNum);
  }
}

// ---------------------------------------------------------------------------
// Frame sections + group editor
// ---------------------------------------------------------------------------

function _renderFrames(e) {
  const area = document.getElementById('spr-frames-area');
  if (!area) return;
  area.innerHTML = '';
  _stopAllAnims();

  const prevDisplayed = _displayedEntry;
  _displayedEntry = e;

  // Cancel selection mode when switching to a *different* primary sprite
  if (prevDisplayed !== null && prevDisplayed.bmp_num !== e.bmp_num) {
    _selMode       = false;
    _selBmpNum     = null;
    _selFrames.clear();
    _editingSecIdx = null;
    _editingEntry  = null;
    _patternPanelBmp = null;
  }

  // Build one combined block: primary + all linked sheets folded in
  const block = _renderSheetBlock(e, '', true, e);

  // Append linked sheets inline into the same block (not for Items)
  if (e.type !== 'Item') {
    (e.linked_bmps || []).forEach(n => {
      const le = spritesByBmp[n];
      if (le) _appendLinkedSections(block, le, e);
    });
  }

  area.appendChild(block);

  // Frame-selection toolbar (always shown for primary sheet)
  _renderSelToolbar(area, e);
}

function _appendLinkedSections(block, linkedEntry, primaryEntry) {
  // Inline divider that acts as the linked-sheet sub-header
  const divider = document.createElement('div');
  divider.className = 'spr-linked-inline-hdr';

  const bmpLbl = document.createElement('span');
  bmpLbl.className = 'spr-sheet-bmp-lbl';
  bmpLbl.textContent = `BMP #${linkedEntry.bmp_num}`;
  divider.appendChild(bmpLbl);

  if (linkedEntry.pattern_name) {
    const patBadge = document.createElement('span');
    patBadge.className   = 'spr-pattern-name-badge';
    patBadge.title       = 'Applied pattern';
    patBadge.textContent = linkedEntry.pattern_name;
    divider.appendChild(patBadge);
  }

  const nameInp = document.createElement('input');
  nameInp.type        = 'text';
  nameInp.className   = 'spr-input spr-sheet-name-inp';
  nameInp.value       = linkedEntry.name || '';
  nameInp.placeholder = (linkedEntry.rdff_names || []).join(', ') || `Sheet #${linkedEntry.bmp_num}`;
  let nTimer;
  nameInp.addEventListener('input', () => {
    clearTimeout(nTimer);
    nTimer = setTimeout(() => {
      linkedEntry.name = nameInp.value.trim();
      _queueSave(linkedEntry.bmp_num, { name: linkedEntry.name });
      _updateListItem(linkedEntry.bmp_num);
    }, 400);
  });
  divider.appendChild(nameInp);

  const gotoBtn = document.createElement('button');
  gotoBtn.className = 'spr-btn spr-btn-sm';
  gotoBtn.innerHTML = '&#128279;';
  gotoBtn.title     = `Select BMP #${linkedEntry.bmp_num}`;
  gotoBtn.addEventListener('click', () => _selectEntry(linkedEntry.bmp_num));
  divider.appendChild(gotoBtn);

  // Pattern buttons for linked sheet
  const lnkPatPanel = document.createElement('div');
  lnkPatPanel.className = 'spr-pattern-panel hidden';
  lnkPatPanel.id        = `spr-pp-${linkedEntry.bmp_num}`;

  const lnkSavePatBtn = document.createElement('button');
  lnkSavePatBtn.className = 'spr-btn spr-btn-sm spr-pattern-save-btn';
  lnkSavePatBtn.title     = 'Save as pattern';
  lnkSavePatBtn.innerHTML = '&#128190; Save pattern';
  lnkSavePatBtn.addEventListener('click', () => {
    if (_patternPanelBmp === linkedEntry.bmp_num && !lnkPatPanel.classList.contains('hidden') &&
        lnkPatPanel.classList.contains('spr-pattern-panel-save')) {
      _closePatternPanel(lnkPatPanel, primaryEntry);
    } else {
      _openPatternPanel(lnkPatPanel, linkedEntry, primaryEntry, 'save');
    }
  });
  divider.appendChild(lnkSavePatBtn);

  const lnkApplyPatBtn = document.createElement('button');
  lnkApplyPatBtn.className = 'spr-btn spr-btn-sm spr-pattern-apply-btn';
  lnkApplyPatBtn.title     = 'Apply a saved pattern';
  lnkApplyPatBtn.innerHTML = '&#128203; Apply pattern';
  lnkApplyPatBtn.addEventListener('click', () => {
    if (_patternPanelBmp === linkedEntry.bmp_num && !lnkPatPanel.classList.contains('hidden') &&
        lnkPatPanel.classList.contains('spr-pattern-panel-apply')) {
      _closePatternPanel(lnkPatPanel, primaryEntry);
    } else {
      _openPatternPanel(lnkPatPanel, linkedEntry, primaryEntry, 'apply');
    }
  });
  divider.appendChild(lnkApplyPatBtn);

  // "New group…" for this linked sheet
  const isSelectingHere = (_selMode && _editingSecIdx === null && _selBmpNum === linkedEntry.bmp_num);
  if (!(_selMode && !isSelectingHere)) {
    const lnkSelToggle = document.createElement('button');
    lnkSelToggle.className = `spr-btn spr-btn-sm spr-sel-toggle${isSelectingHere ? ' active' : ''}`;
    lnkSelToggle.textContent = isSelectingHere ? 'Selecting…' : 'New group…';
    lnkSelToggle.addEventListener('click', () => {
      if (isSelectingHere) {
        _selMode = false; _selBmpNum = null;
        _selFrames.clear(); _editingEntry = null;
      } else {
        _selMode       = true;
        _selBmpNum     = linkedEntry.bmp_num;
        _editingEntry  = linkedEntry;
        _editingSecIdx = null;
        _selFrames.clear();
      }
      _renderFrames(primaryEntry);
    });
    divider.appendChild(lnkSelToggle);
  }

  block.appendChild(divider);

  // Sections
  const keySuffix = `_ls${linkedEntry.bmp_num}`;
  const useCustom = !!(linkedEntry.custom_sections && linkedEntry.custom_sections.length);
  const sections  = useCustom
    ? linkedEntry.custom_sections
    : (linkedEntry.sections || []);
  const keyPrefix = `${linkedEntry.bmp_num}${keySuffix}`;

  // Frames in a linked sheet are selectable when selection mode is active for that sheet
  const isLinkedSelecting = _selMode && _selBmpNum === linkedEntry.bmp_num;

  const secWrap = document.createElement('div');
  secWrap.className = 'spr-sections-wrap';

  if (sections.length === 0) {
    _appendPlainStrip(secWrap, linkedEntry, keyPrefix, isLinkedSelecting);
  } else {
    if (useCustom) _appendOrphanStrip(secWrap, linkedEntry, keyPrefix, isLinkedSelecting, sections);
    _sortedSections(sections).forEach(sec => {
      const origIdx = useCustom ? linkedEntry.custom_sections.indexOf(sec) : sections.indexOf(sec);
      secWrap.appendChild(
        _renderSectionEl(linkedEntry, sec, origIdx, keyPrefix, useCustom, primaryEntry)
      );
    });
  }

  block.appendChild(lnkPatPanel);
  block.appendChild(secWrap);
}

// primaryEntry = the top-level entry driving the whole frame panel
// Returns the block element (caller appends it)
function _renderSheetBlock(entry, keySuffix, isPrimary, primaryEntry) {
  const block = document.createElement('div');
  block.className = 'spr-sheet-block spr-sheet-primary';
  block.dataset.bmpBlock = String(entry.bmp_num);

  // --- Header: primary sheet only (linked sub-headers are inline dividers) ---
  const hdr = document.createElement('div');
  hdr.className = 'spr-sheet-block-hdr';

  const bmpLbl = document.createElement('span');
  bmpLbl.className = 'spr-sheet-bmp-lbl';
  bmpLbl.textContent = `BMP #${entry.bmp_num}`;
  hdr.appendChild(bmpLbl);

  if (entry.pattern_name) {
    const patBadge = document.createElement('span');
    patBadge.className   = 'spr-pattern-name-badge';
    patBadge.title       = 'Applied pattern';
    patBadge.textContent = entry.pattern_name;
    hdr.appendChild(patBadge);
  }

  if (entry.sheet_type) {
    const badge = document.createElement('span');
    badge.className = 'spr-sheet-badge';
    badge.textContent = _sheetLabel(entry.sheet_type);
    hdr.appendChild(badge);
  }
  const fcSpan = document.createElement('span');
  fcSpan.className = 'dim spr-sheet-fc';
  fcSpan.textContent = `${entry.frame_count} frame${entry.frame_count !== 1 ? 's' : ''}`;
  hdr.appendChild(fcSpan);

  // Pattern save / apply buttons (spacer pushes them to the right)
  const patSpacer = document.createElement('span');
  patSpacer.style.flex = '1';
  hdr.appendChild(patSpacer);

  const patPanel = document.createElement('div');
  patPanel.className = 'spr-pattern-panel hidden';
  patPanel.id        = `spr-pp-${entry.bmp_num}`;

  const savePatBtn = document.createElement('button');
  savePatBtn.className = 'spr-btn spr-btn-sm spr-pattern-save-btn';
  savePatBtn.title     = 'Save as pattern';
  savePatBtn.innerHTML = '&#128190; Save pattern';
  savePatBtn.addEventListener('click', () => {
    if (_patternPanelBmp === entry.bmp_num && !patPanel.classList.contains('hidden') &&
        patPanel.classList.contains('spr-pattern-panel-save')) {
      _closePatternPanel(patPanel, primaryEntry);
    } else {
      _openPatternPanel(patPanel, entry, primaryEntry, 'save');
    }
  });
  hdr.appendChild(savePatBtn);

  const applyPatBtn = document.createElement('button');
  applyPatBtn.className = 'spr-btn spr-btn-sm spr-pattern-apply-btn';
  applyPatBtn.title     = 'Apply a saved pattern';
  applyPatBtn.innerHTML = '&#128203; Apply pattern';
  applyPatBtn.addEventListener('click', () => {
    if (_patternPanelBmp === entry.bmp_num && !patPanel.classList.contains('hidden') &&
        patPanel.classList.contains('spr-pattern-panel-apply')) {
      _closePatternPanel(patPanel, primaryEntry);
    } else {
      _openPatternPanel(patPanel, entry, primaryEntry, 'apply');
    }
  });
  hdr.appendChild(applyPatBtn);

  block.appendChild(hdr);
  block.appendChild(patPanel);

  // --- Sections ---
  const useCustom = !!(entry.custom_sections && entry.custom_sections.length);
  const sections  = useCustom
    ? entry.custom_sections
    : (entry.sections || []);  // legacy fallback (should rarely trigger)
  const keyPrefix = `${entry.bmp_num}${keySuffix}`;

  const secWrap = document.createElement('div');
  secWrap.className = 'spr-sections-wrap';

  if (sections.length === 0) {
    _appendPlainStrip(secWrap, entry, keyPrefix, true);
  } else {
    if (useCustom) _appendOrphanStrip(secWrap, entry, keyPrefix, true, sections);
    _sortedSections(sections).forEach(sec => {
      const origIdx = useCustom ? entry.custom_sections.indexOf(sec) : sections.indexOf(sec);
      secWrap.appendChild(
        _renderSectionEl(entry, sec, origIdx, keyPrefix, useCustom, primaryEntry)
      );
    });
  }

  block.appendChild(secWrap);
  return block;
}

// Render a strip of frames that don't appear in any custom section
function _appendOrphanStrip(container, entry, keyPrefix, isPrimary, sections) {
  const usedFrames = new Set(
    sections.flatMap(s => s.frames || [])
  );
  const orphans = [];
  for (let i = 0; i < entry.frame_count; i++) {
    if (!usedFrames.has(i)) orphans.push(i);
  }
  if (orphans.length === 0) return;

  const key = `${keyPrefix}_ungrouped`;

  const sec = document.createElement('div');
  sec.className = 'spr-sec spr-sec-ungrouped';

  const hdr = document.createElement('div');
  hdr.className = 'spr-sec-hdr';

  const lbl = document.createElement('span');
  lbl.className   = 'spr-sec-name dim';
  lbl.textContent = `Ungrouped (${orphans.length}f)`;
  hdr.appendChild(lbl);
  sec.appendChild(hdr);

  const row = document.createElement('div');
  row.className = 'spr-frame-row';
  row.id        = `spr-row-${key}`;
  orphans.forEach(fi =>
    row.appendChild(_makeFrameCell(entry.bmp_num, fi, key, false, isPrimary))
  );
  sec.appendChild(row);

  container.appendChild(sec);
}

function _appendPlainStrip(container, entry, keyPrefix, isPrimary) {
  const key     = `${keyPrefix}_plain`;
  const titleDiv = document.createElement('div');
  titleDiv.className = 'spr-section-title';
  const titleTxt = document.createElement('span');
  titleTxt.textContent = `Frames (${entry.frame_count})`;
  titleDiv.appendChild(titleTxt);

  if (entry.frame_count > 1) {
    const frames  = Array.from({ length: entry.frame_count }, (_, i) => i);
    const playBtn = document.createElement('button');
    playBtn.className = 'spr-play-btn';
    playBtn.id        = `spr-play-${key}`;
    playBtn.innerHTML = '&#9654;';
    playBtn.title     = 'Animate';
    playBtn.addEventListener('click', () => _toggleAnim(key, entry.bmp_num, frames, playBtn));
    titleDiv.appendChild(playBtn);
  }
  container.appendChild(titleDiv);

  const row = document.createElement('div');
  row.className = 'spr-frame-row';
  row.id        = `spr-row-${key}`;
  for (let i = 0; i < entry.frame_count; i++) {
    row.appendChild(_makeFrameCell(entry.bmp_num, i, key, false, isPrimary));
  }
  container.appendChild(row);

  if (entry.frame_count > 1) {
    const preview = document.createElement('div');
    preview.className = 'spr-anim-wrap hidden';
    preview.id        = `spr-anim-${key}`;
    const pImg = document.createElement('img');
    pImg.className = 'spr-anim-img';
    pImg.id        = `spr-anim-img-${key}`;
    pImg.src       = _frameUrl(entry.bmp_num, 0);
    preview.appendChild(pImg);
    container.appendChild(preview);
  }
}

// Sort a sections array by the first frame index (display order only — does not mutate)
function _sortedSections(sections) {
  return [...sections].sort((a, b) => {
    const af = a.frames && a.frames.length ? a.frames[0] : Infinity;
    const bf = b.frames && b.frames.length ? b.frames[0] : Infinity;
    return af - bf;
  });
}

// primaryEntry: the top-level sprite entry (used to re-render the whole panel)
function _renderSectionEl(entry, sec, secIdx, keyPrefix, isCustom, primaryEntry) {
  if (!primaryEntry) primaryEntry = entry;
  const key      = `${keyPrefix}_${secIdx}`;
  const isSingle = sec.frames.length === 1;
  const isEditing = (_selMode && _editingSecIdx === secIdx
                     && _selBmpNum === entry.bmp_num
                     && _editingEntry === entry);

  const secEl = document.createElement('div');
  secEl.className = [
    'spr-sec',
    isSingle ? 'spr-sec-single' : 'spr-sec-multi',
    isCustom  ? 'spr-sec-custom' : 'spr-sec-auto',
    isEditing ? 'spr-sec-editing' : '',
  ].filter(Boolean).join(' ');
  secEl.dataset.secIdx = secIdx;

  const isMirrored = !!sec.mirrored;

  // --- Frame row (play button lives here, next to the frames) ---
  // Frames are always selectable — _makeFrameCell itself guards with _selMode && _selBmpNum===bmpNum
  const row = document.createElement('div');
  row.className = 'spr-frame-row';
  row.id        = `spr-row-${key}`;
  sec.frames.forEach(fi =>
    row.appendChild(_makeFrameCell(entry.bmp_num, fi, key, isMirrored, true))
  );

  // Play button — inline, right after the last frame cell (multi-frame only)
  if (!isSingle) {
    const playBtn = document.createElement('button');
    playBtn.className = 'spr-play-btn spr-play-inline';
    playBtn.id        = `spr-play-${key}`;
    playBtn.innerHTML = '&#9654;';
    playBtn.title     = 'Animate';
    playBtn.addEventListener('click', () =>
      _toggleAnim(key, entry.bmp_num, sec.frames, playBtn)
    );
    row.appendChild(playBtn);
  }

  secEl.appendChild(row);

  // --- Animation preview (multi-frame only) ---
  if (!isSingle) {
    const preview = document.createElement('div');
    preview.className = 'spr-anim-wrap hidden';
    preview.id        = `spr-anim-${key}`;
    const pImg = document.createElement('img');
    pImg.className = 'spr-anim-img';
    pImg.id        = `spr-anim-img-${key}`;
    if (isMirrored) pImg.style.transform = 'scaleX(-1)';
    pImg.src       = _frameUrl(entry.bmp_num, sec.frames[0]);
    preview.appendChild(pImg);
    secEl.appendChild(preview);
  }

  // --- Footer: name → edit → (mirror + delete in edit mode only) ---
  const footer = document.createElement('div');
  footer.className = 'spr-sec-footer';

  // Name
  if (isCustom && isEditing) {
    const nameInp = document.createElement('input');
    nameInp.type        = 'text';
    nameInp.className   = 'spr-input spr-sec-name-inp';
    nameInp.value       = sec.name || '';
    nameInp.placeholder = 'Group name…';
    let nTimer;
    nameInp.addEventListener('input', () => {
      clearTimeout(nTimer);
      nTimer = setTimeout(() => { sec.name = nameInp.value; _saveCustomSections(entry); }, 400);
    });
    footer.appendChild(nameInp);
  } else {
    const nameEl = document.createElement('span');
    nameEl.className   = 'spr-sec-name';
    nameEl.textContent = isCustom ? (sec.name || '(unnamed)') : (sec.name || '');
    footer.appendChild(nameEl);
    if (!isCustom && sec.desc) {
      const descEl = document.createElement('span');
      descEl.className   = 'spr-sec-desc dim';
      descEl.textContent = sec.desc;
      footer.appendChild(descEl);
    }
  }

  if (isCustom) {
    // Edit frames button (pencil — always visible)
    const editBtn = document.createElement('button');
    editBtn.className = `spr-btn spr-btn-sm spr-sec-edit-btn${isEditing ? ' active' : ''}`;
    editBtn.innerHTML = '&#9998;';
    editBtn.title     = isEditing ? 'Done editing frames' : 'Edit frames';
    editBtn.addEventListener('click', () => {
      if (isEditing) {
        _selMode = false; _selBmpNum = null;
        _selFrames.clear(); _editingSecIdx = null; _editingEntry = null;
      } else {
        _selMode = true; _selBmpNum = entry.bmp_num;
        _editingSecIdx = secIdx; _editingEntry = entry;
        _selFrames = new Set(sec.frames);
      }
      _renderFrames(primaryEntry);
    });
    footer.appendChild(editBtn);

    // Mirror + delete — only in edit mode
    if (isEditing) {
      // Mirror toggle — original (non-mirrored) sections only; mirrored copies can't be mirrored again
      if (!isMirrored) {
        const hasMirrorCopy = entry.custom_sections.some(s => s.mirrorOf === sec.id);
        const mirBtn = document.createElement('button');
        mirBtn.className = `spr-btn spr-btn-sm spr-sec-mirror-btn${hasMirrorCopy ? ' active' : ''}`;
        mirBtn.innerHTML = '&#8596;';
        mirBtn.title     = hasMirrorCopy ? 'Remove mirrored copy' : 'Add mirrored copy';
        mirBtn.addEventListener('click', () => {
          if (hasMirrorCopy) {
            // Remove companion
            entry.custom_sections = entry.custom_sections.filter(s => s.mirrorOf !== sec.id);
          } else {
            // Insert companion immediately after this section
            const newSec = {
              id:       _nextSectionId(entry),
              name:     (sec.name ? sec.name + ' \u21d4' : '\u21d4'),
              frames:   [...sec.frames],
              mirrored: true,
              mirrorOf: sec.id,
            };
            const storedIdx = entry.custom_sections.indexOf(sec);
            entry.custom_sections.splice(storedIdx + 1, 0, newSec);
          }
          _saveCustomSections(entry);
          _selMode = false; _selBmpNum = null;
          _selFrames.clear(); _editingSecIdx = null; _editingEntry = null;
          _renderFrames(primaryEntry);
        });
        footer.appendChild(mirBtn);
      }

      const delBtn = document.createElement('button');
      delBtn.className = 'spr-btn spr-btn-sm spr-sec-del-btn';
      delBtn.innerHTML = '&#128465;';
      delBtn.title     = 'Delete group';
      delBtn.addEventListener('click', () => {
        _selMode = false; _selBmpNum = null;
        _selFrames.clear(); _editingSecIdx = null; _editingEntry = null;
        // Remove this section and any mirrored companion
        entry.custom_sections = entry.custom_sections.filter(
          s => s !== sec && s.mirrorOf !== sec.id
        );
        entry.pattern_name = null;
        _saveCustomSections(entry);
        _renderFrames(primaryEntry);
      });
      footer.appendChild(delBtn);
    }
  }

  secEl.appendChild(footer);
  return secEl;
}

// ---------------------------------------------------------------------------
// Custom section helpers
// ---------------------------------------------------------------------------

function _saveCustomSections(entry) {
  _queueSave(entry.bmp_num, {
    custom_sections: JSON.parse(JSON.stringify(entry.custom_sections || [])),
    pattern_name:    entry.pattern_name || null,
  });
}

function _nextSectionId(entry) {
  const ids = (entry.custom_sections || []).map(s => Number(s.id) || 0);
  return String(ids.length ? Math.max(...ids) + 1 : 1);
}

// ---------------------------------------------------------------------------
// Sprite sheet patterns
// ---------------------------------------------------------------------------

async function _loadAllPatterns() {
  if (_patternsCache !== null) return _patternsCache;
  try {
    const r = await fetch('/api/sprite-patterns');
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    _patternsCache = await r.json();
  } catch (_) {
    _patternsCache = [];
  }
  return _patternsCache;
}

/** Insert or refresh the pattern panel inside a block element for the given entry. */
function _openPatternPanel(panelEl, entry, primaryEntry, mode) {
  panelEl.innerHTML = '';
  panelEl.classList.remove('hidden');
  _patternPanelBmp = entry.bmp_num;

  if (mode === 'save') {
    _buildSavePatternForm(panelEl, entry, primaryEntry);
  } else {
    _buildApplyPatternForm(panelEl, entry, primaryEntry);
  }
}

function _closePatternPanel(panelEl, primaryEntry) {
  _patternPanelBmp = null;
  panelEl.classList.add('hidden');
  panelEl.innerHTML = '';
}

/** Build a small form for saving the current sections as a new/updated pattern. */
function _buildSavePatternForm(panelEl, entry, primaryEntry) {
  panelEl.classList.add('spr-pattern-panel-save');
  panelEl.classList.remove('spr-pattern-panel-apply');

  const sections = entry.custom_sections || [];

  if (sections.length === 0) {
    const msg = document.createElement('p');
    msg.className   = 'spr-pattern-msg dim';
    msg.textContent = 'No groups defined. Create at least one group before saving a pattern.';
    panelEl.appendChild(msg);
    const closeBtn = document.createElement('button');
    closeBtn.className   = 'spr-btn spr-btn-sm';
    closeBtn.textContent = 'Close';
    closeBtn.addEventListener('click', () => _closePatternPanel(panelEl, primaryEntry));
    panelEl.appendChild(closeBtn);
    return;
  }

  const summary = document.createElement('p');
  summary.className   = 'spr-pattern-msg dim';
  const mirroredCount = sections.filter(s => s.mirrored).length;
  summary.textContent = `${sections.length} group${sections.length !== 1 ? 's' : ''}` +
    ` · ${entry.frame_count} frame${entry.frame_count !== 1 ? 's' : ''}` +
    (mirroredCount ? ` · ${mirroredCount} mirrored` : '');
  panelEl.appendChild(summary);

  const row = document.createElement('div');
  row.className = 'spr-pattern-form-row';

  const nameInp = document.createElement('input');
  nameInp.type        = 'text';
  nameInp.className   = 'spr-input spr-pattern-name-inp';
  nameInp.placeholder = 'Pattern name…';
  nameInp.maxLength   = 80;
  row.appendChild(nameInp);

  const saveBtn = document.createElement('button');
  saveBtn.className   = 'spr-btn spr-btn-primary spr-btn-sm';
  saveBtn.textContent = 'Save pattern';
  saveBtn.addEventListener('click', async () => {
    const name = nameInp.value.trim();
    if (!name) { nameInp.focus(); return; }
    saveBtn.disabled = true;
    saveBtn.textContent = 'Saving…';
    await _doSavePattern(entry, name);
    // Apply immediately: stamp pattern_name on this entry and re-render.
    // (_doSavePattern already synced all peer entries' custom_sections in memory.)
    entry.pattern_name = name;
    _patternPanelBmp = null;
    _saveCustomSections(entry);
    // Refresh sidebar list items for every sprite now sharing this pattern name
    spriteCatalog.forEach(e2 => {
      if (e2.pattern_name === name && e2.bmp_num !== entry.bmp_num) {
        _updateListItem(e2.bmp_num);
      }
    });
    _renderFrames(primaryEntry);
  });
  row.appendChild(saveBtn);

  const cancelBtn = document.createElement('button');
  cancelBtn.className   = 'spr-btn spr-btn-sm';
  cancelBtn.textContent = 'Cancel';
  cancelBtn.addEventListener('click', () => _closePatternPanel(panelEl, primaryEntry));
  row.appendChild(cancelBtn);

  panelEl.appendChild(row);
  nameInp.focus();
}

/** Build a picker that shows saved patterns compatible with entry.frame_count. */
async function _buildApplyPatternForm(panelEl, entry, primaryEntry) {
  panelEl.classList.add('spr-pattern-panel-apply');
  panelEl.classList.remove('spr-pattern-panel-save');

  const loadingEl = document.createElement('span');
  loadingEl.className   = 'dim';
  loadingEl.textContent = 'Loading patterns…';
  panelEl.appendChild(loadingEl);

  const allPatterns  = await _loadAllPatterns();
  if (!panelEl.isConnected) return;   // panel was closed while loading
  panelEl.innerHTML = '';

  const compatible = allPatterns.filter(p => p.frame_count === entry.frame_count);

  if (compatible.length === 0) {
    const msg = document.createElement('p');
    msg.className   = 'spr-pattern-msg dim';
    msg.textContent = `No saved patterns for ${entry.frame_count}-frame sheets.`;
    panelEl.appendChild(msg);
    const closeBtn = document.createElement('button');
    closeBtn.className   = 'spr-btn spr-btn-sm';
    closeBtn.textContent = 'Close';
    closeBtn.addEventListener('click', () => _closePatternPanel(panelEl, primaryEntry));
    panelEl.appendChild(closeBtn);
    return;
  }

  const hdrRow = document.createElement('div');
  hdrRow.className = 'spr-pattern-form-row';
  const hdrLbl = document.createElement('span');
  hdrLbl.className   = 'spr-pattern-section-lbl';
  hdrLbl.textContent = `Compatible patterns (${entry.frame_count} frames):`;
  hdrRow.appendChild(hdrLbl);
  const cancelBtn = document.createElement('button');
  cancelBtn.className   = 'spr-btn spr-btn-sm';
  cancelBtn.style.marginLeft = 'auto';
  cancelBtn.textContent = 'Cancel';
  cancelBtn.addEventListener('click', () => _closePatternPanel(panelEl, primaryEntry));
  hdrRow.appendChild(cancelBtn);
  panelEl.appendChild(hdrRow);

  const list = document.createElement('div');
  list.className = 'spr-pattern-list';
  compatible.forEach(pat => {
    const item = document.createElement('div');
    item.className = 'spr-pattern-item';

    const info = document.createElement('div');
    info.className = 'spr-pattern-item-info';
    const nameLbl = document.createElement('span');
    nameLbl.className   = 'spr-pattern-item-name';
    nameLbl.textContent = pat.name;
    info.appendChild(nameLbl);
    const detailLbl = document.createElement('span');
    detailLbl.className   = 'dim spr-pattern-item-detail';
    const mirCount = (pat.sections || []).filter(s => s.mirrored).length;
    detailLbl.textContent = `${(pat.sections||[]).length} group${(pat.sections||[]).length!==1?'s':''}` +
      (mirCount ? ` · ${mirCount} mirrored` : '');
    info.appendChild(detailLbl);
    item.appendChild(info);

    const btnRow = document.createElement('div');
    btnRow.className = 'spr-pattern-item-btns';

    const applyBtn = document.createElement('button');
    applyBtn.className   = 'spr-btn spr-btn-primary spr-btn-sm';
    applyBtn.textContent = 'Apply';
    applyBtn.addEventListener('click', () => {
      _doApplyPattern(entry, pat, primaryEntry);
      _closePatternPanel(panelEl, primaryEntry);
    });
    btnRow.appendChild(applyBtn);

    const delBtn = document.createElement('button');
    delBtn.className = 'spr-btn spr-btn-sm spr-pattern-del-btn';
    delBtn.title     = 'Delete this pattern';
    delBtn.innerHTML = '&#128465;';
    delBtn.addEventListener('click', async () => {
      delBtn.disabled = true;
      await _doDeletePattern(pat.id);
      item.remove();
      if (!list.children.length) _buildApplyPatternForm(panelEl, entry, primaryEntry);
    });
    btnRow.appendChild(delBtn);

    item.appendChild(btnRow);
    list.appendChild(item);
  });
  panelEl.appendChild(list);
}

async function _doSavePattern(entry, name) {
  const sections = JSON.parse(JSON.stringify(entry.custom_sections || []));
  const pattern  = {
    name,
    frame_count: entry.frame_count,
    sections,
  };
  try {
    const r = await fetch(`/api/sprite-patterns?game=${state.game}`, {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({ action: 'save', pattern }),
    });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const data = await r.json();
    // Update patterns cache
    if (_patternsCache !== null) {
      const idx = _patternsCache.findIndex(p => p.id === data.pattern.id);
      if (idx >= 0) _patternsCache[idx] = data.pattern;
      else _patternsCache.push(data.pattern);
    }
    // Re-sync ALL sprite metadata — backend may have propagated new sections
    // to every sprite sharing this pattern name.
    const metaR = await fetch(`/api/sprite-metadata?game=${state.game}`);
    if (metaR.ok) {
      const metaFull = await metaR.json();
      Object.entries(metaFull).forEach(([k, v]) => {
        const e = spritesByBmp[Number(k)];
        if (e && v) {
          if (v.custom_sections != null) e.custom_sections = v.custom_sections;
          if ('pattern_name' in v)       e.pattern_name    = v.pattern_name;
        }
      });
    }
    return data.pattern;
  } catch (err) {
    console.error('Failed to save pattern:', err);
    return null;
  }
}

async function _doDeletePattern(patId) {
  try {
    await fetch('/api/sprite-patterns', {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({ action: 'delete', id: patId }),
    });
    if (_patternsCache !== null) {
      _patternsCache = _patternsCache.filter(p => p.id !== patId);
    }
  } catch (err) {
    console.error('Failed to delete pattern:', err);
  }
}

function _doApplyPattern(entry, pattern, primaryEntry) {
  const origSections = pattern.sections || [];
  // Build old-id → new-id mapping first
  const idMap = {};
  origSections.forEach((s, i) => { idMap[s.id] = String(i + 1); });
  // Deep-copy with re-stamped IDs and re-wired mirrorOf references
  entry.custom_sections = origSections.map((s, i) => {
    const copy = JSON.parse(JSON.stringify(s));
    copy.id = String(i + 1);
    if (copy.mirrorOf) copy.mirrorOf = idMap[copy.mirrorOf] || copy.mirrorOf;
    return copy;
  });
  entry.pattern_name = pattern.name;

  _saveCustomSections(entry);
  _selMode = false; _selBmpNum = null;
  _selFrames.clear(); _editingSecIdx = null; _editingEntry = null;
  _patternPanelBmp = null;
  _renderFrames(primaryEntry);
}

// ---------------------------------------------------------------------------
// Frame selection toolbar
// ---------------------------------------------------------------------------

function _renderSelToolbar(area, primaryEntry) {
  // Attach "New group…" toggle to the primary sheet header
  // Hide it when selection mode is active for a different (linked) BMP
  const primaryHdr   = area.querySelector('.spr-sheet-primary .spr-sheet-block-hdr');
  const isEditMode   = (_selMode && _editingSecIdx !== null);
  const isLinkedSel  = (_selMode && _selBmpNum !== primaryEntry.bmp_num);
  const isPrimarySelecting = (_selMode && _editingSecIdx === null && _selBmpNum === primaryEntry.bmp_num);
  if (primaryHdr && primaryEntry.frame_count > 0 && !isEditMode && !isLinkedSel) {
    const selToggle = document.createElement('button');
    selToggle.className = `spr-btn spr-btn-sm spr-sel-toggle${isPrimarySelecting ? ' active' : ''}`;
    selToggle.id        = 'spr-sel-toggle';
    selToggle.textContent = isPrimarySelecting ? 'Selecting…' : 'New group…';
    selToggle.addEventListener('click', () => _toggleSelMode(primaryEntry, selToggle));
    primaryHdr.appendChild(selToggle);
  }

  // Sticky creation/edit bar
  const bar = document.createElement('div');
  bar.id        = 'spr-sel-bar';
  bar.className = `spr-sel-bar${_selMode ? '' : ' hidden'}`;
  const isEditMode2   = (_editingSecIdx !== null);
  const _editSrcEntry = _editingEntry || primaryEntry;
  const editingSec    = isEditMode2 ? (_editSrcEntry.custom_sections || [])[_editingSecIdx] : null;
  // Is selection targeting a linked (non-primary) sheet?
  const isLinkedNewGroup = (!isEditMode2 && _editingEntry && _editingEntry !== primaryEntry);

  const countEl = document.createElement('span');
  countEl.className   = 'spr-sel-count dim';
  countEl.textContent = `${_selFrames.size} frame${_selFrames.size !== 1 ? 's' : ''} selected`;
  bar.appendChild(countEl);

  if (!isEditMode2) {
    // New-group mode: label shows which sheet this belongs to
    if (isLinkedNewGroup) {
      const ctxLbl = document.createElement('span');
      ctxLbl.className   = 'spr-sel-edit-lbl';
      ctxLbl.textContent = `BMP #${_editingEntry.bmp_num}`;
      bar.appendChild(ctxLbl);
    }
    const nameInp = document.createElement('input');
    nameInp.id          = 'spr-sel-name';
    nameInp.type        = 'text';
    nameInp.className   = 'spr-input spr-sel-name-inp';
    nameInp.placeholder = 'Group name…';
    bar.appendChild(nameInp);
  } else {
    // Edit mode: show which group we're editing
    const editLbl = document.createElement('span');
    editLbl.className   = 'spr-sel-edit-lbl';
    editLbl.textContent = `Editing: "${editingSec ? editingSec.name || 'Group' : ''}"`;
    bar.appendChild(editLbl);
  }

  const actionBtn = document.createElement('button');
  actionBtn.className   = `spr-btn spr-btn-primary spr-sel-create-btn`;
  actionBtn.textContent = isEditMode2 ? '✓ Save group' : '+ Create group';
  actionBtn.disabled    = (_selFrames.size === 0);
  actionBtn.addEventListener('click', () => {
    if (isEditMode2) {
      _updateCustomSection(primaryEntry);
    } else {
      _createCustomSection(primaryEntry);
    }
  });
  bar.appendChild(actionBtn);

  const clearBtn = document.createElement('button');
  clearBtn.className   = 'spr-btn spr-sel-clear-btn';
  clearBtn.textContent = 'Clear selection';
  clearBtn.addEventListener('click', () => { _selFrames.clear(); _updateSelUI(); });
  bar.appendChild(clearBtn);

  const doneBtn = document.createElement('button');
  doneBtn.className   = 'spr-btn spr-sel-done-btn';
  doneBtn.textContent = 'Cancel';
  doneBtn.addEventListener('click', () => {
    _selMode = false; _selBmpNum = null;
    _selFrames.clear(); _editingSecIdx = null; _editingEntry = null;
    _renderFrames(primaryEntry);
  });
  bar.appendChild(doneBtn);

  // Insert bar right after the pattern panel for the active sheet
  // (#spr-pp-{bmpNum} sits immediately before that sheet's sections-wrap),
  // so the bar appears directly below whichever "New group…" button was clicked.
  const targetPatPanel = area.querySelector(`#spr-pp-${_selMode ? _selBmpNum : primaryEntry.bmp_num}`);
  if (targetPatPanel) {
    targetPatPanel.insertAdjacentElement('afterend', bar);
  } else {
    // fallback: before primary sections-wrap
    const primaryBlock = area.querySelector('.spr-sheet-primary');
    const secWrap      = primaryBlock && primaryBlock.querySelector('.spr-sections-wrap');
    if (secWrap) primaryBlock.insertBefore(bar, secWrap);
    else area.appendChild(bar);
  }
}

function _toggleSelMode(primaryEntry, btn, forceOff) {
  _selMode       = forceOff ? false : !_selMode;
  _selBmpNum     = _selMode ? primaryEntry.bmp_num : null;
  _selFrames.clear();
  _editingSecIdx = null;
  _editingEntry  = null;
  // Re-render to add/remove selectable classes
  _renderFrames(primaryEntry);
}

function _updateSelUI() {
  const bar = document.getElementById('spr-sel-bar');
  if (!bar) return;
  const count   = _selFrames.size;
  const countEl = bar.querySelector('.spr-sel-count');
  if (countEl) countEl.textContent = `${count} frame${count !== 1 ? 's' : ''} selected`;
  const createBtn = bar.querySelector('.spr-sel-create-btn');
  if (createBtn) createBtn.disabled = (count === 0);
  // Update frame cell highlights (dataset.bmpNum → data-bmpnum in DOM)
  document.querySelectorAll('.spr-fcell').forEach(cell => {
    const cellBmp = Number(cell.dataset.bmpNum);
    if (cellBmp === _selBmpNum) {
      cell.classList.toggle('spr-fcell-sel', _selFrames.has(Number(cell.dataset.pos)));
    }
  });
}

function _toggleFrameSel(bmpNum, frameIdx) {
  if (_selBmpNum !== bmpNum) return;
  if (_selFrames.has(frameIdx)) {
    _selFrames.delete(frameIdx);
  } else {
    _selFrames.add(frameIdx);
  }
  _updateSelUI();
}

function _createCustomSection(primaryEntry) {
  // Target is the entry whose frames were selected — may be a linked sheet
  const entry   = _editingEntry || primaryEntry;
  const bar     = document.getElementById('spr-sel-bar');
  if (!bar) return;
  const nameInp = bar.querySelector('#spr-sel-name');
  const frames  = [..._selFrames].sort((a, b) => a - b);
  if (frames.length === 0) return;
  const name = (nameInp && nameInp.value.trim()) ||
               `Group ${(entry.custom_sections || []).length + 1}`;
  if (!entry.custom_sections) entry.custom_sections = [];
  entry.custom_sections.push({ id: _nextSectionId(entry), name, frames });
  entry.pattern_name = null;
  _saveCustomSections(entry);
  _selFrames.clear();
  if (nameInp) nameInp.value = '';
  _selMode = false; _selBmpNum = null; _editingSecIdx = null; _editingEntry = null;
  _renderFrames(primaryEntry);
}

function _updateCustomSection(primaryEntry) {
  const targetEntry = _editingEntry || primaryEntry;
  if (_editingSecIdx === null || !targetEntry.custom_sections) return;
  const sec = targetEntry.custom_sections[_editingSecIdx];
  if (!sec) return;
  sec.frames = [..._selFrames].sort((a, b) => a - b);
  targetEntry.pattern_name = null;
  _saveCustomSections(targetEntry);
  _selMode = false; _selBmpNum = null;
  _selFrames.clear(); _editingSecIdx = null; _editingEntry = null;
  _renderFrames(primaryEntry);
}

function _makeFrameCell(bmpNum, frameIdx, secKey, mirrored, selectable) {
  const wrap = document.createElement('div');
  wrap.className = 'spr-fcell';
  wrap.dataset.secKey = secKey || '';
  wrap.dataset.pos    = frameIdx;
  wrap.dataset.bmpNum = bmpNum;

  if (selectable && _selMode && _selBmpNum === bmpNum) {
    wrap.classList.add('spr-fcell-selectable');
    if (_selFrames.has(frameIdx)) wrap.classList.add('spr-fcell-sel');
    wrap.addEventListener('click', () => _toggleFrameSel(bmpNum, frameIdx));
  }

  const img = document.createElement('img');
  img.src       = _frameUrl(bmpNum, frameIdx);
  img.alt       = `F${frameIdx}`;
  img.className = 'spr-fimg';
  if (mirrored) img.style.transform = 'scaleX(-1)';
  img.onerror = () => { wrap.classList.add('spr-fcell-err'); img.style.display = 'none'; };

  const lbl = document.createElement('div');
  lbl.className   = 'spr-flbl dim';
  lbl.textContent = frameIdx;

  wrap.appendChild(img);
  wrap.appendChild(lbl);
  return wrap;
}

// ---------------------------------------------------------------------------
// Link group helpers — transitive closure
// ---------------------------------------------------------------------------

// Walk existing linked_bmps transitively and return the full connected set
// (including startBmp itself).
function _collectGroup(startBmp) {
  const visited = new Set();
  const queue   = [startBmp];
  while (queue.length) {
    const n = queue.shift();
    if (visited.has(n)) continue;
    visited.add(n);
    const e = spritesByBmp[n];
    if (e) (e.linked_bmps || []).forEach(m => { if (!visited.has(m)) queue.push(m); });
  }
  return visited;
}

// Given a set of bmp_nums (the merged group), set every member's linked_bmps
// to all *other* members and persist each one.
function _setGroupLinks(members) {
  const arr = [...members].sort((a, b) => a - b);
  for (const n of arr) {
    const e = spritesByBmp[n];
    if (!e) continue;
    e.linked_bmps = arr.filter(x => x !== n);
    _queueSave(n, { linked_bmps: [...e.linked_bmps] });
    _updateListItem(n);
  }
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
      // Optimistically remove back-reference from peer in memory;
      // also flush the peer's custom_sections so configs survive unlinking
      const peer = spritesByBmp[n];
      if (peer) {
        peer.linked_bmps = (peer.linked_bmps || []).filter(x => x !== e.bmp_num);
        _updateListItem(n);
        // Persist peer's section config so it isn't lost when no longer viewed as linked
        if (peer.custom_sections !== undefined) {
          _saveCustomSections(peer);
        }
      }
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
      // Re-render frames + rebuild list deduplication
      _buildCanonicalMap();
      _buildList();
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
    // Merge both groups transitively so all members link to all others
    const merged = new Set([..._collectGroup(e.bmp_num), ..._collectGroup(n)]);
    _setGroupLinks(merged);

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
    // Rebuild canonical map + list so the new peer disappears as a separate entry
    _buildCanonicalMap();
    _buildList();
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
      type:            e.type,
      name:            e.name             || '',
      linked_bmps:     e.linked_bmps      || [],
      notes:           e.notes            || '',
      item_index:      e.item_index       ?? null,
      custom_sections: JSON.parse(JSON.stringify(e.custom_sections || [])),
      pattern_name:    e.pattern_name     || null,
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

    // Merge server-returned bidirectional links back into local catalog.
    // The server maintains back-references, so we must sync ALL entries
    // (not just the ones we sent) and refresh their sidebar list items.
    const metaR = await fetch(`/api/sprite-metadata?game=${state.game}`);
    if (metaR.ok) {
      const metaFull = await metaR.json();
      const needsListUpdate = new Set(Object.keys(toSend).map(Number));
      Object.entries(metaFull).forEach(([k, v]) => {
        const n = Number(k);
        const e = spritesByBmp[n];
        if (e && v) {
          if (v.type)                    e.type            = v.type;
          if (v.name != null)            e.name            = v.name;
          if (v.linked_bmps)             e.linked_bmps     = v.linked_bmps;
          if (v.notes != null)           e.notes           = v.notes;
          if ('item_index' in v)         e.item_index      = v.item_index;
          if (v.custom_sections != null) e.custom_sections = v.custom_sections;
          if ('pattern_name' in v)       e.pattern_name    = v.pattern_name;
          needsListUpdate.add(n);
        }
      });
      // Also clear linked_bmps for entries not in metadata (back-ref removed)
      needsListUpdate.forEach(n => {
        const e = spritesByBmp[n];
        if (e && !(String(n) in metaFull)) e.linked_bmps = [];
      });
      needsListUpdate.forEach(n => _updateListItem(n));
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
  const s = (scale ?? prefs.spriteScale);
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
