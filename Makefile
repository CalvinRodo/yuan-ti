# Dark Sun GFF Toolkit
# Run 'make' or 'make help' to list available commands.

PYTHON   := uv run python3
SRC      := src
GAME_DIR := dark_sun
OUT_DIR  := out

.DEFAULT_GOAL := help

.PHONY: help serve install install-ffmpeg extract list-resource list-gff extract-gff \
        precompute precompute-gff precompute-maps precompute-gamedata \
        precompute-images precompute-audio \
        phaser-install phaser-dev phaser-build

help: ## Show this help message
	@echo ""
	@echo "  Dark Sun GFF Toolkit"
	@echo ""
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \
	  | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'
	@echo ""
	@echo "  Parameterised targets accept overrides, e.g.:  make list-gff GFF=RGN02.GFF"
	@echo ""

# ---------------------------------------------------------------------------

serve: ## Start the Flask web viewer  →  http://localhost:5000
	$(PYTHON) $(SRC)/server.py

install: ## Sync Python deps with uv (creates .venv)
	uv sync

install-ffmpeg: ## Install ffmpeg (required for VOC → MP3 audio conversion)
	sudo apt-get update -qq && sudo apt-get install -y ffmpeg
	@echo "ffmpeg installed: $$(ffmpeg -version 2>&1 | head -1)"

extract: ## Extract ALL GFF files to ./out/<gff-name>/
	bash extract.sh

# ---------------------------------------------------------------------------
# Pre-computation (build disk cache for the server)
# ---------------------------------------------------------------------------

PC_GAME  ?= all
PC_FORCE ?=

precompute: ## Pre-compute ALL steps for all games → precomputed/
	$(PYTHON) $(SRC)/precompute.py --game=$(PC_GAME) --step=all $(if $(PC_FORCE),--force,)

precompute-gff: ## Pre-compute GFF resource manifests only
	$(PYTHON) $(SRC)/precompute.py --game=$(PC_GAME) --step=gff $(if $(PC_FORCE),--force,)

precompute-maps: ## Pre-compute region map tiles + PNG renders only
	$(PYTHON) $(SRC)/precompute.py --game=$(PC_GAME) --step=maps $(if $(PC_FORCE),--force,)

precompute-gamedata: ## Pre-compute items / monsters / spells JSON only
	$(PYTHON) $(SRC)/precompute.py --game=$(PC_GAME) --step=gamedata $(if $(PC_FORCE),--force,)

precompute-images: ## Pre-compute all image resources as PNGs        [PC_GAME=, PC_FORCE=1]
	$(PYTHON) $(SRC)/precompute.py --game=$(PC_GAME) --step=images $(if $(PC_FORCE),--force,)

precompute-audio: ## Pre-compute audio/music/video (VOC MP3 XMI MIDI FLI MP4)
	$(PYTHON) $(SRC)/precompute.py --game=$(PC_GAME) --step=audio $(if $(PC_FORCE),--force,)

# ---------------------------------------------------------------------------

GFF ?= RESOURCE.GFF

list-resource: ## List resources in RESOURCE.GFF
	$(PYTHON) $(SRC)/gff_tool.py --gff=$(GAME_DIR)/RESOURCE.GFF --list-contents

list-gff: ## List resources in one GFF file       [GFF=<name>]
	$(PYTHON) $(SRC)/gff_tool.py --gff=$(GAME_DIR)/$(GFF) --list-contents

extract-gff: ## Extract one GFF file to ./out/    [GFF=<name>]
	$(PYTHON) $(SRC)/gff_tool.py \
	    --gff=$(GAME_DIR)/$(GFF) \
	    --extract-to-dir=$(OUT_DIR)/$(basename $(GFF))

# ---------------------------------------------------------------------------
# Phaser application (frontend)
# ---------------------------------------------------------------------------

phaser-install: ## Install Phaser app npm dependencies
	cd phaser-app && npm install

phaser-dev: ## Start the Phaser dev server (Vite) → http://localhost:5173
	cd phaser-app && npm run dev

phaser-build: ## Build the Phaser app for production → phaser-app/dist/
	cd phaser-app && npm run build
