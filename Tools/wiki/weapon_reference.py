"""Current ship-weapon specifications for the three legacy guidebook tables."""
import html
import math


PREFIX = 'guidebook/in_game/imported/mono/specifications/'
CATALOGUES = {
    'ballistics': ('WeaponTurretL85Autocannon', 'WeaponTurretRAC30', 'WeaponTurretDravon',
                   'WeaponTurretAK570', 'WeaponTurretLongbow', 'WeaponTurretTarnyxReload',
                   'WeaponTurretCyrexa', 'WeaponTurretBofors', 'WeaponTurretCharonReload',
                   'WeaponTurretThanatos', 'WeaponTurretHades'),
    'energy': ('WeaponTurretType35', 'WeaponTurretType54', 'WeaponTurretType241',
               'WeaponTurretM220', 'WeaponTurretDymere', 'WeaponLaserTurretL1Phalanx',
               'WeaponLaserTurretApollo'),
    'missiles': ('WeaponTurretVanyk', 'WeaponTurretVespera', 'WeaponTurretTovek', 'WeaponTurretASM220'),
}
SIZE = {'Light': 'Лёгкое', 'Medium': 'Среднее', 'Heavy': 'Тяжёлое', 'Superheavy': 'Сверхтяжёлое',
        'Spinal': 'Спинальное'}
TYPE = {'Ballistic': 'Баллистическое', 'Energy': 'Энергетическое', 'Missile': 'Ракетное'}
E = lambda value: html.escape(str(value), quote=True)


def number(value):
    return f'{value:g}'.replace('.', ',') if isinstance(value, (float, int)) else str(value)


def _projectile(data, identifier):
    seen = set()
    while identifier and identifier not in seen:
        seen.add(identifier)
        components = data.components(identifier)
        cartridge = components.get('CartridgeAmmo', {})
        if not cartridge.get('proto'):
            return identifier, components
        identifier = cartridge['proto']
    return '', {}


def _ammunition(data, components):
    for kind in ('HitscanBatteryAmmoProvider', 'ProjectileBatteryAmmoProvider'):
        if kind not in components:
            continue
        provider = components[kind]
        maximum = components.get('Battery', {}).get('maxCharge')
        cost = provider.get('fireCost')
        text = 'Энергия'
        if isinstance(maximum, (int, float)) and isinstance(cost, (int, float)) and cost > 0:
            text += f'<br>Полный заряд: {math.floor(maximum / cost)} выстр.'
        return text, provider.get('proto', '')
    provider = components.get('BallisticAmmoProvider', {})
    magazine = components.get('ItemSlots', {}).get('slots', {}).get('gun_magazine', {}).get('startingItem')
    if magazine:
        provider = data.components(magazine).get('BallisticAmmoProvider', {})
    text = E(data.name('entity', magazine)) + '<br>' if magazine else ''
    if provider.get('infiniteUnspawned'):
        text += 'Без ограничения боезапаса'
    elif isinstance(provider.get('capacity'), (int, float)):
        text += f'Ёмкость: {E(number(provider["capacity"]))}'
    else:
        text += 'Ручная загрузка'
    projectile = provider.get('proto', '')
    if not projectile:
        text += '<br>Снаряд выбирается при загрузке'
    return text, projectile


def _fire_mode(gun):
    parts = []
    if 'Burst' in str(gun.get('selectedMode', '')):
        if gun.get('shotsPerBurst') is not None:
            parts.append(f'Серия: {E(number(gun["shotsPerBurst"]))} выстр.')
        if gun.get('burstFireRate') is not None:
            parts.append(f'{E(number(gun["burstFireRate"]))} выстр./с внутри серии')
        if gun.get('burstCooldown') is not None:
            parts.append(f'Пауза: {E(number(gun["burstCooldown"]))} с')
    elif gun.get('fireRate') is not None:
        parts.append(f'{E(number(gun["fireRate"]))} выстр./с')
    return '<br>'.join(parts) or '—'


def _flight(data, gun, ammunition):
    _, projectile = _projectile(data, ammunition)
    raycast = projectile.get('HitscanBasicRaycast', {})
    if raycast.get('maxDistance') is not None:
        return f'Мгновенный луч<br>Дальность: {E(number(raycast["maxDistance"]))} м'
    parts = []
    seeking = projectile.get('TargetSeeking', {})
    if seeking.get('maxSpeed') is not None:
        parts.append(f'До {E(number(seeking["maxSpeed"]))} м/с с наведением')
    elif gun.get('projectileSpeed') is not None:
        parts.append(f'Пуск: {E(number(gun["projectileSpeed"]))} м/с')
    lifetime = projectile.get('TimedDespawn', {}).get('lifetime')
    if isinstance(lifetime, (int, float)):
        parts.append(f'Время существования: {E(number(lifetime))} с')
    return '<br>'.join(parts) or 'Зависит от заряженного снаряда'


def build_weapon_reference(path, data, blocks):
    if not path.startswith(PREFIX) or path[len(PREFIX):] not in CATALOGUES:
        return None
    catalogue = CATALOGUES[path[len(PREFIX):]]
    rows = []
    for identifier in catalogue:
        if not data.get('entity', identifier):
            continue
        components = data.components(identifier)
        gun = components.get('Gun', {})
        if not gun:
            continue
        mount = components.get('HardpointAnchorableOnly', {})
        size = mount.get('size', components.get('ShipGunClass', {}).get('shipClass', ''))
        category = mount.get('class', components.get('ShipGunType', {}).get('shipType', ''))
        mounting = E(SIZE.get(size, size)) + ('<br>' + E(TYPE.get(category, category)) if category else '')
        ammunition, projectile = _ammunition(data, components)
        model = blocks.icon(identifier) + '<strong>' + E(data.name('entity', identifier)) + '</strong>'
        rows.append('<tr data-weapon-id="' + E(identifier) + '"><th scope="row">' + model + '</th><td>' + mounting + '</td><td>' + _fire_mode(gun) + '</td><td>' + ammunition + '</td><td>' + _flight(data, gun, projectile) + '</td></tr>')
    return ('<p>Характеристики корабельных орудий и их штатных боеприпасов. Класс крепления определяет совместимость с оружейной точкой корабля.</p>'
            '<div class="dl-table dl-weapon-reference"><table><thead><tr><th scope="col">Орудие</th><th scope="col">Крепление</th><th scope="col">Темп стрельбы</th><th scope="col">Боепитание</th><th scope="col">Снаряд или луч</th></tr></thead><tbody>'
            + ''.join(rows) + '</tbody></table></div>'
            '<p>Темп указан для базового режима. Серверные условия и питание могут ограничивать непрерывную стрельбу. Число выстрелов от полного заряда рассчитано без подзарядки.</p>'
            '<p>Для летящих снарядов указаны скорость и время существования; столкновения, наведение и движение корабля влияют на фактический полёт. Для мгновенных лучей указана дальность.</p>')
