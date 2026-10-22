"""Render the guidebook's prototype-driven blocks as accessible static HTML."""
from collections import defaultdict
import hashlib
import html
import json
from pathlib import Path
import struct

from game_data import slug

E = lambda value: html.escape(str(value), quote=True)
GROUPS = {'Elements': 'Элементы', 'Medicine': 'Лекарства', 'Narcotics': 'Наркотические вещества',
          'Pyrotechnic': 'Пиротехника', 'Toxins': 'Токсины', 'Foods': 'Пищевые вещества',
          'Botanical': 'Растительные вещества', 'Biological': 'Биологические вещества',
          'Special': 'Особые вещества', 'Unknown': 'Прочие вещества', 'Drinks': 'Напитки',
          'Pizza': 'Пицца', 'Savory': 'Основные блюда', 'Breads': 'Хлеб и выпечка',
          'Breakfast': 'Завтраки', 'Moth': 'Блюда для ниан', 'Pasta': 'Паста и лапша',
          'Dessert': 'Десерты', 'Soup': 'Супы', 'Pie': 'Пироги', 'BarsAndCookies': 'Батончики и печенье',
          'Cake': 'Торты', 'Salad': 'Салаты', 'Medicinal': 'Лечебные продукты', 'Other': 'Прочие блюда',
          'Secret': 'Особые блюда'}
DAMAGE = {'Brute': 'механический', 'Burn': 'термический', 'Airloss': 'нехватка воздуха',
          'Toxin': 'токсины', 'Slash': 'порезы', 'Piercing': 'уколы', 'Blunt': 'ушибы',
          'Heat': 'ожоги', 'Cold': 'обморожение', 'Shock': 'электрический', 'Poison': 'яды',
          'Radiation': 'радиация', 'Bloodloss': 'кровопотеря', 'Asphyxiation': 'удушье',
          'Cellular': 'клеточный'}


def amount(value):
    return f'{float(value):g}' if isinstance(value, (int, float)) else str(value)


class Blocks:
    def __init__(self, data, output):
        self.data = data
        self.output = Path(output)
        (self.output / 'assets').mkdir(parents=True, exist_ok=True)
        self.assets = {}
        self.missing_entities = set()
        self.missing_images = set()
        self.recipes_by_product = defaultdict(list)
        self.reactions = list(data.raw['reaction'].values())
        for recipe in self.reactions:
            for reagent in recipe.get('products', {}):
                self.recipes_by_product[reagent].append(recipe)
        self.grind = defaultdict(list)
        self.juice = defaultdict(list)
        self.lathe = defaultdict(list)
        for identifier in data.raw['entity']:
            if data.raw['entity'][identifier].get('abstract'):
                continue
            comps = data.components(identifier)
            if 'BodyPart' not in comps and 'Pill' not in comps:
                extractable = comps.get('Extractable', {})
                solution = extractable.get('grindableSolutionName')
                if solution:
                    contents = comps.get('SolutionContainerManager', {}).get('solutions', {}).get(solution, {}).get('reagents', [])
                    for entry in contents:
                        self.grind[entry.get('ReagentId')].append((identifier, entry.get('Quantity', 0)))
                for entry in (extractable.get('juiceSolution') or {}).get('reagents', []):
                    self.juice[entry.get('ReagentId')].append((identifier, entry.get('Quantity', 0)))
        for recipe in data.raw['latheRecipe'].values():
            self.lathe[recipe.get('result')].append(recipe)

    def asset(self, path):
        path = Path(path)
        if not path.is_file():
            return None
        blob = path.read_bytes()
        name = 'dl-' + hashlib.sha256(blob).hexdigest()[:18] + path.suffix.lower()
        target = self.output / 'assets' / name
        if not target.exists():
            target.write_bytes(blob)
        self.assets[name] = {'file': name, 'source': str(path.relative_to(self.data.station)).replace('\\', '/')}
        metadata = path.parent / 'meta.json'
        if metadata.exists():
            info = json.loads(metadata.read_text(encoding='utf-8-sig'))
            self.assets[name].update({key: info.get(key) for key in ('license', 'copyright')})
        return '/ru/guidebook/in_game/assets/' + name

    def icon(self, identifier):
        components = self.data.components(identifier)
        sprite = components.get('Sprite', {})
        icon = components.get('Icon', {})
        if icon.get('sprite'):
            sprite = icon
        rsi = sprite.get('sprite')
        if not rsi:
            return ''
        directory = self.data.station / 'Resources/Textures' / str(rsi).lstrip('/')
        if not directory.is_dir():
            self.missing_images.add(identifier)
            return ''
        meta = json.loads((directory / 'meta.json').read_text(encoding='utf-8-sig'))
        frame = meta.get('size', {'x': 32, 'y': 32})
        layers = sprite.get('layers') or [{'state': sprite.get('state', 'icon')}]
        result = []
        for layer in layers:
            if layer.get('visible') is False or not layer.get('state'):
                continue
            path = directory / (str(layer['state']) + '.png')
            if not path.is_file():
                continue
            url = self.asset(path)
            width, height = struct.unpack('>II', path.read_bytes()[16:24])
            cols, rows = width / frame['x'], height / frame['y']
            extra = ''
            color = layer.get('color', '')
            if isinstance(color, str) and color.startswith('#') and len(color) in (4, 7, 9):
                extra = f'background-color:{E(color)};background-image:none;mask-image:url({url});mask-size:calc(100% * {cols:g}) calc(100% * {rows:g});mask-repeat:no-repeat;'
            result.append(f'<span style="background-image:url({url});background-size:calc(100% * {cols:g}) calc(100% * {rows:g});{extra}"></span>')
        if not result:
            self.missing_images.add(identifier)
            return ''
        return f'<span class="dl-sprite" role="img" aria-label="{E(self.data.name("entity", identifier))}">' + ''.join(result) + '</span>'

    def entity(self, identifier, compact=False):
        value = self.data.get('entity', identifier)
        if not value:
            self.missing_entities.add(identifier)
            return f'<aside class="note warning">Объект <code>{E(identifier)}</code> отсутствует в текущих прототипах. Старая инструкция с ним требует пересмотра.</aside>'
        name, description = self.data.name('entity', identifier), self.data.description('entity', identifier)
        description = description if '{' not in description else ''
        components = self.data.components(identifier)
        stats = []
        for solution, info in components.get('SolutionContainerManager', {}).get('solutions', {}).items():
            if info.get('maxVol'):
                stats.append('Ёмкость: ' + amount(info['maxVol']) + ' ед.')
        if components.get('ChemMaster', {}).get('pillDosageLimit') is not None:
            stats.append('Лимит дозы таблетки: ' + amount(components['ChemMaster']['pillDosageLimit']) + ' ед.')
        card = f'<div class="dl-entity">{self.icon(identifier)}<div><strong>{E(name)}</strong>'
        if description:
            card += f'<p>{E(description)}</p>'
        if stats:
            card += '<small>' + E(' · '.join(stats)) + '</small>'
        return card + '</div></div>'

    def reagent_link(self, identifier):
        reagent = self.data.get('reagent', identifier)
        group = reagent.get('group', 'Unknown')
        return f'<a href="/ru/guidebook/in_game/reagents/{slug(group)}#reagent-{slug(identifier)}">{E(self.data.name("reagent", identifier))}</a>'

    def reaction(self, recipe):
        reactants, catalysts = [], []
        for reagent, info in recipe.get('reactants', {}).items():
            entry = f'{self.reagent_link(reagent)} — {E(amount(info.get("amount", 1)))} ед.'
            (catalysts if info.get('catalyst') else reactants).append(entry)
        products = [f'{self.reagent_link(reagent)} — {E(amount(count))} ед.' for reagent, count in recipe.get('products', {}).items()]
        extra_effects = []
        for effect in recipe.get('effects', []):
            if not isinstance(effect, dict):
                continue
            entity = effect.get('entity') or effect.get('prototype')
            if entity:
                products.append(E(self.data.name('entity', entity)) + ' (предмет)')
            else:
                kind = effect.get('effectType', '')
                labels = {'ExplosionReactionEffect': 'взрыв', 'AreaReactionEffect': 'эффект по площади',
                          'CreateGas': 'выделение газа', 'FoamAreaReactionEffect': 'образование пены',
                          'SmokeAreaReactionEffect': 'образование дыма', 'FlammableReaction': 'воспламенение',
                          'ChemVomit': 'рвота', 'SatiateHunger': 'насыщение', 'SatiateThirst': 'утоление жажды'}
                if kind:
                    extra_effects.append(labels.get(kind, kind))
        conditions = []
        if recipe.get('minTemp') is not None:
            conditions.append(f'Температура не ниже {amount(recipe["minTemp"])} K ({float(recipe["minTemp"])-273.15:g} °C)')
        if recipe.get('maxTemp') is not None:
            conditions.append(f'Температура не выше {amount(recipe["maxTemp"])} K ({float(recipe["maxTemp"])-273.15:g} °C)')
        mixers = recipe.get('requiredMixerCategories')
        if mixers:
            names = [self.data.loc(self.data.get('mixingCategory', key).get('verbText', key)) for key in mixers]
            conditions.append('Смешивание: ' + ', '.join(names))
        else:
            conditions.append('Обычное соединение реагентов')
        if recipe.get('quantized'):
            conditions.append('Только целые порции рецепта')
        if extra_effects:
            conditions.append('Дополнительное действие: ' + ', '.join(extra_effects))
        text = '<div class="dl-reaction" data-reaction-id="' + E(recipe['id']) + '"><div class="dl-equation"><div>' + '<br>'.join(reactants) + '</div><b aria-label="получается">→</b><div>' + ('<br>'.join(products) or 'Реакция с эффектом, без жидкого продукта') + '</div></div>'
        if catalysts:
            text += '<p class="dl-catalyst"><strong>Катализатор — не расходуется:</strong> ' + '; '.join(catalysts) + '</p>'
        text += '<p class="dl-conditions">' + '<br>'.join(E(x) for x in conditions) + '</p>'
        return text + '</div>'

    def reagent(self, identifier):
        value = self.data.get('reagent', identifier)
        if not value:
            return ''
        name = self.data.name('reagent', identifier)
        desc = self.data.description('reagent', identifier)
        recipes = sorted(self.recipes_by_product[identifier], key=lambda r: (r.get('source', False), r.get('priority', 0), r['id']))
        color = value.get('color', '#6a9e91')
        if not isinstance(color, str) or not color.startswith('#'):
            color = '#6a9e91'
        text = f'<details class="dl-reagent" id="reagent-{slug(identifier)}"><summary><span class="dl-dot" style="background:{E(color)}"></span>{E(name)}</summary><div class="dl-reagent-body"><p>{E(desc)}</p>'
        if recipes:
            text += '<h4>Получение и реакции</h4>' + ''.join(self.reaction(recipe) for recipe in recipes)
        sources = []
        for verb, source in [('Измельчение', self.grind), ('Отжим', self.juice)]:
            items = source.get(identifier, [])
            if items:
                sources.append(f'<details><summary>{verb}: {len(items)} источников</summary><ul>' + ''.join(f'<li>{E(self.data.name("entity", entity))} — {E(amount(count))} ед.</li>' for entity, count in sorted(items)) + '</ul></details>')
        text += ''.join(sources)
        gases = [value for value in self.data.raw['gas'].values() if value.get('reagent') == identifier]
        if gases:
            text += '<p><strong>Конденсация газа:</strong> ' + ', '.join(E(self.data.loc(value.get('name', value['id']))) for value in gases) + '.</p>'
        rows = []
        for metabolism, info in value.get('metabolisms', {}).items():
            for effect in info.get('effects', []):
                if not isinstance(effect, dict) or effect.get('effectType') != 'HealthChange':
                    continue
                damage = effect.get('damage', {})
                effects = []
                for _, changes in damage.items():
                    if isinstance(changes, dict):
                        effects.extend(f'{DAMAGE.get(key, key)}: {amount(change)}' for key, change in changes.items())
                conditions = []
                for condition in effect.get('conditions', []):
                    if condition.get('effectType') == 'ReagentThreshold':
                        who = condition.get('reagent', identifier)
                        conditions.append(self.data.name('reagent', who) + ': ' + ', '.join(k + ' ' + amount(condition[v]) + ' ед.' for v, k in [('min', 'не менее'), ('max', 'не более')] if v in condition))
                    elif condition.get('effectType'):
                        conditions.append(condition['effectType'] + ': ' + ', '.join(str(v) for k, v in condition.items() if k != 'effectType'))
                if effects:
                    rate = info.get('metabolismRate', 0.5)
                    rows.append('<tr><td>' + E('; '.join(effects)) + '</td><td>' + E('; '.join(conditions) or 'Без дополнительного порога') + '</td><td>' + E(amount(rate)) + '</td></tr>')
        if rows:
            text += '<details><summary>Влияние на здоровье по данным реагента</summary><p>Знак «−» уменьшает урон, «+» увеличивает. Это базовые значения эффекта за срабатывание, а не суммарное лечение от всей дозы. Серверные множители, условия эффекта и масштабирование по количеству могут менять результат.</p><div class="dl-table"><table><thead><tr><th>Базовое изменение урона</th><th>Условие</th><th>Базовый расход за цикл, ед.</th></tr></thead><tbody>' + ''.join(rows) + '</tbody></table></div></details>'
        return text + '</div></details>'

    def reagent_group(self, group):
        keys = [key for key in self.data.raw['reagent'] if not self.data.raw['reagent'][key].get('abstract') and self.data.get('reagent', key).get('group', 'Unknown') == group]
        return '<div class="dl-generated"><p class="muted">' + E(GROUPS.get(group, group)) + f' · {len(keys)} реагентов. Откройте карточку, чтобы посмотреть состав и условия.</p>' + ''.join(self.reagent(key) for key in sorted(keys, key=lambda k: self.data.name('reagent', k).casefold())) + '</div>'

    def cooking(self, group=None, medical=False):
        recipes = [value for value in self.data.raw['microwaveMealRecipe'].values()
                   if not value.get('hideInGuidebook') and (group is None or value.get('group') == group)
                   and (not medical or 'MedicalAssembler' in str(value.get('recipeType', 'Microwave')))]
        if not recipes:
            return ''
        machines = {'Microwave': 'микроволновка', 'Oven': 'духовка', 'MedicalAssembler': 'медицинский сборщик',
                    'Slicing': 'нарезка', 'Grill': 'гриль', 'Assembler': 'сборщик',
                    'ArcFurnace': 'дуговая печь', 'PrecisionAssembler': 'точный сборщик'}
        text = '<div class="dl-cooking">'
        for recipe in sorted(recipes, key=lambda r: self.data.name('entity', r.get('result', '')).casefold()):
            result = recipe.get('result', '')
            result_name = self.data.name('entity', result)
            ingredients = [f'{E(self.data.name("entity", key))} × {E(amount(count))}' for key, count in recipe.get('solids', {}).items()]
            ingredients += [f'{self.reagent_link(key)} — {E(amount(count))} ед.' for key, count in recipe.get('reagents', {}).items()]
            recipe_type = recipe.get('recipeType', 'Microwave')
            machine = ', '.join(machines.get(str(key), str(key)) for key in (recipe_type if isinstance(recipe_type, list) else [recipe_type]))
            text += f'<details class="dl-food"><summary>{self.icon(result)}<span>{E(result_name)}</span></summary><div class="dl-reagent-body"><ul>' + ''.join(f'<li>{item}</li>' for item in ingredients) + '</ul>'
            text += f'<p><strong>Оборудование:</strong> {E(machine)}<br><strong>Время:</strong> {E(amount(recipe.get("time", 5)))} с.<br><strong>Результат:</strong> {E(result_name)} × {E(amount(recipe.get("resultCount", 1)))}</p>'
            if recipe.get('secretRecipe'):
                text += '<p class="muted">Особый рецепт: доступность зависит от игровых условий.</p>'
            text += '</div></details>'
        return text + '</div>'

    def technology(self, discipline):
        values = [value for value in self.data.raw['technology'].values() if value.get('discipline') == discipline and not value.get('hidden')]
        text = '<div class="dl-table"><table><thead><tr><th>Технология</th><th>Уровень / стоимость</th><th>Открываемые рецепты</th></tr></thead><tbody>'
        for tech in sorted(values, key=lambda r: (r.get('tier', 0), r['id'])):
            results = []
            for key in tech.get('recipeUnlocks', []):
                recipe = self.data.get('latheRecipe', key)
                name = self.data.name('entity', recipe.get('result', '')) or key
                materials = recipe.get('materials', {})
                costs = ', '.join(self.data.name('material', k) + ': ' + amount(v) for k, v in materials.items())
                results.append(E(name) + (f' <small>({E(costs)})</small>' if costs else ''))
            prerequisites = [self.data.loc(self.data.get('technology', key).get('name', key)) for key in tech.get('technologyPrerequisites', [])]
            text += '<tr><td>' + E(self.data.loc(tech.get('name', tech['id']))) + ('<p class="muted">Требует: ' + E(', '.join(prerequisites)) + '</p>' if prerequisites else '') + '</td><td>' + E(str(tech.get('tier', 0)) + ' / ' + amount(tech.get('cost', 10000))) + '</td><td>' + '<br>'.join(results) + '</td></tr>'
        return text + '</tbody></table></div><p class="muted">Стоимость материалов базовая; модификаторы станка и сборки могут её менять.</p>'

    def automation(self):
        keys = [key for key in self.data.raw['entity'] if not self.data.raw['entity'][key].get('abstract') and 'AutomationSlots' in self.data.components(key)]
        return '<div class="dl-entities">' + ''.join(self.entity(key, True) for key in sorted(keys)) + '</div>'

    def embed(self, kind, attrs):
        if kind == 'GuideEntityEmbed':
            return self.entity(attrs.get('Entity', ''))
        if kind == 'GuideReagentEmbed':
            return self.reagent(attrs.get('Reagent', ''))
        if kind == 'GuideReagentGroupEmbed':
            return self.reagent_group(attrs.get('Group', ''))
        if kind == 'GuideMicrowaveGroupEmbed':
            return self.cooking(attrs.get('Group', ''))
        if kind == 'GuideMedicalGroupEmbed':
            return self.cooking(medical=True)
        if kind == 'GuideTechDisciplineEmbed':
            return self.technology(attrs.get('Discipline', ''))
        if kind == 'GuideAutomationSlotsEmbed':
            return self.automation()
        raise ValueError(f'Unsupported guidebook embed: {kind}')
