"""Render default keys from the game's bindings, without user-specific settings."""
from collections import Counter
import html
from pathlib import Path
import re

import yaml


CONTROLS_PATH = 'guidebook/in_game/new-player/controls/controls'
CONTROLS_CSS = '''
.dlwiki kbd{display:inline-block;white-space:nowrap;font:inherit;font-size:.92em;padding:1px 6px;margin:1px 0;border:1px solid var(--line);border-bottom-width:2px;border-radius:3px;background:var(--raised);color:var(--ink)}
.dlwiki .dl-controls-table{overflow-x:auto}.dlwiki .dl-controls-table table{min-width:440px}.dlwiki .dl-controls-table td:first-child{width:34%}
'''

# Earlier exports replaced the dynamic Guidebook tag with a plain action marker.
MARKERS = re.compile(
    r'\[keybind\s*=\s*["\'](?P<keybind>\w+)["\']\s*/?\]'
    r'|\[(?:действие|action)\s*:\s*[`"\']?(?P<action>\w+)[`"\']?\s*\]',
    re.I,
)
INLINE_MARKERS = re.compile(
    r'(?:(?P<noun>действие|действием|действия|действию)\s+)?'
    r'(?:<code\b[^>]*>(?P<html>\w+)</code>|`(?P<markdown>\w+)`)', re.I)

KEY_NAMES = {
    'Control': 'Ctrl', 'MouseLeft': 'ЛКМ', 'MouseRight': 'ПКМ',
    'MouseMiddle': 'СКМ', 'Space': 'Пробел', 'Return': 'Enter',
    'Escape': 'Esc', 'BackSpace': 'Backspace', 'Equal': '=', 'Minus': '−',
    'Period': '.', 'Comma': ',', 'SemiColon': ';', 'Apostrophe': "'",
    'LBracket': '[', 'RBracket': ']', 'Backslash': '\\', 'Slash': '/',
    'Tilde': '`', 'NumpadAdd': 'Num +', 'NumpadSubtract': 'Num −',
    'NumpadEnter': 'Num Enter',
}


def key_name(value):
    if value.startswith('NumpadNum'):
        return 'Num ' + value.removeprefix('NumpadNum')
    if re.fullmatch(r'Num\d', value):
        return value[-1]
    return KEY_NAMES.get(value, value)


def preserved_movement(content):
    """Extract the existing section verbatim, including its heading and markup."""
    headings = list(re.finditer(r'<h([1-6])\b[^>]*>(.*?)</h\1>', content, re.S | re.I))
    for index, heading in enumerate(headings):
        title = html.unescape(re.sub('<[^>]*>', '', heading[2])).strip()
        if title.casefold() != 'движение':
            continue
        end = next((other.start() for other in headings[index + 1:]
                    if int(other[1]) <= int(heading[1])), len(content))
        return content[heading.start():end]
    heading = re.search(r'(?m)^(#{1,6})\s+Движение\s*$', content)
    if heading:
        rest = content[heading.end():]
        following = re.search(r'(?m)^#{1,' + str(len(heading[1])) + r'}\s+', rest)
        end = heading.end() + following.start() if following else len(content)
        return content[heading.start():end]
    raise ValueError('Controls: cannot preserve the existing Движение section')


class ControlsReference:
    def __init__(self, station):
        source = Path(station) / 'Resources/keybinds.yml'
        self.bindings = {}
        for value in yaml.safe_load(source.read_text(encoding='utf-8-sig'))['binds']:
            keys = [value.get('mod' + str(index)) for index in range(1, 4)]
            keys = [key_name(key) for key in keys if key and key != 'Unknown']
            keys.append(key_name(value['key']))
            combo = ' + '.join(keys)
            variants = self.bindings.setdefault(value['function'], [])
            if combo not in variants:
                variants.append(combo)

    def key(self, action):
        if action not in self.bindings:
            raise ValueError('No default key binding: ' + action)
        return ' или '.join('<kbd>' + html.escape(combo) + '</kbd>'
                           for combo in self.bindings[action])

    def replace_markers(self, text):
        text = MARKERS.sub(lambda match: self.key(match['keybind'] or match['action']), text)
        def replace(match):
            action = match['html'] or match['markdown']
            if action not in self.bindings and not match['noun']:
                return match[0]
            noun = {'действие': 'клавишу', 'действием': 'клавишей',
                    'действия': 'клавиши', 'действию': 'клавише'}.get((match['noun'] or '').lower())
            return (noun + ' ' if noun else '') + self.key(action)
        return INLINE_MARKERS.sub(replace, text)

    def audit(self, pages):
        counts = Counter()
        affected = []
        for page in pages:
            actions = [match['keybind'] or match['action']
                       for match in MARKERS.finditer(page.get('content', ''))]
            actions.extend(match['html'] or match['markdown']
                           for match in INLINE_MARKERS.finditer(page.get('content', ''))
                           if match['noun'] or (match['html'] or match['markdown']) in self.bindings)
            if actions:
                counts.update(actions)
                affected.append({'path': page['path'], 'markers': len(actions),
                                 'actions': sorted(set(actions))})
        return {'pagesScanned': len(pages), 'pagesWithMarkers': len(affected),
                'markers': sum(counts.values()), 'actions': dict(sorted(counts.items())),
                'unknownActions': sorted(set(counts) - self.bindings.keys()),
                'pages': affected}

    def table(self, rows):
        return ('<div class="dl-controls-table"><table><thead><tr><th>Клавиша</th>'
                '<th>Действие</th></tr></thead><tbody>' + ''.join(
                    '<tr><td>' + self.key(action) + '</td><td>' + description + '</td></tr>'
                    for action, description in rows) + '</tbody></table></div>')

    def editorial_body(self, path, text):
        """Repair the action-bearing paragraphs, leaving neighbouring prose intact."""
        if path == CONTROLS_PATH:
            return self.controls_body(text)
        if path == 'guidebook/in_game/books/the-book-of-control':
            movement = '<h2 id="движение">Движение</h2>' + self.table([
                ('MoveUp', 'Двигаться вверх.'), ('MoveLeft', 'Двигаться влево.'),
                ('MoveDown', 'Двигаться вниз.'), ('MoveRight', 'Двигаться вправо.'),
                ('Walk', 'Идти шагом, пока клавиша удерживается.'),
                ('DeepLagoonSpeedBoost', 'Ускориться, пока клавиша удерживается. Расходует выносливость.'),
                ('TryPullObject', 'Начать тащить объект под курсором.'),
                ('MovePulledObject', 'Переместить объект, который вы тащите, к курсору.'),
                ('ReleasePulledObject', 'Перестать тащить объект.'),
            ])
            return self.controls_body(movement + '<h2>Следующий раздел</h2>')
        replacements = ACTION_PROSE.get(path, {})
        def repair(match):
            actions = tuple(value['html'] or value['markdown'] for value in INLINE_MARKERS.finditer(match[0])
                            if (value['html'] or value['markdown']) in self.bindings)
            if actions not in replacements:
                return match[0]
            replacement = replacements[actions]
            if isinstance(replacement, list):
                replacement = next((prose for clue, prose in replacement if clue in match[0]), None)
                if replacement is None:
                    return match[0]
            return '<' + match[1] + '>' + self.replace_markers(replacement) + '</' + match[1] + '>'
        text = re.sub(r'<(p|li)\b[^>]*>[\s\S]*?</\1>', repair, text)
        return self.replace_markers(text)

    def controls_body(self, previous):
        movement = preserved_movement(previous)
        sections = [
            ('Взаимодействие', [
                ('Use', 'Взаимодействовать с объектом под курсором. Пустой рукой — взять предмет; с предметом в руке — применить его к цели.'),
                ('ActivateItemInWorld', 'Активировать объект под курсором, не перекладывая предмет из руки: например, открыть дверь.'),
                ('AltActivateItemInWorld', 'Выполнить альтернативное взаимодействие с объектом под курсором. Результат зависит от объекта.'),
                ('UIRightClick', 'Открыть контекстное меню: выбрать предмет из стопки или доступное действие.'),
                ('ExamineEntity', 'Осмотреть объект под курсором: прочитать название, описание и сведения о его состоянии.'),
                ('Point', 'Указать на объект под курсором.'),
                ('DeepLagoonOpenInteractions', 'Открыть панель взаимодействий с персонажем под курсором.'),
            ]),
            ('Руки и предметы', [
                ('SwapHands', 'Сменить активную руку.'),
                ('ActivateItemInHand', 'Активировать предмет в активной руке.'),
                ('AltActivateItemInHand', 'Выполнить альтернативное действие предметом в активной руке.'),
                ('Drop', 'Положить предмет из активной руки на пол.'),
                ('ThrowItemInHand', 'Бросить предмет из активной руки в направлении курсора.'),
            ]),
            ('Инвентарь и хранилища', [
                ('OpenInventoryMenu', 'Открыть инвентарь персонажа.'),
                ('OpenBackpack', 'Открыть рюкзак.'),
                ('OpenBelt', 'Открыть пояс.'),
                ('MoveStoredItem', 'В окне хранилища: нажать на предмет, чтобы взять его; удерживать, чтобы перетащить.'),
                ('RotateStoredItem', 'Повернуть предмет при размещении или перетаскивании в хранилище.'),
                ('SmartEquipBackpack', 'Убрать предмет из руки в рюкзак либо достать из него предмет, если рука пуста.'),
                ('SmartEquipBelt', 'Убрать предмет из руки в пояс либо достать из него предмет, если рука пуста.'),
            ]),
            ('Камера', [
                ('ZoomIn', 'Приблизить камеру.'),
                ('ZoomOut', 'Отдалить камеру.'),
                ('ResetZoom', 'Вернуть исходный масштаб.'),
                ('CameraRotateLeft', 'Повернуть камеру влево.'),
                ('CameraRotateRight', 'Повернуть камеру вправо.'),
                ('CameraReset', 'Выровнять камеру по повороту текущей сетки.'),
            ]),
            ('Чат и окна', [
                ('FocusChatInputWindow', 'Перейти к вводу сообщения в чат.'),
                ('CycleChatChannelForward', 'Переключить канал чата вперёд.'),
                ('CycleChatChannelBackward', 'Переключить канал чата назад.'),
                ('OpenCharacterMenu', 'Открыть сведения о персонаже и его задачах.'),
                ('OpenCraftingMenu', 'Открыть меню строительства.'),
                ('OpenGuidebook', 'Открыть игровое руководство.'),
                ('OpenAHelp', 'Открыть обращение к администрации.'),
                ('ShowEscapeMenu', 'Открыть игровое меню.'),
            ]),
        ]
        result = ('<p>Клавиши по умолчанию. Свои назначения можно посмотреть и изменить '
                  'в меню «Настройки» → «Управление». ЛКМ, ПКМ и СКМ — левая, правая и средняя '
                  'кнопки мыши; Num — цифровой блок клавиатуры.</p>\n' + movement)
        for index, (title, rows) in enumerate(sections):
            result += '<h2 id="controls-' + str(index) + '">' + title + '</h2>\n' + self.table(rows)
        return result


ACTION_PROSE = {
    'guidebook/in_game/imported/nf/piloting': {
        ('ShuttleStrafeUp', 'ShuttleStrafeDown', 'ShuttleStrafeLeft', 'ShuttleStrafeRight',
         'ShuttleRotateLeft', 'ShuttleRotateRight', 'ShuttleBrake', 'ShuttleBrake'):
        'Вперёд и назад — `ShuttleStrafeUp` и `ShuttleStrafeDown`; влево и вправо — '
        '`ShuttleStrafeLeft` и `ShuttleStrafeRight`. Повороты — `ShuttleRotateLeft` и '
        '`ShuttleRotateRight`, торможение — `ShuttleBrake`. Удерживайте тормоз вместе '
        'с клавишами движения или поворота, чтобы маневрировать медленно.',
    },
    'guidebook/in_game/antagonist/minor-antagonists': {
        ('Use',): 'Чтобы получить эссенцию, сначала оцените душу цели: более ценные души дают '
        'больше эссенции. Поглощать её можно, когда жертва находится в критическом состоянии, '
        'мертва или спит. Взаимодействие с целью — `Use`.',
    },
    'guidebook/in_game/antagonist/traitors': {
        ('OpenCharacterMenu', 'OpenCharacterMenu'): 'Откройте меню персонажа клавишей '
        '`OpenCharacterMenu`: там находятся ваши цели, кодовые слова и личный код аплинка. '
        'Чтобы открыть аплинк, установите этот код в качестве мелодии звонка КПК.',
    },
    'guidebook/in_game/cargo/cargo': {
        ('ShuttleStrafeUp', 'ShuttleStrafeDown', 'ShuttleStrafeLeft', 'ShuttleStrafeRight',
         'ShuttleRotateLeft', 'ShuttleRotateRight', 'ShuttleBrake'):
        'Движение вперёд и назад — `ShuttleStrafeUp` и `ShuttleStrafeDown`; влево и вправо — '
        '`ShuttleStrafeLeft` и `ShuttleStrafeRight`. Повороты — `ShuttleRotateLeft` и '
        '`ShuttleRotateRight`, торможение — `ShuttleBrake`.',
    },
    'guidebook/in_game/engineering/airlocks': {
        ('Use', 'ActivateItemInWorld'): 'Открыть шлюз можно щелчком `Use` с пустой активной '
        'рукой, клавишей `ActivateItemInWorld`, даже когда рука занята, или подойдя вплотную к двери.',
    },
    'guidebook/in_game/engineering/construction': {
        ('OpenCraftingMenu',): 'Через меню строительства можно создавать предметы и размещать '
        'конструкции. Откройте его клавишей `OpenCraftingMenu`.',
        ('EditorLinePlace', 'EditorGridPlace'): 'При размещении объектов, привязанных к сетке, '
        'удерживайте `EditorLinePlace`, чтобы выстроить линию, или `EditorGridPlace`, чтобы '
        'заполнить прямоугольную область.',
        ('EditorRotateObject', 'EditorFlipObject'): 'При размещении предварительного контура '
        'конструкции нажмите `EditorRotateObject`, чтобы повернуть её по часовой стрелке. '
        'Если конструкцию можно отразить, используйте `EditorFlipObject`.',
        ('RotateObjectClockwise', 'RotateObjectCounterclockwise'): 'Уже построенный '
        'незакреплённый объект под курсором можно повернуть по часовой стрелке клавишей '
        '`RotateObjectClockwise`, против — `RotateObjectCounterclockwise`. Закреплённый '
        'ключом объект сначала нужно открепить.',
    },
    'guidebook/in_game/engineering/expanding-repairing-station': {
        ('Use',): [
            ('стержни', 'Чтобы построить решётку в пустом пространстве, возьмите стержни '
             'в активную руку и нажмите `Use` по нужному месту. Стальные листы можно '
             'превратить в стержни через меню строительства.'),
            ('канатчики', 'Чтобы разобрать решётку, возьмите кусачки в активную руку '
             'и нажмите `Use` по решётке.'),
            ('черепицу', 'Чтобы превратить решётку в обшивку корпуса, примените к ней '
             'сталь или напольную плитку через `Use`. Обшивка удерживает газы и давление.'),
        ],
    },
    'guidebook/in_game/engineering/gas-mining-and-storage': {
        ('ExamineEntity',): 'Осмотрите газодобытчик сочетанием `ExamineEntity`, чтобы увидеть '
        'сведения о его работе.',
    },
    'guidebook/in_game/engineering/networking': {
        ('AltActivateItemInHand', 'AltActivateItemInWorld'): 'Для настройки сетей используйте '
        'мультитул или сетевой конфигуратор. Переключить выбранную систему можно сочетанием '
        '`AltActivateItemInHand`, когда прибор в руке, либо `AltActivateItemInWorld` по прибору под курсором.',
        ('Use',): [
            ('сохранить адрес', 'Чтобы сохранить адрес устройства, примените к нему прибор через `Use`.'),
            ('список устройств', 'Когда адреса сохранены, примените прибор к основному '
             'устройству через `Use`. Откроется окно управления списком связанных устройств.'),
        ],
        ('Use', 'ActivateItemInHand'): 'Чтобы очистить список сохранённых адресов, '
        'активируйте прибор в руке клавишей `ActivateItemInHand` или нажмите на сам прибор '
        'через `Use`. В открывшемся окне нажмите «Очистить».',
    },
    'guidebook/in_game/engineering/shuttlecraft': {
        ('RotateObjectClockwise', 'RotateObjectCounterclockwise'): 'Сопло двигателя должно '
        'быть направлено в открытый космос: плитка перед соплом мешает тяге. Повернуть '
        'двигатель, в том числе закреплённый, можно клавишами `RotateObjectClockwise` '
        'и `RotateObjectCounterclockwise`.',
        ('Use', 'ActivateItemInWorld'): 'Включить или выключить двигатель можно щелчком '
        '`Use` пустой активной рукой либо клавишей `ActivateItemInWorld` по двигателю.',
    },
    'guidebook/in_game/engineering/singularity-engine': {
        ('Use',): 'Включите радиационный коллектор щелчком `Use` пустой активной рукой.',
        ('AltActivateItemInWorld',): 'Чтобы извлечь баллон, используйте `AltActivateItemInWorld` '
        'на коллекторе. Баллон можно заправить плазмой из канистры и вставить обратно.',
    },
    'guidebook/in_game/engineering/singularity-tesla-engine': {
        ('Use',): 'Включите генератор сдерживающего поля щелчком `Use` пустой активной рукой. '
        'Выключенный генератор не создаёт поле, даже если в него попадает луч излучателя.',
        ('AltActivateItemInWorld',): 'Заблокировать управление излучателем можно через '
        '`AltActivateItemInWorld`. Для изменения блокировки нужен подходящий доступ.',
    },
    'guidebook/in_game/new-player/radio': {
        ('FocusChatInputWindow',): 'Нажмите `FocusChatInputWindow`, чтобы перейти к вводу '
        'сообщения. Выбранный рядом с полем канал определяет способ отправки сообщения.',
        ('FocusWhisperChatWindow', 'CycleChatChannelForward'): 'Нажмите `FocusWhisperChatWindow`, '
        'чтобы начать сообщение шёпотом. Для эмоции поставьте в начале сообщения <code>@</code>. '
        'Переключение каналов — `CycleChatChannelForward`. На большом расстоянии собеседник '
        'может расслышать лишь часть фразы, особенно если вы шепчете.',
    },
    'guidebook/in_game/new-player': {
        ('OpenAHelp',): 'Соблюдайте <a href="/ru/rules">правила сервера</a>. '
        'Связаться с администрацией можно через окно помощи: `OpenAHelp`.',
        ('OpenGuidebook',): 'Игровое руководство открывается клавишей `OpenGuidebook`.',
        ('MoveUp', 'MoveLeft', 'MoveDown', 'MoveRight', 'Walk'): 'Передвигайтесь клавишами '
        '`MoveUp`, `MoveLeft`, `MoveDown` и `MoveRight`. Для ходьбы удерживайте `Walk`.',
        ('ExamineEntity', 'Use'): 'Осмотр объекта под курсором — `ExamineEntity`; '
        'взаимодействие — `Use`.',
        ('UseSecondary',): 'Контекстное меню объекта под курсором — `UIRightClick`.',
        ('SwapHands', 'ActivateItemInHand'): 'Смена активной руки — `SwapHands`; '
        'использование предмета в ней — `ActivateItemInHand`.',
        ('Drop', 'ThrowItemInHand'): 'Положить предмет из активной руки на пол — `Drop`; '
        'бросить его в направлении курсора — `ThrowItemInHand`.',
        ('TryPullObject', 'ReleasePulledObject'): 'Начать тащить объект под курсором — '
        '`TryPullObject`; отпустить — `ReleasePulledObject`.',
    },
    'guidebook/in_game/imported/nf/expeditions': {
        ('CameraReset',): 'После прибытия вам сообщат направление к цели. Найдите здание '
        'в указанном направлении и выполните задачу. При исходном повороте камеры север '
        'находится вверху экрана; вернуть камеру к повороту сетки можно клавишей `CameraReset`.',
    },
}
