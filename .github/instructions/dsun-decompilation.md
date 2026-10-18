# DSUN.EXE Decompilation

How to produce and read the decompiled C for **Dark Sun: Shattered Lands**, as
the reference for a fidelity-first reimplementation. Executable layout, the GPL
VM and address conventions are in `dsun-exe-reference.md`.

The output is Ghidra decompiler pseudocode, not original source. It is derived
from the game binary, so it is written to `out/` (gitignored) and regenerated
locally rather than committed.

---

## 1. Regenerating

```
make decomp            # GHIDRA_HOME defaults to ~/.local/opt/ghidra_12.1.4_PUBLIC
```

Steps (about 25 minutes on 4 cores):

1. `unpack-exe`: `src/ovl_unpack.py` flattens the FBOV overlays into
   `out/DSUN_FLAT.EXE` and writes `out/DSUN_FLAT.map` (stub segment → overlay
   segment).
2. `tools/ghidra/gen_symbols.py`: writes `out/symbols.tsv`, made of the curated
   `tools/ghidra/symbols.tsv` plus one `gpl_op_XX_<MNEMONIC>` per GPL handler
   (from the `DS:00C0` table and `gpl_parser._OPCODE_TABLE`).
3. Headless Ghidra imports `DSUN_FLAT.EXE`, runs default auto-analysis, then
   `tools/ghidra/ExportDecomp.java`:
   * creates a function at every `func` symbol;
   * marks `+noreturn` symbols no-return, ends the flow at each call to them
     and recomputes the callers' bodies (so code after `exit()` or `abort()`
     is no longer absorbed), then retries the symbols that were unreachable;
   * creates functions at unclaimed `push bp; mov bp,sp` (with optional
     leading `inc bp`), `enter N,0` after a return, and Borland stack-check
     sites;
   * renames default `FUN_` names to `fn_<seg>_<off>` in EXE coordinates and
     applies names, C prototypes, comments, `+noargs` (no parameters, `void`
     return) and `data`/`label` symbols;
   * decompiles every function, retrying timeouts with a 900 s limit, and
     strips the Borland stack check from the C.

To name something, add it to `tools/ghidra/symbols.tsv` and rerun. Columns:
`seg:off`, kind (`func[+noreturn][+noargs]`, `data` or `label`), name or C
prototype, optional comment. This is the
durable record of naming work: REA/Ghidra GUI annotations are lost when their
session closes.

A prototype is written `ret [near] [pascal] name(type name, ..., ...)`. By
default the function is called far and is cdecl (arguments from `[sp+4]`, first
argument lowest, caller pops). `near` puts the arguments from `[sp+2]`;
`pascal` reverses their order and makes the callee pop them (`ret N`). Data
pointers are near (2 bytes); write `far *` for a 4-byte `seg:off` pointer.
Results are returned in `AL`, `AX` or `DX:AX`. The script sets this storage
explicitly, because the cspec's `__cdecl16far` cannot return in `DX:AX`. The
available types are C base types, `size_t`, `time_t`, `va_list`, `FILE`,
`struct date`/`time`/`heapinfo`/`farheapinfo` and the far code pointers
`sighandler_t`, `atexit_t`, `cbrk_t` and `isr_t` (see
`ExportDecomp.defineTypes`). To check a change without the full 25-minute
run, pass a third script argument with a comma-separated list of EXE segments
to decompile (e.g. `0000,1512`).

## 2. Output (`out/decomp/`)

| File | Contents |
|------|----------|
| `seg_XXXX.c` | functions of EXE segment `XXXX` in address order, each headed `/* seg:off name \| linear 0x… \| N bytes \| origin … \| callers: … */` |
| `index.tsv` | one row per function: `addr linear name size origin status callers callees file` |
| `calls.tsv` | static call edges `caller callee callee_name` (indirect calls are missing) |
| `seeds.log` | function-creation attempts that failed |

`origin`: `ghidra` (auto-analysis), `symbol`, `prologue`, `enter` or
`stackcheck` (recovered by the script). `status`: `ok`, `thunk` (overlay stub
entry, a `JMP FAR` into its overlay), `fail`, or `timeout`.

**Coordinates.** Headers, `index.tsv` and `fn_` names use EXE `seg:off` (load
segment 0), as in `dsun-exe-reference.md`. Ghidra's own `DAT_`, `LAB_`, `s_`
labels and segment constants in the code keep Ghidra segments (EXE seg +
0x1000): `DAT_5356_009c` is `DGROUP(4356):009C`, `DAT_4781_031b` is
`3781:031B`. Linear address = `0x10000 + seg*16 + off`; REA and the Ghidra GUI
take linear addresses.

## 3. Coverage (Ghidra 12.1.4, 2026-10-18)

| | Count |
|---|---|
| Functions | 2 829 (2 564 from auto-analysis; 265 recovered by the script) |
| Decompiled | 2 402 |
| Overlay thunks | 420 |
| Failed | 7 (all auto-analysis functions: `0980:5CDE`, `33A5:0F31`, `33A5:0F40`, `68BD:0627`, `6ECD:07BF`, `74F1:0971`, `896E:0245`) |
| Named | 350 (Borland runtime and overlay manager, GPL interpreter and handlers, `main`) |

Recovered by the script, beyond Ghidra's auto-analysis: 146 symbols (all 114
GPL handlers among them), 106 prologues, 13 stack-check sites.
`seeds.log` lists 3 candidates that could not be created.

### Known defects in the pseudocode

* **Unrecovered jump tables** (61 functions): `switch` dispatch through
  `jmp cs:[bx+…]` sometimes resolves to garbage. The decompiler then pulls in
  unrelated code ("Removing unreachable block" warnings at addresses outside
  the function's segment). Examples: `main`'s command-line switch, and
  `gpl_eval_expr`, whose behaviour is fully documented in
  `dsun-exe-reference.md` and implemented in `src/gpl_parser.py`.
* **Overlapping / bad instructions** (53 functions): usually data decoded as
  code or a wrong entry point. Check with `procedure_assembly` (REA) or
  Capstone before trusting such a function.
* **Inverted stack check**: in a few functions the decompiler wrapped the whole
  body in `if (__brklvl < &local) { … }` with `borland_stack_overflow()`
  after it. Read the `if` body as the function.
* **Indirect calls** (`(*(code *)…)()`, GPL handler table, trace hooks) are
  absent from `calls.tsv`, so `callers: -` is common for handlers.
* `unaff_` registers (271 functions) are register-passed arguments or
  clobbers that Borland's register calling conventions hide; check the
  assembly when one matters.
* **Far pointers print like near ones.** A `void far *` parameter shows as
  `void *`, and a far argument is often built with `CONCAT22(seg, off)`.
  `symbols.tsv` has the real widths. Far code pointers (`sighandler_t` and
  the rest) are typed as 4-byte values, not function pointers, because a
  constant far function pointer makes the decompiler fail. Such constants are
  in Ghidra segments: `signal(8,0x525100f2)` passes `4251:00F2`.
* **Compiler helpers have no prototypes.** `F_LXMUL`, `F_SPUSH`, `LDIV` and
  the rest take their arguments in registers, so calls to them still show
  no arguments (e.g. `N_LXMUL()` in `rand`).
* DOS/BIOS calls appear as `swi(0x21)` etc. with registers lost; read the
  assembly for the AH function number. Port I/O shows as `in(…)`/`out(…)`.

## 4. Top level

`entry` (`0000:0000`) is Borland's `c0` startup. It calls `main` (`1512:0001`)
with `argc, argv, envp`.

Observed in `main`:

1. Loop over `argv`: a `switch` on `argv[i][1] - 'a'` (the table above is the
   unrecovered one) sets options.
2. Initialisation calls: `fn_0000_213a` ×3, `fn_191f_013f`, `fn_6a94_0138`,
   `fn_6a94_07cc`.
3. Outer loop, per pass:
   * clears `DGROUP:116A`, calls `fn_191f_013f`, `fn_8015_0208` and
     `fn_2531_064c` (on -1: `fn_2531_0462`, `fn_0000_2ec5`, `fn_6a94_078c`);
   * inner loop until `DGROUP:116A` is set; returns from `main` when
     `DGROUP:11DA` is 0. Each pass calls `fn_1a0a_32bf`, `fn_2531_0965`,
     `fn_0000_0699`, `fn_2531_0707`, `fn_22f8_047c` (when `DGROUP:1194` is
     set), `fn_1587_1df1` and `fn_1a0a_3345`;
   * drains a list at `DGROUP:1F72` with `fn_1df3_0909`;
   * resets GPL and engine state: 200 records at `*577D:005C` (stride 13),
     `377E:0005–0017 = -1`, `3781:032D` and `3781:035B` = 0;
   * calls `fn_30a2_000a` (on 0: `fn_6a94_078c`), then `gpl_run`.

Inference, not yet confirmed: the inner loop is the per-frame game loop, and
`116A` requests a reload (e.g. a region change) that re-enters GPL.

## 5. Working with REA

Open `out/DSUN_FLAT.EXE` with `open_binary` (provider `ghidra`). REA's session
has only Ghidra's auto-analysis: the 250 recovered functions and all names from
`symbols.tsv` are missing, and its names use Ghidra segments
(`FUN_2512_0001` = `main`). REA cannot create functions, so use it to drill
into functions that already exist (`procedure_pseudo_code`,
`procedure_assembly`, `xrefs`), and use the export for everything else.
A bare `0x…` selector that matches no procedure is read as a file offset.

## 6. Fidelity notes for a reimplementation

Points a faithful rewrite has to preserve, from what is established so far:

* **Integer widths.** `int` is 16-bit; `long` is 32-bit. GPL `lnum`/`gnum`
  are int16 and `lbignum`/`gbignum`/`acc` are int32. Wrap-around on overflow is
  part of the behaviour.
* **GPL expressions** have no precedence: operators apply left to right
  (`dsun-exe-reference.md` §2). Division and comparison semantics should be
  taken from the handler code, not assumed.
* **Interpreter limits**: 50 call frames, 16 nested cross-file calls, 8 levels
  of parentheses, 25 menu entries, 299-character strings, `IF` nesting ≤ 31
  (past `0x1F`, `gpl_op_3e_IF` calls `fn_7372_17a7`, which ends the script
  through `gpl_exit`). Scripts that hit them in the original
  should behave the same.
* **Invalid opcodes** stop the script (`gpl_invalid_opcode` sets
  `377C:0009 = -1`).
* **Random numbers** come from Borland's `rand` (`0000:0822`):
  `seed = seed * 0x015A4E35 + 1` (32-bit, at `DS:4122`), returning
  `(seed >> 16) & 0x7FFF`. The game seeds it once, in its init code
  (`6A94:055E`): `srand((unsigned)time(NULL))`, i.e. the low 16 bits of the DOS
  time with a zero high word. About 22 call sites in 9 segments draw from it.
  GPL `RAND n` (`gpl_op_52_RAND`) calls `rand()` *before* evaluating `n`, then
  computes `(int16)(((int32)rand * (n + 1)) / 0x8000)` with signed 32-bit
  arithmetic, giving 0..n. Reproducing the generator, the seed and the order of
  calls is what makes dice rolls match the original.
* **Timing**: `delay(ms)` (`0000:0F86`) busy-waits on PIT channel 0, scaled by a
  multiplier measured at startup (`delay_calibrate`); `time`/`gettime` read
  the DOS clock. Which of these drive the frame rate is not yet traced.
* **Not yet located**: frame pacing, and the combat and movement rules. Find
  these, and reproduce them exactly, before optimising.

## 7. Runtime library

Segment `0000` (14.8 KB) is Borland C++ 3.0's medium-model runtime: `c0`
startup, compiler helpers, the C library modules linked into `_TEXT`, and the
far string functions. Segment `3653` is the VROOMM overlay manager. Every
function in `0000` is named in `tools/ghidra/symbols.tsv`, along with the RTL's
DGROUP variables (`errno`, `_streams`, `_ctype`, …). 112 runtime
functions (the C library and its internal stack-argument helpers), `main`,
`_OvrInitEms` and `_OvrInitExt` have
Borland C++ 3.0 prototypes, so calls to them show their arguments. The
argument order and calling convention of the internal `pascal` helpers
(`__exit`, `__longtoa`, `__isDST`, the `printf` putters) were checked against
their call sites.

No Borland signature library was available, so each function was
identified from its code (DOS calls, RTL
data it touches, Borland's known algorithms such as `rand`'s LCG) and its call
pattern, and checked against the `#pragma startup`/`exit` tables at
`DS:4250–4279`.

Startup order (priority, lowest first): `ovr_init` (1), `_setupio` (2),
`delay_calibrate`, `_setargv`, `_setenvp` (16), `tzset` (30). Then `c0` calls
`main`, and `exit` runs `atexit` handlers, `ovr_exit` and the exit table.

Most-used entry points, by far-call sites in the image: struct pass-by-value
`F_SPUSH` (157), `_fmemset` (76), `F_SCOPY` (45), `_fmemcpy` (43), `_fstrcpy`
(31), `sprintf` (27), `rand` (22), `_fstrlen` (17), `farmalloc`/`farfree`,
`signal` (3, from `main`), `delay` (3). A rewrite can replace the RTL with its
own standard library except where behaviour leaks into the game:

* `rand`/`srand` (above);
* `sprintf` formatting of `%d`/`%u`/`%s`/`%c` in on-screen text;
* 16-bit `int` results of `strlen`, `atoi` and friends;
* `delay` timing.

Not Borland: segment `303C` is a separate DOS wrapper library (create, open,
read, write, seek, find-first/next, directory and drive calls) that keeps its
own error code at `DS:3CD8` instead of `errno`. Segment `13A7` is the game's
`VMEM` virtual-memory manager (EMS via `int 67h`, XMS via `int 2Fh`).

