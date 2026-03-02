/* ================================================================
   ScriptsScene — GPL/MAS bytecode script browser.
   Uses a DOM panel with a sidebar list and disassembly view.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';
import { showDomPanel, htmlEsc, formatBytes } from '../utils.js';

// Syntax coloring classes by opcode name pattern
const OP_CLASSES = [
  [/^(JMP|JMPT|JMPF|RTS|CALL|GOSUB|RETURN)/i, 'sc-op-flow'],
  [/^(IF|ELSE|ENDIF|WHILE|WEND|LOOP|REPEAT)/i, 'sc-op-kw'],
  [/^(ACCUM|PUSH|POP|GLOBAL|LOCAL)/i, 'sc-op-accum'],
  [/^(SET|ASSIGN|MOVE|LOAD|STORE)/i, 'sc-op-assign'],
  [/^(PRINT|SAY|MSG|TEXT)/i, 'sc-op-print'],
  [/^(NAME|CHAR|NPC|MONSTER)/i, 'sc-op-name'],
  [/^(ERR|UNKNOWN)/i, 'sc-op-err'],
];

function opcodeClass(code) {
  for (const [re, cls] of OP_CLASSES) {
    if (re.test(code)) return cls;
  }
  return '';
}

export default class ScriptsScene extends Phaser.Scene {
  constructor() {
    super('ScriptsScene');
    this._built = false;
    this._cache = null;
    this._game = null;
    this._disasmCache = {};
    this._selected = null;
    this._filter = '';
  }

  create() {
    const panel = showDomPanel('ScriptsScene');
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');

    if (this._game !== state.game || !this._cache) {
      this._game = state.game;
      this._cache = null;
      this._disasmCache = {};
      this._selected = null;
      this._loadList(panel);
    }
  }

  shutdown() {}

  _build(panel) {
    panel.innerHTML = `
      <div class="scripts-view">
        <div class="scripts-sidebar">
          <div class="scripts-search-wrap">
            <input type="search" placeholder="Filter scripts\u2026" autocomplete="off" spellcheck="false">
          </div>
          <ul class="scripts-list"></ul>
        </div>
        <div class="scripts-disasm">
          <div class="scripts-disasm-header hidden">
            <span class="scripts-disasm-title"></span>
            <span class="scripts-disasm-size dim"></span>
          </div>
          <div class="scripts-disasm-empty">Select a script to view its disassembly.</div>
          <div class="scripts-disasm-wrap hidden">
            <table class="disasm-table">
              <thead><tr><th>Addr</th><th>Instruction</th></tr></thead>
              <tbody></tbody>
            </table>
          </div>
        </div>
      </div>
    `;

    panel.querySelector('.scripts-search-wrap input').addEventListener('input', (e) => {
      this._filter = e.target.value.toLowerCase();
      this._renderList(panel);
    });
  }

  async _loadList(panel) {
    const listEl = panel.querySelector('.scripts-list');
    listEl.innerHTML = '<li class="music-loading"><div class="spinner"></div> Loading\u2026</li>';

    this._cache = await api.fetchScripts();
    this._renderList(panel);
  }

  _renderList(panel) {
    const listEl = panel.querySelector('.scripts-list');
    if (!this._cache) return;

    const items = this._filter
      ? this._cache.filter(s => s.label.toLowerCase().includes(this._filter))
      : this._cache;

    listEl.innerHTML = '';
    items.forEach(script => {
      const li = document.createElement('li');
      li.className = 'scripts-list-item';
      const sizeStr = script.size < 1024
        ? `${script.size} B`
        : `${(script.size / 1024).toFixed(1)} KB`;
      const tagCls = script.tag === 'MAS' ? 'sc-tag-mas' : 'sc-tag-gpl';
      li.innerHTML =
        `<span class="sc-tag ${tagCls}">${htmlEsc(script.tag)}</span>` +
        `<span class="sc-label">#${script.number}</span>` +
        `<span class="sc-size dim">${sizeStr}</span>`;

      if (this._selected?.tag_url === script.tag_url && this._selected?.number === script.number) {
        li.classList.add('active');
      }

      li.addEventListener('click', () => this._selectScript(panel, script, li));
      listEl.appendChild(li);
    });
  }

  async _selectScript(panel, script, liEl) {
    this._selected = script;

    // Active state
    panel.querySelectorAll('.scripts-list-item').forEach(li => li.classList.remove('active'));
    liEl.classList.add('active');

    // Header
    const hdr = panel.querySelector('.scripts-disasm-header');
    hdr.classList.remove('hidden');
    panel.querySelector('.scripts-disasm-title').textContent = `${script.tag} #${script.number}`;
    const sizeStr = script.size < 1024 ? `${script.size} bytes` : `${(script.size / 1024).toFixed(1)} KB`;
    panel.querySelector('.scripts-disasm-size').textContent = sizeStr;

    const emptyEl = panel.querySelector('.scripts-disasm-empty');
    const wrapEl = panel.querySelector('.scripts-disasm-wrap');

    const cacheKey = `${script.tag_url}/${script.number}`;
    if (this._disasmCache[cacheKey]) {
      emptyEl.classList.add('hidden');
      wrapEl.classList.remove('hidden');
      this._renderDisasm(panel, this._disasmCache[cacheKey]);
      return;
    }

    emptyEl.textContent = 'Disassembling\u2026';
    emptyEl.classList.remove('hidden');
    wrapEl.classList.add('hidden');

    try {
      const insts = await api.fetchScript(script.tag_url, script.number);
      this._disasmCache[cacheKey] = insts;
      emptyEl.classList.add('hidden');
      wrapEl.classList.remove('hidden');
      this._renderDisasm(panel, insts);
    } catch (err) {
      emptyEl.textContent = `Error: ${err.message}`;
    }
  }

  _renderDisasm(panel, insts) {
    const tbody = panel.querySelector('.disasm-table tbody');
    tbody.innerHTML = '';
    const frag = document.createDocumentFragment();

    insts.forEach(inst => {
      const tr = document.createElement('tr');
      const addrTd = document.createElement('td');
      addrTd.className = 'addr';
      addrTd.textContent = inst.address ?? '';

      const codeTd = document.createElement('td');
      const cls = opcodeClass(inst.code || inst.text || '');
      if (cls) codeTd.className = cls;
      codeTd.textContent = inst.text || inst.code || '';

      tr.appendChild(addrTd);
      tr.appendChild(codeTd);
      frag.appendChild(tr);
    });

    tbody.appendChild(frag);
  }
}
