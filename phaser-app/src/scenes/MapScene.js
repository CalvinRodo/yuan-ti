/* ================================================================
   MapScene — Region map viewer with Phaser camera pan & zoom.
   Loads the server-rendered map PNG and provides smooth
   camera-based navigation (drag to pan, scroll to zoom).
   ================================================================ */

import Phaser from 'phaser';
import { state, events } from '../config.js';
import * as api from '../api.js';

const ZOOM_MIN = 0.125;
const ZOOM_MAX = 4;
const ZOOM_STEP = 0.15;

export default class MapScene extends Phaser.Scene {
  constructor() {
    super('MapScene');
  }

  init() {
    this.mapImage = null;
    this.loadedUrl = null;
    this.isDragging = false;
    this.dragStart = { x: 0, y: 0 };
    this.loadingText = null;
  }

  create() {
    this.cameras.main.setBackgroundColor('#0d0c14');

    // Loading indicator
    const { width, height } = this.scale;
    this.loadingText = this.add.text(width / 2, height / 2, 'Loading map\u2026', {
      fontFamily: 'sans-serif', fontSize: '16px', color: '#8a8499',
    }).setOrigin(0.5).setScrollFactor(0).setDepth(100);

    // Load the map
    this._loadMap();

    // ---- Input: drag to pan ----
    this.input.on('pointerdown', (pointer) => {
      this.isDragging = true;
      this.dragStart.x = pointer.x;
      this.dragStart.y = pointer.y;
    });

    this.input.on('pointermove', (pointer) => {
      if (!this.isDragging || !pointer.isDown) return;
      const cam = this.cameras.main;
      const dx = (pointer.x - this.dragStart.x) / cam.zoom;
      const dy = (pointer.y - this.dragStart.y) / cam.zoom;
      cam.scrollX -= dx;
      cam.scrollY -= dy;
      this.dragStart.x = pointer.x;
      this.dragStart.y = pointer.y;
    });

    this.input.on('pointerup', () => {
      this.isDragging = false;
    });

    // ---- Input: scroll to zoom ----
    this.input.on('wheel', (_pointer, _over, _dx, dy) => {
      const cam = this.cameras.main;
      const factor = dy < 0 ? (1 + ZOOM_STEP) : (1 - ZOOM_STEP);
      const newZoom = Phaser.Math.Clamp(cam.zoom * factor, ZOOM_MIN, ZOOM_MAX);
      cam.setZoom(newZoom);
      this._updateZoomLabel();
    });

    // Handle resize
    this.scale.on('resize', this._onResize, this);

    // Listen for palette changes
    this._palHandler = () => this._loadMap(true);
    events.on('palette-changed', this._palHandler);
  }

  shutdown() {
    this.scale.off('resize', this._onResize, this);
    if (this._palHandler) {
      events.off('palette-changed', this._palHandler);
    }
  }

  // ---------------------------------------------------------------------------
  // Map loading
  // ---------------------------------------------------------------------------
  async _loadMap(force = false) {
    const gff = state.gffByMode.map;
    if (!gff) {
      if (this.loadingText) this.loadingText.setText('Select a region file to view its map.');
      return;
    }

    const url = `/api/gff/${encodeURIComponent(gff)}/map-render`
              + `?game=${state.game}${api.palParams()}`;

    if (!force && url === this.loadedUrl) return;

    if (this.loadingText) {
      this.loadingText.setVisible(true);
      this.loadingText.setText('Loading map\u2026');
    }

    // Remove old map image
    if (this.mapImage) {
      this.mapImage.destroy();
      this.mapImage = null;
    }

    const texKey = 'map_render_' + Date.now();
    try {
      // Fetch the image as a blob and create an object URL
      const res = await fetch(url + `&_v=${Date.now()}`);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const blob = await res.blob();
      const objUrl = URL.createObjectURL(blob);

      // Load into Phaser
      this.load.image(texKey, objUrl);
      this.load.once('complete', () => {
        URL.revokeObjectURL(objUrl);

        this.mapImage = this.add.image(0, 0, texKey).setOrigin(0, 0);
        this.loadedUrl = url;

        // Set camera bounds to the map size
        const { width, height } = this.mapImage;
        this.cameras.main.setBounds(0, 0, width, height);
        this.cameras.main.setZoom(1);
        this.cameras.main.scrollX = 0;
        this.cameras.main.scrollY = 0;

        if (this.loadingText) this.loadingText.setVisible(false);
        this._updateZoomLabel();
      });
      this.load.once('loaderror', () => {
        URL.revokeObjectURL(objUrl);
        if (this.loadingText) this.loadingText.setText('Failed to load map.');
      });
      this.load.start();
    } catch (err) {
      console.error('Map load error:', err);
      if (this.loadingText) this.loadingText.setText('Failed to load map.');
    }
  }

  _updateZoomLabel() {
    // Zoom label could be rendered as Phaser text or sent to DOM
    const pct = Math.round(this.cameras.main.zoom * 100);
    // We can display this as overlay text
    if (!this._zoomLabel) {
      this._zoomLabel = this.add.text(10, 10, '', {
        fontFamily: 'sans-serif', fontSize: '12px', color: '#8a8499',
        backgroundColor: '#16151fcc', padding: { x: 6, y: 3 },
      }).setScrollFactor(0).setDepth(50);
    }
    this._zoomLabel.setText(`Zoom: ${pct}%`);
  }

  _onResize() {
    if (this.loadingText) {
      const { width, height } = this.scale;
      this.loadingText.setPosition(width / 2, height / 2);
    }
  }
}
