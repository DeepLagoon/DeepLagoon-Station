"""Build only species pages from a fresh Wiki.js snapshot; never publishes."""
import argparse
import json
from pathlib import Path
from game_data import GameData
from reference_blocks import Blocks
from species_reference import SPECIES_ARTICLES, ALIASES, PREFIX, build_species_reference


def build(station, folder):
    data = GameData(station, folder / 'prototypes.json')
    blocks = Blocks(data, folder)
    snapshots = {p['path']: p for f in (folder/'before').glob('*.json')
                 if (p := json.loads(f.read_text(encoding='utf-8')))}
    template = snapshots[PREFIX+'mobs/human']
    start, tail = template['content'].split('<article class="prose">', 1)
    suffix = '</article>' + tail.split('</article>', 1)[1]
    pages = []
    def add(path, title, body):
        old = snapshots.get(path, {})
        # Keep the article wrapper and CSS of the current site. Only prose and
        # its heading change; no settings, rules or book articles are written.
        prefix = start.replace(template['title'], title)
        pages.append({'id':old.get('id'), 'beforeUpdatedAt':old.get('updatedAt'),
                      'path':path, 'title':title, 'content':prefix+'<article class="prose">'+body+suffix,
                      'description':'Библиотека DeepLagoon · '+title,
                      'scriptCss':old.get('scriptCss', template['scriptCss']),
                      'editor':'code', 'isPublished':old.get('isPublished', True),
                      'isPrivate':old.get('isPrivate', False),
                      'tags':list(dict.fromkeys([*(t['tag'] for t in old.get('tags', [])), 'deeplagoon-reference', 'guidebook']))})
    for species, relative in SPECIES_ARTICLES.items():
        if species in data.raw['species']:
            path = PREFIX+relative
            add(path, data.loc(data.raw['species'][species]['name']), build_species_reference(path,data,blocks))
    for relative,species in ALIASES.items():
        path=PREFIX+relative
        if path in snapshots:
            add(path,data.loc(data.raw['species'][species]['name']),build_species_reference(path,data,blocks))
    cards=[]
    for species,relative in sorted(SPECIES_ARTICLES.items(),key=lambda item:data.loc(data.raw['species'][item[0]]['name'])):
        p=data.raw['species'][species]
        if not p.get('roundStart'):
            continue
        cards.append('<a class="dl-entity" href="/ru/'+PREFIX+relative+'">'+blocks.icon(p['dollPrototype'])+'<strong>'+data.loc(p['name'])+'</strong></a>')
    add(PREFIX+'mobs/species','Виды персонажей','<p>Выберите вид для описания физиологии, устойчивостей и возвращения после смерти. Здесь показаны виды, разрешённые прототипами для старта раунда; сервер может дополнительно ограничивать выбор.</p><div class="dl-entities">'+''.join(cards)+'</div>')
    for filename,value in [('pages.json',pages),('assets.json',blocks.assets)]:
        (folder/filename).write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
    if blocks.missing_images:
        raise RuntimeError('Missing prototype images: '+str(blocks.missing_images))
    print(f'Built {len(pages)} species articles; {len(cards)} round-start species; {len(blocks.assets)} source images.')


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--station',type=Path,required=True)
    parser.add_argument('--folder',type=Path,required=True)
    args=parser.parse_args()
    build(args.station.resolve(),args.folder.resolve())
