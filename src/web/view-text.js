// =============================================================================
// view-text.js  —  Text resource viewer (TEXT, MERR, SPIN, NAME)
// =============================================================================

const TEXT_DISPLAY_TAGS = new Set(['TEXT', 'MERR', 'SPIN', 'NAME']);

let textData       = [];   // [{tag, number, size, content}]
let textFilter     = '';   // current search string
let activeTextIdx  = null; // index into textData for selected entry
let textDataGff    = null; // which GFF textData was loaded from

// ---------------------------------------------------------------------------
// Public entry point — called from renderContent()
// ---------------------------------------------------------------------------

async function renderTextView() {
  dom.content.scrollTop = 0;
  dom.textView.classList.remove('hidden');

  const gff = state.currentGff;

  // Bind search once
  if (!dom.textSearch._bound) {
    dom.textSearch._bound = true;
    dom.textSearch.addEventListener('input', e => {
      textFilter = e.target.value.toLowerCase();
      renderTextList();
    });
  }

  // Fetch data if stale
  if (textDataGff !== gff) {
    textData      = [];
    activeTextIdx = null;
    textDataGff   = gff;
    dom.textSearch.value = '';
    textFilter = '';
    clearTextReader();

    try {
      const res = await fetch(`/api/gff/${encodeURIComponent(gff)}/texts?game=${state.game}`);
      textData  = await res.json();
    } catch {
      textData = [];
    }
  }

  renderTextList();
}

// ---------------------------------------------------------------------------
// List panel
// ---------------------------------------------------------------------------

function renderTextList() {
  const filtered = textFilter
    ? textData.filter(e => {
        const body = Array.isArray(e.content)
          ? e.content.map(x => typeof x === 'object' ? x.text + ' ' + x.path : x).join(' ')
          : e.content;
        return e.tag.toLowerCase().includes(textFilter) ||
               String(e.number).includes(textFilter) ||
               body.toLowerCase().includes(textFilter);
      })
    : textData;

  dom.textSearchCount.textContent = textFilter
    ? `${filtered.length} / ${textData.length}`
    : `${textData.length} entries`;

  dom.textList.innerHTML = '';
  const frag = document.createDocumentFragment();

  filtered.forEach((entry, fi) => {
    // Find original index so activeTextIdx stays stable across filter changes
    const origIdx = textData.indexOf(entry);
    const li = document.createElement('li');
    li.className = 'text-list-item';
    if (origIdx === activeTextIdx) li.classList.add('active');
    li.dataset.idx = origIdx;

    const previewSrc  = Array.isArray(entry.content)
      ? entry.content.map(e => typeof e === 'object' ? e.text : e).join(' \u00b7 ')
      : entry.content;
    const preview  = previewSrc.trim().replace(/\s+/g, ' ').slice(0, 90);
    const ellipsis = previewSrc.trim().length > 90 ? '\u2026' : '';

    li.innerHTML =
      `<div class="text-list-header">` +
        `<span class="text-tag-badge text-tag-${entry.tag}">${htmlEsc(entry.tag)}</span>` +
        `<span class="text-list-num dim">#${entry.number}</span>` +
      `</div>` +
      `<div class="text-list-preview">${htmlEsc(preview)}${ellipsis}</div>`;

    li.addEventListener('click', () => {
      activeTextIdx = origIdx;
      dom.textList.querySelectorAll('.text-list-item.active')
        .forEach(el => el.classList.remove('active'));
      li.classList.add('active');
      showTextEntry(textData[origIdx]);
    });

    frag.appendChild(li);
  });

  dom.textList.appendChild(frag);

  // If previously selected entry is still visible, ensure it's shown
  if (activeTextIdx !== null && textData[activeTextIdx]) {
    showTextEntry(textData[activeTextIdx]);
  }
}

// ---------------------------------------------------------------------------
// Reader panel
// ---------------------------------------------------------------------------

function showTextEntry(entry) {
  dom.textReaderEmpty.classList.add('hidden');
  dom.textReaderContent.classList.remove('hidden');

  dom.textReaderBadge.textContent   = entry.tag;
  dom.textReaderBadge.className     = `text-tag-badge text-tag-${entry.tag}`;
  dom.textReaderNum.textContent     = `#${entry.number}  \u00b7  ${formatBytes(entry.size)}`;

  // Format content based on tag
  if (entry.tag === 'SPIN') {
    renderSpinContent(entry.content);
  } else if (entry.tag === 'NAME') {
    renderNameEntries(entry.content);
  } else {
    renderPlainContent(entry.content);
  }
}

function renderSpinContent(raw) {
  // SPIN: "Spell Name: Description" — split on first ': '
  let name = '', desc = raw.trim();
  const sep = raw.indexOf(': ');
  if (sep !== -1) {
    name = raw.slice(0, sep).trim();
    desc = raw.slice(sep + 2).trim();
  }

  dom.textReaderBody.innerHTML =
    (name ? `<p class="text-spin-name">${htmlEsc(name)}</p>` : '') +
    `<p class="text-body-para">${htmlEsc(desc).replace(/\n/g, '<br>')}</p>`;
}

function renderNameEntries(entries) {
  // entries: [{text, path, sep_hex, raw_bytes, seg_len}]
  if (!entries || entries.length === 0) {
    dom.textReaderBody.innerHTML = `<p class="text-body-para dim">(empty)</p>`;
    return;
  }

  const blocks = entries.map((e, i) => {
    const bytes  = e.raw_bytes || [];
    const segLen = (e.seg_len != null) ? e.seg_len : bytes.length;
    const dump   = buildNameByteDump(bytes, segLen);

    return `<div class="name-dump-block">` +
      `<div class="name-dump-title">` +
        `<span class="name-dump-idx">${i + 1}</span>` +
        `<span class="name-dump-name">${htmlEsc(e.text)}</span>` +
      `</div>` +
      `<pre class="name-dump-pre">${dump}</pre>` +
    `</div>`;
  });

  dom.textReaderBody.innerHTML = blocks.join('');
}

// Build a 4-bytes-per-row binary+hex+ascii dump for a NAME entry.
// Bytes from index segLen onward are the delimiter and are highlighted in red.
function buildNameByteDump(bytes, segLen) {
  const COLS = 4;
  const rows = [];

  rows.push(
    '<span class="name-dump-hdr">Offs  Binary                               Hex         ASCII</span>'
  );

  for (let i = 0; i < bytes.length; i += COLS) {
    const chunk  = bytes.slice(i, i + COLS);
    const offset = i.toString(16).padStart(4, '0');

    const binParts = [];
    const hexParts = [];
    const ascParts = [];

    for (let j = 0; j < COLS; j++) {
      if (j >= chunk.length) {
        binParts.push('        ');
        hexParts.push('  ');
        ascParts.push(' ');
        continue;
      }

      const b       = chunk[j];
      const byteIdx = i + j;
      const isSep   = byteIdx >= segLen;
      const binStr  = b.toString(2).padStart(8, '0');
      const hexStr  = b.toString(16).padStart(2, '0');
      const safeAsc = (b >= 32 && b < 127)
        ? htmlEsc(String.fromCharCode(b))
        : '·';

      if (isSep) {
        binParts.push(`<span class="name-dump-sep">${binStr}</span>`);
        hexParts.push(`<span class="name-dump-sep">${hexStr}</span>`);
        ascParts.push(`<span class="name-dump-sep">${safeAsc}</span>`);
      } else if (b === 0) {
        binParts.push(`<span class="name-dump-zero">${binStr}</span>`);
        hexParts.push(`<span class="name-dump-zero">${hexStr}</span>`);
        ascParts.push(`<span class="name-dump-zero">·</span>`);
      } else {
        binParts.push(binStr);
        hexParts.push(hexStr);
        ascParts.push(safeAsc);
      }
    }

    rows.push(
      `${offset}  ${binParts.join(' ')}  ${hexParts.join(' ')}  ${ascParts.join('')}`
    );
  }

  return rows.join('\n');
}

function renderPlainContent(raw) {
  // Split on blank lines to make paragraphs; split on single newlines within
  const paras = raw.trim().split(/\n{2,}/).map(p => p.trim()).filter(Boolean);
  dom.textReaderBody.innerHTML = paras.length
    ? paras.map(p => `<p class="text-body-para">${htmlEsc(p).replace(/\n/g, '<br>')}</p>`).join('')
    : `<p class="text-body-para dim">(empty)</p>`;
}

function clearTextReader() {
  dom.textReaderEmpty.classList.remove('hidden');
  dom.textReaderContent.classList.add('hidden');
  dom.textReaderBody.innerHTML = '';
}
