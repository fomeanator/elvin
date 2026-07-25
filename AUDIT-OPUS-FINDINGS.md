# Адверсариальное ревью `3449daf` + `3f6dff6` (Opus, 2026-07-25)

Все находки ниже **проверены исполнением кода** (тесты писались во временных
файлах `tools/lvnconv/importer/zzz_audit*_test.go`, удалены после прогона;
воспроизводимые сниппеты приведены в тексте). `go test ./...` в
`tools/lvnconv` зелёный после удаления временных файлов.

Легенда: серьёзность — влияние на прод/данные, а не на красоту кода.

---

## СВОДКА (сначала главное)

| # | Сев. | Одной строкой |
|---|------|----------------|
| O1 | **CRITICAL** | 3f6dff6 починил *генератор* .lvns, но **не перегенерировал 82 уже задеплоенных сайдкара** — потеря данных при «Save to app» жива на проде СЕГОДНЯ (измерено: −330 audio, −25 wardrobe_show, −25 actor, −275 wallet_cost, +1500 меток) |
| O2 | **CRITICAL** | Несбалансированная `«` в тексте реплики: `soviet.lvn` вообще **не компилируется** обратно; `soviet-ch01` молча **теряет 3 реплики** (склеивает в одну) |
| O3 | **HIGH** | Тело опции choice (`body`) выбрасывается ToLvns → `set _once_*` теряется, вопросы «один раз» становятся бесконечными (14 живых случаев в soviet) |
| O4 | **HIGH** | Конструктор regex в маппере может выдать «всё подряд» `^\s*(.+?)\.?\s*$`, превью показывает 100% → импорт **удаляет всю нарративку** и делает bg из каждой строки |
| O5 | **HIGH** | Хост-опы (`ext`/`LvnOps.Register`, напр. `leaderboard_submit`) и `anim` не round-trip'ятся: recompile падает. Фикс `wardrobe_show` в KnownOps — заплатка одного симптома, а не класса |
| O6 | **HIGH** | `stage-extract` пишет в `TMPDIR` и **никогда не чистит** (совпадает с H1 родительского аудита) + жёсткая связка: `detect-roles` требует dir под `-import-root`, т.е. фича работает только потому что на проде `TMPDIR=/srv/lvn/tmp` |
| O7 | **MEDIUM-HIGH** | `report.speakers` приходит как JSON `null` → `report.speakers.map` в ImportMapper падает белым экраном |
| O8 | **MEDIUM-HIGH** | Роль «Рассказчик» в таблице маппера — ловушка без выхода: DetectRoles исключает narrator-спикеров из отчёта, после «Пересчитать» строка исчезает навсегда |
| O9 | **MEDIUM** | `setRole` пишет `narrator_roles: []` даже для роли «Персонаж» → на частичном шаблоне-оверлее затирает 9 дефолтных narrator-ролей |
| O10 | **MEDIUM** | Двойное применение `SpeakerNames` не идемпотентно на цепочке A→B, B→C (подтверждено тестом) |
| O11 | **MEDIUM** | DetectRoles не видит xlsx-роль протагониста (`ExtraCast`) и xlsx-легенду эмоций → ложные предупреждения «протагонист без арта» и «цвета без эмоции» на каждом bundle-импорте |
| O12 | **MEDIUM** | `«…»`-обёртка реплики срезается на round-trip (24 живых случая в soviet) |
| O13 | **MEDIUM** | `regenerateLvnsSidecars` молча не работает для одноглавного bundle-импорта (ходит только по `res.Scripts`) |
| O14 | **MEDIUM** | DELETE `/import-templates/<name>` — без `snapshotHistory` и без `writeMu`: шаблон исчезает без версии для откатa |
| O15 | **MEDIUM** | `set default=true` с простым (не namespaced) ключом теряет флаг `default` → перезапись значения игрока при входе в главу |
| O16 | **MEDIUM** | Переименование 837 синтетических меток (`n37_000000` → `__nf1`) при re-save ломает якорь позиции сейвов (`AnchorOf`/`Relocate`) |
| O17 | **MEDIUM** | `choice timeout` / `timeout_goto` выбрасываются ToLvns |
| O18 | **MEDIUM** | Суффиксный fallback `displayNameFor` теперь применяется в Run() к id сущностей → чужой персонаж `Ivan_Petrov` получает имя «Иван» из cold.json |
| O19 | **LOW-MEDIUM** | Простые ключи с именем директивы (`def`/`scene`/`return`/`call`/`actor_map`/`choice`) ломают round-trip по-разному, `return` — инъекцией `return`-опа |
| O20 | **LOW-MEDIUM** | Шаблон с именем `default` можно сохранить, но `ResolveTemplate` его никогда не прочитает |
| O21 | **LOW** | Op `text` (реактивные метки) декомпилируется в несовместимую с парсером форму — значения «съезжают» |
| O22 | **LOW** | `wallet_cost` с валютой из двух слов остаётся строкой (не объектом) |
| O23 | **LOW** | Метка `effects` с запятой обрезается («Иван, брат» → «брат») |
| O24 | **LOW** | Имя спикера, начинающееся с `-`, роняет весь recompile |

---

## O1 [CRITICAL] Задеплоенные .lvns-сайдкары остались «до фикса» — потеря данных жива на проде

**Файлы:** `tools/lvnconv/importer/bundle.go:188` (`regenerateLvnsSidecars`) —
исправление затрагивает только НОВЫЕ импорты; `server/content/scripts/*.lvns`
(82 файла, mtime 20 июл, т.е. до коммита 3f6dff6 от 25 июл).

**Что не так.** 3f6dff6 корректно перенёс `regenerateLvnsSidecars` в конец
`RunBundle`. Но уже отгруженные сайдкары никто не перегенерировал, а
`panel/src/components/ScriptSection.jsx:249-270` при открытии главы
**предпочитает соседний .lvns**, и `save()` (`:465-482`) пишет обратно и
`.lvns`, и скомпилированный из него `.lvn`. То есть исходная бага (потеря
данных при каждом «Save to app») **сохраняется в полном объёме для всего
существующего контента** — фикс на неё не действует ретроактивно.

**Измерено** (компиляция каждого отгруженного `.lvns` через тот же
`lvns.Convert`, что и WASM панели, и дифф оп-стрима против отгруженного `.lvn`):

```
cold-ch01   lvn=1664 recompiled=1669  audio:-13 wardrobe_show:-1 actor:-1 say:+6 choice:+3 set:+2 label:+6 goto:+3
cold-ch24   lvn=3688 recompiled=3857  audio:-26 wardrobe_show:-1 actor:-1 say:+9 choice:+3 set:+2 label:+95 goto:+88
cold-ch25   lvn=3773 recompiled=3953  audio:-25 wardrobe_show:-1 actor:-1 say:+8 choice:+3 set:+2 label:+99 goto:+95
… (все 25 глав Cold, суммарно ≈ -330 audio, -25 wardrobe_show, -25 actor)
soviet-ch01 lvn=1535 recompiled=1533  say:-3   ← см. O2
rpg-inv     САЙДКАР НЕ КОМПИЛИРУЕТСЯ: line 15: def: "=" is not a valid preset name
```

Плюс полевые проверки на `cold-ch23`:

```
                lvns   lvn
wardrobe_show      0     1
audio              0    26
wallet_cost        0    11     ← 11 премиум-выборов становятся БЕСПЛАТНЫМИ
mirror             0   143
outfit             0   246
actor_map          0     0     ← ни одного who_id
set default=true   3     0     ← вырезанный boilerplate ВОЗВРАЩАЕТСЯ в .lvn
```

**Сценарий отказа.** Автор открывает любую главу Cold в IDE панели (например,
чтобы поправить одну реплику), жмёт Ctrl+S. В прод улетает `.lvn` без музыки,
без гардероба (нет `wardrobe_show` и нет актёра-героини в гардеробной сцене),
с 11 бесплатными премиум-выборами, без `outfit`/`hair`/`mirror` на 246 actor-опах,
без `who_id` (спикер не подсвечивается), и с вернувшимся 3-строчным
`set default=true` boilerplate. Ничего не падает — всё тихо.

**Фикс:**
1. Одноразовая регенерация: прогнать `ToLvns` по каждому отгруженному `.lvn` и
   перезаписать сайдкар (CLI-подкоманда `lvnconv resync-lvns <content-dir>`), с
   обязательной проверкой `lvns.Convert` результата.
2. Защита в панели: при открытии главы, если `.lvns` **старше** соседнего `.lvn`
   (или его recompile даёт другое число опов), показывать баннер
   «сайдкар устарел — сохранение перетрёт .lvn» и блокировать save до явного
   подтверждения/регенерации. Сейчас ничего не сравнивается.
3. В `RunBundle` после `regenerateLvnsSidecars` валидировать сайдкар
   (`lvns.Convert`) и класть расхождение в `res.Warnings` — сейчас
   пост-импортная валидация (`bundle.go:130-148`) смотрит **только `.lvn`**.

---

## O2 [CRITICAL] Несбалансированная `«` в реплике: recompile либо падает, либо молча склеивает реплики

**Файлы:** `tools/lvnconv/importer/decompile.go:214-242` (`sayLine`, терсная
форма) + `tools/lvnconv/internal/lvns/convert.go:83-96` (сканер `«…»`,
не учитывающий строки/кавычки).

**Что не так.** Парсер трактует `«` как открытие многострочной строки и
считает глубину **по сырой строке, без учёта кавычек**. Терсная форма
`Автор: «Союз нерушимый…` (текст открывает кавычку и не закрывает её в этой же
реплике — стих разбит на строки) заставляет парсер проглатывать последующие
строки файла.

**Живые данные (сканирование `server/content/scripts/*.lvn`):** 5 реплик с
несбалансированными `«»`:

```
soviet-ch01.lvn #1408  Автор: «Союз нерушимый республик свободны х...
soviet-ch01.lvn #1411  Автор: ... Единый, могучий Советский Союз!»
soviet.lvn      #18068 Автор: ... Единый, могучий Советский Союз!»
soviet.lvn      #18070 Автор: «Союз нерушимый республик свободны х...
soviet.lvn      #20487 Главный герой: Видела только такое в «Ну, погоди!
```

Два разных исхода, оба плохие:
- `soviet.lvn`: `»` встречается раньше `«` → на глубине 0 игнорируется, потом
  `«` открывается и не закрывается → **`line 18712: unclosed «…»` — вся глава
  не компилируется**, «Save to app» невозможен вообще.
- `soviet-ch01.lvn`: `«` на #1408 закрывается только на #1411 → 4 реплики
  склеиваются в одну многострочную → **say:-3**, и весь оп-стрим после этого
  сдвигается (в диффе это видно как каскад «actor/say mismatch», «who
  Автор→Игрок», «actor id Starushka→Vadim», «goto n10→n7» — это последствия
  сдвига, а не отдельные баги).

**Минимальный репро:**
```go
doc := &articy.Doc{Script: []articy.Cmd{
    {"op":"say","who":"Аня","text":"Он сказал: «Не уходи"},
    {"op":"say","text":"next line"},
}}
_, err := lvns.Convert(string(ToLvns(doc)))
// line 1: unclosed «…» — the opening « never finds its »
```

**Фикс.** Два независимых:
- `sayLine` должен уходить в generic-форму (`say text="…"`) не только при
  наличии `"`, но и при **несбалансированных `«`/`»`** в тексте — и
  дополнительно `quote()` должен экранировать/escape'ить `«»`, потому что
  сканер `«…»` в convert.go работает и внутри `"…"` (verbose-форма тоже падает).
- В `convert.go` сделать подсчёт `«»` строко-осведомлённым (как уже сделано в
  `stripLineComment`/`firstBlockBrace`), чтобы кавычка внутри `"…"` не открывала
  многострочный блок.

---

## O3 [HIGH] Тело опции choice (`body`) выбрасывается — «спросить один раз» перестаёт работать

**Файл:** `tools/lvnconv/importer/decompile.go:245-258` (`choiceOption`).

`choiceOption` берёт из опции только `text`/`goto`/`requires_*`/`cost`/
`wallet_cost`/`expr`/`effects`. `body` (список опов, исполняемых при выборе)
используется **только** чтобы вытащить оттуда `goto`, а сами опы теряются.

**Живой случай (soviet-ch19.lvn #923, 14 таких в soviet):**
```
до:  {"expr":"!_once_n21_000000","text":"Почему я сидела…",
      "body":[{"op":"set","key":"_once_n21_000000","value":true},
              {"op":"goto","label":"n21_000000"}]}
после round-trip: {"expr":"!_once_n21_000000","goto":"n21_000000","text":"Почему я сидела…"}
```
Флаг `_once_n21_000000` больше никто не ставит, а условие показа опции
(`!_once_n21_000000`) навсегда истинно → пул вопросов «спросить один раз»
никогда не исчерпывается, игрок крутится по одним и тем же вопросам.

**Фикс.** Либо эмитить тело как блок под опцией (в языке уже есть форма
`- text -> label` + вложенные строки? если нет — сгенерировать синтетическую
метку `:opt_body_N` с телом и `goto` на неё), либо, как минимум, **падать/варнить**
в `regenerateLvnsSidecars`, когда опция несёт `body` длиннее одного `goto` —
чтобы потеря не была молчаливой.

---

## O4 [HIGH] Конструктор scene-marker regex может выдать «всё подряд», и превью это подтвердит

**Файл:** `panel/src/components/ImportMapper.jsx:69-80` (`buildSceneMarkerRegex`),
`:287-327` (`SceneMarkerBuilder`).

`buildSceneMarkerRegex` не проверяет, что от примера остался хоть какой-то
литеральный контекст. Проверено в node:

```
"Сцена 5. Кухня." + location="Сцена 5. Кухня."  →  ^\s*(.+?)\s*$      (совпадает с ЛЮБОЙ строкой)
"Кухня."         + location="Кухня"             →  ^\s*(.+?)\.?\s*$   (совпадает с ЛЮБОЙ строкой)
"Кухня"          + location="Кухня"             →  ^\s*(.+?)\s*$
```

Первый случай — ровно то, что сделает торопливый автор: подпись поля гласит
«вставь ровно как в примере», и вставить целую строку естественно. Хуже всего,
что `matchCount` считается **только по `scene_marker_misses`** (несовпавшие
строки), поэтому превью бодро показывает `8/8 примеров совпадёт` — зелёный
сигнал на катастрофическом паттерне.

**Что происходит дальше** (`tools/lvnconv/importer/stage.go:200-205`) —
подтверждено тестом:

```go
tpl.Staging.SceneMarkerRegex = `^\s*(.+?)\.?\s*$`
// 3 реплики на входе → 0 реплик на выходе:
[{"op":"bg","id":"Она_села_на_подоконник_и_закурила","sprite_url":"/content/bg/Она_села_на_подоконник_и_закурила.jpg"},
 {"op":"bg","id":"Дождь_не_переставал",...},
 {"op":"bg","id":"Привет",...}]
```

Вся нарративка удалена, вместо неё bg-опы с 404-путями. Серверная валидация
PUT'а (`server/import_templates.go:99`) проверяет только компилируемость regex.

**Фикс:**
- В `buildSceneMarkerRegex` возвращать `""` (и не показывать «Применить»),
  если и `before`, и `after` после экранирования пусты — паттерн без якорного
  литерала не является маркером сцены.
- Считать в превью не только hit по misses, но и **false-positive rate** по
  выборке уже совпадающих/обычных нарративных строк (сервер их уже отдаёт —
  можно расширить `scene_marker_misses` полем «обычные строки»).
- Серверная валидация: отклонять паттерн, который матчит контрольную прозаическую
  строку (`ParseTemplateJSON` — идеальное место).
- Заодно в `AutoStage` игнорировать пустой захват: при `^Сцена (.*)$` и строке
  `"Сцена "` сейчас получается `{"op":"bg","id":"","sprite_url":"/content/bg/.jpg"}`.

---

## O5 [HIGH] Хост-опы и `anim` не round-trip'ятся; `wardrobe_show` в KnownOps лечит симптом

**Файлы:** `tools/lvnconv/importer/decompile.go:332-347` (`genericOp`),
`tools/lvnconv/internal/lvns/convert.go:44-49` (правка коммита).

`genericOp` печатает `<op> k=v …` для **любого** незнакомого опа и **выбрасывает
все массивы/объекты/null**. Парсер такую строку принимает только если оп
есть в `KnownOps`. Коммит 3f6dff6 добавил в `KnownOps` `wardrobe_show` — это
починило один конкретный оп, но класс проблемы остался:

```
leaderboard_submit board="quiz" score=10
→ line 1: unknown command "leaderboard_submit" (…)     ← doll-quiz.lvn, живой файл
anim id="codel" loop=true          (tracks/prop выброшены genericOp)
→ line 1: anim: prop required                          ← codel-anim-demo, doll-demo, knight-demo, tour-ch04
```

Итого 8 из 83 отгруженных `.lvn` **не декомпилируются-компилируются обратно**
(из них `soviet.lvn` — по O2, `rpg-inv`/`goblin-battle` — по O19, остальные —
здесь). Хост-опы — документированный механизм расширения (`ext` в языке,
`LvnOps.Register` в C#), т.е. любая игра-встройка ломает round-trip.

**Фикс.**
- `ToLvns`: для опа, которого нет в `lvns.KnownOps`, эмитить `ext <op> k=v …`
  (парсер это умеет: `convert.go` KnownOps["ext"]). Импортёр может импортировать
  `internal/lvns` — циклов нет.
- Для опов со структурными полями (`anim.tracks`, `choice.options`) — либо
  специальные ветки, либо fail-loud: `regenerateLvnsSidecars` должен варнить,
  когда `genericOp` выбросил не-скаляр.
- Обязательный self-check: `ToLvns` → `lvns.Convert` сразу после генерации;
  ошибка → warning в отчёт импорта (сейчас `.lvns` вообще не валидируется).

---

## O6 [HIGH] `stage-extract`: утечка scratch-директорий + скрытая связка через TMPDIR

**Файл:** `server/import_detect.go:43-53`.

Две проблемы в четырёх строках:

1. **Утечка.** `os.MkdirTemp("", "lvn-detect-*")` + распаковка полного articy-проекта,
   и **ни одного `os.RemoveAll`** во всём коде (в bundle-импорте, для сравнения,
   есть `defer os.RemoveAll(stage)`). Каждый клик «Настроить роли ▸» — новая
   директория на 100+ МБ навсегда. (Совпадает с H1 родительского аудита —
   подтверждаю независимо.)

2. **Скрытая связка через TMPDIR, из-за которой фича «работает случайно».**
   `handleStageExtract` возвращает `dir` внутри scratch-каталога, а
   `handleDetectRoles` (`:82-85`) требует `s.importDirAllowed(body.Dir)` — то есть
   dir **должен лежать под `-import-root`**. Совпадение обеспечивается только
   тем, что `deploy/setup.sh:82` дописывает `TMPDIR=$LVN_HOME/tmp` в `lvn.env`,
   а `-import-root` указывает на `$LVN_HOME`. На любом другом развёртывании
   (дефолтный `/tmp`, или `PrivateTmp=true` без TMPDIR-override — а
   `deploy/lvn.service.template:45` его как раз ставит) `detect-roles` вернёт
   `403 dir must live under the configured -import-root`, и весь маппер мёртв.
   Нигде в коде эта зависимость не выражена.

**Фикс:**
- Класть scratch не в `MkdirTemp("")`, а в **явную** поддиректорию под
  `-import-root` (например `<import-root>/.detect-cache/<hash-of-path>`) — тогда
  и allow-check становится структурно верным, и путь предсказуем.
- Janitor: при каждом `stage-extract` удалять `lvn-detect-*` старше N часов
  (и/или переиспользовать кэш по хэшу архива вместо создания новой директории —
  панель всё равно зовёт `stage-extract` повторно при каждом открытии маппера).

---

## O7 [MEDIUM-HIGH] `speakers: null` роняет ImportMapper

**Файлы:** `tools/lvnconv/importer/detect.go:81` (`Speakers []SpeakerDetect
\`json:"speakers"\`` — без `omitempty`), `panel/src/components/ImportMapper.jsx:191`
(`report.speakers.map(...)` — единственное место в файле без `|| []`).

Подтверждено маршалингом:
```
{"project_dir":"x","template_name":"","chapters":0,"speakers":null,…}
```

**Сценарий отказа.** DetectRoles исключает narrator-спикеров из `Speakers`
(`detect.go:143`). Автор помечает всех показанных спикеров как «Рассказчик»
(вполне реальный сценарий на техническом проекте, где «говорят» только
служебные роли) и жмёт «Пересчитать» → `speakers: null` →
`TypeError: Cannot read properties of null (reading 'map')` → модалка маппера
падает белым экраном, черновик шаблона теряется.

**Фикс:** `json:"speakers,omitempty"` не спасёт (поле просто исчезнет);
правильнее в Go инициализировать `rep.Speakers = []SpeakerDetect{}`, **и**
в JSX писать `(report.speakers || []).map`. Плюс error boundary на модалке.

---

## O8 [MEDIUM-HIGH] Роль «Рассказчик» — ловушка без выхода

**Файлы:** `tools/lvnconv/importer/detect.go:143-151`,
`panel/src/components/ImportMapper.jsx:17-28, 191-204`.

`DetectRoles` при `tpl.isNarrator(who)` делает `continue` — спикер **не попадает
в `rep.Speakers` вообще**. А `ROLE_OPTIONS` предлагает роль «Рассказчик», и
`roleOf` умеет её вернуть только если `who` уже в `narrator_roles`.

**Сценарий отказа.** Автор ошибочно помечает главного персонажа как
«Рассказчик» → жмёт «Пересчитать» → строка исчезает из таблицы навсегда.
Откатить внутри маппера нельзя ничем, кроме «Продвинутый режим (сырой JSON)»,
который по замыслу «не должен видеть нетехнический автор». Ровно та ошибка,
против которой экран и делался.

Симметрично: ключевая заявленная ценность («рассказчик с незнакомым словарём
сразу виден сверху») работает только в одну сторону — назначить рассказчика
можно, снять нельзя.

**Фикс:** возвращать narrator-спикеров в `Speakers` с `role:"narrator"`
(агрегаты scene-marker считать по ним же, как сейчас), а не выкидывать.

---

## O9 [MEDIUM] `setRole` затирает дефолтные списки ролей на частичном шаблоне

**Файл:** `panel/src/components/ImportMapper.jsx:34-45`.

```js
st.narrator_roles = withoutFrom(st.narrator_roles, who);           // undefined → []
st.protagonist_roles = withoutFrom(st.protagonist_roles, who);
st.protagonist_speaker_labels = withoutFrom(st.protagonist_speaker_labels, who);
```

`withoutFrom` возвращает `[]` для `undefined`, и три ключа **всегда** попадают
в черновик — даже когда автор выбрал роль «Персонаж» и ничего не добавляет.
Шаблоны — overlay-by-presence, а `json.Unmarshal` в Go **заменяет** слайс, а не
дополняет. Значит `"narrator_roles": []` в файле = все 9 дефолтных narrator-ролей
(«Автор», «Игрок», «Информация», …) стёрты.

**Когда стреляет.** Только если исходный шаблон **не объявляет** этих полей.
Проверено: прод-`cold.json` объявляет все три (не стреляет), а
`server/content/import-templates/example-en.json` — частичный оверлей, и
сам файл `import_templates.go:96-98` документирует «partial overlay (e.g. only
speaker_aliases) must stay partial». Для такого шаблона одно изменение роли
превращает «Автор» в NPC → нарратор начинает стейджиться как персонаж с
плейсхолдером.

**Фикс:** писать ключ только если список непуст, либо (надёжнее) при загрузке
черновика в маппер инфлейтить его до полной формы дефолта для этих трёх полей.

---

## O10 [MEDIUM] Двойное применение `SpeakerNames` не идемпотентно (подтверждено)

**Файлы:** `tools/lvnconv/importer/importer.go:303-304, 435-437` (новый вызов на
`doc.Script`) + `bundle_wire.go:547` (второй проход на скомпилированном `.lvn`);
`speaker_rewrite.go:54-102`.

Подтверждено тестом:

```go
tpl.SpeakerNames = map[string]string{"Man":"Мужчина", "Мужчина":"Незнакомец"}
ops := []map[string]any{{"op":"say","who":"Man","text":"hi"}}
applySpeakerNameOverrides(ops, tpl)  // pass 1 (Run)          → "Мужчина"
applySpeakerNameOverrides(ops, tpl)  // pass 2 (PostProcess)   → "Незнакомец"  ← НЕ идемпотентно
```
То же для `applySpeakerNameOverridesToSprites` (имя сущности «Мужчина» → «Незнакомец»).

**Реалистичность.** В текущем прод-`cold.json` цепочек нет (проверил: ни одно
значение не является ключом; суффиксных цепочек тоже нет) — сегодня не стреляет.
Но маппер показывает **все** метки отдельными строками и позволяет вписать в
«Имя на экране» строку, которая является меткой другого спикера — цепочка
создаётся в два клика, и результат зависит от того, bundle-импорт или обычный
(одиночный проходит один раз, bundle — два).

**Фикс:** сделать функции по-настоящему идемпотентными — помечать
переписанные опы (напр. хранить исходную метку в `who_src`) или считать
transitive closure `SpeakerNames` один раз в `compile()` и применять к
неподвижной точке. Минимум — не вызывать одно и то же дважды: убрать
`applySpeakerNames`/`renameProtagonistSpeaker` из `PostProcessBundle`, раз
`Run` их уже сделал (в bundle-режиме `Run` вызывается всегда).

---

## O11 [MEDIUM] DetectRoles предсказывает не то, что сделает импорт

**Файл:** `tools/lvnconv/importer/detect.go:109-120, 130, 197-222`.

`DetectRoles` строит cast только из `adpd.Cast(projectDir)` + `SpeakerAliases`.
Реальный bundle-импорт (`bundle.go:81-91`) дополнительно кладёт в
`opt.ExtraCast` протагониста из **xlsx**-ростера (`Protagonist.TechName`), а
легенду эмоций берёт из **xlsx** (`bundle.go:66-79`, `opt.EmotionColors`).
Ни того, ни другого превью не видит.

**Следствия на каждом bundle-импорте Cold-типа:**
- Всегда срабатывает громкое предупреждение `protagonist role(s) with no roster
  art … she'll stay invisible on stage` — при том что импорт даст ей реальный
  арт. Оно же подталкивает включить `placeholder_protagonist` (безвредно, но
  автор принимает решение по ложным данным).
- `emotion_color_misses` завышен: цвета, которые закрывает xlsx-легенда,
  показываются как «без эмоции».

**Фикс:** принимать в `detect-roles` путь к xlsx (панель его уже загрузила —
`bundle.uploads.vars.path`) и применять `ParseVarsXlsx` тем же кодом, что
`RunBundle`. Как минимум — писать в отчёт, что превью не учитывает xlsx.

---

## O12 [MEDIUM] `«…»`-обёртка реплики срезается на round-trip

**Файлы:** `decompile.go:214-225` (терсная форма) + `convert.go` `stripQuotes`.

```
до:    {"op":"say","who":"Аня","text":"«Мы нежность, мы нежность...»"}
.lvns: Аня: «Мы нежность, мы нежность...»
после: {"op":"say","who":"Аня","text":"Мы нежность, мы нежность..."}
```

Живых случаев (текст целиком обёрнут в `«…»`): **24** — soviet.lvn 13,
soviet-ch03 3, soviet-ch21 3, soviet-ch01 2, soviet-ch09 2, soviet-ch05 1.
Видно игроку (пропали кавычки у цитат/стихов/песен). Потеря одноразовая
(повторные сохранения уже стабильны), но необратимая без реимпорта.

**Фикс:** в `sayLine` уходить в generic-форму, если текст начинается на `«` и
кончается на `»` (как уже делается для `"`); либо в `stripQuotes` не срезать
`«»`, когда строка пришла из терсной формы (это авторский текст, а не
синтаксис).

---

## O13 [MEDIUM] `regenerateLvnsSidecars` не работает для одноглавного bundle-импорта

**Файл:** `tools/lvnconv/importer/bundle_wire.go:1121-1147`.

Функция ходит только по `res.Scripts`. Одноглавный импорт (`Run` без
многоглавного пути) кладёт скрипт в `res.ScriptRel`/`res.Lvn` и сайдкар в
`res.LvnsRel`/`res.Lvns` — `res.Scripts` при этом **пуст**. Подтверждено тестом:

```go
res := &Result{ScriptRel:"scripts/x.lvn", Lvn:[]byte(`{"script":[{"op":"say","text":"NEW"}]}`),
               LvnsRel:"scripts/x.lvns", Lvns:[]byte("OLD STALE SOURCE\n")}
regenerateLvnsSidecars(res)   // res.Lvns не изменился
```

Т.е. для articy-проекта с одной главой (или когда многоглавный путь
откатился в `chapterFallback`) починенная бага **остаётся** в исходном виде.
Тот же класс: `stripDeclaredDefaults`/`buildVarsDeclaration` в
`bundle.go:154-170` тоже гейтятся на `len(res.Scripts) > 0`.

**Фикс:** добавить в `regenerateLvnsSidecars` ветку для одиночной формы
(`res.Lvn`/`res.Lvns`), или нормализовать `Run` так, чтобы одноглавный
результат тоже жил в `res.Scripts`.

---

## O14 [MEDIUM] DELETE шаблона — без истории и без writeMu

**Файл:** `server/import_templates.go:120-126`.

```go
case http.MethodDelete:
    ...
    _ = os.Remove(path)          // ни snapshotHistory, ни s.writeMu.Lock()
```

Комментарий в шапке файла обещает «versions every edit through the SAME
editorial-history machinery … it deserves the same rollback safety net as
manifest.json». PUT это делает, DELETE — нет. `snapshotHistory` копирует
**текущую** версию перед перезаписью, значит последнее сохранённое содержимое
шаблона попадает в `.history` только при следующем PUT. После DELETE
последняя версия шаблона **не восстановима** (в истории лежит предыдущая).
Плюс DELETE идёт без `writeMu`, т.е. может гонятся с `snapshotHistory` другого
запроса (тот прочитает файл, который в этот момент удаляют — не фатально,
`snapshotHistory` игнорирует ошибку чтения, но результат — молча пропущенный снапшот).

Отмечу: `deleteImportTemplate` в `panel/src/lib/api.js:288` экспортирован, но
UI его нигде не вызывает — эксплуатируется только руками/скриптом.

**Фикс:** обернуть DELETE в `s.writeMu` и вызвать `snapshotHistory(s.content, rel)`
до `os.Remove`.

---

## O15 [MEDIUM] `set default=true` с простым ключом теряет флаг `default`

**Файл:** `tools/lvnconv/importer/decompile.go:85-102`.

Короткая форма `key = value` не несёт `default`:

```
{"op":"set","key":"gold","value":0,"default":true}
→ .lvns:  gold = 0
→ back:   {"op":"set","key":"gold","expr":"0"}      ← default потерян, value→expr
```

(Ключ с точкой уходит в `genericOp` и флаг сохраняет — проверено.)

**Живое влияние сейчас нулевое**: все 10943 `set default=*` в отгруженном
контенте имеют namespaced ключи (`Wardrobe.*`, `Temp.*`, `Remember.*`). Но
`buildVarsDeclaration` оставляет инлайновыми именно **конфликтующие**
объявления, и любой проект с ненеймспейсными переменными (или ручная правка)
получит `set default=true gold=0` → `gold = 0`, т.е. **безусловное присваивание
при каждом входе в главу вместо объявления дефолта** → прогресс игрока
сбрасывается.

**Фикс:** уходить в `genericOp`, если у `set` есть любое поле кроме
`op/key/value/expr` (в первую очередь `default`).

---

## O16 [MEDIUM] Переименование синтетических меток при re-save ломает якорь сейвов

**Файлы:** `decompile.go:72-84` (стрелочный `if` без `else`-метки) +
`convert.go` (`nf` — счётчик `__nfN`); `unity/Packages/com.lvn.engine/Runtime/LvnPlayer.cs:507-541`
(`AnchorOf`/`Relocate`).

Дифф по реальным главам: `label id "n37_000000" → "__nf1"` **n=837**,
`if else "n38_000000" → "__nf1"` **n=779**. Коммит называет это «синтетические
fallthrough-метки — безобидно», но `LvnPlayer` якорит позицию сейва на
**id ближайшей предшествующей метки** + смещение, и `Relocate` при пропавшей
метке «falls back to the raw index». Одновременно меняется и число опов
(`label:+95`, `goto:+88` на ch24) → сырой индекс тоже неверен.

**Сценарий отказа.** Автор пересохраняет главу из панели; у игроков, чей сейв
стоял под переименованной меткой, при возобновлении курсор уезжает
(`Relocate` → fallback на сдвинутый индекс). Не крэш — «продолжить» открывает
не ту сцену.

**Фикс:** генерировать `else`-метку с детерминированным именем,
производным от исходной (`n38_000000`), а не из глобального счётчика —
тогда re-save не переименовывает ничего. Плюс имеет смысл проверить, что
`__nfN` не может совпасть с существующей меткой скрипта.

---

## O17 [MEDIUM] Атрибуты уровня `choice` выбрасываются

**Файл:** `tools/lvnconv/importer/decompile.go:129-134`.

`case "choice"` печатает только опции; всё, что стоит на самом опе (`timeout`,
`timeout_goto`, и любое будущее поле), теряется. Подтверждено тестом и живыми
данными (`tour-ch02.lvn`: `LOST choice timeout` ×3, `timeout_goto` ×3).
Парсер обратную форму умеет (`pendingChoice` для `choice timeout=…`), т.е.
это чистая дырка в декомпиляторе.

**Фикс:** эмитить `choice k=v …` перед блоком опций, когда у опа есть поля
кроме `options`.

---

## O18 [MEDIUM] Суффиксный fallback теперь красит чужих персонажей именами из Cold

**Файлы:** `tools/lvnconv/importer/importer.go:357, 499`
(`applySpeakerNameOverridesToSprites` — **новые** вызовы в `Run`/`runMultiChapter`),
`bundle_wire.go:565-575` (`displayNameFor`).

`displayNameFor` откусывает `_`-суффиксы, а `applySpeakerNameOverridesToSprites`
применяет его к **id сущности**, который есть `Slug(who)` — то есть
многословное имя со пробелами превращённое в подчёркивания. Подтверждено:

```go
tpl.SpeakerNames = map[string]string{"Ivan": "Иван"}   // ровно так в прод-cold.json
sprites := map[string]any{"Ivan_Petrov": map[string]any{"name":"Ivan_Petrov"}}
applySpeakerNameOverridesToSprites(sprites, tpl)
// name = "Иван"     ← персонаж «Ivan Petrov» подписан «Иван»
```

До 3449daf это происходило только в bundle-режиме; теперь — в любом импорте,
включая `/v1/admin/import-articy`. А дефолтный шаблон на проде — это `cold.json`
с 56 именами (`Ivan`, `Man`, `Cat`, `Mother`, `Anna`, `Alex`, …), поэтому чужая
новелла, импортированная «по умолчанию», получает подписи из Cold.

**Фикс:** ограничить суффиксный fallback известным списком вариантных
суффиксов (`_black`, `_dead`, `_flashback`, … — то, для чего он и делался),
либо не применять его к id сущностей вообще (только к `who` реплик), либо
требовать, чтобы отрезаемый суффикс не был похож на фамилию (эвристика слабая —
лучше первое).

Связанное (известно родительскому аудиту, повторю с новым аргументом):
`MergeSpritesIntoManifest` (`apply.go:115-138`) сливает сущности в **глобальный**
`manifest.sprites` по id без пространства имён новеллы. 3449daf расширяет
поверхность коллизии: `ensureProtagonistCast` + `SpeakerAliases` теперь стейджат
протагониста в проектах, где раньше её не было, и все они — под одним и тем же
`Slug("Главный герой")` = `Главный_герой`, потому что это дефолтная
`protagonist_roles`. Это и есть механизм инцидента «Mechlove перезаписал Cold».
Продуктовая защита: префикс id новеллой либо отказ мержа при коллизии с
сущностью, на которую ссылается другой title (включая `title.hero`).

---

## O19 [LOW-MEDIUM] Простые ключи с именем директивы ломают round-trip по-разному

**Файл:** `decompile.go:94-99` (короткая форма `key = value`) против
префиксных директив в `convert.go`.

Прогон по `{"op":"set","key":<K>,"value":0}` + следующая реплика:

| K | `.lvns` | результат |
|---|---------|-----------|
| `def` | `def = 0` | **ошибка** `def: "=" is not a valid preset name` |
| `call` | `call = 0` | **ошибка** `call needs exactly one label` |
| `choice` | `choice = 0` | **ошибка** `choice: invalid key name ""` |
| `scene` | `scene = 0` | **оп молча исчез**, `doc.Scene = "= 0"` |
| `actor_map` | `actor_map = 0` | **оп молча исчез** |
| `return` | `return = 0` | `set __ret = "= 0"` + **вставлен оп `return`** — управление уходит из главы |
| `if` | `if = 0` | превратился в **экранную реплику** «if = 0» |
| `voice` | `voice = 0` | превратился в **экранную реплику** «voice = 0» |
| `for`/`while`/`func` | ок | ок |

Живые жертвы: `rpg-inv.lvn`/`goblin-battle.lvn` (переменная `def` = защита) —
оба **не компилируются** ни из `.lvn` через `ToLvns`, ни (для rpg-inv) из
отгруженного `.lvns`. Для articy-импорта сейчас недостижимо (переменные
namespaced), поэтому LOW-MEDIUM, но `return`-инъекция — самый неприятный
класс молчаливой порчи, какой тут возможен.

**Фикс:** в `decompile.go` расширить условие короткой формы: `simpleKeyRe` **и**
`!isDirectiveWord(key)`. Плюс в `convert.go` требовать, чтобы после `def ` шёл
валидный идентификатор, а иначе не считать строку presetom.

---

## O20 [LOW-MEDIUM] Шаблон с именем `default` сохраняется, но никогда не применяется

**Файлы:** `server/import_templates.go:73` (`isDefault := name == "cold" || name == "default"`),
`tools/lvnconv/importer/template.go:415-439` (`ResolveTemplate`).

`ResolveTemplate` первым делом делает `if s == "" || s == "default" { s = "cold" }`,
то есть **никогда** не ищет `default.json`. А PUT `/v1/admin/import-templates/default`
успешно пишет `import-templates/default.json` и рапортует `{"saved":true}`.
Автор сохраняет шаблон и получает подтверждение, а импорт продолжает
использовать `cold.json`. Файл при этом попадёт в список
`handleImportTemplates` и будет выбираем в панели — где выбор `default`
даст… `cold`.

**Фикс:** отклонять PUT/имя `default` (409/400 «зарезервировано, используйте
cold»), либо научить `ResolveTemplate` смотреть `default.json` до подмены на
`cold`.

---

## O21 [LOW] Оп `text` декомпилируется в форму, несовместимую с его же парсером

**Файлы:** `decompile.go:135-137` (`genericOp`) против
`convert.go:465-506` (позиционный синтаксис `text <id> [k=v…] «tmpl»`).

```
{"op":"text","id":"code","text":"Just a plain line.","x":3,"y":12.5,"color":"#9fe8a8","size":50}
→ .lvns: text color="#9fe8a8" id="code" size=50 text="Just a plain line." x=3 y=12.5
→ back:  {"op":"say","text":"text color=\"#9fe8a8\" id=\"code\" size=50 …"}   ← команда стала РЕПЛИКОЙ на экране

{"op":"text","id":"code","text":"Ash: Sparks in my pocket: {{sparks}}."}
→ back:  {"op":"text","id":"id=\"code\"","text":"Sparks in my pocket: {{sparks}}.\""}  ← значения съехали
```

46 + 32 + 28 расхождений в tour/waylight-главах. Практически недостижимо
(articy-импортёр `text`-опы не эмитит, а `.lvns` этих глав — рукописный
источник, ToLvns по ним не гоняется), но это ровно тот же класс, что O5:
`genericOp` не знает про позиционные грамматики. Фикс — вместе с O5.

---

## O22 [LOW] `wallet_cost` с многословной валютой остаётся строкой

**Файлы:** `decompile.go:285-293`, `convert.go:1127-1134` + `reWalletCost`.

`ToLvns` пишет `wallet_cost="<amount> <currency>"`, а
`reWalletCost = ^([0-9]+(?:\.[0-9]+)?)\s+(\S+)$` требует валюту **без пробелов**:

```
{"currency":"soft coins","amount":5} → wallet_cost="5 soft coins" → остаётся СТРОКОЙ "5 soft coins"
{"amount":"20"} (строка)             → wallet_cost="\"20\" crystals" → остаётся строкой
```

`Premium.Currency` — редактируемое поле шаблона, так что «soft coins» вполне
возможно. Раз `LvnPlayer.Choose` ожидает `{amount,currency}`, премиум-выбор
опять становится бесплатным — то есть ровно та бага, которую 3f6dff6 закрывал,
возвращается для валюты с пробелом. `amount=0` при этом парсится, но 0 — не цена.

**Фикс:** сериализовать структурно (`wallet_cost=<currency>:<amount>`, как уже
делает `cost`), а не в свободную строку; и не терять поле молча — если regex не
сматчился, лучше упасть/варнить, чем оставить строку.

---

## O23 [LOW] Метка `effects` с запятой обрезается

**Файлы:** `decompile.go:301-326` (join через `,`), `convert.go:1136-1155`
(split по `,`).

```
[{"label":"Иван, брат","delta":1}] → effects="Иван, брат:+1" → [{"label":"брат","delta":1}]
[{"label":"X","delta":1.5}]        → effects="X:+1"          → delta округлён вниз
[{"label":"X","delta":0}]          → поле выброшено целиком
```
Косметическая подсказка выбора, поэтому LOW. **Фикс:** экранировать разделитель
или сериализовать как JSON-массив в кавычках.

---

## O24 [LOW] Имя спикера, начинающееся с `-`, роняет весь recompile

**Файл:** `decompile.go:218-224` — терсная форма исключает из `who` только
`:` и `"`.

```
{"op":"say","who":"-Ghost","text":"hi"} → .lvns: -Ghost: hi
→ line 2: choice option must have a target label (use '-> label')   ← вся глава не компилируется
```
То же для нарративной строки, начинающейся с `-` (в русской прозе диалог через
тире — обычное дело, хотя тире `—`, а не `-`). Проверено по всему отгруженному
контенту: **0 живых случаев** (`-`/`->`/`:`/`#` в начале терсной строки), поэтому LOW.

**Фикс:** уходить в generic-форму, если `who` или (при пустом `who`) текст
начинается с `-`, `:`, `#`, `->`, или совпадает с директивным словом.

---

# Проверено, НЕ баг

- **Утечка `sentinelNoArt` («\x00noart»)** — гипотеза 1. Прошёл все чтения
  `cast`: значение живёт исключительно в локальной map. `AutoStage:228`
  (`spr != "" && spr != sentinelNoArt`) не даёт ему попасть в `sprite_url`;
  `detect.go:197` и `:273` фильтруют его в `has_art`/`artNames`; `collectArt`
  и `BuildCatalog` читают `doc.Script`, а не cast; `applySpeakerAliasesToCast`
  выполняется **до** `ensureProtagonistCast` в обоих путях, поэтому alias не
  может скопировать сентинел. В JSON/detect-ответ не попадает.
- **regexp из недоверенного JSON (гипотеза 3).** Go RE2 — без бэктрекинга;
  `sceneMarkerMatch` корректно проверяет `len(m) < 2`, паттерн без
  capture-группы (`^Сцена`) даёт `ok=false`, без паники; пустой паттерн
  подменяется дефолтом в `compile()`. Единственная реальная проблема — не
  падение, а слишком широкий паттерн (см. O4).
- **JS-генератор regex → RE2.** `buildSceneMarkerRegex` строит только
  `^ \s* \d+ (.+?) \.? $` + экранированные литералы; lookahead/backref/
  named groups не порождает. Экранирование выполняется до генерализации
  `\d+`/`\s+`, порядок безопасен. Двойное вхождение location обрабатывается
  корректно (`indexOf` берёт первое, остаток уходит в литеральный хвост —
  проверено на `"Сцена 5. Кухня. Кухня пуста."`). Невалидных для RE2
  конструкций нет.
- **Гонка Template CRUD GET vs PUT.** `atomicWrite` = temp + `os.Rename` в той
  же директории → GET читает либо целиком старую, либо целиком новую версию.
  Полузаписанного файла не бывает. (DELETE — см. O14, там проблема не в гонке.)
- **GET `/import-templates/cold` без файла на диске.** `json.MarshalIndent(
  importer.DefaultTemplate())` сериализует все публичные поля с их тегами
  (включая `staging.narrator_roles`); приватные `sceneMarker`/`*Set`
  игнорируются `encoding/json` без ошибки. Round-trip GET→PUT→ParseTemplateJSON
  чистый.
- **`validID` / path traversal в имени шаблона.** `^[A-Za-z0-9_-]+$` отсекает
  `..`, `/`, точки; `historyEligible` дополнительно фильтрует. Утечки из
  `content/import-templates` нет.
- **`actor.mirror` строка→bool на round-trip** (7828 расхождений). Прочитал
  рантайм: `VnStage.Actors.cs:495,532` → `BoolOr(cmd["flip"] ?? cmd["mirror"])`,
  а `LvnPlayer.cs:1179-1184` `BoolOr` делает `t.Value<bool>()` в try/catch —
  Newtonsoft конвертирует `"true"` → `true`. Реального влияния нет
  (заявление коммита о «безобидности» подтверждаю).
- **Потеря `say.emotion` на round-trip** (7668 расхождений). Грепнул весь
  `unity/Packages/com.lvn.engine/Runtime` — поле `emotion` читается **только**
  на `actor`-опах, у `say` не читается нигде. Эмоция едет на актёре, который
  round-trip'ится. Влияния на рантайм нет.
- **Двойное применение `applyProtagonistSpeakerRename`.** Идемпотентно:
  `isProtagSpeaker("{player}")` ложно (шаблон в `protagonist_speaker_labels`
  не входит), плюс `applySpeakerNameOverrides` пропускает `who`, начинающийся
  с `{`. Проблема только у `SpeakerNames`-цепочек (O10).
- **`who_id` для `{player}`-спикера через `actor_map`.** Гипотеза 8 —
  round-trip чистый, проверено:
  `actor_map {player}=Главный_герой` + `{player}: Привет` →
  `{"op":"say","who":"{player}","who_id":"Главный_герой","text":"Привет"}`.
  Фигурные скобки директиву не ломают (`reDialogue` исключает `=`, а
  `actor_map`-ветка идёт по префиксу до диалоговой).
- **Инлайновое переобъявление `actor_map` при смене id у одного имени.**
  Работает точно: `Мужчина`→`Man_black`/`Man_black_2`/`Man_black` даёт
  ровно `[Man_black, Man_black_2, Man_black]`. Парсер обрабатывает строки
  последовательно, физический порядок совпадает с порядком массива опов.
- **Имя спикера с `:`** (`"Dr: X"`). `sayLine` уходит в generic-форму
  (`say who="Dr: X" who_id="drx" …`), а `actor_map Dr: X=drx` парсится
  корректно (`SplitN` по `=`). Round-trip чистый.
- **Имя спикера, совпадающее с KnownOps** (`say`, `actor`, `bg`, `wait`,
  `hint`). Round-trip чистый: `say: hello world` не проходит
  `looksLikeCommand` и корректно попадает в диалоговую ветку.
- **`-vars.json` в `regenerateLvnsSidecars`.** Не ловится ни `.lvn`-, ни
  `.lvns`-суффиксом — пропускается штатно.
- **Локализованный bundle-импорт → пустые тексты в сайдкаре.** Механизм
  реален (`regenerateLvnsSidecars` декомпилирует пост-`Localize` скрипт, где
  вместо `text` стоит `text_id`; проверено — получается
  `say who="Аня" text=""`), но **недостижим**: `Localize` не выставляется ни в
  `server/import_bundle.go:82,204`, ни где-либо ещё на пути `RunBundle`.
  Оставляю как мину: если локализацию когда-нибудь включат для bundle,
  сайдкары обнулят все реплики. Стоит поставить guard
  (`if opt.Localize { skip regenerate + warn }`).
- **`emotion_colors` регистр ключей.** `ColorStat.Hex` = `"#" + normHex(...)`
  (lowercase), панель срезает `#`, `emotionTable` прогоняет через `normHex` —
  регистр и `#` нормализуются на всех стыках. Мисматча нет.
- **`ColorStat` без json-тегов** → в JSON уезжает `{"Hex","Count","Emotion",
  "Sample"}`, и панель читает ровно `c.Hex`/`c.Count`/`c.Emotion`. Совпадает.
- **`detectAliasCollisions` производительность/симметрия.** O(speakers×artNames),
  на реальных объёмах несущественно. `initials("Главный герой")=="ГГ"` работает
  (для `looseContains` порог ≥3 руны — «ГГ» через него не прошло бы).
  Предложение в обратную сторону (у спикера с артом предлагается алиас на
  спикера без арта) безвредно: `applySpeakerAliasesToCast` пропускает alias,
  у которого уже есть свой cast-вход.
- **`AdvancedJsonPanel` / `useJsonDoc`.** Пока автор не трогал JSON, `text ==
  null` и панель зеркалит актуальный `draft`. Только если он отредактировал
  JSON, потом правил таблицу и потом нажал «Применить», правки таблицы
  затрутся — это last-write-wins с видимым индикатором `dirty`, не баг.

---

# Итоговое резюме

Оба коммита делают правильные вещи, но у них общая слабость: **изменения
проверены на «золотом» контенте (Cold) и не проверены на том, что уже
задеплоено, и на контенте, который не Cold.**

1. **Самая дорогая находка — не в коде, а в его неприменённости (O1).**
   3f6dff6 починил генератор `.lvns`, но 82 отгруженных сайдкара остались
   «до фикса», а панель предпочитает именно их. То есть заявленная в коммите
   потеря данных при «Save to app» **сегодня жива на проде в полном объёме**:
   я измерил −330 audio-опов, −25 `wardrobe_show`, −25 актёров-героинь,
   −275 `wallet_cost` (премиум-выборы бесплатны), −246 `outfit`, −143 `mirror`
   на 25 главах Cold. Нужна одноразовая регенерация + гейт в панели по
   свежести сайдкара. Это приоритет №1.

2. **Round-trip «безопасен» только для Cold (O2, O3, O5, O12, O19).** Из 83
   отгруженных `.lvn` **8 не компилируются** после декомпиляции, а `soviet`
   (реальная импортированная новелла) — самый тяжёлый случай: либо hard fail
   (`unclosed «…»`), либо молчаливое склеивание 3 реплик. Плюс необратимо
   теряются тела опций choice (ломается механика «спросить один раз»),
   атрибуты `choice`, обёртки `«…»`, флаг `default` у простых ключей.
   Общий корень — `genericOp`, который печатает любой оп в форме `op k=v`,
   выбрасывая структурные поля и не спрашивая, знает ли парсер этот оп и его
   грамматику. Фикс `wardrobe_show` → `KnownOps` лечил один симптом.
   **Системный фикс:** после `ToLvns` всегда прогонять `lvns.Convert` и
   сравнивать оп-стримы, а расхождения класть в `res.Warnings`. Один такой
   guard поймал бы 8 из 24 находок этого отчёта до деплоя.

3. **Маппер даёт автору три способа тихо испортить импорт (O4, O8, O9).**
   Конструктор regex может выдать паттерн «всё подряд» и показать при этом
   100% совпадения (импорт удалит всю нарративку); роль «Рассказчик» —
   ловушка, из которой в UI нет выхода; изменение любой роли на частичном
   шаблоне затирает дефолтные narrator-роли. Плюс модалка падает на
   `speakers: null` (O7) и предупреждает о проблемах, которых не будет (O11,
   не видит xlsx). Экран задуман как «автор не должен знать regex» — а
   опасные состояния он не отсекает.

4. **Серверная часть.** `stage-extract` течёт и держится на недокументированной
   связке `TMPDIR` ↔ `-import-root` (O6); DELETE шаблона проходит мимо
   заявленной истории правок (O14); шаблон с именем `default` сохраняется, но
   никогда не читается (O20).

5. **Гипотезы, которые НЕ подтвердились** (детали в разделе выше): утечка
   `sentinelNoArt`, паника/бэктрекинг на пользовательском regex, невалидный
   для RE2 вывод JS-генератора, гонка GET vs PUT шаблонов, потеря полей при
   сериализации `DefaultTemplate()`, path traversal в имени шаблона,
   round-trip `{player}`/`who_id` через `actor_map`, инлайновое
   переобъявление `actor_map`, имена спикеров с `:` и совпадающие с KnownOps,
   рантайм-влияние `mirror` строка→bool и потери `say.emotion`.
   Идемпотентность `{player}`-рескина подтвердилась; идемпотентность
   `SpeakerNames` — нет (O10), но живой `cold.json` цепочек не содержит.

**Рекомендуемый порядок работ:** O1 → O2 → (guard «ToLvns→Convert→diff» как
общий предохранитель) → O4/O7/O8 → O3/O5 → O6 → остальное.
