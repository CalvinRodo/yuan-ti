/* ================================================================
   SpellScene — Spell browser with icon thumbnails on the Phaser canvas.
   Fetches spell data from the API, loads icon images as Phaser textures,
   and renders a scrollable grid of spell cards.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';
import { htmlEsc } from '../utils.js';

// Layout constants
const CARD_W = 320;
const CARD_H = 72;
const CARD_GAP = 8;
const CARD_PAD = 12;
const GRID_PAD = 16;
const SEARCH_BAR_H = 56;
const ICON_SIZE = 48;

export default class SpellScene extends Phaser.Scene {
  constructor() {
    super('SpellScene');
  }

  init() {
    this.spellData = [];
    this.filteredSpells = [];
    this.spellFilter = '';
    this.cardGroup = null;
    this.searchOverlay = null;
    this.activeIndex = -1;
  }

  create() {
    this.cameras.main.setBackgroundColor('#0d0c14');

    // Create the search overlay in DOM
    this._createSearchOverlay();

    // Load spell data
    this._loadSpells();

    // Camera scroll via mouse wheel
    this.input.on('wheel', (_pointer, _over, _dx, dy) => {
      this.cameras.main.scrollY = Phaser.Math.Clamp(
        this.cameras.main.scrollY + dy * 0.5,
        0,
        Math.max(0, this._totalHeight() - this.scale.height)
      );
    });

    // Handle resize
    this.scale.on('resize', this._onResize, this);
  }

  shutdown() {
    this.scale.off('resize', this._onResize, this);
    if (this.searchOverlay) {
      this.searchOverlay.remove();
      this.searchOverlay = null;
    }
  }

  // ---------------------------------------------------------------------------
  // Search overlay (DOM element above the canvas)
  // ---------------------------------------------------------------------------
  _createSearchOverlay() {
    const container = document.getElementById('content-area');
    this.searchOverlay = document.createElement('div');
    this.searchOverlay.id = 'spell-overlay';
    this.searchOverlay.innerHTML = `
      <input type="search" placeholder="Search spells\u2026" autocomplete="off" spellcheck="false">
      <span class="count"></span>
    `;
    container.appendChild(this.searchOverlay);

    const input = this.searchOverlay.querySelector('input');
    input.addEventListener('input', () => {
      this.spellFilter = input.value.toLowerCase();
      this._applyFilter();
      this._renderGrid();
    });
  }

  // ---------------------------------------------------------------------------
  // Data loading
  // ---------------------------------------------------------------------------
  async _loadSpells() {
    const gff = state.gffByMode.spells || 'RESOURCE.GFF';
    this.spellData = await api.fetchSpells(gff);
    this._applyFilter();

    // Load icon textures
    const toLoad = [];
    this.filteredSpells.forEach(spell => {
      if (spell.icon_number != null) {
        const key = `spell_icon_${spell.icon_number}`;
        if (!this.textures.exists(key)) {
          const url = api.imageUrl(gff, 'ICON', spell.icon_number, 0);
          this.load.image(key, url);
          toLoad.push(key);
        }
      }
    });

    if (toLoad.length > 0) {
      this.load.once('complete', () => this._renderGrid());
      this.load.start();
    } else {
      this._renderGrid();
    }
  }

  _applyFilter() {
    this.filteredSpells = this.spellFilter
      ? this.spellData.filter(s =>
          s.name.toLowerCase().includes(this.spellFilter) ||
          s.description.toLowerCase().includes(this.spellFilter))
      : [...this.spellData];

    if (this.searchOverlay) {
      const countEl = this.searchOverlay.querySelector('.count');
      countEl.textContent = this.spellFilter
        ? `${this.filteredSpells.length} / ${this.spellData.length}`
        : `${this.spellData.length} spells`;
    }
  }

  // ---------------------------------------------------------------------------
  // Rendering
  // ---------------------------------------------------------------------------
  _renderGrid() {
    // Clear existing
    if (this.cardGroup) this.cardGroup.destroy(true);
    this.cardGroup = this.add.container(0, 0);

    const viewW = this.scale.width;
    const cols = Math.max(1, Math.floor((viewW - GRID_PAD * 2 + CARD_GAP) / (CARD_W + CARD_GAP)));
    const startY = SEARCH_BAR_H + GRID_PAD;

    this.filteredSpells.forEach((spell, i) => {
      const col = i % cols;
      const row = Math.floor(i / cols);
      const x = GRID_PAD + col * (CARD_W + CARD_GAP);
      const y = startY + row * (CARD_H + CARD_GAP);

      this._renderCard(spell, x, y);
    });

    // Update camera bounds
    this.cameras.main.setBounds(0, 0, viewW, this._totalHeight());
    this.cameras.main.scrollY = 0;
  }

  _renderCard(spell, x, y) {
    // Card background
    const bg = this.add.rectangle(
      x + CARD_W / 2, y + CARD_H / 2,
      CARD_W, CARD_H, 0x1a192a
    ).setStrokeStyle(1, 0x33314a);

    bg.setInteractive({ useHandCursor: true });
    bg.on('pointerover', () => bg.setFillStyle(0x22203a));
    bg.on('pointerout', () => bg.setFillStyle(0x1a192a));

    this.cardGroup.add(bg);

    // Icon
    if (spell.icon_number != null) {
      const key = `spell_icon_${spell.icon_number}`;
      if (this.textures.exists(key)) {
        const icon = this.add.image(x + CARD_PAD + ICON_SIZE / 2, y + CARD_H / 2, key);
        const frame = icon.frame;
        const scale = Math.min(ICON_SIZE / frame.width, ICON_SIZE / frame.height);
        icon.setScale(scale);
        this.cardGroup.add(icon);
      }
    }

    // Name
    const textX = x + CARD_PAD + ICON_SIZE + 10;
    const nameText = this.add.text(textX, y + 10, spell.name, {
      fontFamily: 'sans-serif',
      fontSize: '14px',
      fontStyle: 'bold',
      color: '#e1dcd0',
      wordWrap: { width: CARD_W - CARD_PAD * 2 - ICON_SIZE - 10 },
    });
    this.cardGroup.add(nameText);

    // Description (truncated)
    const desc = (spell.description || '').slice(0, 80) + (spell.description?.length > 80 ? '\u2026' : '');
    const descText = this.add.text(textX, y + 30, desc, {
      fontFamily: 'sans-serif',
      fontSize: '11px',
      color: '#8a8499',
      wordWrap: { width: CARD_W - CARD_PAD * 2 - ICON_SIZE - 10 },
    });
    this.cardGroup.add(descText);

    // Number badge
    const numText = this.add.text(x + CARD_W - CARD_PAD, y + CARD_H - 10, `#${spell.number}`, {
      fontFamily: 'sans-serif',
      fontSize: '10px',
      color: '#4e4b64',
    }).setOrigin(1, 1);
    this.cardGroup.add(numText);
  }

  _totalHeight() {
    const viewW = this.scale.width;
    const cols = Math.max(1, Math.floor((viewW - GRID_PAD * 2 + CARD_GAP) / (CARD_W + CARD_GAP)));
    const rows = Math.ceil(this.filteredSpells.length / cols);
    return SEARCH_BAR_H + GRID_PAD + rows * (CARD_H + CARD_GAP) + GRID_PAD;
  }

  _onResize() {
    if (this.filteredSpells.length > 0) {
      this._renderGrid();
    }
  }
}
