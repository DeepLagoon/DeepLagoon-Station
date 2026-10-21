"""Build a standalone visual review from the real C# clothing-fit test renders."""
import argparse
import base64
import json
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
NAMES = {'Vulpkanin': 'Вульпканин', 'Feroxi': 'Ферокси', 'Tajaran': 'Таджар'}
ROWS = ['Без одежды', 'Брюки инженера', 'Юбка инженера', 'Длинное платье', 'Викторианское платье', 'Лабораторный халат', 'Скафандр инженера', 'Отметины ног', 'Высокие ботинки']


def build(destination):
    destination.mkdir(parents=True, exist_ok=True)
    data = {}
    font = ImageFont.truetype('C:/Windows/Fonts/arial.ttf', 20)
    small = ImageFont.truetype('C:/Windows/Fonts/arial.ttf', 15)
    for species, name in NAMES.items():
        for sex in ('male', 'female'):
            path = ROOT / f'bin/Content.Tests/digitigrade-{species}-{sex}.png'
            src = Image.open(path).convert('RGBA')
            assert src.size == (1536, len(ROWS) * 192), 'Rerun DigitigradeClothingTests before building the review.'
            key = species + '-' + sex
            data[key] = 'data:image/png;base64,' + base64.b64encode(path.read_bytes()).decode()
            sheet = Image.new('RGB', (1740, src.height + 100), '#252a34')
            d = ImageDraw.Draw(sheet)
            d.text((18, 16), name + (' · мужской' if sex == 'male' else ' · женский'), font=font, fill='white')
            for direction, title in enumerate(['Юг / спереди', 'Север / сзади', 'Восток', 'Запад']):
                x = 204 + direction * 384
                d.text((x + 10, 48), title, font=font, fill='white')
                d.text((x + 10, 76), 'Исходная одежда', font=small, fill='#aebbd1')
                d.text((x + 202, 76), 'Подгонка', font=small, fill='#69ddbd')
            sheet.paste(src, (204, 100), src)
            for row, title in enumerate(ROWS):
                d.text((12, 100 + row * 192 + 75), title, font=small, fill='white')
                d.line((0, 100 + row * 192, 1740, 100 + row * 192), fill='#394455')
            sheet.save(destination / (key + '.png'))
    html = '''<!doctype html><meta charset="utf-8"><title>Форма ног · визуальная проверка</title>
<style>body{margin:0;background:#171b23;color:#e7edf7;font:16px system-ui}main{max-width:1500px;margin:auto;padding:28px}h1{font-size:25px}p{color:#b4c1d5;max-width:1000px;line-height:1.6}nav{display:flex;gap:16px;flex-wrap:wrap;padding:20px 0;position:sticky;top:0;background:#171b23}select,input,button{font:inherit;background:#293242;color:white;padding:8px;border:1px solid #51607a;border-radius:6px}label{display:grid;gap:6px;font-size:14px;color:#aebbd1}#grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:16px}.card{background:#252a34;border:1px solid #394455;border-radius:10px;padding:14px}.pair{display:flex;justify-content:center;overflow:auto}.sprite{text-align:center}.sprite span{display:block;color:#aebbd1;font-size:13px}canvas{image-rendering:pixelated;max-width:100%;height:auto}#comment{width:100%;box-sizing:border-box;background:#252a34;color:white;padding:12px;border:1px solid #51607a;margin-top:20px;font:inherit}#status{color:#69ddbd}</style>
<main><h1>Скошенные ноги: ферокси, вульпканины и таджары</h1>
<p>Слева — исходная одежда на новых ногах. Справа — та же одежда после подгонки. Изображения получены из реальных RSI и C# карты шейдера, в четырёх направлениях. Базовое тело показано в сером цвете, без косметики; уши и хвосты здесь не добавлены. Отметины выделены розовым. Это проверка спрайтов, а не снимки игрового клиента.</p>
<nav><label>Раса<select id="species"><option value="Vulpkanin">Вульпканин</option><option value="Feroxi">Ферокси</option><option value="Tajaran">Таджар</option></select></label><label>Пол<select id="sex"><option value="female">Женский</option><option value="male">Мужской</option></select></label><label>Одежда<select id="outfit"></select></label><label>Масштаб<input id="zoom" type="range" min="2" max="8" value="5"></label><label>Фон<select id="bg"><option value="#252a34">Тёмный</option><option value="#838990">Серый</option><option value="#e7e1d3">Светлый</option></select></label></nav>
<div id="grid"></div><p>Для замечаний укажите расу, пол, одежду и направление. Особенно проверьте изгиб сбоку, непрерывность брюк и обуви, край короткой юбки, длинные подолы и форму скафандра.</p>
<textarea id="comment" rows="4" placeholder="Ваши замечания"></textarea><button id="save">Скачать замечания</button><span id="status"></span></main>
<script>
const sources=__DATA__, rows=__ROWS__, images={};
const $=id=>document.getElementById(id); rows.forEach((text,i)=>{const o=document.createElement('option');o.value=i;o.textContent=text;$('outfit').append(o)});$('outfit').value=1;
Object.entries(sources).forEach(([k,v])=>{const im=new Image();images[k]=im;im.onload=draw;im.src=v});
function draw(){const im=images[$('species').value+'-'+$('sex').value];if(!im?.complete)return;const grid=$('grid');grid.replaceChildren();const z=+$('zoom').value;
['Юг · спереди','Север · сзади','Восток','Запад'].forEach((name,d)=>{const card=document.createElement('div');card.className='card';card.style.background=$('bg').value;const h=document.createElement('h3');h.textContent=name;card.append(h);const pair=document.createElement('div');pair.className='pair';[0,1].forEach(side=>{const cell=document.createElement('div');cell.className='sprite';const title=document.createElement('span');title.textContent=side?'Подгонка':'Исходная одежда';const canvas=document.createElement('canvas');canvas.width=32*z;canvas.height=40*z;const ctx=canvas.getContext('2d');ctx.imageSmoothingEnabled=false;ctx.drawImage(im,(d*2+side)*192+48,Number($('outfit').value)*192+36,96,120,0,0,canvas.width,canvas.height);cell.append(title,canvas);pair.append(cell)});card.append(pair);grid.append(card)})}
['species','sex','outfit','zoom','bg'].forEach(id=>$(id).addEventListener('input',draw));
$('save').onclick=()=>{const text=[$('species').selectedOptions[0].textContent,$('sex').selectedOptions[0].textContent,rows[$('outfit').value],$('comment').value].join('\\n');const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([text],{type:'text/plain;charset=utf-8'}));a.download='digitigrade-review.txt';a.click();URL.revokeObjectURL(a.href);$('status').textContent=' Замечания сохранены локально'};
</script>'''
    (destination / 'review.html').write_text(html.replace('__DATA__', json.dumps(data)).replace('__ROWS__', json.dumps(ROWS, ensure_ascii=False)), encoding='utf-8')
    print(destination / 'review.html')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path)
    build(parser.parse_args().destination)
