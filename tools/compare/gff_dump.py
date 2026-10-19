"""Dump GFF resources, image frames and palettes as hashes.

Reference side of `make engine-compare`: the C# engine's
engine/tools/DarkSun.Dump writes the same format from its port of
src/gff.py and src/image.py, and the two outputs must be identical.

Usage: gff_dump.py <game-dir> <out.tsv>
"""
import hashlib
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'src'))

from gff import GffFile  # noqa: E402
from image import ImageReading, Palette  # noqa: E402
from region_renderer import render_region  # noqa: E402

IMAGE_TAGS = ['BMP ', 'CBMP', 'TILE', 'ICON', 'PORT', 'WALL', 'FONT']
PALETTE_TAGS = ['PAL ', 'CPAL']


def sha1(data: bytes) -> str:
    return hashlib.sha1(data).hexdigest()


def main(game_dir: str, out_path: str):
    names = sorted(n for n in os.listdir(game_dir) if n.endswith('.GFF'))
    def load(name):
        with open(os.path.join(game_dir, name), 'rb') as f:
            return GffFile.create(f.read())

    aux = [load('SEGOBJEX.GFF'), load('GPLDATA.GFF')]
    resource = load('RESOURCE.GFF')
    first_pal = next(r.number for r in resource.describe_resources() if r.tag == 'PAL ')
    with open(out_path, 'w', newline='\n') as out:
        for name in names:
            with open(os.path.join(game_dir, name), 'rb') as f:
                gff = GffFile.create(f.read())
            resources = gff.describe_resources()
            for r in resources:
                data = gff.get_resource_data(r.tag, r.number)
                out.write(f'R\t{name}\t{r.tag}\t{r.number}\t{r.offset}\t{r.size}\t{sha1(data)}\n')
            for r in resources:
                if r.tag not in IMAGE_TAGS:
                    continue
                prefix = f'I\t{name}\t{r.tag}\t{r.number}'
                try:
                    lines = []
                    frames = ImageReading.extract_frames(gff.get_resource_data(r.tag, r.number))
                    for i, fr in enumerate(frames):
                        n = fr.width * fr.height
                        pixels = bytes(fr.pixels)
                        mask = bytes(fr.alpha_mask)
                        if len(pixels) != n:
                            raise ValueError('pixel buffer grew past the frame')
                        lines.append(f'{prefix}\t{i}\t{fr.width}x{fr.height}\t{sha1(pixels + mask)}\n')
                    out.writelines(lines)
                except Exception:
                    out.write(f'{prefix}\t-\terror\n')
            for r in resources:
                if r.tag not in PALETTE_TAGS:
                    continue
                prefix = f'P\t{name}\t{r.tag}\t{r.number}'
                try:
                    p = Palette.from_pal_data(gff.get_resource_data(r.tag, r.number))
                    rgb = bytes(v for i in range(len(p))
                                for c in [p.get_color(i)] for v in (c.red, c.green, c.blue))
                    out.write(f'{prefix}\t{len(p)}\t{sha1(rgb)}\n')
                except Exception:
                    out.write(f'{prefix}\terror\n')
            if name.startswith('RGN'):
                # Region palette: the GPLDATA.GFF PAL numbered like the GMAP;
                # region 255 (the test map) uses PAL 1; else the first PAL in
                # RESOURCE.GFF.
                try:
                    number = next(r.number for r in resources if r.tag == 'GMAP')
                    if number == 255 and not aux[1].has_resource('PAL ', number):
                        number = 1
                    if aux[1].has_resource('PAL ', number):
                        palette = Palette.from_pal_data(aux[1].get_resource_data('PAL ', number))
                    else:
                        palette = Palette.from_pal_data(resource.get_resource_data('PAL ', first_pal))
                    img = render_region(gff, aux, palette)
                    out.write(f'M\t{name}\t{img.width}x{img.height}\t{sha1(img.tobytes())}\n')
                except Exception:
                    out.write(f'M\t{name}\terror\n')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
