anomaly-component-contact-damage = Аномалия обжигает вашу кожу!

anomaly-vessel-component-anomaly-assigned = Аномалия назначена на корабль.
anomaly-vessel-component-not-assigned = Этот корабль не назначен ни на какое аномальное явление. Попробуйте использовать сканер на нем.
anomaly-vessel-component-assigned = Этот корабль в настоящее время назначен на изучение аномалии.
anomaly-vessel-component-upgrade-output = вывод точки

anomaly-particles-delta = Частицы дельта
anomaly-particles-epsilon = Эпсилон-частицы
anomaly-particles-zeta = Зета-частицы
anomaly-particles-omega = Омега-частицы
anomaly-particles-sigma = Сигма-частицы

anomaly-scanner-component-scan-complete = Сканирование завершено!

anomaly-scanner-ui-title = сканер аномалий
anomaly-scanner-no-anomaly = Аномалия не обнаружена.
anomaly-scanner-severity-percentage = Текущая степень серьезности: [color=gray]{$percent}[/color]
anomaly-scanner-severity-percentage-unknown = Текущая серьезность: [color=red]ОШИБКА[/color]
anomaly-scanner-stability-low = Текущее состояние аномалии: [color=gold]Распадается[/color]
anomaly-scanner-stability-medium = Текущее состояние аномалии: [color=forestgreen]Стабильное[/color]
anomaly-scanner-stability-high = Текущее состояние аномалии: [color=crimson]Увеличивается[/color]
anomaly-scanner-stability-unknown = Текущее состояние аномалии: [color=red]ОШИБКА[/color]
anomaly-scanner-point-output = Вывод точки: [color=gray]{$point}[/color]
anomaly-scanner-point-output-unknown = Вывод точки: [color=red]ОШИБКА[/color]
anomaly-scanner-particle-readout = Анализ реакции частиц:
anomaly-scanner-particle-danger = [color=crimson]Тип опасности:[/color] {$type}
anomaly-scanner-particle-unstable = [color=plum]Нестабильный тип:[/color] {$type}
anomaly-scanner-particle-containment = [color=goldenrod]Тип контейнера:[/color] {$type}
anomaly-scanner-particle-transformation = [color=#6b75fa]Тип преобразования:[/color] {$type}
anomaly-scanner-particle-danger-unknown = [color=crimson]Тип опасности:[/color] [color=red]ОШИБКА[/color]
anomaly-scanner-particle-unstable-unknown = [color=plum]Нестабильный тип:[/color] [color=red]ОШИБКА[/color]
anomaly-scanner-particle-containment-unknown = [color=goldenrod]Тип контейнера:[/color] [color=red]ОШИБКА[/color]
anomaly-scanner-particle-transformation-unknown = [color=#6b75fa]Тип преобразования:[/color] [color=red]ОШИБКА[/color]
anomaly-scanner-pulse-timer = Времени до следующего импульса: [color=gray]{$time}[/color]

anomaly-gorilla-core-slot-name = Аномальный центр
anomaly-gorilla-charge-none = Внутри нет [bold]аномального ядра[/bold].
anomaly-gorilla-charge-limit = У него есть [color={$count ->
    [3]green
    [2]yellow
    [1]orange
    [0]red
    *[other]purple
}]{$count} {$count ->
    [one]charge
    *[other]charges
}[/color] remaining.
anomaly-gorilla-charge-infinite = У него [color=gold]бесконечные заряды[/color]. [italic]Пока что...[/italic]

anomaly-sync-connected = Аномалия успешно захвачена
anomaly-sync-disconnected = Связь с аномалией потеряна!
anomaly-sync-no-anomaly = Аномалии в зоне поражения нет.
anomaly-sync-examine-connected = Он [color=darkgreen]привязан[/color] к аномалии.
anomaly-sync-examine-not-connected = Он [color=darkred]не привязан[/color] к аномалии.
anomaly-sync-connect-verb-text = Присоединить аномалию
anomaly-sync-connect-verb-message = Привяжите ближайшую аномалию к {THE($machine)}.

anomaly-generator-ui-title = Генератор аномалий
anomaly-generator-fuel-display = Бананиум
anomaly-generator-cooldown = Перезарядка: [color=gray]{$time}[/color]
anomaly-generator-no-cooldown = Перезарядка: [color=gray]Завершено[/color]
anomaly-generator-yes-fire = Статус: [color=forestgreen]Готов[/color]
anomaly-generator-no-fire = Статус: [color=crimson]Не готов[/color]
anomaly-generator-generate = Создать аномалию
anomaly-generator-charges = {$charges ->
    [one] {$charges} charge
    *[other] {$charges} charges
}
anomaly-generator-announcement = Было создано аномалию!

anomaly-command-pulse = Импульсный сигнал целевой аномалии
anomaly-command-supercritical = Заставляет целевой аномалийный объект войти в суперкритическое состояние

# Flavor text on the footer
anomaly-generator-flavor-left = Аномалия может появиться внутри оператора.
anomaly-generator-flavor-right = v1.1

anomaly-behavior-unknown = [color=red]ОШИБКА. Невозможно прочитать.[/color]

anomaly-behavior-title = анализ отклонений в поведении:
anomaly-behavior-point = [color=gold]Аномалия производит {$mod}% очков[/color]

anomaly-behavior-safe = [color=forestgreen]Аномалия чрезвычайно стабильна. Чрезвычайно редкие пульсации.[/color]
anomaly-behavior-slow = [color=forestgreen]Частота пульсаций гораздо реже.[/color]
anomaly-behavior-light = [color=forestgreen]Сила пульсации значительно снижена.[/color]
anomaly-behavior-balanced = Отклонений в поведении не обнаружено.
anomaly-behavior-delayed-force = Частота пульсаций значительно снижена, но их мощность увеличена.
anomaly-behavior-rapid = Частота пульсации намного выше, но её сила ослаблена.
anomaly-behavior-reflect = Обнаружена защитная пленка.
anomaly-behavior-nonsensivity = Обнаружена слабая реакция на частицы.
anomaly-behavior-sensivity = Обнаружена усиленная реакция на частицы.
anomaly-behavior-invisibility = Обнаружено искажение световой волны.
anomaly-behavior-secret = Обнаружено вмешательство. Некоторые данные не могут быть прочитаны
anomaly-behavior-inconstancy = [color=crimson]Обнаружена нематериальность. Типы частиц могут изменяться со временем.[/color]
anomaly-behavior-fast = [color=crimson]Частота пульсации значительно увеличена.[/color]
anomaly-behavior-strenght = [color=crimson]Пульсация энергии значительно усиленa.[/color]
anomaly-behavior-moving = [color=crimson]Обнаружена нестабильность координат[/color]
