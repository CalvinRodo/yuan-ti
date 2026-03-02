/* ================================================================
   MusicScene — MIDI / VOC / OGG / XMI music player.
   Uses Web Audio API with soundfont samples for MIDI playback,
   and HTML5 Audio for VOC / OGG tracks.
   ================================================================ */

import Phaser from 'phaser';
import { state } from '../config.js';
import * as api from '../api.js';
import { showDomPanel, htmlEsc, formatBytes } from '../utils.js';

// ---------------------------------------------------------------------------
// MIDI parser — converts raw MIDI bytes → timed event list
// ---------------------------------------------------------------------------

function parseMidi(buf) {
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  let pos = 0;
  const u8  = () => buf[pos++];
  const u16 = () => { const v = dv.getUint16(pos); pos += 2; return v; };
  const u32 = () => { const v = dv.getUint32(pos); pos += 4; return v; };
  const vlq = () => { let v = 0; for (;;) { const b = u8(); v = (v << 7) | (b & 0x7f); if (!(b & 0x80)) return v; } };

  const hdr = String.fromCharCode(buf[0], buf[1], buf[2], buf[3]);
  if (hdr !== 'MThd') throw new Error('Not a MIDI file');
  pos = 4;
  u32(); // header length
  u16(); // format
  const nTrk = u16();
  const ppq  = u16();

  const tracks = [];

  for (let t = 0; t < nTrk; t++) {
    while (pos < buf.length - 8) {
      const ck = String.fromCharCode(buf[pos], buf[pos+1], buf[pos+2], buf[pos+3]);
      pos += 4;
      const len = u32();
      if (ck === 'MTrk') break;
      pos += len;
    }
    const trkLen = dv.getUint32(pos - 4);
    const tEnd = pos + trkLen;

    const events = [];
    let tick = 0, runStatus = 0;

    while (pos < tEnd && pos < buf.length) {
      tick += vlq();
      let byte = u8();

      if (byte === 0xFF) {
        const metaType = u8();
        const metaLen = vlq();
        if (metaType === 0x51 && metaLen >= 3) {
          const tempo = (buf[pos] << 16) | (buf[pos+1] << 8) | buf[pos+2];
          events.push({ tick, type: 'tempo', tempo });
        }
        pos += metaLen;
        runStatus = 0;
        continue;
      }

      if (byte === 0xF0 || byte === 0xF7) {
        const sLen = vlq();
        pos += sLen;
        runStatus = 0;
        continue;
      }

      let type, ch;
      if (byte & 0x80) {
        type = byte & 0xF0;
        ch = byte & 0x0F;
        runStatus = byte;
      } else {
        type = runStatus & 0xF0;
        ch = runStatus & 0x0F;
        pos--;
      }

      if (!type) { runStatus = 0; continue; }

      if (type === 0x90) {
        const note = u8(), vel = u8();
        events.push({ tick, type: vel > 0 ? 'noteOn' : 'noteOff', ch, note, vel });
      } else if (type === 0x80) {
        const note = u8(), vel = u8();
        events.push({ tick, type: 'noteOff', ch, note, vel });
      } else if (type === 0xB0) { u8(); u8(); }
      else if (type === 0xC0) {
        const prog = u8();
        events.push({ tick, type: 'pc', ch, prog });
      } else if (type === 0xA0) { u8(); u8(); }
      else if (type === 0xD0) { u8(); }
      else if (type === 0xE0) { u8(); u8(); }
      else { break; }
    }
    pos = tEnd;
    tracks.push({ events });
  }

  const tempoMap = [{ tick: 0, tempo: 500000, sec: 0 }];
  const allEvents = tracks.flatMap(t => t.events);
  allEvents.filter(e => e.type === 'tempo').sort((a,b) => a.tick - b.tick).forEach(e => {
    const prev = tempoMap[tempoMap.length - 1];
    const sec = prev.sec + (e.tick - prev.tick) / ppq * (prev.tempo / 1e6);
    tempoMap.push({ tick: e.tick, tempo: e.tempo, sec });
  });

  function tickToSec(tick) {
    let seg = tempoMap[0];
    for (let i = 1; i < tempoMap.length; i++) {
      if (tempoMap[i].tick > tick) break;
      seg = tempoMap[i];
    }
    return seg.sec + (tick - seg.tick) / ppq * (seg.tempo / 1e6);
  }

  const timed = allEvents.map(e => ({ ...e, sec: tickToSec(e.tick) }));
  timed.sort((a, b) => a.sec - b.sec);

  const lastNote = timed.filter(e => e.type === 'noteOff' || e.type === 'noteOn').pop();
  const duration = lastNote ? lastNote.sec + 1 : 0;

  const programs = {};
  timed.filter(e => e.type === 'pc').forEach(e => { programs[e.ch] = e.prog; });

  return { ppq, timed, duration, programs, tempoMap };
}

// ---------------------------------------------------------------------------
// Oscillator synth fallback
// ---------------------------------------------------------------------------

const NOTE_FREQ = Array.from({ length: 128 }, (_, i) => 440 * Math.pow(2, (i - 69) / 12));

function scheduleOscillator(ctx, note, ch, startSec, endSec, volume, gainNode) {
  if (startSec >= endSec) return null;
  const osc = ctx.createOscillator();
  const gain = ctx.createGain();
  osc.type = ch === 9 ? 'square' : 'sawtooth';
  osc.frequency.value = NOTE_FREQ[note] || 440;
  gain.gain.setValueAtTime(0, startSec);
  gain.gain.linearRampToValueAtTime(volume * 0.25, startSec + 0.01);
  gain.gain.exponentialRampToValueAtTime(0.001, endSec - 0.01);
  osc.connect(gain);
  gain.connect(gainNode);
  osc.start(startSec);
  osc.stop(endSec);
  return osc;
}

// ---------------------------------------------------------------------------
// Soundfont loader
// ---------------------------------------------------------------------------

const SF_BASE = 'https://gleitz.github.io/midi-js-soundfonts/FluidR3_GM/';
const GM_NAMES = [
  'acoustic_grand_piano','bright_acoustic_piano','electric_grand_piano','honkytonk_piano',
  'electric_piano_1','electric_piano_2','harpsichord','clavinet',
  'celesta','glockenspiel','music_box','vibraphone','marimba','xylophone',
  'tubular_bells','dulcimer','drawbar_organ','percussive_organ','rock_organ',
  'church_organ','reed_organ','accordion','harmonica','tango_accordion',
  'acoustic_guitar_nylon','acoustic_guitar_steel','electric_guitar_jazz',
  'electric_guitar_clean','electric_guitar_muted','overdriven_guitar',
  'distortion_guitar','guitar_harmonics','acoustic_bass','electric_bass_finger',
  'electric_bass_pick','fretless_bass','slap_bass_1','slap_bass_2','synth_bass_1',
  'synth_bass_2','violin','viola','cello','contrabass','tremolo_strings',
  'pizzicato_strings','orchestral_harp','timpani','string_ensemble_1',
  'string_ensemble_2','synth_strings_1','synth_strings_2','choir_aahs',
  'voice_oohs','synth_choir','orchestra_hit','trumpet','trombone','tuba',
  'muted_trumpet','french_horn','brass_section','synth_brass_1','synth_brass_2',
  'soprano_sax','alto_sax','tenor_sax','baritone_sax','oboe','english_horn',
  'bassoon','clarinet','piccolo','flute','recorder','pan_flute','blown_bottle',
  'shakuhachi','whistle','ocarina','lead_1_square','lead_2_sawtooth',
  'lead_3_calliope','lead_4_chiff','lead_5_charang','lead_6_voice',
  'lead_7_fifths','lead_8_bass_lead','pad_1_new_age','pad_2_warm',
  'pad_3_polysynth','pad_4_choir','pad_5_bowed','pad_6_metallic','pad_7_halo',
  'pad_8_sweep','fx_1_rain','fx_2_soundtrack','fx_3_crystal','fx_4_atmosphere',
  'fx_5_brightness','fx_6_goblins','fx_7_echoes','fx_8_sci-fi','sitar','banjo',
  'shamisen','koto','kalimba','bagpipe','fiddle','shanai','tinkle_bell','agogo',
  'steel_drums','woodblock','taiko_drum','melodic_tom','synth_drum',
  'reverse_cymbal','guitar_fret_noise','breath_noise','seashore','bird_tweet',
  'telephone_ring','helicopter','applause','gunshot',
];

const sfCache = new Map();

async function loadInstrument(ctx, prog) {
  const name = prog === 128 ? 'synth_drum' : (GM_NAMES[prog] || 'acoustic_grand_piano');
  if (sfCache.has(prog)) return sfCache.get(prog);

  const promise = (async () => {
    const url = `${SF_BASE}${name}-mp3.js`;
    const resp = await fetch(url, { signal: AbortSignal.timeout(8000) });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const text = await resp.text();

    const re = /"([A-G][b]?\d)"\s*:\s*"data:audio\/[^;]+;base64,([^"]+)"/g;
    const bufMap = {};
    const tasks = [];
    let m;
    while ((m = re.exec(text)) !== null) {
      const key = m[1];
      const b64 = m[2];
      tasks.push((async () => {
        try {
          const bin = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
          bufMap[key] = await ctx.decodeAudioData(bin.buffer.slice(0));
        } catch { /* skip */ }
      })());
    }
    if (tasks.length === 0) throw new Error('no samples');
    await Promise.all(tasks);
    return bufMap;
  })();

  sfCache.set(prog, promise);
  return promise;
}

const NOTE_NAMES = ['C','Db','D','Eb','E','F','Gb','G','Ab','A','Bb','B'];

function buildNearestMap(bufMap) {
  const samples = Object.entries(bufMap).map(([key, buf]) => {
    const m = key.match(/^([A-G][b]?)(-?\d+)$/);
    if (!m) return null;
    const nIdx = NOTE_NAMES.indexOf(m[1]);
    if (nIdx < 0) return null;
    return { midi: (parseInt(m[2]) + 1) * 12 + nIdx, buf, key };
  }).filter(Boolean).sort((a, b) => a.midi - b.midi);

  const nearest = {};
  for (let n = 0; n < 128; n++) {
    let best = null, bestDist = 999;
    for (const s of samples) {
      const d = Math.abs(s.midi - n);
      if (d < bestDist) { best = s; bestDist = d; }
    }
    if (best) nearest[n] = { buf: best.buf, detune: (n - best.midi) * 100 };
  }
  return nearest;
}

const nearestCache = new Map();

function scheduleSoundfont(ctx, bufMap, note, startSec, sustainSec, volume, masterGain) {
  if (!nearestCache.has(bufMap)) nearestCache.set(bufMap, buildNearestMap(bufMap));
  const nm = nearestCache.get(bufMap);
  const hit = nm[note];
  if (!hit) { scheduleOscillator(ctx, note, 0, startSec, startSec + sustainSec, volume, masterGain); return null; }

  const src = ctx.createBufferSource();
  const gain = ctx.createGain();
  src.buffer = hit.buf;
  src.detune.value = hit.detune;
  gain.gain.setValueAtTime(volume, startSec);
  gain.gain.setTargetAtTime(0.001, startSec + sustainSec, 0.3);
  src.connect(gain);
  gain.connect(masterGain);
  src.start(startSec);
  src.stop(startSec + sustainSec + 1.5);
  return src;
}

// ---------------------------------------------------------------------------
// Format helper
// ---------------------------------------------------------------------------

function fmtSec(s) {
  const m = Math.floor(s / 60);
  const r = Math.floor(s % 60);
  return `${m}:${r.toString().padStart(2, '0')}`;
}

// ---------------------------------------------------------------------------
// MusicScene
// ---------------------------------------------------------------------------

export default class MusicScene extends Phaser.Scene {
  constructor() {
    super('MusicScene');
    this._built = false;
    this._tracks = [];
    this._currentIdx = -1;
    this._dataGff = null;

    // MIDI engine state
    this._audioCtx = null;
    this._masterGain = null;
    this._nodes = [];
    this._parsed = null;
    this._midiBytes = null;
    this._startedAt = 0;
    this._pausedAt = 0;
    this._playing = false;
    this._looping = false;
    this._volume = 0.7;
    this._duration = 0;
    this._trackType = 'midi';
    this._endTimer = null;
    this._rafId = null;
    this._sfFailed = false;

    // HTML Audio for VOC / OGG
    this._htmlAudio = null;
  }

  create() {
    const panel = showDomPanel('MusicScene');
    if (!this._built) { this._build(panel); this._built = true; }
    panel.classList.remove('hidden');
    this._loadTracks(panel);
  }

  shutdown() {
    this._stopPlayback(true);
  }

  /* ---- Build DOM shell ---- */
  _build(panel) {
    panel.innerHTML = `
      <div class="music-view">
        <div class="music-sidebar">
          <div class="music-sidebar-header">
            <span class="music-section-label">Music</span>
            <span class="music-track-count dim"></span>
          </div>
          <div class="music-track-list"></div>
        </div>
        <div class="music-player">
          <div class="music-player-inner">

            <div class="music-title-area">
              <div class="music-now-playing">Now Playing</div>
              <div class="music-status dim">Select a track to begin.</div>
            </div>

            <div class="music-progress-wrap">
              <span class="music-time music-time-current">0:00</span>
              <div class="music-progress-bar">
                <div class="music-progress-fill"></div>
                <div class="music-progress-thumb"></div>
              </div>
              <span class="music-time music-time-total">0:00</span>
            </div>

            <div class="music-controls">
              <button class="music-btn music-prev-btn" title="Previous">&#9198;</button>
              <button class="music-btn music-btn-primary music-play-btn" disabled title="Play / Pause">&#9654;</button>
              <button class="music-btn music-stop-btn" disabled title="Stop">&#9209;</button>
              <button class="music-btn music-next-btn" title="Next">&#9197;</button>
              <button class="music-btn music-loop-btn" title="Loop">&#8634;</button>
              <div class="music-volume-group">
                <span class="music-vol-icon">&#128266;</span>
                <input class="music-volume-slider" type="range" min="0" max="1" step="0.05" value="0.7" title="Volume">
              </div>
            </div>

            <div class="music-sf-notice hidden">
              <span class="dim">Soundfont unavailable — using oscillator synth.</span>
            </div>
          </div>
        </div>
      </div>
    `;

    // Bind transport
    panel.querySelector('.music-play-btn').addEventListener('click', () => this._onPlayPause());
    panel.querySelector('.music-stop-btn').addEventListener('click', () => {
      this._stopPlayback(false);
      if (this._htmlAudio) this._htmlAudio.currentTime = 0;
      this._pausedAt = 0;
      this._updateProgress(0);
    });
    panel.querySelector('.music-prev-btn').addEventListener('click', () => this._navigate(-1, panel));
    panel.querySelector('.music-next-btn').addEventListener('click', () => this._navigate(1, panel));
    panel.querySelector('.music-loop-btn').addEventListener('click', () => {
      this._looping = !this._looping;
      if (this._htmlAudio) this._htmlAudio.loop = this._looping;
      this._updateTransport(panel);
    });
    panel.querySelector('.music-volume-slider').addEventListener('input', e => {
      this._volume = parseFloat(e.target.value);
      if (this._htmlAudio) this._htmlAudio.volume = this._volume;
      if (this._masterGain) this._masterGain.gain.value = this._volume;
    });

    // Seekable progress bar
    panel.querySelector('.music-progress-bar').addEventListener('click', e => {
      const rect = e.currentTarget.getBoundingClientRect();
      const pct = (e.clientX - rect.left) / rect.width;
      const sec = pct * (this._duration || 1);

      if ((this._trackType === 'voc' || this._trackType === 'ogg') && this._htmlAudio) {
        this._htmlAudio.currentTime = sec;
        this._updateProgress(sec);
        return;
      }
      if (!this._parsed) return;
      this._pausedAt = sec;
      if (this._playing) this._startMidiPlayback(sec);
      else this._updateProgress(sec);
    });
  }

  /* ---- Load track list ---- */
  async _loadTracks(panel) {
    const gff = state.gffByMode.music;
    const listEl = panel.querySelector('.music-track-list');
    const countEl = panel.querySelector('.music-track-count');
    listEl.innerHTML = '<div class="music-loading"><div class="spinner"></div> Loading\u2026</div>';

    let tracks = [];

    // GFF MIDI tracks
    if (gff) {
      try {
        const music = await api.fetchMusic(gff);
        music.forEach(t => tracks.push({ ...t, gff, type: 'midi' }));
      } catch { /* ignore */ }
    }

    // XMI files
    try {
      const xmi = await api.fetchXmiFiles();
      xmi.forEach(f => tracks.push({
        ...f, type: 'xmi-file', tag_label: 'XMI Music Files',
      }));
    } catch { /* ignore */ }

    // OGG files
    try {
      const ogg = await api.fetchOggFiles();
      ogg.forEach(f => tracks.push({
        ...f, type: 'ogg', tag_label: 'CD Audio Tracks (OGG)',
      }));
    } catch { /* ignore */ }

    this._tracks = tracks;
    this._dataGff = gff;
    countEl.textContent = `${tracks.length} track${tracks.length !== 1 ? 's' : ''}`;

    if (tracks.length === 0) {
      listEl.innerHTML = '<div class="music-error">No music tracks found.</div>';
      return;
    }

    // Group by tag_label
    const groups = {};
    tracks.forEach((t, i) => {
      const label = t.tag_label || 'Music';
      if (!groups[label]) groups[label] = [];
      groups[label].push({ ...t, _idx: i });
    });

    const frag = document.createDocumentFragment();
    for (const [label, items] of Object.entries(groups)) {
      if (Object.keys(groups).length > 1) {
        const hdr = document.createElement('div');
        hdr.className = 'music-group-header dim';
        hdr.textContent = label;
        frag.appendChild(hdr);
      }
      items.forEach(track => {
        const row = document.createElement('div');
        row.className = 'music-track-row';
        row.dataset.idx = track._idx;
        const icon = track.type === 'ogg' ? '&#127925;' : '&#9834;';
        const name = (track.type === 'ogg' || track.type === 'xmi-file')
          ? htmlEsc(track.name || track.filename)
          : `${htmlEsc((track.tag || '').trim())} #${track.number}`;
        row.innerHTML = `
          <span class="music-track-icon">${icon}</span>
          <span class="music-track-label">${name}</span>
          <span class="music-track-size dim">${formatBytes(track.size || 0)}</span>
        `;
        row.addEventListener('click', () => this._playTrack(panel, track._idx));
        frag.appendChild(row);
      });
    }
    listEl.innerHTML = '';
    listEl.appendChild(frag);
  }

  /* ---- Play a track ---- */
  async _playTrack(panel, idx) {
    const track = this._tracks[idx];
    if (!track) return;

    this._stopPlayback(true);
    this._currentIdx = idx;
    this._trackType = track.type || 'midi';
    this._midiBytes = null;
    this._parsed = null;
    this._pausedAt = 0;
    this._duration = 0;

    // Highlight
    panel.querySelectorAll('.music-track-row').forEach(r => {
      r.classList.toggle('active', parseInt(r.dataset.idx) === idx);
    });

    const statusEl = panel.querySelector('.music-status');
    statusEl.textContent = 'Loading\u2026';
    this._updateTransport(panel);

    const label = (track.type === 'ogg' || track.type === 'xmi-file')
      ? (track.name || track.filename)
      : `${(track.tag || '').trim()} #${track.number}`;

    // ---- OGG path (HTML5 Audio) ----
    if (track.type === 'ogg') {
      await this._playHtmlAudio(panel, api.oggFileUrl(track.filename), label, 'ogg');
      return;
    }

    // ---- VOC path (HTML5 Audio) ----
    if (track.type === 'voc') {
      const url = `/api/gff/${encodeURIComponent(track.gff)}/audio/${track.tag_url}/${track.number}?game=${state.game}`;
      await this._playHtmlAudio(panel, url, label, 'voc');
      return;
    }

    // ---- XMI file path (Web Audio MIDI) ----
    if (track.type === 'xmi-file') {
      try {
        const url = api.xmiFileUrl(track.filename) + '&track_index=0';
        const resp = await fetch(url);
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const buf = await resp.arrayBuffer();
        this._midiBytes = new Uint8Array(buf);
        this._parsed = parseMidi(this._midiBytes);
        this._duration = this._parsed.duration;
        statusEl.textContent = `${label}  \u2014  ${fmtSec(this._duration)}`;
      } catch (e) {
        statusEl.textContent = `Error: ${e.message}`;
        return;
      }
      this._updateTransport(panel);
      await this._startMidiPlayback(0);
      return;
    }

    // ---- GFF MIDI path (Web Audio MIDI) ----
    try {
      this._midiBytes = await api.fetchMidi(track.gff, track.tag_url, track.number);
      this._parsed = parseMidi(this._midiBytes);
      this._duration = this._parsed.duration;
      statusEl.textContent = `${label}  \u2014  ${fmtSec(this._duration)}`;
    } catch (e) {
      statusEl.textContent = `Error: ${e.message}`;
      return;
    }
    this._updateTransport(panel);
    await this._startMidiPlayback(0);
  }

  /* ---- HTML5 Audio playback (VOC / OGG) ---- */
  async _playHtmlAudio(panel, url, label, type) {
    if (!this._htmlAudio) this._htmlAudio = new Audio();
    const audio = this._htmlAudio;
    this._trackType = type;
    audio.src = url;
    audio.volume = this._volume;
    audio.loop = this._looping;
    audio.preload = 'metadata';

    const statusEl = panel.querySelector('.music-status');
    statusEl.textContent = label;

    audio.onloadedmetadata = () => {
      if (isFinite(audio.duration)) this._duration = audio.duration;
      statusEl.textContent = `${label}  \u2014  ${fmtSec(this._duration)}`;
      this._updateProgress(0);
    };
    audio.ontimeupdate = () => {
      if (isFinite(audio.duration)) this._duration = audio.duration;
      this._updateProgress(audio.currentTime);
    };
    audio.onended = () => {
      if (this._looping) { audio.currentTime = 0; audio.play().catch(() => {}); }
      else this._stopPlayback(true);
    };

    this._updateTransport(panel);
    try {
      await audio.play();
      this._playing = true;
    } catch (e) {
      statusEl.textContent = `Error: ${e.message}`;
    }
    this._updateTransport(panel);
  }

  /* ---- MIDI playback via Web Audio ---- */
  async _startMidiPlayback(fromSec = 0) {
    this._stopMidiEngine();

    const ctx = this._audioCtx = new AudioContext();
    const master = this._masterGain = ctx.createGain();
    master.gain.value = this._volume;
    master.connect(ctx.destination);

    const parsed = this._parsed;
    if (!parsed) return;

    // Build note segments
    const openNotes = {};
    const noteSegments = [];
    const progOfCh = {};

    parsed.timed.forEach(ev => {
      const key = `${ev.ch}-${ev.note}`;
      if (ev.type === 'pc') {
        progOfCh[ev.ch] = ev.prog;
      } else if (ev.type === 'noteOn' && ev.vel > 0) {
        openNotes[key] = { sec: ev.sec, vel: ev.vel, ch: ev.ch, note: ev.note };
      } else if (ev.type === 'noteOff' || (ev.type === 'noteOn' && ev.vel === 0)) {
        const on = openNotes[key];
        if (on) {
          noteSegments.push({
            ch: ev.ch, prog: progOfCh[ev.ch] ?? 0, note: ev.note,
            startSec: on.sec,
            endSec: Math.max(ev.sec, on.sec + 0.05),
            vel: on.vel,
          });
          delete openNotes[key];
        }
      }
    });

    // Close still-open notes
    Object.values(openNotes).forEach(on => {
      noteSegments.push({
        ch: on.ch, prog: progOfCh[on.ch] ?? 0, note: on.note,
        startSec: on.sec, endSec: on.sec + 0.5, vel: on.vel,
      });
    });

    // Load soundfonts
    const useSf = !this._sfFailed;
    const sfMaps = {};

    if (useSf) {
      const nearProgs = new Set(
        noteSegments.filter(s => s.startSec >= fromSec && s.startSec < fromSec + 60)
          .map(s => s.ch === 9 ? 128 : s.prog)
      );
      let loaded = 0;
      await Promise.all([...nearProgs].slice(0, 8).map(async prog => {
        try {
          sfMaps[prog] = await loadInstrument(ctx, prog);
          loaded++;
        } catch { this._sfFailed = true; }
      }));
      if (loaded === 0) this._sfFailed = true;
    }

    // Update soundfont notice
    const panel = document.getElementById('panel-MusicScene');
    if (panel) {
      const notice = panel.querySelector('.music-sf-notice');
      if (notice) notice.classList.toggle('hidden', !this._sfFailed);
    }

    // Schedule audio nodes
    const baseTime = ctx.currentTime + 0.1;
    const nodes = [];
    const LOOKAHEAD = 300;

    noteSegments.forEach(s => {
      if (s.startSec < fromSec - 0.01) return;
      if (s.startSec > fromSec + LOOKAHEAD) return;
      const t0 = baseTime + (s.startSec - fromSec);
      const t1 = baseTime + (s.endSec - fromSec);
      const v = (s.vel / 127) * this._volume;
      const prog = s.ch === 9 ? 128 : s.prog;

      let node;
      if (!this._sfFailed && sfMaps[prog]) {
        node = scheduleSoundfont(ctx, sfMaps[prog], s.note, t0, t1 - t0, v, master);
      } else {
        node = scheduleOscillator(ctx, s.note, s.ch, t0, t1, v, master);
      }
      if (node) nodes.push(node);
    });

    this._nodes = nodes;
    this._startedAt = ctx.currentTime - fromSec;
    this._pausedAt = fromSec;
    this._playing = true;

    if (panel) this._updateTransport(panel);
    this._scheduleProgressUpdate(panel);

    // Auto-stop / loop
    const remaining = parsed.duration - fromSec;
    this._endTimer = setTimeout(() => {
      if (this._looping) { this._startMidiPlayback(0); }
      else { this._stopPlayback(true); }
    }, remaining * 1000 + 500);
  }

  /* ---- Stop engines ---- */
  _stopMidiEngine() {
    if (this._endTimer) { clearTimeout(this._endTimer); this._endTimer = null; }
    if (this._rafId) { cancelAnimationFrame(this._rafId); this._rafId = null; }
    this._nodes.forEach(n => { try { n.stop(); } catch {} });
    this._nodes = [];
    if (this._audioCtx) { this._audioCtx.close().catch(() => {}); this._audioCtx = null; }
    this._masterGain = null;
  }

  _stopPlayback(ended = false) {
    if (this._htmlAudio) {
      this._htmlAudio.pause();
      if (ended) this._htmlAudio.currentTime = 0;
    }
    if (!ended && this._playing) {
      if ((this._trackType === 'voc' || this._trackType === 'ogg') && this._htmlAudio) {
        this._pausedAt = this._htmlAudio.currentTime;
      } else {
        this._pausedAt = this._currentPositionSec();
      }
    }
    this._stopMidiEngine();
    this._playing = false;

    const panel = document.getElementById('panel-MusicScene');
    if (panel) {
      this._updateTransport(panel);
      this._updateProgress(ended ? 0 : this._pausedAt);
    }
  }

  _currentPositionSec() {
    if (!this._audioCtx || !this._playing) return this._pausedAt;
    return this._audioCtx.currentTime - this._startedAt;
  }

  /* ---- Transport ---- */
  _onPlayPause() {
    if (this._playing) {
      this._pausedAt = (this._trackType === 'voc' || this._trackType === 'ogg') && this._htmlAudio
        ? this._htmlAudio.currentTime : this._currentPositionSec();
      this._stopPlayback(false);
    } else {
      if ((this._trackType === 'voc' || this._trackType === 'ogg') && this._htmlAudio) {
        this._htmlAudio.play().catch(() => {});
        this._playing = true;
      } else if (this._parsed) {
        this._startMidiPlayback(this._pausedAt);
      }
    }
    const panel = document.getElementById('panel-MusicScene');
    if (panel) this._updateTransport(panel);
  }

  _navigate(dir, panel) {
    const next = this._currentIdx + dir;
    if (next >= 0 && next < this._tracks.length) {
      this._playTrack(panel, next);
    }
  }

  /* ---- UI updates ---- */
  _updateTransport(panel) {
    const playBtn = panel.querySelector('.music-play-btn');
    const stopBtn = panel.querySelector('.music-stop-btn');
    const loopBtn = panel.querySelector('.music-loop-btn');
    if (!playBtn) return;
    const hasTrack = !!this._parsed || this._trackType === 'voc' || this._trackType === 'ogg';
    playBtn.innerHTML = this._playing ? '&#9208;' : '&#9654;';
    playBtn.disabled = !hasTrack;
    stopBtn.disabled = !hasTrack;
    loopBtn.classList.toggle('active', this._looping);
  }

  _updateProgress(sec) {
    const panel = document.getElementById('panel-MusicScene');
    if (!panel) return;
    const dur = this._duration || 1;
    const pct = Math.min(sec / dur, 1) * 100;
    const fill = panel.querySelector('.music-progress-fill');
    const thumb = panel.querySelector('.music-progress-thumb');
    const cur = panel.querySelector('.music-time-current');
    const tot = panel.querySelector('.music-time-total');
    if (fill) fill.style.width = pct + '%';
    if (thumb) thumb.style.left = pct + '%';
    if (cur) cur.textContent = fmtSec(Math.max(0, sec));
    if (tot) tot.textContent = fmtSec(this._duration);
  }

  _scheduleProgressUpdate() {
    this._rafId = requestAnimationFrame(() => {
      if (!this._playing) return;
      this._updateProgress(this._currentPositionSec());
      this._scheduleProgressUpdate();
    });
  }
}
