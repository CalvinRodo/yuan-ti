/* ================================================================
   Dark Sun GFF Viewer — view-data.js
   Game data browser: Items, Monsters, Spells
   ================================================================ */

'use strict';

// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------
let dataViewBuilt  = false;
let dataActiveTab  = 'items';   // 'items' | 'monsters' | 'spells'
let dataFilter     = '';
let dataCache      = { items: null, monsters: null, spells: null };
let dataSortCol    = null;
let dataSortDir    = 1; // 1 = asc, -1 = desc

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------
function renderDataView() {
  const view = document.getElementById('data-view');
  if (!view) return;
  view.classList.remove('hidden');
  if (!dataViewBuilt) {
    _buildDataViewDOM(view);
    dataViewBuilt = true;
  }
  // Clear cache if game changed (handled by refreshGameData resetting dataCache)
  _loadTab(dataActiveTab);
}

function teardownDataView() {
  dataCache  = { items: null, monsters: null, spells: null };
  dataSortCol = null;
  dataSortDir = 1;
  // Do not reset dataViewBuilt — DOM can stay; we just flush per-game data
}

// ---------------------------------------------------------------------------
// DOM builder (runs once per page load)
// ---------------------------------------------------------------------------
function _buildDataViewDOM(view) {
  view.innerHTML = `
    <div id="data-toolbar">
      <div id="data-tabs">
        <button class="data-tab-btn active" data-tab="items">&#9876; Items</button>
        <button class="data-tab-btn"        data-tab="monsters">&#9812; Monsters</button>
        <button class="data-tab-btn"        data-tab="spells">&#10022; Spells</button>
      </div>
      <div id="data-search-wrap">
        <input id="data-search" type="search" placeholder="Filter&#8230;" autocomplete="off" spellcheck="false">
      </div>
    </div>
    <div id="data-table-wrap">
      <div id="data-spinner" class="data-spinner hidden">
        <div class="spinner"></div><span>Loading&#8230;</span>
      </div>
      <div id="data-error" class="data-error hidden"></div>
      <table id="data-table">
        <thead id="data-thead"><tr></tr></thead>
        <tbody id="data-tbody"></tbody>
      </table>
    </div>`;

  // Tab buttons
  view.querySelectorAll('.data-tab-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      dataActiveTab = btn.dataset.tab;
      dataFilter    = '';
      dataSortCol   = null;
      dataSortDir   = 1;
      document.getElementById('data-search').value = '';
      view.querySelectorAll('.data-tab-btn').forEach(b =>
        b.classList.toggle('active', b === btn));
      _loadTab(dataActiveTab);
    });
  });

  // Search input
  const searchEl = view.querySelector('#data-search');
  searchEl.addEventListener('input', () => {
    dataFilter = searchEl.value.toLowerCase();
    _renderTable();
  });
}

// ---------------------------------------------------------------------------
// Tab definition: columns and row-builder
// ---------------------------------------------------------------------------
const _TAB_DEFS = {
  items: {
    cols: [
      { key: '_sprite',    label: '',         sortType: null,  nosort: true, img: true },
      { key: 'name',       label: 'Name',     sortType: 'str' },
      { key: 'type',       label: 'Type',     sortType: 'str' },
      { key: 'damage',     label: 'Damage',   sortType: 'str' },
      { key: 'base_AC',    label: 'AC Bonus', sortType: 'num' },
      { key: 'num_attacks',label: 'Attacks',  sortType: 'num' },
      { key: 'range',      label: 'Range',    sortType: 'num' },
      { key: 'weight',     label: 'Weight',   sortType: 'num' },
      { key: 'material',   label: 'Material', sortType: 'num' },
    ],
    rowData: row => [
      row.port_num
        ? `/api/gff/${encodeURIComponent(row.sprite_gff)}/image/${row.sprite_tag_url}/${row.port_num}?frame=0&game=${state.game}`
        : null,
      row.name      || '—',
      row.type      || '—',
      row.damage    || '—',
      row.base_AC   != null ? row.base_AC   : '—',
      row.num_attacks != null ? row.num_attacks : '—',
      row.range     != null ? row.range     : '—',
      row.weight    != null ? row.weight    : '—',
      row.material  != null ? row.material  : '—',
    ],
    filterRow: (row, q) =>
      (row.name  || '').toLowerCase().includes(q) ||
      (row.type  || '').toLowerCase().includes(q) ||
      (row.damage || '').toLowerCase().includes(q),
  },

  monsters: {
    cols: [
      { key: '_sprite',     label: '',        sortType: null,  nosort: true, img: true },
      { key: 'name',        label: 'Name',    sortType: 'str' },
      { key: 'hp',          label: 'HP',      sortType: 'num' },
      { key: 'psp',         label: 'PSP',     sortType: 'num' },
      { key: 'ac',          label: 'AC',      sortType: 'num' },
      { key: 'thac0',       label: 'THAC0',   sortType: 'num' },
      { key: 'move',        label: 'Move',    sortType: 'num' },
      { key: 'str',         label: 'STR',     sortType: 'num' },
      { key: 'dex',         label: 'DEX',     sortType: 'num' },
      { key: 'con',         label: 'CON',     sortType: 'num' },
      { key: 'int',         label: 'INT',     sortType: 'num' },
      { key: 'wis',         label: 'WIS',     sortType: 'num' },
      { key: 'cha',         label: 'CHA',     sortType: 'num' },
    ],
    rowData: row => [
      row.bmp_num && row.sprite_gff
        ? `/api/gff/${encodeURIComponent(row.sprite_gff)}/image/${row.sprite_tag_url}/${row.bmp_num}?frame=0&game=${state.game}`
        : null,
      row.name  || '—',
      row.hp    != null ? row.hp    : '—',
      row.psp   != null ? row.psp   : '—',
      row.ac    != null ? row.ac    : '—',
      row.thac0 != null ? row.thac0 : '—',
      row.move  != null ? row.move  : '—',
      row.str   != null ? row.str   : '—',
      row.dex   != null ? row.dex   : '—',
      row.con   != null ? row.con   : '—',
      row.int   != null ? row.int   : '—',
      row.wis   != null ? row.wis   : '—',
      row.cha   != null ? row.cha   : '—',
    ],
    filterRow: (row, q) =>
      (row.name || '').toLowerCase().includes(q),
  },

  spells: {
    cols: [
      { key: '_sprite', label: '',           sortType: null, nosort: true, img: true },
      { key: 'number',  label: '#',          sortType: 'num' },
      { key: 'name',    label: 'Name',       sortType: 'str' },
      { key: 'desc',    label: 'Description',sortType: 'str', wide: true },
    ],
    rowData: row => [
      row.icon_number != null && row.sprite_gff
        ? `/api/gff/${encodeURIComponent(row.sprite_gff)}/image/${row.sprite_tag_url}/${row.icon_number}?frame=0&game=${state.game}`
        : null,
      row.number != null ? row.number : '—',
      row.name   || '—',
      row.desc   || '—',
    ],
    filterRow: (row, q) =>
      (row.name || '').toLowerCase().includes(q) ||
      (row.desc || '').toLowerCase().includes(q),
  },
};

// ---------------------------------------------------------------------------
// Data loading
// ---------------------------------------------------------------------------
async function _loadTab(tab) {
  if (dataCache[tab] !== null) {
    _renderTable();
    return;
  }
  _showSpinner(true);
  _showError(null);
  try {
    const r = await fetch(`/api/${tab}?game=${state.game}`);
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    dataCache[tab] = await r.json();
  } catch (e) {
    _showSpinner(false);
    _showError(`Failed to load ${tab}: ${e.message}`);
    return;
  }
  _showSpinner(false);
  _renderTable();
}

// ---------------------------------------------------------------------------
// Table rendering
// ---------------------------------------------------------------------------
function _renderTable() {
  const def  = _TAB_DEFS[dataActiveTab];
  const data = dataCache[dataActiveTab];
  if (!def || !data) return;

  // Build header (once per tab switch — detected by checking col count)
  const thead = document.getElementById('data-thead');
  const existingCols = thead.querySelectorAll('th').length;
  if (existingCols !== def.cols.length) {
    const tr = document.createElement('tr');
    def.cols.forEach((col, i) => {
      const th = document.createElement('th');
      th.textContent = col.label;
      if (col.wide)   th.classList.add('data-col-wide');
      if (col.img)    th.classList.add('data-col-sprite-hdr');
      if (!col.nosort) {
        th.dataset.colIdx = i;
        th.addEventListener('click', () => _sortBy(i));
      }
      tr.appendChild(th);
    });
    thead.replaceChildren(tr);
  }

  // Update sort indicators
  thead.querySelectorAll('th').forEach((th, i) => {
    th.classList.remove('sort-asc', 'sort-desc');
    if (dataSortCol === i) th.classList.add(dataSortDir === 1 ? 'sort-asc' : 'sort-desc');
  });

  // Filter
  const q = dataFilter.trim();
  let rows = q ? data.filter(row => def.filterRow(row, q)) : data;

  // Sort (skip sprite column)
  if (dataSortCol !== null && !def.cols[dataSortCol].nosort) {
    const col = def.cols[dataSortCol];
    rows = [...rows].sort((a, b) => {
      const va = def.rowData(a)[dataSortCol];
      const vb = def.rowData(b)[dataSortCol];
      if (col.sortType === 'num') {
        return dataSortDir * ((va === '—' || va == null ? Infinity : Number(va)) - (vb === '—' || vb == null ? Infinity : Number(vb)));
      }
      return dataSortDir * String(va).localeCompare(String(vb));
    });
  }

  // Render rows
  const tbody = document.getElementById('data-tbody');
  tbody.innerHTML = '';
  const frag = document.createDocumentFragment();
  rows.forEach(row => {
    const tr = document.createElement('tr');
    def.rowData(row).forEach((cell, i) => {
      const td = document.createElement('td');
      const colDef = def.cols[i];
      if (colDef.img) {
        // Sprite thumbnail cell
        td.classList.add('data-col-sprite');
        if (cell) {
          const img = document.createElement('img');
          img.src   = cell;
          img.alt   = '';
          img.className = 'data-sprite-img';
          img.onerror = () => { img.style.display = 'none'; };
          td.appendChild(img);
        }
      } else {
        td.textContent = cell;
        if (colDef.wide) td.classList.add('data-col-wide');
      }
      tr.appendChild(td);
    });
    frag.appendChild(tr);
  });
  tbody.appendChild(frag);

  // Show table visibility
  const table = document.getElementById('data-table');
  table.classList.toggle('hidden', rows.length === 0);
  if (rows.length === 0 && data.length > 0) {
    _showError(`No results for "${q}"`);
  } else {
    _showError(null);
  }
}

function _sortBy(colIdx) {
  if (dataSortCol === colIdx) {
    dataSortDir = -dataSortDir;
  } else {
    dataSortCol = colIdx;
    dataSortDir = 1;
  }
  _renderTable();
}

// ---------------------------------------------------------------------------
// UI helpers
// ---------------------------------------------------------------------------
function _showSpinner(on) {
  const el = document.getElementById('data-spinner');
  if (el) el.classList.toggle('hidden', !on);
  const table = document.getElementById('data-table');
  if (table) table.classList.toggle('hidden', on);
}

function _showError(msg) {
  const el = document.getElementById('data-error');
  if (!el) return;
  if (msg) {
    el.textContent = msg;
    el.classList.remove('hidden');
  } else {
    el.classList.add('hidden');
  }
}
