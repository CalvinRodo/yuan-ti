/* ================================================================
   PaletteScene — Renders palette grids on the Phaser canvas.
   Loads palette PNG images from the API and displays them in a
   scrollable grid with labels.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';

const PAL_SIZE = 256;
const GAP = 24;
const LABEL_H = 20;
const PAD = 20;

export default class PaletteScene extends Phaser.Scene {
  constructor() {
    super('PaletteScene');
  }

  init() {
    this.palEntries = [];
  }

  create() {
    this.cameras.main.setBackgroundColor('#0d0c14');

    const allPals = state.allPals;
    if (!allPals || allPals.length === 0) {
      this.add.text(this.scale.width / 2, this.scale.height / 2,
        'No palette resources found.', {
          fontFamily: 'sans-serif', fontSize: '16px', color: '#8a8499',
        }).setOrigin(0.5).setScrollFactor(0);
      return;
    }

    this._renderPalettes(allPals);

    // Scroll
    this.input.on('wheel', (_p, _o, _dx, dy) => {
      this.cameras.main.scrollY = Phaser.Math.Clamp(
        this.cameras.main.scrollY + dy * 0.8,
        0,
        Math.max(0, this._totalH - this.scale.height)
      );
    });

    this.scale.on('resize', this._onResize, this);
  }

  shutdown() {
    this.scale.off('resize', this._onResize, this);
  }

  _renderPalettes(allPals) {
    const viewW = this.scale.width;
    const cols = Math.max(1, Math.floor((viewW - PAD * 2 + GAP) / (PAL_SIZE + GAP)));

    allPals.forEach((p, i) => {
      const tag = (p.tag || 'PAL').trim();
      const key = `pal_${p.gff}_${tag}_${p.number}`;
      const url = api.palPngUrl(p.gff, tag, p.number);

      const col = i % cols;
      const row = Math.floor(i / cols);
      const cx = PAD + col * (PAL_SIZE + GAP) + PAL_SIZE / 2;
      const cy = PAD + row * (PAL_SIZE + LABEL_H + GAP) + PAL_SIZE / 2 + LABEL_H;

      // Label
      this.add.text(cx, cy - PAL_SIZE / 2 - LABEL_H + 4,
        `${p.gff} — ${tag} #${p.number}`, {
          fontFamily: 'sans-serif', fontSize: '13px', color: '#8a8499',
        }).setOrigin(0.5, 0);

      // Load texture
      if (!this.textures.exists(key)) {
        this.load.image(key, url);
      }

      this.palEntries.push({ key, cx, cy });
    });

    // Render once loaded
    this.load.once('complete', () => {
      this.palEntries.forEach(({ key, cx, cy }) => {
        if (this.textures.exists(key)) {
          this.add.image(cx, cy, key);
        }
      });
    });
    this.load.start();

    // Camera bounds
    const rows = Math.ceil(allPals.length / cols);
    this._totalH = PAD + rows * (PAL_SIZE + LABEL_H + GAP) + PAD;
    this.cameras.main.setBounds(0, 0, viewW, this._totalH);
  }

  _onResize() {
    // Could re-layout, but for simplicity just update bounds
  }
}
