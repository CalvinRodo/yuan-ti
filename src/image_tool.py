"""
Image Tool - describe or convert Dark Sun image resources to PNG.
Translated from ImageTool.java by John Glassmyer.
Uses Pillow (PIL) for image output instead of the Java TIFF library.

Usage:
  # Describe frames in an image resource file:
  python image_tool.py --image=<file> --describe

  # Convert image resource to PNG (one file per frame):
  python image_tool.py --image=<file> --output-png=<base_path> --pal=<pal_file>

  # Convert all BMP resources in a GFF to PNGs:
  python image_tool.py --gff=<gff_file> --pal=<pal_file> --output-dir=<dir>

Requirements:
  pip install Pillow
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))

from image import ImageReading, Palette, ImageFrame
from gff import GffFile


def describe_image(image_bytes: bytes):
    frames = ImageReading.extract_frames(image_bytes)
    if not frames:
        print("Image has 0 frames.")
        return
    max_w = max(f.width for f in frames)
    max_h = max(f.height for f in frames)
    print(f"Image has {len(frames)} frame(s) "
          f"(max width: {max_w}, max height: {max_h})")
    for i, frame in enumerate(frames):
        print(f"  Frame {i}: {frame.width} x {frame.height}")


def frame_to_pil_image(frame: ImageFrame, palette: Palette):
    """Convert an ImageFrame + Palette to a Pillow RGBA Image."""
    try:
        from PIL import Image
    except ImportError:
        print("Error: Pillow is required.  Install with:  pip install Pillow",
              file=sys.stderr)
        sys.exit(1)

    img = Image.new('RGBA', (frame.width, frame.height), (0, 0, 0, 0))
    pixels = frame.pixels
    alpha = frame.alpha_mask

    pixel_data = []
    for i in range(frame.width * frame.height):
        if alpha[i]:
            color = palette.get_color(pixels[i] & 0xFF)
            pixel_data.append((color.red, color.green, color.blue, 255))
        else:
            pixel_data.append((0, 0, 0, 0))

    img.putdata(pixel_data)
    return img


def convert_image_to_pngs(image_bytes: bytes, palette: Palette,
                           output_base: str):
    """Save each frame of an image resource as a PNG file."""
    frames = ImageReading.extract_frames(image_bytes)
    if not frames:
        print("  No frames found.")
        return

    for i, frame in enumerate(frames):
        img = frame_to_pil_image(frame, palette)
        if len(frames) == 1:
            path = output_base if output_base.lower().endswith('.png') \
                else output_base + '.png'
        else:
            base, _ = os.path.splitext(output_base)
            path = f"{base}_{i:03d}.png"
        img.save(path)
        print(f"  Saved {path} ({frame.width}x{frame.height})")


def main():
    parser = argparse.ArgumentParser(
        description='Describe or convert Dark Sun image resources to PNG.')

    src_group = parser.add_mutually_exclusive_group(required=True)
    src_group.add_argument('--image', metavar='FILE',
                           help='Path to a raw image resource file')
    src_group.add_argument('--gff', metavar='FILE',
                           help='Path to a GFF file (extracts all BMP resources)')

    parser.add_argument('--describe', action='store_true',
                        help='Print information about the image frames')
    parser.add_argument('--output-png', metavar='PATH',
                        help='Output PNG path (used with --image)')
    parser.add_argument('--output-dir', metavar='DIR',
                        help='Output directory for PNGs (used with --gff)')
    parser.add_argument('--pal', metavar='FILE',
                        help='Path to a PAL resource file (required for conversion)')

    args = parser.parse_args()

    # ---- single image file ------------------------------------------------
    if args.image:
        if not os.path.isfile(args.image):
            print(f"Error: image file not found: {args.image}", file=sys.stderr)
            sys.exit(1)

        with open(args.image, 'rb') as f:
            image_bytes = f.read()

        if args.describe:
            describe_image(image_bytes)
            return

        if not args.output_png or not args.pal:
            parser.error("--output-png and --pal are required for conversion")

        with open(args.pal, 'rb') as f:
            palette = Palette.from_pal_data(f.read())

        convert_image_to_pngs(image_bytes, palette, args.output_png)

    # ---- GFF file: extract all BMP resources ------------------------------
    elif args.gff:
        if not os.path.isfile(args.gff):
            print(f"Error: GFF file not found: {args.gff}", file=sys.stderr)
            sys.exit(1)

        if not args.pal:
            parser.error("--pal is required when using --gff")

        if not args.output_dir and not args.describe:
            parser.error("--output-dir or --describe is required when using --gff")

        with open(args.gff, 'rb') as f:
            gff_bytes = f.read()
        gff = GffFile.create(gff_bytes)

        with open(args.pal, 'rb') as f:
            palette = Palette.from_pal_data(f.read())

        # Find BMP and TILE resources
        image_tags = {'BMP ', 'TILE'}
        descriptors = [d for d in gff.describe_resources()
                       if d.tag in image_tags]

        if not descriptors:
            print("No image resources (BMP, TILE) found in GFF.")
            return

        if args.describe:
            for d in descriptors:
                print(f"\n{d.tag.rstrip()}-{d.number}:")
                data = gff.get_resource_data(d.tag, d.number)
                describe_image(data)
            return

        os.makedirs(args.output_dir, exist_ok=True)
        for d in descriptors:
            data = gff.get_resource_data(d.tag, d.number)
            base_name = f"{d.tag.rstrip()}-{d.number}"
            base_path = os.path.join(args.output_dir, base_name)
            print(f"{base_name}:")
            try:
                convert_image_to_pngs(data, palette, base_path)
            except Exception as e:
                print(f"  Error: {e}")


if __name__ == '__main__':
    main()
