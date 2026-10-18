"""
GPL (Game Programming Language) bytecode disassembler for Dark Sun GFF files.

Based on the GPL parser in libgff/src/gpl/parse.c.
GPL bytecode is stored in 'GPL ' (local) and 'MAS ' (global) resources
inside GPLDATA.GFF for both Dark Sun 1 and Dark Sun 2.

Public API
----------
disassemble_gpl(data: bytes, is_mas: bool = False) -> list[dict]
    Returns a list of instruction dicts:
        {
            'addr':  int,   # byte offset of opcode
            'op':    str,   # mnemonic
            'args':  str,   # decoded arguments (may be empty)
            'depth': int,   # indent level (controlled by IF/WHILE/etc.)
            'raw_op': int,  # raw opcode byte
        }

list_scripts(gff) -> list[dict]
    Returns all GPL/MAS resources in a GffFile:
        {'tag': 'GPL'|'MAS', 'number': int, 'size': int}
"""

import struct


# ---------------------------------------------------------------------------
# Variable / operand type constants  (from libgff/include/gpl/var.h)
# ---------------------------------------------------------------------------
GPL_ACCM         = 0x00
GPL_LSTRING      = 0x01
GPL_LNUM         = 0x02
GPL_LBYTE        = 0x03
GPL_LNAME        = 0x04
GPL_LBIGNUM      = 0x05
GPL_GSTRING      = 0x06
GPL_GNUM         = 0x07
GPL_GBYTE        = 0x08
GPL_GNAME        = 0x09
GPL_GBIGNUM      = 0x0A
GPL_IMMED_BIGNUM = 0x0B
GPL_RETVAL       = 0x0C
GPL_GFLAG        = 0x0D
GPL_LFLAG        = 0x0E
GPL_IMMED_BYTE   = 0x0F
GPL_IMMED_WORD   = 0x10
GPL_IMMED_NAME   = 0x11
GPL_IMMED_STRING = 0x12
GPL_COMPLEX_PTR  = 0x30
GPL_COMPLEX_VAL  = 0x31
GPL_COMPLEX_SET  = 0x32
GPL_COMPLEX_SHORTHAND = 0x33

EXTENDED_VAR     = 0x40
OPERATOR_OFFSET  = 0xD0

GPL_OP_ADD   = 0xD1
GPL_OP_MINUS = 0xD2
GPL_OP_TIMES = 0xD3
GPL_OP_DIV   = 0xD4
GPL_OP_AND   = 0xD5
GPL_OP_OR    = 0xD6
GPL_OP_EQ    = 0xD7
GPL_OP_NEQ   = 0xD8
GPL_OP_GT    = 0xD9
GPL_OP_LT    = 0xDA
GPL_OP_GTE   = 0xDB
GPL_OP_LTE   = 0xDC
GPL_OP_BAND  = 0xDD
GPL_OP_BOR   = 0xDE
GPL_OP_CLR   = 0xDF

_OP_SYMBOLS = {
    GPL_OP_ADD: '+', GPL_OP_MINUS: '-', GPL_OP_TIMES: '*', GPL_OP_DIV: '/',
    GPL_OP_AND: '&&', GPL_OP_OR: '||',
    GPL_OP_EQ: '==', GPL_OP_NEQ: '!=',
    GPL_OP_GT: '>', GPL_OP_LT: '<', GPL_OP_GTE: '>=', GPL_OP_LTE: '<=',
    GPL_OP_BAND: '&', GPL_OP_BOR: '|', GPL_OP_CLR: '&~',
}

_VAR_TYPE_NAMES = {
    GPL_ACCM: 'accum', GPL_LSTRING: 'lstr', GPL_LNUM: 'lnum',
    GPL_LBYTE: 'lbyte', GPL_LNAME: 'lname', GPL_LBIGNUM: 'lbignum',
    GPL_GSTRING: 'gstr', GPL_GNUM: 'gnum', GPL_GBYTE: 'gbyte',
    GPL_GNAME: 'gname', GPL_GBIGNUM: 'gbignum',
    GPL_GFLAG: 'gflag', GPL_LFLAG: 'lflag',
}

# Parens that bracket sub-expressions
GPL_CLOSE_PAREN = 0x61
GPL_OPEN_PAREN  = 0x62
GPL_HI_CLOSE    = 0xE1
GPL_HI_OPEN     = 0xE2

# Search qualifier re-entry marker
SEARCH_QUAL = 0x53

MAX_MENU_ITEMS = 24


# ---------------------------------------------------------------------------
# Stateful reader
# ---------------------------------------------------------------------------
class _Reader:
    """Provides byte-level reading helpers over a bytes buffer."""

    def __init__(self, data: bytes):
        self.data = data
        self.pos = 0

    @property
    def remaining(self) -> int:
        return len(self.data) - self.pos

    @property
    def end(self) -> bool:
        return self.pos >= len(self.data)

    def get_byte(self) -> int:
        if self.pos >= len(self.data):
            raise IndexError(f'EOF at {self.pos}')
        b = self.data[self.pos]
        self.pos += 1
        return b

    def peek_byte(self) -> int | None:
        if self.pos >= len(self.data):
            return None
        return self.data[self.pos]

    def get_halfword_be(self) -> int:
        """Big-endian 16-bit (as used by the C code's gpl_get_half_word)."""
        hi = self.get_byte()
        lo = self.get_byte()
        return hi * 0x100 + lo

    def get_halfword_be_s(self) -> int:
        """Same but signed."""
        v = self.get_halfword_be()
        return v if v < 0x8000 else v - 0x10000

    # String tag bytes (from libgff/src/gpl/string.c)
    _STR_INTRODUCE         = 0x01
    _STR_UNCOMPRESSED      = 0x02
    _STR_COMPRESSED        = 0x05

    def read_text(self) -> str:
        """Read a GPL string.

        GPL strings are preceded by a tag byte:
          0x01 – INTRODUCE  → return a placeholder for the active NPC name
          0x02 – raw (uncompressed) null-terminated string
          0x05 – 7-bit compressed (one character per 7 bits, 1-bit initial offset)
        """
        if self.end:
            return ''
        tag = self.peek_byte()
        if tag == self._STR_INTRODUCE:
            self.get_byte()  # consume tag
            return '<name>'
        if tag == self._STR_COMPRESSED:
            self.get_byte()  # consume tag
            return self._read_compressed()
        if tag == self._STR_UNCOMPRESSED:
            self.get_byte()  # consume tag
            parts = []
            while not self.end:
                b = self.get_byte()
                if b == 0x00:
                    break
                parts.append(chr(b) if 0x20 <= b <= 0x7E else ' ')
            return ''.join(parts)
        # Unknown tag – fall back to treating bytes as a null-terminated string
        parts = []
        while not self.end:
            b = self.get_byte()
            if b == 0x00:
                break
            parts.append(chr(b) if 0x20 <= b <= 0x7E else ' ')
        return ''.join(parts)

    def _read_compressed(self) -> str:
        """Decode 7-bit packed GPL string (mirrors read_compressed in string.c).

        Characters are packed as 7-bit values extracted with a rolling bit-shift.
        idx starts at 1, increments each char, wraps to 0 after 7 (skipping a
        byte-fetch on the wrap step), giving one char per 7 bits of stream data.
        The sentinel value 0x03 terminates the string.
        """
        parts: list[str] = []
        buffer = 0
        idx = 1
        while len(parts) < 4095:
            if idx > 0:
                if self.end:
                    break
                buffer = (buffer << 8) & 0xFF00
                buffer |= self.get_byte()
            inword = (buffer >> idx) & 0x7F
            if inword == 0x03:   # end-of-string sentinel
                break
            parts.append(chr(inword) if 0x20 <= inword <= 0x7E else ' ')
            idx += 1
            if idx > 7:
                idx = 0
        return ''.join(parts)


# ---------------------------------------------------------------------------
# Expression decoder (gpl_read_number)
# ---------------------------------------------------------------------------

def _read_simple_var(rdr: _Reader, var_type: int, extended: bool) -> str:
    """Read variable index bytes and format as a named reference."""
    if extended:
        idx = rdr.get_halfword_be()
    else:
        idx = rdr.get_byte()

    name = _VAR_TYPE_NAMES.get(var_type, f'var{var_type:#x}')

    # GNAME has a special quirk: subtract 0x20
    if var_type == GPL_GNAME:
        if 0x20 <= idx < 0x2F:
            return f'gname[{idx - 0x20}]'
        return f'gname[{idx}?]'

    return f'{name}[{idx}]'


def _read_complex(rdr: _Reader) -> str:
    """Attempt to read a complex (field-access chain) variable reference.

    The format is described in libgff but not fully documented.
    We do a best-effort decode: read a preview halfword, then consume
    one field/type pair until a terminator is found.
    We stop after MAX_DEPTH steps to avoid runaway.
    """
    MAX_DEPTH = 16
    parts = []

    for _ in range(MAX_DEPTH):
        if rdr.remaining < 2:
            break
        peek = rdr.data[rdr.pos] * 0x100 + rdr.data[rdr.pos + 1]
        if peek == 0:
            # 'party' case – header follows
            rdr.pos += 2  # consume the zero half-word
            # read the actual reference
            if not rdr.end:
                obj_type = rdr.get_byte()
                parts.append(f'party_obj({obj_type:#x})')
            break
        elif peek > 0x8000:
            # has extension data
            rdr.pos += 2
            field_type = rdr.get_byte()
            field_idx  = rdr.get_byte()
            parts.append(f'field({field_type:#x},{field_idx})')
            break
        else:
            rdr.pos += 2
            field_type = rdr.get_byte()
            field_idx  = rdr.get_byte()
            parts.append(f'obj_field({field_type:#x},{field_idx})')
            break

    return '.'.join(parts) if parts else '?complex'


def _decode_expression(rdr: _Reader, depth: int = 0) -> str:
    """Decode one expression (number/variable/expression) from the stream.

    Mirrors gpl_read_number() from parse.c.
    Returns a human-readable string representation.
    """
    if depth > 8:
        return '...'

    if rdr.end:
        return '<eof>'

    b = rdr.get_byte()
    cop = b

    # -----------------------------------------------------------------------
    # Direct (big-endian) 16-bit literal: first byte < 0x80
    # -----------------------------------------------------------------------
    if cop < 0x80:
        lo = rdr.get_byte()
        cval = cop * 0x100 + lo
        result = str(cval)
    # -----------------------------------------------------------------------
    # Paren blocks (for nested arithmetic)
    # -----------------------------------------------------------------------
    elif cop in (GPL_OPEN_PAREN, GPL_HI_OPEN):
        inner = _decode_expression(rdr, depth + 1)
        result = f'({inner})'
    elif cop in (GPL_CLOSE_PAREN, GPL_HI_CLOSE):
        return ')'  # shouldn't normally appear standalone
    # -----------------------------------------------------------------------
    # Operators – value followed by operator token then RHS
    # -----------------------------------------------------------------------
    elif cop in _OP_SYMBOLS:
        sym = _OP_SYMBOLS[cop]
        rhs = _decode_expression(rdr, depth + 1)
        return f'{sym} {rhs}'  # caller concatenates LHS
    # -----------------------------------------------------------------------
    # High-range: variables / immediates
    # -----------------------------------------------------------------------
    else:
        # Strip EXTENDED_VAR flag if present (only for values < OPERATOR_OFFSET)
        extended = False
        if cop < OPERATOR_OFFSET:
            if cop & EXTENDED_VAR:
                extended = True
                cop &= ~EXTENDED_VAR
        var_type = cop & 0x7F   # strip the 0x80 base

        if var_type == GPL_ACCM:
            result = 'accum'

        elif var_type in (GPL_LNUM, GPL_LBYTE, GPL_LBIGNUM, GPL_LNAME,
                          GPL_LSTRING, GPL_GNUM, GPL_GBYTE, GPL_GBIGNUM,
                          GPL_GNAME, GPL_GSTRING, GPL_GFLAG, GPL_LFLAG):
            result = _read_simple_var(rdr, var_type, extended)

        elif var_type == GPL_RETVAL:
            # Nested call: (( retval_opcode args ))
            inner_op = rdr.get_byte()
            inner_text = _decode_retval(rdr, inner_op)
            result = f'(({inner_text}))'

        elif var_type == GPL_IMMED_BIGNUM:
            hi = rdr.get_halfword_be()
            lo = rdr.get_halfword_be()
            # big-endian 4-byte signed
            cval = (hi << 16) | lo
            if cval >= 0x80000000:
                cval -= 0x100000000
            result = str(cval)

        elif var_type == GPL_IMMED_BYTE:
            v = rdr.get_byte()
            result = str((v if v < 128 else v - 256))

        elif var_type == GPL_IMMED_WORD:
            v = rdr.get_halfword_be()
            result = str(v if v < 0x8000 else v - 0x10000)

        elif var_type == GPL_IMMED_NAME:
            v = rdr.get_halfword_be()
            result = f'name(-{v})'

        elif var_type == GPL_IMMED_STRING:
            text = rdr.read_text()
            result = f'"{text}"'

        elif GPL_COMPLEX_PTR <= var_type <= GPL_COMPLEX_SHORTHAND:
            result = _read_complex(rdr)

        else:
            result = f'??var 0x{cop:02X}'

    # -----------------------------------------------------------------------
    # After reading a value, check for an arithmetic operator chained to it.
    # 0xe1 (HI_CLOSE) and 0x61 (CLOSE_PAREN) are consumed here to close the
    # matching open-paren from the enclosing recursive call.
    # -----------------------------------------------------------------------
    while not rdr.end:
        next_b = rdr.peek_byte()
        if next_b is None:
            break
        if next_b in (GPL_CLOSE_PAREN, GPL_HI_CLOSE):
            rdr.get_byte()  # consume close-paren marker; terminate sub-expr
            break
        if next_b not in _OP_SYMBOLS:
            break
        rdr.get_byte()  # consume operator byte
        sym = _OP_SYMBOLS[next_b]
        rhs = _decode_expression(rdr, depth + 1)
        result = f'{result} {sym} {rhs}'

    return result


def _decode_retval(rdr: _Reader, op: int) -> str:
    """Decode a RETVAL sub-opcode (called from inside (( ))) ."""
    # Only certain opcodes can appear in RETVAL context (from gpl_retval())
    try:
        return _dispatch_op(rdr, op, in_retval=True)
    except Exception as exc:
        return f'?? retval 0x{op:02X}  ; {exc}'


def _read_params(rdr: _Reader, n: int) -> str:
    """Read n parameters and return them as a comma-separated string."""
    parts = []
    for _ in range(n):
        try:
            parts.append(_decode_expression(rdr))
        except Exception as exc:
            parts.append(f'?({exc})')
            break
    return ', '.join(parts)


# ---------------------------------------------------------------------------
# Special opcode handlers
# ---------------------------------------------------------------------------

def _op_compare(rdr: _Reader, in_retval: bool) -> str:
    """COMPARE: ACCUM <= expr."""
    expr = _decode_expression(rdr)
    return f'ACCUM <= {expr}'


def _op_load_accum(rdr: _Reader, in_retval: bool) -> str:
    expr = _decode_expression(rdr)
    return f'ACCUM <= {expr}'


def _op_if(rdr: _Reader, in_retval: bool) -> str:
    # One parameter = jump address if false (ignored in display)
    addr = _decode_expression(rdr)
    return f'IF (accum == 1)'


def _op_else(rdr: _Reader, in_retval: bool) -> str:
    addr = _decode_expression(rdr)
    return 'ELSE'


def _op_menu(rdr: _Reader, in_retval: bool) -> str:
    title_num = _decode_expression(rdr)
    entries = []
    count = 0
    while count <= MAX_MENU_ITEMS:
        p = rdr.peek_byte()
        if p is None or p == 0x4A:
            break
        text_ref  = _decode_expression(rdr)
        func_ref  = _decode_expression(rdr)
        cond_addr = _decode_expression(rdr)
        entries.append(f'  ENTRY(text={text_ref}, func={func_ref}, cond={cond_addr})')
        count += 1
    rdr.get_byte()  # consume MENU_END (0x4A)
    body = '\n' + '\n'.join(entries) if entries else ''
    return f'MENU(title={title_num}){body}\nMENU_END'


def _op_setrecord(rdr: _Reader, in_retval: bool) -> str:
    # Look at next two bytes to determine format
    if rdr.remaining < 2:
        return 'SETRECORD(?)'
    peek = rdr.data[rdr.pos] * 0x100 + rdr.data[rdr.pos + 1]
    lhs = _read_complex(rdr)
    rhs = _decode_expression(rdr)
    return f'SETRECORD {lhs} = {rhs}'


def _op_load_var(rdr: _Reader, in_retval: bool) -> str:
    """LOAD_VARIABLE: read accum (assignment target), then read type+varnum."""
    lhs_expr = _decode_expression(rdr)   # what's being assigned

    if rdr.end:
        return f'{lhs_expr} = ?'

    b = rdr.get_byte()
    datatype = b
    extended = bool(datatype & EXTENDED_VAR)
    datatype &= ~EXTENDED_VAR
    datatype &= 0x7F

    if datatype < 0x10:
        if extended:
            varnum = rdr.get_halfword_be()
        else:
            varnum = rdr.get_byte()
        vname = _VAR_TYPE_NAMES.get(datatype, f'var{datatype:#x}')
        return f'{vname}[{varnum}] = {lhs_expr}'
    else:
        lhs2 = _read_complex(rdr)
        return f'{lhs2} = {lhs_expr} (complex)'


def _op_search(rdr: _Reader, in_retval: bool) -> str:
    who = _decode_expression(rdr)
    if rdr.remaining < 2:
        return f'SEARCH({who})'

    low  = rdr.get_byte()
    high = rdr.get_byte()

    criteria = []
    for _ in range(32):  # safety max
        p = rdr.peek_byte()
        if p is None:
            break
        if p == SEARCH_QUAL:
            rdr.get_byte()  # consume qualifier byte
        field = rdr.get_byte()
        ftype = rdr.get_byte()
        if ftype in (0, 1, 2, 3, 4, 5, 8):
            val = _decode_expression(rdr)
            criteria.append(f'field[{field}]({ftype})={val}')
        else:
            criteria.append(f'field[{field}]({ftype})')
        p2 = rdr.peek_byte()
        if p2 is None or p2 != SEARCH_QUAL:
            break

    crit_str = ', '.join(criteria)
    return f'SEARCH({who}, range=[{low},{high}], {crit_str})'


# Opcodes that never appear standalone in normal flow (terminal / depth-change)
_DEPTH_CHANGE = {
    0x17: +1,   # COMPARE
    0x3E: +1,   # IF
    0x63: +1,   # WHILE
    0x61: -1,   # CMPEND
    0x64: -1,   # WEND
    0x67: -1,   # ENDIF
}

# parameter count table: opcode -> (mnemonic, param_count)
# param_count=-1 means special handler required
_OPCODE_TABLE = {
    0x00: ('EXIT_GPL',                   0),
    0x01: ('LONG /= ',                   2),
    0x02: ('BYTE_DEC',                   1),
    0x03: ('WORD_DEC',                   1),
    0x04: ('LONG_DEC',                   1),
    0x05: ('BYTE_INC',                   1),
    0x06: ('WORD_INC',                   1),
    0x07: ('LONG_INC',                   1),
    0x08: ('HUNT',                       1),
    0x09: ('GETXY',                      1),
    0x0A: ('STRING_COPY',                2),
    0x0B: ('PHYS_DAMAGE',                2),
    0x0C: ('CHANGE_MONEY',               1),
    0x0D: ('SET_VAR',               -1),  # unknown handler → skip
    0x0E: ('TOGGLE_ACCUM',               0),
    0x0F: ('GET_STATUS',                 1),
    0x10: ('GET_LOS',                    3),
    0x11: ('LONG *=',                    2),
    0x12: ('JUMP',                       1),
    0x13: ('CALL_LOCAL',                 1),
    0x14: ('CALL_GLOBAL',                2),
    0x15: ('LOCAL_RET',                  0),
    0x16: ('LOAD_VAR',              -1),  # special
    0x17: ('COMPARE',               -1),  # special
    0x18: ('LOAD_ACCUM',            -1),  # special
    0x19: ('END_FUNCTION',               0),
    0x1A: ('NEXTTO',                     2),
    0x1B: ('CREATE_INLOS_TRIGGER',       4),
    0x1C: ('CREATE_NOTINLOS_TRIGGER',    4),
    0x1D: ('CLEAR_LOS',                  1),
    0x1E: ('NAMETONUM',                  1),
    0x1F: ('NUMTONAME',                  1),
    0x20: ('BITSNOOP',                   2),  # simplified
    0x21: ('AWARD',                      2),
    0x22: ('REQUEST',                    4),
    0x23: ('SOURCE_TRACE',          -1),  # unknown → 0 params
    0x24: ('SHOP',                       1),
    0x25: ('CLONE',                      6),
    0x26: ('DEFAULT_OP_26',         -1),
    0x27: ('IFCOMPARE',                  2),
    0x28: ('DEFAULT_OP_28',         -1),
    0x29: ('ORELSE',                     1),
    0x2A: ('CLEAR_PIC',                  0),
    0x2B: ('CONTINUE',                   0),
    0x2C: ('LOG',                   -1),  # special (reads text)
    0x2D: ('DAMAGE',                     2),
    0x2E: ('SOURCE_LINE',           -1),  # unknown → 0 params
    0x2F: ('DROP',                       3),
    0x30: ('PASS_TIME',                  1),
    0x31: ('EXIT_GPL',                   0),
    0x32: ('FETCH',                      2),
    0x33: ('SEARCH',                -1),  # special
    0x34: ('GETPARTY',                   1),
    0x35: ('FIGHT',                      0),
    0x36: ('FLEE',                       1),
    0x37: ('FOLLOW',                     2),
    0x38: ('ASK_YES_NO',                 0),
    0x39: ('GIVE',                       4),
    0x3A: ('GO',                         2),
    0x3B: ('INPUT_BIGNUM',          -1),
    0x3C: ('GOXY',                       3),
    0x3D: ('READORDERS',                 1),
    0x3E: ('IF',                    -1),  # special
    0x3F: ('ELSE',                  -1),  # special
    0x40: ('SETRECORD',             -1),  # special
    0x41: ('SET_OTHER',                  1),
    0x42: ('INPUT_STRING',               1),
    0x43: ('INPUT_NUMBER',               1),
    0x44: ('INPUT_MONEY',                1),
    0x45: ('JOINPARTY',             -1),
    0x46: ('LEAVEPARTY',            -1),
    0x47: ('LOCKDOOR',              -1),
    0x48: ('MENU',                  -1),  # special
    0x49: ('SETTHING',                   2),
    0x4A: ('MENU_END',                   0),
    0x4B: ('LOCAL_SUB_TRACE',       -1),
    0x4C: ('OP_4C',                 -1),
    0x4D: ('OP_4D',                 -1),
    0x4E: ('OP_4E',                 -1),
    0x4F: ('PRINT_STRING',               2),
    0x50: ('PRINT_NUMBER',               2),
    0x51: ('PRINT_NEWLINE',              0),
    0x52: ('RAND',                       1),
    0x53: ('OP_53',                 -1),
    0x54: ('SHOW_PIC',                   1),
    0x55: ('OP_55',                 -1),
    0x56: ('OP_56',                 -1),
    0x57: ('OP_57',                 -1),
    0x58: ('SKILLROLL',             -1),
    0x59: ('STAT_ROLL',                  3),
    0x5A: ('STRING_COMPARE',             2),
    0x5B: ('MATCH_STRING',          -1),
    0x5C: ('TAKE',                       4),
    0x5D: ('SOUND',                      1),
    0x5E: ('TELEPORT',                   5),
    0x5F: ('MUSIC',                 -1),
    0x60: ('OP_60',                 -1),
    0x61: ('CMPEND',                     0),
    0x62: ('WAIT',                       1),
    0x63: ('WHILE',                      1),
    0x64: ('WEND',                       1),
    0x65: ('CREATE_ATTACK_TRIGGER',      3),
    0x66: ('CREATE_LOOK_TRIGGER',        3),
    0x67: ('ENDIF',                      0),
    0x68: ('CREATE_MOVE_TILE_TRIGGER',   5),
    0x69: ('CREATE_DOOR_TILE_TRIGGER',   5),
    0x6A: ('CREATE_MOVE_BOX_TRIGGER',    7),
    0x6B: ('CREATE_DOOR_BOX_TRIGGER',    7),
    0x6C: ('CREATE_PICKUP_TRIGGER',      3),
    0x6D: ('CREATE_USE_TRIGGER',         3),
    0x6E: ('CREATE_TALKTO_TRIGGER',      3),
    0x6F: ('CREATE_NOORDERS_TRIGGER',    3),
    0x70: ('CREATE_USEWITH_TRIGGER',     4),
    0x71: ('OP_71',                 -1),
    0x72: ('OP_72',                 -1),
    0x73: ('OP_73',                 -1),
    0x74: ('OP_74',                 -1),
    0x75: ('OP_75',                 -1),
    0x76: ('BYTE +=',                    2),
    0x77: ('BYTE -=',                    2),
    0x78: ('BYTE *=',                    2),
    0x79: ('BYTE /=',                    2),
    0x7A: ('WORD +=',                    2),
    0x7B: ('WORD -=',                    2),
    0x7C: ('WORD *=',                    2),
    0x7D: ('WORD /=',                    2),
    0x7E: ('LONG +=',                    2),
    0x7F: ('LONG -=',                    2),
    0x80: ('RANGE',                      2),
}

# opcodes that decrease depth BEFORE printing (so own line is at outer depth)
_DEPTH_BEFORE: set[int] = {0x61, 0x64, 0x67}
# opcodes that increase depth AFTER printing
_DEPTH_AFTER:  set[int] = {0x17, 0x3E, 0x63}


def _dispatch_op(rdr: _Reader, op: int, in_retval: bool = False) -> str:
    """Dispatch a single decoded opcode and return its disassembly text."""
    # Special-cased opcodes
    if op == 0x16:
        return _op_load_var(rdr, in_retval)
    if op == 0x17:
        return _op_compare(rdr, in_retval)
    if op == 0x18:
        return _op_load_accum(rdr, in_retval)
    if op == 0x2C:
        text = rdr.read_text()
        return f'LOG("{text}")'
    if op == 0x3E:
        return _op_if(rdr, in_retval)
    if op == 0x3F:
        return _op_else(rdr, in_retval)
    if op == 0x33:
        return _op_search(rdr, in_retval)
    if op == 0x40:
        return _op_setrecord(rdr, in_retval)
    if op == 0x48:
        return _op_menu(rdr, in_retval)

    if op not in _OPCODE_TABLE:
        ctx = ' '.join(f'{b:02X}' for b in rdr.data[rdr.pos:rdr.pos + 8])
        return f'?? 0x{op:02X}  ; [{ctx}]'

    mnemonic, n_params = _OPCODE_TABLE[op]

    if n_params == -1:
        # Unimplemented special: show mnemonic + hex + raw following bytes
        ctx = ' '.join(f'{b:02X}' for b in rdr.data[rdr.pos:rdr.pos + 6])
        return f'{mnemonic}  ; 0x{op:02X} [{ctx}]'

    if n_params == 0:
        return mnemonic

    args = _read_params(rdr, n_params)
    return f'{mnemonic}({args})'


# ---------------------------------------------------------------------------
# Main disassembler
# ---------------------------------------------------------------------------

def disassemble_gpl(data: bytes, is_mas: bool = False) -> list[dict]:
    """Disassemble a GPL bytecode buffer.

    Returns a list of instruction dicts sorted by their byte address.
    """
    rdr   = _Reader(data)
    insts = []
    depth = 0

    while not rdr.end:
        addr = rdr.pos
        try:
            op = rdr.get_byte()
        except IndexError:
            break

        # Depth adjustments BEFORE printing
        if op in _DEPTH_BEFORE and depth > 0:
            depth -= 1

        try:
            text = _dispatch_op(rdr, op)
        except Exception as exc:
            text = f'<error: {exc}>'
            insts.append({'addr': addr, 'op': f'?? 0x{op:02X}', 'args': str(exc),
                          'line': f'?? 0x{op:02X}  ; {exc}',  # ERR',
                          'depth': max(0, depth), 'raw_op': op})
            # Don't stop — skip to the next byte and try to continue
            continue

        if op in _OPCODE_TABLE:
            mnemonic = _OPCODE_TABLE[op][0]
        else:
            mnemonic = f'OP_{op:#04x}'

        # Split mnemonic from args for multi-line opcodes (MENU)
        if '\n' in text:
            lines = text.split('\n')
            for i, line in enumerate(lines):
                insts.append({
                    'addr': addr if i == 0 else -1,
                    'op':   line if i == 0 else '',
                    'args': '',
                    'line': line,
                    'depth': max(0, depth),
                    'raw_op': op,
                })
        else:
            # Use first word as `op` for colouring; keep full text in `line`
            first_sep = min(
                (text.find(c) for c in (' ', '(') if c in text),
                default=len(text),
            )
            op_word = text[:first_sep].strip() if first_sep < len(text) else text
            args_str = text[first_sep:].lstrip(' (').rstrip(')') if first_sep < len(text) else ''
            insts.append({'addr': addr, 'op': op_word, 'args': args_str,
                          'line': text,
                          'depth': max(0, depth), 'raw_op': op})

        # Depth adjustments AFTER printing
        if op in _DEPTH_AFTER:
            depth += 1

    return insts


# ---------------------------------------------------------------------------
# Script listing
# ---------------------------------------------------------------------------

def list_scripts(gff) -> list[dict]:
    """Return all GPL/MAS script descriptors from a GffFile."""
    results = []
    for d in gff.describe_resources():
        tag = d.tag.strip()
        if tag in ('GPL', 'MAS'):
            results.append({
                'tag':    tag,
                'number': d.number,
                'size':   d.size if hasattr(d, 'size') else 0,
            })
    return sorted(results, key=lambda x: (x['tag'], x['number']))
