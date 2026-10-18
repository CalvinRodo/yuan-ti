"""
Text and spell resource routes.

Endpoints
---------
GET /api/gff/<name>/texts             – all TEXT / MERR / SPIN / NAME resources decoded
GET /api/gff/<name>/spells            – SPIN resources with icon lookup metadata
"""

from flask import Blueprint, jsonify, request

from config import (
    DEFAULT_GAME, TEXT_TAGS,
    SPIN_ICON_OFFSET,
    PSIONICS_SPIN_START, PSIONICS_SPIN_END, PSIONICS_ICON_BASE,
    TURN_UNDEAD_SPIN, TURN_UNDEAD_VARIANTS,
)
from gff_cache import load_gff
from text_parsers import decode_text, parse_name_resource

bp = Blueprint('text', __name__)


@bp.route('/api/gff/<name>/texts')
def api_texts(name: str):
    """Return decoded text content for all TEXT, MERR, SPIN, and NAME resources.

    Each entry: {tag, number, size, content}
    For NAME resources, content is a list of {text, path, sep_hex, …} entry dicts.
    For all other tags, content is a plain string.
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    gff     = load_gff(name, game_id)
    result  = []

    for d in gff.describe_resources():
        if d.tag.strip() not in TEXT_TAGS:
            continue
        try:
            raw = gff.get_resource_data(d.tag, d.number)
            if d.tag.strip() == 'NAME':
                content = parse_name_resource(raw)
            else:
                content = decode_text(raw)
        except Exception:
            content = [] if d.tag.strip() == 'NAME' else ''

        result.append({
            'tag':     d.tag.strip(),
            'number':  d.number,
            'size':    d.size,
            'content': content,
        })

    return jsonify(result)


@bp.route('/api/gff/<name>/spells')
def api_spells_gff(name: str):
    """Return parsed SPIN resources with icon lookup metadata.

    Icon lookup rules
    -----------------
    * SPIN 1–137, 173–248           → ICON (SPIN_ICON_OFFSET + spin_number)
    * SPIN 139–172 (psionics)       → ICON (3000 + spin_number − 139)
    * SPIN 1000 (Turn Undead)       → expanded to 4 elemental domain variants
    * All others                    → no icon
    """
    game_id      = request.args.get('game', DEFAULT_GAME)
    gff          = load_gff(name, game_id)
    icon_numbers = {
        d.number for d in gff.describe_resources() if d.tag.strip() == 'ICON'
    }
    result = []

    for d in gff.describe_resources():
        if d.tag.strip() != 'SPIN':
            continue

        try:
            raw  = gff.get_resource_data('SPIN', d.number)
            text = raw.decode('latin-1').strip()
        except Exception:
            text = ''

        if ': ' in text:
            spell_name, desc = text.split(': ', 1)
        elif ':' in text:
            spell_name, desc = text.split(':', 1)
        else:
            spell_name, desc = text, ''
        spell_name = spell_name.strip()
        desc       = desc.strip()

        # Turn Undead expands to one card per elemental domain
        if d.number == TURN_UNDEAD_SPIN:
            for domain, icon_n in TURN_UNDEAD_VARIANTS:
                result.append({
                    'number':      icon_n,
                    'name':        f'TURN UNDEAD - {domain}',
                    'description': desc,
                    'icon_number': icon_n if icon_n in icon_numbers else None,
                })
            continue

        # Psionic powers use a different icon block
        if PSIONICS_SPIN_START <= d.number <= PSIONICS_SPIN_END:
            icon_n = PSIONICS_ICON_BASE + (d.number - PSIONICS_SPIN_START)
        else:
            icon_n = SPIN_ICON_OFFSET + d.number

        result.append({
            'number':      d.number,
            'name':        spell_name,
            'description': desc,
            'icon_number': icon_n if icon_n in icon_numbers else None,
        })

    result.sort(key=lambda r: r['name'])
    return jsonify(result)
