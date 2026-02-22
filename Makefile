# Dark Sun GFF Toolkit
# Run 'make' or 'make help' to list available commands.

VENV     := .venv
PYTHON   := $(VENV)/bin/python3
SRC      := src
GAME_DIR := dark_sun
OUT_DIR  := out

.DEFAULT_GOAL := help

.PHONY: help serve install extract list-resource list-gff extract-gff

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

install: ## Create .venv and install Python deps (flask, pillow)
	python3 -m venv $(VENV)
	$(PYTHON) -m pip install flask pillow

extract: ## Extract ALL GFF files to ./out/<gff-name>/
	bash extract.sh

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
