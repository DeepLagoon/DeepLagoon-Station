"""Verify saved page payloads and their public HTTP endpoints after publication."""
import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
import json
from pathlib import Path
import urllib.request
from urllib.parse import quote
from wiki_api import graphql, ORIGIN, read_json, write_json


def canonical(page):
    result={key:(page.get(key) or '').strip() for key in ('content','scriptCss','title','description')}
    result['tags']=sorted(item['tag'] if isinstance(item,dict) else item for item in page.get('tags',[]))
    return result


def verify(folder):
    plan=read_json(folder/'pages.json')
    known={page['path']:page for page in graphql('{pages{list{id path locale}}}')['pages']['list'] if page['locale']=='ru'}
    missing=[page['path'] for page in plan if page['path'] not in known]
    assert not missing, missing
    history=read_json(folder/'publication.json')
    payload_failures=[]
    for offset in range(0,len(plan),8):
        batch=plan[offset:offset+8]
        fields=' '.join(f'p{i}:single(id:{known[p["path"]]["id"]}){{content scriptCss title description editor tags{{tag}}}}' for i,p in enumerate(batch))
        saved=graphql('{pages{'+fields+'}}')['pages']
        for index,page in enumerate(batch):
            actual=saved[f'p{index}']
            assert actual['editor']=='code', (page['path'],actual['editor'])
            expected=canonical(page);received=canonical(actual)
            record=history.get(page['path'],{})
            checksum=lambda value:hashlib.sha256(json.dumps(value,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
            saved_matches=checksum(received)==record.get('savedDigest')
            plan_matches=checksum(expected)==record.get('planDigest')
            # Wiki.js minifies CSS, including selector order and equivalent values.
            expected.pop('scriptCss');received.pop('scriptCss')
            if expected!=received or not saved_matches or not plan_matches: payload_failures.append(page['path'])
            for forbidden in ('Справочные блоки обновлены по прототипам Station','Прямого рецепта синтеза в текущих прототипах нет','В текущих прототипах нет открытых рецептов'):
                assert forbidden not in actual['content'], (page['path'],forbidden)
        print(f'Payloads: {min(offset+8,len(plan))}/{len(plan)}',flush=True)
    def public(page):
        url=ORIGIN+'/ru/'+quote(page['path'])
        for attempt in range(3):
            try:
                request=urllib.request.Request(url,method='HEAD')
                with urllib.request.urlopen(request,timeout=30) as response:
                    return {'path':page['path'],'status':response.status}
            except Exception as error:
                if attempt==2:return {'path':page['path'],'error':str(error)}
    with ThreadPoolExecutor(max_workers=4) as executor:
        public_results=list(executor.map(public,plan))
    result={'pages':len(plan),'payloadMismatches':payload_failures,'publicFailures':[x for x in public_results if x.get('status')!=200],
            'publicChecked':len(public_results),'books':sum('book' in p['tags'] for p in plan)}
    write_json(folder/'live-verification.json',result)
    print(json.dumps(result,ensure_ascii=False,indent=2),flush=True)
    assert not result['payloadMismatches'] and not result['publicFailures']

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('folder',type=Path)
    verify(parser.parse_args().folder)
