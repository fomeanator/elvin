# 🕰️ Time Romance — хаб, сборники, три типа новелл

Демонстрирует **hub-компоновку** (вместо карусели) и то, как три «типа» контента
— **Экспедиции / Свидания / Сюжет Реальности** — это НЕ три системы, а одна
новелла + поле `type` + условия разблокировки на `global.*` флагах. Движок один;
у каждой новеллы свой визуал и контент — всё данными в манифесте.

## Цепочка целиком

1. **Хаб** (`ui.browse.layout = "hub"`) — тайтл игры + плитки сборников.
2. Тап по сборнику → **список карточек**; тап по карточке → **деталь** (картинка +
   текст + «Играть»).
3. «Играть» на экспедиции **тратит 1 энергию** (`cost`) — не хватило → попап → магазин.
4. Играется обычная новелла (`exp_victoria.lvns`) с ветвлением и условием.
5. **Финал ставит `global.*` флаги** — `exp_victoria_done`, `date_victoria`,
   `reality_beat_2`.
6. Свидание и бит реальности в манифесте гейтятся этими флагами (`unlock`) — как
   только флаг стоит, их карточки перестают быть заблокированными.

Скрипт экспедиции — `exp_victoria.lvns` (компилируется, 0 warnings). Флаг внутри
экспедиции (`smelo`) — локальный; финальные (`global.*`) — общие на игрока.

## Манифест (фрагмент)

```json
{
  "ui": { "browse": { "layout": "hub", "title": "Time Romance", "subtitle": "Выбери…" } },

  "collections": [
    { "id": "expeditions", "name": "Экспедиции", "type": "expedition",
      "card": { "image": "/content/cards/exp.jpg", "desc": "Путешествия во времени" },
      "titles": ["exp_victoria"] },
    { "id": "dates", "name": "Свидания", "type": "date",
      "card": { "image": "/content/cards/dates.jpg", "desc": "Романтика" },
      "titles": ["date_victoria"] },
    { "id": "reality", "name": "Сюжет Реальности", "type": "reality",
      "card": { "image": "/content/cards/reality.jpg", "desc": "Что происходит дома" },
      "titles": ["reality_2"] }
  ],

  "titles": [
    { "id": "exp_victoria", "type": "expedition",
      "card": { "image": "/content/cards/exp_victoria.jpg", "desc": "Бал при дворе Виктории." },
      "cost": { "currency": "energy", "amount": 1 },
      "seasons": [ { "chapters": [ { "id": "exp_victoria", "script_url": "/content/scripts/exp_victoria.lvn" } ] } ] },

    { "id": "date_victoria", "type": "date",
      "unlock": "global.exp_victoria_done",
      "locked_hint": "Пройди экспедицию с Викторией",
      "card": { "image": "/content/cards/date_victoria.jpg", "desc": "Свидание с Викторией." },
      "seasons": [ { "chapters": [ { "id": "date_victoria", "script_url": "/content/scripts/date_victoria.lvn" } ] } ] },

    { "id": "reality_2", "type": "reality",
      "unlock": "global.reality_beat_2",
      "card": { "image": "/content/cards/reality_2.jpg", "desc": "Бит 2." },
      "seasons": [ { "chapters": [ { "id": "reality_2", "script_url": "/content/scripts/reality_2.lvn" } ] } ] }
  ]
}
```

## Что здесь — движок, а что — данные

| Движок (общий, одна прошивка) | Данные (у каждой игры свои) |
|---|---|
| хаб рисует **любые** `collections` | какие сборники, имена, арт |
| `type` — свободный тег, движок его не читает | `expedition`/`date`/что угодно |
| `unlock` — выражение над `global.*` | какой флаг гейтит карточку |
| `cost` списывает кошелёк | 1 энергия / бесплатно |
| финал ставит флаг через `set key="global.…"` | какие флаги, что открывают |
| тема экранов из `ui.browse` | цвета, арт, тексты, форма |

Другая новелла = другой `collections`/`type`/`unlock`/`cost` + другой `ui.browse`
→ другой визуал и контент, **та же прошивка**. Экспедиции/Свидания/Реальность —
это данные Time Romance, не движок.

## Собрать и проверить

```
lvnconv convert  -i exp_victoria.lvns -o exp_victoria.lvn
lvnconv validate exp_victoria.lvn        # OK: 32 command(s), 0 warning(s)
```
