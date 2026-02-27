/* ================================================================
   view-prefs.js — Global Preferences page
   Reads / writes the `prefs` object (defined in app.js) and calls
   savePrefs() to persist changes to localStorage.
   ================================================================ */

'use strict';

let prefsBuilt = false;

function renderPrefsView() {
  const view = document.getElementById('prefs-view');
  if (!view) return;
  view.classList.remove('hidden');

  if (prefsBuilt) return;   // static UI — only build once
  prefsBuilt = true;

  view.innerHTML = `
<div class="prefs-page">
  <h2 class="prefs-title">Preferences</h2>

  <section class="prefs-section">
    <h3 class="prefs-section-title">Sprites</h3>

    <div class="prefs-row">
      <label class="prefs-label" for="pref-sprite-scale">
        Sprite viewer scale
        <span class="prefs-hint dim">Pixel scale used when displaying sprite frames</span>
      </label>
      <div class="prefs-control">
        <div class="prefs-scale-btns" id="pref-sprite-scale">
          ${[1, 2, 3, 4, 6, 8].map(v => `
            <button class="prefs-scale-btn${prefs.spriteScale === v ? ' active' : ''}"
                    data-scale="${v}">${v}&times;</button>
          `).join('')}
        </div>
        <span class="prefs-current-val" id="pref-sprite-scale-val">${prefs.spriteScale}&times;</span>
      </div>
    </div>
  </section>

  <div class="prefs-reset-row">
    <button id="prefs-reset-btn" class="spr-btn">Reset to defaults</button>
    <span id="prefs-saved-msg" class="prefs-saved-msg hidden">Saved</span>
  </div>
</div>`;

  // Scale buttons
  const scaleGroup = view.querySelector('#pref-sprite-scale');
  const scaleVal   = view.querySelector('#pref-sprite-scale-val');
  scaleGroup.addEventListener('click', ev => {
    const btn = ev.target.closest('.prefs-scale-btn');
    if (!btn) return;
    const v = Number(btn.dataset.scale);
    prefs.spriteScale = v;
    savePrefs();
    scaleGroup.querySelectorAll('.prefs-scale-btn').forEach(b =>
      b.classList.toggle('active', Number(b.dataset.scale) === v)
    );
    scaleVal.textContent = `${v}×`;
    _flashPrefsSaved(view);
  });

  // Reset
  view.querySelector('#prefs-reset-btn').addEventListener('click', () => {
    Object.assign(prefs, { ...PREFS_DEFAULTS });
    savePrefs();
    // Rebuild the UI to reflect reset values
    prefsBuilt = false;
    view.innerHTML = '';
    renderPrefsView();
  });
}

function _flashPrefsSaved(view) {
  const msg = view.querySelector('#prefs-saved-msg');
  if (!msg) return;
  msg.classList.remove('hidden');
  clearTimeout(msg._t);
  msg._t = setTimeout(() => msg.classList.add('hidden'), 1500);
}
