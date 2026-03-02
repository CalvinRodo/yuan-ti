/* ================================================================
   PrefsScene — Global preferences page.
   Uses a DOM panel for the settings UI.
   ================================================================ */

import Phaser from 'phaser';
import { prefs, savePrefs } from '../config.js';
import { showDomPanel } from '../utils.js';

const PREFS_DEFAULTS = { spriteScale: 3 };

export default class PrefsScene extends Phaser.Scene {
  constructor() {
    super('PrefsScene');
    this._built = false;
  }

  create() {
    const panel = showDomPanel('PrefsScene');
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');
  }

  shutdown() {}

  _build(panel) {
    const scaleButtons = [1, 2, 3, 4, 6, 8].map(v =>
      `<button class="prefs-scale-btn${prefs.spriteScale === v ? ' active' : ''}" data-scale="${v}">${v}\u00d7</button>`
    ).join('');

    panel.innerHTML = `
      <div class="prefs-page">
        <h2 class="prefs-title">Preferences</h2>
        <section class="prefs-section">
          <h3 class="prefs-section-title">Sprites</h3>
          <div class="prefs-row">
            <label class="prefs-label">
              Sprite viewer scale
              <span class="prefs-hint dim">Pixel scale used when displaying sprite frames</span>
            </label>
            <div class="prefs-control">
              <div class="prefs-scale-btns">${scaleButtons}</div>
              <span class="prefs-current-val">${prefs.spriteScale}\u00d7</span>
            </div>
          </div>
        </section>
        <div class="prefs-reset-row">
          <button class="prefs-reset-btn">Reset to defaults</button>
          <span class="prefs-saved-msg hidden">Saved</span>
        </div>
      </div>
    `;

    // Scale buttons
    const scaleGroup = panel.querySelector('.prefs-scale-btns');
    const scaleVal = panel.querySelector('.prefs-current-val');
    scaleGroup.addEventListener('click', (ev) => {
      const btn = ev.target.closest('.prefs-scale-btn');
      if (!btn) return;
      const v = Number(btn.dataset.scale);
      prefs.spriteScale = v;
      savePrefs();
      scaleGroup.querySelectorAll('.prefs-scale-btn').forEach(b =>
        b.classList.toggle('active', Number(b.dataset.scale) === v));
      scaleVal.textContent = `${v}\u00d7`;
      this._flashSaved(panel);
    });

    // Reset
    panel.querySelector('.prefs-reset-btn').addEventListener('click', () => {
      Object.assign(prefs, { ...PREFS_DEFAULTS });
      savePrefs();
      this._built = false;
      panel.innerHTML = '';
      this._build(panel);
    });
  }

  _flashSaved(panel) {
    const msg = panel.querySelector('.prefs-saved-msg');
    if (!msg) return;
    msg.classList.remove('hidden');
    clearTimeout(msg._t);
    msg._t = setTimeout(() => msg.classList.add('hidden'), 1500);
  }
}
