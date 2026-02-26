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

// Animation state
const animState = {
  playing:    false,
  timer:      null,
  startFrame: 0,
  endFrame:   0,
};

// Frame count cache for the Sprites view: "gff|tag_url|number" -> count
const imageFrameCache = new Map();

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
  dom.resFilterTag.addEventListener('change', () => renderResourceTable());
}

function bindDetailPaneEvents() {
  _bindAnimEvents();

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
  let imageResources = state.resources.filter(r => r.is_image);
  if (state.imageTagFilter)
    imageResources = imageResources.filter(r => r.tag.trim() === state.imageTagFilter);

  if (imageResources.length === 0) {
    dom.imageGrid.innerHTML =
      '<div style="grid-column:1/-1;color:var(--text-faint);padding:32px 0;text-align:center;">' +
      (state.imageTagFilter ? `No ${state.imageTagFilter} image resources in this GFF file.` : 'No image resources (BMP, TILE, ICON) found in this GFF file.') + '</div>';
    dom.imageGrid.classList.remove('hidden');
    return;
  }

  if (lazyObserver) { lazyObserver.disconnect(); lazyObserver = null; }

  const frag = document.createDocumentFragment();
  imageResources.forEach(res => {
    const frameCount = imageFrameCache.get(`${state.currentGff}|${res.tag_url}|${res.number}`);
    frag.appendChild(createImageCard(res, frameCount));
  });

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

function createImageCard(res, frameCount) {
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
    (frameCount > 1 ? `<span class="img-frame-badge">${frameCount}f</span>` : '') +
    `</div>` +
    `<div class="img-card-footer">` +
    `<div class="img-card-tag">${htmlEsc(res.tag.trim())}</div>` +
    `<div class="img-card-num">#${res.number}</div>` +
    `<div class="img-card-size">${formatBytes(res.size)}</div>` +
    `</div>`;

  card.addEventListener('click', () => {
    const container = card.closest('#image-grid, #img-sprite-view') || dom.imageGrid;
    container.querySelectorAll('.img-card.selected').forEach(c => c.classList.remove('selected'));
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
  let imageResources   = state.resources.filter(r => r.is_image);
  if (state.imageTagFilter)
    imageResources = imageResources.filter(r => r.tag.trim() === state.imageTagFilter);

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
  const tagF = (dom.resFilterTag.value || '').trim().toLowerCase();

  let rows = state.resources.filter(res => {
    if (tagF && !res.tag.trim().toLowerCase().includes(tagF)) return false;
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
  stopAnimation();
  state.detail.resource   = res;
  state.detail.frames     = [];
  state.detail.frameIndex = 0;
  state.detail.preloaded  = [];

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
  stopAnimation();
  state.detail.resource  = null;
  state.detail.preloaded = [];
  dom.detailPane.classList.add('hidden');
  // Clear the canvas
  const canvas = dom.detailImg;
  const ctx = canvas.getContext('2d');
  if (ctx) ctx.clearRect(0, 0, canvas.width, canvas.height);
  canvas.style.display = 'none';
  dom.tableTbody.querySelectorAll('tr.selected').forEach(r => r.classList.remove('selected'));
  dom.imageGrid.querySelectorAll('.img-card.selected').forEach(c => c.classList.remove('selected'));
  dom.imgSpriteView.querySelectorAll('.img-card.selected').forEach(c => c.classList.remove('selected'));
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
      `/api/gff/${encodeURIComponent(state.currentGff)}/image/${res.tag_url}/${res.number}/info?game=${state.game}&v=${palVersion}`);
    const info = await r.json();
    if (!info.error && info.frames?.length) {
      state.detail.frames = info.frames;
    }
  } catch { /* ignore */ }

  const count = state.detail.frames.length;

  // Preload all frames into memory so animation is seamless
  const gff    = state.currentGff;
  const numFrames = count || 1; // at least frame 0
  let firstReady = false;

  for (let i = 0; i < numFrames; i++) {
    const img = new Image();
    img.src   = imageUrl(gff, res.tag_url, res.number, i);
    state.detail.preloaded[i] = img;
    if (i === 0) {
      img.onload = () => {
        if (!firstReady) {
          firstReady = true;
          renderFrameInPane(0);
        }
      };
      img.onerror = () => {
        dom.detailImgSpinner.style.display = 'none';
        const ctx = dom.detailImg.getContext('2d');
        if (ctx) ctx.clearRect(0, 0, dom.detailImg.width, dom.detailImg.height);
        dom.detailFrameInfo.textContent = '\u26a0 Failed to load';
      };
    }
  }

  // Configure animation controls
  if (count > 1) {
    dom.animControls.classList.remove('hidden');
    animState.startFrame = 0;
    animState.endFrame   = count - 1;
    dom.animRangeStart.min   = 1;
    dom.animRangeStart.max   = count;
    dom.animRangeStart.value = 1;
    dom.animRangeEnd.min     = 1;
    dom.animRangeEnd.max     = count;
    dom.animRangeEnd.value   = count;
    if (count > 3) {
      dom.animRange.classList.remove('hidden');
    } else {
      dom.animRange.classList.add('hidden');
    }
  } else {
    dom.animControls.classList.add('hidden');
  }
}

function renderFrameInPane(frameIdx) {
  const res = state.detail.resource;
  if (!res) return;
  const frames    = state.detail.frames;
  const preloaded = state.detail.preloaded;
  const count     = frames.length;

  frameIdx = Math.max(0, Math.min(frameIdx, Math.max(count - 1, 0)));
  state.detail.frameIndex = frameIdx;

  dom.detailPrev.disabled = (frameIdx <= 0);
  dom.detailNext.disabled = (frameIdx >= count - 1);

  if (animState.playing) {
    dom.animStatus.textContent = `\u25b6 Frame ${frameIdx + 1}`;
  }

  const img = preloaded[frameIdx];
  if (!img) return;

  const drawFrame = () => {
    const scale = state.imageScale || 1;
    const w = img.naturalWidth;
    const h = img.naturalHeight;

    const canvas = dom.detailImg;
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width  = w;
      canvas.height = h;
    }
    canvas.style.width  = (w * scale) + 'px';
    canvas.style.height = (h * scale) + 'px';

    const ctx = canvas.getContext('2d');
    ctx.clearRect(0, 0, w, h);
    ctx.drawImage(img, 0, 0);

    canvas.style.display        = 'block';
    dom.detailImgSpinner.style.display = 'none';

    if (count > 0) {
      const f = frames[frameIdx];
      dom.detailFrameInfo.textContent = `Frame ${frameIdx + 1} / ${count}`;
      dom.detailImgDims.textContent   = `${w} \u00d7 ${h} px`;
    } else {
      dom.detailFrameInfo.textContent = '';
      dom.detailImgDims.textContent   = `${w} \u00d7 ${h} px`;
    }
  };

  if (img.complete && img.naturalWidth > 0) {
    drawFrame();
  } else {
    // Still loading — show spinner and draw once ready
    dom.detailImg.style.display        = 'none';
    dom.detailImgSpinner.style.display = 'block';
    img.onload  = drawFrame;
    img.onerror = () => {
      dom.detailImgSpinner.style.display = 'none';
      dom.detailFrameInfo.textContent    = '\u26a0 Failed to load';
    };
  }
}

function navigateFrame(delta) {
  stopAnimation();
  renderFrameInPane(state.detail.frameIndex + delta);
}

// ---------------------------------------------------------------------------
// Image type filter bar
// ---------------------------------------------------------------------------

function buildImageFilterBar() {
  const tags = [...new Set(
    state.resources.filter(r => r.is_image).map(r => r.tag.trim())
  )].sort();

  if (tags.length <= 1) {
    dom.imgFilterBar.classList.add('hidden');
    return;
  }

  dom.imgFilterBar.innerHTML = '';
  const makeBtn = (label, value) => {
    const btn = document.createElement('button');
    btn.className = 'img-tag-btn' + (state.imageTagFilter === value ? ' active' : '');
    btn.dataset.tag = value;
    btn.textContent = label;
    btn.addEventListener('click', () => {
      state.imageTagFilter = value;
      dom.imgFilterBar.querySelectorAll('.img-tag-btn')
        .forEach(b => b.classList.toggle('active', b.dataset.tag === value));
      renderContent();
    });
    return btn;
  };
  dom.imgFilterBar.appendChild(makeBtn('All', ''));
  tags.forEach(t => dom.imgFilterBar.appendChild(makeBtn(t, t)));
  dom.imgFilterBar.classList.remove('hidden');
}

// ---------------------------------------------------------------------------
// Sprite sheet view (multi-frame images only)
// ---------------------------------------------------------------------------

async function renderSpriteView() {
  const gff  = state.currentGff;
  let imageResources = state.resources.filter(r => r.is_image);
  if (state.imageTagFilter)
    imageResources = imageResources.filter(r => r.tag.trim() === state.imageTagFilter);

  // Check whether all frame counts are already in cache (seeded from resources list)
  const uncached = imageResources.filter(res =>
    !imageFrameCache.has(`${gff}|${res.tag_url}|${res.number}`)
  );

  if (uncached.length > 0) {
    dom.imgSpriteView.innerHTML =
      '<div class="sprite-loading"><div class="spinner"></div><span>Scanning frames\u2026</span></div>';
    dom.imgSpriteView.classList.remove('hidden');

    // Batch uncached ones — limit to 8 concurrent to avoid flooding the server
    const CONCURRENCY = 8;
    for (let i = 0; i < uncached.length; i += CONCURRENCY) {
      await Promise.all(uncached.slice(i, i + CONCURRENCY).map(async res => {
        const key = `${gff}|${res.tag_url}|${res.number}`;
        try {
          const r = await fetch(
            `/api/gff/${encodeURIComponent(gff)}/image/${res.tag_url}/${res.number}/info` +
            `?game=${state.game}&v=${palVersion}`);
          const info = await r.json();
          imageFrameCache.set(key, info.frames?.length ?? 1);
        } catch {
          imageFrameCache.set(key, 1);
        }
      }));
    }

    if (state.mode !== 'images' || state.imageView !== 'sprites') return;
  } else {
    dom.imgSpriteView.classList.remove('hidden');
  }

  const sprites = imageResources.filter(res =>
    (imageFrameCache.get(`${gff}|${res.tag_url}|${res.number}`) ?? 1) > 1
  );

  if (sprites.length === 0) {
    dom.imgSpriteView.innerHTML =
      `<div class="img-empty">No sprite sheets found` +
      (state.imageTagFilter ? ` for \u201c${state.imageTagFilter}\u201d` : '') +
      `.</div>`;
    return;
  }

  if (lazyObserver) { lazyObserver.disconnect(); lazyObserver = null; }

  const frag = document.createDocumentFragment();
  sprites.forEach(res => {
    const frameCount = imageFrameCache.get(`${gff}|${res.tag_url}|${res.number}`);
    frag.appendChild(createImageCard(res, frameCount));
  });
  dom.imgSpriteView.innerHTML = '';
  dom.imgSpriteView.appendChild(frag);

  lazyObserver = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      if (!entry.isIntersecting) return;
      const img = entry.target.querySelector('img[data-src]');
      if (!img) return;
      loadCardImage(img);
      lazyObserver.unobserve(entry.target);
    });
  }, { rootMargin: '200px' });
  dom.imgSpriteView.querySelectorAll('.img-card').forEach(card => lazyObserver.observe(card));
}

// ---------------------------------------------------------------------------
// Animation
// ---------------------------------------------------------------------------

function _animGetDuration() {
  const v = parseInt(dom.animDuration.value, 10);
  return Number.isFinite(v) && v >= 50 ? Math.min(v, 10000) : 1000;
}

function _bindAnimEvents() {
  dom.animPlay.addEventListener('click', startAnimation);
  dom.animStop.addEventListener('click', stopAnimation);

  // Sync range inputs to animState while not playing
  dom.animRangeStart.addEventListener('change', () => {
    if (animState.playing) return;
    const count = state.detail.frames.length;
    let v = parseInt(dom.animRangeStart.value, 10);
    if (!Number.isFinite(v)) v = 1;
    v = Math.max(1, Math.min(v, count));
    dom.animRangeStart.value = v;
    animState.startFrame = v - 1;
    if (animState.startFrame > animState.endFrame) {
      animState.endFrame = animState.startFrame;
      dom.animRangeEnd.value = v;
    }
  });
  dom.animRangeEnd.addEventListener('change', () => {
    if (animState.playing) return;
    const count = state.detail.frames.length;
    let v = parseInt(dom.animRangeEnd.value, 10);
    if (!Number.isFinite(v)) v = count;
    v = Math.max(1, Math.min(v, count));
    dom.animRangeEnd.value = v;
    animState.endFrame = v - 1;
    if (animState.endFrame < animState.startFrame) {
      animState.startFrame = animState.endFrame;
      dom.animRangeStart.value = v;
    }
  });
}

function startAnimation() {
  if (animState.playing) return;
  const frames = state.detail.frames;
  if (!frames || frames.length < 2) return;

  // Read current range from inputs
  const count = frames.length;
  let s = parseInt(dom.animRangeStart.value, 10) - 1;
  let e = parseInt(dom.animRangeEnd.value, 10) - 1;
  if (!Number.isFinite(s) || s < 0) s = 0;
  if (!Number.isFinite(e) || e >= count) e = count - 1;
  if (s > e) s = 0;
  animState.startFrame = s;
  animState.endFrame   = e;

  // Start from current frame if it's in range, otherwise from start
  if (state.detail.frameIndex < s || state.detail.frameIndex > e) {
    renderFrameInPane(s);
  }

  animState.playing = true;
  dom.animPlay.classList.add('hidden');
  dom.animStop.classList.remove('hidden');
  dom.animStatus.textContent = `\u25b6 Frame ${state.detail.frameIndex + 1}`;

  animState.timer = setTimeout(_animTick, _animGetDuration());
}

function stopAnimation() {
  animState.playing = false;
  if (animState.timer) { clearTimeout(animState.timer); animState.timer = null; }
  if (dom.animPlay) {
    dom.animPlay.classList.remove('hidden');
    dom.animStop.classList.add('hidden');
    dom.animStatus.textContent = '';
  }
}

function _animTick() {
  if (!animState.playing) return;
  const frames = state.detail.frames;
  if (!frames || frames.length === 0) { stopAnimation(); return; }

  let next = state.detail.frameIndex + 1;
  if (next > animState.endFrame) next = animState.startFrame;
  renderFrameInPane(next);

  animState.timer = setTimeout(_animTick, _animGetDuration());
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
