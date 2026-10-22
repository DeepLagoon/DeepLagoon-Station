"""Wiki.js publication helpers. Credentials are read only from process environment."""
import argparse
import json
import os
from pathlib import Path
import time
import urllib.request
import urllib.error
from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
import mimetypes
import uuid

ORIGIN = 'https://wiki.deep-lagoon-ss14.ru'


def graphql(query, variables=None):
    data = json.dumps({'query': query, 'variables': variables or {}}).encode()
    for attempt in range(5):
        try:
            request = urllib.request.Request(ORIGIN + '/graphql', data=data,
                headers={'Authorization': 'Bearer ' + os.environ['DEEPLAGOON_WIKI_TOKEN'],
                         'Content-Type': 'application/json'})
            with urllib.request.urlopen(request, timeout=90) as response:
                result = json.load(response)
            if result.get('errors'):
                raise RuntimeError(json.dumps(result['errors'], ensure_ascii=False))
            return result['data']
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            if attempt == 4 or query.lstrip().startswith('mutation'):
                raise
            time.sleep(2 ** attempt)


def snapshot(folder):
    if (folder / 'before').exists() and any((folder / 'before').iterdir()):
        raise RuntimeError('Backup already exists; use a fresh publication directory')
    folder.mkdir(parents=True, exist_ok=True)
    schema = graphql('''{pages{list{id path title locale}} __schema{mutationType{fields{name}}}
      pageMutation:__type(name:"PageMutation"){fields{name args{name type{kind name ofType{kind name ofType{kind name}}}}}}
      assetMutation:__type(name:"AssetMutation"){fields{name args{name type{kind name ofType{kind name ofType{kind name}}}}}}
      assetQuery:__type(name:"AssetQuery"){fields{name args{name type{kind name ofType{kind name ofType{kind name}}}}}}}''')
    (folder / 'schema.json').write_text(json.dumps(schema, ensure_ascii=False, indent=2), encoding='utf-8')
    pages = schema['pages']['list']
    backup = folder / 'before'
    backup.mkdir(exist_ok=True)
    count = 0
    for start in range(0, len(pages), 15):
        batch = pages[start:start + 15]
        fields = ' '.join(f'p{x["id"]}:single(id:{x["id"]})' + '{id path title description content editor contentType isPublished isPrivate locale tags{tag} scriptCss scriptJs updatedAt}' for x in batch)
        result = graphql('{pages{' + fields + '}}')['pages']
        for item in result.values():
            if item:
                (backup / f'{item["id"]}.json').write_text(json.dumps(item, ensure_ascii=False, indent=2), encoding='utf-8')
                count += 1
        print(f'Backed up {count}/{len(pages)} pages', flush=True)
    print('Snapshot complete.', flush=True)


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8'))


def write_json(path, data):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    temporary.replace(path)


def asset_folder():
    parent = 0
    # Wiki.js strips the locale prefix from public /ru/... requests. The
    # physical asset hierarchy therefore starts at guidebook, without ru.
    for slug in ('guidebook', 'in_game', 'assets'):
        def children():
            return graphql('query($parent:Int!){assets{folders(parentFolderId:$parent){id name slug}}}', {'parent': parent})['assets']['folders']
        found = next((f for f in children() if f['slug'] == slug), None)
        if found is None:
            result = graphql('mutation($parent:Int!,$slug:String!){assets{createFolder(parentFolderId:$parent,slug:$slug){responseResult{succeeded message errorCode}}}}', {'parent': parent, 'slug': slug})
            response = result['assets']['createFolder']['responseResult']
            if not response['succeeded']:
                raise RuntimeError(response)
            found = next(f for f in children() if f['slug'] == slug)
        parent = found['id']
    return parent


def upload_assets(folder, limit=None):
    folder_id = asset_folder()
    entries = list(read_json(folder / 'assets.json').values())
    existing = graphql('query($folder:Int!){assets{list(folderId:$folder,kind:ALL){id filename}}}', {'folder': folder_id})['assets']['list']
    known = {entry['filename'] for entry in existing}
    pending = [entry for entry in entries if entry['file'] not in known]
    if limit:
        pending = pending[:limit]
    def upload(entry):
        name = entry['file']
        blob = (folder / 'assets' / name).read_bytes()
        boundary = 'WikiStation' + uuid.uuid4().hex
        mime = mimetypes.guess_type(name)[0] or 'application/octet-stream'
        metadata = json.dumps({'folderId': folder_id}).encode()
        body = (f'--{boundary}\r\nContent-Disposition: form-data; name="mediaUpload"\r\n\r\n'.encode() + metadata +
                f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="mediaUpload"; filename="{name}"\r\nContent-Type: {mime}\r\n\r\n'.encode() + blob + f'\r\n--{boundary}--\r\n'.encode())
        request = urllib.request.Request(ORIGIN + '/u', data=body,
            headers={'Authorization': 'Bearer ' + os.environ['DEEPLAGOON_WIKI_TOKEN'],
                     'Content-Type': 'multipart/form-data; boundary=' + boundary})
        with urllib.request.urlopen(request, timeout=90) as response:
            result = response.read().decode()
        if result.strip() != 'ok':
            raise RuntimeError('Asset upload failed: ' + result[:300])
        return name
    completed = []
    failures = []
    with ThreadPoolExecutor(max_workers=4) as executor:
        futures = {executor.submit(upload, entry): entry for entry in pending}
        for future in as_completed(futures):
            try:
                completed.append(future.result())
            except Exception as error:
                failures.append({'file': futures[future]['file'], 'error': str(error)})
            if (len(completed) + len(failures)) % 50 == 0:
                print(f'Assets uploaded: {len(completed)}/{len(pending)}; failed: {len(failures)}', flush=True)
    write_json(folder / 'asset-publication.json', {'folderId': folder_id, 'alreadyPresent':len(known), 'uploaded':completed, 'failed':failures})
    print(f'Assets: {len(known)} already present, {len(completed)} uploaded, {len(failures)} failed.', flush=True)
    if failures:
        raise RuntimeError('Some assets failed; rerun upload to resume.')
    if entries:
        name = entries[0]['file']
        with urllib.request.urlopen(ORIGIN + '/ru/guidebook/in_game/assets/' + name, timeout=30) as response:
            actual = response.read()
        assert hashlib.sha256(actual).digest() == hashlib.sha256((folder / 'assets' / name).read_bytes()).digest()
        print('Public asset URL verified byte-for-byte.', flush=True)


def apply_pages(folder, selection=None, workers=3):
    pages = read_json(folder / 'pages.json')
    if selection:
        pages = [page for page in pages if page['path'] in selection]
        assert len(pages) == len(selection)
    known = {p['path']: p for p in graphql('{pages{list{id path title locale}}}')['pages']['list'] if p['locale'] == 'ru'}
    history_path = folder / 'publication.json'
    history = read_json(history_path) if history_path.exists() else {}
    def digest(page):
        value = {k:page.get(k) for k in ('content','scriptCss','title','description','tags')}
        value['tags'] = sorted(value.get('tags') or [])
        for key in ('content','scriptCss','title','description'):
            value[key] = (value[key] or '').strip()
        return hashlib.sha256(json.dumps(value,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
    normalization = read_json(folder / 'css-normalization.json') if (folder / 'css-normalization.json').exists() else {}
    def publish(page):
        path = page['path']
        existing = known.get(path)
        if existing:
            current = graphql('query($id:Int!){pages{single(id:$id){id path title description content scriptCss updatedAt editor tags{tag}}}}', {'id':existing['id']})['pages']['single']
            comparison = {**current, 'tags':[item['tag'] for item in current['tags']]}
            previous = history.get(path, {})
            css_key = hashlib.sha256(page['scriptCss'].encode()).hexdigest()
            normalized_plan = {**page, 'scriptCss':normalization.get(css_key,page['scriptCss'])}
            if digest(comparison) == digest(normalized_plan) or (previous.get('planDigest') == digest(page) and previous.get('savedDigest') == digest(comparison)):
                return {'path':path,'id':current['id'],'status':'unchanged','digest':digest(comparison),'savedDigest':digest(comparison),'planDigest':digest(page)}
            original_unchanged = page.get('id') and current['updatedAt'] == page.get('beforeUpdatedAt')
            backup_path = folder / 'before' / f"{page.get('id')}.json"
            if not original_unchanged and backup_path.exists():
                backup = read_json(backup_path)
                backup['tags'] = [item['tag'] for item in backup.get('tags', [])]
                # An editor-only conversion can change updatedAt without changing
                # any protected content. Compare the complete original payload.
                original_unchanged = digest(backup) == digest(comparison)

            managed_unchanged = previous.get('savedDigest', previous.get('digest')) == digest(comparison)
            if not original_unchanged and not managed_unchanged:
                raise RuntimeError('Page changed after backup; refusing overwrite: ' + path)
            if current.get('editor') != page['editor']:
                converted = graphql('mutation($id:Int!,$editor:String!){pages{convert(id:$id,editor:$editor){responseResult{succeeded message errorCode}}}}',
                                    {'id':existing['id'],'editor':page['editor']})['pages']['convert']['responseResult']
                if not converted['succeeded']:
                    raise RuntimeError('Editor conversion failed: ' + str(converted))
        values = {key: page[key] for key in ('content','description','editor','isPublished','isPrivate','path','scriptCss','tags','title')}
        values['locale'] = 'ru'
        definitions = '$content:String!,$description:String!,$editor:String!,$isPublished:Boolean!,$isPrivate:Boolean!,$locale:String!,$path:String!,$scriptCss:String!,$tags:[String]!,$title:String!'
        arguments = 'content:$content,description:$description,editor:$editor,isPublished:$isPublished,isPrivate:$isPrivate,locale:$locale,path:$path,scriptCss:$scriptCss,tags:$tags,title:$title'
        verb = 'update' if existing else 'create'
        if existing:
            definitions += ',$id:Int!'
            arguments += ',id:$id'
            values['id'] = existing['id']
        result = graphql('mutation(' + definitions + '){pages{' + verb + '(' + arguments + '){responseResult{succeeded message errorCode} page{id path}}}}', values)['pages'][verb]
        if not result['responseResult']['succeeded']:
            raise RuntimeError(str(result['responseResult']))
        saved = graphql('query($id:Int!){pages{single(id:$id){content scriptCss title description updatedAt tags{tag}}}}', {'id':result['page']['id']})['pages']['single']
        saved['tags'] = [item['tag'] for item in saved['tags']]
        return {'path':path,'id':result['page']['id'],'status':verb,'digest':digest(saved),'savedDigest':digest(saved),'planDigest':digest(page),'updatedAt':saved['updatedAt']}

    failed = []
    counts = {}
    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = {executor.submit(publish, page): page for page in pages}
        for future in as_completed(futures):
            path = futures[future]['path']
            try:
                result = future.result()
                history[path] = result
                counts[result['status']] = counts.get(result['status'], 0) + 1
            except Exception as error:
                failed.append({'path':path,'error':str(error)})
                print('Failed: ' + path + ': ' + str(error)[:300], flush=True)
            write_json(history_path, history)
            if sum(counts.values()) % 25 == 0:
                print(f'Pages: {sum(counts.values())}/{len(pages)}; failures: {len(failed)}', flush=True)
    write_json(folder / 'publication-failures.json', failed)
    print('Publication: ' + json.dumps(counts) + '; failures: ' + str(len(failed)), flush=True)
    if failed:
        raise RuntimeError('Publication incomplete; inspect publication-failures.json')


def inspect_pages(folder):
    pages = graphql('{pages{list{id path title locale}}}')['pages']['list']
    after = folder / 'live-check'
    after.mkdir(exist_ok=True)
    for page in pages:
        if page['path'] not in ('home', 'guidebook/in_game/books/bartenders-manual', 'guidebook/in_game/chemistry/bicaridine'):
            continue
        current = graphql('query($id:Int!){pages{single(id:$id){id path title description content scriptCss updatedAt editor tags{tag}}}}', {'id':page['id']})['pages']['single']
        (after / f'{page["id"]}.json').write_text(json.dumps(current,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps({**{k:current[k] for k in ('id','path','updatedAt','editor','tags')},'contentLength':len(current['content']),
                          'cssLength':len(current['scriptCss']),'cssStart':current['scriptCss'][:150]},ensure_ascii=False),flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['snapshot', 'assets', 'apply', 'inspect'])
    parser.add_argument('--folder', type=Path, required=True)
    parser.add_argument('--limit', type=int)
    parser.add_argument('--workers', type=int, default=1, choices=range(1,5))
    parser.add_argument('--only', help='Comma-separated paths for a first-page check')
    args = parser.parse_args()
    if args.action == 'snapshot':
        snapshot(args.folder)
    elif args.action == 'assets':
        upload_assets(args.folder, args.limit)
    elif args.action == 'apply':
        apply_pages(args.folder, set(args.only.split(',')) if args.only else None, workers=args.workers)
    elif args.action == 'inspect':
        inspect_pages(args.folder)
