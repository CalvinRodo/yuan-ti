// =============================================================================
// view-spells.js  —  Spell browser for RESOURCE.GFF / any GFF with SPIN data
// =============================================================================

let spellData    = [];   // raw fetch result
let spellFilter  = '';   // current search string
let activeSpellN = null; // currently highlighted spell number

// ---------------------------------------------------------------------------
// Public entry point — called from renderContent()
// ---------------------------------------------------------------------------

async function renderSpellGrid() {
  dom.content.scrollTop = 0;
  dom.spellGrid.classList.remove('hidden');

  // Build container once
  if (!dom.spellGrid.querySelector('#spell-search-wrap')) {
    dom.spellGrid.innerHTML = `
      <div id="spell-search-wrap">
        <input id="spell-search" type="search" placeholder="Search spells…"
               autocomplete="off" spellcheck="false">
        <span id="spell-search-count"></span>
      </div>
      <div id="spell-list"></div>
    `;
    dom.spellGrid.querySelector('#spell-search').addEventListener('input', e => {
      spellFilter = e.target.value.toLowerCase();
      renderSpellCards();
    });
  }

  // Fetch (or reuse) spell data for the current GFF
  const gff = state.currentGff;
  if (!spellData.length || spellData._gff !== gff) {
    try {
      const res = await fetch(`/api/gff/${encodeURIComponent(gff)}/spells`);
      spellData = await res.json();
      spellData._gff = gff;
    } catch {
      spellData = [];
    }
  }

  renderSpellCards();
}

// ---------------------------------------------------------------------------
// Internal — re-render card list applying current filter
// ---------------------------------------------------------------------------

function renderSpellCards() {
  const listEl    = dom.spellGrid.querySelector('#spell-list');
  const countEl   = dom.spellGrid.querySelector('#spell-search-count');
  if (!listEl) return;

  const gff = state.currentGff;

  const filtered = spellFilter
    ? spellData.filter(s =>
        s.name.toLowerCase().includes(spellFilter) ||
        s.description.toLowerCase().includes(spellFilter))
    : spellData;

  countEl.textContent = spellFilter
    ? `${filtered.length} / ${spellData.length}`
    : `${spellData.length} spells`;

  const frag = document.createDocumentFragment();

  filtered.forEach(spell => {
    const card = document.createElement('div');
    card.className = 'spell-card';
    if (spell.number === activeSpellN) card.classList.add('active');
    card.dataset.num = spell.number;

    const iconHtml = spell.icon_number != null
      ? `<img src="${imageUrl(gff, 'ICON', spell.icon_number, 0)}"
              alt="${htmlEsc(spell.name)}" loading="lazy">`
      : '';

    card.innerHTML = `
      <div class="spell-card-icon">${iconHtml}</div>
      <div class="spell-card-body">
        <div class="spell-card-name">${htmlEsc(spell.name)}</div>
        <div class="spell-card-desc">${htmlEsc(spell.description)}</div>
        <div class="spell-card-num">#${spell.number}</div>
      </div>
    `;

    card.addEventListener('click', () => {
      activeSpellN = spell.number;
      // Mark active
      listEl.querySelectorAll('.spell-card').forEach(c => c.classList.remove('active'));
      card.classList.add('active');
      // Open detail pane showing the icon image (if it exists)
      if (spell.icon_number != null) {
        const fakeRes = {
          tag:         'ICON',
          tag_url:     'ICON',
          number:      spell.icon_number,
          size:        0,
          description: spell.name,
          is_image:    true,
        };
        openDetailPane(fakeRes);
      }
    });

    frag.appendChild(card);
  });

  listEl.innerHTML = '';
  listEl.appendChild(frag);
}
