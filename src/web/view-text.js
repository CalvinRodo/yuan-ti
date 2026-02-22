// =============================================================================
// view-text.js  —  Text resource viewer (TEXT, MERR, SPIN)
// =============================================================================

const TEXT_DISPLAY_TAGS = new Set(['TEXT', 'MERR', 'SPIN']);

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
      const res = await fetch(`/api/gff/${encodeURIComponent(gff)}/texts`);
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
    ? textData.filter(e =>
        e.tag.toLowerCase().includes(textFilter) ||
        String(e.number).includes(textFilter) ||
        e.content.toLowerCase().includes(textFilter))
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

    const preview = entry.content.trim().replace(/\s+/g, ' ').slice(0, 90);
    const ellipsis = entry.content.trim().length > 90 ? '\u2026' : '';

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
