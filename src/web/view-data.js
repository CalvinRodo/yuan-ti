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
let monsterLoreCache  = null;   // array from /api/monster-lore
let selectedMonsterId = null;   // rdff_id of the currently selected monster

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
  dataCache         = { items: null, monsters: null, spells: null };
  dataSortCol       = null;
  dataSortDir       = 1;
  monsterLoreCache  = null;
  selectedMonsterId = null;
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
    </div>
    <div id="monster-browser" class="hidden">
      <div id="monster-list-wrap">
        <ul id="monster-list"></ul>
      </div>
      <div id="monster-detail-wrap">
        <div id="monster-detail"><p class="mm-empty">&#8678; Select a monster from the list</p></div>
      </div>
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
    if (dataActiveTab === 'monsters') {
      _buildMonsterList();
    } else {
      _renderTable();
    }
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
  const isMonsters = tab === 'monsters';
  _showMonsterBrowser(isMonsters);

  if (!isMonsters) {
    if (dataCache[tab] !== null) { _renderTable(); return; }
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
    return;
  }

  // --- Monster browser: fetch game data + lore in parallel ---
  if (dataCache.monsters !== null && monsterLoreCache !== null) {
    _buildMonsterList();
    return;
  }
  _showMonsterDetailMsg('<p class="mm-empty"><span class="spinner" style="width:18px;height:18px;border-width:2px"></span>&nbsp;Loading…</p>');
  try {
    const pending = [];
    if (dataCache.monsters === null)
      pending.push(
        fetch(`/api/monsters?game=${state.game}`)
          .then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); })
          .then(d => { dataCache.monsters = d; })
      );
    if (monsterLoreCache === null)
      pending.push(
        fetch(`/api/monster-lore?game=${state.game}`)
          .then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); })
          .then(d => { monsterLoreCache = d; })
      );
    await Promise.all(pending);
  } catch (e) {
    _showMonsterDetailMsg(`<p class="mm-empty" style="color:#c87f7f">Failed to load: ${e.message}</p>`);
    return;
  }
  _buildMonsterList();
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

// ---------------------------------------------------------------------------
// Monster browser
// ---------------------------------------------------------------------------

/** Toggle between table view (items/spells) and monster browser. */
function _showMonsterBrowser(on) {
  const browser   = document.getElementById('monster-browser');
  const tableWrap = document.getElementById('data-table-wrap');
  if (browser)   browser.classList.toggle('hidden', !on);
  if (tableWrap) tableWrap.classList.toggle('hidden', on);
}

/** Set raw HTML inside the detail panel (used for loading/error states). */
function _showMonsterDetailMsg(html) {
  const det = document.getElementById('monster-detail');
  if (det) det.innerHTML = html;
}

/** Escape a string for safe insertion as HTML text. */
function _esc(str) {
  if (str == null) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/** Normalise a monster name for fuzzy matching. */
function _normName(name) {
  return (name || '')
    .toLowerCase()
    .replace(/\([^)]*\)/g, '')      // strip parentheticals
    .replace(/[^a-z0-9 ]/g, ' ')   // non-alphanumeric → space
    .replace(/\s+/g, ' ')
    .trim();
}

/** Find the best lore entry for a given game-extracted monster name. */
function _matchMonsterLore(gameName) {
  if (!monsterLoreCache || !monsterLoreCache.length) return null;
  const norm = _normName(gameName);
  // 1. Exact
  let found = monsterLoreCache.find(e => _normName(e.name) === norm);
  if (found) return found;
  // 2. One starts with the other
  found = monsterLoreCache.find(e => {
    const en = _normName(e.name);
    return en.startsWith(norm) || norm.startsWith(en);
  });
  if (found) return found;
  // 3. Any significant word match (length > 3)
  const words = norm.split(' ').filter(w => w.length > 3);
  if (words.length) {
    found = monsterLoreCache.find(e => {
      const en = _normName(e.name);
      return words.some(w => en.includes(w));
    });
  }
  return found || null;
}

/** Build / rebuild the left-panel monster list (applying active filter). */
function _buildMonsterList() {
  const ul = document.getElementById('monster-list');
  if (!ul) return;
  const q    = dataFilter.trim();
  const data = (dataCache.monsters || []).filter(
    m => !q || m.name.toLowerCase().includes(q)
  );

  ul.innerHTML = '';
  const frag = document.createDocumentFragment();

  data.forEach(m => {
    const li = document.createElement('li');
    li.className = 'monster-list-item' + (m.rdff_id === selectedMonsterId ? ' active' : '');
    li.dataset.id = m.rdff_id;

    // Icon
    const iconDiv = document.createElement('div');
    iconDiv.className = 'monster-list-icon';
    if (m.bmp_num && m.sprite_gff) {
      const img = document.createElement('img');
      img.src   = `/api/gff/${encodeURIComponent(m.sprite_gff)}/image/${m.sprite_tag_url}/${m.bmp_num}?frame=0&game=${state.game}`;
      img.alt   = '';
      img.onerror = () => { img.style.display = 'none'; };
      iconDiv.appendChild(img);
    }
    li.appendChild(iconDiv);

    // Name
    const nameSpan = document.createElement('span');
    nameSpan.className = 'monster-list-name';
    nameSpan.textContent = m.name;
    li.appendChild(nameSpan);

    // Badge if lore has detailed stats/narrative
    const lore = _matchMonsterLore(m.name);
    if (lore && lore.details) {
      const badge = document.createElement('span');
      badge.className = 'monster-list-badge';
      badge.title     = 'Has detailed manual entry';
      badge.textContent = '\u{1F4D6}';
      li.appendChild(badge);
    }

    li.addEventListener('click', () => _selectMonster(m));
    frag.appendChild(li);
  });

  if (data.length === 0) {
    const li = document.createElement('li');
    li.style.cssText = 'padding:12px 8px;color:var(--text-dim);font-size:12px;';
    li.textContent   = q ? `No results for "${q}"` : 'No monsters found.';
    frag.appendChild(li);
  }

  ul.appendChild(frag);

  // Restore selection if still visible
  if (selectedMonsterId !== null) {
    const m = data.find(x => x.rdff_id === selectedMonsterId);
    if (m) { _selectMonster(m, false); return; }
  }
  // Auto-select first if none selected
  if (selectedMonsterId === null && data.length > 0) {
    _selectMonster(data[0], false);
  }
}

/** Select a monster entry and render its detail panel. */
function _selectMonster(gameEntry, scrollToTop = true) {
  selectedMonsterId = gameEntry.rdff_id;
  // Update active highlight
  const ul = document.getElementById('monster-list');
  if (ul) {
    ul.querySelectorAll('.monster-list-item').forEach(li =>
      li.classList.toggle('active', Number(li.dataset.id) === gameEntry.rdff_id)
    );
  }
  const lore = _matchMonsterLore(gameEntry.name);
  _renderMonsterDetail(gameEntry, lore);
  if (scrollToTop) {
    const wrap = document.getElementById('monster-detail-wrap');
    if (wrap) wrap.scrollTop = 0;
  }
}

/**
 * Parse and render the psionics block, handling the three data-quality tiers
 * produced by the two-column PDF OCR:
 *  1. Clean  — normal lines (Screamer Beetle)
 *  2. Mixed  — column bleed on each line (Daggoran, Psurlon)
 *  3. Garbled — stat table extracted char-by-char (Dagolar Slime, Mindhome)
 */
function _buildPsionicsHtml(raw) {
  if (!raw || !raw.trim()) return '';

  const allLines  = raw.split('\n');
  const nonEmpty  = allLines.filter(l => l.trim().length > 0);
  if (nonEmpty.length === 0) return '';

  const shortCount = nonEmpty.filter(l => l.trim().length <= 3).length;
  const isGarbled  = nonEmpty.length > 5 && (shortCount / nonEmpty.length) > 0.4;

  let statRows  = [];   // Level / Dis/Sci/Dev header rows
  let discLines = [];   // Discipline description text lines

  if (isGarbled) {
    // --- Tier 3: reconstruct level stats from joined char fragments ---
    const joined = nonEmpty.map(l => l.trim()).join('');
    // Extract Level, Dis/Sci/Dev, Score, PSPs from the noise
    const lvl = (joined.match(/Le(\d+)vel/) || joined.match(/Le.*?vel.*?(\d+)/))?.[1];
    const dsd = joined.match(/Dis.*?(\d+).*?Sci.*?(\d+).*?Dev.*?(\d+)/) || joined.match(/(\d+)\/(\d+)\/(\d+)/);
    // Score is usually preceded by "Sc" or "re" fragments; PSPs follow
    const scMatch = joined.match(/Sc.*?(\d{2}).*?P.*?S.*?(\d{1,3})Ps/);
    if (lvl || dsd) {
      statRows.push('Level  Dis/Sci/Dev  Score  PSPs');
      const level  = lvl    || '?';
      const dsdStr = dsd    ? `${dsd[1]}/${dsd[2]}/${dsd[3]}` : '?';
      const score  = scMatch ? scMatch[1] : '?';
      const psps   = scMatch ? scMatch[2] : '?';
      statRows.push(`${level}  ${dsdStr}  ${score}  ${psps}`);
    }
    // Keep any lines that contain readable discipline keywords
    discLines = allLines.filter(l => {
      const t = l.trim();
      return t.length > 8 &&
        /sciences:|devotions:|telepathy|psychokinesis|psychoportation|psychometabolism|clairsentience|metapsionics/i.test(t);
    });
  } else {
    // --- Tier 1 / 2: normal lines or moderate column bleed ---
    let pastHeader = false;
    for (const line of allLines) {
      const t = line.trim();
      if (!t) continue;
      if (!pastHeader) {
        if (/^level/i.test(t) || /^\d/.test(t) || /^adept/i.test(t)) {
          statRows.push(t);
          continue;
        }
        if (statRows.length > 0) pastHeader = true;
      }
      // Filter short right-column bleed lines (no useful keywords, just OCR noise)
      const hasKeyword = /sciences:|devotions:|telepathy|psycho|clairsentience|metapsionics|psurlon|also have/i.test(t);
      if (t.length > 7 || hasKeyword) discLines.push(t);
    }
    // Also strip trailing page numbers from last line
    if (discLines.length) {
      discLines[discLines.length - 1] = discLines[discLines.length - 1].replace(/\s+\d+\s*$/, '');
    }
  }

  // --- Render stat summary table ---
  let html = '';
  if (statRows.length > 0) {
    html += '<table class="mm-psi-table"><tbody>';
    statRows.forEach((row, idx) => {
      const isHdr = idx === 0 && /^level/i.test(row);
      const cols  = row.split(/  +/).filter(Boolean);
      const tag   = isHdr ? 'th' : 'td';
      html += '<tr>' + cols.map(c => `<${tag}>${_esc(c.trim())}</${tag}>`).join('') + '</tr>';
    });
    html += '</tbody></table>';
  }

  // --- Render discipline descriptions ---
  if (discLines.length > 0) {
    const disc = discLines.map(l => l.trim()).join(' ')
      .replace(/\s{3,}/g, '  ').replace(/\s+$/g, '').trim();

    const kwRe = /(?=\b(?:Telepathy|Psychokinesis|Psychoportation|Psychometabolism|Clairsentience|Metapsionics)\s*[\u2014\-])/g;
    const segs  = disc.split(kwRe).filter(s => s.trim().length > 0);

    html += '<div class="mm-psi-discs">';
    if (segs.length > 1) {
      segs.forEach(seg => {
        const m = seg.match(/^(\w+)\s*[\u2014\-]\s*([\s\S]+)/);
        if (m) {
          html += `<div class="mm-psi-disc"><span class="mm-psi-dname">${_esc(m[1])}</span>${_esc(' \u2014 ' + m[2].trim())}</div>`;
        } else {
          html += `<div class="mm-psi-disc">${_esc(seg.trim())}</div>`;
        }
      });
    } else {
      html += `<div class="mm-psi-disc">${_esc(disc)}</div>`;
    }
    html += '</div>';
  }

  if (!html) {
    html = '<p style="color:var(--text-dim);font-style:italic;font-size:12px">Psionic creature (stat table not recoverable from manual scan)</p>';
  }
  return html;
}

/** Render a D&D Monster Manual-style detail entry for the selected monster. */
function _renderMonsterDetail(g, lore) {
  const det = document.getElementById('monster-detail');
  if (!det) return;

  const lstats    = lore?.details?.stats     || {};
  const narrative = lore?.details?.narrative || {};
  const psionics  = lore?.details?.psionics  || null;

  // Sprite URL
  const spriteUrl = (g.bmp_num && g.sprite_gff)
    ? `/api/gff/${encodeURIComponent(g.sprite_gff)}/image/${g.sprite_tag_url}/${g.bmp_num}?frame=0&game=${state.game}`
    : null;

  // Helper: render a stat value, annotating game-extracted data when it differs
  function statCell(manualVal, gameVal) {
    const hasM = manualVal != null && String(manualVal).trim().length > 0;
    const hasG = gameVal   != null && String(gameVal).trim().length   > 0;
    if (!hasM && !hasG) return '&mdash;';
    if (!hasM) return `<span class="mm-in-game-val">${_esc(String(gameVal))}</span><span class="mm-game-badge" title="Value from game data">game</span>`;
    const mStr = String(manualVal).trim();
    let html = _esc(mStr);
    if (hasG) {
      const gStr  = String(gameVal).trim();
      const mNum  = parseFloat(mStr);
      const gNum  = parseFloat(gStr);
      const diff  = isNaN(mNum) ? (mStr.toLowerCase() !== gStr.toLowerCase()) : (mNum !== gNum);
      if (diff) {
        html += ` <span class="mm-game-badge mm-differs" title="Game value: ${_esc(gStr)}">${_esc(gStr)}</span>`;
      }
    }
    return html;
  }

  // Stat block definitions: [label, manualStatKey, gameVal]
  const block1 = [
    ['No. Appearing',   lstats.number_appearing,  null],
    ['Armor Class',     lstats.armor_class,        g.ac],
    ['Movement',        lstats.movement,           g.move],
    ['Hit Dice',        lstats.hit_dice,           null],
    ['THAC0',           lstats.thac0,              g.thac0],
    ['No. of Attacks',  lstats.attacks,            null],
    ['Damage/Attack',   lstats.damage,             null],
    ['Special Attacks', lstats.special_attacks,    null],
    ['Spec. Defenses',  lstats.special_defenses,   null],
    ['Magic Resistance',lstats.magic_resistance,   null],
    ['Size',            lstats.size,               null],
    ['Morale',          lstats.morale,             null],
    ['XP Value',        lstats.xp_value,           null],
  ];
  const block2 = [
    ['Frequency',       lstats.frequency,          null],
    ['Organization',    lstats.organization,       null],
    ['Activity Cycle',  lstats.activity_cycle,     null],
    ['Diet',            lstats.diet,               null],
    ['Intelligence',    lstats.intelligence,       g.int],
    ['Treasure',        lstats.treasure,           null],
    ['Alignment',       lstats.alignment,          null],
    ['Climate/Terrain', lstats.climate_terrain,    null],
  ];

  function buildStatTable(rows) {
    const active = rows.filter(([, mv, gv]) =>
      (mv != null && String(mv).trim()) ||
      (gv != null && String(gv).trim())
    );
    if (!active.length) return '';
    return `<table class="mm-stat-block"><tbody>${
      active.map(([label, mv, gv]) =>
        `<tr><th>${_esc(label)}</th><td>${statCell(mv, gv)}</td></tr>`
      ).join('')
    }</tbody></table>`;
  }

  // Clean garbled OCR text into a readable paragraph
  function cleanText(raw) {
    if (!raw || !raw.trim()) return '';
    return raw.replace(/\n/g, ' ').replace(/\s{2,}/g, ' ').trim();
  }

  function buildSection(title, raw) {
    const text = cleanText(raw);
    if (text.length < 25) return '';   // too short / garbled
    return `<div class="mm-narrative-section"><h4>${_esc(title)}</h4><p>${_esc(text)}</p></div>`;
  }

  // In-game ability scores grid
  const abilities = [
    ['HP',    g.hp],  ['PSP',  g.psp], ['AC',   g.ac],
    ['THAC0', g.thac0], ['Move', g.move],
    ['STR',   g.str],  ['DEX',  g.dex], ['CON',  g.con],
    ['INT',   g.int],  ['WIS',  g.wis], ['CHA',  g.cha],
  ].filter(([, v]) => v != null);

  const abilityGrid = abilities.map(([label, val]) =>
    `<div class="mm-ability-cell"><span class="mm-ability-label">${label}</span><span class="mm-ability-val">${val}</span></div>`
  ).join('');

  const hasDetailedStats = Object.keys(lstats).length > 0;
  const hasNarrative     = Object.keys(narrative).length > 0;
  const simpleDesc       = cleanText(lore?.description || '');

  det.innerHTML = `
<div class="mm-entry">
  <div class="mm-header">
    <div class="mm-title-block">
      <h2 class="mm-name">${_esc(g.name)}</h2>
      ${ lore?.section ? `<div class="mm-section-tag">${_esc(lore.section)}</div>` : '' }
    </div>
    ${ spriteUrl ? `<img class="mm-sprite" src="${spriteUrl}" alt="" onerror="this.style.display='none'">` : '' }
  </div>

  ${ abilities.length ? `
  <div class="mm-in-game-stats">
    <h4>In-Game Statistics</h4>
    <div class="mm-ability-grid">${abilityGrid}</div>
  </div>` : '' }

  ${ hasDetailedStats ? buildStatTable(block1) + buildStatTable(block2) : '' }

  ${ psionics ? `<div class="mm-narrative-section"><h4>Psionic Summary</h4><div class="mm-psi-wrap">${_buildPsionicsHtml(psionics)}</div></div>` : '' }

  <div class="mm-narrative">
    ${ hasNarrative
        ? buildSection('Appearance',      narrative.appearance)
        + buildSection('Combat',          narrative.combat)
        + buildSection('Habitat / Society', narrative.habitat_society)
        + buildSection('Ecology',         narrative.ecology)
        : ( simpleDesc.length > 20
            ? `<div class="mm-narrative-section"><p>${_esc(simpleDesc)}</p></div>`
            : '<p class="mm-empty" style="padding:20px 0">No manual description available.</p>'
          )
    }
  </div>
</div>`;
}
