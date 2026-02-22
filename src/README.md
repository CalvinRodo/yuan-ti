cd "/media/calvin/EVO SDD/games/Dark Sun/src"

# List resources in a GFF file
python gff_tool.py --gff=../RESOURCE.GFF --list-contents

# Extract all resources to a directory
python gff_tool.py --gff=../RESOURCE.GFF --extract-to-dir=./out

# Replace a resource
python gff_tool.py --gff=../RESOURCE.GFF --replace-with=<file> --tag=TAG --number=N


pip install Pillow

# Describe frames in an image resource
python image_tool.py --image=<file> --describe

# Convert to PNG (one file per frame)
python image_tool.py --image=<file> --output-png=<base_path> --pal=<pal_file>

# Convert all BMP resources in a GFF to PNGs
python image_tool.py --gff=../RESOURCE.GFF --pal=<pal_file> --output-dir=./out