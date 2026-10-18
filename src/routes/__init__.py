# Route blueprints for the Dark Sun GFF viewer Flask app.
# Each submodule registers a Blueprint that is imported in server.py.
#
# Ensure the parent src/ directory is on sys.path so every blueprint can
# import core modules (gff, config, gff_cache, …) without relative imports.

import os
import sys

_SRC_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _SRC_DIR not in sys.path:
    sys.path.insert(0, _SRC_DIR)
