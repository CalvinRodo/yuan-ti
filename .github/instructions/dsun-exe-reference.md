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
`7372:0388` and `1A0A:2AB4` read the size from byte 0x3D of the object's
character record, in the table at far pointer `DS:1661` (17 records of 0x47
bytes). An object without one has size 0. The records come from the
creatures' `RDFF` resources: after the combat record (type 2, 58 bytes) comes
an entry with load action 3, type 4 and length 0x47, then the record.
`7155:0732` copies type 4 (object kind 4) into `DS:1661`, type 2 into
`DS:1665` and type 1 into `DS:165D`. For creatures outside the party it
multiplies the word at +8 by ((difficulty − 1) × 50 + 100) / 100.

The 290 creatures in `SEGOBJEX.GFF` have these sizes:

| Size | Footprint | Creatures |
|---|---|---|
| 0 | 1 × 1 | 263 |
| 1 | 3 × 1 | 3 (Daggoran, Wyvern) |
| 8 | 1 × 2 | 9 (Psurlon, Rampager, …) |
| 9 | 3 × 2 | 8 (Mastyrial, Otyugh, …) |
| 17 | 3 × 3 | 7 (Dark Spider, Spider Mage, …) |

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
| 5, 6 | hot spot x, y (signed): replaces the object's hot spot (first the `OJFF` anchor), and the object is drawn at its world position minus it (`217D:0070`; y also minus object byte +0x0D). Creatures use (width / 2, height) unless flag 0x40. A door leaf's frames narrow towards the hinge, so its hot spot changes every frame (`SCMD` 78: x 23 → 7) |
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

**Drawing order.** `draw_objects_and_walls` (`1DBD:0002`) draws the objects
in list order (`DS:2420`, 8 bytes each; every shipped `ETAB` is sorted by y).
Before an object whose y is in row `y >> 4`, it draws the walls of every
visible row above that row, top to bottom and left to right. A wall on row r
therefore lies over objects with y < (r + 1) × 16 and under those at or below
that line. The walls are drawn at (x × 16 + 8 − `DS:574C`[w], y × 16 + 16 −
`DS:574D`[w]). `region_renderer.py` draws all objects and then all walls; the
engine's region view sorts by these rules.

**Pathfinding.** `1A0A:0B0F` plans a move with `08BB:07F3(nodes[30],
x, y, goal x, goal y, path[80], 30, 1, 80, object+0x1A)`. It first sets
`DS:01FA` = 1, which makes the wall-follower probe 45° back towards the wall,
and repeats with 0 if that fails. Creatures (index ≥ 4) more than 24 cells
from their goal that planned less than 32 ticks ago give up instead.
Blocked means `footprint_blocked` (`1A0A:11DB`): `GMAP` 0x40 anywhere under
the mover's footprint, other creatures not counted. The algorithm is a "bug"
pathfinder:

1. **Line** (`08BB:0000`): step from the start by the signs of (goal − here)
   (`096C:009B`: in mode 1 an 8-way table at `3779:0000`; otherwise 4-way),
   with directions 0 = N clockwise and steps at `096C:0002`/`0012`, for at
   most 80 points. Points are 6 bytes: x, y, direction on. If the end of the
   line is blocked, the target moves back to its last free point.
2. **Nodes** (`08BB:00B9`), 18 bytes each: x, y, dir, turn, straight link,
   two side links, parent, next. Links are node number + 1; 0 means none yet
   and −1 dead. The nodes are the start, then the free cells before and after
   each blocked stretch of the line, then the target. Overflowing 30 nodes
   fails.
3. **Search** (`08BB:02D2`):
   * Where a node's next line cell is free, it links straight to the next node.
   * Otherwise two followers (`01D5`, turns −2 and +2 from `DS:01FE`/`0200`)
     step round the obstacle. Each tries its heading, turns by its turn when
     blocked, and turns back after each step. It fails after 4 turns.
   * A follower that lands on a node further on wins: that node's parent is
     set, and it becomes current. One that returns to its own node fails.
   * Landing on an earlier node is remembered. If that follower later wins,
     the detour starts from the earlier node instead.
   * If both followers fail, the search falls back to the parent node. It
     gives up after 500 rounds.
4. **Replay** (`08BB:05C1`): walk the linked nodes, straight or by
   wall-following, writing every cell, at most 79.

Ported as `DarkSun.Game.Movement.OriginalPathfinder`. One flaw shows in the
port: the remembered earlier node can be one the route skipped, and the
replay only moves forward. In 40 of 6 600 random long paths, the path then
runs straight into a wall. The mover would stop at that step.

**Walking speed.** A walk step lasts as long as its walk records take: SCMD 2
and 109 take 4 × 7 ticks for 16 px (3.43 cells/s), and 6 others 32 ticks
(3.0 cells/s). SCMD 239's cycle covers only 8 px, so it runs twice per cell.
Nine walk scripts have delay 0 and moved once per pass of the main loop, at a
speed that depended on the PC.

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
objects; then `1A0A:0256(actor)`, GETXY's core, puts the actor's cell in
`gname[1]`, `gname[2]` and 0 in `gname[3]` (`477E:001B/1D/1F`). Scripts read
them without a GETXY of their own: `GPL 210` clones a rope at
(`gname[1]` + 1, `gname[2]` − 1).

**Region changes.** TELEPORT of the party to another region
(`7155:1487`) saves the old region (`7155:0C64`), loads the new one and runs
`region_enter_scripts` (`039D:0000`); it checks no move triggers.
`1587:1F83`, which builds the adventure screen (from the main menu, `8015`,
and two other screens, `60B9`, `6CCA`), runs `region_enter_scripts` for a new
game and then `trigger_on_move` for each party member's cell.

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
    98 (2 868 times, narration), but window 2 (`84C9:0253`, jump table at
    `84C9:09E6`: 0 `027B`, 1 `0318`, 2 and 4 `0323`, 3 `0745`) never reads
    it: all text looks the same.
  * `PRINT_NUMBER(style, n)` prints n in decimal; `PRINT_NEWLINE` prints `"\n"`.
  * `SHOW_PIC(n)` shows `PORT` n (1–120, 32 × 32) in window 1; `CLEAR_PIC`
    clears window 1.
  * `CONTINUE` shows "Press continue" and a Continue button; `ASK_YES_NO`
    "Answer Yes or No", Yes and No, and sets acc to 1 for Yes.
* **The windows** (`RESOURCE.GFF`, all 318 × 58 at the bottom of the screen,
  over the stone panel in the top 58 rows of `BMP` 3007): `8BEB` opens
  `WIND` 3007 (`6BB8:0152(0xBBF)`, kept at `4E5F:0004`) and `WIND` 3500
  (`4E5F:0008`).
  * `WIND` 3007, the text window: `BUTN` 12300 (300 × 58, no icon: the
    whole window is a button), `BUTN` 2090 (44 × 44 at (8, 6): `ICON` 2090,
    frame 0 the 48 × 47 portrait frame, frame 1 the 32 × 32 sun shown with
    no portrait), `EBOX` 4002 (236 × 46 at (58, 6); +0x5A font 2081, +0x7E
    229, the magenta transparent key, +0x80 199 the text colour, a tan),
    `BUTN` 2093 (14 × 14 at (305, 4), `ICON` 2093: the up arrow) and `BUTN`
    2094 (14 × 35 at (305, 18), `ICON` 12100: MORE over a down arrow). Their
    frames are normal, highlighted, disabled, pressed. `84C9:0020` enables
    2093 when the text box isn't at its top and 2094 when there is more below.
  * `WIND` 3008, the choices: five `BUTN` 2076–2080 (300 × 10 at (3, 13),
    (3, 21) … (3, 45), `ICON` 12102–12106: stone bars, text drawn on them),
    `BUTN` 2095 (`ICON` 2093) and 2096 (`ICON` 12100) to scroll.
  * `WIND` 3500 holds only `EBOX` 4500 (239 × 46 at (56, 6)): text without
    a portrait.
  * A `BUTN`'s icon is the word at +0x64 (+0x5A is its own number).
  * Controls share a header (tag, length, number at +0x08). The layout
    routine `2A1D` reads the alignment word at +0x58: bits 8–9 place the
    icon across and 10–11 down, bits 4–5 the text across and 6–7 down, each
    0 at the start, 1 inset by the border (2 or 3 pixels, more with the
    flags at +0x0E), 2 centred, 3 at the end. An `APFM` has only a number,
    a size and that word: a slot the code fills (`2AF5` sets them).
* **The font** (`FONT` 100, loaded at start-up by `2531`, pointer at
  `577D:5EA3`, current font `577D:6108`): see `DarkSun.Data.Text.GameFont`.
  The blitter (`2E86:06C0`) draws a glyph pixel p in colour `font[8 + p]`,
  skipping 0; widths add up for a string (`2E86:07E0`); the height is the
  word at +2 (`2E86:082C`). Before drawing a text box `2BF9:055D` sets
  `map[0] = 0` and `map[DS:3C02]` (set to 0xFE) to `DS:A1C9`, the colour:
  the letters take the text colour and their shadow (pixel 20) stays
  palette colour 20 (56, 56, 84 in every palette).
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
  | 1 / 4 | the party rests (`073D(1, a)` / `073D(0, a)`): "THE PARTY RESTS"; members with status 2 or 3 (with 1: any) get status 1, then healing and spells (not traced further) |
  | 2 | a door: `6BB8:0D13(object, a, 1)` (see Doors) |
  | 3 | `028D(who, a, b)`: for the party, the member with the lowest `030B` score, then `caseD_e`; a check used in conditions (13 uses), not traced |
  | 6 | play cinematic a (`0AC7`) |
  | 7 | `0AE1(who, x, y)`: makes object −who at cell (x, y); an item there sets off an area effect (a party check `028D(party, 2)`, damage to creatures around), then the object is removed. A trap or blast; combat |
  | 8 | `0CB4(a, b)`: a random monster from the region's `MONR` table (42-byte records); combat |
  | 9 | `0F1C(who, ojff, b)` (each match when who ≤ 0): remove the object and make `OJFF` a where it stood (b ≠ 0: at its saved start), with its facing; the new one becomes "other" (`37B7:0003`) |
  | 14 | `0FE9`: an ambush: makes objects around the active member, sets allegiances and starts combat there |
  | 15 | `1587:2927(1)`: sets `577D:09B5` and `DS:11B8` and calls `191F:0A99(1)`; not identified |
  | 16 | `6BB8:0FA9(a)`: if sound is on and a ≠ `DS:0D9A`, `361A:0000(a)` sets the music's intensity level (an XMIDI section, at most the track's byte at `DS:1675 + 3·track`) |
  | 20 | `13B9(a)`: how many objects 5–319 are creatures (kind 2) whose allegiance has a bit of a (all with a = 0) |

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
  | 10 | `0357` | the party's money (int32; CHANGE_MONEY adds to it) |
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
* Engine variable 11 (`3781:035F`); object fields other than
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

### Music

* Sequences are XMIDI, in four versions per track: `GSEQ` (General MIDI:
  GM programs, drums on channel 10), `LSEQ` (MT-32 / LAPC-1 programs),
  `PSEQ` and `CSEQ`. `RESOURCE.GFF` holds tracks 1–23 and `CINE.GFF` holds
  26–29. They are played by the Miles AIL driver (`33A5`) at 120 ticks a second.
* **What plays when:**
  * The main menu plays 17 (`8015:0208` → `1A0A:0672(0x11)`).
  * Entering a region plays the byte at `+0x2F` of its `RDAT` record
    (`6BB8:0A41` loads the 49 bytes to `4931:025D`; `6BB8:030E` →
    `35D1:0265(5000, 4931:028C)`). For example, region 42 plays 2.
  * The record also holds two colour-cycling ranges (first, count,
    speed × 10) at `+0` and `+3`.
  * GPL `MUSIC` (op 0x5F, `1A0A:0672`) is used by no shipped script.
* **Changes and fades:**
  * `1A0A:0672(n)` does nothing if n is already playing; otherwise it calls
    `35D1:0265(5000, n)`. That fades out what plays over 5000 ms, stops it,
    starts n at volume 0 and fades it in over 5000 ms.
  * `35D1:012D(ms, 0x32)` sets the volume to 0 and then ramps to the music
    volume; `(ms, 0x33)` ramps to 0.
  * `3592:000E(n)` starts a track if its flags at `DS:1676 + 3n` allow it
    (bit 4); bit 2 hands it to a hook.

### Names (`NAME`)

`GPLDATA.GFF` `NAME` 1 is loaded whole at start-up (`60B9:0165`, after the
item-type table; pointer at `DS:166D`, also `577D:1179`). It is read as
`char[25]` records. Name n is `_fstrcpy`'d from `table + n * 0x19`
(`612E`, `78A9:5642`, `88F4:0237`). The number comes from the word at +0x12 of
a 0x15-byte item record in the table at `DS:165D`. When the byte at +0x14 is
set, the name is formatted instead (`1D53:0002`). The 3 bytes at the end of
each record are not read.

### Items (`IT1R`, item records)

* **Item types:** `GPLDATA.GFF` `IT1R` 1 loads to `DS:1669`, as 20-byte
  records (×0x14), at start-up (`7155`, `7372`, `60B9:0165`). The code reads
  +0x0A (range; below 2 is melee) and +0x0E (damage bonus) when showing a
  weapon (`78A9:2A9F`). The other offsets follow libgff's `ds_item1r_t`; the
  shipped data fits them (daggers 1d4, chest armour in slot 1, …).
* **Item records:** a table of 399 records of 0x15 bytes at `DS:165D`
  (`7155`). Loading an object copies the first entry of its `RDFF` there when
  it is an item (`7155:0732`), and sets +4 and +8 to 9999 (none) and +0x0C
  to 0. Combat records (0x3A bytes) go to `DS:1665` instead.
  * Read by the code: +0 id (−RDFF number), +0x0A the `IT1R` number, +0x0F
    flags (bit 1 → `6BB8:0FF8`; bit 8 → cursor 0x1778), +0x12 the `NAME`
    number, +0x14 the bonus.
  * The name shown is `NAME` n, or with a bonus `sprintf("%+d %Fs", bonus,
    name)` (`DS:1D3E`). The bonus is added to the type's damage bonus. The
    next string, `"%d%s*%dD%d%+d"`, formats damage.
  * Every item `RDFF` that references an `IT1R` or `NAME` entry (745 and
    706 of 755) references the record's own type and name.
* **`IT1R` +8, material:** the low nibble indexes the material names at
  `3B68:0000` (`74F1:23AA`): 0 wooden, 1 bone, 2 stone, 3 obsidian, 4 metal,
  5 leather. Bit 0x20 is a fixture that can't be picked up (`68BD:0C6D`;
  doors, geysers, nests). 0x40 is on ordinary items and 0x80 on named magic
  ones; 0x80 with a material below 4 selects magic-item cases (`612E:2501`,
  `6868:0337`), and 0x04 adds 10 to a value (`612E:0190`). Those three are
  not traced further.
* **`IT1R` +0x0F, flags:** 1 offers Use (`68BD:0C6D`); 2 stacks
  (`7372:0FEE`) and swaps where the power is kept (below); 8 is a container
  (the inventory's VIEW INVENTORY, `6527:12BD`; chests, bags, skeletons);
  0x80 armour (verb-window flag 8). 0x40 is on two-handed weapons only
  (staves, the great axe, polearms, bows; inferred). 4 (doors, chests,
  nests, obelisks) and 0x10 are not traced.
* **`IT1R` +0x10, classes:** a word with one bit per class code. A
  character record's +0x12 is the OR of `DS:0718`[code] over its class
  codes (`6868:0000`). The codes come from `38D4:00ED` + class × 4 +
  sphere: 1–4 cleric of air, earth, fire, water; 5–8 druid; 9 fighter; 10
  gladiator; 11 preserver; 12 psionicist; 13–16 ranger; 17 thief. The bits
  are 1, 2, 4, 8 the four clerics, 0x10 druid, 0x20 fighter, 0x40 gladiator,
  0x80 preserver, 0x100 psionicist, 0x200 ranger, 0x400 thief. 0x800 and
  0x1000 belong to no player class; wands and scrolls carry 0x880, so likely
  the defiler and the templar. Wearing an item needs a bit in common
  (`6466:0854`). 44 of 115 types are 0x1FFF (anyone).
* **Magic: quantity, charges and +0x0F.** A magic item's quantity (+2) is
  the spell or power it casts (`SPIN` n). The Wand of Missiles is 9, MAGIC
  MISSILE; the psionicist's discipline items are 145–171. Its charges
  (+0x0E) are the uses left: a use takes one, and at 0 the item goes
  (`78A9:2324` → `7BEB:0A06`). For a type that stacks the two swap: the
  quantity counts and the charges hold the spell. +0x0F keeps a copy of the
  spell, put back by `78A9:6488`. The shipped items agree (fruit casts
  NEUTRALIZE POISON with one charge). Doors keep their state in +0x0F
  instead (bits 1/2/4/8).
* **Item record +0x10, special:** 1–6, how easily it breaks; 6 never (480
  items). `80F5:1F4B` picks the carried item with the lowest value below 6
  and breaks it when 1d20 < 15 − its bonus (`612E:20A1`). `612E:2700` turns
  it into a "Broken Weapon": type 107, `NAME` 203, id −1210, special 1. Its
  callers (combat) aren't traced.
* **Containers' contents:** the shipped item `RDFF`s have none: they hold
  only the record and the `IT1R` and `NAME` references. Contents come at run
  time: GIVE to the object, and death. Creatures do carry items in their
  `RDFF` (98 of 291, 479 items; see "Creature `RDFF`s"). When a creature
  dies, `612E` makes `OJFF` 1101 ("Dead Body", special 5) beside it and
  moves its items into the body.
* **Creature `RDFF`s** (`7155:0000`, the object loader): entries are read
  in order, each numbered as a block (the object is block 0), and the load
  action (switch at `7155:038F`) says what an entry does:
  * 1 (`7155:0397`): the object's own record (`7155:0732` by type).
  * 2 (`7155:03D7`): a new object (`7155:04E6`) loaded from the entry and
    stored in field `from` of block `blocknum` (`0826:012A`, the object
    field setter GPL uses too). A creature's first carried item: from 0x4F.
  * 3 (`7155:0416`): a reference. Type 4 loads the character record into
    the current object; otherwise field `from` of block `blocknum` is set to
    `index`: an item's `IT1R` type (field 4, record +0x0A) and `NAME`
    (field 2, +0x12).
  * 4 (`7155:0484`): a new object linked after block `blocknum`'s item
    (item record +4, the next link): the other carried items.
  * The list ends at a header whose action is 0xFF (the 10 bytes after the
    last entry). The shipped creatures are combat record (1), character
    record (3), then per item: item record (2, then 4), `IT1R` and `NAME`
    references (3). Every carried item's record names its own `RDFF` (−id)
    and matches that `RDFF`'s type and name; one (of 479) is worn, the rest
    are in the pack (slot 255).
  * For a creature (index 4 and up) the loader then sets the combat
    record's hit points to the character record's +8, after the difficulty
    scaling: 85 creatures have a different number at combat record +0.
    `DarkSun.Data.Regions.CreatureDefinition`, `src/ds1_parsers.py`
    (`carried_items`).

### Items in scripts

* **Who carries what:** each holder (creature, party member, or an item:
  a container) has a list of item-table entries, read with
  `1A0A:1A2D(object, buffer)` (10-byte entries: kind 1, item index),
  added with `1A0A:20EC` and removed with `1A0A:1CD6`. An object's kind
  is the byte at `0C36 + 3·index` (0 scenery, 1 item, 2 creature), its
  record index the word after it.
* **GIVE(count, item, to, from)** (`043D:15FE` → `7372:0D93(from, to,
  count, item)`) and **TAKE(count, item, from, to)** (`043D:1DE9` →
  `7372:0D1D`): from 9999 makes new items (`7372:0FEE`: `RDFF` |item| loaded
  with `7155:0000`; one entry with a quantity when the type's `IT1R` +0x0F
  has bit 2, else one entry each; refused past 0x4E entries); to 9999
  destroys them, one at a time (a stack loses 1). Otherwise entries move
  (part of a stack: new ones for the receiver). Item 32767 is any item.
  TAKE from 32766 asks each party member in turn (for the whole count
  each time); TAKE from a party member takes from the active member
  (`DS:179E`) first. Both return how many moved.
* **DROP(count, item, who)** (`043D:12A5`, `7372:0370`): who 32766 is the
  party, 32767 nothing; matching creatures put items on the map
  (`7372:04E4`: a new object at their place, with the quantity in field
  0x52). Returns 1 if as many as asked (or count 32767) were dropped.
* **SEARCH(who, range, criteria…)** (`043D:08F8`): who 32766 is each party
  member, an index one object, a name nothing. Each criterion is a field,
  an op byte and, for ops 4–6, a value. The last criterion's op decides
  the result, combined over members: 0 the smallest, 1 the largest, 2 and 3
  added, 4–6 or'ed (jump table at `043D:0B04`). Per member the work is
  `0826:0344` (not traced). The fields used: 72 (0x48) the item's id,
  82 (0x52) its quantity, 85 (0x55) its equipment slot. The range bytes
  (77–80 or 0–0 in every script) go through `1A0A:0234` per kind.
* **Equipment slots** (`6466:0854`): 14, item record +0x11 (255 in the
  pack); slot n takes the `IT1R` placement at `3B67:0000` + n: 3 arms,
  11 ammunition, 12 missile weapon, 5 hand, 9 finger, 2 waist, 10 legs,
  6 head, 7 neck, 1 chest, 5 hand, 9 finger, 8 cloak, 4 feet. Scripts test
  slot 13 (the Serpent Boots, worn) and 2 (a bow).
* **Pick up** (`7BEB:0A60`): an item goes into the pack (`7BEB:01F5`),
  then its pickup trigger runs (`039D:02A7`); a party member's box opens
  their inventory.
* **Money** is `gname[10]` (`3781:0357`, int32): CHANGE_MONEY (`043D:24C4`
  → `1A0A:28F6`) adds to it; scripts compare and print it.
* **AWARD(who, xp)** (`043D:10EC`, `7372:1467`): 32766 each party member,
  32767 nobody; a member below status 3 gains xp ÷ their number of classes
  (`8E77:02E5`) in the character record (+0, the most at +4), up to
  2 000 000 000; out of combat `8E77:0A8F` follows (see "Experience").
* **DAMAGE(who, n)** / **PHYS_DAMAGE(who, percent)** (`043D:0C8A` →
  `7372:12AA` / `1342`, `80F5:0E61`, `612E:18DA`): 32766 the party, 32767
  nobody, a name each match. PHYS_DAMAGE takes that percentage of the hit
  points (+0); scripts use 1000 to kill. For a creature: damage below its
  hit points takes them; up to 9 more leaves status 3 and 0 hit points;
  more, status 5 (dead); 10 000 or more, 8. The status is also copied to
  `DS:0333 + |id|` (|id| 1–50). Status above 2 takes it out of combat and
  calls `612E:1AAF`. An item takes damage above its type's +6 word.
* **SHOP(merchant)** (`043D:1192` → `7372:08D2` → `74F1:042B(actor,
  merchant)`, `84C9:11B4`): the shop screen, not traced. Scripts count the
  merchant's items with SEARCH before and after it to tell whether
  something was bought.

### Sound effects

* `play_sound_effect(n)` (`191F:0A6F`) does nothing when sound is off
  (`DS:1162`, `DS:11A8`) or n is −1. Otherwise it calls `3172:04F1(n, …)`.
* Callers: `SCMD` record byte 7 (`object_apply_scmd_record`, `1DF3:1BEF`),
  GPL SOUND (op 0x5D, `1A0A:0663`), and cinematic op 0x67.
* **The sound table** is 182 entries of 6 bytes at `3A98:08A2`: a byte, a
  type byte, and a far pointer to a descriptor.
  * Type 5 (0–130) is a digitised sample. The descriptor's word +7 is the
    `BVOC` number, which equals n (0 → 1). Byte +5 is an alias that FM
    devices use for `FVOC` sounds (`3348:02D2`).
  * Type 4 (131–180) are MIDI note effects for the music driver.
* 19 sample numbers have no `BVOC` in the data (15, 16, 19, 24, 27, 28, 30,
  41, 55, 61, 64, 65, 66, 87, 89, 105, 123, 127, 128).
* `BVOC` files are Creative VOC, all 8000 Hz unsigned 8-bit. BVOC 58 and 70
  repeat a 3.0 s and a 2.2 s sound 100 and 200 times.
* `SCMD` sounds in the data: 1, 2, 6, 8, 42, 45, 57 and 60. Almost all are on
  the records of door and lever sequences: slot 2 opening, slot 3 closing, or
  the header word 0x44 start that `REQUEST(5)` plays.

### Verb window (`68BD:0040`)

Right-clicking an object (kind 1 item or 2 creature) runs its look trigger
(`039D:02F3`), then works out verb bits: `68BD:0C6D` from the object, and
`039D:08AA` from its triggers (masked to 0x15). Bits: 0x01 get (an item
whose `IT1R` +8 lacks 0x20), 0x04 talk, 0x10 use (an item type with
`IT1R` +0x0F bit 1; for a door, `6BB8:0B33`, only this bit), 0x40
(`IT1R` +0x0F bit 8, `6527:002C`, not traced), 0x08 info (creatures; items
via `68BD:0B91`). One verb alone is done at once (get `7BEB`, talk
`039D:021F`, use `6BB8:0B56`); otherwise a window opens: `WIND` 15500 for
items, 3020 for creatures (`RESOURCE.GFF`). Each has three 15 × 15 buttons,
disabled unless their bit is set, and INFO:

| Window | Get | Talk | Use | Info |
|--------|-----|------|-----|------|
| 15500 | `BUTN` 15303 | 15301 | 15302 | 15304 |
| 3020 | 15308 | 15306 | 15307 | 15309 |

A `BUTN` whose +0x5A is its own number shows the `ICON` named at +0x64
(15101–15103 for items, 15105–15107 for creatures: mouth = talk, hand =
use, grasping hand = get; 15108/15109 INFO, text "INFO" at +0x6E). The
frame is `APFM` 15200.

### Character creation

The rules are tables in segment `38D4` (ability order Str, Dex, Con, Int,
Wis, Cha; races 1–8 and classes 1–8 in the string table's order), used by
`6CCA` and `6ECD`:

* **Racial adjustments**, `38D4:014D` + 6 × race: dwarf Str +1, Dex −1,
  Con +2, Cha −2; elf Dex +2, Con −2, Int +1, Wis −1; half-elf Dex +1,
  Con −1; half-giant Str +4, Dex −5, Con +2, Int −5, Wis −3, Cha −3;
  halfling Str −2, Dex +2, Con −1, Wis +2, Cha −1; mul Str +2, Con +1,
  Int −1, Cha −2; thri-kreen Dex +2, Int −1, Wis +1, Cha −2. All match the
  manual but the half-giant's (it has Str +4, Con +2, Int, Wis, Cha −2).
* **An ability** (`6CCA:16BD`): rolled as the best of four `6CCA:1671(4, 4)`
  (4d4) + 4 + the adjustment; at most 20 + the adjustment; at least
  `6ECD:1F19`'s minimum. A click changes it within the same limits.
* **The minimum** (`6ECD:1F19`), over the character's classes (record
  +0x21–0x23): 17 if the ability is the class's key one, else the class's
  number, from 3-byte entries at `38D4:0180` + 3 × class (word ability, byte
  number): cleric Wis 9, druid Wis 12, fighter Str 9, gladiator Str 13,
  preserver Int 9, psionicist Wis 12, ranger Wis 14, thief Dex 9. The
  numbers are the manual's key minimums (but the psionicist's, 15 there).
* **Hit points** (`6ECD:0156`): for each class, a die from `38D4:014A` +
  class (8, 8, 10, 10, 4, 6, 10, 6) per level (record +0x24–0x26): the range
  is levels to levels × die, doubled for half-giants, divided by the number
  of classes, plus Constitution's bonus once (`8E77:032C`).
* **A new character** starts as the template at `38D4:0048` (character
  record, 0x47 bytes: +0 experience 3000, +4 the most experience 3000, +0x10
  an id, +0x18 race, +0x1B the abilities) and `38D4:008F` (combat record,
  0x3A bytes: +0x22–0x27 the abilities). Finishing copies both into the
  tables (`6ECD:1644`, `DS:1661` + 0x47 × slot, `DS:1665` + 0x3A × slot),
  gives the record an unused id from −32767 up, sets the base armour class
  (+0x27: 10, thri-kreen 5) and the attacks (`8E77:038F`). Experience is
  per class (AWARD divides by the number of classes), so a new character
  has 3000 in each.
* **The map icon**: the party is put on the map (`6CCA:1046`) as `OJFF`
  combat record +0x18 + 300. `OJFF` 300–313 are the 14 race and gender
  figures, in the portraits' order (human male, human female, dwarf male …
  thri-kreen female), all walking with `SCMD` 174; half-elves (306, 307)
  share the humans' pictures (`BMP` 2095, 2099).
* **Humans** count as one class (`8E77:02E5`).
* `9106` is the View Character screen, `8F23` the character sheet, `91F7`
  the item window (`VIEWITEM`).

### Character screens

* Create Characters (`8015:00F9`) calls `9106:0622` with the character
  table (`DS:1661`): the View Character screen, `WIND` 11500 (backdrop
  `BMP` 11000; `BMP` *n* is generally the backdrop of `WIND` *n*): character
  boxes `BUTN` 11300–11303 (34 × 34, frame `ICON` 11100), leader and
  computer-control buttons 11309–11316, name bar 11318, text buttons
  11319–11320, the screen icons 10300 and 11304–11306, game menu 11308,
  return 10308.
* The Character Generation screen is `WIND` 3011: portrait area `BUTN` 2001
  (100 × 116), map icon 2027 (45 × 35), die 2010, class list 2002–2009
  (`ICON`s of the names, frames normal / chosen / unavailable), name `EBOX`
  4003, abilities 2012–2017, hit points 2011, alignment 2018, EXIT 2058,
  DONE 2000. `WIND` 3012 lists the psionic disciplines (`ICON` 2038–2041;
  2041, psychoportation, has a blank normal frame) with VIEW SPHERES 2046;
  `WIND` 3013 the spheres (2042–2045) with VIEW PSIONICS 2047.
* Body portraits are `BMP` 20000–20013, in the string table's race order
  with male then female (mul male only, thri-kreen female only); the die
  faces are `BMP` 20048–20058. Random names are `TEXT` 0–32 (male),
  100–118 (female) and 200–207 (thri-kreen).
* A `BUTN` whose `+0x5A` is its own number shows the `ICON` at `+0x64`; text
  buttons keep a placeholder at `+0x6E` ("XXXXX"). `APFM` is a frame: `+0x08`
  number, `+0x28`/`+0x2A` size, `+0x58` a style word; it holds no image.
* **APFM at run time** (`2AF5`, looked up by tag `0x4D465041`): placing one
  (`2AF5:0006`) sets its rectangle (+0x10–0x16, from the size) and its draw
  callback (+0x54). `2AF5:02F3` sets (1), clears (2) or zeroes (3) bits of
  the flag word +0x58. +0x5A, +0x5E and +0x62 are far pointers the screen
  sets (+0x62 a picture, `2AF5:02A6` / `036C`); +0x68/+0x6A an offset. So
  what a frame shows is the screen's code:
  * View Character (`WIND` 11500): 11201–11204 the portraits, 11205–11212 the
    leader and computer-control buttons' places, 11261–11266 three bars under
    each row of boxes (row r: 11261 + r hit points, 11263 + r psionic
    points, 11265 + r status), 11269 the panel, 11270 where the status line
    starts. The hover texts (`78A9:0400`): a box its member, or PRESS RIGHT
    MOUSE BUTTON when empty; the leader's and computer control's places
    INACTIVE CHARACTER for an empty box, else the leader and "COMPUTER
    CONTROL IS " texts; the bars from `DS:328E` (HIT POINTS: CURRENT/MAX,
    PSIONIC POINTS: CURRENT/MAX, CURRENT STATUS).
  * The inventory (`74F1:0000`): `BMP` 13000 frame 1 behind the name at
    (53, 3), frame 3, the status line's bar, at (46, 161). Under each box,
    the member's hit points "%d/%d" (`DS:0E5A`) centred on x 27 at the box's
    top + 33 (`6CCA:1A1B`), and its status word centred in x 1–51 at + 39
    (`78A9:1FBB`). Status words are at `DS:11DC`: New, Okay, Stunned, Out
    Cold, Dying, Dead, Animated, Petrified, Gone; for Okay, a current
    effect's name if there is one. The active member's text is palette
    colour 47, the others' 52, shadow 17 (`DS:326E`).
  * The container art is `BMP` 13003 (a bag) for a leather item and 13002
    (a chest) otherwise (`6527:12BD`: `IT1R` +8 & 0x0F == 5).
* DUAL (Create Characters' and View Character's box menu) is `WIND` 17502
  over `BMP` 17002: the eight class names (`BUTN` 17303–17310 in the class
  order cleric, druid, fighter, gladiator, preserver, psionicist, ranger,
  thief; `ICON` 17101–17108), EXIT 17302, and NO OTHER CLASSES AVAILABLE
  (`DS:3075`). The manual: humans only, third level at least, twice at most.
* The manual's pregenerated party is a saved game that came with the game
  ("A saved game containing a pre-built party"). The GOG install has none:
  `DARKRUN.GFF` is an empty template. So the engine's pre-made party is its
  own (`engine/game/party/premade.json`).
* The names of genders, races, classes, abilities and alignments are one
  string block at linear `0x52062`, with a far-pointer table to them at
  `0x51F5A`. The roster file is `CHARSAVE.GFF`.
* In View Character's panel (`APFM` 11269) the 35 slots 11213–11247 hold
  spells: three rows of seven above, two below. The status line's words
  include TOGGLE SPELLS & PSIONICS, SELECT SPELL LEVEL, SELECT PSIONIC
  DISCIPLINE, MEMORIZE SPELLS, "%d LEVEL %d SPELL%s TO CAST", ONLY ACTIVE CHAR
  CAN CAST, YOU NEED TO REST, CAN'T START THAT PSIONIC (`DS:3026`–`DS:3260`).
  Learning is `WIND` 17500 ("CHOOSE A SPELL", LEARN, EXIT; `8CCC:0211`).
  Picking a power is 17501 ("PICK A PSIONIC POWER", `8CCC`). 17502 lists
  the eight classes (17101–17108), probably a level-up choice. The leader
  and computer-control buttons say "MAKE %Fs LEADER", "%Fs IS LEADER",
  "COMPUTER CONTROL IS ON / OFF / LOCKED" (`DS:1AA3`–`DS:1B46`).
* The inventory (`WIND` 13500 over `BMP` 13001) has six more slots,
  11242–11247, below SELL (13304). With the store's messages (STORE, NO
  DEAL!, SOLD!, NOT ENOUGH MONEY, "I'll give you %u$", TAKE IT / LEAVE IT, I
  COULDN'T SELL THAT, `DS:2ED2`–`DS:2F34`) they are the store's selling
  slots; the store is `74F1:042B`.

### Spells (`DS:019C`)

* One 7-byte record per spell, 1–138: the level (+0); a caster mask (+1,
  three bytes and a zero); the name, a `DS` offset (+5). It is read as
  `*(byte*)(n·7 + 0x19C)` and `*(ulong*)(n·7 + 0x19D) & mask` (`6868:0487`,
  `612E:2574`, `6527:0459`). The file copy is at `DSUN_FLAT.EXE` offset
  `0x4D87C`.
* The mask has bit `code + 1` for each class code of `38D4:00ED`: 1–4
  cleric of air, earth, fire, water; 5–8 druid; 11 preserver; 13–16 ranger.
  Bits 0 and 19 come with every wizard spell, and 1 and 20 with every
  priest spell (likely defiler and templar).
* Wizard spells 1–69 have levels 1–5 (12, 13, 15, 16, 13 spells); priest
  spells 70–138 too. Most priest spells are everyone's (`0x13C3FE`). The
  ones with one element have its sphere only: Magical Stone and Conjure
  Lesser Earth Elemental are earth, Dust Devil air and earth, Flame Blade
  fire. At levels 4 and 5 the spells of no one sphere are the druids' alone
  (`0x1003C2`); clerics get their sphere's.
* A character's spells per level are kept at `DS:4B08` (wizard) and
  `DS:4B11` (priest), 0x1E bytes per character, indexed by level. These
  tables aren't traced. The engine's slots, spellbook sizes and psionic
  strength follow AD&D 2nd edition (`CharacterRules`). `38D4:013B` holds
  20, 22, … 34, likely a psionicist's strength points by level.

### Game menu and preferences

* GAME MENU is `WIND` 10500 over `BMP` 10000 ("FATAL: Error loading
  WIND_GAMEMENU"). Its status line names the buttons (`DS:0C4A`): the
  character screens (10300, 11304–11306), EXIT TO DOS (10301: "EXIT: SAVE
  GAME?" SAVE / QUIT / CANCEL), LOAD/SAVE GAME (10302: LOAD/RESTART GAME),
  SET PREFERENCES (10303), OVERHEAD MAP (10305), CENTER ON LEADER (10306),
  MOVE, LOOK and ATTACK CURSOR (10310–10312), COLLAPSE PARTY (10313), and
  RETURN TO GAME (10308). END TURN belongs to combat.
* Preferences are `WIND` 16500 over the same frame (`870B:0708` names the
  parts by where the pointer is): MUSIC TOGGLE ON/OFF (16300), MUSIC VOLUME
  (arrows 16304 / 16305), SOUND EFFECTS ON/OFF (16301) and SOUND EFFECT
  VOLUME (16306 / 16307), GAME DIFFICULTY (16308 / 16309: EASY, BALANCED,
  HARD, HIDEOUS), ANIMATIONS ON/OFF (16303), ABOUT (16302), GAME MENU
  (11308), RETURN TO GAME (10308).
* Questions use `WIND` 14000 over `BMP` 14000: a title (14004), three
  choices (14001–14003) and return (10308).

### Doors

* **What a door is** (`6BB8:0B33`): an item object with an `OJFF` number in
  0x46A–0x49C or 0x803–0x898 (166 in `SEGOBJEX.GFF`, 123 placed in 9
  regions). The other leaf of a double door is the `OJFF` number one up (odd)
  or one down (even).
* **State:** the item record's byte +0x0F. 0x01 closed, 0x02 locked, 0x04
  stuck, 0x08 secret. In the data, 48 doors start closed, 38 closed and
  locked, 72 closed and stuck, 6 open and stuck, and 2 open.
* **Size:** byte +0x0E. 0–9 is that many extra cells east; 10 and more is
  n − 10 extra cells south.
* **Animations:** `SCMD` slot 1 is closed, 2 opening (ends open), 3 closing.
  The header slot k is word k.
* **Use** (`6BB8:0B56`, called for verb 0x10 from `68BD`):
  * A door is set to the opposite of its closed bit with `6BB8:0D13(object,
    state, 0)`. The object's use trigger runs next (`039D:0319`).
  * If the door didn't move: with 0x08 set it says "THERE'S A SECRET DOOR!"
    and clears 0x08; with 0x02 "THE DOOR IS LOCKED"; when closed "THE DOOR
    WON'T OPEN".
  * A non-door whose item type has flag 1 (`IT1R` +0x0F) toggles object flag
    0x40 and plays the header word 0x44 start, as `REQUEST(5)`.
* **`6BB8:0D13(object, state, force)`:**
  * It does nothing when 0x0C is set, unless forced. So a locked (0x02)
    door does open when used, as far as this code goes.
  * State 0 opens it and clears 0x03. A state with 0x02 sets 0x03. Otherwise
    it sets 0x01. Both leaves get the same flags.
  * When the closed bit changes it plays slot 2 (state 0) or 3, and updates
    the map with `6BB8:0FF8`.
  * `REQUEST(2, door, state)` calls it forced: 98 opens and 22 closes in
    the scripts.
* **`6BB8:0FF8`:**
  * Closed sets `GMAP` 0xC0 on the door's cells, from its position ÷ 16;
    open clears them.
  * When opening, the first cell is blocked again if the cell before it
    (west, or north) is free or the door is longer than one cell. That is
    its hinge.
  * The map files block only those hinge cells, so a door's state comes
    from its record when the region loads.
* **Loading a region:** a door with 0x02 also gets 0x01. Its cells are set
  as above and its animation starts at slot 1 (closed) or 2 (open).
* **`CREATE_DOOR_TILE/BOX_TRIGGER`:** nothing in DSUN.EXE reads these lists.


## 8. Combat

Combat lives in `612E` (rounds, attacks, damage), `80F5` (effects,
modifiers, saving throws), `6698` (psionics), `6868` (ability tables),
`8E77` (levels, THAC0, attacks, saves) and `7D80` (spell effects). Every roll
goes through `rand` (`0000:0822`). `1A0A:2FA9(lo, hi)` is a number in
lo–hi; `6CCA:1671` takes the same pair packed in a long (0x140001: 1–20). Dice
are `612E:1F92(n, sides, plus)`.

Records: a combatant is an object of kind 2 (`DS:0C36 + 3·index`) with a
0x3A-byte combat record (`DS:1665`) and a 0x47-byte character record
(`DS:1661`, from combat record +4). The fields used here:

* **Combat record:** +0 hit points (from the character record's +8 when
  loaded), +2 psionic strength points, +4 the character record, +6 the id
  (−`RDFF`; for |id| 1–50 the status is copied to `DS:0333 + |id|` when it
  changes), +0x16 special abilities (see "The AI"), +0x1B Move, +0x1C
  status, +0x1D allegiance (& 3: fighting), +0x1E mind (see "The AI"),
  +0x1F THAC0, +0x21 flags (0x20 computer control, 0x40 locked), +0x22 the
  six abilities (Str, Dex, Con, Int, Wis, Cha; `6868:04FC(object, n)`, 9
  for a non-creature), +0x28 the name (18 bytes), +0x38 facing (0–7, at run
  time).
* **Character record:** +0x12 class bits (as items', `6868:0000`), +0x18 race
  (1 human, 2 dwarf, 3 elf, 4 half-elf, 5 half-giant, 6 halfling, 7 mul,
  8 thri-kreen), +0x21–0x23 class codes and +0x24–0x26 their levels
  (`6868:00AF(object, code)` is the level in a code), +0x27 base armour
  class, +0x2A attacks per round in halves, +0x2B–0x2D, +0x2E–0x30,
  +0x31–0x33, +0x34–0x36 up to three natural attacks (attacks in halves,
  dice, sides, bonus, attack i at + i; `80F5:2ACC`; Body Weaponry makes
  the first 2d4, Animal Affinity at least 1d10), +0x37–0x3B the five
  saving throws. Also +0 experience (int32; creatures mostly 1000), +4 the
  experience a kill is worth (int32, AD&D's by hit dice: 35, 65, 120, 175,
  270, 420, 650, 975, 1400 …), +8 hit points, +0x3D size, +0x3E a creature's
  spell set (`3FB9:0110 + 14·set`, 7 words, spell − 1, −1 none;
  `6798:032B`): 1 Magic Missile, Fireball, Acid Arrow, Ice Storm, Cone of
  Cold; 2 Cause Light Wounds, Hold Person, Poison, Slay Living, Cause
  Disease; 3 Cause Light Wounds, Hold Person; 4 those, Dehydrate, Cause
  Disease, Flame Strike; 5 Magic Missile, Stinking Cloud, Lightning Bolt,
  Turn Pebble to Boulder; 6 Cause Fear, Hold Person, Cause Blindness; 7 and
  8 those and Blood Flow or Dehydrate. +0x46 its psionic set (see "The
  AI"; 1–9 are filled: 2 Ballistic Attack, Disintegrate, Psionic Blast,
  Detonate, Psychic Crush; 4 the attack modes; 9 Psychic Crush only; …).
  Also +0x1A alignment (1–9; 3, 6 and 9 are evil to `80F5:377E`) and
  +0x3C the allegiance a charm's end restores (`8053:066F`).
* **A creature's casts** (`6798:0000`, `6798:0059`): counted the first time
  its spells are asked for and kept in a 28-row table (10 bytes: +0 object,
  +2–8 one byte per set spell, low nibble "has some", high nibble the count,
  +9 age; `6798:0366` gives a spell's byte, party members use
  `DS:0168 + 0x8A·member`). For spell levels 1–9 it adds up its classes'
  slots (`6798:071C`; a human's later classes only while below its first's
  level), scales them by current ÷ record hit points (×256 >> 8), then deals
  one cast at a time to the set's spells in order, round and round, while
  their level has any left. The slots (`6798:0B32`): `DS:0664 + class code`
  holds up to two row numbers (nibbles), each a word at `DS:0678 + 2·row`
  (bits 8–11 max, 4–7 offset, 0 parity); a row gives v − (v + parity) / 2 −
  (spell level + offset − 1), 2 instead of 1 when v's oddness equals the
  parity, clamped 0 … max, with v the class level for the first row and
  Wisdom for the second. Codes 1–8 (clerics, druids): rows 3 (max 9) and 4
  (Wisdom, offset 6, max 3); 11, 18, 19 (preserver, defiler, templar): row 1
  (max 5); 13–16 (rangers): row 2 (offset 3, parity 1, max 3). A level 5
  preserver gets 3, 2 and 1 casts of levels 1–3; at half its hit points 1
  and 1. `CombatRules.SpellSlots` has the rows.

### Rounds, initiative and turn order

* **The loop** (`612E:04A7`): `612E:09B6` picks the next combatant; when
  none is left, `612E:0754` starts a round, and when it returns 0 the fight
  is over (`612E:281F` finds no enemies; experience follows, `8E77:0A8F`).
  The combatant first needs one of the 48 active slots (`1A0A:273E`, see
  "Active slots"); if none is free it leaves the round (`612E:2596(it,
  0)`). A party member (index below 4) becomes the active member
  (`3781:0369`, `1587:3866`). Then a combatant under computer control
  (combat record +0x21 & 0x20) runs the AI, `612E:0BAD` (see "The AI"),
  and `612E:1AE1` follows; any other returns 2 and waits for the player.
* **A new round** (`612E:0754`): the round clock (`DS:035B` in segment 4781)
  goes up by 60. Every creature with status 1 (Okay) and an allegiance that
  fights (& 3), the party or those `612E:0000` finds involved, rolls:
  * initiative = 20 + d10 (0–9) + Dexterity's reaction adjustment
    (`6868:033F`) + `80F5:3811` (effects: −2, +2, −2), at `DS:00D9 + 4·n`;
  * a tie-breaker 0–199 at `DS:00DB + 4·n`;
  * movement points = Move (+0x1B) × 10, at `DS:022B + 2·n` of segment 47BD;
  * then `80F5:36CC`'s speed state, from the conditions (see
    "Conditions"), first match: Paralyzed (34) 0, Stuck (51) 5, Slowed
    (47) 1, Hasted (22) 4, else 2. 0 and 5 set the points to 0, 1 halves
    them (rounding toward 0), 4 doubles them. The jump table (`612E:09AA`)
    also has code that drops the creature from the round (initiative −1),
    but no case reaches it.
  The action flags (`DS:01D5 + 2·n`) start at 0x3FF: bit 0 may move, bit 1
  may attack. Has-target (`37BD:0085`) and `37BD:005B` are reset to −1,
  `37BD:0181` to 0.
* **Turn order** (`612E:09B6`): the highest initiative acts first, a tie
  going to the higher tie-breaker. The original's order is the reverse of
  AD&D's low-goes-first: a high Dexterity acts early.
* **Movement** (`612E:2446`): a step costs the points at `DS:055C` by
  direction, 10 straight and 14 diagonal; below 10 left, the move bit
  clears. A target farther than 24 (`1A0A:2BA5`) can't be attacked.

### Attacks: to-hit and damage

* **Who hits** (`612E:1359`, for each weapon in use, up to three):
  * the target number is THAC0 (combat record +0x1F), less the attacker's
    modifiers, less the target's armour class;
  * from behind (the target's facing is opposite, `612E:1B5E(dir, 4)`):
    −2 to the target number, and shields don't count;
  * a thief (class bit 0x400) in melee with a weapon lighter than 41
    (`IT1R` +4) backstabs: −4 instead, and damage × `612E:2904` = (thief
    level − 1) / 4 + 2, at most 5 (×2 at levels 1–4 … ×5 from 13);
  * monsters (index 4 and up) add the difficulty − 1 (`DS:11AE`, 0–3) to
    their bonus;
  * the attacker's bonus is the weapon's item bonus (+0x14), plus
    `6868:0411` (non-metal weapons that aren't magic and have no bonus:
    wooden −3, bone −1, stone −2, obsidian −2), plus `80F5:3849` (effects,
    alignment, a halfling's +1 with a sling or thrown weapon, Strength's
    to-hit in melee or Dexterity's reaction at range), less a second
    weapon's penalty (`612E:292C`: the Dexterity reaction, not for rangers).
* **The roll** (`612E:1B7A`): d20 per attack. A 20 always hits, a 1 always
  misses, otherwise it hits when d20 ≥ the target number.
* **Attacks per round:** +0x2A holds halves: 2, then 3 from warrior level 1,
  4 from 7, 5 from 13 (`8E77:038F`; thri-kreen +0x2B 8 natural). Hasted
  doubles, slowed halves; a round gets (halves + (round & 1)) / 2, so 3
  halves alternate 1 and 2. Missile weapons are limited by the ammunition
  in slot 1 (`612E:1FCF`, message 0x5B4 when it runs out); a missile's
  range is `IT1R` +0x0A.
* **Damage:** `IT1R` dice (+0x0D) × sides (+0x0C) + damage bonus (+0x0E) +
  the item's bonus (+0x14) + `6868:0489` (wooden −2, bone, stone, obsidian
  −1) + ammunition's bonus; at least 1. In melee `80F5:3671` adds
  Strength's damage (`6868:030D`) and an effect's 10 % per level. With no
  weapon, the natural attacks (`80F5:2ACC`). Damage is applied by
  `80F5:0008` / `0079` and `612E:18DA` (see "Items in scripts", DAMAGE).

### Armour class (`612E:2113`)

* An item object is AC 5; a creature starts from its character record's
  +0x27 (10 for the party).
* Each worn armour piece (`IT1R` flags 0x80) subtracts its AC (`IT1R`
  +0x12) and its bonus (+0x14); shields (`IT1R` +0 & 4) not from behind.
  Pieces on the arms, legs and chest (slots 0, 6, 9) make the body armour,
  which effects can cap; the rest (shield, helm, …) count on top.
* A gladiator (class bit 0x40) gets −1 per 5 gladiator levels.
* `80F5:39B7`: Dexterity's adjustment (`6868:0358`, not from behind), and
  the effects: Armor caps the body armour at 6, Shield at 4, Spirit Armor at
  6 − level/4, others at 10 − level; some subtract 2.

### Ability tables (`DS:07A8`–`DS:0843`, 26 bytes each, by score 0–25)

| Table | Getter | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8–14 | 15 | 16 | 17 | 18 | 19 | 20 | 21 | 22 | 23 | 24 | 25 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Str damage `07A8` | `6868:030D` | −4 | −2 | −1 | −1 | −1 | 0 | 0 | 0 | 0 | +1 | +1 | +4 | +7 | +8 | +9 | +10 | +11 | +12 | +14 |
| Str to-hit `07C2` | `6868:0326` | −5 | −3 | −3 | −2 | −2 | −1 | −1 | 0 | 0 | 0 | +1 | +2 | +3 | +3 | +4 | +4 | +5 | +6 | +7 |
| Dex reaction `07DC` | `6868:033F` | −6 | −4 | −3 | −2 | −1 | 0 | 0 | 0 | 0 | +1 | +2 | +2 | +3 | +3 | +4 | +4 | +4 | +5 | +5 |
| Dex AC `07F6` | `6868:0358` | +5 | +5 | +4 | +3 | +2 | +1 | 0 | 0 | −1 | −2 | −3 | −4 | −4 | −4 | −5 | −5 | −5 | −6 | −6 |
| Con vs poison `0810` | `6868:0371` | −2 | −1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | +1 | +1 | +2 | +2 | +3 | +3 | +4 |
| Wis magic defence `082A` | `6868:038A` | −6 | −4 | −3 | −2 | −1 | −1 | −1 | 0 | +1 | +2 | +3 | +4 | +4 | +4 | +4 | +4 | +4 | +4 | +4 |

These are AD&D 2nd edition's, but 18 has no percentile strength (+2 to hit,
+4 damage).

### Classes: groups, THAC0, hit dice and saving throws (segment `40B1`)

* `40B1:0010 + class code` gives the group: codes 1–8 (clerics, druids) and
  19 priest (0); 9, 10, 13–16 (fighter, gladiator, ranger) warrior (1); 11,
  18 (preserver, defiler) wizard (2); 12, 17 (psionicist, thief) rogue (3).
* `40B1:0000 + 4·group`: hit die, the last level that adds one, hit points
  a level after it, THAC0 factor. Priest d8, 9, +2, 8; warrior d10, 9, +3,
  12; wizard d4, 10, +1, 4; rogue d6, 9, +2, 6. `8E77:0473` takes the best
  level in each group (a dual-class human's former class only while the
  current one's level is lower; `+0x18` = 1).
* **THAC0** (`8E77:0416`): 20 − the best of (level − 1) × factor / 12 over
  the groups: 1 a level for warriors, 2 in 3 for priests, 1 in 2 for rogues,
  1 in 3 for wizards.
* **Saving throws** (`8E77:059B`): five, in AD&D's order (paralysation,
  poison and death; rod, staff and wand; petrification and polymorph;
  breath weapon; spell), at character record +0x37–0x3B. For each group the
  character has and each save, the save is base − min(max, rate × (level −
  1) / 100) from `40B1:006C + 15·group + 3·save` (base, rate, max); the
  lowest over the groups is kept. Dwarves and halflings (race 2, 6) get
  Con × 2 / 7 off, but the test (`8E77:064D`) is on the *group* counter
  (`cx` = 1 or 4), so only the warrior group's saves get it, all five; it
  was likely meant for saves 1 and 4 (rod and spell), as AD&D has it. The
  bases (level 1):

  | Group | Para./poison | Rod/wand | Petri./poly. | Breath | Spell | Rates (×100) | Max |
  |---|---|---|---|---|---|---|---|
  | Priest | 10 | 14 | 13 | 16 | 15 | 45 | 2, 6, 5, 8, 7 |
  | Warrior | 14 | 16 | 15 | 17 | 17 | 69 (breath 82) | 3, 5, 4, 4, 6 |
  | Wizard | 14 | 11 | 13 | 15 | 12 | 30, 40, 40, 40, 40 | 8, 3, 5, 7, 4 |
  | Rogue | 13 | 14 | 12 | 16 | 15 | 25, 50, 25, 25, 50 | 8, 4, 7, 11, 5 |

  The engine keeps the THAC0 factors and these rows per class in
  `rules/characters.json` (`ClassRules.Thac0Factor`, `Saves`), the Con
  saves per race (`RaceRules.ConstitutionSaves`, warriors only unless
  `ConstitutionSavesWarriorsOnly` is false).

* **The roll** (`80F5:0ED1`): only for an effect whose record allows one
  (save byte bit 0). Category = save byte >> 5; 6 means none; the save
  score is +0x36 + `DS:1E75`[category] (words 1, 1, 1, 2, 3, 3, 4, 5, 5, 5:
  most spells, category 5, end up at the third save; *unverified* that
  this is meant). Not for a target in status 3 or with effect bit 4. A
  natural 1 fails, a 20 succeeds; otherwise d20 (doubled for element flags
  0x86) + `80F5:119C` + the spell's own modifier (save byte bits 1–4,
  signed) must reach the score. The save effect 0x86 doubles is fire, cold
  and electricity. **`80F5:119C`** adds: −1 Save Penalty (45); +3 Spirit
  Armor (56) except on the first save (paralysation and poison), +1
  Barkskin (57), +1 Bless (7); +2 Protection from Evil (38) against an evil
  caster (`80F5:377E`: alignment 3, 6, 9); +4 Protection from Lightning
  (41), +3 from Cold (36), +3 from Fire (40) against their element; Prayer
  (73; `7D80:191B`) +1 when its priest shares the target's allegiance, −1
  when not; +4 against an effect of area below 9 from a caster who can't see
  the target (`80F5:31AB`); +4 Brave (9) against flag 0x100, spending a
  charge; +2 for a druid (codes 5–8, `80F5:339B`) against fire or
  electricity; +2 for a psionicist (code 12) against flags 6; −Dexterity's
  defensive adjustment (`6868:0358`) against flag 0x40; +Wisdom's magical
  defence (`6868:038A`) against flags 0x1E; on the first save
  Constitution's poison adjustment (`6868:0371`) and a dwarf's or halfling's
  Con × 2 ÷ 7. Then by effect: Chaos (57) −100 without fighter, gladiator or
  ranger levels (`80F5:3589`), Dismissal (61) + the target's level − the
  caster's, Cloudkill (58) −100 below level 7, Scare (22) −100 below level 6
  and +100 from 6. No shipped record sets flags 0x40, 0x80 or 0x100, so the
  Dexterity, gaze and Brave cases never come up.

### Spells in combat (`3CB4:0037 + 0x20 · (spell − 1)`)

* A 32-byte record per spell (index = `SPIN` number − 1; checked against
  the saves of 18 spells; 196 records, the monster effects up to 196). The
  engine ships them as `DarkSun.Game.Combat.SpellRules` (`rules/spells.json`).
  The fields the code reads:

  | Offset | Read as | Meaning (examples) |
  |---|---|---|
  | −0x06 | word | range (`8BEB:07F3`): (this + −0x04 × level) × 4, in sixteenths of a cell; −1 touch (8), −2 a short reach (28). Magic Missile 720 + 120 a level, Fireball 120 + 80 |
  | −0x04 | byte | range per level |
  | −0x03 | byte | duration dice: low 4 bits count, high 4 sides (Color Spray 0x42: 2d4; Haste 0x13: 3) |
  | −0x02 | word | duration per level (Hold Person 2, Shield 5) |
  | +0x00 | word | duration unit: 60 (a round) for most, 600 a turn, 3600 an hour, −9999 permanent (Charm Person, Thought Shield), 0 instant (Magic Missile), −1 **charges**: the duration is a count of uses, not time (see "Charges") |
  | +0x02 | word | area size (`8BEB:086F`): this + +0x04 × level, doubled for a circle template; 1, 40 (Burning Hands, Lightning Bolt), 80 (Fireball), 240 (Confusion). Cones use it as their range |
  | +0x04 | byte | area size per level (Cone of Cold 40) |
  | +0x05 | byte | target: 1 an area, 3 touch, 4 a creature, 6 a cone, 7 self |
  | +0x09 | byte | projectile (Magic Missile 4, Fireball 1, Lightning 5); 0xFF none |
  | +0x0A | word | flags: 0x0001 summons, 0x0002 mind, 0x0004 charm or hold (`80F5:2500`), 0x0008 illusion (`2540`), 0x0010 fear and the psionic attacks; 0x26 is "mind" to `80F5:2580` (Wisdom immunity, Mind Bar, the undead), 0x1E adds Wisdom to the save (`2560`), 0x06 a psionicist's +2 (`2520`); 0x0040 Dexterity on the save (`25C7`), 0x0080 a gaze (`25E7`), 0x0100 Brave's +4 (`25A0`), none of them set; 0x0200 combat only, 0x4000 damaging, 0x8000 no damage if saved (else half) |
  | +0x0D | word | effect graphic (0x20xx), 0xFFFF none |
  | +0x10 | byte | area template (signed, −1 none): `3C81:0000 + 0x12 ·` it (see "Areas") |
  | +0x12 | byte | condition on a failed save: Armor 2, Charm 10, Confusion 11, Ego Whip 33, Hold 34, Shield 46 |
  | +0x13 | word | element bits for resistances (see "Elements"): poison 0x1, fire 0x2, cold 0x4, acid 0x40, electricity 0x80, negative 0x100, magic 0x200, psionic 0x400 |
  | +0x15 | byte | damage: bits 5–7 dice per step, bits 0–4 flat damage per step |
  | +0x16 | byte | damage: bits 3–7 base dice, bits 0–2 levels per step (0 is read as 1, and written back) |
  | +0x17 | byte | damage: bits 0–3 die sides, bits 4–7 (signed) levels added before dividing |
  | +0x18 | byte | save: bit 0 allowed, bits 1–4 modifier (Confusion −2), bits 5–7 category |

* **Applying one** (`80F5:0079`, queued by `80F5:0532` / `057E`, run by
  `80F5:045A`): immunity (`80F5:14E5`), whether it affects the target
  (`80F5:1999`), magic resistance (`80F5:161F`: d100 against `80F5:1901`),
  then the save: a failed save applies the condition (+0x12) for the duration
  from `7D80:1BD3(spell, level)` (`7D80:1301`); a successful one halves the
  damage, or cancels it with flag 0x8000. The damage then passes
  `80F5:1059` (−2 against some armour, ±1 by an ally's alignment, AC for
  spell 0x8C) and is applied (`80F5:0E61`).
* **Casting** (`7D80:003B`): with a template that isn't a burst, the area
  becomes a field (`7D80:05C7`, see "Fields") and nothing is applied now;
  otherwise every combatant `8BEB:0000` finds (a given target object alone)
  gets `80F5:0079`. A damaging effect from a party member makes a non-party
  combatant it reaches allegiance 2. Then Dispel Magic (records 26, 98),
  effect 195 and any fire (+0x13 & 2) remove the field objects in the same
  area (`8BEB:0000` with its last argument 1, `7D80:2368`/`2485`/`269C`;
  fire only those with template +2 & 0x40, `2267:051C`).
* Casting needs the caster active ("ONLY ACTIVE CHAR CAN CAST"); being hit
  interrupts it ("YOU WERE HIT"). The memorized spells per level are
  `DS:4B08` (wizard) and `DS:4B11` (priest), 0x1E bytes per character.
* The record before spell 1 (−0x03 to −0x01 of record 0, `3CB4:0034`) is
  read too, so the records really start 3 bytes earlier; the offsets here
  are kept as they were.
* **Damage** (`7D80:1CC2(spell, level, &max)`): level at most 10; steps =
  (level + (+0x17 >> 4)) / (+0x16 & 7), at least 1; dice = (+0x16 >> 3) +
  (+0x15 >> 5) × steps of (+0x17 & 0xF) sides (`6CCA:1671(n, sides)`, n
  dice); plus (+0x15 & 0x1F) × steps; max is the same with every die at
  its highest. Fireball and Lightning Bolt (32, 1, 6): level d6 up to
  10d6. Magic Missile (33, 2, 0x14): (level + 1) / 2 × (1d4 + 1). Burning
  Hands (2, 9, 3): 1d3 + 2 a level. Flame Arrow (160, 5, 6): 5d6 per 5
  levels. Cause Light Wounds (0, 9, 8): 1d8.
* **Duration** (`7D80:1BD3(spell, level)`, level 0 counts as 1): −9999 when
  the unit is −9999; otherwise unit × (dice (−0x03, `roll_dice`) + per
  level (−0x02) × (level + (+0x17 >> 4)) / (+0x16 & 7)), at most 32767.
  Hold Person: 60 × 2 × level (2 rounds a level); Confusion: 60 × (2 +
  level); Haste: 60 × (3 + level).
* **Charges** (unit −1): the duration formula then gives a negative
  number, and adding the effect (`7D80:1362`) stores its negation as a count
  at effect +8 instead of setting a timer; −9999 sets neither. `7D80:1723(object,
  condition)` takes one from the count (`8053:08BB`, always 1) and removes the
  effect at 0. What spends one: being damaged, Armor (condition 2,
  `80F5:0E61`; 8 + level hits); attacking or casting at someone,
  Invisibility and Invisibility to Undead (23, 24; `80F5:0008`); each round,
  Acid (1, also 2d4 damage) and Poisoned (35, which kills, 1000 damage, when
  the count runs out; `8053:0163`); and the defences as they turn something
  aside, Mirror Image (25, 1d4 + level / 3), Stoneskin (48, 1d4 + level / 2),
  Ironskin (53, 1d6), Minor Spell Turning (44), Negative Plane Protection
  (42), Cloak of Fear and Bravery (18, 9; `80F5:119C`, `1999`, `161F`).
  Poison and Cloudkill have 10, the rest 1.
* **Elements** (spell +0x13; a weapon's `IT1R` +2): poison 0x1, fire 0x2,
  cold 0x4, bludgeoning 0x8 (slings, Ice Storm, Magical Stone), slashing
  0x10, piercing 0x20 (bows, daggers, Flame Arrow), acid 0x40, electricity
  0x80, negative 0x100 (the cause-wounds spells, Chill and Vampiric Touch,
  Slay Living), magic 0x200 (most damaging spells), psionic 0x400, death
  0x800 (Poison, Slay Living); a weapon's bonus adds 0x1000 (+1), 0x3000
  (+2) or 0xB000 (+3) (`612E:1359`). Named from the spells and weapons that
  carry them. A creature's resistances (`80F5:28F2`) are the 4-entry row
  `3E45:00D2 + 12·row`, row = `3E45:00A8`[type] (type = combat record
  +0x16, 42 types, 16 rows): entry i is an element mask (word +2i) and a
  percent (byte +8 + i). The percent of the damage taken is the highest
  among the entries sharing a bit with its elements, else 100; then the
  target's protection conditions halve it, and some make it 0. `80F5:0079`
  multiplies the damage by it ÷ 100; at 0 `161F` stops the effect too
  (the ssurrans' 50 % against fire is the manual's "half damage from fire"). Most rows
  are AD&D's "hit only by magic weapons": babau, shadows and lesser
  elementals take physical elements at 0 % and anything with magic or +1 at
  100 %; elementals and vrocks need +2. Spiders ignore poison, slimes all but
  slashing, the animated statue and the xorn fire and cold.

### Areas (`8BEB:0000`, template `3C81:0000 + 0x12·n`)

Coordinates are sixteenths of a cell (object x, y; cell = coordinate ÷ 16),
distances `1A0A:2DE6` (`0898:000C` on the differences, scaled to ≤ 127
and back), directions `69CB:09D8` (0–255, 0 north, clockwise; `69CB:0B3A`
the vector back, `69CB:08AA` a rotation). The template:

| Byte | Meaning |
|---|---|
| +0 | word: the record index of the effect a field gives |
| +2 | low nibble the kind (1 a burst; 0 fog, 2 ground, 3 cloud stay as fields), 0x10 hides (cells get map flag 0x80, `142F:01B1`), 0x20 blocks (map flag 0x40, `142F:0182`), 0x40 burns (fire clears it) |
| +5 | bits 2–3 the shape: 0 a line or wall, 1 a circle, 2 a cone |
| +6 | high nibble: sixteenths of a turn added to a line's direction (4 for the walls) |
| +7, +8 | signed width and length factors (`8BEB:0393`); +9 bits 4–7 offsets (all 1: none) |
| +0xA, +0xB, +0xC | a field's time: +0xC × (dice (+0xA >> 2) of sides (+0xB & 0xF) + (+0xB >> 4) × level) seconds; +0xC 30000 forever (`7D80:05C7`) |

* **Size** (`8BEB:092D`): for target 2 (the walls) the distance between the
  two points the player gives (the middle is between them, the direction
  from one to the other); otherwise the area size (`086F`: +2 + +4 ×
  level, doubled for a circle), at least 1; targets 3, 4, 5, 7, 8 use 8.
  The cast's direction is from the caster to the point; a cone starts 4
  sixteenths out along it. The AI casts with both points on its chosen cell
  (`6466:0000`), so its walls are a single point.
* **Width and length** (`0393`) for size s: with +7 > 0, width s and length
  +8 × s ÷ +7, or −(+8) when +8 is negative; with +7 < 0 (none), −(+7) and s.
* **Circle** (`612E:2DCC`): within s ÷ 2 of the point. Fireball (80, so
  160): 5 cells.
* **Cone** (`612E:2982`): within s ÷ 2 of the apex and inside the angle
  from direction − h to direction + h, h = `09D8(0, 0, width ÷ 2, −length)`;
  never the caster. Burning Hands (40, template 5: 10, 15) reaches 20
  sixteenths, about 18° either side: the cell in front.
* **Line** (`612E:2A99`): a box around the middle, length ÷ 2 either way
  along the direction (+ the template's turn) and width ÷ 2 across.
  Lightning Bolt (40, template 13: 2, 8): 10 cells along the line of fire,
  centred on the point, 2.5 across. A wall (template 28: 2, −8) is 8
  sixteenths thick and as long as its ends are apart.
* Everyone of the allegiance mask (`8BEB:0B43`; the cast uses all) in the
  shape and in sight of its centre (or apex) counts, up to 64; `8BEB:02DA`
  then drops those `80F5:1CF7` filters out. The AI's estimate (`034D`) uses
  the same shapes.

### Fields (`7D80:05C7`, `7D80:0C6E`)

A field is a row of map objects (`69CB:0000` lays the shape out, `078A`
places one where the cell is open and in sight of the centre) whose object
byte `+0x66A2` is template + 1 and `+0x66A8` the level (bits 0–4) and a
value (bits 5–7); it goes when its time is up (`7D80:2782`). Who stands in
which field is a list at `DS:000A` (3 bytes: object, template; up to 84).

* `7D80:09EA`, as it is made: everyone in it gets its effect and is listed.
* `7D80:0C6E(object, tick)`, after a move (tick 0: `2267:04D7`,
  `217D:00E2`, `1DF3:055A`) and at a turn's start (tick 1: `8053:00EB`):
  entering gives the effect (`7D80:0F9D` mode 0, `1095` →
  `80F5:0008`); staying (mode 1) gives it again, on a move for ground
  (kind 2) and on a turn's start for a cloud (3); leaving (mode 2,
  `7D80:0ED9` → `113A`) ends the entries of that effect on the object. Fog
  (kind 0) does nothing to those in it.
* A removed field's occupants are checked again and so leave it.

### Defences (`80F5:14E5`, `80F5:1999`, `80F5:161F`)

* `14E5`, before anything: Protection from Missiles (28) stops missile
  attacks (and effects saved in category 4, `324C`), and a thri-kreen
  above level 6 dodges a missile when 1d20 > 8 (`1E5D`); Minor Globe (26)
  stops spells of level 1–3 (`31FC`) and Disintegrate (record 0x8B);
  Shield (46) stops Magic Missile (`DS:1607`, 6 entries; the other five do
  nothing). Blink (5) stops blows and small effects (area < 9) once its
  holder has acted this round: its initiative (`37BD:00D9 + 4·slot`, −1
  when its turn is over) is −1.
* `1999`: Armor (2) spends a charge and a Cloak of Fear (18) gives the
  attacker Cause Fear (record 0x4E) at level 6, for any effect (the port: hostile ones). Then
  for a blow (no spell), an area over 8 or a power: the on-hit abilities;
  Negative Plane Protection (42) against an undead's effect with element
  0x100 spends a charge and, if the target saves as against record 0x27
  (Vampiric Touch, which allows no save), stops it and queues 2d6 magic on
  the undead (`057E`) — so in the original it never stops anything; and a
  Dispelling Evil (52) attacker kills a race-14 target. For any other
  spell: a mirror image (25) takes it 75 in 100; Gaze Reflection (43)
  turns a gaze (flag 0x80) back; Minor Spell Turning (44) turns a level 1–3
  spell with no template back, spending a charge. Blows never meet mirror
  images (`612E:1B7A` passes no spell).
* `161F`: Stoneskin (48) spends a charge on anything with elements and
  stops a physical one; Ironskin (53) stops and is spent by a physical one;
  a cleric of fire (code 3) from level 5 is untouched by fire; the
  resistance percent at 0; magic resistance (spells below 0x8A); Free
  Action (21) and Protection from Paralysis (29) against effects whose
  condition is Paralyzed or Slowed; Wisdom and Intelligence 19+ against
  mind effects and illusions of level ≤ score − 18 (`3C8E`: the monster
  effects are level 3); the undead against mind effects and elements 0x100
  and 0x1; the ability bits; Web (record 0x18) against type 12; Graft
  Weapon (0x9B) when grafted (49); effect 196 against a carrier of item
  0x7545 (`RDFF` 30021).
* **Whom some effects take** (`80F5:1CF7`, applied by `8BEB:02DA` to every
  target list; 14 entries at `80F5:1DCF`): Charm Person and both Hold
  Persons a person (race 1–7, `80F5:2447`), Charm Person or Mammal also
  race 12 (`23FE`); Hold Undead and Turn Undead the undead; Dismissal and
  Abjure race 14; Haste the caster's allegiance; Slow, Curse and Minor
  Malison any other; Bless and Aid the caster's allegiance with no enemy
  within 28 sixteenths (`80F5:249D`, which by a slip measures from the
  object's (x, x)).
* **To hit** (`80F5:3849(target, attacker, items, distance)`): Bless +1,
  Curse −1, Slow −4, Graft Weapon +1 for the attacker, Prayer ±1 as for
  saves, −2 against Blur (55), +1 for a halfling (race 6) with a weapon at
  a distance over 28 cells (beyond sight, so never); then Strength within
  1 cell, Dexterity beyond.
* **A blow's elements** (`612E:1359`): a weapon's `IT1R` +2 and its bonus's
  magic bits; without one, bludgeoning (8), and a creature not of a player
  race (race 0 or above 8) counts as magic of (level − 2) ÷ 2 (0x1000,
  0x3000, 0xB000). A missile's bonus is the better of the weapon's and the
  ammunition's (`612E:1FCF`, `DS:47BD:0006`).
* **Armour class** (`80F5:39B7`): from the front Dexterity's adjustment;
  Spirit Armor caps it at 4, else Armor at 6; Barkskin at 6 − level ÷ 4;
  Shield at 4 from the front; Magical Vestments (60) at 5 − (level − 5) ÷ 3
  from level 6; Flesh Armor (59) at 10 − level (`7D80:16AC`: the effects'
  levels, `88F4:06A6`). Then Displacement (58) −2, Dispelling Evil −7
  against a race-14 attacker, Blind +4, Slowed +4, Protection from Evil −2
  against an evil attacker, unseen −4, Biofeedback (4) −1.

### Conditions

An effect's condition (spell record +0x12; effect list `3BF6:0106`, 10
bytes each, +0 object, +6 condition, sorted by object, `DS:1E24` entries)
is bit n of the 10-byte bitfield `7D80:1864(object, buf)` builds. Names
(far pointers, 6 bytes each, at `3F8D:0000`, entry n − 1): 1 Acid,
2 Improved AC, 3 Berserk, 4 Biofeedback, 5 Blink, 6 —, 7 Blessed, 8 Blind,
9 Brave, 10 Charmed, 11 Confused, 12 Cursed, 13 Diseased, 14 Detect Traps,
15 Detect Invis, 16 Enlarged, 17 Afraid, 18 Cloak of Fear, 19 Feeblemind,
20 Fire Shield, 21 Free Action, 22 Hasted, 23 Invisible, 24 Invis to
Undead, 25 Mirror Images, 26 Englobed, 27 —, 28 Prot Missile, 29 Prot
Paralysis, 30 Synaptic Static, 31 Low Resistance, 32 Mind Bar, 33 Can't
Attack, 34 Paralyzed, 35 Poisoned, 36 Prot Cold, 37 Prot Energy, 38 Prot
Evil, 39 Prot Evil 10', 40 Prot Fire, 41 Prot Lightning, 42 Neg Plane
Prot, 43 Gaze Reflection, 44 Spell Turning, 45 Save penalty, 46 Shielded,
47 Slowed, 48 Stoneskin, 49 Graft Weapon, 50 No spell use, 51 Stuck,
52 Dispelling evil, 53 Ironskin, 54 —, 55 Blur, 56 Spirit Armor,
57 Barkskin, 58 Displacement, 59 Flesh Armor, 60 Magical Vestments,
61 Animal Affinity, 62 Body Weaponry, 63 Strength Enhanced, 64 Strength
Borrowed, 65 Strength Lent, 66 Adrenalin Control, 67 Strength,
68 Weakened, 69 Extra Hitpoints, 70 Flame Blade, 71 Spirit. Hammer,
72 Shillelagh, 73 Prayer. The spells' +0x12 match (Bless 7, Glitterdust
8, Charm 10, Confusion 11, Fear 17, Haste 22, Hold 34, Slow 47, Web 51,
Prayer 73).

When one is added (`8053:03C0`, a table at `8053:0647`): Bless and Curse,
Haste and Slow, Brave and Afraid cancel each other instead; Charmed (10)
replaces a charm and sets the allegiance to the caster's (1 without one)
and forgets the target; Can't Attack (33) clears the attack flag and the
guard; Paralyzed (34) clears all the action flags; Free Action and
Protection from Paralysis keep off Slowed, and Paralyzed unless the target
is Poisoned; Free Action and ability bit 9 keep off Stuck; Paralyzed and
Stuck clear the move flag. Afraid, Charmed, Berserk and Confused set the
combat record's computer-control bits (+0x21 |= 0x60) while any of them
lasts (`8053:066F` clears them). When one ends (`8053:066F`, `8053:08A3`):
Charmed restores the allegiance from character record +0x3C; Lent
Strength (65) ends the matching Borrowed Strength; Extra Hit Points (69)
counts down; the conjured weapons (70–72) go. Berserk's random allegiance
isn't restored (the port restores it).

### Experience

* **Award** (`7372:1467(member, xp)`, also GPL AWARD): a party member with
  status below 3 and allegiance & 1 gains xp ÷ their number of classes
  (`8E77:02E5`; 1 for a human, who has one class at a time), up to
  2 000 000 000, in character record +0 (int32); +4 keeps the highest
  total. Out of combat (`377E:0019` = 0) the level check follows.
* **Kills** (`612E:25F2`, from the damage code `612E:18DA`): when an enemy
  (allegiance 2) dies, its character record +4 (int32) ÷ the number of
  occupied party slots (`6868:02E9`: object kind ≠ 0, the dead included)
  goes to each of the four, truncated to int16. It is given at the kill,
  not at the end of the fight.
* **Level check** (`8E77:0A8F` → `8E77:08A6(0)`; at the end of a fight and
  after an award): for each party member with status below 3 and each class
  (a human only the first), while the level (+0x24 + i) is below 9 and XP ≥
  100 × `38F0:027C`[row][level] (20 words a row; row from `38D4:00CA +
  2·class code`), it shows the message and raises the level (`8E77:0765`),
  then recomputes the character (`8E77:0162`). `8E77:0AA0` (argument 1)
  raises one level regardless.
* XP for the next level (row: class codes): 1 clerics (1–4) 1 500, 3 000,
  6 000, 13 000, 27 500, 55 000, 110 000, 225 000; 2 druids (5–8) 2 000,
  4 000, 7 500, 12 500, 20 000, 35 000, 60 000, 90 000; 3 fighter (9) and 4
  gladiator (10) 2 000, 4 000, 8 000 … doubling to 250 000; 5 preserver
  (11) 2 500, 5 000, 10 000, 20 000, 40 000, 60 000, 90 000, 135 000;
  6 psionicist (12) 2 200, 4 400, 8 800, 16 500, 30 000, 55 000, 100 000,
  200 000; 7 rangers (13–16) 2 200, 4 500, 9 000 … doubling to 300 000;
  8 thief (17) 1 200, 2 500, 5 000, 10 000, 20 000, 40 000, 70 000,
  110 000 (AD&D has 1 250). The row is the low byte of the word (`8E77:093D`
  reads a byte). Codes 18 (defiler) and 19 (templar) give row 0, which isn't
  an XP table (25 600, 100, 25 700, …). No party member can have them:
  character creation offers only the 8 classes 1–17 cover, and only
  creatures carry 18 (Balkazar, Tara, Wyrmias, the dark spiders, defilers)
  or 19 (templars, Keldar, Mikquetzl), so the row is never read.

### Psionic powers (`3FB9:0000 + 8 · (power − 139)`)

* An 8-byte record per power, matching the manual's power list:
  +0 power score (low 3 bits the ability, Str–Cha; the high 5 a signed
  modifier: Detonate Con −3, Disintegrate Wis −4), +1 initial cost (18, 40,
  …), +2 maintenance cost a round (99: none), +3 discipline (low bits 0
  psychokinesis, 1 psychometabolism, 2 telepathy; 0x40 likely a science,
  0x80 a defence mode), +4 a range or area (Mass Domination 20), +5 the
  psionic attack or defence (0–3 Psychic Crush, Ego Whip, Id Insinuation,
  Psionic Blast; 254 defence modes), +6 its text.
* **Using one** (`6698:0280`): the cost is +1, except Enhanced Strength
  ((24 − character record +0x1B) × 2) and the dominations (by the target's
  level: below 6 3, below 11 8, below 16 13, else 18). Not enough points:
  message 0x612. The power check (`6698:0CED`): score = ability + modifier
  + `6698:0137` − 1 (`0137`: a party member's rank in the power, the power
  byte >> 3, a monster's 1; learning a power makes it 1, `6698:07C5`, and
  learning it again from an item, `91F7:03D0`, raises it, `6698:0685`, up to
  30; `085D` and `06F9` lower it; it is saved as `PSPP`); d20 ≤ score succeeds (20 always fails, 1 always succeeds), and the
  roll is kept (`DS:47F0:0076`). While anyone has Synaptic Static (30,
  `7D80:1963`) its holder can use none, and anyone else is blocked
  (message 0x626) unless its d20 ≤ its rank and the holder's d20 is over
  Int + Synaptic Static's modifier + the holder's rank − 1 or under the
  user's. A failed check costs
  half; a success the full cost, then `6698:0C54` and the effect
  (`7D80:0000`, effect 0x8A + power). A party member's power with a
  maintenance cost stays up (`DS:0077 + 0x22·member + power`, bit 1):
  each round `6698:04C7` (from `612E:0754`) casts it again and takes the
  maintenance, or drops it and ends its effects (`060D`).
* **Psionic defence** (`6698:0E68`, from the resistances `80F5:28F2` for an
  effect of element 0x400 alone): the defence is Wis − 7; a party member's
  best defence mode (+5 = 254) that is up, if rank + cost ÷ 2 is better and it
  can pay the cost again now (it does); a monster's first-class level if
  better; at least 1. Two rolls of 1 … defence both over the attacker's kept
  roll resist it. So the defence modes work only as kept-up powers, and the
  attack modes (+5 0–3) are never compared with them.
* **Learning from an item** (USE, `91F7:03D0`): items `RDFF` 1400–1499
  teach the spell or power of record +0x0F − 1 (a record index). A spell
  (index < 0x8A) is set known if it wasn't ("YOU LEARN THE SPELL", else "YOU
  ALREADY KNOW THIS SPELL"); a power is learned at rank 1 (`6698:07C5`,
  "%Fs LEARNED" with its name, power record +6) or, if known, raised a rank
  (`6698:0685`, at most 30; "%Fs ENHANCED TO %d"); no class, discipline or
  count is checked. Learning uses up the item (`7BEB:0A06`); anything else
  is "CANNOT LEARN FROM THIS ITEM". Items 30999 down to 0x754D and
  0x7929–0x7940 run their own use code instead.
* `6698:0C54(object, code)` is the level in class code (a psionicist's at
  least 1).

### Active slots (`1A0A:273E`)

Only objects 0–47 have order records (`3972:08A6 + 0x13·n`: +0 target
object or x, +2 y, +4 order mode) and movement state (`3972:0377 +
0x1C·n`, 0xFF when not moving). Free slots are a list headed at
`477D:0B00`, linked through the object table's index word (`1A0A:07DE`
pops one); `1A0A:0005` frees a slot back to its list (0–47 `0B00`,
0x140–0x207 `0B02`, else `0B04`).

`1A0A:273E(object)` returns the object unchanged if it is below 48 or no
slot is free (callers test ≥ 48 for failure). Otherwise it moves the object
to the free slot and returns the new index: it copies the 32-byte record
(`477D:2424 + 32·n`) and the 3-byte object entry (`3972:0C36`), points the
render-list entry back at it (`*477D:2420` + 8·record+1, field +6), and
fills the old record with 0xFF. Then it updates every reference to the old
index: `gname[12..15]` (`3781:0361`–`0367`); order targets and the active
member (`1A0A:31D5`, skipping orders in mode 0xF, whose target is a cell);
effects and their table (`7D80:2256`, re-sorted by object). Last, it frees
the old slot (`1A0A:0005`). Callers: giving orders (`1A0A:03CA`, `04DC`,
`0554`), planning a move (`0B0F`), the combat loop and `7372:1763`.

### The AI (`612E:0BAD`)

Per-combatant state, indexed by combat record n: action flags
`37BD:01D5` (bit 0 may move, 1 may attack, 0x40 re-equip, 0x80 may use
psionics, 0x100 new round, 0x200 may use the +0x16 ability), movement
points `37BD:022B`, has-target `37BD:0085` (−1 none, 1 yes), tried-spells
`37BD:00AF`, remembered target `3931:01DD`. The return value is 2 when the
turn is over and 3 while a move or attack is still under way, after which the
loop calls the AI again.

* **Mind** (`DS:4946`): set once a round, when flag 0x100 is up, by
  `8053:000E`, which clears the flag. From the conditions: Afraid (17)
  gives 1; Berserk (3) re-rolls the allegiance (+0x1D = d2, either side)
  and gives 7; Confused (11) rolls d10: 1 gives 1, 2–6 give 0, 7–9 as
  Berserk, 10 the default. The default is combat record +0x1E: in the
  shipped monsters 2 for most, 1 for civilians (villagers, serfs, slaves,
  worshippers), 8 for bystanders (prisoners, a sage, some guards), also 0,
  4 and 10. Only 1 and 8 change what the AI does. 8 skips the turn. 1
  **flees**: no spells, abilities, psionics or attacks; it moves with order
  mode 1, away from its target. A confused creature on 2–6 therefore acts
  normally, not standing confused as in AD&D.
* **Target:** the remembered one, while it is still a combatant with status
  ≤ 2. Otherwise `612E:1284` picks one at **random** (not the nearest) from
  the enemies (allegiance & (3 − own)) with status below 3 within 24,
  measured from the actor's cell with no line-of-sight test (`612E:0000`).
  With none, the turn ends.
* **Weapon** (`612E:0252` → `612E:0350`): the best weapon in a hand or the
  missile slot (slots 3, 10, 2) with ammunition left. Its score is dice ×
  sides + 2 × damage bonus + 4 × item bonus, plus 10 if magic (bonus > 0,
  item +0x0F, or `IT1R` +8 & 4). A missile weapon (`IT1R` range +0x0A > 1)
  multiplies that by `IT1R` +0x0B. A melee weapon adds Strength damage and
  3 × the material modifier, then multiplies by attacks per round (halves).
  Reach is the weapon's range, or 1 with no weapon. Flag 0x40 re-equips
  first (`6466:0854`).
* Then, in order, the first that applies:
  1. **Too far:** a target more than 24 away (`1A0A:2BA5`, nearest footprint
     cells) ends the turn.
  2. **Spells**, once (until `00AF` is set), with Intelligence (combat
     record +0x25) above 9 and mind ≠ 1: `6798:046B(actor, list, 3, −1)` lists
     castable spells (`DS:019C`, 7 bytes per spell). Within 5 of the target,
     with points to move (flag 0x1, > 10), it first steps back (order mode
     1, `1A0A:0B0F(actor, 1)`; returns 3 while moving). Then `6466:0000`:
     each castable spell with a slot left (`6798:0366`, count in the high
     4 bits) rolls rand(0 … weight − 1), weight = `37E5:0000`[spell − 1]
     (signed bytes: Fireball and Monster Summoning II 63, Ice Storm 59,
     Insect Plague and Slay Living 60, Vampiric Touch and Cone of Cold 55,
     Lightning Bolt 47, Magic Missile 40, …, Cure Light Wounds 6, Armor 0:
     never); the highest roll is cast at the cell `6466:0179` picks. On
     success the turn ends, otherwise `00AF` is set and the target dropped.
  3. **Special ability** (flag 0x200, combat record +0x16 ≠ 0), `6466:067E`:
     one turn in 4 (`6466:05EF`), the first of 11 abilities the creature
     has, tested as bits of a 32-bit mask at `3E45:0000 + 4·(+0x16)`
     (`84C1:0000`), in the order of `DS:05D0` (bits 26, 20, 31, 30, 6, 12,
     18, 19, 21, 22, 29), gives an effect from `DS:05DB`: 27 Fireball,
     181 a 6d6 fire cone, 185 a 3d6 fire cone, 30 Hold Person, 138 Detonate (the
     first psionic power), 175 an area that leaves Stuck, 179 a paralysing
     touch, 180 a 1d6 missile (projectile 14), 182 a paralysing cone, 183 a
     touch, 43 Fear (effect = spell record index, 0-based; 172 and up have
     no `SPIN` text). +0x16 is the creature's type (see "Ability bits");
     types with abilities include wyverns, vrocks, babau, slimes, shadows and
     psurlons. It is cast at `6466:0179`'s cell
     (`7D80:21C5`); on success the turn ends.
  * **Area targeting** (`6466:0179(caster, effect, &x, &y)`, spells and
    abilities): for each cell within ±2 of the target, on the map, in sight
    and within range (`8BEB:0850` ÷ 16), it adds up the hit points in the
    effect's area (`8BEB:034D`) per side. A monster (allegiance 2) scores
    party-side HP − its own side's HP. A party-side caster scores the
    enemies' HP, and only when it hits no allies and that HP is above
    |weight| / 2 (a party member) or / 32 (an ally). It skips the search
    when the enemies' total HP is below half its own side's. The best cell
    with a score above 0 wins; none, no cast.
  4. **Psionics** (flag 0x80, monsters only, until `00AF`): `6466:0453`
     takes character record +0x46 as a power set (1-based) and draws one of
     its 10 powers at random (`DS:001C + 10·(set − 1)`, power = byte + 0x8A);
     it is used if the target is in line of sight, within range
     (`8BEB:0850`) and the cost (`6698:0C2A`) is no more than the PSP
     (+2), which pay it.
  5. **Attack** when the target is within reach and in line of sight
     (`0898:00A4`): order mode 0, `combat_attack` (`612E:1359`), then 3
     while attacks remain (`612E:23F2`), else 2.
  6. **An adjacent enemy** in line of sight (`1284` with range 1) becomes
     the target and is attacked.
  7. **Move** (flag 0x1): `1A0A:2E99` finds a free cell next to the target,
     order mode 0xF to that cell, `1A0A:0B0F`; 3 while moving. When it can't
     move, the nearest enemy in sight within 24 (`612E:0000`, sorted by
     `612E:01BF`) becomes the target and it tries again.
  8. **Missile weapon** (`6466:0AE8`, slot 2): a random enemy in sight
     within its range becomes the target and is attacked.
  9. **Otherwise:** with more than 10 movement points left, forget the
     target. With flag 0x2 (it may still attack) it **guards**
     (`612E:2514(actor, 1)`) and the turn ends; else it just ends.
* **Guard, Wait, End turn** (`612E:2514(object, mode)`; the player's
  combat menu GUARD / WAIT / END TURN, `1587:2EAF`, gives modes 1, 2 and
  `612E:2596(object, 0)`): 1 sets the guard byte `3931:0231`[slot] and
  leaves the round (`2596(object, 0)`: initiative −1, action flags 0); 2
  waits: initiative above 10 drops to 10, above 1 by one, and flag 0x10 is
  set (`2596(object, 0x10)`), so it acts again later in the round. End turn
  just leaves the round. While an enemy moves (`1A0A:0B0F`), each guard with
  status 1, of another allegiance, within 1 cell (`1A0A:2BA5` < 2) attacks it
  (`612E:1359`); the byte is cleared when its attacks run out
  (`612E:23F2`) and at its next turn (`612E:09B6`, which also clears flag
  0x10). Message "%Fs GUARDS".
* The tables this uses that are only in the EXE (ability masks and the 11
  abilities, resistances, spell and psionic sets, spell weights) ship as
  `DarkSun.Game.Rules.CreatureRules`, generated from `3E45:0000`,
  `DS:05D0`/`05DB`, `3E45:00A8`/`00D2`, `3FB9:0110`, `37F0:001C` and
  `37E5:0000` (sizes from the segment table and the layout of `3E45`: 42
  masks, 16 resistance rows, 174 weights; spell sets 0–9, psionic sets 1–9
  used, the rows after them zero).
* The engine's port is `DarkSun.Game.Combat.Battle` (`Battle.Ai.cs` for
  this AI; its choices are `CombatRules`, `rules/combat.json`).
* **Distance** (`0898:000C`) is in cells: the smallest d with d² ≥ dx² +
  dy², from an octagonal first guess. 999 out of range.

### Ability bits (`3E45:0000`)

Segment `3E45` (0x192 bytes) holds 42 ability masks (dwords, by type =
combat record +0x16, read as a word), then a resistance row number per type
at `00A8` and the 16 rows at `00D2` (see "Elements"). Aposs (128) and Maris
(129) have types past the table; `84C1:0000` reads past it for them. The
bits, from every caller of `84C1:0000` and every load of segment `3E45`
(effects numbered as `SPIN`, record index + 1):

| Bit | Effect | Tested by | Types with it |
|---|---|---|---|
| 0, 1 | burrows (0) and flies (1): off the ground, so unaffected by a ranged spell (range > 8) whose area template (`3CB4` +0x10, ×0x12 at `3C81:0000`, byte +2 & 0xF ≠ 1) lingers: fog, clouds, walls, webs | `80F5:161F` | 0 bulette, dune reaper and fire eel (under the sand, the manual says), xorn, rampager; 1 wyvern, vrock, fire elementals |
| 2 | first natural attack also does effect 173 (2d6 cold) | `80F5:1999` | none |
| 3 | … effect 174 (paralysed 2d6 rounds) | `1999` | shadows |
| 4 | … effect 175 (2d6 acid) | `1999` | slimes |
| 5 | immune to spells with flag 4 (Charm Person, Charm Monster, Charm Person or Mammal, Hold Person, Hold Monster) | `161F` | dark spiders, shadows, psurlons, First Folk |
| 6, 12, 18–22, 26, 29–31 | the AI's 11 (see "The AI"); 22 (effect 184, no damage or condition) is the babau's only, its "gaze … as though touched by a ray of enfeeblement" (clue book) | `6466:05EF` | |
| 7 | nothing tests it; *guess*: sees in the dark | | dark spiders (subterranean), psurlons (no eyes), Undermountain folk (infravision 120'), magera (caves) and the generic type the magera share |
| 10 | nothing tests it: "+1 or better weapon to hit" (manual, clue book); the resistance rows enforce it | | babau, shadows, blue slaad, psurlons, lesser elementals |
| 11 | nothing tests it: "+2 or better weapons" | | elementals, vrock |
| 28 | nothing tests it; *guess*: can't be surprised (the styr, whose type 21 the animated statue has: "having four heads makes it impossible to be surprised") | | animated statue, xorn, slimes, dark spiders, slaadi, vrock, fire eel |
| 8 | nothing tests it | | none |
| 9 | can't be Stuck (condition 51; Free Action also blocks it; `8053:03C0`), and unaffected by ranged ground templates (kind 2: grease, web, solid fog, walls, entangle, quicksand) | `8053:03C0`, `161F` | shadows, xorns |
| 13 | last natural attack also does effect 177 (10 poison, none if saved) | `1999` | spiders |
| 14 | … effect 178 (30 poison, none if saved) | `1999` | mastyrials |
| 15 | … effect 120 (Poison: death when its 10 charges run out) | `1999` | wyverns |
| 16 | first natural attack also does effect 179 (20 acid, and may corrode armour as bit 24) | `1999` | rampager |
| 17 | regains 3 hit points a round, up to character record +8 (`80F5:307D`) | `8053:0163` | dark spiders, mastyrials, red slaad |
| 21 | also immune to Wall of Fog (record 11) | `161F` | red slaad |
| 23 | a damaging ranged spell does 1000 more | `80F5:0E61` | none |
| 24 | first natural attack also casts effects 187, "Armor is corroded" (`80F5:2024`: the first item worn on the arms, legs or chest, slots 0, 6, 9, is destroyed unless its item record +0x0F v ≥ 1 and 1d20 ≥ 10 − v), and 188, "Weapon is corroded" (`80F5:1EBA`: the weapon in hand, `612E:234A`, unless 1d20 ≥ 8 − its bonus +0x14); the clue book's babau "can also corrode weapons and armor" | `1999` | babau |
| 25 | first natural attack, 1 in 10, also gives effect 185 (Diseased, permanent) | `1999` | blue slaad |
| 27 | sees invisible: `80F5:31AB` (the target is unseen: Blind, or Invisible, or Invisible to Undead against race 9) is false for it; used for AC (−4) and saves (+4 at range < 9) | `80F5:31AB` | none |

The "first" attack is attack index 0 in `1999`, the "last" the creature's
last natural attack (the highest i with character record +0x2B + i > 0).
`1999` also gives thri-kreen (race 8) above level 4 effect 190 (paralysed
2d8 rounds) on their last attack, and does 1000 when a hit involves a
race-14 creature and Dispelling Evil (condition 52; which side has which is
not checked). Race 9 is undead
(`80F5:3280`): immune to mind effects (flags 0x26) and to elements 0x100
(negative) and 0x1 (poison).
Bits 10 and 11 agree with the clue book ("lesser elementals can only be
affected by +1 or greater weapons, the regular elementals +2") except that
the resistance rows of types 32 (fire elemental, bit 11) and 33 (lesser fire
elemental, bit 10) are swapped: in the original the fire elemental takes +1
weapons and the lesser one needs +2. The port keeps that and offers `Fixes.FireElementalResistances`
(`rules/combat.json`): the fire elemental needing +2, the lesser +1, both
untouched by fire.

**Effect handlers** (`80F5:05E6(target, caster, set, record, saved, …,
level, allegiance, duration)`): what a record alone doesn't say is a switch
on the record index (38 cases at `80F5:0D96`, handlers at `+0x4C`; by
`SPIN` number here):

| Effect | Handling |
|---|---|
| 30 Haste, 38 Slow | this round's movement points doubled (at most Move × 20) or halved (`80F5:36CC`) |
| 27, 99 Dispel Magic | each condition but those at `0D6E` (Biofeedback, Diseased, Feeblemind, Synaptic Static, Mind Bar, Poisoned, Graft Weapon, No Spells, Stuck, 54) ends if d100 ≤ 50 + 5 × level − 5 × its effect's level |
| 195 | ends the conditions at `0D46`: Acid, Berserk, Blind, Confused, Cursed, Afraid, Low Resistance, Can't Attack, Save Penalty, Slowed |
| 191 Turn Undead | an undead (race 9) is destroyed when d20 ≥ (u − p) × 3 + 10 (22 → 20), u its level (+1 above 4, at most 13), p the priest's (at most 14, −1 above 10, −2 above 12; `80F5:2217`); otherwise nothing (`1A0A:0251` is an empty function) |
| 61 Dismissal, 108 Abjure | a race-14 creature dies when d20 ≥ 11 − level + its level |
| 133 Insect Plague | No Spells (50) for a round |
| 21, 106, 118 | end Slowed, and Paralyzed unless Poisoned |
| 17 Glitterdust | ends Invisible |
| 148 Absorb Disease | the caster takes the target's disease; then falls into the next case |
| 24 Strength, 149 Adrenalin Control | Strength (67) or Adrenalin Control (66) with value 1d6 |
| 158 Share Strength | a = min(24 − target Str, (caster Str − 3) ÷ 2): Borrowed (64) +a, the caster Lent (65) −2a |
| 104 Remove Curse | Strength back to the record's |
| 173 (the shadows' touch) | Weakened (68) −1 |
| 72, 113, 128 | heal 1d8, 2d8 + 1, 3d8 + 3 (`80F5:307D`) |
| 152 Cell Adjustment | heal 2 |
| 157 Lend Health | a = min(target's missing hit points, caster's − 5): the target heals a, the caster loses it |
| 109 Blood Flow | its own side heals 2d6; others, if they fail, Paralyzed |
| 81 Aid | Extra Hit Points (69) 1d8 for 600 (`80F5:3142`) |
| 40 Vampiric Touch, 147 Life Draining | (level ÷ 2)d6 or 1d6 damage, the caster gets them as Extra Hit Points (`80F5:316E`) |
| 170 Psionic Blast | if not saved, down to a fifth of its hit points |
| 80, 86, 90 | Shillelagh, Flame Blade, Spiritual Hammer: with the hand free (else "Failed: weapon in hand"), `7372:0D93` gives the caster item `RDFF` 30024 (a 2d4 club), 30025 (1d4 + 4, fire and magic, +1) or 30026 (+1, 1d4 + 1, two attacks) in slot 3, and condition 72, 70, 71 (value: an uninitialised byte); the item goes when the condition ends (`8053:066F`) |
| 136 Raise Dead | the dead with Con left: Con − 1, 1 hit point, well; else status 8 |
| 139 Detonate | effect 196 too, then `1F4B` breaks the item most likely to |
| 179, 187; 188 | corrode armour (`2024`); corrode weapon (`1EBA`) |

Without a save (`0D32`, 5 cases): 4 Chill Touch Weakened −1, 63 Feeblemind
(stats again), 137 Slay Living 1000 damage, 140 Disintegrate 10 000, 184
Weakened −5. Then `2BB8` (stats) and `2F8E` (hit points) are recomputed for
whoever changed. Magic resistance (`80F5:1901`) is character record +0x29
(vrock 70, babau 50, psurlon 45, blue slaad 40, red slaad and styr 30,
rampager 25): +75 against mind spells (flags 0x26) with Mind Bar (32),
halved by Low Resistance (31), less 30 + level for Lower Resistance.

### The combat screen

* **The menu** (`1587:2EAF`, from `1587:1B43`): once the active party member
  can't move any more but still has action flags (and isn't under computer
  control), a one-shot flag `DS:0BC3` pops up `WIND` 14000 by it
  (`5F6D:0191`): its title bar (button 14004) reads "END %Fs's MOVE"
  (`DS:0C1D`, the name; "END MOVE", `DS:0C2C`, when longer than 20), its
  buttons 14001–14003 GUARD, WAIT and END TURN (`612E:2514(member, 1 / 2)`,
  `612E:2596(member, 0)`), and 10308 closes it. Low on memory it uses
  `WIND` 14001 and buttons 14005 on.
* **Attacks** (`896E:0000(attacker, target, …, distance, item)`): the
  attacker plays `SCMD` action 4 (slot facing × 8 + 4), or 5 at range
  (`896E:0191`). In melee its character record +0x44 is its blow's sound; at
  range a missile object flies from it to the target (`896E:084E` →
  `0245`, 4 a tick): `OJFF` 8117 for items 1010 and 2646, 8500 for 1015,
  1016, 2533 and 2644, else 8101. 36 of the 256 `SCMD` scripts have an
  action 4 and 35 an action 5. A blow that wounds sounds the target's
  character record +0x42 (0: sound 4); one that does nothing sound 5
  (`80F5:0079`).
* **Spells** (`7D80:003B`): record +8 the cast sound, +6 an `OJFF` played
  over the caster, +9 a projectile (`3CB4:0000 + 3·n`: 16 entries, a word
  `OJFF` 8100–8116 and a byte) flown from the caster to the point with
  +0xC's sound. On each target it affects without hurting (`80F5:0079`):
  +0xF's sound and +0xD's `OJFF` (−1: the template's +3). Template +3 is
  also the `OJFF` a field shows on its cells (`7D80:2782`). Sounds are
  `play_sound_effect` (`191F:0A6F`) numbers.
* **Damage** (`80F5:0E61` → `896E:0A0E`): the number over the target on a
  splash, `BMP` 5014's frame 4 for fire, cold or electricity (0x86), 2
  poison or acid (0x41), 3 magic (0x200), else 0 under 15 and 1 from 15.
  `0E61` also adds 1000 for ability bit 23 against an area over 8. That is
  while the map shows (display mode `DS:0BC9` = 1: 1 the map, set by
  `1587:1F83` and `2459`; 2 a window over it, `1587:22BA`, which every
  popup, message and character screen calls; 4 changing region, `26E5`; 0
  the menus, `2631`). With a window over the map, damage to a party member
  (`896E:0ED3`) shows frame 4 alone, no number, for 0x28 ticks over its
  portrait: with the inventory screen open (`3B6A:003C`) at x 37, y 48 ×
  member + 30 (the portrait column); otherwise at the 2 × 2 boxes, (78, 54),
  (129, 54), (78, 114), (129, 114) less 10 in x (centres).
* **A burst** (`7D80:05C7` → `7D80:200D`, a template of kind 1): after the
  projectile lands, the template's +3 `OJFF` is drawn at every point
  `69CB:0000` lays the shape out at, then over six steps (a frame each,
  `0980:5814`) only the first 6/8, 5/8 … 1/8 of the points, then none;
  then the effects are applied.
* **The fight's end** (`612E:04A7` when `612E:0754` finds no enemies):
  `87B3:11AC`, `612E:3199` (kept-up powers end), `8053:0914`, `896E:09D6`,
  `612E:3060`: each party member's Paralyzed ends, its orders clear; one with
  status 1–3 gets status 1, with 1 hit point if it had none; if none of
  them had status 1 or 2 with hit points left (`DS:116A` stays 1), the
  party has fallen: `85FE:02C9` shows `WIND` 3004 (full screen, one button)
  with `BMP` 10004 ("your party has been defeated") at (68, 24) and waits;
  otherwise the experience level check (`8E77:0A8F`) follows.
* **Move triggers** fire for every step planned (`1A0A:0B0F` calls
  `trigger_on_move`), in a fight too.
* **Natural attacks** of a new character (`38D4:0048`, `6ECD:1344`,
  `8E77:038F`): attack 1 with the attack halves of +0x2A (2, 3 from warrior
  level 1, 4 from 7, 5 from 13) of 1d1; a thri-kreen 8 halves of 1d4 and
  attack 3, 2 halves of 1d4 + 1, Move 15 (others 12, +0x28 and combat
  record +0x1B). In a fight (`80F5:2ACC`) Body Weaponry (62) makes attack 1
  2d4 and Animal Affinity (61) at least 1d10.

### Order modes (`1A0A:0B0F`)

`0B0F` plans a move from the order record's mode (+4; jump table at
`1A0A:11B5`, mode − 1):

| Mode | Goal |
|---|---|
| 0 | none (an attack, or idle) |
| 1 | **flee**: start from the free cell next to the target nearest the mover (`1A0A:2E99`, else the target's cell), then take the free cell within x ± 4, y − 6 … + 4 of the mover that is farthest from it (`1A0A:2C86`); always re-planned |
| 10, 11, 13 | the target object's cell |
| 15 | the cell (+0, +2) |
| others | no new goal |

### Not traced yet

What the mind values 0, 2, 4, 7 and 10 mean outside the AI, the save
category table's intent, whether ability bits 7 and 28 meant what the
monster entries suggest, and the class caster mask at `DS:0118`
(`6798:071C`).
