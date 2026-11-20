"""WhiteMoon's asymmetric smoothing groups and 47 valid eight-neighbor junctions."""
import re


def groups(value):
    return sorted(set(re.findall(r'\bSMOOTH_GROUP_[A-Z0-9_]+\b', value or '')))


def junctions(cardinal_only=False):
    result = []
    for mask in range(16 if cardinal_only else 256):
        if any(mask & corner and mask & sides != sides for corner, sides in
               ((16, 5), (32, 6), (64, 10), (128, 9))):
            continue
        result.append(mask)
    return result
