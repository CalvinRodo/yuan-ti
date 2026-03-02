/* ================================================================
   GFF Viewer — API client
   All server communication goes through these helpers.
   ================================================================ */

import { state } from './config.js';

// ---------------------------------------------------------------------------
// PAL / URL helpers
// ---------------------------------------------------------------------------
export function palParams() {
  if (!state.selectedPal) return '';
  const p = state.selectedPal;
  const tag = (p.tag || 'PAL').trim() || 'PAL';
  return `&pal_gff=${encodeURIComponent(p.gff)}&pal_num=${p.number}&pal_tag=${encodeURIComponent(tag)}`;
}

export function imageUrl(gff, tagUrl, number, frame = -1, scale = 1) {
  const s = scale > 1 ? `&scale=${scale}` : '';
  return `/api/gff/${encodeURIComponent(gff)}/image/${tagUrl}/${number}`
       + `?frame=${frame}&game=${state.game}${palParams()}${s}&v=${state.palVersion}`;
}

export function rawUrl(gff, tagUrl, number) {
  return `/api/gff/${encodeURIComponent(gff)}/raw/${tagUrl}/${number}?game=${state.game}`;
}

export function palPngUrl(gff, strippedTag, number) {
  const tagSeg = strippedTag + '_'.repeat(4 - strippedTag.length);
  return `/api/gff/${encodeURIComponent(gff)}/pal-png/${tagSeg}/${number}?game=${state.game}`;
}

// ---------------------------------------------------------------------------
// Game list
// ---------------------------------------------------------------------------
export async function fetchGames() {
  try {
    const r = await fetch('/api/games');
    state.games = await r.json();
  } catch {
    state.games = [];
  }
}

// ---------------------------------------------------------------------------
// GFF lists
// ---------------------------------------------------------------------------
export async function fetchGffList(mode) {
  if (state.gffListByMode[mode]?.length > 0) return;
  const capMap = { map: 'maps', images: 'images', text: 'texts', music: 'music', sfx: 'sfx' };
  const cap = capMap[mode] ?? 'resources';
  try {
    const r = await fetch(`/api/gffs?cap=${cap}&game=${state.game}`);
    state.gffListByMode[mode] = await r.json();
  } catch {
    state.gffListByMode[mode] = [];
  }
}

// ---------------------------------------------------------------------------
// Resources & palettes
// ---------------------------------------------------------------------------
export async function fetchResources(gff) {
  try {
    const r = await fetch(`/api/gff/${encodeURIComponent(gff)}/resources?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchPals(gff) {
  try {
    const r = await fetch(`/api/gff/${encodeURIComponent(gff)}/pals?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchAllPals() {
  try {
    const r = await fetch(`/api/pals/all?game=${state.game}`);
    state.allPals = await r.json();
  } catch {
    state.allPals = [];
  }
}

// ---------------------------------------------------------------------------
// Domain-specific fetches
// ---------------------------------------------------------------------------
export async function fetchSpells(gff) {
  try {
    const r = await fetch(`/api/gff/${encodeURIComponent(gff)}/spells?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchTexts(gff) {
  try {
    const r = await fetch(`/api/gff/${encodeURIComponent(gff)}/texts?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchMusic(gff) {
  try {
    const r = await fetch(`/api/gff/${encodeURIComponent(gff)}/music?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchAudio(gff) {
  try {
    const r = await fetch(`/api/gff/${encodeURIComponent(gff)}/audio?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchSfx() {
  try {
    const r = await fetch(`/api/sfx?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchOggFiles() {
  try {
    const r = await fetch(`/api/ogg-files?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchXmiFiles() {
  try {
    const r = await fetch(`/api/xmi-files?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchMidi(gff, tagUrl, number) {
  const url = `/api/gff/${encodeURIComponent(gff)}/midi/${tagUrl}/${number}?game=${state.game}`;
  const r = await fetch(url);
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  return new Uint8Array(await r.arrayBuffer());
}

export async function fetchScripts() {
  try {
    const r = await fetch(`/api/scripts?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchScript(tagUrl, number) {
  const r = await fetch(`/api/script/${tagUrl}/${number}?game=${state.game}`);
  if (!r.ok) { const j = await r.json(); throw new Error(j.error || r.statusText); }
  return await r.json();
}

export async function fetchCinematics() {
  try {
    const r = await fetch(`/api/cinematics?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchItems() {
  try {
    const r = await fetch(`/api/items?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchMonsters() {
  try {
    const r = await fetch(`/api/monsters?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchMonsterLore() {
  try {
    const r = await fetch(`/api/monster-lore?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchDataSpells() {
  try {
    const r = await fetch(`/api/spells?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchBmpCatalog() {
  try {
    const r = await fetch(`/api/bmp-catalog?game=${state.game}`);
    return await r.json();
  } catch { return []; }
}

export async function fetchImageInfo(gff, tagUrl, number) {
  try {
    const r = await fetch(`/api/gff/${encodeURIComponent(gff)}/image/${tagUrl}/${number}/info?game=${state.game}`);
    return await r.json();
  } catch { return null; }
}

export function sfxFileUrl(filename) {
  return `/api/sfx-file/${encodeURIComponent(filename)}?game=${state.game}`;
}

export function oggFileUrl(filename) {
  return `/api/ogg-file/${encodeURIComponent(filename)}?game=${state.game}`;
}

export function xmiFileUrl(filename) {
  return `/api/xmi-file/${encodeURIComponent(filename)}?game=${state.game}`;
}

export async function fetchRaw(gff, tagUrl, number) {
  const r = await fetch(rawUrl(gff, tagUrl, number));
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  return new Uint8Array(await r.arrayBuffer());
}
