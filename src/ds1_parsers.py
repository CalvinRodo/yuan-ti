"""
Parsers for Dark Sun 1/2 game-data tables embedded in GFF files.

Three data sources are supported:

  items     — IT1R item types (GPLDATA.GFF), named from the item records
              (SEGOBJEX.GFF RDFF type 1) and NAME
  monsters  — RDFF combat objects (type 2) in SEGOBJEX.GFF / OBJEX.GFF
  spells    — SPIN text resources in RESOURCE.GFF

All public functions receive a loaded GffFile object (or a dict of them) and
return plain Python lists-of-dicts suitable for jsonify().
"""

import os
import struct

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

# IT1R placement (+9), named from the items that use each value; same as
# DarkSun.Data.Items.ItemPlacement.
_SLOT_NAMES = [
    'None', 'Chest', 'Waist', 'Arms', 'Feet', 'Hand', 'Head', 'Neck',
    'Cloak', 'Finger', 'Legs', 'Ammunition', 'Missile weapon',
]

# ds1_item_t (21 bytes): the item record DSUN.EXE copies from an RDFF's first
# entry. id i16, quantity u16, next i16, value u16, pack_index i16,
# item_index i16 (IT1R), icon i16, charges u8, flags u8, special u8, slot u8,
# name_idx u16 (NAME), bonus i8.
_ITEM_FMT = '<hHhHhhhBBBBHb'
_ITEM_FIELDS = ['id', 'quantity', 'next', 'value', 'pack_index', 'item_index', 'icon',
                'charges', 'flags', 'special', 'slot', 'name_idx', 'bonus']

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

def item_name(names: bytes, name_idx: int, bonus: int) -> str:
    """An item's name as DSUN.EXE shows it: NAME entry, "%+d name" with a bonus."""
    if name_idx >= 9999 or name_idx * _NAME_SIZE >= len(names):
        return ''
    name = names[name_idx * _NAME_SIZE:(name_idx + 1) * _NAME_SIZE].split(b'\x00')[0].decode('latin-1')
    return f'{bonus:+d} {name}' if bonus else name


def rdff_entries(raw: bytes, with_from: bool = False) -> list[tuple]:
    """An RDFF's entries: (load_action, type, index, data), up to the 0xFF end byte.

    Every load action is followed by 'len' bytes of data (0 for references).
    DSUN.EXE's loader (7155:0000): 1 the object's own record; 2 a new object
    stored in field 'from' of block 'blocknum' (a creature's first carried
    item); 3 a reference (a character record, type 4, or field 'from' set to
    'index': an item's IT1R type is field 4, its NAME field 2); 4 a new object
    linked after block 'blocknum' (the next carried item). With *with_from*,
    entries are (load_action, type, index, from, data). Same as
    DarkSun.Data.Gff.Rdff.
    """
    entries, pos = [], 0
    while pos + _RDFF_HDR_SIZE <= len(raw) and raw[pos] != 0xFF:
        load_action, _bnum, rtype, idx, frm, rlen = struct.unpack_from(_RDFF_HDR_FMT, raw, pos)
        if not 1 <= load_action <= 4 or rlen < 0 or pos + _RDFF_HDR_SIZE + rlen > len(raw):
            break
        data = raw[pos + _RDFF_HDR_SIZE:pos + _RDFF_HDR_SIZE + rlen]
        entries.append((load_action, rtype, idx, frm, data) if with_from else (load_action, rtype, idx, data))
        pos += _RDFF_HDR_SIZE + rlen
    return entries


# Character record (RDFF type 4, 0x47 bytes; dsun-exe-reference.md §8): +4
# int32 experience for killing it; +8 hit points (scaled by difficulty, and
# made the combat record's when loaded); +0x18 race; +0x21-0x23 class codes,
# +0x24-0x26 levels; +0x27 base AC; +0x2A attacks a round in halves;
# +0x2B/+0x2E/+0x31/+0x34 + i natural attack i (halves, dice, sides, bonus);
# +0x37-0x3B saves; +0x3D size (footprint: half-width size & 7, height
# size // 8); +0x3E spell set (3FB9:0110); +0x46 psionic set (37F0:001C).
CHARACTER_RECORD_SIZE = 0x47


def character_record(raw: bytes):
    """The character record of an RDFF resource, or None."""
    for _action, rtype, _idx, data in rdff_entries(raw):
        if rtype == 4 and len(data) >= CHARACTER_RECORD_SIZE:
            return data[:CHARACTER_RECORD_SIZE]
    return None


def parse_item_records(objex, gpldata) -> list[dict]:
    """Every item RDFF (first entry an inline type-1 record) in SEGOBJEX.GFF, with its name."""
    try:
        names = gpldata.get_resource_data('NAME', 1)
    except Exception:
        names = b''
    items = []
    for d in objex.describe_resources():
        if d.tag != 'RDFF':
            continue
        raw = objex.get_resource_data('RDFF', d.number)
        if len(raw) < _RDFF_HDR_SIZE + 21:
            continue
        load_action, _bnum, rtype, _idx, _from, rlen = struct.unpack_from(_RDFF_HDR_FMT, raw, 0)
        if load_action != 1 or rtype != GFF_ITEM_OBJECT or rlen < 21:
            continue
        rec = dict(zip(_ITEM_FIELDS, struct.unpack_from(_ITEM_FMT, raw, _RDFF_HDR_SIZE)))
        rec['rdff'] = d.number
        rec['name'] = item_name(names, rec['name_idx'], rec['bonus'])
        rec['base_name'] = item_name(names, rec['name_idx'], 0)
        items.append(rec)
    return items


def parse_items(gff, objex=None) -> list[dict]:
    """Return the item types (IT1R in GPLDATA.GFF).

    Item types have no names of their own: names belong to the item records
    (an RDFF's NAME number), so with *objex* (SEGOBJEX.GFF) each type is
    named after the items of that type ('name': the most common name
    without a bonus, 'names': every name, with bonuses as the game shows them). Returns [] if the GFF has no IT1R.
    """
    from collections import Counter
    from gff import NoSuchResourceError

    try:
        it1r_data = gff.get_resource_data('IT1R', 1)
    except (NoSuchResourceError, Exception):
        return []

    used, base = {}, {}
    if objex is not None:
        for rec in parse_item_records(objex, gff):
            used.setdefault(rec['item_index'], Counter())[rec['name']] += 1
            base.setdefault(rec['item_index'], Counter())[rec['base_name']] += 1

    items = []
    for i in range(len(it1r_data) // _ITEM1R_SIZE):
        raw = struct.unpack_from(_ITEM1R_FMT, it1r_data, i * _ITEM1R_SIZE)
        d   = dict(zip(_ITEM1R_FIELDS, raw))
        names = [n for n, _ in used.get(i, Counter()).most_common() if n]
        base_names = [n for n, _ in base.get(i, Counter()).most_common() if n]

        damage = ''
        if d['dice'] > 0 and d['sides'] > 0:
            damage = f"{d['dice']}d{d['sides']}"
            if d['mod'] > 0:
                damage += f'+{d["mod"]}'
            elif d['mod'] < 0:
                damage += str(d['mod'])

        placement = d['placement']
        items.append({
            'index':       i,
            'name':        base_names[0] if base_names else '',
            'names':       names,
            'weapon_type': _weapon_type_label(d['weapon_type']),
            'slot':        _SLOT_NAMES[placement] if placement < len(_SLOT_NAMES) else str(placement),
            'damage':      damage,
            'weight':      d['weight'],
            'base_AC':     d['base_AC'],
            'num_attacks': d['num_attacks'],
            'range':       d['range'],
            'material':    d['material'],
            'flags':       d['flags'],
            'legal_class': d['legal_class'],
            # PORT resources in GPLDATA.GFF are 1-indexed relative to IT1R order (unverified)
            'port_num':    i + 1,
        })

    return items


# ---------------------------------------------------------------------------
# Monsters  (RDFF combat objects in SEGOBJEX.GFF / OBJEX.GFF)
# ---------------------------------------------------------------------------

def carried_items(raw: bytes) -> list[dict]:
    """The items a creature RDFF carries: each item record (load action 2 or 4)
    with the IT1R type and NAME its references set; slot 255 is the pack."""
    items = []
    for action, rtype, idx, frm, data in rdff_entries(raw, with_from=True):
        if action in (2, 4) and rtype == GFF_ITEM_OBJECT and len(data) >= struct.calcsize(_ITEM_FMT):
            rec = dict(zip(_ITEM_FIELDS, struct.unpack_from(_ITEM_FMT, data)))
            items.append({'rdff_id': -rec['id'], 'type': rec['item_index'], 'name_idx': rec['name_idx'], 'slot': rec['slot']})
        elif action == 3 and items and rtype == 5 and frm == 4:
            items[-1]['type'] = idx
        elif action == 3 and items and rtype == 3 and frm == 2:
            items[-1]['name_idx'] = idx
    return items


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
        bmp_num: int | None = None
        ojff_num = d.number  # abs(-rdff_id) == rdff_id
        try:
            ojff_raw = gff.get_resource_data('OJFF', ojff_num)
            if len(ojff_raw) >= _OJFF_SIZE:
                bmp_num = struct.unpack_from(_OJFF_FMT, ojff_raw)[_OJFF_BMP_IDX]
                if bmp_num == 0:
                    bmp_num = None
        except Exception:
            pass

        character = character_record(raw)
        size = character[0x3D] if character else 0
        monsters.append({
            'rdff_id':       d.number,
            'name':          name,
            'size':          size,
            'footprint':     f'{(size & 7) * 2 + 1}x{size // 8 + 1}',
            # DSUN.EXE uses the character record's hit points (7155:0000).
            'hp':            struct.unpack_from('<H', character, 8)[0] if character else hp,
            'combat_hp':     hp,
            'xp_value':      struct.unpack_from('<i', character, 4)[0] if character else 0,
            'mind':          _data,
            'allegiance':    allegiance,
            'spell_set':     character[0x3E] if character else 0,
            'psionic_set':   character[0x46] if character else 0,
            'natural_attacks': [
                {'halves': character[0x2B + i], 'dice': character[0x2E + i],
                 'sides': character[0x31 + i], 'bonus': struct.unpack_from('<b', character, 0x34 + i)[0]}
                for i in range(3) if character and character[0x2B + i]
            ],
            'carried':       carried_items(raw),
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
