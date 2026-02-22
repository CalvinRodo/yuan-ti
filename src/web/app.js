/* ================================================================
   Dark Sun GFF Viewer — app.js  (v2 — detail pane + hex viewer)
   Vanilla ES2022, no external dependencies
   ================================================================ */

'use strict';

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------
const state = {
  gffs:        [],
  currentGff:  null,
  resources:   [],
  pals:        [],
  selectedPal: null,
  activeFilter: 'images',
  detail: {
    resource:   null,
    frames:     [],
    frameIndex: 0,
  },
};

// ---------------------------------------------------------------------------
// DOM refs
// ---------------------------------------------------------------------------
const $ = id => document.getElementById(id);

const dom = {
  gffList:        $('gff-list'),
  sidebarSearch:  $('sidebar-search'),
  currentLabel:   $('current-file-label'),
  filterBtns:     document.querySelectorAll('.filter-btn'),
  palSelect:      $('pal-select'),
  content:        $('content'),
  emptyState:     $('empty-state'),
  imageGrid:      $('image-grid'),
  tableWrap:      $('resource-table-wrap'),
  tableTbody:     $('resource-tbody'),
  // detail pane
  detailPane:       $('detail-pane'),
  detailTag:        $('detail-tag'),
  detailNum:        $('detail-num'),
  detailSize:       $('detail-size'),
  detailDesc:       $('detail-desc'),
  detailClose:      $('detail-close'),
  detailImageView:  $('detail-image-view'),
  detailPrev:       $('detail-prev'),
  detailNext:       $('detail-next'),
  detailFrameInfo:  $('detail-frame-info'),
  detailImgWrap:    $('detail-img-wrap'),
  detailImg:        $('detail-img'),
  detailImgSpinner: $('detail-img-spinner'),
  detailImgDims:    $('detail-img-dims'),
  detailHexView:    $('detail-hex-view'),
  hexInfo:          $('hex-info'),
  hexTruncated:     $('hex-truncated'),
  hexPre:           $('hex-pre'),
};

// IntersectionObserver for lazy-loading image cards
let lazyObserver = null;

// Cache-bust suffix when PAL changes
let palVersion = 0;

// ---------------------------------------------------------------------------
// Initialisation
// ---------------------------------------------------------------------------
async function init() {
  bindEvents();
  await loadGffList();
}

function bindEvents() {
  dom.sidebarSearch.addEventListener('input', () => renderSidebar());
  dom.palSelect.addEventListener('change', onPalChange);

  dom.filterBtns.forEach(btn => {
    btn.addEventListener('click', () => {
      dom.filterBtns.forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      state.activeFilter = btn.dataset.filter;
      renderContent();
    });
  });

  dom.detailClose.addEventListener('click', closeDetailPane);
  dom.detailPrev.addEventListener('click', () => navigateFrame(-1));
  dom.detailNext.addEventListener('click', () => navigateFrame(1));

  document.addEventListener('keydown', e => {
    if (!state.detail.resource) return;
    if (e.key === 'Escape')     closeDetailPane();
    if (e.key === 'ArrowLeft')  navigateFrame(-1);
    if (e.key === 'ArrowRight') navigateFrame(1);
  });
}

// ---------------------------------------------------------------------------
// GFF list
// ---------------------------------------------------------------------------
async function loadGffList() {
  try {
    const res  = await fetch('/api/gffs');
    state.gffs = await res.json();
  } catch {
    state.gffs = [];
  }
  renderSidebar();
}

function renderSidebar() {
  const q = dom.sidebarSearch.value.trim().toLowerCase();
  dom.gffList.innerHTML = '';
  state.gffs
    .filter(g => !q || g.toLowerCase().includes(q))
    .forEach(name => {
      const li = document.createElement('li');
      li.textContent = name;
      if (name === state.currentGff) li.classList.add('active');
      li.addEventListener('click', () => selectGff(name));
      dom.gffList.appendChild(li);
    });
}

// ---------------------------------------------------------------------------
// GFF selection
// ---------------------------------------------------------------------------
async function selectGff(name) {
  if (state.currentGff === name) return;
  state.currentGff  = name;
  state.resources   = [];
  state.pals        = [];
  state.selectedPal = null;
  state.detail.resource = null;

  renderSidebar();
  dom.currentLabel.textContent = name;
  dom.currentLabel.classList.remove('dim');
  closeDetailPane();
  showLoadingState();

  const [resources, pals] = await Promise.all([
    fetch(`/api/gff/${encodeURIComponent(name)}/resources`).then(r => r.json()).catch(() => []),
    fetch(`/api/gff/${encodeURIComponent(name)}/pals`).then(r => r.json()).catch(() => []),
  ]);

  state.resources = resources;
  state.pals      = pals;

  buildPalSelector(pals);
  renderContent();
}

function showLoadingState() {
  dom.emptyState.classList.add('hidden');
  dom.imageGrid.classList.add('hidden');
  dom.tableWrap.classList.add('hidden');

  let loading = $('content-loading');
  if (!loading) {
    loading = document.createElement('div');
    loading.id = 'content-loading';
    loading.innerHTML = '<div class="spinner"></div><span>Loading\u2026</span>';
    dom.content.appendChild(loading);
  }
  loading.classList.remove('hidden');
}

function hideLoadingState() {
  const loading = $('content-loading');
  if (loading) loading.classList.add('hidden');
}

// ---------------------------------------------------------------------------
// PAL selector
// ---------------------------------------------------------------------------
function buildPalSelector(pals) {
  dom.palSelect.innerHTML = '<option value="auto">Auto\u2011detect</option>';
  pals.forEach((p, i) => {
    const opt = document.createElement('option');
    opt.value = i;
    opt.textContent = `${p.gff}  \u2014  PAL #${p.number}`;
    dom.palSelect.appendChild(opt);
  });
  dom.palSelect.value = 'auto';
  state.selectedPal = null;
}

function onPalChange() {
  const val = dom.palSelect.value;
  state.selectedPal = val === 'auto' ? null : state.pals[parseInt(val)];
  palVersion++;
  renderContent();
  if (state.detail.resource?.is_image) {
    renderFrameInPane(state.detail.frameIndex);
  }
}

// ---------------------------------------------------------------------------
// PAL / URL helpers
// ---------------------------------------------------------------------------
function palParams() {
  if (!state.selectedPal) return '';
  const p = state.selectedPal;
  return `&pal_gff=${encodeURIComponent(p.gff)}&pal_num=${p.number}`;
}

function imageUrl(gff, tagUrl, number, frame = -1) {
  return `/api/gff/${encodeURIComponent(gff)}/image/${tagUrl}/${number}`
       + `?frame=${frame}${palParams()}&v=${palVersion}`;
}

function rawUrl(gff, tagUrl, number) {
  return `/api/gff/${encodeURIComponent(gff)}/raw/${tagUrl}/${number}`;
}

// ---------------------------------------------------------------------------
// Content rendering
// ---------------------------------------------------------------------------
function renderContent() {
  hideLoadingState();
  dom.emptyState.classList.add('hidden');
  dom.imageGrid.classList.add('hidden');
  dom.tableWrap.classList.add('hidden');

  if (!state.currentGff || state.resources.length === 0) {
    dom.emptyState.classList.remove('hidden');
    return;
  }

  if (state.activeFilter === 'images') {
    renderImageGrid();
  } else {
    renderResourceTable();
  }
}

// ---------------------------------------------------------------------------
// Image grid
// ---------------------------------------------------------------------------
function renderImageGrid() {
  const imageResources = state.resources.filter(r => r.is_image);

  if (imageResources.length === 0) {
    dom.imageGrid.innerHTML =
      '<div style="grid-column:1/-1;color:var(--text-faint);padding:32px 0;text-align:center;">' +
      'No image resources (BMP, TILE, ICON) found in this GFF file.</div>';
    dom.imageGrid.classList.remove('hidden');
    return;
  }

  if (lazyObserver) { lazyObserver.disconnect(); lazyObserver = null; }

  const frag = document.createDocumentFragment();
  imageResources.forEach(res => frag.appendChild(createImageCard(res)));

  dom.imageGrid.innerHTML = '';
  dom.imageGrid.appendChild(frag);
  dom.imageGrid.classList.remove('hidden');

  lazyObserver = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      if (!entry.isIntersecting) return;
      const img = entry.target.querySelector('img[data-src]');
      if (!img) return;
      loadCardImage(img);
      lazyObserver.unobserve(entry.target);
    });
  }, { rootMargin: '200px' });

  dom.imageGrid.querySelectorAll('.img-card').forEach(card => {
    lazyObserver.observe(card);
  });
}

function createImageCard(res) {
  const card = document.createElement('div');
  card.className = 'img-card';
  if (state.detail.resource?.tag_url === res.tag_url &&
      state.detail.resource?.number  === res.number) {
    card.classList.add('selected');
  }
  card.dataset.tagUrl = res.tag_url;
  card.dataset.number = res.number;

  const src = imageUrl(state.currentGff, res.tag_url, res.number, -1);

  card.innerHTML =
    `<div class="img-card-preview">` +
    `<img data-src="${src}" alt="${res.tag.trim()}-${res.number}">` +
    `<div class="card-spinner"></div>` +
    `<div class="card-error">Failed to load</div>` +
    `</div>` +
    `<div class="img-card-footer">` +
    `<div class="img-card-tag">${htmlEsc(res.tag.trim())}</div>` +
    `<div class="img-card-num">#${res.number}</div>` +
    `<div class="img-card-size">${formatBytes(res.size)}</div>` +
    `</div>`;

  card.addEventListener('click', () => {
    dom.imageGrid.querySelectorAll('.img-card.selected')
      .forEach(c => c.classList.remove('selected'));
    card.classList.add('selected');
    openDetailPane(res);
  });
  return card;
}

function loadCardImage(img) {
  const preview = img.closest('.img-card-preview');
  const spinner = preview.querySelector('.card-spinner');
  const errDiv  = preview.querySelector('.card-error');
  img.src = img.dataset.src;
  img.removeAttribute('data-src');
  img.onload  = () => { img.classList.add('loaded'); if (spinner) spinner.style.display = 'none'; };
  img.onerror = () => { if (spinner) spinner.style.display = 'none'; if (errDiv) errDiv.style.display = 'block'; };
}

// ---------------------------------------------------------------------------
// Resource table
// ---------------------------------------------------------------------------
function renderResourceTable() {
  dom.tableTbody.innerHTML = '';
  const gff  = state.currentGff;
  const frag = document.createDocumentFragment();

  state.resources.forEach(res => {
    const tr = document.createElement('tr');
    tr.dataset.tagUrl = res.tag_url;
    tr.dataset.number = res.number;

    if (state.detail.resource?.tag_url === res.tag_url &&
        state.detail.resource?.number  === res.number) {
      tr.classList.add('selected');
    }

    const thumbCell = res.is_image
      ? `<td class="td-thumb"><img src="${imageUrl(gff, res.tag_url, res.number, 0)}" alt="${res.tag.trim()}-${res.number}" loading="lazy"></td>`
      : '<td></td>';

    tr.innerHTML =
      `<td class="td-tag">${htmlEsc(res.tag.trim())}</td>` +
      `<td class="td-num">${res.number}</td>` +
      `<td class="td-size">${formatBytes(res.size)}</td>` +
      `<td class="td-desc">${htmlEsc(res.description)}</td>` +
      thumbCell;

    tr.addEventListener('click', () => {
      dom.tableTbody.querySelectorAll('tr.selected').forEach(r => r.classList.remove('selected'));
      tr.classList.add('selected');
      openDetailPane(res);
    });

    frag.appendChild(tr);
  });

  dom.tableTbody.appendChild(frag);
  dom.tableWrap.classList.remove('hidden');
}

// ---------------------------------------------------------------------------
// Detail pane — open / close
// ---------------------------------------------------------------------------
async function openDetailPane(res) {
  state.detail.resource   = res;
  state.detail.frames     = [];
  state.detail.frameIndex = 0;

  dom.detailTag.textContent  = res.tag.trim();
  dom.detailNum.textContent  = `#${res.number}`;
  dom.detailSize.textContent = formatBytes(res.size);
  dom.detailDesc.textContent = res.description;

  dom.detailPane.classList.remove('hidden');
  dom.detailImageView.classList.add('hidden');
  dom.detailHexView.classList.add('hidden');

  if (res.is_image) {
    dom.detailImageView.classList.remove('hidden');
    await loadDetailImage(res);
  } else {
    dom.detailHexView.classList.remove('hidden');
    await loadDetailHex(res);
  }
}

function closeDetailPane() {
  state.detail.resource = null;
  dom.detailPane.classList.add('hidden');
  dom.detailImg.src = '';
  dom.tableTbody.querySelectorAll('tr.selected').forEach(r => r.classList.remove('selected'));
  dom.imageGrid.querySelectorAll('.img-card.selected').forEach(c => c.classList.remove('selected'));
}

// ---------------------------------------------------------------------------
// Detail pane — image view
// ---------------------------------------------------------------------------
async function loadDetailImage(res) {
  dom.detailImg.style.display        = 'none';
  dom.detailImgSpinner.style.display = 'block';
  dom.detailFrameInfo.textContent    = '';
  dom.detailImgDims.textContent      = '';
  dom.detailPrev.disabled = true;
  dom.detailNext.disabled = true;

  try {
    const r = await fetch(
      `/api/gff/${encodeURIComponent(state.currentGff)}/image/${res.tag_url}/${res.number}/info?v=${palVersion}`);
    const info = await r.json();
    if (!info.error && info.frames?.length) {
      state.detail.frames = info.frames;
    }
  } catch { /* ignore */ }

  renderFrameInPane(0);
}

function renderFrameInPane(frameIdx) {
  const res = state.detail.resource;
  if (!res) return;
  const frames = state.detail.frames;
  const count  = frames.length;

  frameIdx = Math.max(0, Math.min(frameIdx, Math.max(count - 1, 0)));
  state.detail.frameIndex = frameIdx;

  dom.detailPrev.disabled = (frameIdx <= 0);
  dom.detailNext.disabled = (frameIdx >= count - 1);

  if (count > 0) {
    const f = frames[frameIdx];
    dom.detailFrameInfo.textContent = `${frameIdx + 1} / ${count}`;
    dom.detailImgDims.textContent   = `${f.width} \u00d7 ${f.height} px`;
  } else {
    dom.detailFrameInfo.textContent = '';
    dom.detailImgDims.textContent   = '';
  }

  dom.detailImg.style.display        = 'none';
  dom.detailImgSpinner.style.display = 'block';

  const src = imageUrl(state.currentGff, res.tag_url, res.number, frameIdx);
  dom.detailImg.onload = () => {
    dom.detailImgSpinner.style.display = 'none';
    dom.detailImg.style.display        = 'block';
    if (!frames.length) {
      dom.detailImgDims.textContent =
        `${dom.detailImg.naturalWidth} \u00d7 ${dom.detailImg.naturalHeight} px`;
    }
  };
  dom.detailImg.onerror = () => {
    dom.detailImgSpinner.style.display = 'none';
    dom.detailImg.alt                  = '\u26a0 Failed to load';
    dom.detailImg.style.display        = 'block';
  };
  dom.detailImg.src = src;
}

function navigateFrame(delta) {
  renderFrameInPane(state.detail.frameIndex + delta);
}

// ---------------------------------------------------------------------------
// Detail pane — hex view
// ---------------------------------------------------------------------------
const HEX_MAX_BYTES = 8192;

async function loadDetailHex(res) {
  dom.hexPre.innerHTML = '';
  dom.hexInfo.textContent = 'Loading\u2026';
  dom.hexTruncated.classList.add('hidden');

  let buffer;
  try {
    const resp = await fetch(rawUrl(state.currentGff, res.tag_url, res.number));
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    buffer = await resp.arrayBuffer();
  } catch (e) {
    dom.hexInfo.textContent = `Error: ${e.message}`;
    return;
  }

  const bytes      = new Uint8Array(buffer);
  const totalLen   = bytes.length;
  const displayLen = Math.min(totalLen, HEX_MAX_BYTES);
  const truncated  = totalLen > HEX_MAX_BYTES;

  dom.hexInfo.textContent = `${totalLen} bytes`;
  if (truncated) dom.hexTruncated.classList.remove('hidden');

  dom.hexPre.innerHTML = buildHexHtml(bytes.subarray(0, displayLen));
}

function buildHexHtml(bytes) {
  const lines = [];
  for (let i = 0; i < bytes.length; i += 16) {
    const chunk = bytes.subarray(i, i + 16);

    const offsetStr = `<span class="hex-offset">${i.toString(16).padStart(8, '0')}</span>`;

    const hexGroups = [];
    for (let g = 0; g < 2; g++) {
      const cells = [];
      for (let j = 0; j < 8; j++) {
        const idx = g * 8 + j;
        if (i + idx < bytes.length) {
          const b   = bytes[i + idx];
          const hex = b.toString(16).padStart(2, '0').toUpperCase();
          cells.push(`<span class="${b === 0 ? 'hex-zero' : 'hex-bytes'}">${hex}</span>`);
        } else {
          cells.push('  ');
        }
      }
      hexGroups.push(cells.join(' '));
    }
    const hexStr = hexGroups.join('  ');

    const ascii = Array.from(chunk)
      .map(b => (b >= 0x20 && b < 0x7F)
        ? `<span class="hex-ascii">${htmlEsc(String.fromCharCode(b))}</span>`
        : '.')
      .join('');

    lines.push(`${offsetStr}  ${hexStr}  |${ascii}|`);
  }
  return lines.join('\n');
}

// ---------------------------------------------------------------------------
// Utilities
// ---------------------------------------------------------------------------
function formatBytes(n) {
  if (n < 1024)    return `${n} B`;
  if (n < 1048576) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1048576).toFixed(2)} MB`;
}

function htmlEsc(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------
document.addEventListener('DOMContentLoaded', init);
