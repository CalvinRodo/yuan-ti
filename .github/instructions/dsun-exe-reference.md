# DSUN.EXE & GPL Bytecode Reference

Reverse-engineering notes for **Dark Sun: Shattered Lands** (`dark_sun/DSUN.EXE`,
611 408 bytes, GOG release). Derived statically with Ghidra 12.1.4 (via REA) and
Capstone; nothing here was observed at runtime. Tools: `src/ovl_unpack.py`,
`src/gpl_parser.py`.

Addresses are `seg:off` with segments as stored in the EXE (load segment 0).
Ghidra adds its load segment 0x1000, so `043D:3990` is Ghidra `143d:3990` /
linear `0x17D60`.

---

## 1. Executable layout

| Item | Value |
|------|-------|
| Compiler | Borland C++ 3.0 (1991), **medium** model (far code, near data; RTL is `C0M.OBJ` + `CM.LIB`), 386 instructions (`66` prefixes). Most game data lives in far segments reached through explicit `far` pointers |
| MZ load image | file `0x5400`–`0x52EA0` (header 1344 paragraphs, 4853 relocs) |
| Entry | `0000:0000`, sets `DS = 4356h` (DGROUP) |
| Overlays | Borland VROOMM: `FBOV` block appended at file `0x52EA0` |
| Code segments | `0000`–`3653` (flag 1 in the segment table) |
| Far data | `3779`–`4356` (`3781` = GPL state, `377C`, `377E`, …) |
| Overlay stubs | `41A6`–`4351` (58 stubs: 52 with code, 6 empty) |

Most functions open with `push bp; mov bp,sp`. Some (72 found, among them
`gpl_eval_expr`) use Borland's overlay-aware far
frame `inc bp; push bp; mov bp,sp … pop bp; dec bp; retf`, so they start one byte
**before** `push bp`. The odd saved BP marks a far frame for the overlay
manager's stack walk (`3653:07FE`), which patches return addresses into
overlays it unloads.
Functions built with stack checking (468 of them, including 100 of the 115 GPL
handlers) open with `cmp [9Ch],sp / jb / call far 0000:230B`. `DS:009C` is the
RTL's `__brklvl`; `0000:230B` prints "Stack overflow!" and exits.

Segment `0000` is Borland's `c0` startup plus every runtime module whose code
segment is `_TEXT`; `3653` is the VROOMM overlay manager. Both are named in
`tools/ghidra/symbols.tsv` (see `dsun-decompilation.md` §7).

### FBOV overlay block

```
FBOV header (16 bytes): 'FBOV' uint32 overlay_size uint32 segtable_fileoff uint32 segcount
overlay code:           starts at FBOV + 0x10
```

Segment table (file `0x46770`, 220 entries × 8 bytes): `seg, maxoff, flags, minoff`
(flags 0 = data, 1 = code, 3 = overlay stub, 4 = other). Segment 0 merges the
startup code and the runtime library, so the table is coarser than the real CS values.

Overlay stub segment (in the resident image):

```
+00 CD 3F    int 3Fh                    +08 uint16 codesize
+02 uint16   memswap                    +0A uint16 relsize (fixup bytes after the code)
+04 uint32   fileoff (from FBOV+0x10)   +0C uint16 nentries   +0E uint16 prevstub
+20 entries: CD 3F <uint16 target offset> 00   (5 bytes each)
```

**Overlay fixups are not segment values.** Each fixed-up word holds a byte offset
into the segment table (index × 8); the overlay manager replaces it with that
entry's segment. Treating them as plain MZ relocations scatters bogus segment
boundaries through Ghidra's analysis (e.g. a fake segment `0688` splitting the
GPL interpreter). `ovl_unpack.py` resolves them, appends every overlay as its own
segment and rewrites stub entries to `JMP FAR`.

```
python src/ovl_unpack.py --exe=dark_sun/DSUN.EXE --out=DSUN_FLAT.EXE --map=ovl_map.txt
```

Load `DSUN_FLAT.EXE` in Ghidra (x86 Real Mode). Calls into an overlay go through
its stub, e.g. `call far 4251:00B1` → stub `4251` → overlay segment `7372`.

---

## 2. GPL interpreter (code segment `043D`, 0x3E99 bytes)

Ghidra's auto-analysis only creates 18 functions here, because handlers are reached
through the near-pointer table below. `make decomp` creates and names the rest
(see `dsun-decompilation.md`).

| Address | Role |
|---------|------|
| `043D:0001` | far entry `gpl_run(file, addr, …)`: resets state, calls `00A1` |
| `043D:00D3` | main loop: `op = fetch(); dispatch(op)` until `3781:0326` is set |
| `043D:01C8` | dispatcher: `op < 0x81` → optional trace hook `DS:00AC`, then `DS:00C0[op]()` |
| `043D:2E28` / `2E07` | read byte / read big-endian word |
| `043D:2E49` / `2DBD` / `2668` | peek / peek(+n) / advance PC |
| `043D:398F` | expression evaluator |
| `043D:3DAA` | read N expressions → values `3781:00B4[i]`, lvalue ptrs `3781:00D4[i]` |
| `043D:2BB7` | string operand |
| `043D:26D6` | variable read |
| `043D:3297` | assignment target |
| `043D:2FCC` | object reference |
| `043D:0205` / `0290` | push / pop call frame (max 50, depth in `3781:0192`) |
| `043D:02F3` / `0384` | call / return across GPL files (max 16, depth in `3781:0327`) |
| `043D:264C` | invalid opcode → `377C:0009 = -1`, exit |
| `043D:00F9` | exit: `3781:0326 = 1` |

PC = `3781:0295[depth]` + base `3781:0193`; code buffer far ptr `3781:0255`.
Accumulator (`acc`) = `3781:031B` (int32). Command table = `DGROUP:00C0`,
129 near pointers into `043D`.

`GPLX` 1 is loaded by overlay `7372:0046`: bytes `0x000–0x3DD` → `DS:4D7A`,
`0x3DE–0x440` → `DS:537A` (per-field types, needed to decode SEARCH),
then 10 bytes → `DS:53F7`, then `0xAA` bytes.

### Expression grammar (`043D:398F`)

| Byte | Meaning |
|------|---------|
| `00–7F` | 15-bit immediate: `(b << 8) \| next` |
| `80` | `acc` |
| `81–8E` | variable, type = `b & 0x7F`, 1-byte index |
| `C1–CE` | same with 2-byte index |
| `8B` | int32 immediate (two words, high first) |
| `8C` | embedded command: next byte is an opcode; value = `acc` afterwards |
| `8F` / `90` / `91` | int8 / uint16 / −uint16 immediate |
| `92` | string operand (below) |
| `B1` | object reference |
| `D1–DF` | operator `+ - * / && \|\| == != > < >= <= & \| &~` |
| `E2` / `E1` | `(` / `)` (depth ≤ 8) |

No precedence: operators apply left to right. An expression continues while
the next byte is an operator (or `E1` inside parentheses).

### Variable types

| Type | Meaning | Storage (far ptr in `3781`) |
|------|---------|-----------------------------|
| 01 / 06 | local / global string (42-byte records) | `032F` / `033F` |
| 02 / 07 | local / global int16 | `0337` / `0347` |
| 05 / 0A | local / global int32 | `0333` / `0343` |
| 0D / 0E | global / local bit flags | `034B` / `033B` |
| 09 | `gname`: engine variable, raw index `0x20–0x2E` shown as 0–14 (`0x29–0x2A` are 32-bit) | far-ptr table |

### Other operands

* **string** (`2BB7`): kind byte. `5` = 7-bit packed text (8 chars per 7 bytes,
  big-endian bit order, char 3 ends it, max 299 chars, `2C26`). `1` = speaker
  name (`377E:0000` prefix + name of object `3781:0369`), no further bytes.
  `2` = bytes run through `043D:396B` until 3 (unused in the shipped data).
  A `00` byte right after a string is legal filler (opcode 0 checks `377C:0002`).
* **objref** (`2FCC`): word id, count byte n, n field bytes. id < 0x8000 = object
  `-id`; `0x8000|n` for n in 25 26 27 28 2B 2C = special objects (`3781:035F–0369`).
* **target** (`3297`): byte t; `t & 0x40` → 2-byte index (type `t + 0xC0`);
  type < 0x10 = variable, otherwise an objref follows.
* **menu** (`338B`, opcode 48): text commands (`23 28 2E 4B`), header expr, then
  up to 25 × (text expr, address expr, condition expr), terminated by byte `4A`.
  The chosen option's address is entered with gosub.
* **SEARCH** (`08F8`, opcode 33): who expr, 2 bytes, then entries
  `[53] field mode [expr]` while the next byte is `53`; the expr is present only
  if mode is 4–6 and `GPLX field_type[field]` ∈ {10, 12, 13, 14}.

### Command table

Handler offsets are in segment `043D`. Operand shapes come from the handlers;
mnemonics are libgff's (they agree with the handlers wherever checked, e.g. the
INPUT_* commands read keyboard input via overlay `7372:1150/1187/11CB/11FD`).
`addr` operands are jump/call targets (shown as `@xxxx`). Opcodes 26 4A 4C 4D 4E 53 55 56 57 60 71–75 and ≥ 81 hit
the error stub (4A and 53 only appear as MENU / SEARCH delimiters).

| Op | Handler | Mnemonic | Operands |
|----|---------|----------|----------|
| `00` | `24DD` | STRING_END | — |
| `01` | `0B1C` | LONG /= | expr, expr |
| `02` | `0B57` | BYTE_DEC | expr |
| `03` | `0B76` | WORD_DEC | expr |
| `04` | `0B95` | LONG_DEC | expr |
| `05` | `0BB5` | BYTE_INC | expr |
| `06` | `0BD4` | WORD_INC | expr |
| `07` | `0BF3` | LONG_INC | expr |
| `08` | `0C13` | HUNT | expr |
| `09` | `15E8` | GETXY | expr |
| `0A` | `0C39` | STRING_COPY | expr, expr |
| `0B` | `0C66` | PHYS_DAMAGE | expr, expr |
| `0C` | `24C4` | CHANGE_MONEY | expr |
| `0D` | `2510` | SET_VAR | — |
| `0E` | `0D9C` | TOGGLE_ACCUM | — |
| `0F` | `15A3` | GET_STATUS | expr |
| `10` | `0E24` | GET_LOS | expr, expr, expr |
| `11` | `0EDB` | LONG *= | expr, expr |
| `12` | `0F12` | JUMP | addr |
| `13` | `0F35` | CALL_LOCAL | addr |
| `14` | `0F49` | CALL_GLOBAL | addr, expr |
| `15` | `0F74` | LOCAL_RET | — |
| `16` | `0F83` | LOAD_VAR | expr, target |
| `17` | `0F95` | COMPARE | expr |
| `18` | `10C0` | LOAD_ACCUM | expr |
| `19` | `10DD` | END_FUNCTION | — |
| `1A` | `0E69` | NEXTTO | expr, expr |
| `1B` | `1495` | CREATE_INLOS_TRIGGER | expr, expr, expr, expr |
| `1C` | `14D5` | CREATE_NOTINLOS_TRIGGER | expr, expr, expr, expr |
| `1D` | `1515` | CLEAR_LOS | expr |
| `1E` | `1647` | NAMETONUM | expr |
| `1F` | `1668` | NUMTONAME | expr |
| `20` | `0DD3` | BITSNOOP | expr, expr |
| `21` | `10EC` | AWARD | expr, expr |
| `22` | `11A8` | REQUEST | expr, expr, expr, expr |
| `23` | `1183` | SOURCE_TRACE | string |
| `24` | `1192` | SHOP | expr |
| `25` | `11F1` | CLONE | expr, expr, expr, expr, expr, expr |
| `27` | `0FFD` | IFCOMPARE | expr, addr |
| `28` | `124C` | OP_28 | expr, expr, string |
| `29` | `1060` | ORELSE | addr |
| `2A` | `125B` | CLEAR_PIC | — |
| `2B` | `126C` | CONTINUE | — |
| `2C` | `127D` | LOG | string |
| `2D` | `0C78` | DAMAGE | expr, expr |
| `2E` | `1296` | SOURCE_LINE | expr |
| `2F` | `12A5` | DROP | expr, expr, expr |
| `30` | `1398` | PASS_TIME | expr |
| `31` | `13AE` | EXIT_GPL | — |
| `32` | `13BE` | FETCH | expr, expr |
| `33` | `13ED` | SEARCH | search |
| `34` | `1468` | GETPARTY | expr |
| `35` | `152B` | FIGHT | — |
| `36` | `1557` | FLEE | expr |
| `37` | `1574` | FOLLOW | expr, expr |
| `38` | `15C7` | ASK_YES_NO | — |
| `39` | `15FE` | GIVE | expr, expr, expr, expr |
| `3A` | `1689` | GO | expr, expr |
| `3B` | `16B8` | INPUT_BIGNUM | expr |
| `3C` | `16F4` | GOXY | expr, expr, expr |
| `3D` | `1728` | READORDERS | expr |
| `3E` | `190B` | IF | addr |
| `3F` | `1971` | ELSE | addr |
| `40` | `174E` | SETRECORD | objref, expr |
| `41` | `1895` | SET_OTHER | expr |
| `42` | `19AA` | INPUT_STRING | expr |
| `43` | `19E8` | INPUT_NUMBER | expr |
| `44` | `1A0D` | INPUT_MONEY | expr |
| `45` | `1A32` | JOINPARTY | expr |
| `46` | `1A58` | LEAVEPARTY | expr |
| `47` | `1A7E` | LOCKDOOR | — |
| `48` | `1A95` | MENU | menu |
| `49` | `140A` | SETTHING | expr, expr |
| `4B` | `0F26` | LOCAL_SUB_TRACE | expr, expr, expr |
| `4F` | `1AA4` | PRINT_STRING | expr, expr |
| `50` | `1AD3` | PRINT_NUMBER | expr, expr |
| `51` | `1B02` | PRINT_NEWLINE | — |
| `52` | `1B13` | RAND | expr |
| `54` | `1B55` | SHOW_PIC | expr |
| `58` | `1B97` | SKILLROLL | expr, expr, expr |
| `59` | `1C43` | STAT_ROLL | expr, expr, expr |
| `5A` | `1CEF` | STRING_COMPARE | expr, expr |
| `5B` | `1D6C` | MATCH_STRING | expr, expr |
| `5C` | `1DE9` | TAKE | expr, expr, expr, expr |
| `5D` | `1B6B` | SOUND | expr |
| `5E` | `1EAE` | TELEPORT | expr, expr, expr, expr, expr |
| `5F` | `1B81` | MUSIC | expr |
| `61` | `109B` | CMPEND | — |
| `62` | `1FA6` | WAIT | expr |
| `63` | `1FC3` | WHILE | addr |
| `64` | `1FF1` | WEND | addr |
| `65` | `2176` | CREATE_ATTACK_TRIGGER | expr, expr, expr |
| `66` | `21AE` | CREATE_LOOK_TRIGGER | expr, expr, expr |
| `67` | `2005` | ENDIF | — |
| `68` | `202A` | CREATE_MOVE_TILE_TRIGGER | expr, expr, expr, expr, expr |
| `69` | `206F` | CREATE_DOOR_TILE_TRIGGER | expr, expr, expr, expr, expr |
| `6A` | `20B4` | CREATE_MOVE_BOX_TRIGGER | expr, expr, expr, expr, expr, expr, expr |
| `6B` | `20F9` | CREATE_DOOR_BOX_TRIGGER | expr, expr, expr, expr, expr, expr, expr |
| `6C` | `213E` | CREATE_PICKUP_TRIGGER | expr, expr, expr |
| `6D` | `21E6` | CREATE_USE_TRIGGER | expr, expr, expr |
| `6E` | `221E` | CREATE_TALKTO_TRIGGER | expr, expr, expr |
| `6F` | `2256` | CREATE_NOORDERS_TRIGGER | expr, expr, expr |
| `70` | `228A` | CREATE_USEWITH_TRIGGER | expr, expr, expr, expr |
| `76` | `22C2` | BYTE += | expr, expr |
| `77` | `22F5` | BYTE -= | expr, expr |
| `78` | `2328` | BYTE *= | expr, expr |
| `79` | `235F` | BYTE /= | expr, expr |
| `7A` | `239A` | WORD += | expr, expr |
| `7B` | `23CD` | WORD -= | expr, expr |
| `7C` | `2400` | WORD *= | expr, expr |
| `7D` | `2437` | WORD /= | expr, expr |
| `7E` | `2472` | LONG += | expr, expr |
| `7F` | `249B` | LONG -= | expr, expr |
| `80` | `0EA2` | RANGE | expr, expr |

Semantics notes (from the handlers):
* `00` is legal only directly after a string; anywhere else it aborts the script.
* `01` `*lv /= v`; `02–04` decrement byte/word/long lvalue; `05–07` increment.
* `0E` acc = !acc. `18` acc = expr. `12` / `13` jump / call local. `15` return.
* `14` call (addr, file) in another GPL file; `19` return to the calling file.
* `16` assign expr → target.
* `3E` IF: jump to the else address when acc == 0 (condition stack, max 32);
  `3F` ELSE: skip when the IF branch ran; `67` ENDIF.
* `17` COMPARE: acc = expr, pushed on the switch stack (max 8); `27` IFCOMPARE
  value, next-case address; `29` ORELSE: jump if a case matched; `61` CMPEND.
  `63` WHILE: jump to exit if acc == 0; `64` WEND: jump back.
* `2A` CLEAR_PIC / `54` SHOW_PIC drive text window 1; `2B` "Press continue";
  `38` "Answer Yes or No" → acc; `4F` / `50` / `51` print to window 2.
* `0D` SET_VAR reads typed numbers via overlay `7372:11CB` into variables
  (no bytecode operands).
* `2C`, `45`, `46` end in `2A0A:0251`, an empty stub (feature compiled out).
* Engine module `1A0A` iterates the 320 objects matching a selector
  (`1A0A:0922`): `03CA` sets AI mode/target (cmd 08 32 36 37 3A 3C 62),
  `03AA` reads it (3D), `04DC` clears it (1D), `0256` focuses an object (09),
  `00B3` any match within range and line of sight (10), `0623` find object (41),
  `01A0` distance to the nearest match (80).

---

## 3. Region maps and object animation

Derived statically from the code named in `tools/ghidra/symbols.tsv`
(2026-10-18); not yet observed at run time. DGROUP addresses are `DS:xxxx`.

### Region map flags (`GMAP`)

`region_load_maps` (`2191:01CB`) loads the region's `PAL `, `RMAP` (far pointer
`DS:2F4A`) and `GMAP` (far pointer `DS:0556`), each 128 × 98 bytes, row-major
(`y * 128 + x`). Segment `142F` holds the accessors. Coordinates outside
128 × 98 count as blocked and as not blocking sight.

| Bits | Meaning | Evidence |
|------|---------|----------|
| 0–4 (`0x1F`) | Wall index: 0 none, else `WALL` region × 100 + index − 1, drawn bottom-centred on the cell | `draw_objects_and_walls` (`1DBD:0002`) |
| 5 (`0x20`) | Occupied by a creature (run-time only; cleared on load, set with `0x40` at party cells) | `gmap_mark_party` / `gmap_unmark_party` set and clear `0x60`; stored in 1 cell of all 33 files |
| 6 (`0x40`) | Blocks movement | `gmap_is_blocked` (`142F:00DC`), used by `footprint_blocked` and the movement code in `1587`/`1A0A`; the game's own debug overlay (`142F:0426`) boxes these cells |
| 7 (`0x80`) | Blocks line of sight | `line_of_sight` (`0898:00A4`) stops at the first `0x80` cell; spell-area targeting (`8B7D:00AB`) accepts blocked cells that lack `0x80` |

Most `0x80` cells also have `0x40` (walls); outdoor regions have few
(cliffs block movement but not sight), indoor and city regions many.

**Footprint.** `footprint_blocked` (`1A0A:11DB`) tests every cell from
x − w to x + w and from y − h to y, with w = `DS:1650` and h = `DS:1652`.
`find_free_cell` (`7372:0909`) sets them from a size byte: w = size & 7,
h = size / 8. Both are 0 by default (a single cell). Its caller at
`7372:0388` reads the size from byte 0x3D of a 0x47-byte record in the table
at far pointer `DS:1661` (*unverified*: per-creature data; the file it is
loaded from is not traced).

**Directions** (tables `DS:2F52` dx and `DS:2F5B` dy): 0 N (0, −1), 1 NE,
2 E (1, 0), 3 SE, 4 S (0, 1), 5 SW, 6 W (−1, 0), 7 NW.

### Objects

Each `ETAB` entry (8 bytes: int16 x, int16 y, int8 elevation, flags, int16
`OJFF` number) becomes an object the first time it is drawn: `object_create`
(`1DF3:1157`) reads the `OJFF` and stores the object index in place of the
negated `OJFF` number. The object table is `DS:6694`, 0x140 entries of 0x20
bytes:

| Offset | Size | Contents |
|--------|------|----------|
| `+00` | 1 | flags: 0x01 animating, 0x02 ?, 0x04 set by SCMD flag 0x20, 0x20 moved, 0x80 disabled |
| `+01` | 2 | `ETAB` index |
| `+03`, `+05` | 2, 2 | screen position: `ETAB` x − `OJFF` +2, `ETAB` y − `OJFF` +4 − `OJFF` +0x0A (signed byte) |
| `+07`, `+08` | 1, 1 | hot spot (from the `OJFF` anchor, then from `SCMD` records) |
| `+09`, `+0B` | 2, 2 | world position (`ETAB` x, y; moved by `SCMD` dx, dy) |
| `+0D` | 1 | `ETAB` elevation byte |
| `+0E` | 1 | `OJFF` +0x0B |
| `+11` | 1 | current `BMP ` frame |
| `+12`, `+13` | 1, 1 | frame width, height |
| `+14` | 1 | delay of the current record |
| `+15` | 2 | `SCMD` cache slot, or −number while not loaded, or −1 |
| `+17` | 1 | current `SCMD` record index |
| `+18` | 2 | `BMP ` number (`OJFF` +0x0C) |
| `+1C` | 4 | tick of the last step |

`OJFF` (16 bytes): +0 object flags, +2 / +4 int16 anchor x / y, +0x0A int8 y
adjustment, +0x0B ?, +0x0C uint16 `BMP ` number, +0x0E int16 `SCMD` number
(0: not animated; 703 of the 2 775 have one, and every number exists).

The `ETAB` flags byte: bit 7 mirrored, bit 6 refresh once (the next step
skips the records and clears it), bit 5 draw at world position − hot spot
instead of the screen position above, bit 4 hidden. None of bits 4–6 is set
in the files.

### Object animation (`SCMD`)

An `SCMD` resource (`SEGOBJEX.GFF`) is a 0x48-byte header and a list of 8-byte
records (count = (size − 0x48) / 8). `scmd_load` (`1DF3:1A00`) caches up to
64, with the headers at `DS:57CC`.

**Header.** 32 uint16 record indexes, slot = facing × 8 + action, for the four
facings N, E, S, W; then 8 bytes (+0x40 word and +0x44 byte are read by
`896E:0C92` and `87B3:0AE1`; meaning unknown). Actions seen in the code: 0
stand, 1 walk, 7 walk diagonally. The direction tables `DS:118C` (stand: 0, 8,
8, 8, 16, 24, 24, 24) and `DS:1184` (walk: 1, 7, 9, 15, 17, 23, 25, 31) map the
eight directions onto slots: a diagonal uses action 7 of the facing
counter-clockwise from it (NE → N, SE → E, SW → S, NW → W). Creatures start
at their stand slot; other objects at record 0 (*unverified*: from the 0xFF
initial index being read as 0).

**Records** (`object_apply_scmd_record`, `1DF3:1BEF`):

| Byte | Meaning |
|------|---------|
| 0 | `BMP ` frame (0xFF: keep the frame); for a jump record, the target index |
| 1 | delay before the next step, in 96 Hz ticks; values above 99 are × 10. Forced to 0 for a moving record while `377E:0019` is set |
| 2 | flags: 0x01 jump to byte 0 (taken when the step lands on this record, which is not shown), 0x02 stop after this record, 0x04 mirror (clear: not mirrored), 0x10 mark moved, 0x20 calls `1DF3:2BCF` (unknown), 0x40 creatures: take the hot spot from bytes 5–6 instead of (width / 2, height) |
| 3, 4 | int8 dx, dy added to the world position; leaving −64…0x800 × −64…0x620 stops the animation |
| 5, 6 | hot spot x, y |
| 7 | sound effect (`play_sound_effect`), 0: none |

**Stepping** (`object_animate_step`, `1DF3:23A9`): when the last step plus the
delay is at most the tick count `DS:11D6` and the object is animating: if
the index is past the last record, animation stops and the index resets to 0;
the record at the index is applied; unless it has flag 0x02 the index moves
on, and if the new record has flag 0x01 the index becomes its byte 0. The tick
count is advanced by `timer_tick` (`1DF3:29E8`), registered at 96 Hz with the
sound library's timer (`33A5:077F`, `33A5:0992`). `main`'s loop runs without
a frame limit, so a record with delay 0 advances on every pass and its speed
depended on the PC; 38 of the walking object sprites (e.g. `SCMD` 194) walk
with delay 0. Every 8 ticks `timer_tick` also sets `DS:1194`, which makes the
loop run `22F8:047C`, a palette step (it calls the palette code `22F8:0514`),
not a frame limiter.

**Drawing order.** `draw_objects_and_walls` (`1DBD:0002`) walks the visible
rows top to bottom, drawing each row's walls and the objects whose `ETAB` y
falls in it, so objects and walls are depth-sorted by row. `region_renderer.py`
draws all objects and then all walls.

## 4. What runs the scripts

Derived statically (2026-10-18); names in `tools/ghidra/symbols.tsv`. All
script runs go through `gpl_run(file, address, kind)` (`043D:0001`): kind 1
runs `GPL ` resource *file*, kind 2 runs `MAS ` resource *file*, starting at
byte *address* (`gpl_call_file`, `043D:02F3`; loaders `043D:04BA` cache,
`043D:0555` load).

### Entry points

| When | Runs | Code |
|------|------|------|
| New game (each pass of `main`'s outer loop), and GPL start-up (`gpl_init`, `5DAA:0000`) | `MAS 99` from 0 | `main` `1512:03E0`; `5DAA:0000` |
| Entering region *n* | `MAS` *n* from 0, after removing the old region's triggers | `region_enter_scripts` (`039D:0000`), called with the current region `DS:117C` |
| A trigger matches an event | its `GPL` file and address | the check functions below |
| A creature has nothing to do | its no-orders script | `creature_noorders` (`1A0A:1897`), `run_noorders_scripts` (`1A0A:0A77`) |
| A creature sees, or loses sight of, the active party member | its line-of-sight script | `creature_check_los` (`1A0A:1914`) |

`MAS` numbers are the region numbers (2–45; `RGNFF.GFF`, region 255, has
none), plus 99 for the game start. A new game resets the region to 42
(`RGN2A.GFF`), the start position to (488, 152) (`DS:117E`/`1180`), the global
variables and the trigger lists, then runs `MAS 99`.

File 0 means "no script": `gpl_main_loop` runs nothing for it, and the
creature code treats a script file of 0 as unset. `MAS` files start with code
(run from 0); `GPL` files start with END_FUNCTION and are entered at the
addresses triggers give.

The region scripts register the region's triggers with the `CREATE_…_TRIGGER`
commands; most other scripts are `GPL` files that those triggers run.

### Trigger lists

Triggers are 13-byte records in a 200-entry table (far pointer `DS:42CC`,
allocated by `gpl_init`), chained into ten sorted lists whose heads are words
at `377E:0005`–`0017`. Common fields: `+0` address, `+2` `GPL` file, `+0B`
next index (−1 ends).

| List | Command (opcode) | Arguments | Record | Checked by | Called from |
|------|------------------|-----------|--------|------------|-------------|
| `+05` | `CREATE_MOVE_TILE_TRIGGER` (68) | x, y, address, file, b | `+4` x, `+6` y, `+8` b | `trigger_on_move` (`039D:01D1`), tiles first | region entry, for each party member's cell (`1587:1F83`); `1DF3:055A`, `1A0A:0B0F`, `7372:06DF` (*unverified*: the per-step movement code) |
| `+07` | `CREATE_DOOR_TILE_TRIGGER` (69) | as above | as above | `trigger_on_door` (`039D:0245`) | **nothing**: no reference in DSUN.EXE, and no script uses door triggers |
| `+09` | `CREATE_MOVE_BOX_TRIGGER` (6A) | x, y, width, height, address, file, who | `+4` x, `+6` y, `+8` width, `+9` height, `+0A` who | `trigger_on_move`, after tiles | as move tile |
| `+0B` | `CREATE_DOOR_BOX_TRIGGER` (6B) | as above | as above | `trigger_on_door` | nothing |
| `+0D` | `CREATE_PICKUP_TRIGGER` (6C) | address, file, object | `+4` object, `+6` global | `trigger_on_pickup` (`039D:02A7`) | `6527:0C0D`, `7BEB:0A60` |
| `+0F` | `CREATE_TALKTO_TRIGGER` (6E) | address, file, object | as pickup | `trigger_on_talk` (`039D:021F`) | `68BD:0040` |
| `+11` | `CREATE_ATTACK_TRIGGER` (65) | address, file, object | as pickup | `trigger_on_attack` (`039D:02CD`) | `612E:1359`, `7D80:003B` |
| `+13` | `CREATE_LOOK_TRIGGER` (66) | address, file, object | as pickup | `trigger_on_look` (`039D:02F3`) | `68BD:0040` |
| `+15` | `CREATE_USE_TRIGGER` (6D) | address, file, object | as pickup | `trigger_on_use` (`039D:0319`) | `6BB8:0B56`, `91F7:03D0` |
| `+17` | `CREATE_USEWITH_TRIGGER` (70) | object, object, address, file | `+4`, `+6` the objects, `+8` global | `trigger_on_use_with` (`039D:033F`) | `6527:0CC0`, `74F1:2532`, `7BEB:006B` |

Matching (first match in list order runs, and the check returns 1):

* **Tile**: the mover's cell (world position >> 4) equals (`+4`, `+6`), and
  the event's *who* value is at most byte `+0A`. Tile records never write
  `+0A` (their fifth argument goes to `+8`), and the table starts filled with
  0xFF, so in practice they fire for every mover (*unverified* whether that
  was intended).
* **Box**: `+4` ≤ x ≤ `+4` + width − 1, `+6` ≤ y ≤ `+6` + height − 1, and
  *who* ≤ `+0A`. For party moves *who* is 0 for the active member
  (`37B7:0009`) and 1 for the others: a box with 0 fires only for the active
  member.
* **Object** (pickup, talk, attack, look, use): `+4` equals the target's id,
  field 0x48 of the object (read through `0826:0009`). Scripts name objects
  as `name(-n)` with *n* the `OJFF` number: 356 of the 449 objects named by
  the region scripts' object triggers are `OJFF` numbers in that region's
  `ETAB` (the rest are presumably made at run time, e.g. by CLONE).
* **Use with**: the two ids match in either order.

Before running, the checks set the script's context: `37B7:0007` (=
`3781:0367`) the actor, `37B7:0005` and `37B7:0003` the target or the two
objects; `1A0A:0256` focuses the actor.

**Lifetime.** `region_enter_scripts` empties the tile, door and box lists, and
removes object and use-with triggers whose "global" byte is 0. That byte is
set when the trigger was created by `MAS 99` (`377C:0012` = 99 and `377C:0014`
= 2), so the game-start script's object triggers survive region changes.
Registering a trigger with the same key as an existing one replaces it.

### Per-creature scripts

A table at `3972:08AA`, 0x13 bytes per object (indexes 0–3 are the party;
the others are creatures), filled by:

* `CREATE_NOORDERS_TRIGGER` (6F) (address, file, object) via
  `creature_set_noorders` (`1A0A:053E`): `+04` address, `+08` file. Run when a
  non-party creature has no current order (`+00` = 0). A creature with neither
  script falls back to `creature_default_idle` (`1A0A:0743`).
* `CREATE_INLOS_TRIGGER` (1B) and `CREATE_NOTINLOS_TRIGGER` (1C) (address,
  file, object, range) via `creature_set_los` (`1A0A:0525`, kind 1 or 2):
  `+02` kind, `+06` address, `+0A` file, `+0C` range. Kind 1 runs when the
  creature has a clear line of sight (`line_of_sight`, §3) to the active
  party member and the distance (`0898:000C`) is at most the range; kind 2
  when the sight line is blocked or the distance is greater.

Script use of the trigger commands (all 250 scripts): no-orders 1 224,
in-LOS 768, talk-to 536, attack 664, move-box 408, use 324, not-in-LOS 216,
use-with 164, look 150, move-tile 94, pickup 16, door 0.

### Text, dialogue and teleports

From the handlers (2026-10-18):

* **Text windows** are one routine, `84C9:0253(window, …)`: window 0 the prompt
  buttons, 1 the picture, 2 the text, 3 "wait for a button" (then
  `84C9:0CD9` returns the button pressed).
  * `PRINT_STRING(style, text)` (`7372:15D9`) prints in window 2; only the
    style's low byte is passed. The scripts use 115 (10 849 times, speech) and
    98 (2 868 times, narration); what the byte selects is not traced.
  * `PRINT_NUMBER(style, n)` prints n in decimal; `PRINT_NEWLINE` prints `"\n"`.
  * `SHOW_PIC(n)` shows `PORT` n (1–120, 32 × 32) in window 1; `CLEAR_PIC`
    clears window 1.
  * `CONTINUE` shows "Press continue" and a Continue button; `ASK_YES_NO`
    "Answer Yes or No", Yes and No, and sets acc to 1 for Yes.
* `NAMETONUM` and `NUMTONAME` only negate: acc = −expr.
* **TELEPORT** `(who, region, x, y, flag)` (`043D:1EAE`), cells:
  * who 32766 (0x7FFE) is the party (`7372:0857`): in the current region each
    member is placed at a free cell near (x, y) (`7372:06DF`, which also
    fires move triggers); another region is entered (`7155:1487`, which runs
    `region_enter_scripts`). The party's orders are cleared; acc = 1.
  * who < 0: every object named −who; who ≥ 0: that object index; each via
    `7372:06DF`. Region 255 takes creatures off the map.
* **Region exits** are move-box triggers on the map edges that run `GPL 80`:
  each entry sets the edge (1 north, 2 east, 3 south, 4 west), and the routine
  at `053C` returns unless the active member faces out through it
  (`obj_special(0x25)` field 95, facing 0–7 clockwise from north), asks
  whether to leave (`gstr[6]`, ASK_YES_NO), and on Yes the entry teleports the
  party (`TELEPORT(32766, region, x, y, 0)`).

### Creatures, orders and queries

From the handlers (2026-10-18); the engine's model is `DarkSun.Game.World`.

* **Selectors** (`1A0A:0922`): a negative value is a name, matching every
  object whose field 0x48 (72) equals it (creatures from `OJFF` n have −n);
  0 or more is an object index (0–319; 0–3 the party); 9999 is none.
* **Orders** go through `1A0A:03CA(who, mode, target, extra)`, for object
  indexes below 48 only. Except for modes 0x0F and 0x11, the target is
  resolved to its first match, and with no match nothing happens. The order
  is stored in the creature table (`3972:08A6`: +0 target, +2 extra, +4 mode).

  | Command | Mode | Target / extra |
  |---|---|---|
  | FLEE(who) | 0x01 | — |
  | FETCH(target, who) | 0x0A | target |
  | FOLLOW(target, who) | 0x0B | target |
  | GO(target, who) | 0x0D | target |
  | GOXY(x, y, who) | 0x0F | x, y |
  | WAIT(who) | 0x11 | — |
  | HUNT(who) | 0x12 | the active member (`3781:0369`) |

  READORDERS(who) returns the mode; CLEAR_LOS(who) clears the line-of-sight
  kind (+0x8AC).
* **Queries:**
  * GETXY(sel) puts the cell in `gname[1]`, `gname[2]` and 0 in `gname[3]`.
  * RANGE(a, b): a is an index. It returns the smallest distance
    (`0898:000C`) to b's matches other than a, makes the nearest "other"
    (`gname[8]`), and returns 999 if there is none. NEXTTO is RANGE < 2.
  * GET_LOS(a, b, range) is 1 if any match of b is in clear sight within
    range, and also sets other.
  * SET_OTHER(e): a name, or 9999, stores its first match's index in
    `gname[12]` (`3781:0361`), with acc 1 if found, else 0. An index is
    stored as given.
  * GET_STATUS(who) is the combat record's status (+0x1C) for |who| 1–50,
    else 8. BITSNOOP(a, b) is (a & b) == b, computed in the VM.
* **FIGHT** starts combat at (`gname[1]`, `gname[2]`) (`7372:030B`) and ends
  the script. **CLONE**(name, count, x, y, radius, flag) makes copies near
  (x, y) (`7372:0A44`) and returns how many it made.
* **REQUEST(code, who, a, b)** dispatches at `87B3:0000`, with a jump table at
  `87B3:0177`:

  | Code | Effect |
  |---|---|
  | 5 | toggle object flag 0x40 and play the `SCMD` sequence at header byte 0x44 (or slot 0 when switching back) |
  | 10 / 11 | set / clear `GMAP` 0x40 at (a, b) |
  | 12 / 13 | set / clear `GMAP` 0x80 at (a, b) |
  | 17 | if who < 0, place `OJFF` −who at cell (a, b) (`896E:0E2E`) |
  | 18 | set the allegiance (combat record +0x1D) of matching creatures (index ≥ 4, kind 2) |
  | 19 | `DS:116A` = 1 (reload) |
  | 1–4, 6–9, 14–16, 20 | not identified: `073D`, `6BB8:0D13`, `028D`, `0AC7`, `0AE1` (spawns a creature, combat-related), `0CB4`, `0F1C`, `0FE9`, `1587:2927`, `6BB8:0FA9`, `13B9` |

  The scripts use these codes this often: 18 ×515, 16 ×382, 17 ×144,
  2 ×126, 5 ×85, 11 ×67, 9 ×62, 7 ×50.
* **Engine variables** (`gname` n is raw 0x20 + n; pointer table at `3781:0000`):

  | gname | Address | Meaning |
  |---|---|---|
  | 0 | `377E:0019` | combat or fast-mode flag (exits refuse when ≠ 0) |
  | 1–3 | `377E:001B–1F` | GETXY |
  | 4 | `377E:0021` | current region |
  | 5 | `3781:0369` | active member |
  | 6 | `0367` | actor |
  | 7 | `0365` | target |
  | 8 | `0363` | other |
  | 9 | `035B` | game clock (int32) |
  | 10 | `0357` | int32, unknown |
  | 11 | `035F` | unknown |
  | 12 | `0361` | SET_OTHER |

  `obj_special(n)` is the object whose index is in raw variable n, so 0x25
  is the active member.

## 5. Validation

* All 217 `GPL` and 33 `MAS` resources in `GPLDATA.GFF` decode with no unknown
  bytes, ending exactly at the resource end (`python src/gpl_parser.py
  --gff=dark_sun/GPLDATA.GFF --all --check`).
* All 16 002 local jump / call / menu targets land on an instruction start (the
  other 325 address operands are CALL_GLOBAL targets in other files).
* The earlier libgff-based parser desynchronised in 121/250 scripts, mainly by
  treating `61`/`62` as parentheses (they are immediates; `61` is CMPEND).
* Dark Sun 2 (`dark_sun_2/data/GPLDATA.GFF`): 258/350 decode; it is a later VM
  revision with extra opcodes.

## 6. Open questions

* `OP_28` (expr, expr, string; sets the trace flag like SOURCE_TRACE) is unnamed.
* Engine variables 10 and 11 (`3781:0357`, `035F`); object fields other than
  19, 72, 75 (allegiance, inferred) and 95.
* The order handlers themselves (how GO, FOLLOW, HUNT and FLEE move a
  creature, and when no-orders and line-of-sight scripts run); the engine
  approximates them.
* libgff mnemonics for most engine commands are not individually verified.
* Byte 0 of every script is `19` (END_FUNCTION); real entry points come from
  `GPLI`, whose format is not decoded yet.

## 7. Cinematics and the main menu

From the handlers (2026-10-18). Played by `DarkSun.Game.Cinematics.CinematicPlayer`.

### Which cinematic plays when

`play_cinematic(n)` (`60B9:042E`) is a switch on n:

| n | Scripts | Called from |
|---|---|---|
| 0 | `ACF` 1–8, stopping when one returns 0 (skipped) | start-up (`6A94:0138`), when sound and the intro are enabled (`DS:1162`, `DS:1160`) |
| 1 | `ACF` 9, 10 | entering region 42 with a new party (`1587:1F83`) |
| 100 | `ACF` 11, 12, 13 | the ending |
| 200, 300 | a `CMAT` / `CPAL` picture | |

`REQUEST(6, n)` (`87B3:0AC7`) plays cinematic n from a script.

### `ACF` scripts (`985F:0000`)

A script is a list of 16-bit words. An instruction's first word is
`op << 8 | length in bytes`; its arguments follow. Op 0 ends the script.
Before running it, the interpreter collects every op 1 entry,
`(index, frames, bytes)`. These describe the `BMA` with the same number: it
is that many images in the `BMP` format, back to back, streamed 64 KB at a
time (`0852`).

There are two VGA pages (`0980:5814(-1)` flips them): page 0 is shown and
page 1 is hidden. The script's clock (`3931:02C5`) is reset when each script
starts and counts XMIDI callback events with value 0x7F (`3164:0033`), so
waits follow the music (about 8–10 ticks a second). Colour cycling and the
effects run on a 70 Hz timer (`985F:0C60`, set up through `33A5:0992`). Any
key ends the cinematic (`0CE5`).

| Op | Arguments | Effect | Handler |
|---|---|---|---|
| 01 | index, frames, bytes | `BMA` chunk table (no effect when run) | |
| 03 / 04 | | timer flag on / off | `0C1E` / `0C49` |
| 05 | | wait for the next tick, draw the next `BMA` frame on the shown page at (0, 0) | `099A` |
| 06 | n | wait n ticks, each counted from the last | `0A91` |
| 07 | | clock, frame and chunk counters to 0 | `0AE2` |
| 08 | a, n | (`0980:4723(1, a)`), flip, wait n ticks | `0B35` |
| 14 | `index << 8 \| R`, `G << 8 \| B` | set a colour in the staging palette | `9B64:0000` |
| 15 | | apply the staging palette | `9B64:00A0` |
| 16 | n | set `PAL` n | `985F:0BD0` |
| 19 | slot, first, last, speed | colour cycling: every `speed` timer steps colours first…last rotate up by one, the last one moving to the front | `9B64:017B`, `22F8:0387/047C` |
| 1A | slot | stop cycling | `9B64:0235` |
| 28 / 29 | slot (0–4), n / slot | load `BMP` n / free it | `9388:0000` / `0105` |
| 2A | slot, x, y | draw the slot's image on the hidden page | `9388:01D8` |
| 2B | | flip | `9388:02E4` |
| 3C | n | dissolve: copy the hidden page to the shown page in the order of a 17-bit LFSR (mask 0x12000; x the low 9 bits, y the rest; (0, 0) is never reached), flip, wait until n ticks since the start | `9794:0000` |
| 3F | first, last, R, G, B, n | fade colours first…last toward (R, G, B) ÷ 4, one unit per channel a pass, then wait until n ticks | `9794:046C` |
| 40 | p, n | fade all colours toward `PAL` p likewise | `9794:07DD` |
| 5B | bands, n | wipe: the hidden page opens out from lines 200 ÷ bands apart, one row a step, flip, wait until n ticks | `93D2:112C` |
| 64 | n | play music n (`GSEQ` etc. 26–29) | `9936:0000` |
| 65 / 66 | ms | music fade in / out | `9936:0048` / `0073` |
| 7F | 12 values | not decoded (only in `ACF` 11) | `9944:1AD7` |

Ops 2, 9–0x13, 0x17, 0x18, 0x2C, 0x3D, 0x3E, 0x50–0x5A, 0x5C–0x5E, 0x67 and
0x79–0x7E exist but the shipped scripts don't use them.

### Windows, buttons and accelerators (`RESOURCE.GFF`)

* `WIND`: a 0x105-byte header and 30-byte items. `+0x08` is the number,
  `+0xBE`/`+0xC0` the size and `+0xF3` the item count. Item i is at
  `0x105 + 30·i`: `+4` tag, `+8` number, `+0x0C`/`+0x0E` position.
* `BUTN` (110 bytes): `+0x28`/`+0x2A` size, `+0x5A` the `ICON` drawn. Icon
  frames: 0 normal, 1 highlighted, 2 disabled (1 × 1 if none), 3 pressed.
* `ACCL`: `+0x0C` count, then 5-byte entries (flags, key, command).
* **Main menu** (`8015:0208`): clear the screen, `PAL` 1001, `BMP` 20028 at
  (47, 25) and 20029 at (4, 45), then window 3000. Its buttons are 2048–2051
  (Start Game, Create Characters, Load Saved Game, Exit to DOS) and its
  accelerators are `ACCL` 8100 (S s C c L l E e → 8100–8107). The handlers
  are in a table at `8015:01D8`.
