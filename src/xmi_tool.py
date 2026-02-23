"""XMI (XMIDI) to standard MIDI (.mid) converter.

XMIDI is an Extended MIDI format used by Dark Sun: Shattered Lands and other
SSI games.  Sequence data extracted from GFF files (tags CSEQ, GSEQ, LSEQ,
PSEQ) is stored in XMIDI XDIR/XMID IFF format.

Reference: XmiTool.java by John Glassmyer (dsun_music project).

Key XMIDI differences from standard MIDI
-----------------------------------------
* Timing — delays are individual bytes (0x00–0x7F), each equal to 1 tick
  (1/120 sec).  There is no variable-length quantity (VLQ) delta encoding for
  delays; any byte < 0x80 simply adds 1 to the current tick counter.
* Note On — each 0x9n event carries an *inline duration* after the velocity
  byte.  The duration is encoded as a VLQ-like sequence where continuation is
  signalled by byte > 0x80 (note: NOT ≥ 0x80 — byte 0x80 terminates).
* Loop controllers — XMIDI-specific MIDI controllers:
    0x74  FOR   — loop start (value 0 / 127 = infinite, else N iterations)
    0x75  NEXT  — loop end  (value < 64 = Break, ≥ 64 = Next)
  These are unrolled / skipped during conversion.
* API control controllers — 0x73 (Indirect), 0x77 (Callback), 0x78 (Seq
  Branch Index) — are silently dropped.

Conversion strategy
-------------------
* PPQ = 60, injected tempo = 500 000 µs/beat (120 BPM) → 120 ticks/sec.
* XMIDI delay ticks map 1-to-1 to MIDI ticks.
* Any tempo meta event already present in the EVNT data is dropped (it is
  meant for the DOS hardware MIDI driver, not for our playback).
* Each Note-On + inline duration is expanded into a Note-On + Note-Off pair.
* Loops: infinite loops are unrolled once (MAX_INF_LOOP_ITER = 1).
* Output: MIDI Format 0, single track, 60 PPQ.
"""

import io
import struct
from dataclasses import dataclass
from typing import Optional

# ---------------------------------------------------------------------------
# XMIDI controller numbers
# ---------------------------------------------------------------------------
CTRL_INDIRECT   = 0x73  # Indirect Controller  — drop
CTRL_FOR        = 0x74  # Loop start
CTRL_NEXT       = 0x75  # Loop end / break
CTRL_CALLBACK   = 0x77  # Callback             — drop
CTRL_SEQ_BRANCH = 0x78  # Sequence Branch Index — drop

# Maximum loop iterations when a loop is "infinite" (value 0 or 127)
MAX_INF_LOOP_ITER = 2

# ---------------------------------------------------------------------------
# MIDI output timing constants
# ---------------------------------------------------------------------------
OUT_PPQ   = 60          # ticks per quarter note
OUT_TEMPO = 500_000     # µs per beat  (120 BPM → 120 ticks/sec)


# ---------------------------------------------------------------------------
# Error
# ---------------------------------------------------------------------------
class XmiError(Exception):
    pass


# ---------------------------------------------------------------------------
# Low-level helpers
# ---------------------------------------------------------------------------

def _u32be(data: bytes, pos: int) -> int:
    return struct.unpack_from('>I', data, pos)[0]


def _tag4(data: bytes, pos: int) -> str:
    return data[pos:pos + 4].decode('ascii', errors='replace')


def _vlq_encode(value: int) -> bytes:
    """Encode *value* as a MIDI variable-length quantity."""
    result = bytearray([value & 0x7F])
    value >>= 7
    while value:
        result.append((value & 0x7F) | 0x80)
        value >>= 7
    result.reverse()
    return bytes(result)


def _read_xmidi_duration(data: bytes, pos: int) -> tuple[int, int]:
    """Decode an XMIDI note-duration value at *pos*.

    Bytes with value > 0x80 are continuation bytes; the first byte ≤ 0x80
    terminates the sequence.  7 bits per byte contribute to the value,
    big-endian.

    Returns (duration_ticks, new_pos).
    """
    duration = 0
    while True:
        b = data[pos] & 0xFF
        duration = (duration << 7) | (b & 0x7F)
        pos += 1
        if b <= 0x80:
            break
    return duration, pos


# ---------------------------------------------------------------------------
# IFF chunk iterator
# ---------------------------------------------------------------------------

def _iter_chunks(data: bytes, start: int, end: int):
    """Yield (tag_str, data_start, data_length) for IFF chunks in [start, end)."""
    pos = start
    while pos + 8 <= end:
        t   = _tag4(data, pos)
        lng = _u32be(data, pos + 4)
        yield t, pos + 8, lng
        pos += 8 + lng
        if lng & 1:     # IFF alignment pad byte
            pos += 1


# ---------------------------------------------------------------------------
# EVNT → MIDI track body
# ---------------------------------------------------------------------------

@dataclass
class _Ev:
    tick:     int
    priority: int   # 0 = note-off (sorts before note-on at same tick)
    data:     bytes


def _evnt_to_track_body(evnt: bytes) -> bytes:
    """Convert an XMIDI EVNT block into a standard MIDI track body (no MTrk header)."""
    events: list[_Ev] = []
    pos    = 0
    tick   = 0
    n      = len(evnt)

    # Loop stack frames: {'for_pos': int, 'iters': int}
    loops: list[dict] = []

    while pos < n:
        b = evnt[pos] & 0xFF

        # ── Delay bytes ────────────────────────────────────────────────────
        if b < 0x80:
            tick += b
            pos  += 1
            continue

        # ── Status byte ────────────────────────────────────────────────────
        status   = b
        msg_type = status & 0xF0

        # Meta event (0xFF …)
        if status == 0xFF:
            if pos + 2 >= n:
                break
            meta_type = evnt[pos + 1] & 0xFF
            meta_len  = evnt[pos + 2] & 0xFF
            ev_end    = pos + 3 + meta_len
            if meta_type == 0x51:
                # Tempo — drop; we inject our own at track start
                pass
            elif meta_type == 0x2F:
                # End of track — stop parsing
                pos = ev_end
                break
            else:
                events.append(_Ev(tick, 1, bytes(evnt[pos:ev_end])))
            pos = ev_end
            continue

        # SysEx
        if status in (0xF0, 0xF7):
            pos += 1
            while pos < n and (evnt[pos] & 0xFF) != 0xF7:
                pos += 1
            pos += 1
            continue

        # ── Channel messages ───────────────────────────────────────────────
        if msg_type == 0xB0:
            # Controller
            if pos + 2 >= n:
                break
            ctrl  = evnt[pos + 1] & 0xFF
            value = evnt[pos + 2] & 0xFF
            pos  += 3

            if ctrl == CTRL_FOR:
                iters = MAX_INF_LOOP_ITER if (value == 0 or value == 127) else value
                loops.append({'for_pos': pos, 'iters': iters})

            elif ctrl == CTRL_NEXT:
                if value < 64:
                    # Break: exit innermost loop
                    if loops:
                        loops.pop()
                else:
                    # Next: decrement and maybe loop back
                    if loops:
                        frame = loops[-1]
                        frame['iters'] -= 1
                        if frame['iters'] > 0:
                            pos = frame['for_pos']
                        else:
                            loops.pop()

            elif ctrl in (CTRL_INDIRECT, CTRL_CALLBACK, CTRL_SEQ_BRANCH):
                pass  # Drop API-control events

            else:
                events.append(_Ev(tick, 1, bytes([status, ctrl, value])))

        elif msg_type == 0x90:
            # Note On with inline XMIDI duration
            if pos + 2 >= n:
                break
            note     = evnt[pos + 1] & 0xFF
            velocity = evnt[pos + 2] & 0xFF
            pos     += 3
            dur, pos = _read_xmidi_duration(evnt, pos)

            off_status = (status & 0x0F) | 0x80   # Note Off same channel

            if velocity > 0:
                events.append(_Ev(tick, 1, bytes([status, note, velocity])))
                events.append(_Ev(tick + dur, 0, bytes([off_status, note, 0])))
            else:
                # velocity 0 → Note Off immediately
                events.append(_Ev(tick, 0, bytes([off_status, note, 0])))

        elif msg_type in (0xA0, 0xE0):
            # Poly aftertouch / pitch bend (3 bytes)
            if pos + 2 >= n:
                break
            events.append(_Ev(tick, 1, bytes(evnt[pos:pos + 3])))
            pos += 3

        elif msg_type in (0xC0, 0xD0):
            # Program change / channel pressure (2 bytes)
            if pos + 1 >= n:
                break
            events.append(_Ev(tick, 1, bytes(evnt[pos:pos + 2])))
            pos += 2

        else:
            # Unknown status — skip
            pos += 1

    # End-of-track meta event
    events.append(_Ev(tick, 2, bytes([0xFF, 0x2F, 0x00])))

    # Sort: tick first, then priority (note-off = 0 before note-on = 1)
    events.sort(key=lambda e: (e.tick, e.priority))

    # Encode as VLQ delta-time stream
    buf  = io.BytesIO()
    prev = 0
    for ev in events:
        delta = ev.tick - prev
        prev  = ev.tick
        buf.write(_vlq_encode(delta))
        buf.write(ev.data)

    return buf.getvalue()


# ---------------------------------------------------------------------------
# XMID helper: extract EVNT from inside one FORM XMID
# ---------------------------------------------------------------------------

def _evnt_from_xmid_block(data: bytes, start: int, end: int) -> Optional[bytes]:
    """Return the EVNT chunk bytes from within a FORM XMID block, or None."""
    for tag, dstart, dlen in _iter_chunks(data, start, end):
        if tag == 'EVNT':
            return data[dstart:dstart + dlen]
    return None


def _timbre_from_xmid_block(data: bytes, start: int, end: int) -> Optional[bytes]:
    """Return the TIMB chunk bytes from within a FORM XMID block, or None."""
    for tag, dstart, dlen in _iter_chunks(data, start, end):
        if tag == 'TIMB':
            return data[dstart:dstart + dlen]
    return None


# ---------------------------------------------------------------------------
# Top-level XMI parser
# ---------------------------------------------------------------------------

def xmi_extract_evnts(xmi_data: bytes) -> list[bytes]:
    """Parse XMI data and return the raw EVNT bytes for each XMID sequence."""
    data = xmi_data
    if len(data) < 12:
        raise XmiError("Data too short to be XMI")

    top_tag  = _tag4(data, 0)
    top_len  = _u32be(data, 4)
    top_type = _tag4(data, 8)

    if top_tag != 'FORM':
        raise XmiError(f"Expected FORM, got {top_tag!r}")
    if top_type not in ('XDIR', 'XMID'):
        raise XmiError(f"Unknown FORM type: {top_type!r}")

    evnts: list[bytes] = []

    if top_type == 'XMID':
        # Single-track XMI file
        ev = _evnt_from_xmid_block(data, 12, len(data))
        if ev:
            evnts.append(ev)

    else:
        # XDIR: FORM XDIR { INFO } CAT XMID { FORM XMID … }
        # The FORM XDIR content starts at byte 12.  Its length is top_len.
        # After the FORM XDIR chunk (8 + top_len bytes) comes the CAT chunk.
        cat_start = 8 + top_len    # first byte after FORM XDIR content

        if cat_start + 8 > len(data):
            raise XmiError("Truncated XMI: no CAT chunk")

        cat_tag  = _tag4(data, cat_start)
        cat_len  = _u32be(data, cat_start + 4)
        cat_type = _tag4(data, cat_start + 8)

        if cat_tag != 'CAT ':
            raise XmiError(f"Expected 'CAT ', got {cat_tag!r}")

        cat_body_start = cat_start + 12
        cat_body_end   = cat_start + 8 + cat_len

        # Iterate FORM XMID children inside the CAT body.
        # The CAT body starts with a 4-byte type tag ("XMID"), then chunks.
        pos = cat_body_start
        while pos + 8 <= cat_body_end:
            itag = _tag4(data, pos)
            ilen = _u32be(data, pos + 4)
            iend = pos + 8 + ilen

            if itag == 'FORM':
                xmid_type = _tag4(data, pos + 8)
                if xmid_type == 'XMID':
                    ev = _evnt_from_xmid_block(data, pos + 12, iend)
                    if ev:
                        evnts.append(ev)

            pos = iend

    return evnts


def xmi_track_count(xmi_data: bytes) -> int:
    """Return the number of XMID sequences in the XMI data."""
    try:
        return len(xmi_extract_evnts(xmi_data))
    except XmiError:
        return 0


def xmi_to_midi(xmi_data: bytes, track_index: int = 0) -> bytes:
    """Convert XMI data to a standard General MIDI (.mid) file.

    Parameters
    ----------
    xmi_data    : raw XMI bytes as extracted from a GFF *SEQ resource
    track_index : which XMID sequence to convert (0-based)

    Returns
    -------
    bytes — a complete standard MIDI file (Format 0, 1 track, 60 PPQ)
    """
    evnts = xmi_extract_evnts(xmi_data)
    if not evnts:
        raise XmiError("No EVNT data found in XMI")
    if track_index >= len(evnts):
        raise XmiError(
            f"Track {track_index} out of range (file has {len(evnts)} track(s))")

    # Prepend our own tempo event so timing is 120 Hz
    tempo_ev = bytes([
        0x00,                              # delta = 0
        0xFF, 0x51, 0x03,                  # meta: Set Tempo, 3 bytes
        (OUT_TEMPO >> 16) & 0xFF,
        (OUT_TEMPO >>  8) & 0xFF,
         OUT_TEMPO        & 0xFF,
    ])

    track_body  = tempo_ev + _evnt_to_track_body(evnts[track_index])
    track_chunk = struct.pack('>4sI', b'MTrk', len(track_body)) + track_body
    header      = struct.pack('>4sIHHH', b'MThd', 6, 0, 1, OUT_PPQ)

    return header + track_chunk


# ---------------------------------------------------------------------------
# CLI (for testing)
# ---------------------------------------------------------------------------

if __name__ == '__main__':
    import sys, os

    if len(sys.argv) < 3:
        print("Usage: python xmi_tool.py <input.xmi | gff:RESOURCE.GFF:GSEQ:1> "
              "<output.mid> [track_index]")
        sys.exit(1)

    src        = sys.argv[1]
    dst        = sys.argv[2]
    track_idx  = int(sys.argv[3]) if len(sys.argv) > 3 else 0

    # Allow "gff:FILENAME:TAG:NUMBER" shorthand
    if src.startswith('gff:'):
        _, gff_path, tag, num = src.split(':', 3)
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from gff import GffFile
        with open(gff_path, 'rb') as fh:
            raw_data = fh.read()
        g       = GffFile.create(raw_data)
        xmi_raw = g.get_resource_data(tag, int(num))
    else:
        with open(src, 'rb') as fh:
            xmi_raw = fh.read()

    n = xmi_track_count(xmi_raw)
    print(f"XMI file has {n} track(s).  Converting track {track_idx}…")

    midi_bytes = xmi_to_midi(xmi_raw, track_idx)
    with open(dst, 'wb') as fh:
        fh.write(midi_bytes)
    print(f"Wrote {len(midi_bytes)} bytes → {dst}")
