"""Russian subject directory linking only to existing game guide pages."""
from collections import defaultdict
from html import escape

from editorial_content import protected_page

GUIDES_PATH = 'guidebook/in_game/guides'
ROOT = 'guidebook/in_game/'
SECTIONS = [
    ('start', 'Первые шаги'),
    ('medical', 'Врач и химик'),
    ('engineering', 'Инженер и атмосферный техник'),
    ('cargo', 'Снабжение и утилизация'),
    ('piloting', 'Пилот и экипаж корабля'),
    ('science', 'Учёный и робототехник'),
    ('service', 'Повар, бармен, ботаник и уборщик'),
    ('security', 'Служба безопасности'),
    ('command', 'Командование и организация экипажа'),
    ('species', 'Виды персонажей'),
    ('psionics', 'Псионика'),
    ('creatures', 'Существа и противники'),
    ('antagonists', 'Антагонисты'),
    ('reference', 'Рецепты и справочные таблицы'),
    ('other', 'Другие механики'),
]

DIRECTORY_CSS = '''
.dlwiki .dl-guide-nav{display:flex;flex-wrap:wrap;gap:8px;margin:0 0 28px}
.dlwiki .dl-guide-nav a{border:1px solid var(--line);padding:8px 12px;border-radius:3px;color:var(--ink);font-size:13px}
.dlwiki .dl-guide-nav a:hover,.dlwiki .dl-guide-nav a:focus{border-color:var(--accent);background:var(--surface)}
.dlwiki .dl-guide-section{scroll-margin-top:24px;margin:32px 0}
.dlwiki .dl-guide-section h2{font-size:23px;line-height:1.35}
.dlwiki .dl-guide-links{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0 24px;list-style:none;padding:0;margin:12px 0}
.dlwiki .dl-guide-links li{margin:0;border-bottom:1px solid var(--line);min-width:0}
.dlwiki .dl-guide-links a{display:block;padding:11px 0;line-height:1.5;overflow-wrap:anywhere}
@media(max-width:700px){.dlwiki .dl-guide-links{grid-template-columns:1fr}.dlwiki .dl-guide-nav a{padding:7px 9px}.dlwiki .dl-guide-section h2{font-size:21px}}
'''


def section_for(path):
    value = path.removeprefix(ROOT)
    parts = value.split('/')
    if 'new-player' in parts or value in ('survival', 'space-station14', 'glossary') or value.endswith('/beginner-guide'):
        return 'start'
    if 'mobs' in parts:
        return 'species'
    if 'mobs-hostile-expedition' in parts or 'creatures' in parts:
        return 'creatures'
    if 'medical' in parts or value == 'chemistry/bicaridine':
        return 'medical'
    if 'engineering' in parts or 'economy' in parts:
        return 'engineering'
    if parts[0] == 'cargo' or parts[-1] in ('expeditions', 'cargo-hauling'):
        return 'cargo'
    if parts[-1] in ('piloting', 'shipyard', 'ship-combat', 'motley-anne', 'nfrmanual', 'takeaway') or 'specifications' in parts:
        return 'piloting'
    if 'science' in parts or 'robotics' in parts:
        return 'science'
    if 'service' in parts:
        return 'service'
    if 'security' in parts or 'nfsd' in parts:
        return 'security'
    if value in ('command', 'jobs') or parts[-1] in ('hiring', 'bank'):
        return 'command'
    if 'psionics' in parts:
        return 'psionics'
    if parts[0] in ('antagonist', 'antagonists'):
        return 'antagonists'
    if parts[0] in ('reference', 'reference-tables', 'chemical-tabs', 'reagents') or value in ('chemicals', 'references'):
        return 'reference'
    return 'other'


def directory(pages):
    groups = defaultdict(list)
    seen = set()
    for page in pages:
        path = page['path']
        if path in seen or not path.startswith(ROOT) or protected_page(path):
            continue
        tail = path.removeprefix(ROOT)
        if tail.startswith('books') or tail in ('guides', 'illustrations'):
            continue
        seen.add(path)
        groups[section_for(path)].append(page)
    nav = '<nav class="dl-guide-nav" aria-label="Профессии и темы">' + ''.join(
        '<a href="#guides-' + key + '">' + escape(title) + '</a>' for key, title in SECTIONS if groups[key]) + '</nav>'
    sections = []
    for key, title in SECTIONS:
        if not groups[key]:
            continue
        links = ''.join('<li><a href="/ru/' + escape(p['path'], quote=True) + '">' + escape(p['title']) + '</a></li>' for p in sorted(groups[key], key=lambda p: p['title'].casefold()))
        sections.append('<section class="dl-guide-section" id="guides-' + key + '"><h2>' + escape(title) + '</h2><ul class="dl-guide-links">' + links + '</ul></section>')
    return nav + ''.join(sections)
