"""
Build the symbol list for ExportDecomp.java.

Combines the curated tools/ghidra/symbols.tsv with one entry per GPL command
handler, read from the dispatch table at DGROUP:00C0 in DSUN.EXE and named
from gpl_parser's opcode table.

  python tools/ghidra/gen_symbols.py --exe=dark_sun/DSUN.EXE --out=out/symbols.tsv
"""

import argparse
import os
import re
import struct
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'src'))
from gpl_parser import _OPCODE_TABLE  # noqa: E402

DGROUP = 0x4356
GPL_SEG = 0x043D
CMD_TABLE = 0x00C0
CMD_COUNT = 0x81
INVALID_HANDLER = 0x264C

_OPERATOR_WORDS = {'+=': 'ADD', '-=': 'SUB', '*=': 'MUL', '/=': 'DIV'}


def _handler_name(op: int) -> str:
    entry = _OPCODE_TABLE.get(op)
    if entry is None:
        return f'gpl_op_{op:02x}'
    mnemonic = entry[0]
    for sym, word in _OPERATOR_WORDS.items():
        mnemonic = mnemonic.replace(sym, word)
    return f'gpl_op_{op:02x}_' + re.sub(r'\W+', '_', mnemonic).strip('_')


def gpl_handlers(exe: bytes) -> list[tuple[str, str, str, str]]:
    header_size = struct.unpack_from('<H', exe, 8)[0] * 16
    table = header_size + DGROUP * 16 + CMD_TABLE
    rows = []
    seen = set()
    for op, off in enumerate(struct.unpack_from(f'<{CMD_COUNT}H', exe, table)):
        if off == INVALID_HANDLER or off in seen:
            continue
        seen.add(off)
        rows.append((f'{GPL_SEG:04X}:{off:04X}', 'func', _handler_name(op),
                     f'GPL opcode 0x{op:02x} handler (DS:00C0 table)'))
    return rows


def main():
    here = os.path.dirname(__file__)
    ap = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    ap.add_argument('--exe', required=True, help='original DSUN.EXE')
    ap.add_argument('--curated', default=os.path.join(here, 'symbols.tsv'))
    ap.add_argument('--out', required=True)
    args = ap.parse_args()

    with open(args.exe, 'rb') as f:
        exe = f.read()
    with open(args.curated) as f:
        curated = [line for line in f if line.strip() and not line.startswith('#')]
    handlers = gpl_handlers(exe)
    with open(args.out, 'w') as f:
        f.writelines(curated)
        for row in handlers:
            f.write('\t'.join(row) + '\n')
    print(f'{len(curated)} curated + {len(handlers)} GPL handlers -> {args.out}')


if __name__ == '__main__':
    main()
