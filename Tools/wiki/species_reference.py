"""Playable species articles derived from Station bodies, sprites and mechanics.

Only the editorial notes are handwritten. Numeric values and breathing gases
come from current prototypes. New articles and client routes share this map.
"""
from html import escape as E
from reference_blocks import DAMAGE, amount

PREFIX = 'guidebook/in_game/'
SPECIES_ARTICLES = {
    'Human': 'mobs/human', 'Arachnid': 'mobs/arachnid',
    'Diona': 'imported/nf/mobs/diona', 'Dwarf': 'imported/nf/mobs/dwarf',
    'Goblin': 'imported/nf/mobs/goblin', 'Moth': 'mobs/moth',
    'Reptilian': 'mobs/mono/mono-reptilian', 'SlimePerson': 'mobs/slime-person',
    'Vox': 'mobs/vox', 'Demon': 'mobs/demon', 'Synth': 'mobs/synth',
    'Chitinid': 'mobs/chitinid', 'Feroxi': 'mobs/feroxi', 'Harpy': 'mobs/dv/harpy',
    'Rodentia': 'mobs/rodentia', 'Vulpkanin': 'mobs/vulpkanin',
    'IPC': 'mobs/ipcs', 'Tajaran': 'mobs/tajaran', 'Resomi': 'mobs/resomi',
    'Hydrakin': 'mobs/hydrakin', 'Ariral': 'mobs/shiba-station/ariral',
    'Oni': 'mobs/oni', 'Felinid': 'mobs/felinid', 'Protogen': 'mobs/protogen',
    'Yowie': 'mobs/yowie', 'Skeleton': 'mobs/skeleton',
    'Gingerbread': 'mobs/gingerbread', 'Asakim': 'mobs/asakim',
}
ALIASES = {'mobs/diona': 'Diona', 'mobs/dwarf': 'Dwarf', 'mobs/reptilian': 'Reptilian'}
NOTES = {
    'Human': 'Человек — базовый органический гуманоид. Ему нужны дыхательная смесь, еда и вода; лечение подбирают по типу повреждений.',
    'Arachnid': 'Арахнид — членистоногий гуманоид с медьсодержащей кровью. Следите за дыханием: повреждение от удушья накапливается быстрее, чем у человека.',
    'Diona': 'Диона — растительный организм, объединяющий нимф. Вместо крови в сосудах циркулирует древесный сок. Особенно опасны ожоги.',
    'Dwarf': 'Дворф — невысокий органический гуманоид. Небольшой рост не заменяет защиту от разгерметизации; удушье для него опаснее, чем для человека.',
    'Goblin': 'Гоблин — небольшой гуманоид с особой кровью и повышенной устойчивостью к ядам. Устойчивость не делает отравление безопасным.',
    'Moth': 'Ниан — гуманоид с крыльями и усиками. Пламя наносит ему особенно большой урон; тушите возгорание немедленно.',
    'Reptilian': 'Унатх — чешуйчатый гуманоид. Лучше переносит жару, но чувствителен к охлаждению и тупым ударам.',
    'SlimePerson': 'Слаймолюд — органический гуманоид со слизью вместо крови. Для дыхания ему нужен азот. Контакт с водой наносит термический урон.',
    'Vox': 'Вокс дышит азотом. Кислород отравляет его: используйте подходящую маску и азотный баллон, проверяйте смесь перед включением подачи.',
    'Demon': 'Аркана — органический гуманоид с рогами и хвостом. Несмотря на внешность, нуждается в дыхании, пище и воде; переносит широкий диапазон температуры тела.',
    'Synth': 'Синт — механический гуманоид с настраиваемой оболочкой. Работает от батареи; органического дыхания, голода, жажды и кровотечения у него нет.',
    'IPC': 'IPC — механический гуманоид с батарейным питанием. Не нуждается в воздухе и пище. Особенно уязвим к электрическому урону и перегреву.',
    'Chitinid': 'Хитинид — гуманоид с хитиновым покровом и кровью насекомых. Его устойчивость к радиации сочетается с уязвимостью к холоду и электричеству.',
    'Feroxi': 'Ферокси — органический гуманоид с особенностями обмена веществ. Его устойчивость к холоду и тупым ударам сопровождается повышенной чувствительностью к порезам.',
    'Harpy': 'Гарпия — пернатый гуманоид. Дышит чаще человека, поэтому быстрее расходует запас баллона и чувствительна к загрязнённому воздуху. Может подражать звукам и петь.',
    'Rodentia': 'Родентия — небольшой гуманоид. Особенно уязвима к механическим повреждениям; небольшой размер не обеспечивает защиту от ударов и выстрелов.',
    'Vulpkanin': 'Вульпканин — покрытый шерстью гуманоид. Нуждается в обычной медицинской помощи; ожоги опаснее, чем для человека.',
    'Tajaran': 'Таяр — покрытый шерстью гуманоид, лучше переносящий холод. Чувствителен к жаре и механическим травмам; тёплая одежда не защищает от перегрева.',
    'Resomi': 'Резоми — небольшой пернатый гуманоид. Лучше переносит холод, но более уязвим к механическим повреждениям и ожогам.',
    'Hydrakin': 'Гидракин — органический гуманоид с аммиачной кровью и необычным газовым обменом. Переносимость отдельных газов не гарантирует безопасность горячей или разреженной атмосферы.',
    'Ariral': 'Арирал — высокий гуманоид с токсичной кровью. Устойчив к ядам, но кровопотеря, порезы, уколы, холод и радиация опаснее для него, чем для человека.',
    'Oni': 'Ёкай — органический гуманоид с повышенной устойчивостью к механическим повреждениям. Эта устойчивость не распространяется автоматически на ожоги, удушье и яды.',
    'Felinid': 'Фелинид — гуманоид с кошачьими чертами. Его внешний вид не меняет необходимость дышать и получать медицинскую помощь.',
    'Protogen': 'Протоген сочетает органическую физиологию с технологическими чертами. В этой сборке он дышит и имеет кровь: порядок ремонта IPC к нему не относится.',
    'Yowie': 'Йови — крупный органический гуманоид с собственным обменом веществ. Не переносите на него рецепты и дозировки из описаний других сборок.',
    'Skeleton': 'Скелет не нуждается в дыхании, пище и воде и не имеет кровотечения. При этом у него есть запас прочности и состояние смерти.',
    'Gingerbread': 'Пряничный гуманоид имеет сахар вместо крови. Его физиология и устойчивости отличаются от человеческих.',
    'Asakim': 'Азаким — чешуйчатый гуманоид с изменённой физиологией. Лучше переносит высокую температуру тела, но нуждается в дыхании и лечении.',
}


def field(data, entity, component, key, default=None):
    """Resolve a component field atomically, including dictionary replacement.

    Deep-merging thresholds would retain a parent's obsolete death threshold.
    """
    value = data.raw['entity'].get(entity, {})
    for comp in value.get('components', []):
        if comp.get('type') == component and key in comp:
            return comp[key]
    parents = value.get('parent', [])
    if isinstance(parents, str):
        parents = [parents]
    missing = object()
    for parent in reversed(parents):
        result = field(data, parent, component, key, missing)
        if result is not missing:
            return result
    return default


def organ_types(data, entity):
    body = data.get('body', field(data, entity, 'Body', 'prototype', ''))
    result = set()
    for slot in body.get('slots', {}).values():
        for organ in slot.get('organs', {}).values():
            if isinstance(organ, str) and 'Lung' in data.components(organ):
                result.update(field(data, organ, 'Metabolizer', 'metabolizerTypes', []))
    return result


def matches_organs(conditions, types):
    return all((condition.get('type') in types) == condition.get('shouldHave', True)
               for condition in conditions if condition.get('effectType') == 'OrganType')


def breathing_gases(data, entity):
    types = organ_types(data, entity)
    if not types:
        return []
    # A reagent can both replenish and displace oxygen for the same organ type.
    # Evaluate the combined effect, rather than finding any positive entry.
    return sorted(key for key, reagent in data.raw['reagent'].items()
                  if sum(effect.get('factor', 1)
                         for effect in reagent.get('metabolisms', {}).get('Gas', {}).get('effects', [])
                         if effect.get('effectType') == 'Oxygenate' and
                         matches_organs(effect.get('conditions', []), types)) > 0)


def species_for_path(path):
    relative = path.removeprefix(PREFIX)
    return ALIASES.get(relative) or next((key for key, value in SPECIES_ARTICLES.items() if value == relative), None)


def build_species_index(data, blocks):
    cards = []
    for species, relative in sorted(SPECIES_ARTICLES.items(), key=lambda item: data.loc(data.raw['species'].get(item[0], {}).get('name', item[0]))):
        value = data.raw['species'].get(species, {})
        if not value.get('roundStart'):
            continue
        cards.append('<a class="dl-entity" href="/ru/' + PREFIX + relative + '">' + blocks.icon(value['dollPrototype']) + '<strong>' + E(data.loc(value['name'])) + '</strong></a>')
    return '<p>Выберите вид для описания физиологии, устойчивостей и возвращения после смерти. Здесь показаны виды, разрешённые прототипами для старта раунда; сервер может дополнительно ограничивать выбор.</p><div class="dl-entities">' + ''.join(cards) + '</div>'


def build_species_reference(path, data, blocks):
    if path == PREFIX + 'mobs/species':
        return build_species_index(data, blocks)
    species_id = species_for_path(path)
    if not species_id or species_id not in data.raw['species']:
        return None
    species = data.raw['species'][species_id]
    entity = species['prototype']
    components = data.components(entity)
    name = data.loc(species['name'])
    icon = blocks.icon(species['dollPrototype'])
    if not icon:
        raise ValueError('Missing actual species portrait: ' + species_id)
    result = f'<figure class="dl-species-portrait" data-entity-id="{E(species["dollPrototype"])}">{icon}<figcaption>{E(name)}</figcaption></figure>'
    result += '<p>' + E(NOTES[species_id]) + '</p>'
    if not species.get('roundStart'):
        result += '<p>Этот вид не входит в стандартный выбор при создании персонажа.</p>'
    rows = []
    gases = breathing_gases(data, entity)
    gas_names = {gas.get('reagent'):data.loc(gas['name']) for gas in data.raw['gas'].values()}
    rows.append(('Газы, поддерживающие дыхание', ', '.join(gas_names.get(gas,data.name('reagent',gas)) for gas in gases) if gases else 'Дыхание не требуется'))
    blood = field(data, entity, 'Bloodstream', 'bloodReagent', 'Blood')
    rows.append(('Кровь', data.name('reagent', blood) if 'Bloodstream' in components else 'Кровеносной системы нет'))
    rows.append(('Питание и вода', 'Нужны' if 'Hunger' in components and 'Thirst' in components else 'Обычные голод и жажда отсутствуют'))
    thresholds = field(data, entity, 'MobThresholds', 'thresholds', {})
    states = field(data, entity, 'MobState', 'allowedStates', ['Alive', 'Critical', 'Dead'])
    for state, title in [('Critical', 'Порог критического состояния'), ('Dead', 'Порог смерти')]:
        values = [float(key) for key, value in thresholds.items() if value == state]
        if values and state in states:
            rows.append((title, amount(min(values)) + ' суммарного урона'))
    if 'Temperature' in components:
        cold = field(data, entity, 'Temperature', 'coldDamageThreshold', 260)
        heat = field(data, entity, 'Temperature', 'heatDamageThreshold', 360)
        rows.append(('Температура тела без температурного урона', f'{amount(cold)}–{amount(heat)} K ({amount(round(cold-273.15,2))}…{amount(round(heat-273.15,2))} °C)'))
    result += '<h2 id="physiology">Физиология</h2><table><tbody>' + ''.join(f'<tr><th>{E(k)}</th><td>{E(v)}</td></tr>' for k,v in rows) + '</tbody></table>'
    result += '<p>Температура тела отличается от температуры воздуха. Способность использовать газ для дыхания не делает любую его концентрацию или примеси безопасными. Переносимость холода или жары не заменяет защиту от давления, огня и отсутствия дыхательной смеси.</p>'
    modifiers = data.get('damageModifierSet', field(data, entity, 'Damageable', 'damageModifierSet', ''))
    coefficients = modifiers.get('coefficients', {})
    special = [(DAMAGE.get(key, data.loc('damage-type-' + key.lower())), value) for key,value in coefficients.items() if value != 1]
    if special:
        result += '<h2 id="damage">Уязвимости и устойчивости</h2><p>Множитель входящего урона до учёта экипировки: меньше ×1 — устойчивость, больше ×1 — уязвимость. Он не отменяет эффекты, игнорирующие сопротивления.</p><ul>'
        result += ''.join(f'<li>{E(label)}: ×{amount(value)}.</li>' for label,value in special) + '</ul>'
    result += '<h2 id="revival">Возвращение после смерти</h2>'
    if 'DeadStartupButton' in components:
        result += '<ol><li>Восстановите повреждённый корпус подходящим ремонтным инструментом. Сварка исправляет механические повреждения; для остальных типов урона проверьте возможности инструмента.</li><li>Снизьте суммарный урон строго ниже порога смерти. Один ремонт не запускает отключённого синтетика.</li><li>Проверьте заряд батареи. Откройте контекстное меню тела и выберите «Перезапустить»; дождитесь завершения действия.</li></ol><p>Дефибриллятор не заменяет ремонт и запуск. Электрический разряд по отключённому синтетику может разрядить батарею. Если корпус снова отключается, проверьте повреждения и питание.</p>'
    else:
        result += '<ol><li>Доставьте целое тело в медблок и устраните причину смерти: травмы, кровопотерю, удушье, яд или температуру.</li><li>Подготовьте тело к дефибрилляции: после лечебного импульса суммарный урон должен быть строго ниже порога смерти. Один разряд не устраняет все виды повреждений.</li><li>Примените включённый и заряженный дефибриллятор. После восстановления продолжайте лечение критического состояния и обеспечьте подходящую дыхательную смесь, если она требуется.</li></ol><p>Сгнившее тело и состояние с запретом воскрешения блокируют обычную дефибрилляцию. Для возвращения игрока нужна доступная связь разума с телом; отсутствие игрока не исправляется дополнительными разрядами.</p>'
    if species_id == 'Diona':
        result += '<p>Особенность дионы: при извлечении органа-нимфы появляется нимфа. Если у органа включён перенос разума, управление переходит к ней. Это не автоматическое восстановление прежнего тела дионы.</p>'
    if species_id == 'SlimePerson':
        result += '<p>Для слаймолюда подготовьте азотную подачу и избегайте воды на теле: иначе после оживления снова начнут накапливаться повреждения.</p>'
    if species_id == 'Vox':
        result += '<p>Для вокса перед оживлением обеспечьте азот и исключите кислород из подаваемой смеси.</p>'
    result += '<p><a href="/ru/guidebook/in_game/mobs/species">Другие виды</a> · <a href="/ru/guidebook/in_game/medical/medical-doctor">Медицинская помощь</a></p>'
    return result
