"""
GFF file reader for Dark Sun: Shattered Lands.
Translated from GffFile.java, GffiTable.java, PrimaryGffiTable.java,
SecondaryGffiTable.java, GffFileList.java, ResourceDescriptor.java
by John Glassmyer.

GFF file layout (little-endian int32 everywhere):
  byte 12: offset to the index block (GFFI header)

Index block:
  [skip 8 bytes]
  int16: numberOfTags
  For each tag:
    4 bytes ASCII tag
    int32 numberOfChunksIfPrimary
    if > 0 -> PrimaryGffiTable starts at (current_pos - 4)
    if == 0:
      int32 (skip)
      int32 secondaryTableIndex (entry in GFFI table)
      int32 numberSegmentCount
      for each segment: int32 startResourceNumber, int32 segmentCount

PrimaryGffiTable entry (12 bytes each):
  int32 resourceNumber, int32 offset, int32 size

SecondaryGffiTable entry (8 bytes each):
  int32 offset, int32 size
  Resource numbers derived from inline numbering segments.
"""

import struct
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple


# ---------------------------------------------------------------------------
# Known chunk type descriptions
# ---------------------------------------------------------------------------
GFF_CHUNK_TYPES: Dict[str, str] = {
    # ── GFF structure ────────────────────────────────────────────────────
    'FORM': 'GFF internal chunk',
    'GFFI': 'GFFI internal chunk',
    'GFRE': 'GFF internal chunk',
    'GTOC': 'GFF internal chunk',

    # ── Items ────────────────────────────────────────────────────────────
    'IT1R': 'IT1R',

    # ── Text ─────────────────────────────────────────────────────────────
    'NAME': 'NAME',
    'TEXT': 'Text resource',
    'MERR': 'text error file',
    'ETME': 'copyright notice',
    'SPIN': 'Spell text.',

    # ── Images (all use PAL palette) ─────────────────────────────────────
    'BMP ': 'Bitmap (GIL bitmap table, 1+ bitmaps)',
    'BMAP': '3DX Bump map (GIL bitmap table, 1 bitmap)',
    'CBMP': 'Color Bit Map',
    'PORT': 'Portrait',
    'WALL': 'WALL?',
    'ICON': 'Icon resource (GIL bitmap table, 1-4 bitmaps)',
    'TILE': 'Tile graphic (GIL bitmap table, 1 bitmap)',
    'FONT': 'Font (GIL font)',
    'PAL ': 'VGA 256 color palette',
    'CPAL': 'CPAL?',
    'CMAT': 'CMAT?',

    # ── Animation ────────────────────────────────────────────────────────
    'SCMD': 'Animation script command table',
    'SJMP': 'OENGINE animation script jump table',
    'VECT': 'VECT?   rotate vector?',

    # ── Objects ──────────────────────────────────────────────────────────
    'OJFF': 'Object data?',
    'MONR': 'monsters by region ids and level',
    'PSIN': 'psionic and sphere selection',
    'PSST': 'psionc list bytes',

    # ── Map data ─────────────────────────────────────────────────────────
    'ETAB': 'Object entry table',
    'GMAP': 'Region map flags',
    'RMAP': 'Region tile map',
    'MAP ': 'Map data',
    'RDAT': 'Names?',
    'RNME': 'Region name',

    # ── Window / UI data ─────────────────────────────────────────────────
    'WIND': 'Window resource',
    'BUTN': 'Button resource',
    'APFM': 'Application frame resource',
    'EBOX': 'Edit box resource',
    'ACCL': 'Accelertor resource',
    'SBAR': 'Scroll-bar resource',
    'MENU': 'Menu resource',
    'DBOX': 'Dialog box resource',

    # ── GPL scripting ────────────────────────────────────────────────────
    'GPLI': 'Compiled GPL file ID for subs',
    'GPLX': 'GPL index file (GPLSHELL)',
    'MAS ': 'Compiled MAS (GPL master) files',
    'GPL ': 'Compiled GPL files',

    # ── Characters ───────────────────────────────────────────────────────
    'CHAR': 'saved character slot.',
    'CACT': 'valid char id (0 if entry is invalid.)',

    # ── Spell / combat status ────────────────────────────────────────────
    'SPST': 'spell list bit-mask',

    # ── Saveable object data (RDFF) ──────────────────────────────────────
    'RDFF': 'RDFF?',
    'FNFO': 'FNFO: Appears to have the object data for the game.',

    # ── Audio ────────────────────────────────────────────────────────────
    'BVOC': 'Background play samples',
    'FVOC': 'Foreground play samples',
    'GSEQ': 'GENERAL MIDI',
    'LSEQ': 'LAPC',
    'FSEQ': 'FM',
    'CSEQ': 'Clock Sequences',
    'PSEQ': 'PCSPKR',
    'MSEQ': 'XMIDI sequence files (.XMI)',
    'PLYL': 'PLYL?',
    'VPLY': 'VPLY?',
    'MGTL': 'Global timbre library',
    'SINF': 'Sound card info',

    # ── Cinematics ───────────────────────────────────────────────────────
    'ACF ': 'Cinematic Binary Script File',
    'BMA ': 'Cinematic Binary File',

    # ── Audio drivers ────────────────────────────────────────────────────
    'ADV ': 'AIL Audio Driver',
    'DADV': 'AIL and .COM drivers (MEL version 1.x only)',
    'DRV ': 'Run-time loadable code/data module',

    # ── Save entries ─────────────────────────────────────────────────────
    'STXT': 'name of the save file entry',
    'SAVE': 'save entries...',
    'POS ': 'save entry pos (not part of original engine.)',
    'ROBJ': 'save entry region (not part of original engine.)',
    'TRIG': 'save entry triggers (not part of original engine.)',
    'GDAT': 'save entry GPL data (not part of original engine.)',
    'PLAY': 'save entry player data (not part of original engine.)',
    'RENT': 'save entry entity data (not part of original engine.)',

    # ── Misc ─────────────────────────────────────────────────────────────
    'ALL ': 'ALL',
    'DATA': 'DATA?',
    'EDAT': '3DX Environment database',
    'GREQ': 'Global requirement',
    'GSEC': 'Global secondary data',
    'OMAP': 'Opacity map (GIL bitmap table, 1 bitmap)',
    'POBJ': 'PolyMesh object database',
    'PREF': 'Preferences',
    'TMAP': 'Texture map (GIL bitmap table, 1 bitmap)',
    'TXRF': 'Texture map (GIL bitmap table, 1 bitmap)',
    'CMAP': 'Color map table',
}


# ---------------------------------------------------------------------------
# ResourceDescriptor
# ---------------------------------------------------------------------------
@dataclass
class ResourceDescriptor:
    tag: str
    number: int
    offset: int
    size: int

    def __repr__(self):
        return (f"ResourceDescriptor(tag={self.tag!r}, number={self.number}, "
                f"offset=0x{self.offset:X}, size={self.size})")


# ---------------------------------------------------------------------------
# GffiTable base
# ---------------------------------------------------------------------------
class GffiTable:
    """
    Abstract base for GFF index tables.
    Backed by a mutable bytearray so offsets/sizes can be patched in place.
    """

    def __init__(self, data: bytearray, start_position: int, entry_count: int):
        self._data = data
        self._start = start_position
        self._entry_count = entry_count
        self._index_cache: Dict[int, Optional[int]] = {}

    @property
    def entry_count(self) -> int:
        return self._entry_count

    def total_size(self) -> int:
        return 4 + self._entry_count * self._entry_size()

    def get_resource_number(self, index: int) -> int:
        raise NotImplementedError

    def get_index_for_resource_number(self, resource_number: int) -> Optional[int]:
        if resource_number not in self._index_cache:
            result = None
            for i in range(self._entry_count):
                if self.get_resource_number(i) == resource_number:
                    result = i
                    break
            self._index_cache[resource_number] = result
        return self._index_cache[resource_number]

    def get_offset(self, index: int) -> int:
        assert index < self._entry_count
        pos = self._entry_position(index) + self._offset_offset()
        return struct.unpack_from('<I', self._data, pos)[0]

    def set_offset(self, index: int, offset: int):
        assert index < self._entry_count
        pos = self._entry_position(index) + self._offset_offset()
        struct.pack_into('<I', self._data, pos, offset)

    def get_size(self, index: int) -> int:
        assert index < self._entry_count
        pos = self._entry_position(index) + self._size_offset()
        return struct.unpack_from('<I', self._data, pos)[0]

    def set_size(self, index: int, size: int):
        assert index < self._entry_count
        pos = self._entry_position(index) + self._size_offset()
        struct.pack_into('<I', self._data, pos, size)

    def _entry_position(self, index: int) -> int:
        return self._start + 4 + index * self._entry_size()

    def _entry_size(self) -> int:
        raise NotImplementedError

    def _offset_offset(self) -> int:
        raise NotImplementedError

    def _size_offset(self) -> int:
        raise NotImplementedError


# ---------------------------------------------------------------------------
# PrimaryGffiTable
# ---------------------------------------------------------------------------
class PrimaryGffiTable(GffiTable):
    """
    Table where each 12-byte entry is: resourceNumber(4), offset(4), size(4).
    """

    _ENTRY_SIZE = 12
    _OFFSET_OFFSET = 4
    _SIZE_OFFSET = 8

    @classmethod
    def create(cls, data: bytearray, start_position: int) -> 'PrimaryGffiTable':
        entry_count = struct.unpack_from('<I', data, start_position)[0]
        resource_numbers = [
            struct.unpack_from('<I', data, start_position + 4 + i * cls._ENTRY_SIZE)[0]
            for i in range(entry_count)
        ]
        return cls(data, start_position, entry_count, resource_numbers)

    def __init__(self, data: bytearray, start_position: int,
                 entry_count: int, resource_numbers: List[int]):
        super().__init__(data, start_position, entry_count)
        self._resource_numbers = resource_numbers

    def get_resource_number(self, index: int) -> int:
        return self._resource_numbers[index]

    def _entry_size(self) -> int:
        return self._ENTRY_SIZE

    def _offset_offset(self) -> int:
        return self._OFFSET_OFFSET

    def _size_offset(self) -> int:
        return self._SIZE_OFFSET


# ---------------------------------------------------------------------------
# SecondaryGffiTable
# ---------------------------------------------------------------------------
class SecondaryGffiTable(GffiTable):
    """
    Table where each 8-byte entry is: offset(4), size(4).
    Resource numbers are derived from numbering segments defined inline
    in the index block.
    """

    _ENTRY_SIZE = 8

    @classmethod
    def create(cls, data: bytearray, table_offset: int,
               resource_numbering_offset: int) -> 'SecondaryGffiTable':
        entry_count = struct.unpack_from('<I', data, table_offset)[0]
        # Read numbering segments
        pos = resource_numbering_offset
        segment_count = struct.unpack_from('<I', data, pos)[0]
        pos += 4

        # sorted dict: segmentStartIndex -> segmentStartResourceNumber
        numbering: Dict[int, int] = {}
        segment_start_index = 0
        for _ in range(segment_count):
            seg_start_resource = struct.unpack_from('<I', data, pos)[0]
            pos += 4
            seg_count = struct.unpack_from('<I', data, pos)[0]
            pos += 4
            numbering[segment_start_index] = seg_start_resource
            segment_start_index += seg_count

        return cls(data, table_offset, entry_count, numbering)

    def __init__(self, data: bytearray, table_offset: int,
                 entry_count: int, numbering: Dict[int, int]):
        super().__init__(data, table_offset, entry_count)
        # Keys sorted for floor-entry lookup
        self._numbering_keys = sorted(numbering.keys())
        self._numbering = numbering

    def get_resource_number(self, index: int) -> int:
        # Find the largest key <= index (floor entry)
        floor_key = None
        for k in self._numbering_keys:
            if k <= index:
                floor_key = k
            else:
                break
        seg_start_index = floor_key
        seg_start_resource = self._numbering[seg_start_index]
        return seg_start_resource + (index - seg_start_index)

    def _entry_size(self) -> int:
        return self._ENTRY_SIZE

    def _offset_offset(self) -> int:
        return 0

    def _size_offset(self) -> int:
        return 4


# ---------------------------------------------------------------------------
# GffFile
# ---------------------------------------------------------------------------
class NoSuchResourceError(Exception):
    def __init__(self, tag: str, resource_number: int):
        super().__init__(f"no resource {tag}-{resource_number} in GFF file")
        self.tag = tag
        self.resource_number = resource_number


class GffFile:
    @classmethod
    def create(cls, gff_bytes: bytes) -> 'GffFile':
        data = bytearray(gff_bytes)
        tables = cls._create_tables(data)
        return cls(data, tables)

    @staticmethod
    def _create_tables(data: bytearray) -> Dict[str, GffiTable]:
        index_start = struct.unpack_from('<I', data, 12)[0]
        pos = index_start

        # skip 2 int32s
        pos += 8

        tables_by_tag: Dict[str, GffiTable] = {}

        # tag -> (secondaryTableIndex, resourceNumberingOffset)
        secondary_descriptors: Dict[str, Tuple[int, int]] = {}

        number_of_tags = struct.unpack_from('<H', data, pos)[0]
        pos += 2

        for _ in range(number_of_tags):
            tag = data[pos:pos+4].decode('ascii')
            pos += 4

            # Must be read as signed int32 to match Java's ByteBuffer.getInt()
            # behaviour — negative values indicate secondary (indirect) tables.
            n_chunks = struct.unpack_from('<i', data, pos)[0]

            if n_chunks > 0:
                table_start = pos  # the int32 we just peeked is entryCount
                table = PrimaryGffiTable.create(data, table_start)
                tables_by_tag[tag] = table
                pos = table_start + table.total_size()
            else:
                pos += 4  # skip n_chunks (0)
                pos += 4  # skip another int32
                secondary_table_index = struct.unpack_from('<I', data, pos)[0]
                pos += 4
                resource_numbering_offset = pos  # segments start here
                secondary_descriptors[tag] = (secondary_table_index, resource_numbering_offset)

                # skip the segments
                seg_count = struct.unpack_from('<I', data, pos)[0]
                pos += 4
                pos += seg_count * 8  # 2 x int32 per segment

        # Resolve secondary tables via the GFFI primary table
        gffi_table = tables_by_tag.get('GFFI')
        if gffi_table is not None:
            for tag, (sec_idx, numbering_offset) in secondary_descriptors.items():
                sec_table_offset = gffi_table.get_offset(sec_idx)
                tables_by_tag[tag] = SecondaryGffiTable.create(
                    data, sec_table_offset, numbering_offset)

        return tables_by_tag

    def __init__(self, data: bytearray, tables_by_tag: Dict[str, GffiTable]):
        self._data = data
        self._tables = tables_by_tag

    def describe_resources(self) -> List[ResourceDescriptor]:
        descriptors = []
        for tag, table in self._tables.items():
            for i in range(table.entry_count):
                descriptors.append(ResourceDescriptor(
                    tag=tag,
                    number=table.get_resource_number(i),
                    offset=table.get_offset(i),
                    size=table.get_size(i),
                ))
        descriptors.sort(key=lambda d: d.offset)
        return descriptors

    def has_resource(self, tag: str, resource_number: int) -> bool:
        table = self._tables.get(tag)
        if table is None:
            return False
        return table.get_index_for_resource_number(resource_number) is not None

    def get_resource_data(self, tag: str, resource_number: int) -> bytes:
        table = self._tables.get(tag)
        if table is None:
            raise NoSuchResourceError(tag, resource_number)
        idx = table.get_index_for_resource_number(resource_number)
        if idx is None:
            raise NoSuchResourceError(tag, resource_number)
        offset = table.get_offset(idx)
        size = table.get_size(idx)
        return bytes(self._data[offset:offset + size])

    def replace_resource(self, tag: str, resource_number: int,
                         replacement: bytes) -> bytes:
        """Return new GFF bytes with the named resource replaced."""
        table = self._tables.get(tag)
        if table is None:
            raise NoSuchResourceError(tag, resource_number)
        idx = table.get_index_for_resource_number(resource_number)
        if idx is None:
            raise NoSuchResourceError(tag, resource_number)

        new_size = len(replacement)
        if new_size <= table.get_size(idx):
            new_offset = table.get_offset(idx)
            new_data = bytearray(self._data)
        else:
            new_offset = len(self._data)
            new_data = bytearray(self._data) + bytearray(new_size)

        table.set_offset(idx, new_offset)
        table.set_size(idx, new_size)

        new_data[new_offset:new_offset + new_size] = replacement
        return bytes(new_data)


# ---------------------------------------------------------------------------
# GffFileList
# ---------------------------------------------------------------------------
class GffFileList:
    """Searches multiple GFF files as one logical reader."""

    def __init__(self, gff_files: List[GffFile]):
        self._files = list(gff_files)

    def describe_resources(self) -> List[ResourceDescriptor]:
        result = []
        for gff in self._files:
            result.extend(gff.describe_resources())
        return result

    def has_resource(self, tag: str, resource_number: int) -> bool:
        return any(g.has_resource(tag, resource_number) for g in self._files)

    def get_resource_data(self, tag: str, resource_number: int) -> bytes:
        for gff in self._files:
            if gff.has_resource(tag, resource_number):
                return gff.get_resource_data(tag, resource_number)
        raise NoSuchResourceError(tag, resource_number)
