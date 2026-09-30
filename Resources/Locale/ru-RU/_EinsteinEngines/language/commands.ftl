command-list-langs-desc = Список языков, которые ваш текущий персонаж может говорить в данный момент.
command-list-langs-help = Использование: {$command}

command-saylang-desc = Отправить сообщение на определенном языке. Чтобы выбрать язык, можно использовать название языка или его позицию в списке языков.
command-saylang-help = Использование: {$command} <идентификатор языка> <сообщение>. Пример: {$command} TauCetiBasic "Привет, мир!". Пример: {$command} 1 "Привет, мир!"

command-language-select-desc = Выберите текущий язык вашего персонажа. Можно использовать название языка или его позицию в списке языков.
command-language-select-help = Использование: {$command} <код языка>. Пример: {$command} 1. Пример: {$command} TauCetiBasic

command-language-spoken = Говорящая речь:
Russian:
command-language-understood = Понял:
command-language-current-entry = {$id}. {$language} - {$name} (текущий)
command-language-entry = {$id}. {$language} - {$name}

command-language-invalid-number = Номер языка должен находиться в диапазоне от 0 до {$total}. В качестве альтернативы используйте название языка.
command-language-invalid-language = Язык {$id} не существует или вы не владеете им.

# Toolshed

command-description-language-add = Добавляет новый язык в связанную сущность. Последние два аргумента обозначают, будет ли он произноситься/пониматься. Пример: 'self language:add "Canilunzt" true true'
command-description-language-rm = Удаляет язык из конвейерной сущности. Работает аналогично language:add. Пример: 'self language:rm "TauCetiBasic" true true'.
command-description-language-lsspoken = Выводит все языки, на которых может говорить сущность. Пример: 'self language:lsspoken'
command-description-language-lsunderstood = Выводит все языки, которые сущность может понимать. Пример: 'self language:lssunderstood'

command-description-translator-addlang = Добавляет новый целевой язык в сущность конвейерного переводчика. См. language:add для получения подробной информации.
command-description-translator-rmlang = Удаляет целевой язык из конвейерного перевода. См. language:rm для подробностей.
command-description-translator-addrequired = Добавляет новый обязательный язык в сущность переводчика. Пример: 'ent 1234 translator:addrequired "TauCetiBasic"'
command-description-translator-rmrequired = Удаляет необходимый язык из сущности конвейерного переводчика. Пример: 'ent 1234 translator:rmrequired "TauCetiBasic"'
command-description-translator-lsspoken = Выводит все разговорные языки для сущности переводчика. Пример: 'ent 1234 translator:lsspoken'
command-description-translator-lsunderstood = Выводит все известные языки для передаваемого перевода. Пример: 'ent 1234 translator:lssunderstood'
command-description-translator-lsrequired = Выводит все необходимые языки для конвейерного перевода. Пример: 'ent 1234 translator:lsrequired'

command-language-error-this-will-not-work = Это не сработает.
command-language-error-not-a-translator = Сущность {$entity} не является переводчиком.
