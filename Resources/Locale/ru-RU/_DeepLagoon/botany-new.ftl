botany-ui-section-plant = Состояние растения
botany-ui-health = Здоровье
botany-ui-age = Возраст
botany-ui-growth = Рост
botany-ui-growth-stage = Стадия
botany-ui-harvest-type = Урожай
botany-ui-harvest-type-value = { $type ->
    [NoRepeat] Однократный
    [Repeat] Многократный
    [SelfHarvest] Самосбор
   *[other] Неизвестно
}
botany-ui-harvest-ready = Готово к сбору
botany-ui-section-traits = Характеристики
botany-ui-endurance = Выносливость
botany-ui-yield = Урожайность
botany-ui-lifespan = Продолжительность жизни
botany-ui-maturation = Созревание
botany-ui-production = Период плодоношения
botany-ui-potency = Потенция
botany-ui-section-resources = Ресурсы лотка
botany-ui-section-reagents = Реагенты в лотке
botany-ui-section-tray = Состояние лотка
botany-ui-tray-empty = Лоток пуст.
botany-ui-tray-plant = В лотке растёт [color=limegreen]{$plant}[/color].
botany-ui-section-conditions = Необходимые условия
botany-ui-condition-current-missing = недоступно
botany-ui-section-consumed-gases = Поглощаемые газы
botany-ui-section-exuded-gases = Выделяемые газы
botany-ui-gases-none = Газы не обнаружены.
botany-ui-section-chemicals = Вещества в урожае
botany-ui-chemicals-none = Вещества в урожае не обнаружены.
botany-ui-section-mutations = Мутации
botany-ui-mutations-none = Устойчивые мутации не обнаружены.
botany-ui-mutation = Уровень
botany-ui-mutation-mod = Модификатор
botany-ui-section-warnings = Предупреждения
botany-ui-warnings-none = Предупреждений нет.
botany-ui-unit-cycles = циклов

mutation-plant-description-kudzu = Растение необычно [color=crimson]тонкое и быстро растёт[/color].
mutation-plant-description-ligneous = Стебель одревеснел; для сбора нужен острый инструмент.
mutation-plant-description-scream = Растение выглядит встревоженным.
mutation-plant-description-unviable = Растение [color=crimson]увядает и выглядит больным[/color].
mutation-plant-description-seedless = Урожай [color=crimson]не содержит семян[/color].
mutation-plant-description-sampled = С этого растения уже взяли образец.
mutation-plant-noun-kudzu = рост кудзу
mutation-plant-noun-ligneous = одревеснение
mutation-plant-noun-scream = мандрагора
mutation-plant-noun-unviable = нежизнеспособность
mutation-plant-noun-seedless = отсутствие семян
mutation-plant-noun-sampled = следы среза

plant-component-plant-success-popup = Вы сажаете {$seedNoun} растения «{$seedName}».
plant-component-already-seeded-popup = В {$name} уже что-то посажено!
plant-component-something-already-growing-message = Здесь растёт [color=limegreen]{$seedName}[/color].
plant-component-dead-plant-matter-message = Растение полно [color=crimson]мёртвых тканей[/color].
plant-component-ligneous-cant-harvest-message = Стебель слишком твёрдый для сбора голыми руками.
plant-component-improper-heat-warning = Растению не подходит [color=orange]температура[/color].
plant-component-improper-pressure-warning = Растению не подходит [color=lightblue]давление[/color].
plant-component-missing-gas-warning = Растению не подходит [color=skyblue]газовый состав[/color].
plant-component-toxins-high-warning = Уровень [color=mediumpurple]токсинов[/color] опасен для растения.
plant-component-weeds-high-warning = Уровень [color=lightgreen]сорняков[/color] опасен для растения.
plant-component-pests-high-warning = Уровень [color=gray]вредителей[/color] опасен для растения.

tray-component-nothing-planted-message = Здесь ничего не посажено.
tray-component-water-level-message = Вода: [color=cyan]{$waterLevel}[/color]
tray-component-nutrient-level-message = Питание: [color=orange]{$nutritionLevel}[/color]
tray-component-weed-high-level-warning = Здесь полно [color=lightgreen]сорняков[/color]!
tray-component-water-low-warning = Уровень [color=cyan]воды[/color] [color=crimson]низкий[/color]!
tray-component-nutrient-low-warning = Уровень [color=orange]питания[/color] [color=crimson]низкий[/color]!
tray-component-toxin-high-level-warning = Мигает индикатор [color=mediumpurple]токсичности[/color].
tray-component-pest-high-level-warning = Здесь полно [color=gray]мелких червей[/color]!
tray-component-plant-improper-heat-warning = Мигает индикатор [color=orange]неподходящей температуры[/color].
tray-component-plant-improper-pressure-warning = Мигает индикатор [color=lightblue]неподходящего давления[/color].
tray-component-plant-missing-gas-warning = Мигает индикатор [color=skyblue]неподходящего газового состава[/color].
tray-component-plant-health-warning = Мигает индикатор [color=red]здоровья[/color].

plant-hoe-component-already-seeded-popup = Вы удаляете сорняки из {$name}.
plant-hoe-component-remove-weeds-others-popup = {$otherName} начинает выпалывать сорняки.
plant-hoe-component-no-weeds-popup = Здесь нет сорняков, прополка не нужна!
plant-shovel-component-remove-plant-popup = Вы удаляете растение из {$name}.
plant-shovel-component-remove-plant-others-popup = {$name} удаляет растение.
plant-shovel-component-no-plant-popup = Здесь нет растения.
plant-sample-component-early-sample-popup = Растение ещё недостаточно выросло для взятия образца.
plant-sample-component-already-sampled-popup = С этого растения уже взяли образец.
plant-sample-component-take-sample-popup = Вы берёте образец растения «{$seedName}».
plant-sample-component-dead-plant-popup = Растение мертво.
plant-produce-component-compost-popup = Вы превращаете {$usingItem} в компост для {$owner}.
plant-produce-component-compost-others-popup = {$user} превращает {$usingItem} в компост для {$owner}.

plant-attribute-water-consumption = расход воды
plant-attribute-weed-tolerance = устойчивость к сорнякам
plant-attribute-toxins-tolerance = устойчивость к токсинам
plant-attribute-nutrient-consumption = расход питательных веществ
plant-attribute-pest-tolerance = устойчивость к вредителям
plant-attribute-endurance = выносливость
plant-attribute-yield = урожайность
plant-attribute-lifespan = продолжительность жизни
plant-attribute-maturation = время созревания
plant-attribute-production = период плодоношения
plant-attribute-low-heat-tolerance = нижний предел температуры
plant-attribute-high-heat-tolerance = верхний предел температуры
plant-attribute-low-pressure-tolerance = нижний предел давления
plant-attribute-high-pressure-tolerance = верхний предел давления

entity-effect-guidebook-plant-attribute = Изменяет {$attribute} на {$amount}
entity-effect-guidebook-plant-cryoxadone = Омолаживает растение в зависимости от его возраста и срока созревания
entity-effect-guidebook-plant-diethylamine = С вероятностью 10% отдельно увеличивает срок жизни и выносливость растения
entity-effect-guidebook-plant-robust-harvest = Повышает потенцию на {$increase} до {$limit}. После {$seedlesstreshold} растение теряет семена. При превышении {$limit} с вероятностью 10% снижает урожайность
entity-effect-guidebook-plant-seeds-add = Восстанавливает семена растения
entity-effect-guidebook-plant-seeds-remove = Удаляет семена растения
entity-effect-guidebook-plant-change-trait = Изменяет признак растения «{$trait}»
entity-effect-guidebook-plant-mutate-exude-gasses = Заставляет растение выделять от {$minValue} до {$maxValue} молей газа
entity-effect-guidebook-plant-mutate-consume-gasses = Заставляет растение поглощать от {$minValue} до {$maxValue} молей газа
entity-effect-guidebook-plant-mutate-chemicals = Мутирует растение для производства {$name}
units-u = ед.

reagent-name-heparin = гепарин
reagent-desc-heparin = Антикоагулянт, препятствующий естественному свёртыванию крови. Большие дозы вызывают кровотечение.
reagent-name-mutagenic-fertilizer = мутагенное удобрение
reagent-desc-mutagenic-fertilizer = Удобрение, способствующее полезным изменениям растений.
reagent-effect-status-effect-Anticoagulant = подавление свёртывания крови
