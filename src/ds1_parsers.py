"""
Parsers for Dark Sun 1/2 game-data tables embedded in GFF files.

Three data sources are supported:

  items     — IT1R + NAME resources in GPLDATA.GFF (item type lookup table)
  monsters  — RDFF combat objects (type 2) in SEGOBJEX.GFF / OBJEX.GFF
  spells    — SPIN text resources in RESOURCE.GFF

All public functions receive a loaded GffFile object (or a dict of them) and
return plain Python lists-of-dicts suitable for jsonify().
"""

import os
import struct
from typing import Optional

# ---------------------------------------------------------------------------
# Constants derived from libgff headers
# ---------------------------------------------------------------------------

# ds_item1r_t  (20 bytes, __packed__)
# weapon_type u8, data0 u8, damage_type u16, weight u8, data1 u16,
# base_hp u8, material u8, placement u8, range u8,
# num_attacks u8, sides u8, dice u8, mod i8,
# flags u8, legal_class u16, base_AC i8, data2 u8
_ITEM1R_FMT  = '<BBHBHBBBBBBBbBHbB'
_ITEM1R_SIZE = struct.calcsize(_ITEM1R_FMT)   # 20
_ITEM1R_FIELDS = [
    'weapon_type', 'data0', 'damage_type', 'weight', 'data1',
    'base_hp', 'material', 'placement', 'range',
    'num_attacks', 'sides', 'dice', 'mod',
    'flags', 'legal_class', 'base_AC', 'data2',
]

# ds1_combat_t  (58 bytes, __packed__)
# hp i16, psp i16, char_index i16, id i16,
# ready_item_index i16, weapon_index i16, pack_index i16,
# data_block[8] u8×8,
# special_attack u8, special_defense u8,
# icon i16,
# ac i8, move u8, status u8, allegiance u8, data u8,
# thac0 i8, priority u8, flags u8,
# stats: str u8, dex u8, con u8, int u8, wis u8, cha u8,
# name[18] bytes
# (33 values: indices 0–6 hp/psp/.../pack, 7–14 data_block, 15 spc_atk,
#  16 spc_def, 17 icon, 18 ac, 19 move, 20 status, 21 allegiance, 22 data,
#  23 thac0, 24 priority, 25 flags, 26–31 stats, 32 name)
_COMBAT_FMT  = '<7h8BBhbBBBBBbBB6B18s'
_COMBAT_SIZE = struct.calcsize(_COMBAT_FMT)   # 58

# gff_rdff_header_t  (10 bytes)
# load_action i8, blocknum i8, type i16, index i16, from i16, len i16
_RDFF_HDR_FMT  = '<bbhhhh'
_RDFF_HDR_SIZE = struct.calcsize(_RDFF_HDR_FMT)  # 10

# RDFF object types
RDFF_OBJECT    = 1
RDFF_CONTAINER = 2  # NOTE: in code type 2 = GFF_COMBAT_OBJECT (not RDFF_CONTAINER)
GFF_COMBAT_OBJECT = 2
GFF_ITEM_OBJECT   = 1

# NAME record size
_NAME_SIZE = 25

# OJFF struct: flags u16, xoffset i16, yoffset i16, xpos u16, ypos u16,
#              zpos i8, object_index u8, bmp_id u16, script_id u16
_OJFF_FMT   = '<HhhHHbBHH'
_OJFF_SIZE  = struct.calcsize(_OJFF_FMT)   # 16
_OJFF_BMP_IDX = 7  # index of bmp_id in the unpacked tuple

# ---------------------------------------------------------------------------
# Weapon / armour classification helpers
# ---------------------------------------------------------------------------
_WEAPON_FLAGS = {
    0x01: 'Melee',
    0x02: 'Missile',
    0x04: 'Shield',
    0x08: 'Uses ammo',
    0x10: 'Thrown',
    0x80: 'Armour',
}

_SLOT_NAMES = [
    'Arm', 'Ammo', 'Missile', 'Hand', 'Finger', 'Waist',
    'Legs', 'Head', 'Neck', 'Chest', 'Hand (off)', 'Finger (off)',
    'Cloak', 'Foot',
]

_RACE_NAMES = [
    'Monster', 'Human', 'Dwarf', 'Elf', 'Half-Elf',
    'Half-Giant', 'Halfling', 'Mul', "Thri-Kreen",
]

_CLASS_NAMES = [
    'None',
    'Air Cleric', 'Earth Cleric', 'Fire Cleric', 'Water Cleric',
    'Air Druid', 'Earth Druid', 'Fire Druid', 'Water Druid',
    'Fighter', 'Gladiator', 'Preserver', 'Psionicist',
    'Air Ranger', 'Earth Ranger', 'Fire Ranger', 'Water Ranger',
    'Thief', 'Defiler', 'Templar',
]

_ALIGNMENT_NAMES = [
    'Lawful Good', 'Neutral Good', 'Chaotic Good',
    'Lawful Neutral', 'True Neutral', 'Chaotic Neutral',
    'Lawful Evil', 'Neutral Evil', 'Chaotic Evil',
]


def _decode_name(raw: bytes) -> str:
    """Null-terminate and strip a fixed-width C string."""
    return raw.split(b'\x00')[0].decode('latin-1', errors='replace').strip()


def _weapon_type_label(wt: int) -> str:
    parts = [label for flag, label in _WEAPON_FLAGS.items() if wt & flag]
    return ', '.join(parts) if parts else 'Other'


# ---------------------------------------------------------------------------
# Items  (IT1R + NAME in GPLDATA.GFF)
# ---------------------------------------------------------------------------

def parse_items(gff) -> list[dict]:
    """Return a list of item-type dicts from IT1R + NAME resources.

    *gff* should be the GPLDATA.GFF GffFile object.
    Returns [] if the GFF does not contain IT1R data.
    """
    from gff import NoSuchResourceError

    try:
        it1r_data = gff.get_resource_data('IT1R', 1)
    except (NoSuchResourceError, Exception):
        return []

    try:
        name_data = gff.get_resource_data('NAME', 1)
    except (NoSuchResourceError, Exception):
        name_data = b''

    num_items = len(it1r_data) // _ITEM1R_SIZE
    num_names  = len(name_data) // _NAME_SIZE
    items = []

    for i in range(num_items):
        raw = struct.unpack_from(_ITEM1R_FMT, it1r_data, i * _ITEM1R_SIZE)
        d   = dict(zip(_ITEM1R_FIELDS, raw))

        name = ''
        if i < num_names:
            name = _decode_name(name_data[i * _NAME_SIZE: (i + 1) * _NAME_SIZE])

        damage = ''
        if d['dice'] > 0 and d['sides'] > 0:
            damage = f"{d['dice']}d{d['sides']}"
            if d['mod'] > 0:
                damage += f'+{d["mod"]}'
            elif d['mod'] < 0:
                damage += str(d['mod'])

        # PORT resources in GPLDATA.GFF are 1-indexed relative to IT1R order
        port_num = i + 1

        items.append({
            'index':       i,
            'name':        name,
            'weapon_type': _weapon_type_label(d['weapon_type']),
            'damage':      damage,
            'weight':      d['weight'],
            'base_AC':     d['base_AC'],
            'num_attacks': d['num_attacks'],
            'range':       d['range'],
            'material':    d['material'],
            'flags':       d['flags'],
            'legal_class': d['legal_class'],
            'port_num':    port_num,
        })

    return items


# ---------------------------------------------------------------------------
# Monsters  (RDFF combat objects in SEGOBJEX.GFF / OBJEX.GFF)
# ---------------------------------------------------------------------------

def parse_monsters(gff) -> list[dict]:
    """Return a list of combat-entity dicts from RDFF type-2 resources.

    *gff* should be the SEGOBJEX.GFF (DS1) or OBJEX.GFF (DS2) GffFile object.
    Returns [] if no combat objects are found.
    """
    from gff import NoSuchResourceError

    monsters = []
    seen_names: set[str] = set()

    for d in gff.describe_resources():
        if d.tag.strip() != 'RDFF':
            continue
        try:
            raw = gff.get_resource_data('RDFF', d.number)
        except (NoSuchResourceError, Exception):
            continue

        if len(raw) < _RDFF_HDR_SIZE:
            continue

        _la, _bnum, rtype, _idx, _from, _rlen = struct.unpack_from(
            _RDFF_HDR_FMT, raw, 0)

        if rtype != GFF_COMBAT_OBJECT:
            continue
        if len(raw) < _RDFF_HDR_SIZE + _COMBAT_SIZE:
            continue

        vals = struct.unpack_from(_COMBAT_FMT, raw, _RDFF_HDR_SIZE)
        (hp, psp, char_idx, obj_id,
         ready, weapon, pack,
         *data_block_and_rest) = vals

        # Unpack positionally  (33 values — see _COMBAT_FMT comment above)
        (hp, psp, char_idx, obj_id, ready, weapon, pack,
         db0, db1, db2, db3, db4, db5, db6, db7,
         special_atk, special_def,
         icon,
         ac, move, status, allegiance, _data,
         thac0, priority, flags,
         s_str, s_dex, s_con, s_int, s_wis, s_cha,
         name_raw) = struct.unpack_from(_COMBAT_FMT, raw, _RDFF_HDR_SIZE)

        name = _decode_name(name_raw)
        if not name:
            continue

        # Resolve sprite via OJFF: obj_id is always -rdff_id for combat objects
        bmp_num: Optional[int] = None
        ojff_num = d.number  # abs(-rdff_id) == rdff_id
        try:
            ojff_raw = gff.get_resource_data('OJFF', ojff_num)
            if len(ojff_raw) >= _OJFF_SIZE:
                bmp_num = struct.unpack_from(_OJFF_FMT, ojff_raw)[_OJFF_BMP_IDX]
                if bmp_num == 0:
                    bmp_num = None
        except Exception:
            pass

        monsters.append({
            'rdff_id':       d.number,
            'name':          name,
            'hp':            hp,
            'psp':           psp,
            'ac':            ac,
            'thac0':         thac0,
            'move':          move,
            'special_atk':   special_atk,
            'special_def':   special_def,
            'str':           s_str,
            'dex':           s_dex,
            'con':           s_con,
            'int':           s_int,
            'wis':           s_wis,
            'cha':           s_cha,
            'icon':          icon,
            'flags':         flags,
            'bmp_num':       bmp_num,
        })

    # Sort by name, deduplicate for display (keep unique rdff_id entries)
    monsters.sort(key=lambda m: m['name'].lower())
    return monsters


# ---------------------------------------------------------------------------
# Spells  (SPIN text resources in RESOURCE.GFF)
# ---------------------------------------------------------------------------

def parse_spells(gff) -> list[dict]:
    """Return a list of spell dicts from SPIN resources.

    Each SPIN resource is a plain text description.  The conventional format
    is 'SPELL NAME: description text' — we split on the first colon.

    *gff* should be the RESOURCE.GFF GffFile object.
    """
    from gff import NoSuchResourceError

    spells = []
    spin_descs = [d for d in gff.describe_resources() if d.tag.strip() == 'SPIN']

    for d in sorted(spin_descs, key=lambda x: x.number):
        try:
            raw = gff.get_resource_data('SPIN', d.number)
        except (NoSuchResourceError, Exception):
            continue

        text = raw.rstrip(b'\x00').decode('latin-1', errors='replace').strip()
        if not text:
            continue

        # Split 'NAME: description'
        if ':' in text:
            name, _, desc = text.partition(':')
            name = name.strip()
            desc = desc.strip()
        else:
            name = f'Spell #{d.number}'
            desc = text

        spells.append({
            'number': d.number,
            'name':   name,
            'desc':   desc,
        })

    return spells
