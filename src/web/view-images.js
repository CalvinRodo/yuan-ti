/* ================================================================
   Dark Sun GFF Viewer — view-images.js
   Image grid, resource table, and detail pane (image + hex views).
   Depends on globals from app.js: state, dom, palVersion,
   palParams(), imageUrl(), rawUrl(), formatBytes(), htmlEsc()
   ================================================================ */

'use strict';

// IntersectionObserver for lazy-loading image cards
let lazyObserver = null;

// Sort state for the resource table
let tableSort = { col: null, dir: 1 };

// ---------------------------------------------------------------------------
// Detail pane resize + collapse
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Resource table — filter + sort event binding
// ---------------------------------------------------------------------------

function bindResourceTableEvents() {
  // Click sortable column headers
  document.querySelectorAll('#resource-table thead th[data-col]').forEach(th => {
    th.addEventListener('click', () => {
      const col = th.dataset.col;
      if (tableSort.col === col) {
        tableSort.dir *= -1;
      } else {
        tableSort.col = col;
        tableSort.dir = 1;
      }
      renderResourceTable();
    });
  });

  // Filter inputs
  dom.resFilterTag.addEventListener('input',  () => renderResourceTable());
  dom.resFilterDesc.addEventListener('input', () => renderResourceTable());
}

function bindDetailPaneEvents() {
  // Collapse / expand toggle
  dom.detailToggle.addEventListener('click', () => {
    const collapsed = dom.detailPane.classList.toggle('collapsed');
    dom.detailToggle.textContent = collapsed ? '›' : '‹';
    dom.detailToggle.title       = collapsed ? 'Expand panel' : 'Collapse panel';
  });
  // Prevent the button's mousedown from starting a resize drag
  dom.detailToggle.addEventListener('mousedown', e => e.stopPropagation());

  // Drag-to-resize
  let resizing = false, startX = 0, startW = 0;

  dom.detailResizer.addEventListener('mousedown', e => {
    if (dom.detailPane.classList.contains('collapsed')) return;
    resizing = true;
    startX   = e.clientX;
    startW   = dom.detailPane.offsetWidth;
    document.body.style.cursor     = 'ew-resize';
    document.body.style.userSelect = 'none';
    e.preventDefault();
  });

  document.addEventListener('mousemove', e => {
    if (!resizing) return;
    const delta = startX - e.clientX;   // drag left → wider
    const newW  = Math.max(200, Math.min(startW + delta, window.innerWidth * 0.85));
    dom.detailPane.style.width = newW + 'px';
  });

  document.addEventListener('mouseup', () => {
    if (!resizing) return;
    resizing = false;
    document.body.style.cursor     = '';
    document.body.style.userSelect = '';
  });
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
// Image list view
// ---------------------------------------------------------------------------

function renderImageList() {
  dom.content.scrollTop = 0;
  const gff            = state.currentGff;
  const imageResources = state.resources.filter(r => r.is_image);

  if (!imageResources.length) {
    dom.imageListWrap.innerHTML =
      '<div style="padding:32px 0;color:var(--text-faint);text-align:center">No image resources found.</div>';
    dom.imageListWrap.classList.remove('hidden');
    return;
  }

  dom.imageListTbody.innerHTML = '';
  const frag = document.createDocumentFragment();

  imageResources.forEach(res => {
    const tr = document.createElement('tr');
    if (state.detail.resource?.tag_url === res.tag_url &&
        state.detail.resource?.number  === res.number) {
      tr.classList.add('selected');
    }
    tr.innerHTML =
      `<td class="td-thumb img-list-thumb"><img src="${imageUrl(gff, res.tag_url, res.number, 0)}" loading="lazy"></td>` +
      `<td class="td-tag">${htmlEsc(res.tag.trim())}</td>` +
      `<td class="td-num">${res.number}</td>` +
      `<td class="td-size">${formatBytes(res.size)}</td>`;

    tr.addEventListener('click', () => {
      dom.imageListTbody.querySelectorAll('tr.selected').forEach(r => r.classList.remove('selected'));
      tr.classList.add('selected');
      openDetailPane(res);
    });
    frag.appendChild(tr);
  });

  dom.imageListTbody.appendChild(frag);
  dom.imageListWrap.classList.remove('hidden');
}

// ---------------------------------------------------------------------------
// Resource table
// ---------------------------------------------------------------------------

function renderResourceTable() {
  dom.content.scrollTop = 0;
  dom.tableTbody.innerHTML = '';
  const gff = state.currentGff;

  // ---- filter ----
  const tagF  = (dom.resFilterTag.value  || '').trim().toLowerCase();
  const descF = (dom.resFilterDesc.value || '').trim().toLowerCase();

  let rows = state.resources.filter(res => {
    if (tagF  && !res.tag.trim().toLowerCase().includes(tagF))   return false;
    if (descF && !res.description.toLowerCase().includes(descF)) return false;
    return true;
  });

  // ---- sort ----
  if (tableSort.col) {
    const col = tableSort.col;
    const d   = tableSort.dir;
    rows = rows.slice().sort((a, b) => {
      let av = a[col], bv = b[col];
      if (typeof av === 'string') { av = av.trim().toLowerCase(); bv = bv.trim().toLowerCase(); }
      return av < bv ? -d : av > bv ? d : 0;
    });
  }

  // ---- update sort arrows on headers ----
  document.querySelectorAll('#resource-table thead th[data-col]').forEach(th => {
    th.classList.remove('th-sort-asc', 'th-sort-desc');
    if (th.dataset.col === tableSort.col) {
      th.classList.add(tableSort.dir === 1 ? 'th-sort-asc' : 'th-sort-desc');
    }
  });

  // ---- update row count ----
  dom.resFilterCount.textContent =
    rows.length < state.resources.length ? `${rows.length} / ${state.resources.length}` : '';

  // ---- render rows ----
  const frag = document.createDocumentFragment();

  rows.forEach(res => {
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
  dom.resFilterBar.classList.remove('hidden');
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

  // Ensure pane is visible and expanded
  dom.detailPane.classList.remove('hidden', 'collapsed');
  dom.detailToggle.textContent = '‹';
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
  if (dom.imageListTbody) {
    dom.imageListTbody.querySelectorAll('tr.selected').forEach(r => r.classList.remove('selected'));
  }
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

  // Auto-fit the panel wide enough that the hex pre needs no horizontal scrollbar
  requestAnimationFrame(() => {
    const resizerW = dom.detailResizer.offsetWidth;
    const needed   = dom.hexPre.scrollWidth + resizerW + 2;
    dom.detailPane.style.width = Math.max(440, needed) + 'px';
  });
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
