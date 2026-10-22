"""List every GPL and MAS script in GPLDATA.GFF with src/gpl_parser.py.

Reference side of `make engine-compare`: engine/tools/DarkSun.Dump writes the
same listing from the C# decoder (DarkSun.Game.Scripting.GplDecoder), and the
two must be identical. The engine names REQUEST codes (REQUEST(MUSIC_LEVEL, …)
for REQUEST(16, …)); so does this listing, with the names read from the
engine's table (engine/src/DarkSun.Game/Scripting/GplRequests.cs), the only
place they are written.

Usage: gpl_listing.py <GPLDATA.GFF> <out.txt>
"""
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'src'))

from gff import GffFile  # noqa: E402
from gpl_parser import disassemble_gpl, field_types_from  # noqa: E402


REQUESTS_CS = os.path.join(os.path.dirname(__file__), '..', '..', 'engine', 'src', 'DarkSun.Game', 'Scripting', 'GplRequests.cs')


def request_names() -> dict[int, str]:
    """The REQUEST codes' names, from the engine's GplRequests table: new(code, "NAME", "summary")."""
    with open(REQUESTS_CS, encoding='utf-8') as f:
        return {int(code): name for code, name in re.findall(r'new\((\d+), "([A-Z0-9_]+)"', f.read())}


def name_request(line: str, names: dict[int, str]) -> str:
    """REQUEST(16, …) as REQUEST(MUSIC_LEVEL, …), as a command or in an expression: a plain number with a
    name, as GplDecoder.RequestName does."""
    return re.sub(r'\bREQUEST\((\d+)(?=[,)])',
                  lambda m: f'REQUEST({names[int(m.group(1))]}' if int(m.group(1)) in names else m.group(0), line)


def main(gff_path: str, out_path: str):
    names = request_names()
    with open(gff_path, 'rb') as f:
        gff = GffFile.create(f.read())
    field_types = field_types_from(gff)
    with open(out_path, 'w', newline='\n') as out:
        for tag in ('GPL ', 'MAS '):
            for n in sorted(d.number for d in gff.describe_resources() if d.tag == tag):
                out.write(f'; {tag.strip()} {n}\n')
                for inst in disassemble_gpl(gff.get_resource_data(tag, n), field_types):
                    addr = f'{inst["addr"]:05x}' if inst['addr'] >= 0 else ' ' * 5
                    out.write(f'{addr}  {"  " * inst["depth"]}{name_request(inst["line"], names)}\n')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
