"""
Overlay unpacker - flattens a Borland VROOMM (FBOV) overlaid DOS EXE into a
plain MZ image so disassemblers (Ghidra, IDA) can see the overlay code.

Each overlay stub segment in the resident image looks like:
  +00 CD 3F         int 3Fh
  +02 uint16        memswap
  +04 uint32        fileoff   (relative to FBOV header + 0x10)
  +08 uint16        codesize
  +0A uint16        relsize   (bytes of uint16 fixup offsets after the code)
  +0C uint16        nentries
  +0E uint16        prevstub
  +20 entries[n]    CD 3F <uint16 offset> 00

Overlay fixup words don't hold segment values: they hold a byte offset into
the Borland segment table (FBOV header +8 -> table of 8-byte entries
`seg, maxoff, flags, minoff`), which the overlay manager swaps for the real
segment at load time. A reference to the overlay's own stub segment means the
overlay itself unless it is the target of a far call.

The overlay code is appended to the load image (after a 64K gap so DGROUP's
BSS doesn't overlap it); each stub entry is rewritten to `JMP FAR seg:offset`,
the fixups are resolved through the segment table, and they're added to the
MZ relocation table.

Usage:
  python ovl_unpack.py --exe=DSUN.EXE --out=DSUN_FLAT.EXE [--map=segments.txt]
"""

import argparse
import struct


def _stub_entries(image, stub, ovl_len):
    """Return [(entry_pos, target_offset)] if a valid stub starts at `stub`."""
    if image[stub:stub + 2] != b'\xcd\x3f':
        return None
    fileoff, codesize, relsize, nentries = struct.unpack_from('<IHHH', image, stub + 4)
    if codesize == 0 or nentries == 0 or relsize % 2 or fileoff + codesize + relsize > ovl_len:
        return None
    entries = []
    for e in range(nentries):
        p = stub + 0x20 + 5 * e
        if image[p:p + 2] != b'\xcd\x3f':
            return None
        entries.append((p, struct.unpack_from('<H', image, p + 2)[0]))
    return entries


def segment_table(exe: bytes, img_end: int):
    """Borland segment table from the FBOV header: [(seg, maxoff, flags, minoff)].
    flags: 0 = data, 1 = code, 3 = overlay stub, 4 = stack/other."""
    _, _, tbl, count = struct.unpack_from('<4sIII', exe, img_end)
    return [struct.unpack_from('<4H', exe, tbl + 8 * i) for i in range(count)]


def unpack(exe: bytes):
    (_, last, pages, nrel, hdr_par, minalloc, maxalloc,
     ss, sp, _, ip, cs, reloff, _) = struct.unpack_from('<14H', exe, 0)
    img_end = (pages - 1) * 512 + last if last else pages * 512
    hdr = hdr_par * 16
    image = bytearray(exe[hdr:img_end])
    relocs = [struct.unpack_from('<HH', exe, reloff + 4 * i) for i in range(nrel)]

    fbov = exe[img_end:]
    if fbov[:4] != b'FBOV':
        raise ValueError('no FBOV overlay block after load image')
    ovl_data = fbov[0x10:]
    segtab = segment_table(exe, img_end)

    image.extend(b'\x00' * 0x10000)
    segments = []

    # Stubs are laid out back to back; the first is the first paragraph with a
    # plausible header whose entry list checks out.
    stub = next(s for s in range(0, img_end - hdr - 0x20, 16)
                if _stub_entries(image, s, len(ovl_data)))
    while image[stub:stub + 2] == b'\xcd\x3f':
        fileoff, codesize, relsize, nentries = struct.unpack_from('<IHHH', image, stub + 4)
        if codesize == 0 and nentries == 0:
            stub += 0x20  # empty placeholder stub
            continue
        entries = _stub_entries(image, stub, len(ovl_data))
        if not entries:
            raise ValueError(f'malformed overlay stub at image offset {stub:#x}')

        while len(image) % 16:
            image.append(0)
        new_seg = len(image) // 16
        stub_seg = stub // 16
        code_start = len(image)
        image.extend(ovl_data[fileoff:fileoff + codesize])
        for i in range(relsize // 2):
            r = struct.unpack_from('<H', ovl_data, fileoff + codesize + 2 * i)[0]
            p = code_start + r
            idx = struct.unpack_from('<H', image, p)[0]
            if idx % 8 or idx // 8 >= len(segtab):
                raise ValueError(f'overlay {stub_seg:04X}: fixup {r:#x} has bad segment index {idx:#x}')
            seg = segtab[idx // 8][0]
            if seg == stub_seg and not (r >= 3 and image[p - 3] == 0x9A):
                seg = new_seg
            struct.pack_into('<H', image, p, seg)
            relocs.append((r, new_seg))

        for p, off in entries:
            image[p] = 0xEA
            struct.pack_into('<HH', image, p + 1, off, new_seg)
            relocs.append((p + 3 - stub_seg * 16, stub_seg))
        segments.append((stub_seg, new_seg, fileoff, codesize, [o for _, o in entries]))
        stub += (0x20 + 5 * nentries + 15) & ~15

    reloc_bytes = b''.join(struct.pack('<HH', o, s) for o, s in relocs)
    hdr_size = (0x40 + len(reloc_bytes) + 15) & ~15
    total = hdr_size + len(image)
    header = bytearray(hdr_size)
    struct.pack_into('<14H', header, 0, 0x5A4D, total % 512, (total + 511) // 512,
                     len(relocs), hdr_size // 16, minalloc, maxalloc,
                     ss, sp, 0, ip, cs, 0x40, 0)
    header[0x40:0x40 + len(reloc_bytes)] = reloc_bytes
    return bytes(header) + bytes(image), segments


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[1])
    ap.add_argument('--exe', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--map', help='write stub->overlay segment map here')
    args = ap.parse_args()

    with open(args.exe, 'rb') as f:
        flat, segments = unpack(f.read())
    with open(args.out, 'wb') as f:
        f.write(flat)
    print(f'{len(segments)} overlay segments unpacked -> {args.out} ({len(flat)} bytes)')

    if args.map:
        with open(args.map, 'w') as f:
            f.write('stub_seg ovl_seg fileoff  size  entries\n')
            for stub, seg, off, size, ents in segments:
                f.write(f'{stub:04X}     {seg:04X}    {off:06X}  {size:04X}  '
                        + ' '.join(f'{e:04X}' for e in ents) + '\n')


if __name__ == '__main__':
    main()
