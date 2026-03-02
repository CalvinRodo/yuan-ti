/* ================================================================
   DataScene — Game data browser: Items, Monsters, Spells tables.
   Monsters tab shows a master-detail browser with lore + stats.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';
import { showDomPanel, htmlEsc } from '../utils.js';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function _esc(str) {
  if (str == null) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function _normName(name) {
  return (name || '')
    .toLowerCase()
    .replace(/\([^)]*\)/g, '')
    .replace(/[^a-z0-9 ]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function _matchMonsterLore(gameName, loreCache) {
  if (!loreCache || !loreCache.length) return null;
  const norm = _normName(gameName);
  let found = loreCache.find(e => _normName(e.name) === norm);
  if (found) return found;
  found = loreCache.find(e => {
    const en = _normName(e.name);
    return en.startsWith(norm) || norm.startsWith(en);
  });
  if (found) return found;
  const words = norm.split(' ').filter(w => w.length > 3);
  if (words.length) {
    found = loreCache.find(e => {
      const en = _normName(e.name);
      return words.some(w => en.includes(w));
    });
  }
  return found || null;
}

function _buildPsionicsHtml(raw) {
  if (!raw || !raw.trim()) return '';
  const allLines = raw.split('\n');
  const nonEmpty = allLines.filter(l => l.trim().length > 0);
  if (nonEmpty.length === 0) return '';

  const shortCount = nonEmpty.filter(l => l.trim().length <= 3).length;
  const isGarbled  = nonEmpty.length > 5 && (shortCount / nonEmpty.length) > 0.4;

  let statRows  = [];
  let discLines = [];

  if (isGarbled) {
    const joined = nonEmpty.map(l => l.trim()).join('');
    const lvl = (joined.match(/Le(\d+)vel/) || joined.match(/Le.*?vel.*?(\d+)/))?.[1];
    const dsd = joined.match(/Dis.*?(\d+).*?Sci.*?(\d+).*?Dev.*?(\d+)/) || joined.match(/(\d+)\/(\d+)\/(\d+)/);
    const scMatch = joined.match(/Sc.*?(\d{2}).*?P.*?S.*?(\d{1,3})Ps/);
    if (lvl || dsd) {
      statRows.push('Level  Dis/Sci/Dev  Score  PSPs');
      const level  = lvl    || '?';
      const dsdStr = dsd    ? `${dsd[1]}/${dsd[2]}/${dsd[3]}` : '?';
      const score  = scMatch ? scMatch[1] : '?';
      const psps   = scMatch ? scMatch[2] : '?';
      statRows.push(`${level}  ${dsdStr}  ${score}  ${psps}`);
    }
    discLines = allLines.filter(l => {
      const t = l.trim();
      return t.length > 8 &&
        /sciences:|devotions:|telepathy|psychokinesis|psychoportation|psychometabolism|clairsentience|metapsionics/i.test(t);
    });
  } else {
    let pastHeader = false;
    for (const line of allLines) {
      const t = line.trim();
      if (!t) continue;
      if (!pastHeader) {
        if (/^level/i.test(t) || /^\d/.test(t) || /^adept/i.test(t)) {
          statRows.push(t); continue;
        }
        if (statRows.length > 0) pastHeader = true;
      }
      const hasKeyword = /sciences:|devotions:|telepathy|psycho|clairsentience|metapsionics|psurlon|also have/i.test(t);
      if (t.length > 7 || hasKeyword) discLines.push(t);
    }
    if (discLines.length) {
      discLines[discLines.length - 1] = discLines[discLines.length - 1].replace(/\s+\d+\s*$/, '');
    }
  }

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
  if (discLines.length > 0) {
    const disc = discLines.map(l => l.trim()).join(' ')
      .replace(/\s{3,}/g, '  ').replace(/\s+$/g, '').trim();
    const kwRe = /(?=\b(?:Telepathy|Psychokinesis|Psychoportation|Psychometabolism|Clairsentience|Metapsionics)\s*[\u2014\-])/g;
    const segs  = disc.split(kwRe).filter(s => s.trim().length > 0);
    html += '<div class="mm-psi-discs">';
    if (segs.length > 1) {
      segs.forEach(seg => {
        const m = seg.match(/^(\w+)\s*[\u2014\-]\s*([\s\S]+)/);
        if (m) html += `<div class="mm-psi-disc"><span class="mm-psi-dname">${_esc(m[1])}</span>${_esc(' \u2014 ' + m[2].trim())}</div>`;
        else   html += `<div class="mm-psi-disc">${_esc(seg.trim())}</div>`;
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

// ---------------------------------------------------------------------------
// Tab definitions (items / spells — monsters has its own layout)
// ---------------------------------------------------------------------------
const TAB_DEFS = {
  items: {
    cols: ['', 'Name', 'Type', 'Damage', 'AC Bonus', 'Attacks', 'Range', 'Weight'],
    fetch: () => api.fetchItems(),
    filterRow: (row, q) => (row.name || '').toLowerCase().includes(q) || (row.type || '').toLowerCase().includes(q),
    rowCells: (row) => {
      const img = row.port_num != null && row.sprite_gff
        ? `<img src="${api.imageUrl(row.sprite_gff, row.sprite_tag_url || 'PORT', row.port_num, 0)}" alt="">`
        : '';
      return [
        { html: img, cls: 'img-cell' },
        { text: row.name || '\u2014' },
        { text: row.type || '\u2014' },
        { text: row.damage || '\u2014' },
        { text: row.base_AC != null ? row.base_AC : '\u2014' },
        { text: row.num_attacks != null ? row.num_attacks : '\u2014' },
        { text: row.range != null ? row.range : '\u2014' },
        { text: row.weight != null ? row.weight : '\u2014' },
      ];
    },
  },
  spells: {
    cols: ['', '#', 'Name', 'Description'],
    fetch: () => api.fetchDataSpells(),
    filterRow: (row, q) => (row.name || '').toLowerCase().includes(q) || (row.desc || '').toLowerCase().includes(q),
    rowCells: (row) => {
      const img = row.icon_number != null && row.sprite_gff
        ? `<img src="${api.imageUrl(row.sprite_gff, row.sprite_tag_url || 'ICON', row.icon_number, 0)}" alt="">`
        : '';
      return [
        { html: img, cls: 'img-cell' },
        { text: row.number ?? '' },
        { text: row.name || '\u2014' },
        { text: (row.desc || row.description || '').slice(0, 100) },
      ];
    },
  },
};

// ---------------------------------------------------------------------------
// Scene
// ---------------------------------------------------------------------------
export default class DataScene extends Phaser.Scene {
  constructor() {
    super('DataScene');
    this._built = false;
    this._tab = 'items';
    this._filter = '';
    this._cache = { items: null, monsters: null, spells: null };
    this._loreCache = null;
    this._selectedId = null;
    this._game = null;
    this._panel = null;
  }

  create() {
    const panel = showDomPanel('DataScene');
    this._panel = panel;
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');

    if (this._game !== state.game) {
      this._game = state.game;
      this._cache = { items: null, monsters: null, spells: null };
      this._loreCache = null;
      this._selectedId = null;
    }
    this._loadTab(panel);
  }

  shutdown() {}

  // -------------------------------------------------------------------------
  // DOM construction
  // -------------------------------------------------------------------------
  _build(panel) {
    panel.innerHTML = `
      <div class="data-view">
        <div class="data-toolbar">
          <div class="data-tabs">
            <button class="data-tab-btn active" data-tab="items">&#9876; Items</button>
            <button class="data-tab-btn" data-tab="monsters">&#9812; Monsters</button>
            <button class="data-tab-btn" data-tab="spells">&#10022; Spells</button>
          </div>
          <div class="data-search-wrap">
            <input type="search" placeholder="Filter\u2026" autocomplete="off" spellcheck="false">
          </div>
        </div>

        <!-- Table view (items / spells) -->
        <div class="data-table-wrap">
          <div class="data-spinner hidden"><div class="spinner"></div><span>Loading\u2026</span></div>
          <table class="data-table">
            <thead><tr></tr></thead>
            <tbody></tbody>
          </table>
        </div>

        <!-- Monster browser (master-detail) -->
        <div class="monster-browser hidden">
          <div class="monster-list-wrap">
            <ul class="monster-list"></ul>
          </div>
          <div class="monster-detail-wrap">
            <div class="monster-detail"><p class="mm-empty">\u2190 Select a monster from the list</p></div>
          </div>
        </div>
      </div>
    `;

    panel.querySelectorAll('.data-tab-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        this._tab = btn.dataset.tab;
        this._filter = '';
        panel.querySelector('.data-search-wrap input').value = '';
        panel.querySelectorAll('.data-tab-btn').forEach(b =>
          b.classList.toggle('active', b === btn));
        this._loadTab(panel);
      });
    });

    panel.querySelector('.data-search-wrap input').addEventListener('input', (e) => {
      this._filter = e.target.value.toLowerCase();
      if (this._tab === 'monsters') {
        this._buildMonsterList(panel);
      } else {
        this._renderTable(panel);
      }
    });
  }

  // -------------------------------------------------------------------------
  // Loading
  // -------------------------------------------------------------------------
  async _loadTab(panel) {
    const isMonsters = this._tab === 'monsters';
    this._showMonsterBrowser(panel, isMonsters);

    if (!isMonsters) {
      const def = TAB_DEFS[this._tab];
      if (!def) return;
      const spinner = panel.querySelector('.data-spinner');
      if (!this._cache[this._tab]) {
        spinner.classList.remove('hidden');
        this._cache[this._tab] = await def.fetch();
        spinner.classList.add('hidden');
      }
      this._renderTable(panel);
      return;
    }

    // ---- Monsters ----
    if (this._cache.monsters !== null && this._loreCache !== null) {
      this._buildMonsterList(panel);
      return;
    }
    this._setDetailMsg(panel, '<p class="mm-empty"><span class="spinner" style="width:18px;height:18px;border-width:2px"></span>&nbsp;Loading\u2026</p>');
    try {
      const pending = [];
      if (this._cache.monsters === null)
        pending.push(api.fetchMonsters().then(d => { this._cache.monsters = d; }));
      if (this._loreCache === null)
        pending.push(api.fetchMonsterLore().then(d => { this._loreCache = d; }));
      await Promise.all(pending);
    } catch (e) {
      this._setDetailMsg(panel, `<p class="mm-empty" style="color:#c87f7f">Failed to load: ${_esc(e.message)}</p>`);
      return;
    }
    this._buildMonsterList(panel);
  }

  // -------------------------------------------------------------------------
  // Table rendering (items / spells)
  // -------------------------------------------------------------------------
  _renderTable(panel) {
    const def  = TAB_DEFS[this._tab];
    const data = this._cache[this._tab] || [];
    const filtered = this._filter ? data.filter(r => def.filterRow(r, this._filter)) : data;

    const thead = panel.querySelector('.data-table thead tr');
    thead.innerHTML = def.cols.map(c => `<th>${htmlEsc(c)}</th>`).join('');

    const tbody = panel.querySelector('.data-table tbody');
    tbody.innerHTML = '';
    const frag = document.createDocumentFragment();
    filtered.forEach(row => {
      const tr = document.createElement('tr');
      const cells = def.rowCells(row);
      cells.forEach(cell => {
        const td = document.createElement('td');
        if (cell.cls) td.className = cell.cls;
        if (cell.html != null) td.innerHTML = cell.html;
        else td.textContent = String(cell.text);
        tr.appendChild(td);
      });
      frag.appendChild(tr);
    });
    tbody.appendChild(frag);
  }

  // -------------------------------------------------------------------------
  // Monster browser
  // -------------------------------------------------------------------------
  _showMonsterBrowser(panel, on) {
    panel.querySelector('.monster-browser').classList.toggle('hidden', !on);
    panel.querySelector('.data-table-wrap').classList.toggle('hidden', on);
  }

  _setDetailMsg(panel, html) {
    const det = panel.querySelector('.monster-detail');
    if (det) det.innerHTML = html;
  }

  _buildMonsterList(panel) {
    const ul = panel.querySelector('.monster-list');
    if (!ul) return;
    const q    = this._filter.trim();
    const data = (this._cache.monsters || []).filter(
      m => !q || m.name.toLowerCase().includes(q)
    );

    ul.innerHTML = '';
    const frag = document.createDocumentFragment();
    data.forEach(m => {
      const li = document.createElement('li');
      li.className = 'monster-list-item' + (m.rdff_id === this._selectedId ? ' active' : '');
      li.dataset.id = m.rdff_id;

      const iconDiv = document.createElement('div');
      iconDiv.className = 'monster-list-icon';
      if (m.bmp_num && m.sprite_gff) {
        const img = document.createElement('img');
        img.src = `/api/gff/${encodeURIComponent(m.sprite_gff)}/image/${m.sprite_tag_url}/${m.bmp_num}?frame=0&game=${state.game}`;
        img.alt = '';
        img.onerror = () => { img.style.display = 'none'; };
        iconDiv.appendChild(img);
      }
      li.appendChild(iconDiv);

      const nameSpan = document.createElement('span');
      nameSpan.className = 'monster-list-name';
      nameSpan.textContent = m.name;
      li.appendChild(nameSpan);

      const lore = _matchMonsterLore(m.name, this._loreCache);
      if (lore && lore.details) {
        const badge = document.createElement('span');
        badge.className = 'monster-list-badge';
        badge.title = 'Has detailed manual entry';
        badge.textContent = '\u{1F4D6}';
        li.appendChild(badge);
      }

      li.addEventListener('click', () => this._selectMonster(panel, m));
      frag.appendChild(li);
    });

    if (data.length === 0) {
      const li = document.createElement('li');
      li.style.cssText = 'padding:12px 8px;color:var(--text-dim);font-size:12px;';
      li.textContent = q ? `No results for "${q}"` : 'No monsters found.';
      frag.appendChild(li);
    }
    ul.appendChild(frag);

    if (this._selectedId !== null) {
      const m = data.find(x => x.rdff_id === this._selectedId);
      if (m) { this._selectMonster(panel, m, false); return; }
    }
    if (this._selectedId === null && data.length > 0) {
      this._selectMonster(panel, data[0], false);
    }
  }

  _selectMonster(panel, gameEntry, scrollToTop = true) {
    this._selectedId = gameEntry.rdff_id;
    panel.querySelectorAll('.monster-list-item').forEach(li =>
      li.classList.toggle('active', Number(li.dataset.id) === gameEntry.rdff_id)
    );
    const lore = _matchMonsterLore(gameEntry.name, this._loreCache);
    this._renderMonsterDetail(panel, gameEntry, lore);
    if (scrollToTop) {
      const wrap = panel.querySelector('.monster-detail-wrap');
      if (wrap) wrap.scrollTop = 0;
    }
  }

  _renderMonsterDetail(panel, g, lore) {
    const det = panel.querySelector('.monster-detail');
    if (!det) return;

    const lstats    = lore?.details?.stats     || {};
    const narrative = lore?.details?.narrative || {};
    const psionics  = lore?.details?.psionics  || null;

    const spriteUrl = (g.bmp_num && g.sprite_gff)
      ? `/api/gff/${encodeURIComponent(g.sprite_gff)}/image/${g.sprite_tag_url}/${g.bmp_num}?frame=0&scale=4&game=${state.game}`
      : null;

    function statCell(manualVal, gameVal) {
      const hasM = manualVal != null && String(manualVal).trim().length > 0;
      const hasG = gameVal   != null && String(gameVal).trim().length   > 0;
      if (!hasM && !hasG) return '&mdash;';
      if (!hasM) return `<span class="mm-in-game-val">${_esc(String(gameVal))}</span><span class="mm-game-badge" title="Value from game data">game</span>`;
      const mStr = String(manualVal).trim();
      let html = _esc(mStr);
      if (hasG) {
        const gStr = String(gameVal).trim();
        const mNum = parseFloat(mStr);
        const gNum = parseFloat(gStr);
        const diff = isNaN(mNum) ? (mStr.toLowerCase() !== gStr.toLowerCase()) : (mNum !== gNum);
        if (diff) {
          html += ` <span class="mm-game-badge mm-differs" title="Game value: ${_esc(gStr)}">${_esc(gStr)}</span>`;
        }
      }
      return html;
    }

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
        (mv != null && String(mv).trim()) || (gv != null && String(gv).trim())
      );
      if (!active.length) return '';
      return `<table class="mm-stat-block"><tbody>${
        active.map(([label, mv, gv]) =>
          `<tr><th>${_esc(label)}</th><td>${statCell(mv, gv)}</td></tr>`
        ).join('')
      }</tbody></table>`;
    }

    function cleanText(raw) {
      if (!raw || !raw.trim()) return '';
      return raw.replace(/\n/g, ' ').replace(/\s{2,}/g, ' ').trim();
    }

    function buildSection(title, raw) {
      const text = cleanText(raw);
      if (text.length < 25) return '';
      return `<div class="mm-narrative-section"><h4>${_esc(title)}</h4><p>${_esc(text)}</p></div>`;
    }

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
        ? buildSection('Appearance',        narrative.appearance)
        + buildSection('Combat',            narrative.combat)
        + buildSection('Habitat / Society', narrative.habitat_society)
        + buildSection('Ecology',           narrative.ecology)
        : ( simpleDesc.length > 20
            ? `<div class="mm-narrative-section"><p>${_esc(simpleDesc)}</p></div>`
            : '<p class="mm-empty" style="padding:20px 0">No manual description available.</p>'
          )
    }
  </div>
</div>`;
  }
}
