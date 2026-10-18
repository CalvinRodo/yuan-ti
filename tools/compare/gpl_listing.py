"""List every GPL and MAS script in GPLDATA.GFF with src/gpl_parser.py.

Reference side of `make engine-compare`: engine/tools/DarkSun.Dump writes the
same listing from the C# decoder (DarkSun.Game.Scripting.GplDecoder), and the
two must be identical.

Usage: gpl_listing.py <GPLDATA.GFF> <out.txt>
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'src'))

from gff import GffFile  # noqa: E402
from gpl_parser import disassemble_gpl, field_types_from  # noqa: E402


def main(gff_path: str, out_path: str):
    with open(gff_path, 'rb') as f:
        gff = GffFile.create(f.read())
    field_types = field_types_from(gff)
    with open(out_path, 'w', newline='\n') as out:
        for tag in ('GPL ', 'MAS '):
            for n in sorted(d.number for d in gff.describe_resources() if d.tag == tag):
                out.write(f'; {tag.strip()} {n}\n')
                for inst in disassemble_gpl(gff.get_resource_data(tag, n), field_types):
                    addr = f'{inst["addr"]:05x}' if inst['addr'] >= 0 else ' ' * 5
                    out.write(f'{addr}  {"  " * inst["depth"]}{inst["line"]}\n')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
