"""
Text resource parsing helpers.

Handles decoding of plain text resources (TEXT, MERR, SPIN) and the NAME
table of 25-byte name records. Ported to C# as DarkSun.Data.Text.
"""


def decode_text(raw: bytes) -> str:
    """Decode a null-terminated latin-1 string, normalising line endings."""
    return raw.split(b'\x00')[0].decode('latin-1').replace('\r\n', '\n').replace('\r', '\n')


def parse_spell(raw: bytes) -> tuple[str, str]:
    """Split a SPIN resource ("NAME: description") at the first ': '."""
    name, _, desc = decode_text(raw).strip().partition(': ')
    return name, desc


# NAME table: 25-byte records, each a NUL-terminated name. DSUN.EXE copies a
# name from table + 25 * n (see dsun-exe-reference.md); the bytes after the
# NUL are leftovers from the tool that wrote the file, not data.
NAME_RECORD_SIZE = 25


def parse_name_resource(raw: bytes) -> list:
    """Parse the NAME table into a list of entry dicts, one per record.

    Each dict has:
      index     – record number (what item records refer to)
      text      – the name
      path      – the leftover bytes after the name's NUL (printable ASCII
                  as-is, others as \\xNN), kept for inspection only
      sep_hex   – the record's last three bytes as hex (also leftovers)
      raw_bytes – the whole record as list[int]
      seg_len   – length of the name and its NUL
    """
    entries = []
    for index in range((len(raw) + NAME_RECORD_SIZE - 1) // NAME_RECORD_SIZE):
        record = raw[index * NAME_RECORD_SIZE:(index + 1) * NAME_RECORD_SIZE]
        null_pos = record.find(b'\x00')
        name = record if null_pos < 0 else record[:null_pos]
        rest = b'' if null_pos < 0 else record[null_pos + 1:]
        entries.append({
            'index':     index,
            'text':      name.decode('latin-1'),
            'path':      ''.join(chr(b) if 32 <= b < 127 else f'\\x{b:02x}' for b in rest[:-3]),
            'sep_hex':   ' '.join(f'{b:02x}' for b in record[-3:]),
            'raw_bytes': list(record),
            'seg_len':   len(name) + (1 if null_pos >= 0 else 0),
        })
    return entries
