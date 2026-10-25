"""Debug overlay data for a region: GMAP flags, ETAB objects and script triggers.

The layers follow the engine's DebugOverlay (and DSUN.EXE's own GMAP overlay,
142F:0426). Triggers are found statically: every CREATE_..._TRIGGER in the
region's MAS script and the GPL files it calls, whatever their conditions.
"""

import struct

from ds1_parsers import rdff_entries
from gpl_parser import disassemble_gpl, field_types_from
from region_connections import _arguments, _number
from region_renderer import DEFAULT_MAP_WIDTH, TILE_PIXEL_SIZE, _get_resource

# opcode: (kind, operand layout); see dsun-exe-reference.md "Trigger lists".
_TRIGGERS = {
    0x68: ('move tile', 'tile'), 0x69: ('door tile', 'tile'),
    0x6A: ('move box', 'box'), 0x6B: ('door box', 'box'),
    0x6C: ('pickup', 'object'), 0x6D: ('use', 'object'), 0x6E: ('talk', 'object'),
    0x65: ('attack', 'object'), 0x66: ('look', 'object'), 0x6F: ('no orders', 'object'),
    0x1B: ('in sight', 'object'), 0x1C: ('not in sight', 'object'),
    0x70: ('use with', 'use with'),
}


def _object_number(text):
    """name(-n) and -n name OJFF n; anything else is computed at run time."""
    if text.startswith('name(') and text.endswith(')'):
        text = text[5:-1]
    value = _number(text)
    return -value if value is not None and value < 0 else None


def _triggers(gpldata, region):
    field_types = field_types_from(gpldata)
    triggers, pending, seen = [], [('MAS ', region)], set()
    while pending:
        tag, number = pending.pop()
        if (tag, number) in seen or not gpldata.has_resource(tag, number):
            continue
        seen.add((tag, number))
        for row in disassemble_gpl(gpldata.get_resource_data(tag, number), field_types):
            op = row['raw_op']
            if row['addr'] < 0 or (op not in _TRIGGERS and op != 0x14):
                continue
            raw_args = _arguments(row['line'][row['line'].find('(') + 1:-1])
            args = [_number(a) for a in raw_args]
            if op == 0x14:
                if args[1] is not None:
                    pending.append(('GPL ', args[1]))
                continue
            kind, layout = _TRIGGERS[op]
            trigger = {'kind': kind, 'source': f'{tag.strip()} {number} @{row["addr"]:04X}', 'line': row['line']}
            if layout == 'tile':
                trigger.update(x=args[0], y=args[1], width=1, height=1, address=args[2], file=args[3])
            elif layout == 'box':
                trigger.update(x=args[0], y=args[1], width=args[2], height=args[3],
                               address=args[4], file=args[5], who=args[6])
            elif layout == 'object':
                trigger.update(address=args[0], file=args[1], objects=[_object_number(raw_args[2])])
            else:
                trigger.update(address=args[2], file=args[3],
                               objects=[_object_number(raw_args[0]), _object_number(raw_args[1])])
            triggers.append(trigger)
    return triggers


def region_debug(rgn_gff, aux_gffs, gpldata):
    """Return {region, width, height, gmap, objects, triggers} for a region GFF."""
    gffs = [rgn_gff, *aux_gffs]
    region = next(d.number for d in rgn_gff.describe_resources() if d.tag == 'GMAP')
    gmap = rgn_gff.get_resource_data('GMAP', region)
    objects = []
    etab = _get_resource(gffs, 'ETAB', region) or b''
    for index, off in enumerate(range(0, len(etab) - 7, 8)):
        x, y = struct.unpack_from('<hh', etab, off)
        ojff = abs(struct.unpack_from('<h', etab, off + 6)[0])
        rdff = _get_resource(gffs, 'RDFF', ojff)
        if rdff and any(rtype == 2 for _, rtype, _, _ in rdff_entries(rdff)):
            kind = 'creature'
        elif 0x46A <= ojff <= 0x49C or 0x803 <= ojff <= 0x898:
            kind = 'door'
        else:
            kind = 'thing'
        objects.append({
            'index': index, 'ojff': ojff, 'x': x, 'y': y, 'kind': kind,
            'cell': [x // TILE_PIXEL_SIZE, y // TILE_PIXEL_SIZE],
        })
    return {
        'region': region, 'width': DEFAULT_MAP_WIDTH, 'height': len(gmap) // DEFAULT_MAP_WIDTH,
        'gmap': list(gmap), 'objects': objects,
        'triggers': _triggers(gpldata, region) if gpldata else [],
    }
