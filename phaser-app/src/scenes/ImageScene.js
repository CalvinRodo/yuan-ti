/* ================================================================
   ImageScene — Image browser rendered on the Phaser canvas.
   Displays thumbnails of all image resources in a scrollable grid.
   Clicking a thumbnail shows a detail view with frame navigation.
   ================================================================ */

import Phaser from 'phaser';
import { state, events } from '../config.js';
import * as api from '../api.js';
import { formatBytes } from '../utils.js';

const CARD_SIZE = 148;
const CARD_GAP = 8;
const GRID_PAD = 12;

export default class ImageScene extends Phaser.Scene {
  constructor() {
    super('ImageScene');
  }

  init() {
    this.imageResources = [];
    this.cards = [];
    this.detailSprite = null;
    this.detailActive = false;
    this.detailResource = null;
    this.detailFrameIdx = 0;
    this.detailFrameCount = 0;
    this.loadQueue = [];
    this.loadBatchSize = 20;
    this.loadedCount = 0;
  }

  create() {
    this.cameras.main.setBackgroundColor('#0d0c14');

    // Filter image resources
    this.imageResources = state.resources.filter(r => r.is_image);

    if (this.imageResources.length === 0) {
      this.add.text(this.scale.width / 2, this.scale.height / 2,
        state.gffByMode.images
          ? 'No image resources found.'
          : 'Select a GFF file to browse images.',
        { fontFamily: 'sans-serif', fontSize: '16px', color: '#8a8499' }
      ).setOrigin(0.5).setScrollFactor(0);
      return;
    }

    this._renderGrid();

    // Camera scroll
    this.input.on('wheel', (_p, _o, _dx, dy) => {
      this.cameras.main.scrollY = Phaser.Math.Clamp(
        this.cameras.main.scrollY + dy * 0.8,
        0,
        Math.max(0, this._totalHeight() - this.scale.height)
      );
    });

    // Handle resize
    this.scale.on('resize', this._onResize, this);
  }

  shutdown() {
    this.scale.off('resize', this._onResize, this);
    this._closeDetail();
  }

  // ---------------------------------------------------------------------------
  // Grid rendering
  // ---------------------------------------------------------------------------
  _renderGrid() {
    // Clear existing
    this.cards.forEach(c => {
      if (c.bg) c.bg.destroy();
      if (c.img) c.img.destroy();
      if (c.label) c.label.destroy();
    });
    this.cards = [];

    const gff = state.gffByMode.images;
    if (!gff) return;

    const viewW = this.scale.width;
    const cols = Math.max(1, Math.floor((viewW - GRID_PAD * 2 + CARD_GAP) / (CARD_SIZE + CARD_GAP)));

    this.imageResources.forEach((res, i) => {
      const col = i % cols;
      const row = Math.floor(i / cols);
      const cx = GRID_PAD + col * (CARD_SIZE + CARD_GAP) + CARD_SIZE / 2;
      const cy = GRID_PAD + row * (CARD_SIZE + CARD_GAP) + CARD_SIZE / 2;

      // Card background
      const bg = this.add.rectangle(cx, cy, CARD_SIZE, CARD_SIZE, 0x1a192a)
        .setStrokeStyle(1, 0x33314a)
        .setInteractive({ useHandCursor: true });

      bg.on('pointerover', () => bg.setFillStyle(0x22203a));
      bg.on('pointerout', () => bg.setFillStyle(0x1a192a));
      bg.on('pointerdown', () => this._showDetail(res));

      // Label
      const label = this.add.text(cx, cy + CARD_SIZE / 2 - 8,
        `${res.tag.trim()} #${res.number}`, {
          fontFamily: 'sans-serif', fontSize: '10px', color: '#8a8499',
          align: 'center',
        }).setOrigin(0.5, 1);

      this.cards.push({ bg, label, img: null, res, cx, cy });
    });

    // Load thumbnails in batches
    this._loadThumbnailBatch(0);

    // Camera bounds
    this.cameras.main.setBounds(0, 0, viewW, this._totalHeight());
    this.cameras.main.scrollY = 0;
  }

  _loadThumbnailBatch(startIdx) {
    const gff = state.gffByMode.images;
    if (!gff) return;

    const end = Math.min(startIdx + this.loadBatchSize, this.imageResources.length);
    let needsStart = false;

    for (let i = startIdx; i < end; i++) {
      const res = this.imageResources[i];
      const key = `thumb_${gff}_${res.tag_url}_${res.number}_v${state.palVersion}`;

      if (!this.textures.exists(key)) {
        const url = api.imageUrl(gff, res.tag_url, res.number, 0);
        this.load.image(key, url);
        needsStart = true;
      }
      this.cards[i]._texKey = key;
    }

    const onComplete = () => {
      for (let i = startIdx; i < end; i++) {
        const card = this.cards[i];
        if (!card) continue;
        const key = card._texKey;
        if (this.textures.exists(key)) {
          const img = this.add.image(card.cx, card.cy - 8, key);
          const frame = img.frame;
          const maxDim = CARD_SIZE - 24;
          const scale = Math.min(maxDim / frame.width, maxDim / frame.height, 2);
          img.setScale(scale);
          card.img = img;
        }
      }
      // Load next batch
      if (end < this.imageResources.length) {
        this._loadThumbnailBatch(end);
      }
    };

    if (needsStart) {
      this.load.once('complete', onComplete);
      this.load.start();
    } else {
      onComplete();
    }
  }

  _totalHeight() {
    const viewW = this.scale.width;
    const cols = Math.max(1, Math.floor((viewW - GRID_PAD * 2 + CARD_GAP) / (CARD_SIZE + CARD_GAP)));
    const rows = Math.ceil(this.imageResources.length / cols);
    return GRID_PAD + rows * (CARD_SIZE + CARD_GAP) + GRID_PAD;
  }

  // ---------------------------------------------------------------------------
  // Detail overlay
  // ---------------------------------------------------------------------------
  _showDetail(res) {
    this._closeDetail();
    this.detailActive = true;
    this.detailResource = res;
    this.detailFrameIdx = 0;

    const gff = state.gffByMode.images;
    const { width, height } = this.scale;

    // Dim background
    this._detailBg = this.add.rectangle(width / 2, height / 2, width, height, 0x000000, 0.7)
      .setScrollFactor(0).setDepth(100).setInteractive();
    this._detailBg.on('pointerdown', () => this._closeDetail());

    // Info text
    this._detailInfo = this.add.text(width / 2, 30,
      `${res.tag.trim()} #${res.number}  —  ${formatBytes(res.size)}`, {
        fontFamily: 'sans-serif', fontSize: '14px', color: '#e1dcd0',
        align: 'center',
      }).setOrigin(0.5).setScrollFactor(0).setDepth(101);

    // Nav hint
    this._detailNav = this.add.text(width / 2, height - 20,
      'Click background to close  \u2022  Arrow keys: prev/next frame', {
        fontFamily: 'sans-serif', fontSize: '12px', color: '#8a8499',
      }).setOrigin(0.5).setScrollFactor(0).setDepth(101);

    // Load full image
    this._loadDetailFrame(gff, res, 0);

    // Get frame count
    api.fetchImageInfo(gff, res.tag_url, res.number).then(info => {
      if (info) {
        this.detailFrameCount = info.frame_count || 1;
        this._updateDetailNav();
      }
    });

    // Keyboard nav
    this._keyHandler = (e) => {
      if (!this.detailActive) return;
      if (e.key === 'Escape') this._closeDetail();
      if (e.key === 'ArrowLeft') this._navigateDetailFrame(-1);
      if (e.key === 'ArrowRight') this._navigateDetailFrame(1);
    };
    document.addEventListener('keydown', this._keyHandler);
  }

  _loadDetailFrame(gff, res, frame) {
    const key = `detail_${gff}_${res.tag_url}_${res.number}_f${frame}_v${state.palVersion}`;
    const url = api.imageUrl(gff, res.tag_url, res.number, frame);

    const showSprite = () => {
      if (this.detailSprite) { this.detailSprite.destroy(); this.detailSprite = null; }
      if (!this.textures.exists(key)) return;

      const { width, height } = this.scale;
      this.detailSprite = this.add.image(width / 2, height / 2, key)
        .setScrollFactor(0).setDepth(101);

      // Scale to fit
      const frame = this.detailSprite.frame;
      const maxW = width * 0.8;
      const maxH = height * 0.7;
      const scale = Math.min(maxW / frame.width, maxH / frame.height, 4);
      this.detailSprite.setScale(Math.max(1, Math.floor(scale)));

      this._updateDetailNav();
    };

    if (this.textures.exists(key)) {
      showSprite();
    } else {
      this.load.image(key, url);
      this.load.once('complete', showSprite);
      this.load.start();
    }
  }

  _navigateDetailFrame(dir) {
    if (this.detailFrameCount <= 1) return;
    this.detailFrameIdx = Phaser.Math.Wrap(
      this.detailFrameIdx + dir, 0, this.detailFrameCount
    );
    const gff = state.gffByMode.images;
    this._loadDetailFrame(gff, this.detailResource, this.detailFrameIdx);
  }

  _updateDetailNav() {
    if (this._detailInfo && this.detailResource) {
      const res = this.detailResource;
      let text = `${res.tag.trim()} #${res.number}  —  ${formatBytes(res.size)}`;
      if (this.detailFrameCount > 1) {
        text += `  |  Frame ${this.detailFrameIdx + 1} / ${this.detailFrameCount}`;
      }
      this._detailInfo.setText(text);
    }
  }

  _closeDetail() {
    this.detailActive = false;
    this.detailResource = null;
    if (this.detailSprite) { this.detailSprite.destroy(); this.detailSprite = null; }
    if (this._detailBg) { this._detailBg.destroy(); this._detailBg = null; }
    if (this._detailInfo) { this._detailInfo.destroy(); this._detailInfo = null; }
    if (this._detailNav) { this._detailNav.destroy(); this._detailNav = null; }
    if (this._keyHandler) {
      document.removeEventListener('keydown', this._keyHandler);
      this._keyHandler = null;
    }
  }

  _onResize() {
    if (this.imageResources.length > 0 && !this.detailActive) {
      this._renderGrid();
    }
  }
}
