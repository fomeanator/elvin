#!/usr/bin/env bash
# ПЕРЕВОДЫ НЕ ТЕРЯЮТСЯ, КОГДА АВТОР ПРАВИТ ГЛАВУ.
#
# Каталог строится в стиле gettext: КЛЮЧ — сама исходная строка, значение —
# перевод. Инструмент запускают снова после каждой правки текста, и цена ошибки
# здесь считается не в минутах, а в неделях чужой работы: потерянный перевод
# восстанавливать неоткуда.
#
# Обещание из справки дословно: «Existing translations are kept, new lines are
# prefilled with the source text; -check only reports coverage (exit 1 on
# missing keys), -prune drops stale keys».
#
# Проверяются три разные вещи, и третья — граница, а не дефект:
#
#   ХРАНИТ    правка соседней реплики не трогает уже переведённые;
#   ЧИСТИТ    -prune убирает ключи исчезнувших строк и не трогает живые;
#   ТЕРЯЕТ    правка САМОЙ переведённой строки меняет ключ, и перевод
#             осиротеет. Это неизбежно при ключе-исходнике, но автор обязан
#             знать цену: исправил опечатку в реплике — перевод к ней пропал.
#
# ОСОБО: -check сообщает про НЕПЕРЕВЕДЁННЫЕ, но падает только на ОТСУТСТВУЮЩИХ.
# Свежий каталог, где всё «переведено» в самого себя, проходит проверку. Иначе
# и быть не может — отличить перевод от копии инструмент не в силах, — но гейт
# на -check охраняет полноту КЛЮЧЕЙ, а не наличие перевода.
#
#   qa/locale-check.sh [-bite]
#
# -bite удаляет ключ и требует, чтобы -check упал: проверка, которая не падает
# на дыре, ничего не охраняет.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT
go build -C tools/lvnconv -o "$W/lvnconv" . || { echo "lvnconv не собрался"; exit 1; }

cat > "$W/ch.lvns" <<'EOF'
scene loc

Мира: Первая реплика.
Мира: Вторая реплика.
Мира: Третья реплика.
EOF
"$W/lvnconv" convert -i "$W/ch.lvns" -o "$W/ch.lvn" >/dev/null 2>&1 || { echo "скрипт не собрался"; exit 1; }
"$W/lvnconv" locale -lang ru "$W/ch.lvn" >/dev/null 2>&1 || { echo "каталог не построился"; exit 1; }

cat="$W/ch.ru.json"
say() { echo "  $1"; }
fail=0

# Переводим две строки из трёх — это и есть та работа, которую нельзя терять.
python3 - "$cat" <<'PY'
import json, sys
p = sys.argv[1]; d = json.load(open(p, encoding="utf-8"))
d["Первая реплика."] = "ПЕРЕВОД ПЕРВОЙ"
d["Вторая реплика."] = "ПЕРЕВОД ВТОРОЙ"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY

if [ -n "$BITE" ]; then
  python3 - "$cat" <<'PY'
import json, sys
p = sys.argv[1]; d = json.load(open(p, encoding="utf-8"))
del d["Третья реплика."]
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
  "$W/lvnconv" locale -lang ru -check "$W/ch.lvn" >/dev/null 2>&1; rc=$?
  say "укус: ключ удалён, -check вернул $rc (ждём 1)"
  [ "$rc" = "1" ] && { say "стенд честный: дыру в каталоге проверка видит"; exit 0; }
  say "✗ СТЕНД ВРЁТ: -check прошёл на неполном каталоге"; exit 2
fi

# ── ХРАНИТ: правим СОСЕДНЮЮ строку, переводы обязаны уцелеть ───────────────
cat > "$W/ch.lvns" <<'EOF'
scene loc

Мира: Первая реплика.
Мира: Вторая реплика.
Мира: Третья реплика, переписанная.
Мира: Четвёртая, новая.
EOF
"$W/lvnconv" convert -i "$W/ch.lvns" -o "$W/ch.lvn" >/dev/null 2>&1
"$W/lvnconv" locale -lang ru "$W/ch.lvn" >"$W/out2.txt" 2>&1
say "после правки: $(tail -1 "$W/out2.txt" | sed 's|.*json: ||')"

python3 - "$cat" <<'PY' || exit 1
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
bad = []
if d.get("Первая реплика.") != "ПЕРЕВОД ПЕРВОЙ": bad.append("перевод первой строки потерян")
if d.get("Вторая реплика.") != "ПЕРЕВОД ВТОРОЙ": bad.append("перевод второй строки потерян")
if "Четвёртая, новая." not in d: bad.append("новая строка не попала в каталог")
if "Третья реплика." not in d: bad.append("осиротевший ключ исчез БЕЗ -prune — работа пропала молча")
for b in bad: print("  ✗ " + b)
print("  переводы на месте, новая строка добавлена, старый ключ сохранён до -prune" if not bad else "")
raise SystemExit(1 if bad else 0)
PY
rc=$?; [ "$rc" = "0" ] || fail=1

# ── ГРАНИЦА: правка САМОЙ переведённой строки роняет её перевод ────────────
python3 - "$cat" <<'PY'
import json, sys
p = sys.argv[1]; d = json.load(open(p, encoding="utf-8"))
d["Третья реплика, переписанная."] = d.get("Третья реплика, переписанная.", "")
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
say "граница: у переписанной строки НОВЫЙ ключ — перевод к прежней осиротел"

# ── ЧИСТИТ: -prune убирает мёртвое и не трогает живое ──────────────────────
"$W/lvnconv" locale -lang ru -prune "$W/ch.lvn" >"$W/out3.txt" 2>&1
say "после -prune: $(tail -1 "$W/out3.txt" | sed 's|.*json: ||')"
python3 - "$cat" <<'PY' || exit 1
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
bad = []
if "Третья реплика." in d: bad.append("-prune не убрал ключ исчезнувшей строки")
if d.get("Первая реплика.") != "ПЕРЕВОД ПЕРВОЙ": bad.append("-prune СТЁР живой перевод")
if d.get("Вторая реплика.") != "ПЕРЕВОД ВТОРОЙ": bad.append("-prune СТЁР живой перевод")
for b in bad: print("  ✗ " + b)
print("  -prune убрал мёртвый ключ и не тронул переводы" if not bad else "")
raise SystemExit(1 if bad else 0)
PY
[ "$?" = "0" ] || fail=1

[ "$fail" = "0" ] && { echo "ПЕРЕВОДЫ ПЕРЕЖИВАЮТ ПРАВКУ ГЛАВЫ"; exit 0; }
echo "ПЕРЕВОДЫ ТЕРЯЮТСЯ"; exit 1
