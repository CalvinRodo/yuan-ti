#!/usr/bin/env bash

set -euo pipefail

# Enable case-insensitive matching and avoid literal "*.gff" if no matches
shopt -s nullglob nocaseglob

INPUT_DIR="."
OUTPUT_ROOT="./out"
SCRIPT="./src/gff_tool.py"

mkdir -p "$OUTPUT_ROOT"

for gff in "$INPUT_DIR"/*.GFF; do
    # Remove directory path
    filename="$(basename "$gff")"

    # Remove extension (.gff or .GFF)
    base="${filename%.*}"

    outdir="$OUTPUT_ROOT/$base"

    echo "Processing: $gff"
    echo "Output dir:  $outdir"

    mkdir -p "$outdir"

    python3 "$SCRIPT" \
        --gff="$gff" \
        --extract-to-dir="$outdir"

    echo "Done: $filename"
    echo "-----------------------------"
done

echo "All files processed."