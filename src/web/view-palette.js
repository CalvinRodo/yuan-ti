/* ================================================================
   Dark Sun GFF Viewer — view-palette.js
   Palette viewer: renders every PAL/CPAL resource as a
   16-column × 16-row grid of 32×32 colour swatches.
   ================================================================ */

'use strict';

/**
 * Convert a stripped tag name (e.g. 'PAL', 'CPAL') to the
 * 4-character URL path segment used by the Flask routes.
 *   'PAL'  -> 'PAL_'   (trailing space replaced with underscore)
 *   'CPAL' -> 'CPAL'   (already 4 chars, no trailing space)
 */
function _tagToUrlSeg(strippedTag) {
  return strippedTag + '_'.repeat(4 - strippedTag.length);
}

/**
 * Build the URL for the server-rendered palette PNG.
 */
function palPngUrl(gff, strippedTag, number) {
  const tagSeg = _tagToUrlSeg(strippedTag);
  return `/api/gff/${encodeURIComponent(gff)}/pal-png/${tagSeg}/${number}?game=${state.game}`;
}

/**
 * Render the palette viewer into #palette-view.
 * Called by renderContent() when state.mode === 'palette'.
 */
function renderPaletteView() {
  const allPals = state.allPals;

  if (!allPals || allPals.length === 0) {
    dom.emptyMsg.textContent = 'No palette resources found.';
    dom.emptyState.classList.remove('hidden');
    dom.paletteView.classList.add('hidden');
    return;
  }

  dom.emptyState.classList.add('hidden');
  dom.paletteView.classList.remove('hidden');
  dom.paletteView.innerHTML = '';

  allPals.forEach(p => {
    const tag    = (p.tag || 'PAL').trim();
    const imgSrc = palPngUrl(p.gff, tag, p.number);

    const card = document.createElement('div');
    card.className = 'pal-card';

    // Title: "FILENAME.GFF — PAL #N" (above the swatch)
    const titleEl = document.createElement('div');
    titleEl.className   = 'pal-title';
    titleEl.textContent = `${p.gff} — ${tag} #${p.number}`;

    // Swatch image (below the title)
    const swatchWrap = document.createElement('div');
    swatchWrap.className = 'pal-swatch-wrap';

    const spinner = document.createElement('div');
    spinner.className = 'pal-spinner';

    const img = document.createElement('img');
    img.alt = titleEl.textContent;

    img.addEventListener('load', () => {
      img.classList.add('loaded');
      spinner.remove();
    });
    img.addEventListener('error', () => {
      spinner.remove();
      const err = document.createElement('span');
      err.className   = 'pal-error';
      err.textContent = 'Error loading palette';
      swatchWrap.appendChild(err);
    });

    swatchWrap.appendChild(spinner);
    swatchWrap.appendChild(img);

    card.appendChild(titleEl);
    card.appendChild(swatchWrap);
    dom.paletteView.appendChild(card);

    // Set src after the element is in the DOM so the browser
    // can correctly intersect-test it for any deferred loading.
    img.src = imgSrc;
  });
}
