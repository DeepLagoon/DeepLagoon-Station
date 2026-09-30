# Loc strings for various entity state & client-side PVS related commands

cmd-reset-ent-help = Использование: resetent <UID объекта>
cmd-reset-ent-desc = Сбросить сущность до последнего полученного состояния сервера. Это также сбросит сущности, которые были отсоединены в нулевое пространство.

cmd-reset-all-ents-help = Использование: resetallents
cmd-reset-all-ents-desc = Сбрасывает все объекты к последнему полученному состоянию сервера. Это влияет только на объекты, которые не были отсоединены в нулевое пространство.

cmd-detach-ent-help = Использование: отсоединить <Entity UID>
cmd-detach-ent-desc = Отсоединить сущность в нулевое пространство, как будто она покинула зону видимости.

cmd-local-delete-help = Использование: localdelete <Entity UID>
cmd-local-delete-desc = Удаляет сущность. В отличие от обычной команды удаления, эта команда работает ТОЛЬКО НА КЛИЕНТЕ. Если сущность не является клиентской, это, скорее всего, вызовет ошибки.

cmd-full-state-reset-help = Использование: fullstatereset
cmd-full-state-reset-desc = Сбрасывает всю информацию о состоянии объекта и запрашивает полное состояние с сервера.
