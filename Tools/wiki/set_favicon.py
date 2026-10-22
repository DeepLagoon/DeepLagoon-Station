"""Install a transparent PNG as Wiki.js favicon without changing its header logo."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import urllib.request
import uuid
from wiki_api import ORIGIN, asset_folder, graphql, read_json, write_json

THEME_QUERY='{theming{config{theme iconset darkMode tocPosition injectCSS injectHead injectBody}}}'


def install(image, folder):
    folder_id=asset_folder()
    known=graphql('query($folder:Int!){assets{list(folderId:$folder,kind:ALL){filename}}}',{'folder':folder_id})['assets']['list']
    urls={}
    images=[(size,image.parent/f'favicon-{size}.png') for size in (16,32,192,256)]
    assert all(file.exists() for _,file in images)
    for size,file in images:
        blob=file.read_bytes()
        assert blob[:8]==b'\x89PNG\r\n\x1a\n'
        name='dl-favicon-'+hashlib.sha256(blob).hexdigest()[:18]+'.png'
        if not any(item['filename']==name for item in known):
            boundary='WikiFavicon'+uuid.uuid4().hex
            body=(f'--{boundary}\r\nContent-Disposition: form-data; name="mediaUpload"\r\n\r\n'.encode()+json.dumps({'folderId':folder_id}).encode()+
                  f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="mediaUpload"; filename="{name}"\r\nContent-Type: image/png\r\n\r\n'.encode()+blob+f'\r\n--{boundary}--\r\n'.encode())
            request=urllib.request.Request(ORIGIN+'/u',data=body,headers={'Authorization':'Bearer '+os.environ['DEEPLAGOON_WIKI_TOKEN'],'Content-Type':'multipart/form-data; boundary='+boundary})
            with urllib.request.urlopen(request,timeout=90) as response:assert response.read().decode().strip()=='ok'
        urls[size]='/ru/guidebook/in_game/assets/'+name
        with urllib.request.urlopen(ORIGIN+urls[size],timeout=30) as response:assert response.read()==blob
    blob=image.read_bytes();url=urls[256]
    current=graphql(THEME_QUERY)['theming']['config']
    backup=folder/'theme-before-favicon.json'
    if not backup.exists():write_json(backup,current)
    original=read_json(backup)
    snippet='<!-- deeplagoon-favicon:start -->\n'+'\n'.join(f'<link rel="icon" type="image/png" sizes="{size}x{size}" href="{asset}">' for size,asset in urls.items())+'\n<link rel="shortcut icon" type="image/png" sizes="32x32" href="'+urls[32]+'">\n<!-- deeplagoon-favicon:end -->'
    head=re.sub(r'<!-- deeplagoon-favicon:start -->.*?<!-- deeplagoon-favicon:end -->','',current['injectHead'] or '',flags=re.S).strip()
    desired={**current,'injectHead':(head+'\n'+snippet).strip()}
    if current!=desired:
        assert {**current,'injectHead':head}=={**original,'injectHead':(original['injectHead'] or '').strip()},'Theme changed since backup; inspect before applying'
        result=graphql('mutation($theme:String!,$iconset:String!,$darkMode:Boolean!,$tocPosition:String,$injectCSS:String,$injectHead:String,$injectBody:String){theming{setConfig(theme:$theme,iconset:$iconset,darkMode:$darkMode,tocPosition:$tocPosition,injectCSS:$injectCSS,injectHead:$injectHead,injectBody:$injectBody){responseResult{succeeded message errorCode}}}}',desired)['theming']['setConfig']['responseResult']
        assert result['succeeded'],result
    actual=graphql(THEME_QUERY)['theming']['config']
    assert actual==desired,'Saved theme differs from request'
    write_json(folder/'favicon-publication.json',{'url':ORIGIN+url,'source':str(image),'sha256':hashlib.sha256(blob).hexdigest(),'headerLogoChanged':False,'icons':{size:ORIGIN+asset for size,asset in urls.items()}})
    print('Favicon installed: '+ORIGIN+url,flush=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('image',type=Path);parser.add_argument('--folder',required=True,type=Path)
    args=parser.parse_args();install(args.image,args.folder)
