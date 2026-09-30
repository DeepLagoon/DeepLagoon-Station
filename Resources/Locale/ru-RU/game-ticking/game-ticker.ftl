game-ticker-restart-round = Перезапуск раунда...
game-ticker-start-round = Раунд начинается...
game-ticker-start-round-cannot-start-game-mode-fallback = Не удалось запустить режим {$failedGameMode}! Переключаемся на режим {$fallbackMode}...
game-ticker-start-round-cannot-start-game-mode-restart = Не удалось запустить режим {$failedGameMode}! Перезапуск раунда...
game-ticker-start-round-invalid-map = Выбранная карта {$map} не подходит для режима игры {$mode}. Режим игры может работать некорректно...
game-ticker-unknown-role = Неизвестно
game-ticker-delay-start = Старт раунда отложен на {$seconds} секунд.
game-ticker-pause-start = Старт раунда приостановлен.
game-ticker-pause-start-resumed = Обратный отсчет до начала раунда возобновлен.
game-ticker-player-join-game-message = Добро пожаловать на Космическую Станцию 14! Если вы впервые играете, обязательно ознакомьтесь с правилами игры, и не стесняйтесь просить помощи в LOOC (локальный OOC) или OOC (обычно доступен только между раундами).
game-ticker-get-info-text = Привет! Добро пожаловать на [color=white]Космическую станцию 14![/color]
                            The current round is: [color = Белый]#{$roundId}[/color]
                            The current player count is: [color = [color=white]{$playerCount}[/color]
                            The current map is: [color = [colort=white]{$mapName}[/color]
                            The current game mode is: [color = [color=white]{$gmTitle}[/color]
                            >[color = [color=yellow]{$desc}[/color]
game-ticker-get-info-preround-text = Привет! Добро пожаловать на [color=white]Космическую станцию 14![/color]
                            The current round is: [color = белый]#{$roundId}[/color]
                            The current player count is: [color = [color=white]{$playerCount}[/color] ([color=white]{$readyCount}[/color] {$readyCount ->
                                [one] is
                                *[other] are
                            } ready)
                            The current map is: [color = белый]{$mapName}[/color]
                            The current game mode is: [color = [color=white]{$gmTitle}[/color]
                            >[color = [spoiler]{$desc}[/spoiler]
game-ticker-no-map-selected = [color=yellow]Карта еще не выбрана![/color]
game-ticker-player-no-jobs-available-when-joining = При попытке присоединиться к игре доступных заданий не нашлось.

# Displayed in chat to admins when a player joins
player-join-message = Игрок {$name} присоединился.
player-first-join-message = Игрок {$name} присоединился впервые.

# Displayed in chat to admins when a player leaves
player-leave-message = Игрок {$name} вышел.

latejoin-arrival-announcement = {$character} ({$job}) {CONJUGATE-HAVE($entity)} прибыл на станцию!
latejoin-arrival-announcement-special = {$job} {$character} в экипаже!
latejoin-arrival-sender = Станция
latejoin-arrivals-direction = Шаттл, перевозящий вас на вашу станцию, скоро прибудет.
latejoin-arrivals-direction-time = Шаттл, доставляющий вас на вашу станцию, прибудет через {$time}.
latejoin-arrivals-dumped-from-shuttle = Загадочная сила не дает вам покинуть базу на шаттле прибытия.
latejoin-arrivals-teleport-to-spawn = Таинственная сила телепортирует вас из шаттла прибытия. Приятной смены!

preset-not-enough-ready-players = Не удаётся запустить {$presetName}. Требуется {$minimumPlayers} игроков, а у нас {$readyPlayersCount}
preset-no-one-ready = Не удаётся запустить {$presetName}. Нет готовых игроков.

game-run-level-PreRoundLobby = Пре-раунд лобби
game-run-level-InRound = В раунде
game-run-level-PostRound = Построение
