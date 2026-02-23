/* ================================================================
   view-music.js  —  XMIDI music player for GFF Viewer
   Uses Web Audio API for playback; soundfonts via CDN (with
   oscillator fallback when offline).
   ================================================================ */

'use strict';

// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------
const musicState = {
  tracks:       [],       // [{tag, tag_url, tag_label, number, size, gff, type}]
  currentIdx:   -1,       // index into musicState.tracks
  trackType:    null,     // 'midi' | 'voc'
  midiBytes:    null,     // Uint8Array of current MIDI file
  parsed:       null,     // ParsedMidi object
  vocAudio:     null,     // HTMLAudioElement for VOC playback
  audioCtx:     null,
  nodes:        [],       // scheduled AudioNodes (for stop)
  startedAt:    0,        // audioCtx.currentTime when playback started
  pausedAt:     0,        // position in seconds at pause
  playing:      false,
  looping:      false,
  volume:       0.7,
  instruments:  {},       // channel → AudioBuffer[] (soundfont samples)
  sfLoading:    false,
  sfFailed:     false,
  rafId:        null,     // requestAnimationFrame id for progress bar
  endTimer:     null,     // setTimeout id for auto-advance
  duration:     0,        // seconds
};

// ---------------------------------------------------------------------------
// Lightweight MIDI format-0 parser
// ---------------------------------------------------------------------------

function parseMidi(bytes) {
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let pos = 0;

  function tag()  { const s = String.fromCharCode(...bytes.slice(pos, pos+4)); pos+=4; return s; }
  function u32()  { const v = dv.getUint32(pos, false); pos+=4; return v; }
  function u16()  { const v = dv.getUint16(pos, false); pos+=2; return v; }
  function u8()   { return bytes[pos++]; }
  function vlq()  {
    let v=0;
    for (let i=0;i<4;i++) { const b=u8(); v=(v<<7)|(b&0x7f); if(!(b&0x80)) break; }
    return v;
  }

  // MThd
  if (tag() !== 'MThd') throw new Error('Not a MIDI file');
  const hlen = u32();
  const fmt  = u16();
  const ntrk = u16();
  const ppq  = u16();
  if (ppq & 0x8000) throw new Error('SMPTE timecode PPQ not supported');

  const tracks = [];
  for (let t = 0; t < ntrk; t++) {
    if (pos + 8 > bytes.length) break;
    const trktag = tag();
    if (trktag !== 'MTrk') { pos += u32(); continue; }
    const tlen  = u32();
    const tend  = pos + tlen;
    const events = [];
    let tick = 0;
    let runStatus = 0;
    let tempo = 500000; // default 120 BPM

    while (pos < tend) {
      const delta = vlq();
      tick += delta;
      let status = bytes[pos];

      if (status & 0x80) { runStatus = status; pos++; }
      else               { status = runStatus; }

      const type   = status & 0xF0;
      const ch     = status & 0x0F;

      if (status === 0xFF) {
        // Meta event
        const mtype = u8();
        const mlen  = vlq();
        const mdata = bytes.slice(pos, pos + mlen); pos += mlen;
        if (mtype === 0x51 && mlen === 3) {
          tempo = (mdata[0]<<16)|(mdata[1]<<8)|mdata[2];
          events.push({ tick, type:'tempo', tempo });
        } else if (mtype === 0x2F) {
          break; // end of track
        }
        runStatus = 0;
        continue;
      }

      if (status === 0xF0 || status === 0xF7) {
        const slen = vlq(); pos += slen;
        runStatus = 0; continue;
      }

      if (type === 0x90) {
        const note = u8(), vel = u8();
        events.push({ tick, type: vel > 0 ? 'noteOn' : 'noteOff', ch, note, vel });
      } else if (type === 0x80) {
        const note = u8(), vel = u8();
        events.push({ tick, type: 'noteOff', ch, note, vel });
      } else if (type === 0xB0) {
        const ctrl = u8(), val = u8();
        events.push({ tick, type: 'cc', ch, ctrl, val });
      } else if (type === 0xC0) {
        const prog = u8();
        events.push({ tick, type: 'pc', ch, prog });
      } else if (type === 0xA0) {
        u8(); u8(); // poly aftertouch
      } else if (type === 0xD0) {
        u8(); // channel pressure
      } else if (type === 0xE0) {
        u8(); u8(); // pitch bend
      } else {
        break; // unknown — bail
      }
    }
    pos = tend;
    tracks.push({ events });
  }

  // convert ticks → seconds using tempo map from first track
  const tempoMap = [{ tick: 0, tempo: 500000, sec: 0 }];
  const allEvents = tracks.flatMap(t => t.events);
  allEvents.filter(e => e.type === 'tempo').sort((a,b) => a.tick - b.tick).forEach(e => {
    const prev = tempoMap[tempoMap.length - 1];
    const sec  = prev.sec + (e.tick - prev.tick) / ppq * (prev.tempo / 1e6);
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

  // Flatten all tracks into timed events, sorted by time
  const timed = allEvents.map(e => ({ ...e, sec: tickToSec(e.tick) }));
  timed.sort((a, b) => a.sec - b.sec);

  const lastNote = timed.filter(e => e.type === 'noteOff' || e.type === 'noteOn').pop();
  const duration = lastNote ? lastNote.sec + 1 : 0;

  // Build per-channel program map
  const programs = {};
  timed.filter(e => e.type === 'pc').forEach(e => { programs[e.ch] = e.prog; });

  return { ppq, timed, duration, programs, tempoMap };
}

// ---------------------------------------------------------------------------
// Oscillator synth fallback
// ---------------------------------------------------------------------------

const NOTE_FREQ = Array.from({ length: 128 }, (_, i) => 440 * Math.pow(2, (i - 69) / 12));

function noteToHz(note) { return NOTE_FREQ[note] || 440; }

function scheduleOscillator(ctx, note, ch, startSec, endSec, volume, gainNode) {
  if (startSec >= endSec) return null;
  const osc  = ctx.createOscillator();
  const gain = ctx.createGain();
  osc.type      = ch === 9 ? 'square' : 'sawtooth'; // drum channel = ch 9
  osc.frequency.value = noteToHz(note);
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
// Soundfont loader (gleitz midi-js-soundfonts, mp3 format)
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

const sfCache = new Map(); // prog number → Promise<AudioBuffer[]>

async function loadInstrument(ctx, prog) {
  const name = prog === 128 ? 'synth_drum' : (GM_NAMES[prog] || 'acoustic_grand_piano');
  if (sfCache.has(prog)) return sfCache.get(prog);

  const promise = (async () => {
    const url  = `${SF_BASE}${name}-mp3.js`;
    const resp = await fetch(url, { signal: AbortSignal.timeout(8000) });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const text = await resp.text();

    // Extract note → base64 pairs; soundfont format:
    //   "C4": "data:audio/mp3;base64,AAAA..."
    const re  = /"([A-G][b]?\d)"\s*:\s*"data:audio\/[^;]+;base64,([^"]+)"/g;
    const bufMap = {};
    const tasks  = [];
    let m;
    while ((m = re.exec(text)) !== null) {
      const key    = m[1];
      const b64    = m[2];
      tasks.push((async () => {
        try {
          const bin = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
          bufMap[key] = await ctx.decodeAudioData(bin.buffer.slice(0));
        } catch { /* skip bad sample */ }
      })());
    }
    if (tasks.length === 0) throw new Error('no samples found');
    await Promise.all(tasks);
    return bufMap;
  })();

  sfCache.set(prog, promise);
  return promise;
}

// Note name → MIDI number (for soundfont key lookup)
const NOTE_NAMES = ['C','Db','D','Eb','E','F','Gb','G','Ab','A','Bb','B'];
function midiToSfKey(midi) {
  const oct  = Math.floor(midi / 12) - 1;
  const name = NOTE_NAMES[midi % 12];
  return `${name}${oct}`;
}

// Precompute MIDI number → nearest available sample from a bufMap
function buildNearestMap(bufMap) {
  // Parse keys like "C4", "Bb3", "Gb5" → midi number
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

const nearestCache = new Map(); // prog → nearestMap

function scheduleSoundfont(ctx, bufMap, note, startSec, sustainSec, volume, masterGain) {
  if (!nearestCache.has(bufMap)) nearestCache.set(bufMap, buildNearestMap(bufMap));
  const nm  = nearestCache.get(bufMap);
  const hit = nm[note];
  if (!hit) { scheduleOscillator(ctx, note, 0, startSec, startSec + sustainSec, volume, masterGain); return null; }

  const src  = ctx.createBufferSource();
  const gain = ctx.createGain();
  src.buffer  = hit.buf;
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
// Playback engine
// ---------------------------------------------------------------------------

async function startPlayback(fromSec = 0) {
  stopPlayback();

  const ctx  = musicState.audioCtx = new AudioContext();
  const master = ctx.createGain();
  master.gain.value = musicState.volume;
  master.connect(ctx.destination);

  const parsed = musicState.parsed;
  if (!parsed) return;

  // Group note-on events with matching note-off to determine sustain
  const openNotes = {}; // `${ch}-${note}` → { sec, vel }
  const noteSegments = []; // { ch, prog, note, startSec, endSec, vel }
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
          ch:       ev.ch,
          prog:     progOfCh[ev.ch] ?? 0,
          note:     ev.note,
          startSec: on.sec,
          endSec:   Math.max(ev.sec, on.sec + 0.05),
          vel:      on.vel,
        });
        delete openNotes[key];
      }
    }
  });

  // Close any still-open notes at the end
  Object.values(openNotes).forEach(on => {
    noteSegments.push({
      ch: on.ch, prog: progOfCh[on.ch]??0, note: on.note,
      startSec: on.sec, endSec: on.sec + 0.5, vel: on.vel,
    });
  });

  // Decide playback mode: try soundfont, else oscillator
  const useSf = !musicState.sfFailed;
  const sfMaps  = {};

  if (useSf) {
    // Preload unique programs used in range [fromSec, fromSec+60]
    const nearProgs = new Set(
      noteSegments.filter(s => s.startSec >= fromSec && s.startSec < fromSec + 60)
                  .map(s => s.ch === 9 ? 128 : s.prog)
    );
    let loaded = 0;
    await Promise.all([...nearProgs].slice(0, 8).map(async prog => {
      try {
        sfMaps[prog] = await loadInstrument(ctx, prog);
        loaded++;
      } catch {
        musicState.sfFailed = true;
      }
    }));
    if (loaded === 0) musicState.sfFailed = true;
  }

  // Schedule audio nodes
  const baseTime = ctx.currentTime + 0.1;
  const nodes    = [];
  const LOOKAHEAD = 300; // schedule at most this many seconds ahead

  noteSegments.forEach(s => {
    if (s.startSec < fromSec - 0.01) return;
    if (s.startSec > fromSec + LOOKAHEAD) return;
    const t0 = baseTime + (s.startSec - fromSec);
    const t1 = baseTime + (s.endSec   - fromSec);
    const v  = (s.vel / 127) * musicState.volume;
    const prog = s.ch === 9 ? 128 : s.prog;

    let node;
    if (!musicState.sfFailed && sfMaps[prog]) {
      node = scheduleSoundfont(ctx, sfMaps[prog], s.note, t0, t1 - t0, v, master);
    } else {
      node = scheduleOscillator(ctx, s.note, s.ch, t0, t1, v, master);
    }
    if (node) nodes.push(node);
  });

  musicState.nodes     = nodes;
  musicState.startedAt = ctx.currentTime - fromSec;
  musicState.pausedAt  = fromSec;
  musicState.playing   = true;

  updateTransportUI();
  scheduleProgressUpdate();

  // Auto-stop + auto-loop at end
  const remaining = parsed.duration - fromSec;
  musicState.endTimer = setTimeout(() => {
    if (musicState.looping) {
      startPlayback(0);
    } else {
      stopPlayback(true);
    }
  }, remaining * 1000 + 500);
}

function stopPlayback(ended = false) {
  // Stop VOC (HTML5 Audio) if active
  if (musicState.vocAudio) {
    musicState.vocAudio.pause();
    if (ended) musicState.vocAudio.currentTime = 0;
  }
  // Stop MIDI engine
  if (musicState.endTimer) { clearTimeout(musicState.endTimer); musicState.endTimer = null; }
  if (musicState.rafId)    { cancelAnimationFrame(musicState.rafId); musicState.rafId = null; }
  musicState.nodes.forEach(n => { try { n.stop(); } catch {} });
  musicState.nodes = [];
  if (musicState.audioCtx) {
    musicState.audioCtx.close().catch(() => {});
    musicState.audioCtx = null;
  }
  // Capture pause position
  if (musicState.trackType === 'voc' && musicState.vocAudio) {
    const pos = ended ? 0 : musicState.vocAudio.currentTime;
    musicState.playing = false;
    updateTransportUI();
    updateProgressBar(pos);
  } else {
    if (!ended) musicState.pausedAt = currentPositionSec();
    musicState.playing = false;
    updateTransportUI();
    updateProgressBar(ended ? 0 : musicState.pausedAt);
  }
}

function pausePlayback() {
  if (!musicState.playing) return;
  musicState.pausedAt = currentPositionSec();
  stopPlayback();
  musicState.playing = false;
  updateTransportUI();
}

function currentPositionSec() {
  if (!musicState.audioCtx || !musicState.playing) return musicState.pausedAt;
  return musicState.audioCtx.currentTime - musicState.startedAt;
}

function scheduleProgressUpdate() {
  musicState.rafId = requestAnimationFrame(() => {
    if (!musicState.playing) return;
    updateProgressBar(currentPositionSec());
    scheduleProgressUpdate();
  });
}

// ---------------------------------------------------------------------------
// Load a track (fetch MIDI, parse, update UI)
// ---------------------------------------------------------------------------

async function loadMusicTrack(idx) {
  const track = musicState.tracks[idx];
  if (!track) return;

  stopPlayback();
  musicState.currentIdx = idx;
  musicState.trackType  = track.type || 'midi';
  musicState.midiBytes  = null;
  musicState.parsed     = null;
  musicState.pausedAt   = 0;
  musicState.duration   = 0;

  highlightTrack(idx);
  setPlayerStatus('Loading…');
  updateTransportUI();

  // ---- VOC (HTML5 Audio) path ----
  if (track.type === 'voc') {
    if (!musicState.vocAudio) musicState.vocAudio = new Audio();
    const audio = musicState.vocAudio;
    const url   = `/api/gff/${encodeURIComponent(track.gff)}/audio/${track.tag_url}/${track.number}`
                + `?game=${state.game}`;
    audio.src     = url;
    audio.volume  = musicState.volume;
    audio.loop    = musicState.looping;
    audio.preload = 'metadata';

    audio.onloadedmetadata = () => {
      if (isFinite(audio.duration)) musicState.duration = audio.duration;
      setPlayerStatus(`${track.tag.trim()} #${track.number}  —  ${fmtSec(musicState.duration)}`);
      updateProgressBar(0);
    };
    audio.ontimeupdate = () => {
      if (isFinite(audio.duration)) musicState.duration = audio.duration;
      updateProgressBar(audio.currentTime);
    };
    audio.onended = () => {
      if (!musicState.looping) stopPlayback(true);
    };

    setPlayerStatus(`${track.tag.trim()} #${track.number}`);
    updateTransportUI();
    try {
      await audio.play();
      musicState.playing = true;
    } catch (e) {
      setPlayerStatus(`Error: ${e.message}`);
    }
    updateTransportUI();
    return;
  }

  // ---- MIDI (Web Audio) path ----
  try {
    const url = `/api/gff/${encodeURIComponent(track.gff)}/midi/${track.tag_url}/${track.number}`
              + `?game=${state.game}&track_index=0`;
    const resp = await fetch(url);
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const buf  = await resp.arrayBuffer();
    musicState.midiBytes = new Uint8Array(buf);
    musicState.parsed    = parseMidi(musicState.midiBytes);
    musicState.duration  = musicState.parsed.duration;
    setPlayerStatus(`${track.tag.trim()} #${track.number}  —  ${fmtSec(musicState.duration)}`);
  } catch (e) {
    setPlayerStatus(`Error: ${e.message}`);
    return;
  }

  updateTransportUI();
  await startPlayback(0);
}

// ---------------------------------------------------------------------------
// UI helpers
// ---------------------------------------------------------------------------

function fmtSec(s) {
  const m = Math.floor(s / 60);
  const r = Math.floor(s % 60);
  return `${m}:${r.toString().padStart(2,'0')}`;
}

function updateProgressBar(sec) {
  const dur  = musicState.duration || 1;
  const pct  = Math.min(sec / dur, 1) * 100;
  const bar   = document.getElementById('music-progress-bar');
  const fill  = document.getElementById('music-progress-fill');
  const thumb = document.getElementById('music-progress-thumb');
  const cur   = document.getElementById('music-time-current');
  const tot   = document.getElementById('music-time-total');
  if (fill)  fill.style.width    = pct + '%';
  if (thumb) thumb.style.left    = pct + '%';
  if (cur)   cur.textContent     = fmtSec(Math.max(0, sec));
  if (tot)   tot.textContent     = fmtSec(dur);
}

function updateTransportUI() {
  const playBtn  = document.getElementById('music-play-btn');
  const stopBtn  = document.getElementById('music-stop-btn');
  const loopBtn  = document.getElementById('music-loop-btn');
  if (!playBtn) return;
  const hasTrack = !!musicState.parsed || musicState.trackType === 'voc';
  playBtn.textContent = musicState.playing ? '⏸' : '▶';
  playBtn.disabled    = !hasTrack;
  stopBtn.disabled    = !hasTrack;
  loopBtn.classList.toggle('active', musicState.looping);
}

function setPlayerStatus(msg) {
  const el = document.getElementById('music-status');
  if (el) el.textContent = msg;
}

function highlightTrack(idx) {
  document.querySelectorAll('.music-track-row').forEach((r, i) => {
    r.classList.toggle('active', i === idx);
  });
}

// ---------------------------------------------------------------------------
// Build the music view DOM
// ---------------------------------------------------------------------------

let musicViewBuilt   = false;
let musicDataGff     = null;

async function renderMusicView() {
  const view = document.getElementById('music-view');
  if (!view) return;
  view.classList.remove('hidden');

  const gff     = state.gffByMode.music || 'RESOURCE.GFF';
  const needLoad = !musicViewBuilt || musicDataGff !== gff || musicState.tracks.length === 0;

  if (!musicViewBuilt) {
    buildMusicViewDom(view);
    musicViewBuilt = true;
  }

  bindMusicGffSelect();

  if (needLoad) {
    musicDataGff = gff;
    await loadMusicTrackList(gff);
  }

  updateTransportUI();
}

function buildMusicViewDom(view) {
  view.innerHTML = `
    <div id="music-layout">

      <!-- Track list -->
      <div id="music-sidebar">
        <div id="music-sidebar-header">
          <span class="music-section-label">Tracks</span>
          <span id="music-track-count" class="dim"></span>
        </div>
        <div id="music-track-list"></div>
      </div>

      <!-- Player panel -->
      <div id="music-player">
        <div id="music-player-inner">

          <div id="music-title-area">
            <div id="music-now-playing">Now Playing</div>
            <div id="music-status" class="dim">Select a track to begin.</div>
          </div>

          <!-- Waveform / progress -->
          <div id="music-progress-wrap">
            <span id="music-time-current" class="music-time">0:00</span>
            <div id="music-progress-bar">
              <div id="music-progress-fill"></div>
              <div id="music-progress-thumb"></div>
            </div>
            <span id="music-time-total" class="music-time">0:00</span>
          </div>

          <!-- Transport controls -->
          <div id="music-controls">
            <button id="music-prev-btn"  class="music-btn" title="Previous track">⏮</button>
            <button id="music-play-btn"  class="music-btn music-btn-primary" disabled title="Play / Pause">▶</button>
            <button id="music-stop-btn"  class="music-btn" disabled title="Stop">⏹</button>
            <button id="music-next-btn"  class="music-btn" title="Next track">⏭</button>
            <button id="music-loop-btn"  class="music-btn music-btn-toggle" title="Loop">↺</button>

            <div id="music-volume-group">
              <span class="music-vol-icon">🔊</span>
              <input id="music-volume" type="range" min="0" max="1" step="0.05"
                     value="0.7" title="Volume">
            </div>
          </div>

          <div id="music-sf-notice" class="hidden">
            <span class="warn">Soundfont unavailable — using oscillator synth.</span>
          </div>

        </div>
      </div>

    </div>
  `;

  // Bind transport events
  document.getElementById('music-play-btn').addEventListener('click', onPlayPause);
  document.getElementById('music-stop-btn').addEventListener('click', () => {
    stopPlayback(false);
    if (musicState.vocAudio) musicState.vocAudio.currentTime = 0;
    musicState.pausedAt = 0;
    updateProgressBar(0);
  });
  document.getElementById('music-prev-btn').addEventListener('click', () => navigateTrack(-1));
  document.getElementById('music-next-btn').addEventListener('click', () => navigateTrack(1));
  document.getElementById('music-loop-btn').addEventListener('click', () => {
    musicState.looping = !musicState.looping;
    if (musicState.vocAudio) musicState.vocAudio.loop = musicState.looping;
    updateTransportUI();
  });
  document.getElementById('music-volume').addEventListener('input', e => {
    musicState.volume = parseFloat(e.target.value);
    if (musicState.vocAudio) musicState.vocAudio.volume = musicState.volume;
  });

  // Seekable progress bar
  const bar = document.getElementById('music-progress-bar');
  bar.addEventListener('click', e => {
    const rect = bar.getBoundingClientRect();
    const pct  = (e.clientX - rect.left) / rect.width;
    const sec  = pct * (musicState.duration || 1);
    if (musicState.trackType === 'voc' && musicState.vocAudio) {
      musicState.vocAudio.currentTime = sec;
      updateProgressBar(sec);
      return;
    }
    if (!musicState.parsed) return;
    musicState.pausedAt = sec;
    if (musicState.playing) startPlayback(sec);
    else updateProgressBar(sec);
  });
}

async function loadMusicTrackList(gff) {
  const listEl = document.getElementById('music-track-list');
  const countEl = document.getElementById('music-track-count');
  if (!listEl) return;

  listEl.innerHTML = '<div class="music-loading"><div class="spinner"></div> Loading…</div>';

  let allTracks = [];
  try {
    const r = await fetch(`/api/gff/${encodeURIComponent(gff)}/music?game=${state.game}`);
    const data = await r.json();
    allTracks = data.map(t => ({ ...t, gff }));
  } catch {
    listEl.innerHTML = '<div class="music-error">Failed to load track list.</div>';
    return;
  }

  musicState.tracks = allTracks;

  if (countEl) countEl.textContent = `${allTracks.length} track${allTracks.length !== 1 ? 's' : ''}`;

  if (allTracks.length === 0) {
    listEl.innerHTML = '<div class="music-error">No music resources found.</div>';
    return;
  }

  // Separate MIDI tracks from VOC (sound effects) tracks
  const midiTracks = allTracks.map((t, i) => ({ ...t, _idx: i })).filter(t => t.type !== 'voc');
  const vocTracks  = allTracks.map((t, i) => ({ ...t, _idx: i })).filter(t => t.type === 'voc');

  // Group MIDI tracks by tag_label
  const groups = {};
  midiTracks.forEach(t => {
    (groups[t.tag_label] = groups[t.tag_label] || []).push(t);
  });

  const frag = document.createDocumentFragment();

  function buildTrackRow(track) {
    const row = document.createElement('div');
    row.className = 'music-track-row';
    if (track._idx === musicState.currentIdx) row.classList.add('active');
    row.dataset.idx = track._idx;
    row.innerHTML = `
      <span class="music-track-icon">${track.type === 'voc' ? '&#128266;' : '&#9834;'}</span>
      <span class="music-track-label">${htmlEsc(track.tag.trim())} #${track.number}</span>
      <span class="music-track-size dim">${formatBytes(track.size)}</span>
    `;
    row.addEventListener('click', () => loadMusicTrack(track._idx));
    return row;
  }

  // Render MIDI groups
  Object.entries(groups).forEach(([label, tracks]) => {
    const grpHeader = document.createElement('div');
    grpHeader.className = 'music-group-header';
    grpHeader.textContent = label;
    frag.appendChild(grpHeader);
    tracks.forEach(track => frag.appendChild(buildTrackRow(track)));
  });

  // Render Sound Effects section
  if (vocTracks.length > 0) {
    const sfxHeader = document.createElement('div');
    sfxHeader.className = 'music-group-header';
    sfxHeader.textContent = 'Sound Effects';
    frag.appendChild(sfxHeader);
    vocTracks.forEach(track => frag.appendChild(buildTrackRow(track)));
  }

  listEl.innerHTML = '';
  listEl.appendChild(frag);
}

// ---------------------------------------------------------------------------
// Transport event handlers
// ---------------------------------------------------------------------------

async function onPlayPause() {
  // VOC (HTML5 Audio) path
  if (musicState.trackType === 'voc' && musicState.vocAudio) {
    const audio = musicState.vocAudio;
    if (musicState.playing) {
      audio.pause();
      musicState.playing = false;
    } else {
      try { await audio.play(); musicState.playing = true; }
      catch (e) { setPlayerStatus(`Error: ${e.message}`); }
    }
    updateTransportUI();
    return;
  }
  // MIDI path
  if (musicState.playing) {
    pausePlayback();
  } else if (musicState.parsed) {
    await startPlayback(musicState.pausedAt);
  }
}

function navigateTrack(dir) {
  const n = musicState.tracks.length;
  if (n === 0) return;
  let idx = musicState.currentIdx + dir;
  if (idx < 0) idx = n - 1;
  if (idx >= n) idx = 0;
  loadMusicTrack(idx);
}

// Hook into GFF selector
function bindMusicGffSelect() {
  // The mode-gff-select in the main toolbar drives state.gffByMode.music
  // already; we just need to reload when it changes.
}

// ---------------------------------------------------------------------------
// Keyboard shortcuts (while music mode is active)
// ---------------------------------------------------------------------------
document.addEventListener('keydown', e => {
  if (state.mode !== 'music') return;
  if (e.target.tagName === 'INPUT') return;
  if (e.code === 'Space') { e.preventDefault(); onPlayPause(); }
  if (e.key  === 'ArrowRight' && e.altKey) navigateTrack(1);
  if (e.key  === 'ArrowLeft'  && e.altKey) navigateTrack(-1);
});

// Cleanup on mode switch
function teardownMusicView() {
  stopPlayback();
  const view = document.getElementById('music-view');
  if (view) view.classList.add('hidden');
}

// ---------------------------------------------------------------------------
// Sound Effects (SFX) view
// A lightweight player for BVOC (Creative Voice File) resources.
// Shares musicState.vocAudio but keeps its own track list and state.
// ---------------------------------------------------------------------------

const sfxState = {
  tracks:     [],   // [{tag, tag_url, number, size, gff}]
  currentIdx: -1,
  audio:      null, // HTMLAudioElement (lazily created)
  playing:    false,
};

let sfxViewBuilt = false;
let sfxDataGff   = null;

function getSfxAudio() {
  if (!sfxState.audio) sfxState.audio = new Audio();
  return sfxState.audio;
}

async function loadSfxTrack(idx) {
  const track = sfxState.tracks[idx];
  if (!track) return;

  const audio = getSfxAudio();
  audio.pause();
  sfxState.playing    = false;
  sfxState.currentIdx = idx;

  // Highlight row
  document.querySelectorAll('#sfx-track-list .sfx-track-row').forEach((r, i) => {
    r.classList.toggle('active', i === idx);
  });

  const url = `/api/gff/${encodeURIComponent(track.gff)}/audio/${track.tag_url}/${track.number}`
            + `?game=${state.game}`;

  audio.src     = url;
  audio.preload = 'metadata';
  audio.volume  = parseFloat(document.getElementById('sfx-volume')?.value ?? '0.7');

  audio.onended = () => {
    sfxState.playing = false;
    updateSfxUI();
    // Auto-advance
    const next = sfxState.currentIdx + 1;
    if (next < sfxState.tracks.length) loadSfxTrack(next);
  };
  audio.ontimeupdate = () => updateSfxProgress();
  audio.onloadedmetadata = () => updateSfxProgress();

  try {
    await audio.play();
    sfxState.playing = true;
  } catch (e) {
    const statusEl = document.getElementById('sfx-status');
    if (statusEl) statusEl.textContent = `Error: ${e.message}`;
  }
  updateSfxUI();
}

function updateSfxUI() {
  const playBtn = document.getElementById('sfx-play-btn');
  const stopBtn = document.getElementById('sfx-stop-btn');
  if (!playBtn) return;
  const hasTrack = sfxState.currentIdx >= 0;
  playBtn.textContent = sfxState.playing ? '\u23f8' : '\u25b6';
  playBtn.disabled    = !hasTrack;
  stopBtn.disabled    = !hasTrack;

  const track = sfxState.tracks[sfxState.currentIdx];
  const statusEl = document.getElementById('sfx-status');
  if (statusEl && track) {
    statusEl.textContent = `${track.tag.trim()} #${track.number}`;
  }
}

function updateSfxProgress() {
  const audio  = sfxState.audio;
  const fill   = document.getElementById('sfx-progress-fill');
  const thumb  = document.getElementById('sfx-progress-thumb');
  const cur    = document.getElementById('sfx-time-current');
  const tot    = document.getElementById('sfx-time-total');
  if (!audio) return;
  const dur = isFinite(audio.duration) ? audio.duration : 0;
  const pct = dur > 0 ? Math.min(audio.currentTime / dur, 1) * 100 : 0;
  if (fill)  fill.style.width  = pct + '%';
  if (thumb) thumb.style.left  = pct + '%';
  if (cur)   cur.textContent   = fmtSec(Math.max(0, audio.currentTime));
  if (tot)   tot.textContent   = fmtSec(dur);
}

async function renderSfxView() {
  const view = document.getElementById('sfx-view');
  if (!view) return;
  view.classList.remove('hidden');

  const gff      = state.gffByMode.sfx || 'RESOURCE.GFF';
  const needLoad = !sfxViewBuilt || sfxDataGff !== gff;

  if (!sfxViewBuilt) {
    buildSfxViewDom(view);
    sfxViewBuilt = true;
  }

  if (needLoad) {
    sfxDataGff = gff;
    await loadSfxTrackList(gff);
  }

  updateSfxUI();
}

async function loadSfxTrackList(gff) {
  const listEl  = document.getElementById('sfx-track-list');
  const countEl = document.getElementById('sfx-track-count');
  if (!listEl) return;

  listEl.innerHTML = '<div class="music-loading"><div class="spinner"></div> Loading\u2026</div>';

  let allTracks = [];
  try {
    const r    = await fetch(`/api/gff/${encodeURIComponent(gff)}/music?game=${state.game}`);
    const data = await r.json();
    allTracks  = data.filter(t => t.type === 'voc').map(t => ({ ...t, gff }));
  } catch {
    listEl.innerHTML = '<div class="music-error">Failed to load sound effects.</div>';
    return;
  }

  sfxState.tracks     = allTracks;
  sfxState.currentIdx = -1;

  if (countEl) countEl.textContent = `${allTracks.length} sample${allTracks.length !== 1 ? 's' : ''}`;

  if (allTracks.length === 0) {
    listEl.innerHTML = '<div class="music-error">No sound effects found in this file.</div>';
    return;
  }

  const frag = document.createDocumentFragment();
  allTracks.forEach((track, i) => {
    const row = document.createElement('div');
    row.className = 'sfx-track-row music-track-row';
    row.dataset.idx = i;
    row.innerHTML = `
      <span class="music-track-icon">&#128266;</span>
      <span class="music-track-label">${htmlEsc(track.tag.trim())} #${track.number}</span>
      <span class="music-track-size dim">${formatBytes(track.size)}</span>
    `;
    row.addEventListener('click', () => loadSfxTrack(i));
    frag.appendChild(row);
  });

  listEl.innerHTML = '';
  listEl.appendChild(frag);
  updateSfxUI();
}

function buildSfxViewDom(view) {
  view.innerHTML = `
    <div id="music-layout">
      <div id="music-sidebar">
        <div id="music-sidebar-header">
          <span class="music-section-label">Sound Effects</span>
          <span id="sfx-track-count" class="dim"></span>
        </div>
        <div id="sfx-track-list"></div>
      </div>

      <div id="music-player">
        <div id="music-player-inner">
          <div id="music-title-area">
            <div id="music-now-playing">Now Playing</div>
            <div id="sfx-status" class="dim">Select a sound to play.</div>
          </div>

          <div id="music-progress-wrap">
            <span id="sfx-time-current" class="music-time">0:00</span>
            <div id="sfx-progress-bar">
              <div id="sfx-progress-fill"></div>
              <div id="sfx-progress-thumb"></div>
            </div>
            <span id="sfx-time-total" class="music-time">0:00</span>
          </div>

          <div id="music-controls">
            <button id="sfx-prev-btn"  class="music-btn" title="Previous">&#9198;</button>
            <button id="sfx-play-btn"  class="music-btn music-btn-primary" disabled title="Play / Pause">&#9654;</button>
            <button id="sfx-stop-btn"  class="music-btn" disabled title="Stop">&#9209;</button>
            <button id="sfx-next-btn"  class="music-btn" title="Next">&#9197;</button>
            <div id="music-volume-group">
              <span class="music-vol-icon">&#128266;</span>
              <input id="sfx-volume" type="range" min="0" max="1" step="0.05"
                     value="0.7" title="Volume">
            </div>
          </div>
        </div>
      </div>
    </div>
  `;

  // Progress bar seek
  document.getElementById('sfx-progress-bar').addEventListener('click', e => {
    const audio = sfxState.audio;
    if (!audio || !isFinite(audio.duration)) return;
    const rect = e.currentTarget.getBoundingClientRect();
    audio.currentTime = ((e.clientX - rect.left) / rect.width) * audio.duration;
    updateSfxProgress();
  });

  // Transport controls
  document.getElementById('sfx-play-btn').addEventListener('click', async () => {
    const audio = getSfxAudio();
    if (sfxState.playing) {
      audio.pause();
      sfxState.playing = false;
    } else if (sfxState.currentIdx >= 0) {
      try { await audio.play(); sfxState.playing = true; }
      catch (e) { /* ignore */ }
    }
    updateSfxUI();
  });

  document.getElementById('sfx-stop-btn').addEventListener('click', () => {
    const audio = getSfxAudio();
    audio.pause();
    audio.currentTime  = 0;
    sfxState.playing   = false;
    updateSfxUI();
    updateSfxProgress();
  });

  document.getElementById('sfx-prev-btn').addEventListener('click', () => {
    const n = sfxState.tracks.length;
    if (n === 0) return;
    const idx = sfxState.currentIdx <= 0 ? n - 1 : sfxState.currentIdx - 1;
    loadSfxTrack(idx);
  });

  document.getElementById('sfx-next-btn').addEventListener('click', () => {
    const n = sfxState.tracks.length;
    if (n === 0) return;
    const idx = (sfxState.currentIdx + 1) % n;
    loadSfxTrack(idx);
  });

  document.getElementById('sfx-volume').addEventListener('input', e => {
    if (sfxState.audio) sfxState.audio.volume = parseFloat(e.target.value);
  });
}

function teardownSfxView() {
  if (sfxState.audio) {
    sfxState.audio.pause();
    sfxState.playing = false;
  }
  const view = document.getElementById('sfx-view');
  if (view) view.classList.add('hidden');
}

// Keyboard shortcuts for sfx mode
document.addEventListener('keydown', e => {
  if (state.mode !== 'sfx') return;
  if (e.target.tagName === 'INPUT') return;
  if (e.code === 'Space') {
    e.preventDefault();
    document.getElementById('sfx-play-btn')?.click();
  }
  if (e.key === 'ArrowRight' && e.altKey) document.getElementById('sfx-next-btn')?.click();
  if (e.key === 'ArrowLeft'  && e.altKey) document.getElementById('sfx-prev-btn')?.click();
});
