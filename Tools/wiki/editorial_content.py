"""Lossless extraction and guarded editorial metadata for existing wiki articles."""
from html.parser import HTMLParser
import re


class Elements(HTMLParser):
    def __init__(self, source):
        super().__init__(convert_charrefs=False)
        self.source = source
        self.lines = [0]
        self.lines.extend(m.end() for m in re.finditer('\n', source))
        self.open = {}
        self.elements = []
        self.feed(source)

    def source_offset(self):
        line, column = self.getpos()
        return self.lines[line - 1] + column

    def handle_starttag(self, tag, attrs):
        if tag in ('img', 'br', 'hr', 'input', 'meta', 'link', 'source', 'wbr'):
            return
        start = self.source_offset()
        self.open.setdefault(tag, []).append((start, start + len(self.get_starttag_text()), dict(attrs)))

    def handle_endtag(self, tag):
        stack = self.open.get(tag, [])
        if stack:
            start, inner, attrs = stack.pop()
            end = self.source_offset()
            self.elements.append((tag, attrs, start, inner, end, self.source.index('>', end) + 1))


def article_body(content):
    """Return original article bytes as text, without nesting the website chrome."""
    parsed = Elements(content)
    candidates = [e for e in parsed.elements if e[0] in ('article', 'div') and 'prose' in e[1].get('class', '').split()]
    if candidates:
        element = min(candidates, key=lambda e: e[2])
        return content[element[3]:element[4]]
    return content


def protected_page(path):
    parts = set(path.lower().split('/'))
    return bool(parts & {'rules', 'server-rules', 'lore', 'factions', 'star-system', 'sop', 'standard-operating-procedures'}) or path.endswith(('/books/space-law', '/books/ussplaw', '/books/tsfsop'))


TITLES = {
    'new-player/controls/controls': 'Управление',
    'new-player/your-first-character': 'Первый персонаж',
    'cargo/cargo': 'Снабжение',
    'engineering/air-alarms': 'Воздушная сигнализация',
    'engineering/airlocks': 'Шлюзы',
    'engineering/airlock-security': 'Защита шлюзов',
    'engineering/air-scrubber': 'Газовый скруббер',
    'engineering/air-vent': 'Вентиляция',
    'engineering/atmospheric-alerts-computer': 'Компьютер атмосферных оповещений',
    'engineering/atmospheric-network-monitor': 'Монитор атмосферной сети',
    'engineering/atmos-tools': 'Атмосферные инструменты',
    'engineering/atmospheric-upsets': 'Атмосферные аварии',
    'engineering/fire-and-gas-control': 'Борьба с пожарами и утечками',
    'engineering/gas-manipulation': 'Работа с газами',
    'engineering/gasses': 'Газы',
    'engineering/generators': 'Генераторы',
    'engineering/inspecting-power': 'Диагностика электроснабжения',
    'engineering/networking': 'Электрические сети',
    'engineering/shuttlecraft': 'Шаттлы',
    'engineering/singularity-engine': 'Генератор на сингулярности',
    'engineering/singularity-tesla-engine': 'Сингулярность и Тесла',
    'engineering/teg': 'Термоэлектрический генератор (ТЭГ)',
    'engineering/tesla-engine': 'Генератор Теслы',
    'medical/medical-doctor': 'Врач',
    'mobs/dv/harpy': 'Гарпии',
    'reference-tables/drinks': 'Напитки',
    'science/robotics': 'Робототехника',
    'science/scanners-and-vessels': 'Сканеры и сосуды аномалий',
    'security/criminal-records': 'Криминальные записи',
    'security/defusal': 'Обезвреживание больших бомб',
    'security/forensics': 'Криминалистика',
    'security/security': 'Служба безопасности',
    'service/bars-and-cookies-recipes': 'Батончики и печенье',
    'service/botany': 'Ботаника',
    'service/chef': 'Повар',
    'service/food-recipes': 'Рецепты кухни',
    'service/janitorial': 'Уборка',
    'service/moth-recipes': 'Блюда для молей',
    'service/pie-recipes': 'Пироги',
    'service/pizza-recipes': 'Пицца',
    'service/savory-recipes': 'Основные блюда',
    'service/medicinal-recipes': 'Лечебные блюда',
    'writing': 'Письмо и оформление текста',
    'imported/nf/piloting': 'Пилотирование',
    'imported/nf/safety-deposit-box': 'Банковские ячейки',
    'imported/nf/medical/medical-recipes': 'Рецепты медицинского сборщика',
    'imported/nf/medical/medical-bounties': 'Медицинские заказы',
    'imported/nf/medical/doc': 'Директор медицинской службы',
    'imported/nf/mobs-hostile-expedition/rogue-silicons': 'Враждебные синтетики',
    'imported/nf/mobs-hostile-expedition/explorers': 'Враждебные исследователи',
    'imported/shitmed/medical/surgery': 'Хирургия',
    'imported/shitmed/medical/organ-manipulation': 'Операции на органах',
    'imported/shitmed/medical/part-manipulation': 'Операции на частях тела',
    'imported/mono/economy/economy': 'Промышленность',
    'imported/mono/economy/arc-furnace': 'Рецепты дуговой печи',
    'imported/mono/misc/grid-claimer': 'Закрепление обломков и астероидов',
    'imported/mono/misc': 'Дополнительные механики',
    'imported/mono/robotics/mechs': 'Мехи',
    'imported/mono/robotics/s1mechs': 'Боевые мехи серии 1',
    'imported/mono/robotics/s2mechs': 'Боевые мехи серии 2',
    'imported/mono/robotics/s4mechs': 'Боевые мехи серии 4',
    'imported/mono/ship-combat': 'Корабельный бой',
    'imported/goobstation/engineering/automation': 'Автоматизация',
    'imported/ce/flight': 'Полёт',
}


def article_title(path, previous):
    if protected_page(path):
        return previous
    value = TITLES.get(path.removeprefix('guidebook/in_game/'), previous)
    return value[:1].upper() + value[1:]
