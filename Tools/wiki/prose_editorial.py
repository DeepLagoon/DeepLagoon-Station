"""Reviewed, source-backed language repairs for existing Guidebook prose.

The changes are deliberately scoped to page paths and exact visible text.
Heading anchors, links, prototype attributes and protected pages are preserved.
This is an editorial pass, not a validation of every mechanic in the Guidebook.
"""
import html
import re

from editorial_cleanup import protected_path


# Keys are wiki paths relative to guidebook/in_game. Each group was compared
# with the corresponding Guidebook XML; SOURCE_FILES records the provenance.
HEADING_EDITS = {
    'antagonist/nuclear-operatives': {'Процедура ядерного оружия и вооружения': 'Установка и взведение ядерной бомбы', 'Условия выигрыша': 'Условия победы'},
    'antagonist/revolutionaries': {'Headrevs и конверсия': 'Главы революции и обращение', 'революционный': 'Революционер'},
    'antagonist/space-ninja': {'Твой костюм': 'Костюм ниндзя', 'Энергия Катана': 'Энергетическая катана', 'Скриншоты Spider Clan Charge': 'Заряд клана Паука'},
    'antagonist/thieves': {'Инструменты торговли': 'Инструменты вора', 'Центральная часть коллекции': 'Главный экспонат коллекции'},
    'antagonist/traitors': {'Uplink & Активация': 'Аплинк и его активация', 'убийство': 'Убийство', 'Уклонение и самопрекращение': 'Уход от преследования и самоубийство', 'Ассигнования': 'Присвоение предметов'},
    'antagonist/zombies': {'Инитальный инфицированный': 'Первоначально заражённый', 'передача': 'Передача инфекции', 'Атрибуты зомби': 'Особенности зомби', 'выживание': 'Выживание', 'Лечить': 'Лечение'},
    'cargo/cargo': {'Зарабатывая деньги': 'Заработок', 'заказывать': 'Заказ товаров', 'пилотирование': 'Пилотирование'},
    'command': {'Зарядиться': 'Руководство отделом', 'Ваш локер': 'Личный шкаф'},
    'engineering/air-alarms': {'Тревожный статус': 'Уровень тревоги', 'Операционные режимы': 'Режимы работы'},
    'engineering/air-vent': {'Операция по умолчанию': 'Работа по умолчанию', 'Локаут давления': 'Блокировка по давлению', 'Границы давления': 'Пределы давления'},
    'engineering/airlocks': {'Болтинг и аварийный доступ': 'Болты и аварийный доступ', 'Дистанционный контроль': 'Дистанционное управление', 'связывание': 'Привязка устройств', 'укладка': 'Оформление шлюзов', 'проводка': 'Проводка'},
    'engineering/ame': {'Операция': 'Эксплуатация'},
    'engineering/atmos-tools': {'Проектор Holofan': 'Проектор голографического барьера'},
    'engineering/atmospheric-network-monitor': {'Перечисленные сети': 'Список сетей', 'Станционная карта': 'Карта станции'},
    'engineering/expanding-repairing-station': {'решетка': 'Решётка', 'Хвостовые плитки': 'Обшивка корпуса'},
    'engineering/fire-and-gas-control': {'Огненные замки': 'Пожарные шлюзы', 'Секция станции': 'Разделение станции на отсеки', 'Основная операция': 'Основной принцип работы', 'Операция под воздушной тревогой': 'Работа с воздушной сигнализацией', 'Операция под огневой тревогой': 'Работа с пожарной сигнализацией', 'Огневая тревога': 'Пожарная сигнализация'},
    'engineering/gasses': {'Air Mix': 'Воздушная смесь', 'водяной пар': 'Водяной пар', 'плазма': 'Плазма', 'тритий': 'Тритий', 'Оксид азота': 'Закись азота', 'аммиак': 'Аммиак'},
    'engineering/inspecting-power': {'Мониторинг мощности компьютера': 'Компьютер мониторинга электроснабжения', 'Закладки устройств': 'Вкладки устройств', 'Проверка через Multitool': 'Проверка мультитулом'},
    'engineering/networking': {'Система Link': 'Прямое соединение устройств', 'Система списков': 'Связывание через списки устройств'},
    'engineering/pipe-networks': {'Как работают пипенеты': 'Как работают трубопроводные сети'},
    'engineering/power-storage': {'МСП': 'СМЭС', 'Продвинутые МСП': 'Улучшенные СМЭС'},
    'engineering/shuttlecraft': {'Фонд': 'Основание шаттла', 'Направленный контроль': 'Управление поступательным движением', 'Ротационный контроль': 'Управление поворотом', 'Власть': 'Электроснабжение', 'пилотирование': 'Пилотирование'},
    'engineering/singularity-engine': {'Производить власть': 'Выработка электроэнергии', 'свойства сингулярности': 'Свойства сингулярности', 'Потерянная сингулярность (Singuloose)': 'Выход сингулярности из поля сдерживания'},
    'engineering/solar-panels': {'Мощный мост': 'Подключение к электросети', 'Регулирующие панели': 'Поворот панелей', 'ремонт': 'Ремонт'},
    'engineering/teg': {'генератор': 'Генератор', 'Сгоревшая камера': 'Камера сгорания', 'Охладитель воды': 'Водяной охладитель', 'Охлаждающий массив': 'Система охлаждения', 'Холодильная петля': 'Контур охлаждения'},
    'engineering/tesla-engine': {'Удар молнии': 'Разряды молнии', 'Tesla Coils': 'Катушки Теслы', 'Заземляющие штаны': 'Заземляющие стержни', 'Тесла (Tesloose)': 'Выход Теслы из поля сдерживания'},
    'engineering/thermomachines': {'Космический нагреватель': 'Переносной обогреватель', 'Pipenet Thermomachines (Фризер и нагреватель)': 'Трубопроводные термомашины: охладитель и нагреватель', 'Термомашины из ада': 'Эффективная работа термомашин'},
    'engineering/voltage-networks': {'Шоковый ущерб': 'Урон от электричества'},
    'imported/far-horizons/engineering/nuclear-materials': {'Секции нейтронного креста': 'Сечения взаимодействия нейтронов'},
    'imported/far-horizons/engineering/nuclear-reactor': {
        'Реакторные стручки': 'Стержни реактора', 'Ручки управления': 'Управляющие стержни',
        'целенаправленный': 'Выбор элемента', 'Grid View': 'Схема реактора',
        'Группа по статусу': 'Панель состояния', 'Загрузка статора': 'Нагрузка статора',
        'РПМ': 'Обороты в минуту', 'Показатели состояния турбин': 'Индикаторы состояния турбины',
        'превышение скорости': 'Превышение допустимых оборотов', 'Чрезмерный темп': 'Перегрев',
        'Задержка': 'Остановка турбины', 'Температура': 'Недостаточная температура',
    },
    'glossary': {
        'В характере Slang': 'Внутриигровой сленг',
        'болван': 'На болтах (Bolted)', 'Умерший мозг': 'Отключившийся игрок (Braindead)',
        'Капсула': 'Капитан (Cap)', 'Критика / Критика': 'Критическое состояние (Crit)',
        'магия': 'Емаг (Emag)', 'Хоппер': 'Глава персонала (HoP)',
        'Хос': 'Глава службы безопасности (HoS)', 'Яни': 'Уборщик (Jani)',
        'Настойчивый': 'Робаст (Robust)', 'Спасение': 'Утилизатор (Salv)',
        'сдвиг': 'Смена (Shift)', 'Пространство': 'Разгерметизация (Spacing)',
        'Спецназ': 'Космос (Spess)', 'Стражник': 'Смотритель вне брига (Wardenloose)',
        'Оригинальное название: Out-Of-Character Slang': 'Внеигровой сленг',
        'Адмиам': 'Админское событие (Admeme)', 'муравей': 'Антагонист (Antag)',
        'сволочь': 'Бвоинк (Bwoink)', 'самоанализ': 'Самоантагонизм (Self-Antag)',
        'вверх по течению': 'Апстрим (Upstream)',
    },
    'imported/goobstation/engineering/automation': {'Бин для хранения': 'Контейнер хранения', 'Взаимодействующий': 'Интерактор'},
    'imported/mono/beginner-guide': {'Загрузка рабочих мест': 'Стартовое снаряжение', 'Прорастая в': 'Появление в игре', 'Суда': 'Корабли', 'Получить прибыль': 'Заработок', 'Круглый конец': 'Завершение раунда'},
    'imported/mono/creatures/asakim': {'Примечания Misc:': 'Дополнительные сведения'},
    'imported/mono/creatures/cortical-borer': {'Инъекционные химикаты': 'Введение реагентов', 'Проверить кровь': 'Проверка крови', 'Возьмите контроль': 'Захват управления', 'Сложить яйцо': 'Откладывание яйца'},
    'imported/mono/creatures/xenoborgs': {'Материнский корабль Core': 'Ядро материнского корабля', 'Добавление команды в свои ряды:': 'Пополнение рядов', 'Xenoborg Chassis': 'Шасси ксеноборгов', 'Инженер Ксеноборг': 'Ксеноборг-инженер', 'Скаут Ксеноборг': 'Ксеноборг-разведчик', 'Скрытая Ксеноборг': 'Скрытный ксеноборг', 'терять': 'Поражение'},
    'imported/mono/misc/armor-explanation': {'коэффициент сопротивления': 'Коэффициент сопротивления', 'отражение': 'Отражение'},
    'imported/mono/shipyard/motley-anne': {'1.Энергоснабжение': '1. Энергоснабжение', '1.2 Подразделение AME.': '1.2 Двигатель антиматерии', '2.1 Распределительная петля': '2.1 Распределительный контур', '3.Прочие проверки': '3. Прочие проверки', 'бойцы': 'Истребители'},
    'imported/mono/shipyard/nfrmanual': {'СТАРТУП': 'Запуск', 'ГАЗ-охлаждение': 'Пополнение газового теплоносителя', 'ЭМЕРГЕНЦИЯ ТУРБИН СТУДУН': 'Аварийное отключение турбины', 'СИДЕНОТЫ': 'Примечания'},
    'imported/mono/shipyard/takeaway': {'1.Энергоснабжение': '1. Энергоснабжение', '1.2 Солнечный контроль': '1.2 Управление солнечными панелями', '1.3 генераторная установка S.U.P.E.R.P.A.C.M.A.N.': '1.3 Генератор S.U.P.E.R.P.A.C.M.A.N.', '2.1 Распределительная петля': '2.1 Распределительный контур', '2.2 Пуск отходов': '2.2 Контур отработанных газов', '3.Прочие проверки': '3. Прочие проверки'},
    'imported/nf/medical/cryogenics': {'Использование Pod': 'Использование криокапсулы', 'Дополнительная информация:': 'Дополнительные сведения'},
    'imported/nf/medical/doc': {'Твой Леджер': 'Журнал учёта'},
    'imported/nf/medical/medical-bounties': {'Найти награды': 'Поиск заказов', 'Восстановление наград': 'Поиск и возврат тел', 'Претензионные премии': 'Получение вознаграждения'},
    'imported/nf/piloting': {'стыковка': 'Стыковка'},
    'imported/nf/safety-deposit-box': {'Консоль депозита безопасности': 'Консоль банковских ячеек', 'Размеры коробок и цены': 'Размеры ячеек и цены', 'Купить коробку': 'Покупка ячейки', 'Возвращение предметов': 'Получение предметов', 'Показатели состояния коробки': 'Индикаторы состояния ячейки', 'Возвращение потерянных ящиков': 'Восстановление доступа к ячейке', 'Пункт Сохранение': 'Сохранение предметов'},
    'imported/nf/science/xenoarchaeology': {'Artifexium и ты': 'Артифексиум'},
    'imported/nf/starting-gear': {'Сенсоры Suit': 'Датчики костюма'},
    'imported/shitmed/medical/organ-manipulation': {'анатомия': 'Анатомия', 'Что делает каждая трансплантация органов?': 'Возможности пересаженных органов', 'Почему не пересадили орган': 'Почему пересаженный орган', 'Делать что-нибудь?': 'не работает?'},
    'imported/shitmed/medical/part-manipulation': {'анатомия': 'Анатомия', 'Где взять больше деталей?': 'Где получить части тела?', 'Я прикрепил конечность моего пациента, но': 'Я присоединил конечность пациента, но', 'Это не работает?': 'она не работает?'},
    'imported/shitmed/medical/utility-surgeries': {'Тенд раны': 'Обработка ран', 'Имплантация полости': 'Имплантация в полость тела'},
    'medical/cryogenics': {'Использование Pod': 'Использование криокапсулы', 'Дополнительная информация:': 'Дополнительные сведения'},
    'medical/medical-doctor': {'возрождение': 'Реанимация'},
    'mobs/dv/harpy': {'диета': 'Питание', 'Специальный': 'Особые способности', 'Полет': 'Полёт'},
    'mobs/ipcs': {'ремонт': 'Ремонт'},
    'mobs/mono/mono-reptilian': {'когти': 'Когти', 'температура': 'Температура', 'нагревать': 'Согревание'},
    'new-player/character-creation': {'Приоритет Spawn': 'Приоритет появления', 'Работа': 'Профессии', 'Загрузка рабочих мест': 'Стартовое снаряжение', 'муравьи': 'Антагонисты', 'черты': 'Черты персонажа'},
    'new-player/your-first-character': {'Выбираем виды': 'Выбор расы', 'Незначительные характеристики': 'Основные параметры', 'Волосы и лицевые волосы': 'Причёска и растительность на лице', 'Маркировка': 'Узоры и отметины'},
    'psionics/glimmer': {"Strossmayer's Constant (Равновесие мерцания)": 'Постоянная Штроссмайера: равновесие мерцания', 'Источники Glimmer': 'Источники мерцания', 'Обработка Glimmer чрезвычайных ситуаций': 'Действия при опасном мерцании'},
    'psionics/glimmer-creatures': {'Блестящий клещ': 'Клещ мерцания', 'Климмер Висп': 'Огонёк мерцания', 'выживший': 'Ревенант'},
    'psionics/psionics': {'Масштабирование мощности': 'Сила псионических способностей', 'псионический потенциал': 'Псионический потенциал', 'телепатия': 'Телепатия', 'Oracle': 'Оракул', 'Приобретение Psionics': 'Получение псионики'},
    'science/anomalous-research': {'оборудование': 'Оборудование', 'Пульсы и сверхкритические события': 'Импульсы и сверхкритические события'},
    'science/cyborgs': {'модули': 'Модули'},
    'science/machine-upgrading': {'Части': 'Компоненты', 'модернизация': 'Модернизация'},
    'science/robotics': {'Изготовитель экзоскелетов': 'Фабрикатор экзокостюмов', 'мех': 'Мехи', 'оборудование': 'Оборудование'},
    'science/scanners-and-vessels': {'Сканер': 'Сканер аномалий', 'Сосуд': 'Сосуд аномалии'},
    'science/science': {'токарные станки': 'Производственные станки'},
    'science/technologies': {'Обслуживание персонала': 'Гражданские технологии'},
    'security/defusal': {'Гир': 'Снаряжение', 'Жесткие бомбы': 'Большие бомбы', 'Вооружение': 'Взведение', 'провода': 'Провода'},
    'security/forensics': {'У меня есть ДНК. Как узнать, чей он?': 'Как установить владельца образца ДНК?', 'волокно': 'Волокна'},
    'security/security': {'Гир': 'Снаряжение', 'вспышки': 'Вспышки', 'смертельный': 'Летальное оружие'},
    'service/botany': {'Начните свою смену': 'Начало смены', 'Гидропоника Трей': 'Гидропонный лоток', 'черты': 'Свойства растений', 'перекрестное осуждение': 'Перекрёстное опыление'},
    'service/chef': {'Кухня Prep:': 'Подготовка кухни', 'Смешивание Dough:': 'Приготовление теста', 'Изготовление муки:': 'Получение муки', 'Получить растения:': 'Получение растений', 'Сбор молока:': 'Получение молока', 'Как быть мясником:': 'Разделка животных'},
    'service/janitorial': {'Приобретение и удаление жидкостей': 'Сбор и утилизация жидкостей', 'Успокойся.': 'Уборка мусора'},
    'service/service': {'Работать вместе': 'Совместная работа'},
    'space-station14': {'Терминал Шаттла': 'Терминал шаттлов', 'Покидая станцию': 'Отправление со станции', 'Больше информации': 'Дополнительные сведения'},
}

# Book chapters originate in these same Guidebook pages. They receive only the
# same reviewed heading/phrase repairs, rather than a separate rewrite.
BOOK_SOURCES = {
    'books/how-to-cook-for-forty-spaceman': 'service/chef',
    'books/how-to-keep-station-clean': 'service/janitorial',
    'books/leaf-lovers-secret': 'service/botany',
    'books/scientists-guidebook': 'science/science',
}


TEXT_EDITS = {
    'imported/far-horizons/engineering/nuclear-reactor': {
        'Реакторный стержень - это части, которые входят в реакторную сетку и заставляют ее функционировать.': 'Стержни реактора — компоненты, которые вставляются в ячейки реактора и обеспечивают его работу.',
        'Если температура реактора Рода достигнет': 'Если температура стержня реактора достигнет',
        'Обработка горячих реакторов может вызвать серьезные ожоги.': 'При обращении с горячими стержнями можно получить сильные ожоги.',
        'UI реактора имеет три основные панели: таргетинг, Grid View, панель состояния': 'В интерфейсе реактора три основные панели: выбор элемента, схема реактора и панель состояния.',
        'Панель таргетинга': 'Панель выбора элемента',
        'Вид сетки дает графическую визуализацию состояния сетки реактора.': 'Схема реактора показывает состояние его ячеек.',
        'Температурный калибр показывает': 'Индикатор температуры показывает',
        'передают тепло от себя в газ охлаждающей жидкости, который течет через них.': 'передают тепло газовому теплоносителю, который проходит через них.',
    },
    'engineering/inspecting-power': {'Статистика власти показывает:': 'Статистика электроснабжения показывает:'},
    'engineering/ame': {'Проверка власти': 'Диагностика электроснабжения'},
    'engineering/gas-mining-and-storage': {'газа, добываемого шахтерами': 'газа, добываемого газодобытчиками', 'Они встречаются в атмосфере.': 'Они находятся в атмосферном отсеке.'},
    'engineering/tesla-engine': {'БТР': 'ЛКП', 'рыхлую теслу': 'выход Теслы из поля сдерживания', 'заземляющие штанги': 'заземляющие стержни', 'Заземляющие штанги': 'Заземляющие стержни'},
    'antagonist/traitors': {
        'Восходящая ссылка - ваш самый важный инструмент в качестве предателя.': 'Аплинк — основной инструмент предателя.',
        'со своей восходящей линии связи': 'со своего аплинка',
        'на вашей восходящей линии связи': 'в вашем аплинке',
        'Имплантированные восходящие линии связи': 'Имплантированные аплинки',
        'имплантат восходящей линии связи': 'имплант аплинка',
        'свой собственный восходящий канал': 'собственный аплинк',
    },
    'science/science': {'на токарных станках': 'на производственных станках', 'На токарном станке': 'На производственном станке', 'у шахтеров или спасателей': 'у шахтёров или утилизаторов', 'Каждая технология стоит Research Points': 'Для изучения каждой технологии нужны очки исследований'},
    'service/janitorial': {'Держите вещи в чистоте, иногда это тяжелая работа, но кто-то должен это сделать. Это проведет вас через инструменты торговли, и как они используются.': 'Уборщик удаляет мусор, пятна и разлитые жидкости. Для этого используются инструменты и оборудование, перечисленные ниже.'},
}


def editorial_prose(path, text, data=None):
    if protected_path(path):
        return text
    relative = path.removeprefix('guidebook/in_game/')
    relative = BOOK_SOURCES.get(relative, relative)
    headings = HEADING_EDITS.get(relative, {})
    def localized_heading(visible):
        replacement = headings.get(visible)
        if replacement is None and data is not None and visible in data.raw.get('reagent', {}):
            localized = data.name('reagent', visible)
            # A missing translation must not turn the prototype ID into an FTL key.
            if re.search('[А-Яа-яЁё]', localized):
                replacement = localized[:1].upper() + localized[1:]
        return replacement
    def heading(match):
        visible = html.unescape(re.sub(r'<[^>]+>', '', match[2])).strip()
        replacement = localized_heading(visible)
        if replacement is None:
            return match[0]
        return match[1] + html.escape(replacement) + match[3]
    text = re.sub(r'(<h[23]\b[^>]*>)(.*?)(</h[23]>)', heading, text, flags=re.S | re.I)
    def markdown_heading(match):
        # Some earlier XML exports placed indented prose in Markdown code blocks.
        # Retain that structure here; render_prose handles its final conversion.
        title = match[2].rstrip()
        suffix = match[2][len(title):]
        replacement = localized_heading(html.unescape(title))
        return match[0] if replacement is None else match[1] + html.escape(replacement) + suffix
    text = re.sub(r'(?m)^((?:<pre\b[^>]*>\s*<code\b[^>]*>)?[ \t]*#{2,3}[ \t]+)([^\r\n<]+)',
                  markdown_heading, text)
    split_headings = {
        'imported/shitmed/medical/organ-manipulation': (
            'Почему пересаженный орган', 'не работает?', 'Почему пересаженный орган не работает?'),
        'imported/shitmed/medical/part-manipulation': (
            'Я присоединил конечность пациента, но', 'она не работает?', 'Почему присоединённая конечность не работает?'),
    }
    if relative in split_headings:
        first, second, combined = split_headings[relative]
        pattern = (r'(<h[23]\b[^>]*>)' + re.escape(first) + r'(</h[23]>)\s*'
                   r'<h[23]\b(?P<attrs>[^>]*)>' + re.escape(second) + r'</h[23]>')
        def merge(match):
            anchor = re.search(r'\bid\s*=\s*(["\'])(.*?)\1', match['attrs'])
            extra = '<span id="' + html.escape(anchor[2], quote=True) + '"></span>' if anchor else ''
            return match[1] + html.escape(combined) + extra + match[2]
        text = re.sub(pattern, merge, text)
    replacements = TEXT_EDITS.get(relative, {})
    if replacements:
        pieces = re.split(r'(<[^>]*>)', text)
        for index in range(0, len(pieces), 2):
            for before, after in replacements.items():
                pieces[index] = pieces[index].replace(before, after)
        text = ''.join(pieces)
    return text




SOURCE_FILES = {'antagonist/nuclear-operatives': 'Resources/ServerInfo/Guidebook/Antagonist/NuclearOperatives.xml',
 'antagonist/revolutionaries': 'Resources/ServerInfo/Guidebook/Antagonist/Revolutionaries.xml',
 'antagonist/space-ninja': 'Resources/ServerInfo/Guidebook/Antagonist/SpaceNinja.xml',
 'antagonist/thieves': 'Resources/ServerInfo/Guidebook/Antagonist/Thieves.xml',
 'antagonist/traitors': 'Resources/ServerInfo/Guidebook/Antagonist/Traitors.xml',
 'antagonist/zombies': 'Resources/ServerInfo/Guidebook/Antagonist/Zombies.xml',
 'cargo/cargo': 'Resources/ServerInfo/Guidebook/Cargo/Cargo.xml',
 'command': 'Resources/ServerInfo/Guidebook/Command.xml',
 'engineering/air-alarms': 'Resources/ServerInfo/Guidebook/Engineering/AirAlarms.xml',
 'engineering/air-vent': 'Resources/ServerInfo/Guidebook/Engineering/AirVent.xml',
 'engineering/airlocks': 'Resources/ServerInfo/Guidebook/Engineering/Airlocks.xml',
 'engineering/ame': 'Resources/ServerInfo/Guidebook/Engineering/AME.xml',
 'engineering/atmos-tools': 'Resources/ServerInfo/Guidebook/Engineering/AtmosTools.xml',
 'engineering/atmospheric-network-monitor': 'Resources/ServerInfo/Guidebook/Engineering/AtmosphericNetworkMonitor.xml',
 'engineering/expanding-repairing-station': 'Resources/ServerInfo/Guidebook/Engineering/ExpandingRepairingStation.xml',
 'engineering/fire-and-gas-control': 'Resources/ServerInfo/Guidebook/Engineering/FireAndGasControl.xml',
 'engineering/gas-mining-and-storage': 'Resources/ServerInfo/Guidebook/Engineering/GasMiningAndStorage.xml',
 'engineering/gasses': 'Resources/ServerInfo/Guidebook/Engineering/Gasses.xml',
 'engineering/inspecting-power': 'Resources/ServerInfo/Guidebook/Engineering/InspectingPower.xml',
 'engineering/networking': 'Resources/ServerInfo/Guidebook/Engineering/Networking.xml',
 'engineering/pipe-networks': 'Resources/ServerInfo/Guidebook/Engineering/PipeNetworks.xml',
 'engineering/power-storage': 'Resources/ServerInfo/Guidebook/Engineering/PowerStorage.xml',
 'engineering/shuttlecraft': 'Resources/ServerInfo/Guidebook/Engineering/Shuttlecraft.xml',
 'engineering/singularity-engine': 'Resources/ServerInfo/Guidebook/Engineering/SingularityEngine.xml',
 'engineering/solar-panels': 'Resources/ServerInfo/Guidebook/Engineering/SolarPanels.xml',
 'engineering/teg': 'Resources/ServerInfo/Guidebook/Engineering/TEG.xml',
 'engineering/tesla-engine': 'Resources/ServerInfo/Guidebook/Engineering/TeslaEngine.xml',
 'engineering/thermomachines': 'Resources/ServerInfo/Guidebook/Engineering/Thermomachines.xml',
 'engineering/voltage-networks': 'Resources/ServerInfo/Guidebook/Engineering/VoltageNetworks.xml',
 'glossary': 'Resources/ServerInfo/Guidebook/Glossary.xml',
 'imported/far-horizons/engineering/nuclear-materials': 'Resources/ServerInfo/_FarHorizons/Guidebook/Engineering/NuclearMaterials.xml',
 'imported/far-horizons/engineering/nuclear-reactor': 'Resources/ServerInfo/_FarHorizons/Guidebook/Engineering/NuclearReactor.xml',
 'imported/goobstation/engineering/automation': 'Resources/ServerInfo/_Goobstation/Guidebook/Engineering/Automation.xml',
 'imported/mono/beginner-guide': 'Resources/ServerInfo/_Mono/Guidebook/BeginnerGuide.xml',
 'imported/mono/creatures/asakim': 'Resources/ServerInfo/_Mono/Guidebook/Creatures/Asakim.xml',
 'imported/mono/creatures/cortical-borer': 'Resources/ServerInfo/_Mono/Guidebook/Creatures/CorticalBorer.xml',
 'imported/mono/creatures/xenoborgs': 'Resources/ServerInfo/_Mono/Guidebook/Creatures/Xenoborgs.xml',
 'imported/mono/misc/armor-explanation': 'Resources/ServerInfo/_Mono/Guidebook/Misc/ArmorExplanation.xml',
 'imported/mono/shipyard/motley-anne': 'Resources/ServerInfo/_Mono/Guidebook/Shipyard/MotleyAnne.xml',
 'imported/mono/shipyard/nfrmanual': 'Resources/ServerInfo/_Mono/Guidebook/Shipyard/NFRManual.xml',
 'imported/mono/shipyard/takeaway': 'Resources/ServerInfo/_Mono/Guidebook/Shipyard/Takeaway.xml',
 'imported/nf/medical/cryogenics': 'Resources/ServerInfo/_NF/Guidebook/Medical/Cryogenics.xml',
 'imported/nf/medical/doc': 'Resources/ServerInfo/_NF/Guidebook/Medical/Doc.xml',
 'imported/nf/medical/medical-bounties': 'Resources/ServerInfo/_NF/Guidebook/Medical/MedicalBounties.xml',
 'imported/nf/piloting': 'Resources/ServerInfo/_NF/Guidebook/Piloting.xml',
 'imported/nf/safety-deposit-box': 'Resources/ServerInfo/_NF/Guidebook/SafetyDepositBox.xml',
 'imported/nf/science/xenoarchaeology': 'Resources/ServerInfo/_NF/Guidebook/Science/Xenoarchaeology.xml',
 'imported/nf/starting-gear': 'Resources/ServerInfo/_NF/Guidebook/StartingGear.xml',
 'imported/shitmed/medical/organ-manipulation': 'Resources/ServerInfo/_Shitmed/Guidebook/Medical/OrganManipulation.xml',
 'imported/shitmed/medical/part-manipulation': 'Resources/ServerInfo/_Shitmed/Guidebook/Medical/PartManipulation.xml',
 'imported/shitmed/medical/utility-surgeries': 'Resources/ServerInfo/_Shitmed/Guidebook/Medical/UtilitySurgeries.xml',
 'medical/cryogenics': 'Resources/ServerInfo/Guidebook/Medical/Cryogenics.xml',
 'medical/medical-doctor': 'Resources/ServerInfo/Guidebook/Medical/MedicalDoctor.xml',
 'mobs/dv/harpy': 'Resources/ServerInfo/Guidebook/Mobs/_DV/Harpy.xml',
 'mobs/ipcs': 'Resources/ServerInfo/Guidebook/Mobs/IPCs.xml',
 'mobs/mono/mono-reptilian': 'Resources/ServerInfo/Guidebook/Mobs/_Mono/MonoReptilian.xml',
 'new-player/character-creation': 'Resources/ServerInfo/Guidebook/NewPlayer/CharacterCreation.xml',
 'new-player/your-first-character': 'Resources/ServerInfo/Guidebook/NewPlayer/YourFirstCharacter.xml',
 'psionics/glimmer': 'Resources/ServerInfo/Guidebook/Psionics/Glimmer.xml',
 'psionics/glimmer-creatures': 'Resources/ServerInfo/Guidebook/Psionics/GlimmerCreatures.xml',
 'psionics/psionics': 'Resources/ServerInfo/Guidebook/Psionics/Psionics.xml',
 'science/anomalous-research': 'Resources/ServerInfo/Guidebook/Science/AnomalousResearch.xml',
 'science/cyborgs': 'Resources/ServerInfo/Guidebook/Science/Cyborgs.xml',
 'science/machine-upgrading': 'Resources/ServerInfo/Guidebook/Science/MachineUpgrading.xml',
 'science/robotics': 'Resources/ServerInfo/Guidebook/Science/Robotics.xml',
 'science/scanners-and-vessels': 'Resources/ServerInfo/Guidebook/Science/ScannersAndVessels.xml',
 'science/science': 'Resources/ServerInfo/Guidebook/Science/Science.xml',
 'science/technologies': 'Resources/ServerInfo/Guidebook/Science/Technologies.xml',
 'security/defusal': 'Resources/ServerInfo/Guidebook/Security/Defusal.xml',
 'security/forensics': 'Resources/ServerInfo/Guidebook/Security/Forensics.xml',
 'security/security': 'Resources/ServerInfo/Guidebook/Security/Security.xml',
 'service/botany': 'Resources/ServerInfo/Guidebook/Service/Botany.xml',
 'service/chef': 'Resources/ServerInfo/Guidebook/Service/Chef.xml',
 'service/janitorial': 'Resources/ServerInfo/Guidebook/Service/Janitorial.xml',
 'service/service': 'Resources/ServerInfo/Guidebook/Service/Service.xml',
 'space-station14': 'Resources/ServerInfo/Guidebook/SpaceStation14.xml'}
