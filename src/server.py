"""
Dark Sun GFF Viewer — Flask application entry point.

Registers all route blueprints and starts the development server.

Usage:
    uv run python src/server.py   →   http://localhost:5000
"""

import os
import sys

# Make every module in src/ importable from route subpackages as well.
SRC_DIR = os.path.dirname(os.path.abspath(__file__))
BASE_DIR = os.path.dirname(SRC_DIR)
sys.path.insert(0, SRC_DIR)

from flask import Flask, send_from_directory

from config import WEB_DIR, GAMES

# ---------------------------------------------------------------------------
# Flask application
# ---------------------------------------------------------------------------

# Prefer the built Phaser app if it exists; otherwise fall back to the
# original vanilla-JS frontend in src/web/.
PHASER_DIST = os.path.join(BASE_DIR, 'phaser-app', 'dist')
STATIC_DIR = PHASER_DIST if os.path.isdir(PHASER_DIST) else WEB_DIR

app = Flask(__name__, static_folder=STATIC_DIR, static_url_path='')

# ---------------------------------------------------------------------------
# Register route blueprints
# ---------------------------------------------------------------------------

from routes import gff, images, maps, text, audio, cinematics, gamedata, scripts, sprites

app.register_blueprint(gff.bp)
app.register_blueprint(images.bp)
app.register_blueprint(maps.bp)
app.register_blueprint(text.bp)
app.register_blueprint(audio.bp)
app.register_blueprint(cinematics.bp)
app.register_blueprint(gamedata.bp)
app.register_blueprint(scripts.bp)
app.register_blueprint(sprites.bp)


@app.route('/')
def index():
    return send_from_directory(STATIC_DIR, 'index.html')


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == '__main__':
    print(f"Games          : {', '.join(GAMES.keys())}")
    print(f"Web directory  : {STATIC_DIR}")
    print(f"Open           : http://localhost:5000")
    app.run(debug=True, port=5000)
