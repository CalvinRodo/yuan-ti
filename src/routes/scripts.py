"""
GPL script routes.

Endpoints
---------
GET /api/scripts                         – list all GPL/MAS scripts in GPLDATA.GFF
GET /api/script/<tag_url>/<number>       – disassemble a single GPL or MAS script
"""

from flask import Blueprint, jsonify, abort, request

from gff import NoSuchResourceError
from config import DEFAULT_GAME, tag_to_url_seg, url_seg_to_tag
from gff_cache import load_gff
from gpl_parser import disassemble_gpl, field_types_from

bp = Blueprint('scripts', __name__)

# Cache: 'scripts:<game_id>' → list of script descriptors
_script_list_cache: dict[str, list] = {}


@bp.route('/api/scripts')
def api_scripts():
    """List all GPL and MAS scripts in GPLDATA.GFF for the requested game."""
    game_id = request.args.get('game', DEFAULT_GAME)
    cache_key = f'scripts:{game_id}'

    if cache_key not in _script_list_cache:
        try:
            gff     = load_gff('GPLDATA.GFF', game_id)
            scripts = []
            for d in gff.describe_resources():
                tag = d.tag.strip()
                if tag in ('GPL', 'MAS'):
                    scripts.append({
                        'tag':     tag,
                        'tag_url': tag_to_url_seg(d.tag),
                        'number':  d.number,
                        'size':    d.size,
                        'label':   f'{tag} #{d.number}',
                    })
            scripts.sort(key=lambda x: (x['tag'], x['number']))
            _script_list_cache[cache_key] = scripts
        except Exception as exc:
            return jsonify({'error': str(exc)}), 500

    return jsonify(_script_list_cache[cache_key])


@bp.route('/api/script/<tag_url>/<int:number>')
def api_script_disasm(tag_url: str, number: int):
    """Disassemble a single GPL or MAS script and return the instruction list."""
    game_id = request.args.get('game', DEFAULT_GAME)
    tag     = url_seg_to_tag(tag_url)

    try:
        gff  = load_gff('GPLDATA.GFF', game_id)
        data = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)
    except Exception as exc:
        return jsonify({'error': str(exc)}), 500

    try:
        instructions = disassemble_gpl(bytes(data), field_types_from(gff))
    except Exception as exc:
        return jsonify({'error': str(exc)}), 500

    return jsonify(instructions)
