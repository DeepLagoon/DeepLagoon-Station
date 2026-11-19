"""Translate the spawn-only subset used by WhiteMoon PopulateContents.

This deliberately rejects unknown control flow instead of filling every branch
of a conditional or pretending a procedural initializer was an empty closet.
"""
import re
from source_model import strip_comment


class UnsupportedContents(ValueError):
    pass


def all_of(children, **extra):
    return {'kind': 'all', 'children': children, **extra}


def parse_contents(body):
    lines = []
    in_comment = False
    for line in body.splitlines()[1:]:
        text = line.strip()
        if text.startswith('/*'):
            in_comment = True
        if in_comment:
            if '*/' in text:
                in_comment = False
            continue
        text = strip_comment(text).strip()
        if text:
            lines.append((len(line) - len(line.lstrip('\t')), text))

    def block(start, indent):
        result = []
        i = start
        while i < len(lines) and lines[i][0] >= indent:
            level, line = lines[i]
            if level != indent:
                raise UnsupportedContents(f'Unexpected nested statement: {line}')
            if spawn := re.match(r'new\s+(/[\w/]+)\s*\(', line):
                result.append({'kind': 'spawn', 'path': spawn[1], 'count': 1})
                i += 1
            elif loop := re.fullmatch(r'for\(var/\w+ in 1 to (\d+|rand\(\d+,\s*\d+\))\)', line):
                children, i = block(i + 1, indent + 1)
                count = tuple(map(int, re.findall(r'\d+', loop[1])))
                result.append(all_of(children, rolls=count[0] if len(count) == 1 else count))
            elif conditional := re.fullmatch(r'if\s*\(prob\((\d+)\)\)', line):
                chance = int(conditional[1])
                children, i = block(i + 1, indent + 1)
                if i < len(lines) and lines[i] == (indent, 'else'):
                    other, i = block(i + 1, indent + 1)
                    result.append({'kind': 'group', 'children': [all_of(children, weight=chance), all_of(other, weight=100-chance)]})
                else:
                    result.append(all_of(children, probability=chance / 100))
            elif line.startswith('switch (pick_weight(') or line.startswith('switch(pick_weight('):
                weights = {key: int(value) for key, value in re.findall(r'"([^"]+)"\s*=\s*(\d+)', line)}
                if not weights:
                    raise UnsupportedContents(line)
                i += 1
                choices = []
                while i < len(lines) and lines[i][0] > indent:
                    case = re.fullmatch(r'if\s*\(\s*"([^"]+)"\s*\)', lines[i][1])
                    if not case or case[1] not in weights:
                        raise UnsupportedContents(lines[i][1])
                    children, i = block(i + 1, indent + 2)
                    choices.append(all_of(children, weight=weights[case[1]]))
                if sum(c['weight'] for c in choices) != sum(weights.values()):
                    raise UnsupportedContents('Weighted switch has an unhandled branch')
                result.append({'kind': 'group', 'children': choices})
            elif re.match(r'var/(?:static/)?\w+ = list\(', line):
                # generate_items_inside(list, src) is a declarative spawn table.
                declaration = line
                i += 1
                while i < len(lines) and declaration.count('(') > declaration.count(')'):
                    declaration += lines[i][1]
                    i += 1
                entries = re.findall(r'(/[\w/]+)\s*=\s*(\d+)', declaration)
                if not entries or i >= len(lines) or not lines[i][1].startswith('generate_items_inside('):
                    raise UnsupportedContents(declaration)
                result.extend({'kind': 'spawn', 'path': path, 'count': int(count)} for path, count in entries)
                i += 1
            elif line in ('..()', '. = ..()', 'return', 'return ..()'):
                i += 1
            else:
                raise UnsupportedContents(line)
        return result, i

    if not lines:
        return all_of([])
    children, end = block(0, lines[0][0])
    if end != len(lines):
        raise UnsupportedContents(lines[end][1])
    return all_of(children)


def source_paths(tree):
    if tree['kind'] == 'spawn':
        return {tree['path']}
    return set().union(*(source_paths(child) for child in tree.get('children', [])))
