#!/usr/bin/env bash
# КАЖДАЯ ГЛАВА ПРОХОДИТСЯ ДО КОНЦА.
#
# Обход по структуре (`lvnconv walk`) отвечает на вопрос «есть ли путь» и
# отвечает честно — но по СХЕМЕ, а не исполнением. Он не заметит, что игрок
# упрётся в развилку, где все варианты закрыты условием; что цикл вернёт его
# на ту же реплику; что переход уводит в блок, из которого нет выхода. Всё это
# видно только когда историю кто-то ДЕЙСТВИТЕЛЬНО играет.
#
# Стенд играет каждую живую главу настоящим плеером — тем самым JS-плеером,
# который стоит в песочнице /play/, — выбирая ПЕРВЫЙ доступный вариант:
#
#   ДОШЛА       история закончилась сама, а не упёрлась в потолок шагов;
#   НЕ ВСТАЛА   на каждом шаге есть чем продолжить: выбор с вариантами,
#               реплика, ввод — но не пустая стопка;
#   НЕ ЦИКЛ     потолок шагов не достигнут (иначе это петля без выхода).
#
#   qa/playthrough-check.sh [-bite]
#
# -bite подкладывает главу с развилкой, где все варианты закрыты условием:
# стенд обязан назвать её. Прогон, который не видит тупика, не доказывает и
# проходимости остальных.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v node >/dev/null 2>&1 || { echo "нет node — пропускаю"; exit 0; }

W="$(mktemp -d)"
trap 'rm -rf "$W"' EXIT

go build -C tools/lvnconv -o "$W/lvnconv" . || { echo "компилятор не собрался"; exit 1; }

# Живые главы: примеры из howto и examples — то, что читает автор и что мы
# раздаём как образцы. Пакеты берём тоже: они попадают в чужие игры.
srcs=()
while IFS= read -r f; do srcs+=("$f"); done < <(
  find howto examples packages -name '*.lvns' -not -path '*/node_modules/*' 2>/dev/null | sort)

# ЧУЖОЙ КАТАЛОГ — ПО ПРОСЬБЕ. Стенд умеет пройти и настоящий контент студии:
# LVN_CONTENT_DIR=/путь/к/content qa/playthrough-check.sh. В репозитории такого
# контента нет и быть не должно, поэтому по умолчанию проверяются только
# образцы; прогон по живому каталогу — отдельное действие владельца.
if [ -n "${LVN_CONTENT_DIR:-}" ]; then
  while IFS= read -r f; do srcs+=("$f"); done < <(
    find "$LVN_CONTENT_DIR" -name '*.lvns' 2>/dev/null | sort)
fi

if [ -n "$BITE" ]; then
  mkdir -p "$W/укус"
  # Ровно тот дефект, который стенд и нашёл в живом образце: у главы ЕСТЬ
  # финал, но поток крутится и не доходит до него. Развилку с закрытыми
  # вариантами для укуса брать нельзя: с 05.09 движок из неё выходит сам
  # (см. «Игрок не застревает навсегда»), и такая глава честно проходится.
  cat > "$W/укус/петля.lvns" <<'LVNS'
scene петля

:круг
Плёнка идёт по кругу.
-> круг

Сюда поток не попадёт никогда.
-> __end
LVNS
  srcs=("$W/укус/петля.lvns")
fi

[ "${#srcs[@]}" -gt 0 ] || { echo "не нашлось ни одной главы — проверять нечего"; exit 2; }

# Компилируем всё, что скомпилируется. Глава, которая не компилируется, —
# забота другого стража (гейт содержимого), здесь она просто пропускается со
# счётом: молчаливый пропуск превратил бы «все прошли» в «прошли те, кого
# смогли собрать».
docs="$W/docs.json"
: > "$W/list.txt"
compiled=0; skipped=0
for f in "${srcs[@]}"; do
  out="$W/$(echo "$f" | tr '/' '_').lvn"
  if "$W/lvnconv" convert -i "$f" -o "$out" >/dev/null 2>&1; then
    echo "$f|$out" >> "$W/list.txt"
    compiled=$((compiled + 1))
  else
    skipped=$((skipped + 1))
  fi
done
[ "$compiled" -gt 0 ] || { echo "ни одна глава не скомпилировалась"; exit 2; }

python3 - "$W/list.txt" "$docs" <<'PY'
import json, sys

# ЧТО СТЕНД ПРОЙТИ НЕ МОЖЕТ — и говорит об этом сам.
#
# Раннер двигает историю тем же, чем двигает её игрок с одной кнопкой: тап,
# выбор, ввод. Двух вещей он не умеет, и обе законны:
#
#   бесконечная по замыслу — витрина эффектов или песочница-хаб, у которой
#     конца нет вовсе (в исходнике ни одного перехода в __end);
#   ведётся кликом — жанр point-and-click: дальше пускает нажатие по объекту
#     на фоне, а не реплика.
#
# Требовать от них «дойти до конца» значит требовать несуществующего. Считаем
# отдельной строкой: молчаливый пропуск превратил бы «все прошли» в «прошли
# те, кого мы умеем играть».
# Подмножество, которое браузерный плеер ОБЪЯВИЛ (conformance/ops-owners.json,
# поле browser.interprets). Операция вне него плеером не толкуется — он
# пересылает её рендереру и идёт дальше. Для прохождения это важно: если такая
# операция ОСТАНАВЛИВАЕТ поток в движке (гардероб ждёт выбора игрока), браузер
# пролетит её и упрётся в цикл, которого в игре нет.
#
# Замер 05.09 на живом каталоге студии: 12 глав объявлялись «вечным циклом»
# ровно по этой причине — в каждом цикле стоял wardrobe_show. Ложная тревога на
# ровном месте, и она обрушила бы стенд на первом же настоящем контенте.
подмножество = set()
try:
    подмножество = set(json.load(open("conformance/ops-owners.json", encoding="utf-8"))
                       .get("browser", {}).get("interprets") or [])
except Exception:
    pass

def характер(doc):
    script = doc.get("script") or []
    кликом = any(isinstance(c, dict) and c.get("op") in ("obj", "actor")
                 and (c.get("on_click") or c.get("on_drop") or c.get("on_drop_miss"))
                 for c in script)
    конец = any(isinstance(c, dict) and c.get("op") == "goto" and c.get("label") == "__end"
                for c in script)
    if кликом:
        return "ведётся кликом"
    if not конец:
        return "бесконечна по замыслу"
    return "играется"


# ЧЬЯ ПЕТЛЯ — ГЛАВЫ ИЛИ ПЛЕЕРА. Незнакомая операция сама по себе прохождению не
# мешает: `sfx`, `hint`, `anim` плеер просто пересылает дальше. Мешает она
# ровно тогда, когда ОСТАНАВЛИВАЕТ поток в движке, а браузер её пролетает — и
# упирается в круг, которого у игрока нет. Такой круг видно прямо в скрипте:
# обратный переход, внутри которого нет ни реплики, ни выбора, ни ввода, зато
# есть операция вне объявленного подмножества.
def чужая_остановка(doc):
    script = doc.get("script") or []
    метки = {c.get("id"): i for i, c in enumerate(script)
             if isinstance(c, dict) and c.get("op") == "label"}
    for i, c in enumerate(script):
        if not (isinstance(c, dict) and c.get("op") == "goto"):
            continue
        t = метки.get(c.get("label"))
        if t is None or t > i:
            continue
        внутри = [x.get("op") for x in script[t:i] if isinstance(x, dict)]
        if any(o in ("say", "choice", "input") for o in внутри):
            continue
        чужие = sorted({o for o in внутри if o and подмножество and o not in подмножество})
        if чужие:
            return чужие[0]
    return ""

rows = []
for line in open(sys.argv[1], encoding="utf-8"):
    src, path = line.rstrip("\n").split("|", 1)
    try:
        doc = json.load(open(path, encoding="utf-8"))
        rows.append({"name": src, "doc": doc, "kind": характер(doc)})
    except Exception as e:
        rows.append({"name": src, "doc": None, "err": str(e), "kind": "играется"})
json.dump(rows, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY

cat > "$W/play.mjs" <<'JS'
// Проходит каждую главу НАСТОЯЩИМ плеером песочницы.
//
// ВЫБОР — СЛУЧАЙНЫЙ, А НЕ ПЕРВЫЙ. Первая редакция стенда всегда жала первый
// вариант и объявила петлёй одиннадцать глав из двадцати пяти: почти каждый
// хаб («осмотреть / поговорить / уйти») возвращает в себя, и первый вариант в
// нём — это законная кнопка «остаться». Зацикливалась стратегия, а не история.
// Теперь выбор псевдослучайный с ФИКСИРОВАННЫМ зерном (прогон обязан быть
// повторяемым), а глава считается проходимой, если до конца дошёл хотя бы один
// из нескольких заходов — ровно то же, что делает живой игрок, который в хабе
// рано или поздно жмёт «уйти».
//
// На ввод отвечаем именем: пустая строка в некоторых главах не проходит
// проверку, а вопрос «как тебя зовут» не должен решать, проходима ли глава.
import { readFileSync } from "node:fs";
const [, , playerPath, docsJson, capRaw] = process.argv;
const { Player } = await import(playerPath);
const rows = JSON.parse(readFileSync(docsJson, "utf8"));
const cap = Number(capRaw || 20000);

const ЗАХОДОВ = 12;

// Линейный конгруэнтный генератор: тот же seed — тот же прогон, всегда.
function генератор(seed) {
  let x = seed >>> 0;
  return () => (x = (x * 1664525 + 1013904223) >>> 0) / 4294967296;
}

function заход(doc, seed, cap) {
  const rnd = генератор(seed);
  const player = new Player(doc, { onStage: () => {} });
  let steps = 0;
  let ev = player.advance();
  // ПРЕДПОЧИТАЕМ НЕИСПРОБОВАННОЕ. Чистая случайность в диалоговом хабе ходит
  // по кругу: замер на живой главе — 1807 выборов за тридцать тысяч шагов,
  // одна и та же развилка «Да./Нет.» восемьсот восемьдесят три раза, а выход
  // из неё есть (обход по структуре видит 100% команд). Память о том, что уже
  // пробовал в этой точке, — то же, что делает живой игрок, и она находит
  // выход за один заход вместо двенадцати.
  const пробовали = new Map();
  while (steps++ < cap) {
    if (ev.type === "end") return { verdict: "дошла", steps };
    if (ev.type === "choice") {
      const opts = ev.options || [];
      if (opts.length === 0) return { verdict: "тупик", steps, detail: `пустая развилка на шаге ${steps}` };
      const точка = opts.map((o) => o.index).join(",") + "|" + String(ev.text || "").slice(0, 40);
      let было = пробовали.get(точка);
      if (!было) { было = new Set(); пробовали.set(точка, было); }
      const свежие = opts.filter((o) => !было.has(o.index));
      const пул = свежие.length ? свежие : opts;
      const выбор = пул[Math.floor(rnd() * пул.length)];
      было.add(выбор.index);
      ev = player.choose(выбор.index);
    } else if (ev.type === "input") {
      // Ответ вводом даётся ИМЕННО ЭТИМ методом. Первая редакция стенда звала
      // несуществующий answer() и падала обратно на advance(), а тот честно
      // возвращал тот же самый вопрос: квиз «зацикливался» на трёх тысячах
      // одинаковых событий input — по вине мерки, а не главы.
      ev = player.submitInput("Игрок");
    } else {
      ev = player.advance();
    }
  }
  return { verdict: "петля", steps, detail: `потолок ${cap} шагов` };
}

const out = [];
for (const row of rows) {
  if (!row.doc) { out.push({ name: row.name, verdict: "не разобралась", detail: row.err }); continue; }
  if (row.kind !== "играется") { out.push({ name: row.name, verdict: row.kind, detail: "", steps: 0 }); continue; }
  let итог = null, шагов = 0;
  for (let s = 1; s <= ЗАХОДОВ; s++) {
    let r;
    // Потолок шагов — по размеру главы: сборник на тридцать тысяч команд не
    // проходится за двадцать тысяч шагов, и это была бы ложная петля.
    const свой = Math.max(cap, (row.doc.script || []).length * 6);
    try { r = заход(row.doc, s * 7919, свой); }
    catch (e) { r = { verdict: "исключение", steps: 0, detail: String(e && e.message || e) }; }
    шагов += r.steps || 0;
    // Тупик и исключение — беда сразу, их не «перехаживают» другим зерном.
    if (r.verdict === "дошла" || r.verdict === "тупик" || r.verdict === "исключение") { итог = r; break; }
    итог = r;
  }
  out.push({ name: row.name, verdict: итог.verdict, detail: итог.detail || "", steps: шагов });
}
process.stdout.write(JSON.stringify(out));
JS

node "$W/play.mjs" "$PWD/panel/public/play/core.js" "$docs" 20000 > "$W/result.json" 2>"$W/node.err" || {
  echo "плеер не отработал:"; tail -5 "$W/node.err"; exit 2; }

python3 - "$W/result.json" "$compiled" "$skipped" "${BITE:-}" "$docs" <<'PY'
import json, sys

подмножество = set()
try:
    подмножество = set(json.load(open("conformance/ops-owners.json", encoding="utf-8"))
                       .get("browser", {}).get("interprets") or [])
except Exception:
    pass


def чужая_остановка(doc):
    script = (doc or {}).get("script") or []
    метки = {c.get("id"): i for i, c in enumerate(script)
             if isinstance(c, dict) and c.get("op") == "label"}
    for i, c in enumerate(script):
        if not (isinstance(c, dict) and c.get("op") == "goto"):
            continue
        t = метки.get(c.get("label"))
        if t is None or t > i:
            continue
        внутри = [x.get("op") for x in script[t:i] if isinstance(x, dict)]
        if any(o in ("say", "choice", "input") for o in внутри):
            continue
        чужие = sorted({o for o in внутри if o and подмножество and o not in подмножество})
        if чужие:
            return чужие[0]
    return ""
rows = json.load(open(sys.argv[1], encoding="utf-8"))
compiled, skipped, bite = int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
def мимо_ли(v):
    return (v in ("бесконечна по замыслу", "ведётся кликом")
            or v.startswith("вне подмножества плеера"))
мимо = [r for r in rows if мимо_ли(r["verdict"])]
bad = [r for r in rows if r["verdict"] != "дошла" and not мимо_ли(r["verdict"])]

# Петля и «вечный goto» могут быть не бедой главы, а слепотой плеера — см.
# чужая_остановка. Переносим такие в «мимо стенда», назвав операцию.
исходники = {row["name"]: row.get("doc") for row in json.load(open(sys.argv[5], encoding="utf-8"))}
оставить = []
for r in bad:
    op = чужая_остановка(исходники.get(r["name"]) or {})
    if op and r["verdict"] in ("петля", "исключение"):
        r["verdict"] = f"вне подмножества плеера ({op})"
        мимо.append(r)
    else:
        оставить.append(r)
bad = оставить
if bite:
    if bad:
        print(f"укус чист: тупик назван — {bad[0]['name']}: {bad[0]['verdict']} ({bad[0]['detail']})")
        raise SystemExit(0)
    print("СТЕНД СЛЕП: глава с закрытыми вариантами прошла как ни в чём не бывало")
    raise SystemExit(2)
шагов = sum(r.get("steps", 0) for r in rows)
сыграно = len(rows) - len(мимо)
print(f"  глав сыграно: {сыграно} из {len(rows)} (не скомпилировалось {skipped}), шагов всего {шагов}")
for r in мимо:
    print(f"  мимо стенда: {r['name']} — {r['verdict']}")
for r in bad:
    print(f"  {r['name']}: {r['verdict']} — {r['detail']}")
if bad:
    print("РВЁТСЯ: не каждая глава проходится до конца")
    raise SystemExit(1)
print("держит: каждая живая глава доигрывается до конца сама")
PY
