/* ================================================================
   Dark Sun GFF Viewer — view-scripts.js
   GPL/MAS script browser: list on left, disassembly on right.
   ================================================================ */

'use strict';

// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------
let scriptsViewBuilt  = false;
let scriptsGame       = null;
let scriptsCache      = null;   // [{tag, tag_url, number, size, label}]
let scriptsDisasmCache= {};     // "TAG/num" -> instruction list
let scriptsSelected   = null;   // {tag, tag_url, number}
let scriptsFilter     = '';

// ---------------------------------------------------------------------------
// Entry point (called from app.js renderContent)
// ---------------------------------------------------------------------------
function renderScriptsView() {
  const view = document.getElementById('scripts-view');
  if (!view) return;
  view.classList.remove('hidden');

  if (!scriptsViewBuilt) {
    _buildScriptsDOM(view);
    scriptsViewBuilt = true;
  }

  if (scriptsGame !== state.game || scriptsCache === null) {
    scriptsGame       = state.game;
    scriptsCache      = null;
    scriptsDisasmCache= {};
    scriptsSelected   = null;
    _loadScriptList();
  }
}

function teardownScriptsView() {
  scriptsGame       = null;
  scriptsCache      = null;
  scriptsDisasmCache= {};
  scriptsSelected   = null;
}

// ---------------------------------------------------------------------------
// DOM builder (runs once)
// ---------------------------------------------------------------------------
function _buildScriptsDOM(view) {
  view.innerHTML = `
    <div id="scripts-layout">
      <div id="scripts-sidebar">
        <div id="scripts-search-wrap">
          <input id="scripts-search" type="search" placeholder="Filter scripts&#8230;"
                 autocomplete="off" spellcheck="false">
        </div>
        <div id="scripts-list-wrap">
          <div id="scripts-spinner" class="scripts-spinner hidden">
            <div class="spinner"></div><span>Loading&#8230;</span>
          </div>
          <div id="scripts-list-error" class="scripts-error hidden"></div>
          <ul id="scripts-list"></ul>
        </div>
      </div>
      <div id="scripts-disasm">
        <div id="scripts-disasm-header" class="hidden">
          <span id="scripts-disasm-title"></span>
          <span id="scripts-disasm-size" class="dim"></span>
        </div>
        <div id="scripts-disasm-spinner" class="scripts-spinner hidden">
          <div class="spinner"></div><span>Disassembling&#8230;</span>
        </div>
        <div id="scripts-disasm-error" class="scripts-error hidden"></div>
        <div id="scripts-disasm-empty" class="scripts-empty">
          Select a script to view its disassembly.
        </div>
        <div id="scripts-disasm-wrap" class="hidden">
          <table id="scripts-disasm-table">
            <thead><tr>
              <th class="sc-th-addr">Addr</th>
              <th class="sc-th-code">Instruction</th>
            </tr></thead>
            <tbody id="scripts-disasm-tbody"></tbody>
          </table>
        </div>
      </div>
    </div>`;

  view.querySelector('#scripts-search').addEventListener('input', e => {
    scriptsFilter = e.target.value.toLowerCase();
    _renderScriptList();
  });
}

// ---------------------------------------------------------------------------
// Script list
// ---------------------------------------------------------------------------
function _loadScriptList() {
  const spinner = document.getElementById('scripts-spinner');
  const errEl   = document.getElementById('scripts-list-error');
  if (!spinner) return;

  spinner.classList.remove('hidden');
  errEl.classList.add('hidden');
  document.getElementById('scripts-list').innerHTML = '';

  fetch(`/api/scripts?game=${state.game}`)
    .then(r => r.ok ? r.json() : r.json().then(j => { throw new Error(j.error || r.statusText); }))
    .then(data => {
      spinner.classList.add('hidden');
      scriptsCache = data;
      _renderScriptList();
    })
    .catch(err => {
      spinner.classList.add('hidden');
      errEl.textContent = `Error: ${err.message}`;
      errEl.classList.remove('hidden');
    });
}

function _renderScriptList() {
  const ul    = document.getElementById('scripts-list');
  if (!ul || !scriptsCache) return;

  const items = scriptsFilter
    ? scriptsCache.filter(s => s.label.toLowerCase().includes(scriptsFilter))
    : scriptsCache;

  ul.innerHTML = '';
  items.forEach(script => {
    const li = document.createElement('li');
    li.className = 'scripts-list-item';
    const sizeStr = script.size < 1024
      ? `${script.size} B`
      : `${(script.size / 1024).toFixed(1)} KB`;
    li.innerHTML = `<span class="sc-tag ${script.tag === 'MAS' ? 'sc-tag-mas' : 'sc-tag-gpl'}">${htmlEsc(script.tag)}</span>`
                 + `<span class="sc-label">#${script.number}</span>`
                 + `<span class="sc-size dim">${sizeStr}</span>`;
    li.dataset.tag    = script.tag_url;
    li.dataset.number = script.number;
    li.addEventListener('click', () => _selectScript(script, li));
    if (scriptsSelected
        && scriptsSelected.tag_url === script.tag_url
        && scriptsSelected.number  === script.number) {
      li.classList.add('active');
    }
    ul.appendChild(li);
  });
}

// ---------------------------------------------------------------------------
// Script selection & disassembly
// ---------------------------------------------------------------------------
function _selectScript(script, liEl) {
  scriptsSelected = script;

  // Update active state
  document.querySelectorAll('.scripts-list-item').forEach(li => li.classList.remove('active'));
  liEl.classList.add('active');

  // Update header
  const hdr = document.getElementById('scripts-disasm-header');
  hdr.classList.remove('hidden');
  document.getElementById('scripts-disasm-title').textContent = `${script.tag} #${script.number}`;
  const sizeStr = script.size < 1024
    ? `${script.size} bytes`
    : `${(script.size / 1024).toFixed(1)} KB`;
  document.getElementById('scripts-disasm-size').textContent = sizeStr;

  // Check cache
  const cacheKey = `${script.tag_url}/${script.number}`;
  if (scriptsDisasmCache[cacheKey]) {
    _renderDisasm(scriptsDisasmCache[cacheKey]);
    return;
  }

  // Fetch
  const spinner = document.getElementById('scripts-disasm-spinner');
  const errEl   = document.getElementById('scripts-disasm-error');
  const emptyEl = document.getElementById('scripts-disasm-empty');
  const wrap    = document.getElementById('scripts-disasm-wrap');
  spinner.classList.remove('hidden');
  errEl.classList.add('hidden');
  emptyEl.classList.add('hidden');
  wrap.classList.add('hidden');

  fetch(`/api/script/${script.tag_url}/${script.number}?game=${state.game}`)
    .then(r => r.ok ? r.json() : r.json().then(j => { throw new Error(j.error || r.statusText); }))
    .then(insts => {
      spinner.classList.add('hidden');
      scriptsDisasmCache[cacheKey] = insts;
      _renderDisasm(insts);
    })
    .catch(err => {
      spinner.classList.add('hidden');
      errEl.textContent = `Disassembly error: ${err.message}`;
      errEl.classList.remove('hidden');
    });
}

// ---------------------------------------------------------------------------
// Disassembly renderer
// ---------------------------------------------------------------------------
const _KEYWORD_OPS = new Set([
  'IF', 'ELSE', 'ENDIF', 'WHILE', 'WEND', 'CMPEND',
  'EXIT_GPL', 'END_FUNCTION', 'LOCAL_RET', 'GLOBAL_RET',
  'MENU', 'MENU_END',
  'JUMP', 'CALL_LOCAL', 'CALL_GLOBAL',
]);
const _FLOW_OPS = new Set([
  'IF', 'ELSE', 'ENDIF', 'WHILE', 'WEND', 'CMPEND', 'JUMP',
  'CALL_LOCAL', 'CALL_GLOBAL',
]);

function _colorize(line) {
  if (!line) return '';
  const h = htmlEsc(line);

  // Comment-like (LOG or PRINT_STRING with text)
  if (h.startsWith('LOG') || h.startsWith('PRINT_')) {
    return `<span class="sc-op-print">${h}</span>`;
  }

  // ACCUM assignment
  if (h.startsWith('ACCUM &lt;=')) {
    const kw  = 'ACCUM &lt;=';
    const rest = h.slice(kw.length);
    return `<span class="sc-op-accum">${kw}</span><span class="sc-args">${rest}</span>`;
  }

  // Variable assignment (e.g. "gflag[36] = 1" or "lnum[0] = accum")
  const assignMatch = h.match(/^([a-z]+\[[\d]+\])\s*=/);
  if (assignMatch) {
    const varPart = assignMatch[0];
    const rest    = h.slice(varPart.length);
    return `<span class="sc-op-assign">${varPart}</span><span class="sc-args">${rest}</span>`;
  }

  // Multi-word control flow
  const parts = h.split(/(\()/);
  const opWord = parts[0].trim();
  if (_FLOW_OPS.has(opWord.replace(/[()]/g, ''))) {
    return `<span class="sc-op-flow">${h}</span>`;
  }
  if (_KEYWORD_OPS.has(opWord)) {
    return `<span class="sc-op-kw">${h}</span>`;
  }

  // Error or unknown (prefix ?? covers: unknown opcode, unknown var, decode error)
  if (h.startsWith('??') || h.startsWith('ERR_') || h.startsWith('UNKNOWN_') || h.startsWith('&lt;')) {
    // Split on first semicolon so the opcode is bold and the context is dimmed
    const semi = h.indexOf(';');
    if (semi > 0) {
      const op  = h.slice(0, semi);
      const ctx = h.slice(semi);
      return `<span class="sc-op-err">${op}</span><span class="sc-op-ctx dim">${ctx}</span>`;
    }
    return `<span class="sc-op-err">${h}</span>`;
  }

  // Regular instruction: bold op, dim args
  const parenIdx = h.indexOf('(');
  if (parenIdx > 0) {
    const op   = h.slice(0, parenIdx);
    const args = h.slice(parenIdx);
    return `<span class="sc-op-name">${op}</span><span class="sc-args">${args}</span>`;
  }
  return `<span class="sc-op-name">${h}</span>`;
}

function _renderDisasm(insts) {
  const wrap  = document.getElementById('scripts-disasm-wrap');
  const tbody = document.getElementById('scripts-disasm-tbody');
  const empty = document.getElementById('scripts-disasm-empty');

  if (!insts || insts.length === 0) {
    empty.textContent = 'Script is empty.';
    empty.classList.remove('hidden');
    return;
  }

  wrap.classList.remove('hidden');
  empty.classList.add('hidden');

  const rows = insts.map(inst => {
    const addrStr  = inst.addr >= 0 ? inst.addr.toString(16).padStart(4, '0') : '';
    const indent   = '\u00a0\u00a0'.repeat(inst.depth || 0);  // &nbsp; pairs
    const colored  = _colorize(inst.line || inst.op);
    return `<tr class="sc-row-depth-${Math.min(inst.depth || 0, 6)}">`
         + `<td class="sc-addr">${addrStr}</td>`
         + `<td class="sc-code">${indent}${colored}</td>`
         + `</tr>`;
  });

  tbody.innerHTML = rows.join('');
}
