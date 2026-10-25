"""Render assembler recipes from the packs actually assigned to each machine."""
import html


ECONOMY = 'guidebook/in_game/imported/mono/economy/'
MEDICAL = 'guidebook/in_game/imported/nf/medical/medical-recipes'
MACHINES = {
    MEDICAL: ('MedicalAssembler',),
    ECONOMY + 'arc-furnace': ('IndustrialFurnaceEconomy', 'IndustrialFurnaceEconomyCompact'),
    ECONOMY + 'precision-assembler': ('PrecisionAssemblerEconomy', 'PrecisionAssemblerEconomyCompact'),
}
E = lambda value: html.escape(str(value), quote=True)


def number(value):
    return f'{value:g}'.replace('.', ',') if isinstance(value, (int, float)) else str(value)


def _recipes(data, pack_ids):
    result = {}
    for identifier in pack_ids:
        for recipe_id in data.get('latheRecipePack', identifier).get('recipes', []):
            recipe = data.get('latheRecipe', recipe_id)
            if recipe and not data.raw['latheRecipe'].get(recipe_id, {}).get('abstract'):
                result[recipe_id] = recipe
    return result


def _output(data, recipe):
    result = recipe.get('result', '')
    count = recipe.get('resultCount', 1)
    if result:
        components = data.components(result)
        spawner = components.get('SpawnOnDespawn', {})
        if 'TimedDespawn' in components and spawner.get('prototype'):
            # SpawnOnDespawnSystem iterates from zero through Count inclusively.
            count *= spawner.get('count', 0) + 1
            result = spawner['prototype']
    return result, count


def _ingredients(data, recipe, blocks):
    parts = []
    for identifier, amount in recipe.get('entities', {}).items():
        parts.append(f'{E(data.name("entity", identifier))} × {E(number(amount))}')
    for identifier, amount in recipe.get('materials', {}).items():
        parts.append(f'{E(data.name("material", identifier))}: {E(number(amount))} ед. материала')
    for identifier, amount in recipe.get('reagents', {}).items():
        parts.append(f'{blocks.reagent_link(identifier)}: {E(number(amount))} ед.')
    return '<ul>' + ''.join('<li>' + part + '</li>' for part in parts) + '</ul>' if parts else '—'


def _table(data, recipes, blocks, researched=False):
    rows = []
    def sort_key(item):
        identifier, recipe = item
        result, _ = _output(data, recipe)
        return (data.name('entity', result) if result else data.loc(recipe.get('name', identifier))).casefold(), identifier
    for identifier, recipe in sorted(recipes.items(), key=sort_key):
        result, count = _output(data, recipe)
        products = []
        if result:
            name = data.name('entity', result)
            label = '<strong>' + E(name) + '</strong>' + (' × ' + E(number(count)) if count != 1 else '')
            products.append(blocks.icon(result) + label)
        for material, amount in recipe.get('materialResult', {}).items():
            products.append(f'{E(data.name("material", material))}: {E(number(amount))} ед. материала')
        for reagent, amount in recipe.get('resultReagents', {}).items():
            products.append(f'{blocks.reagent_link(reagent)}: {E(number(amount))} ед.')
        if not products:
            products.append(E(data.loc(recipe.get('name', identifier))))
        if 'AltRecipes' in recipe.get('categories', []):
            products.append('<small>Альтернативный состав</small>')
        if researched:
            technologies = [data.loc(value.get('name', key)) for key in data.raw['technology']
                            if identifier in (value := data.get('technology', key)).get('recipeUnlocks', [])]
            if technologies:
                products.append('<small>Исследование: ' + E('; '.join(technologies)) + '</small>')
        time = recipe.get('completetime')
        duration = E(number(time)) + ' с' if time is not None else '—'
        rows.append('<tr data-lathe-recipe-id="' + E(identifier) + '"><th scope="row">' + '<br>'.join(products) + '</th><td>' + _ingredients(data, recipe, blocks) + '</td><td>' + duration + '</td></tr>')
    return '<div class="dl-table dl-industrial-reference"><table><thead><tr><th scope="col">Результат</th><th scope="col">Ингредиенты</th><th scope="col">Базовое время</th></tr></thead><tbody>' + ''.join(rows) + '</tbody></table></div>'


def build_industrial_reference(path, data, blocks):
    if path == ECONOMY + 'economy-recipes':
        return ('<p>Производственные рецепты для переработки материалов и изготовления компонентов.</p>'
                '<ul><li><a href="/ru/' + ECONOMY + 'arc-furnace">Дуговая печь: сплавы, бронеплиты, кремний и иридит</a></li>'
                '<li><a href="/ru/' + ECONOMY + 'precision-assembler">Точный сборщик: электроника, компоненты и торговые грузы</a></li></ul>')
    if path not in MACHINES:
        return None
    devices = [identifier for identifier in MACHINES[path] if 'Lathe' in data.components(identifier)]
    if not devices:
        return None
    static, dynamic = {}, {}
    for identifier in devices:
        lathe = data.components(identifier)['Lathe']
        static.update(_recipes(data, lathe.get('staticPacks', [])))
        dynamic.update(_recipes(data, lathe.get('dynamicPacks', [])))
    dynamic = {key: value for key, value in dynamic.items() if key not in static}
    machines = '<div class="dl-entities">' + ''.join('<div class="dl-entity" data-entity-id="' + E(identifier) + '">' + blocks.icon(identifier) + '<strong>' + E(data.name('entity', identifier)) + '</strong></div>' for identifier in devices) + '</div>'
    text = machines + '<h2>Штатные рецепты</h2>' + _table(data, static, blocks)
    if dynamic:
        text += '<h2>После исследований</h2>' + _table(data, dynamic, blocks, researched=True)
    return text + '<p>Время и расход материалов приведены для базового рецепта. Скорость станка, детали и модификаторы могут менять время и материальные затраты; количество предметов и реагентов указано отдельно.</p>'
