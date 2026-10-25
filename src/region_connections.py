"""Static region connections from startup scripts, calls and registered triggers."""

import re

from gpl_parser import disassemble_gpl, field_types_from


def _arguments(text):
    parts, start, depth = [], 0, 0
    for index, char in enumerate(text):
        if char in '([':
            depth += 1
        elif char in ')]':
            depth -= 1
        elif char == ',' and depth == 0:
            parts.append(text[start:index].strip())
            start = index + 1
    parts.append(text[start:].strip())
    return parts


def _number(text):
    if re.fullmatch(r'@[0-9a-fA-F]+', text):
        return int(text[1:], 16)
    if re.fullmatch(r'-?\d+', text):
        return int(text)
    return None


def region_connections(gff, nodes: list[dict]) -> dict:
    """Return possible directed party transitions; conditions are not evaluated."""
    field_types = field_types_from(gff)
    scripts, problems = {}, set()
    edges = {}

    def script(tag, number):
        key = (tag, number)
        if key not in scripts:
            if not gff.has_resource(tag, number):
                problems.add(f'Missing {tag.strip()} {number}')
                scripts[key] = {}
            else:
                rows = disassemble_gpl(gff.get_resource_data(tag, number), field_types)
                instructions = {}
                current = None
                for row in rows:
                    if row['addr'] >= 0:
                        current = {**row, 'menu_targets': []}
                        instructions[row['addr']] = current
                    if current is not None and row['raw_op'] == 0x48:
                        match = re.search(r'func=(@[0-9a-fA-F]+)', row['line'])
                        if match:
                            current['menu_targets'].append(_number(match[1]))
                    if row['op'].startswith('??'):
                        problems.add(f'{tag.strip()} {number} @{row["addr"]:04X}: {row["line"]}')
                addresses = list(instructions)
                for index, address in enumerate(addresses):
                    instructions[address]['next'] = addresses[index + 1] if index + 1 < len(addresses) else None
                scripts[key] = instructions
        return scripts[key]

    # Trigger operands are (address, file) at these indices, as in DSUN.EXE.
    trigger_indices = {
        0x1B: 0, 0x1C: 0, 0x65: 0, 0x66: 0,
        0x68: 2, 0x69: 2, 0x6A: 4, 0x6B: 4,
        0x6C: 0, 0x6D: 0, 0x6E: 0, 0x6F: 0, 0x70: 2,
    }
    for node in nodes:
        source = node['id']
        pending = [('MAS ', source, 0)]
        visited = set()
        while pending:
            tag, number, address = pending.pop()
            key = (tag, number, address)
            if key in visited:
                continue
            visited.add(key)
            instructions = script(tag, number)
            if address not in instructions:
                problems.add(f'{tag.strip()} {number}: no instruction at @{address:04X}')
                continue
            row = instructions[address]
            if row['op'].startswith('??'):
                continue
            op = row['raw_op']
            args = _arguments(row['line'][row['line'].find('(') + 1:-1])

            def follow(target, target_file=number, target_tag=tag):
                if target is None or target_file is None:
                    problems.add(f'{tag.strip()} {number} @{address:04X}: dynamic script target')
                else:
                    pending.append((target_tag, target_file, target & 0xFFFF))

            if op == 0x5E and _number(args[0]) == 32766:
                target = _number(args[1])
                if target is None:
                    problems.add(f'{tag.strip()} {number} @{address:04X}: dynamic party destination {args[1]}')
                elif target != source:
                    edge = edges.setdefault((source, target), {
                        'source': source, 'target': target, 'transitions': [],
                    })
                    edge['transitions'].append({
                        'script': tag.strip(), 'number': number, 'address': address,
                        'x': _number(args[2]), 'y': _number(args[3]),
                    })
            if op in trigger_indices:
                index = trigger_indices[op]
                follow(_number(args[index]), _number(args[index + 1]), 'GPL ')
            elif op == 0x14:
                follow(_number(args[0]), _number(args[1]), 'GPL ')
            elif op in (0x12, 0x13, 0x29, 0x3E, 0x3F, 0x63, 0x64):
                follow(_number(args[0]))
            elif op == 0x27:
                follow(_number(args[1]))
            for target in row['menu_targets']:
                follow(target)
            if op not in (0x12, 0x15, 0x19, 0x31, 0x35, 0x64) and row['next'] is not None:
                pending.append((tag, number, row['next']))

    known = {node['id'] for node in nodes}
    for target in sorted({target for _, target in edges} - known):
        nodes.append({'id': target, 'file': None})
    return {
        'nodes': sorted(nodes, key=lambda node: node['id']),
        'edges': [edges[key] for key in sorted(edges)],
        'warnings': sorted(problems),
    }
