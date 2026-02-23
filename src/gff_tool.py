"""
GFF Tool - list, extract, and replace resources in Dark Sun GFF files.
Translated from GffTool.java by John Glassmyer.

Usage:
  # List all resources in a GFF file:
  python gff_tool.py --gff=<file> --list-contents

  # Extract all resources to a directory:
  python gff_tool.py --gff=<file> --extract-to-dir=<dir>

  # Replace a resource in a GFF file:
  python gff_tool.py --gff=<file> --replace-with=<file> --tag=TAG --number=N
"""

import argparse
import os
import sys

# Allow running from the src directory directly
sys.path.insert(0, os.path.dirname(__file__))

from gff import GffFile, ResourceDescriptor


def list_contents(gff_file: GffFile, file_size: int):
    descriptors = gff_file.describe_resources()
    print("Listing contents.")
    print(f"  {'offset':<10} {'tag':<6} {'number':>6} {'size':>10}")
    print(f"  {'-'*8}  {'-'*4}  {'-'*6}  {'-'*8}")

    last_end = 0
    for d in descriptors:
        if last_end < d.offset:
            print(f"  0x{last_end:06X}  ....  ......  {d.offset - last_end:>8d}  (gap)")
        print(f"  0x{d.offset:06X}  {d.tag:>4}  {d.number:>6}  {d.size:>8d}")
        last_end = d.offset + d.size

    if last_end < file_size:
        print(f"  0x{last_end:06X}  ....  ......  {file_size - last_end:>8d}  (gap/trailer)")


def extract_all_resources(gff_file: GffFile, output_dir: str):
    print(f"Extracting all resources to directory: {output_dir}")
    os.makedirs(output_dir, exist_ok=True)

    descriptors = gff_file.describe_resources()

    # Work out digit padding per tag so filenames sort nicely
    max_digits: dict = {}
    for d in descriptors:
        digits = len(str(d.number))
        max_digits[d.tag] = max(max_digits.get(d.tag, 0), digits)

    for d in descriptors:
        pad = max_digits[d.tag]
        filename = f"{d.tag.rstrip()}-{d.number:0{pad}d}"
        out_path = os.path.join(output_dir, filename)
        data = gff_file.get_resource_data(d.tag, d.number)
        with open(out_path, 'wb') as f:
            f.write(data)
        print(f"  Wrote {out_path} ({len(data)} bytes)")


def replace_resource(gff_path: str, gff_file: GffFile,
                     tag: str, number: int, replacement_path: str):
    with open(replacement_path, 'rb') as f:
        replacement = f.read()

    new_bytes = gff_file.replace_resource(tag, number, replacement)

    with open(gff_path, 'wb') as f:
        f.write(new_bytes)

    print(f"Replaced {tag}-{number} in {gff_path} with {replacement_path} "
          f"({len(replacement)} bytes)")


def main():
    parser = argparse.ArgumentParser(
        description='List, extract, or replace resources in a Dark Sun GFF file.')
    parser.add_argument('--gff', required=True,
                        help='Path to the GFF file')
    parser.add_argument('--list-contents', action='store_true',
                        help='List all resources in the GFF')
    parser.add_argument('--extract-to-dir', metavar='DIR',
                        help='Extract all resources to this directory')
    parser.add_argument('--replace-with', metavar='FILE',
                        help='Replace a resource with the contents of this file')
    parser.add_argument('--tag', help='4-char tag of resource to replace')
    parser.add_argument('--number', type=int,
                        help='Resource number to replace')

    args = parser.parse_args()

    if not os.path.isfile(args.gff):
        print(f"Error: GFF file not found: {args.gff}", file=sys.stderr)
        sys.exit(1)

    commands = sum([
        bool(args.list_contents),
        bool(args.extract_to_dir),
        bool(args.replace_with),
    ])
    if commands != 1:
        parser.error("Specify exactly one of --list-contents, "
                     "--extract-to-dir, or --replace-with")

    if args.replace_with and not (args.tag and args.number is not None):
        parser.error("--replace-with requires --tag and --number")

    with open(args.gff, 'rb') as f:
        gff_bytes = f.read()

    gff_file = GffFile.create(gff_bytes)

    if args.list_contents:
        list_contents(gff_file, len(gff_bytes))

    if args.extract_to_dir:
        extract_all_resources(gff_file, args.extract_to_dir)

    if args.replace_with:
        # Pad tag to 4 chars if needed
        tag = args.tag.ljust(4)[:4]
        replace_resource(args.gff, gff_file, tag, args.number, args.replace_with)


if __name__ == '__main__':
    main()
