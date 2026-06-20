// Демо-глава Liminal Tales — упражняет всё подмножество ink2lvn.
// Открой этот файл в Inky (https://github.com/inkle/inky/releases):
// текст и выборы играются как есть, теги стейджинга Inky просто показывает.

# scene: demo
# actors: Луна=heroine, Морт=boy

VAR met_luna = false
VAR courage = 0
CONST BRAVE = 2

-> intro

== intro ==
# preload: /content/bg/forest.jpg /content/actors/heroine.png /content/audio/theme.ogg
# bg: demo.forest sprite_url=/content/bg/forest.jpg
# audio: music play /content/audio/theme.ogg loop=true volume=0.8
# fade: clear 0.6
Лес дышал туманом. // нарратив: строка без "Имя:"
# actor: heroine show=true position=left enter=left sprite_url=/content/actors/heroine.png
Луна [happy]: Ты всё-таки пришёл. # style: whisper
Морт: Я обещал. -> meeting

== meeting ==
~ met_luna = true
Луна: {meeting: Мы это уже обсуждали.|Зачем ты здесь?}

* [Сказать правду]
    Морт: Мне нужна твоя помощь.
    ~ courage = courage + 1
* [Соврать] -> lie
+ {met_luna} [Промолчать (10 soft)]
    # camera: shake amplitude=6 duration=0.3
    Тишина повисла между ними.
- (after_answer)
# dim: 0.3 0.5
Луна задумалась {~мгновение|на секунду|надолго}.
-> luna_verdict ->
-> finale

== luna_verdict ==
// Туннель: переиспользуемая сценка, возвращается к месту вызова.
{courage >= BRAVE && met_luna:
    Луна [smile]: Ты смелее, чем кажешься.
    ~ courage = courage * 2
- else:
    Луна: Посмотрим, чего ты стоишь.
}
->->

== lie ==
Морт: Просто гулял неподалёку...
~ courage = courage - 1
-> after_ref
= after_ref
-> meeting.after_answer

== finale ==
# particles: rain
# hint: Дождь усиливается
Луна: Идём. Дальше — вместе.
# audio: music stop fade=2.0
# fade: black 0.8
-> END
