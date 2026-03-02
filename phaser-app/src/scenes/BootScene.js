/* ================================================================
   BootScene — Initial loading screen shown while the app boots.
   Transitions to the first real scene once main.js calls activateMode().
   ================================================================ */

import Phaser from 'phaser';

export default class BootScene extends Phaser.Scene {
  constructor() {
    super('BootScene');
  }

  create() {
    const { width, height } = this.scale;
    this.add.text(width / 2, height / 2 - 20, 'GFF Viewer', {
      fontFamily: 'sans-serif',
      fontSize: '24px',
      color: '#c8902b',
    }).setOrigin(0.5);

    this.add.text(width / 2, height / 2 + 16, 'Loading\u2026', {
      fontFamily: 'sans-serif',
      fontSize: '14px',
      color: '#8a8499',
    }).setOrigin(0.5);
  }
}
