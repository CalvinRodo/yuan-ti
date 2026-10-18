"""
GPL (Game Programming Language) bytecode disassembler for Dark Sun GFF files.

GPL bytecode is stored in 'GPL ' (local) and 'MAS ' (global) resources inside
GPLDATA.GFF; both use the same instruction set. The operand grammar follows the
interpreter in DSUN.EXE (Borland segment 043D); mnemonics follow libgff.
See .github/instructions/dsun-exe-reference.md for addresses and derivation.

Public API
----------
field_types_from(gff) -> bytes
    Per-field type table needed to decode SEARCH (opcode 0x33); pass it to
    disassemble_gpl. Comes from GPLX 1.

disassemble_gpl(data: bytes, field_types: bytes = b'') -> list[dict]
    Returns a list of instruction dicts:
        {
            'addr':   int,  # byte offset of opcode (-1 for MENU continuation lines)
            'op':     str,  # mnemonic
            'args':   str,  # decoded arguments (may be empty)
            'line':   str,  # full display text
            'depth':  int,  # indent level (IF/WHILE/COMPARE blocks)
            'raw_op': int,  # raw opcode byte
        }
    Decoding stops at the first invalid opcode, as the interpreter does.

CLI
---
  python gpl_parser.py --gff=GPLDATA.GFF --number=1
  python gpl_parser.py --gff=GPLDATA.GFF --tag=MAS --all --check
"""

import argparse
import struct


# ---------------------------------------------------------------------------
# Operand constants
# ---------------------------------------------------------------------------

# Variable operand bytes 0x81..0x8E (type = b & 0x7F); +0x40 = 2-byte index.
_VAR_TYPE_NAMES = {
    0x01: 'lstr',     # local 42-byte string
    0x02: 'lnum',     # local int16
    0x05: 'lbignum',  # local int32
    0x06: 'gstr',     # global 42-byte string
    0x07: 'gnum',     # global int16
    0x09: 'gname',    # engine variable, raw index 0x20..0x2E
    0x0A: 'gbignum',  # global int32
    0x0D: 'gflag',    # global bit
    0x0E: 'lflag',    # local bit
}
GNAME_BASE, GNAME_END = 0x20, 0x2F

EXPR_ACCUM     = 0x80
EXPR_BIGNUM    = 0x8B
EXPR_RETVAL    = 0x8C   # embedded command, value = accum afterwards
EXPR_BYTE      = 0x8F
EXPR_WORD      = 0x90
EXPR_NAME      = 0x91   # negated word: object name reference
EXPR_STRING    = 0x92
EXPR_COMPLEX   = 0xB1   # object field reference
EXPR_OPEN      = 0xE2
EXPR_CLOSE     = 0xE1
MAX_PAREN      = 8

_OP_SYMBOLS = {
    0xD1: '+', 0xD2: '-', 0xD3: '*', 0xD4: '/', 0xD5: '&&', 0xD6: '||',
    0xD7: '==', 0xD8: '!=', 0xD9: '>', 0xDA: '<', 0xDB: '>=', 0xDC: '<=',
    0xDD: '&', 0xDE: '|', 0xDF: '&~',
}

# String kinds (peeked; only these advance the PC).
STR_INTRODUCE  = 0x01   # speaker name, no further bytes
STR_RAW        = 0x02   # bytes until 3, each mapped by gpl 396B
STR_COMPRESSED = 0x05   # 7-bit packed, char 3 ends
STR_END_CHAR   = 0x03
STR_MAX        = 0x12B

# Object ids with the top bit set name engine-held objects.
_SPECIAL_OBJECTS = {0x25, 0x26, 0x27, 0x28, 0x2B, 0x2C}

MENU_END         = 0x4A
MENU_MAX_ITEMS   = 25
MENU_TEXT_OPS    = {0x23, 0x28, 0x2E, 0x4B}
SEARCH_QUAL      = 0x53
SEARCH_EXPR_TYPES = {0x10, 0x12, 0x13, 0x14}


# ---------------------------------------------------------------------------
# Opcode table: opcode -> (mnemonic, operand spec)
#   E expression   S string   V assignment target   C object field reference
#   Opcodes with dedicated decoders use spec None.
# ---------------------------------------------------------------------------
_OPCODE_TABLE = {
    0x00: ('STRING_END',               ''),   # legal only right after a string
    0x01: ('LONG /=',                  'EE'),
    0x02: ('BYTE_DEC',                 'E'),
    0x03: ('WORD_DEC',                 'E'),
    0x04: ('LONG_DEC',                 'E'),
    0x05: ('BYTE_INC',                 'E'),
    0x06: ('WORD_INC',                 'E'),
    0x07: ('LONG_INC',                 'E'),
    0x08: ('HUNT',                     'E'),
    0x09: ('GETXY',                    'E'),
    0x0A: ('STRING_COPY',              'EE'),
    0x0B: ('PHYS_DAMAGE',              'EE'),
    0x0C: ('CHANGE_MONEY',             'E'),
    0x0D: ('SET_VAR',                  ''),
    0x0E: ('TOGGLE_ACCUM',             ''),
    0x0F: ('GET_STATUS',               'E'),
    0x10: ('GET_LOS',                  'EEE'),
    0x11: ('LONG *=',                  'EE'),
    0x12: ('JUMP',                     'A'),
    0x13: ('CALL_LOCAL',               'A'),
    0x14: ('CALL_GLOBAL',              'AE'),
    0x15: ('LOCAL_RET',                ''),
    0x16: ('LOAD_VAR',                 None),
    0x17: ('COMPARE',                  'E'),
    0x18: ('LOAD_ACCUM',               None),
    0x19: ('END_FUNCTION',             ''),
    0x1A: ('NEXTTO',                   'EE'),
    0x1B: ('CREATE_INLOS_TRIGGER',     'EEEE'),
    0x1C: ('CREATE_NOTINLOS_TRIGGER',  'EEEE'),
    0x1D: ('CLEAR_LOS',                'E'),
    0x1E: ('NAMETONUM',                'E'),
    0x1F: ('NUMTONAME',                'E'),
    0x20: ('BITSNOOP',                 'EE'),
    0x21: ('AWARD',                    'EE'),
    0x22: ('REQUEST',                  'EEEE'),
    0x23: ('SOURCE_TRACE',             'S'),
    0x24: ('SHOP',                     'E'),
    0x25: ('CLONE',                    'EEEEEE'),
    0x27: ('IFCOMPARE',                'EA'),
    0x28: ('OP_28',                    'EES'),
    0x29: ('ORELSE',                   'A'),
    0x2A: ('CLEAR_PIC',                ''),
    0x2B: ('CONTINUE',                 ''),
    0x2C: ('LOG',                      'S'),
    0x2D: ('DAMAGE',                   'EE'),
    0x2E: ('SOURCE_LINE',              'E'),
    0x2F: ('DROP',                     'EEE'),
    0x30: ('PASS_TIME',                'E'),
    0x31: ('EXIT_GPL',                 ''),
    0x32: ('FETCH',                    'EE'),
    0x33: ('SEARCH',                   None),
    0x34: ('GETPARTY',                 'E'),
    0x35: ('FIGHT',                    ''),
    0x36: ('FLEE',                     'E'),
    0x37: ('FOLLOW',                   'EE'),
    0x38: ('ASK_YES_NO',               ''),
    0x39: ('GIVE',                     'EEEE'),
    0x3A: ('GO',                       'EE'),
    0x3B: ('INPUT_BIGNUM',             'E'),
    0x3C: ('GOXY',                     'EEE'),
    0x3D: ('READORDERS',               'E'),
    0x3E: ('IF',                       'A'),
    0x3F: ('ELSE',                     'A'),
    0x40: ('SETRECORD',                None),
    0x41: ('SET_OTHER',                'E'),
    0x42: ('INPUT_STRING',             'E'),
    0x43: ('INPUT_NUMBER',             'E'),
    0x44: ('INPUT_MONEY',              'E'),
    0x45: ('JOINPARTY',                'E'),
    0x46: ('LEAVEPARTY',               'E'),
    0x47: ('LOCKDOOR',                 ''),
    0x48: ('MENU',                     None),
    0x49: ('SETTHING',                 'EE'),
    0x4B: ('LOCAL_SUB_TRACE',          'EEE'),
    0x4F: ('PRINT_STRING',             'EE'),
    0x50: ('PRINT_NUMBER',             'EE'),
    0x51: ('PRINT_NEWLINE',            ''),
    0x52: ('RAND',                     'E'),
    0x54: ('SHOW_PIC',                 'E'),
    0x58: ('SKILLROLL',                'EEE'),
    0x59: ('STAT_ROLL',                'EEE'),
    0x5A: ('STRING_COMPARE',           'EE'),
    0x5B: ('MATCH_STRING',             'EE'),
    0x5C: ('TAKE',                     'EEEE'),
    0x5D: ('SOUND',                    'E'),
    0x5E: ('TELEPORT',                 'EEEEE'),
    0x5F: ('MUSIC',                    'E'),
    0x61: ('CMPEND',                   ''),
    0x62: ('WAIT',                     'E'),
    0x63: ('WHILE',                    'A'),
    0x64: ('WEND',                     'A'),
    0x65: ('CREATE_ATTACK_TRIGGER',    'EEE'),
    0x66: ('CREATE_LOOK_TRIGGER',      'EEE'),
    0x67: ('ENDIF',                    ''),
    0x68: ('CREATE_MOVE_TILE_TRIGGER', 'EEEEE'),
    0x69: ('CREATE_DOOR_TILE_TRIGGER', 'EEEEE'),
    0x6A: ('CREATE_MOVE_BOX_TRIGGER',  'EEEEEEE'),
    0x6B: ('CREATE_DOOR_BOX_TRIGGER',  'EEEEEEE'),
    0x6C: ('CREATE_PICKUP_TRIGGER',    'EEE'),
    0x6D: ('CREATE_USE_TRIGGER',       'EEE'),
    0x6E: ('CREATE_TALKTO_TRIGGER',    'EEE'),
    0x6F: ('CREATE_NOORDERS_TRIGGER',  'EEE'),
    0x70: ('CREATE_USEWITH_TRIGGER',   'EEEE'),
    0x76: ('BYTE +=',                  'EE'),
    0x77: ('BYTE -=',                  'EE'),
    0x78: ('BYTE *=',                  'EE'),
    0x79: ('BYTE /=',                  'EE'),
    0x7A: ('WORD +=',                  'EE'),
    0x7B: ('WORD -=',                  'EE'),
    0x7C: ('WORD *=',                  'EE'),
    0x7D: ('WORD /=',                  'EE'),
    0x7E: ('LONG +=',                  'EE'),
    0x7F: ('LONG -=',                  'EE'),
    0x80: ('RANGE',                    'EE'),
}
# Every other opcode (26 4A 4C-4E 53 55-57 60 71-75, >= 81) hits the
# interpreter's error stub, which aborts the script.

# Indentation: open blocks increase depth after the line, closers decrease it
# before; ELSE does both.
_DEPTH_BEFORE = {0x3F, 0x61, 0x64, 0x67}
_DEPTH_AFTER  = {0x17, 0x3E, 0x3F, 0x63}


class GplError(Exception):
    pass


# ---------------------------------------------------------------------------
# Decoder
# ---------------------------------------------------------------------------

class _Decoder:
    def __init__(self, data: bytes, field_types: bytes):
        self.data = data
        self.pos = 0
        self.field_types = field_types

    # -- raw reads ----------------------------------------------------------

    def byte(self) -> int:
        if self.pos >= len(self.data):
            raise GplError(f'read past end at {self.pos:#x}')
        b = self.data[self.pos]
        self.pos += 1
        return b

    def word(self) -> int:
        """Big-endian 16-bit, high byte first."""
        hi = self.byte()
        return (hi << 8) | self.byte()

    def sword(self) -> int:
        v = self.word()
        return v - 0x10000 if v >= 0x8000 else v

    def peek(self) -> int | None:
        return self.data[self.pos] if self.pos < len(self.data) else None

    # -- operands -----------------------------------------------------------

    def string(self) -> str:
        kind = self.peek()
        if kind == STR_INTRODUCE:
            self.pos += 1
            return '<name>'
        if kind == STR_COMPRESSED:
            self.pos += 1
            return f'"{self._compressed()}"'
        if kind == STR_RAW:
            self.pos += 1
            out = []
            while len(out) < STR_MAX:
                c = (self.byte() << 2) & 0xFF   # gpl 396B
                if c == STR_END_CHAR:
                    break
                out.append(chr(c) if 0x20 <= c <= 0x7E else ' ')
            return f'"{"".join(out)}"'
        # Unknown kind: the interpreter consumes nothing and reuses its buffer.
        return '<last string>'

    def _compressed(self) -> str:
        """7-bit packed text: 8 chars per 7 bytes, big-endian bit order."""
        acc, shift, out = 0, 1, []
        while len(out) < STR_MAX:
            if shift > 0:
                acc = ((acc << 8) & 0xFF00) | self.byte()
            c = (acc >> shift) & 0x7F
            if c == STR_END_CHAR:
                break
            out.append(chr(c) if 0x20 <= c <= 0x7E else ' ')
            shift = shift + 1 if shift < 7 else 0
        return ''.join(out)

    def variable(self, b: int) -> str:
        wide = b >= 0xC0
        var_type = (b - 0x40 if wide else b) & 0x7F
        idx = self.word() if wide else self.byte()
        name = _VAR_TYPE_NAMES.get(var_type)
        if name is None:
            raise GplError(f'bad variable type {b:#04x}')
        if var_type == 0x09:
            return (f'gname[{idx - GNAME_BASE}]' if GNAME_BASE <= idx < GNAME_END
                    else f'gname[?{idx}]')
        return f'{name}[{idx}]'

    def complex_ref(self) -> str:
        """Object field reference: word id, count n, n field bytes."""
        oid = self.word()
        path = [self.byte() for _ in range(self.byte())]
        if oid >= 0x8000:
            n = oid & 0x7FFF
            name = f'obj_special({n:#x})' if n in _SPECIAL_OBJECTS else f'obj_special(?{n:#x})'
        else:
            name = f'obj({oid})'
        return name + ''.join(f'.{p}' for p in path)

    def operand(self) -> str:
        b = self.byte()
        if b < 0x80:
            return str((b << 8) | self.byte())
        if b == EXPR_ACCUM:
            return 'accum'
        if b == EXPR_BIGNUM:
            v = (self.word() << 16) | self.word()
            return str(struct.unpack('<i', struct.pack('<I', v))[0])
        if b == EXPR_RETVAL:
            return f'(({self.instruction_text(self.byte())}))'
        if b == EXPR_BYTE:
            v = self.byte()
            return str(v - 0x100 if v >= 0x80 else v)
        if b == EXPR_WORD:
            return str(self.sword())
        if b == EXPR_NAME:
            return f'name(-{self.word()})'
        if b == EXPR_STRING:
            return self.string()
        if b == EXPR_COMPLEX:
            return self.complex_ref()
        if 0x81 <= b <= 0x8E or 0xC1 <= b <= 0xCE:
            return self.variable(b)
        raise GplError(f'bad operand byte {b:#04x}')

    def expr(self) -> str:
        """Operands joined by operators, applied left to right (no precedence)."""
        parts, depth = [], 0
        while True:
            if self.peek() == EXPR_OPEN:
                self.pos += 1
                depth += 1
                if depth >= MAX_PAREN:
                    raise GplError('parentheses nested too deep')
                parts.append('(')
                continue
            parts.append(self.operand())
            while depth > 0 and self.peek() == EXPR_CLOSE:
                self.pos += 1
                depth -= 1
                parts.append(')')
            nxt = self.peek()
            if nxt in _OP_SYMBOLS:
                self.pos += 1
                parts.append(_OP_SYMBOLS[nxt])
                continue
            return ' '.join(parts).replace('( ', '(').replace(' )', ')')

    def address(self) -> str:
        """Jump/call target: shown in hex to match the address column."""
        text = self.expr()
        return f'@{int(text):04x}' if text.isdigit() else text

    def target(self) -> str:
        """LOAD_VAR destination: type byte, then index or object reference."""
        b = self.byte() & 0x7F
        wide = bool(b & 0x40)
        var_type = (b + 0xC0) & 0xFF if wide else b
        if var_type >= 0x10:
            return self.complex_ref()
        idx = self.word() if wide else self.byte()
        name = _VAR_TYPE_NAMES.get(var_type)
        if name is None:
            raise GplError(f'bad assignment type {b:#04x}')
        return f'{name}[{idx}]'

    # -- instructions -------------------------------------------------------

    def search(self) -> str:
        who = self.expr()
        low, high = self.byte(), self.byte()
        criteria = []
        while True:
            if self.peek() == SEARCH_QUAL:
                self.pos += 1
            field, mode = self.byte(), self.byte()
            ftype = self.field_types[field] if field < len(self.field_types) else 0
            if 4 <= mode <= 6 and ftype in SEARCH_EXPR_TYPES:
                criteria.append(f'field[{field}]({mode})={self.expr()}')
            else:
                criteria.append(f'field[{field}]({mode})')
            if self.peek() != SEARCH_QUAL:
                break
        return f'SEARCH({who}, range=[{low},{high}], {", ".join(criteria)})'

    def menu(self) -> list[str]:
        lines = [self.instruction_text(self.byte()) for _ in self._menu_text()]
        title = self.expr()
        entries = []
        while self.peek() != MENU_END and len(entries) < MENU_MAX_ITEMS:
            entries += [self.instruction_text(self.byte()) for _ in self._menu_text()]
            text, func, cond = self.expr(), self.address(), self.expr()
            entries.append(f'  ENTRY(text={text}, func={func}, cond={cond})')
            entries += [self.instruction_text(self.byte()) for _ in self._menu_text()]
        if self.byte() != MENU_END:
            raise GplError('MENU not terminated')
        return lines + [f'MENU(title={title})'] + entries + ['MENU_END']

    def _menu_text(self):
        while self.peek() in MENU_TEXT_OPS:
            yield

    def instruction_text(self, op: int) -> str:
        if op == 0x48:
            return '\n'.join(self.menu())
        if op == 0x16:
            value = self.expr()
            return f'{self.target()} = {value}'
        if op == 0x18:
            return f'ACCUM <= {self.expr()}'
        if op == 0x33:
            return self.search()
        if op == 0x40:
            if self.data[self.pos:self.pos + 2] == b'\x80\x00':
                return 'SETRECORD'   # id 0x8000 matches no branch; nothing is read
            ref = self.complex_ref()
            return f'SETRECORD {ref} = {self.expr()}'
        if op not in _OPCODE_TABLE:
            raise GplError(f'invalid opcode {op:#04x}')
        mnemonic, spec = _OPCODE_TABLE[op]
        readers = {'E': self.expr, 'A': self.address, 'S': self.string}
        args = [readers[kind]() for kind in spec]
        return f'{mnemonic}({", ".join(args)})' if args else mnemonic


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def field_types_from(gff) -> bytes:
    """GPLX 1 [0x3DE:0x441], copied to DS:537A by the game at load time."""
    if gff.has_resource('GPLX', 1):
        return gff.get_resource_data('GPLX', 1)[0x3DE:0x3DE + 0x63]
    return b''


def _split(text: str):
    sep = min((i for i in (text.find(' '), text.find('(')) if i >= 0), default=len(text))
    return text[:sep].strip(), text[sep:].lstrip(' (').rstrip(')')


def disassemble_gpl(data: bytes, field_types: bytes = b'') -> list[dict]:
    """Disassemble a GPL/MAS bytecode buffer into instruction dicts."""
    dec = _Decoder(bytes(data), field_types)
    insts = []
    depth = 0
    while dec.pos < len(dec.data):
        addr = dec.pos
        op = dec.byte()
        if op in _DEPTH_BEFORE and depth > 0:
            depth -= 1
        try:
            text = dec.instruction_text(op)
        except GplError as exc:
            line = f'?? 0x{op:02X}  ; {exc}'
            insts.append({'addr': addr, 'op': f'?? 0x{op:02X}', 'args': str(exc),
                          'line': line, 'depth': depth, 'raw_op': op})
            break
        for i, line in enumerate(text.split('\n')):
            op_word, args = _split(line)
            insts.append({'addr': addr if i == 0 else -1, 'op': op_word, 'args': args,
                          'line': line, 'depth': depth, 'raw_op': op})
        if op in _DEPTH_AFTER:
            depth += 1
    return insts


def decoded_cleanly(insts: list[dict]) -> bool:
    return not insts or not insts[-1]['op'].startswith('??')


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main():
    import os
    import sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from gff import GffFile

    ap = argparse.ArgumentParser(description='Disassemble GPL/MAS bytecode.')
    ap.add_argument('--gff', required=True)
    ap.add_argument('--tag', default='GPL')
    ap.add_argument('--number', type=int)
    ap.add_argument('--all', action='store_true')
    ap.add_argument('--check', action='store_true', help='only report decode failures')
    args = ap.parse_args()

    with open(args.gff, 'rb') as f:
        gff = GffFile.create(f.read())
    tag = args.tag.ljust(4)
    numbers = (sorted(d.number for d in gff.describe_resources() if d.tag == tag)
               if args.all else [args.number])
    field_types = field_types_from(gff)

    ok = 0
    for n in numbers:
        insts = disassemble_gpl(gff.get_resource_data(tag, n), field_types)
        ok += decoded_cleanly(insts)
        if args.check:
            if not decoded_cleanly(insts):
                print(f'{tag.strip()} {n}: {insts[-1]["line"]} at {insts[-1]["addr"]:#x}')
            continue
        print(f'; {tag.strip()} {n}')
        for inst in insts:
            addr = f'{inst["addr"]:05x}' if inst['addr'] >= 0 else ' ' * 5
            print(f'{addr}  {"  " * inst["depth"]}{inst["line"]}')
    if args.check:
        print(f'{ok}/{len(numbers)} decoded cleanly')


if __name__ == '__main__':
    main()
