"""Read authored DM data and initialization procedures from the pinned source.

This is a data reader, not a DM interpreter. Expressions that cannot be
evaluated explicitly are retained for an adapter rather than executed.
"""
import ast
from functools import cache
import operator
import re


TYPE = re.compile(r"^(/[\w/]+)\s*(?:\{|$)")
PROPERTY = re.compile(r"^\t(?:var/(?:[\w]+/)*)?(\w+)\s*=\s*(.*)$")


class SourceModel:
    def __init__(self, repo, revision, git, read_blobs):
        self.types = {}
        self.procs = {}
        dme = read_blobs(repo, ["tgstation.dme"])["tgstation.dme"].decode("utf-8-sig")
        available = set(git(repo, "ls-tree", "-r", "--name-only", revision).decode().splitlines())
        paths = list(dict.fromkeys(p.replace("\\", "/") for p in
                     re.findall(r'^#include "([^"]+\.dm)"', dme, re.M)))
        paths = [p for p in paths if p in available]
        for start in range(0, len(paths), 600):
            for file, data in read_blobs(repo, paths[start:start + 600]).items():
                self.read(file, data.decode("utf-8-sig", errors="replace"))

    def read(self, file, text):
        lines = text.splitlines()
        current = None
        for i, line in enumerate(lines):
            if line.lstrip().startswith(("//", "/*", "*")):
                continue
            if line.startswith("/"):
                match = TYPE.fullmatch(line.split("//", 1)[0].rstrip())
                current = match[1] if match else None
                if current:
                    self.types.setdefault(current, {})
                elif "(" in line:
                    signature = line.split("(", 1)[0].replace("/proc/", "/")
                    end = i + 1
                    while end < len(lines) and not (lines[end].startswith("/") and
                                                  not lines[end].startswith(("//", "/*"))) and not lines[end].startswith("#"):
                        end += 1
                    self.procs.setdefault(signature, []).append((file, i + 1, "\n".join(lines[i:end])))
            elif current and (match := PROPERTY.match(line)):
                value = strip_comment(match[2]).strip()
                # DM lists and strings can span lines. Preserve the entire data
                # expression, including its flags, instead of a truncated token.
                end = i + 1
                while (value.count("(") > value.count(")") or value.endswith("\\")) and end < len(lines):
                    value = value.removesuffix("\\") + strip_comment(lines[end].strip())
                    end += 1
                self.types[current][match[1]] = value

    def parent(self, path):
        return self.types.get(path, {}).get("parent_type") or {
            "/obj": "/atom/movable", "/mob": "/atom/movable", "/turf": "/atom"
        }.get(path, path.rpartition("/")[0])

    @cache
    def fields(self, path):
        if not path:
            return {}
        return {**self.fields(self.parent(path)), **self.types.get(path, {})}

    def initialization(self, path, method):
        """Yield inherited initializers in execution order for explicit ..() calls."""
        if not path:
            return []
        own = self.procs.get(path + "/" + method, ())
        if not own:
            return self.initialization(self.parent(path), method)
        result = []
        if any("..(" in body for _, _, body in own):
            result.extend(self.initialization(self.parent(path), method))
        for file, line, body in own:
            result.append((file, line, body))
        return result


def number(value, default=None):
    if value is None:
        return default
    value = str(value).replace("STANDARD_CELL_CHARGE", "10000")
    value = value.replace("SECONDS", "* 10").replace("SECOND", "* 10").replace("MINUTES", "* 600")
    value = value.replace("JOULES", "").replace("JOULE", "").replace("KILO", "* 1000")
    value = re.sub(r"LASER_SHOTS\(([^,()]+),\s*([^()]+)\)", r"((\2) / (\1))", value)
    ops = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul,
           ast.Div: operator.truediv, ast.USub: operator.neg, ast.UAdd: operator.pos}
    def evaluate(node):
        if isinstance(node, ast.Constant) and isinstance(node.value, (int, float)):
            return node.value
        if isinstance(node, ast.UnaryOp) and type(node.op) in ops:
            return ops[type(node.op)](evaluate(node.operand))
        if isinstance(node, ast.BinOp) and type(node.op) in ops:
            return ops[type(node.op)](evaluate(node.left), evaluate(node.right))
        raise ValueError(value)
    try:
        return evaluate(ast.parse(value, mode="eval").body)
    except (ValueError, SyntaxError, ZeroDivisionError):
        return default


def string(value, default=""):
    if not value or not value.startswith('"'):
        return default
    return value[1:-1].replace('\\"', '"').replace("\\improper ", "").replace("\\proper ", "")


def body_coverage(value):
    """Source clothing coverage expressed as native target bodypart flags."""
    parts = set(re.findall(r'[A-Z_]+', value or '0'))
    result = set()
    mapping = {'HEAD': ('Head',), 'CHEST': ('Torso',), 'GROIN': ('Groin',),
        'ARMS': ('LeftArm', 'RightArm'), 'HANDS': ('LeftHand', 'RightHand'),
        'LEGS': ('LeftLeg', 'RightLeg'), 'FEET': ('LeftFoot', 'RightFoot'),
        'ARM_LEFT': ('LeftArm',), 'ARM_RIGHT': ('RightArm',),
        'HAND_LEFT': ('LeftHand',), 'HAND_RIGHT': ('RightHand',),
        'LEG_LEFT': ('LeftLeg',), 'LEG_RIGHT': ('RightLeg',),
        'FOOT_LEFT': ('LeftFoot',), 'FOOT_RIGHT': ('RightFoot',)}
    for part in parts:
        result.update(mapping.get(part, ()))
    return ', '.join(sorted(result)) or '0'


def strip_comment(value):
    quoted = None
    escaped = False
    for i, char in enumerate(value):
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == quoted:
            quoted = None
        elif not quoted and char in ('"', "'"):
            quoted = char
        elif not quoted and value[i:i + 2] == "//":
            return value[:i]
    return value
