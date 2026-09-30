execution-verb-name = Исполнить
execution-verb-message = Используйте своё оружие, чтобы убить кого-нибудь.

# All the below localisation strings have access to the following variables
# attacker (the person committing the execution)
# victim (the person being executed)
# weapon (the weapon used for the execution)

execution-popup-melee-initial-internal = Вы готовы {THE($weapon)} угрожать горлу {THE($victim)}.
execution-popup-melee-initial-external = { CAPITALIZE(THE($attacker)) } вытягивает {POSS-ADJ($attacker)} {$weapon} на горло {THE($victim)}.
execution-popup-melee-complete-internal = Ты перерезал горло {THE($victim)}!
execution-popup-melee-complete-external = { CAPITALIZE(THE($attacker)) } перерезает горло {THE($victim)}!

execution-popup-self-initial-internal = Готов ли ты использовать {THE($weapon)} против своего собственного горла?
execution-popup-self-initial-external = { CAPITALIZE(THE($attacker)) } вытягивает {POSS-ADJ($attacker)} {$weapon} и угрожает собой горлом.
execution-popup-self-complete-internal = Ты сам себе горло перерезал!
execution-popup-self-complete-external = { CAPITALIZE(THE($attacker)) } сам себе горло прорезал!
