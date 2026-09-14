#!/usr/bin/env bash
# А ТЕСТЫ-ТО ОХРАНЯЮТ? Ломаем инвариант нарочно и ждём красного.
#
# У стендов для этого есть флаг `-bite`; у тестов на Go такого флага нет и быть
# не может — их «укус» это правка самого кода. Значит вопрос «зелёный прогон
# чего-то стоит?» для них до сих пор оставался без ответа.
#
# Здесь он проверяется прямо: в код вносится ИЗВЕСТНАЯ поломка, прогоняются
# тесты, и мерка обязана покраснеть. Не покраснела — инвариант не охраняется
# никем, и это находка того же сорта, что дефект.
#
# Мутации выбраны по цене ошибки: деньги, доставка контента, вес арта.
#
#   qa/mutation-check.sh            только Go: секунды
#   qa/mutation-check.sh --csharp   плюс C#: каждая мутация — прогон EditMode,
#                                   это минуты, поэтому в общий прогон не входит
#
# Мутации живут только в отдельном снимке, включая незакоммиченные файлы.
# Возвращать резервную копию в живое дерево нельзя: пока шли тесты, автор
# мог сохранить новую правку — restore затёр бы её даже при штатном выходе.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
CSHARP=""
case "${1:-}" in
  ""|-bite) ;; # общий сборщик укусов зовёт и этот стенд
  --csharp) CSHARP=1 ;;
  *) echo "неизвестный аргумент: $1"; exit 2 ;;
esac

command -v go >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }

for dependency in git rsync python3; do
  command -v "$dependency" >/dev/null 2>&1 || { echo "нет $dependency — безопасная изоляция невозможна"; exit 1; }
done
MUTATION_ROOT="$PWD"
W="$(mktemp -d "${TMPDIR:-/tmp}/elvin-mutation.XXXXXX")" || exit 1
trap 'rm -rf -- "$W"' EXIT
# Только временный каталог убирается при выходе. Восстанавливать исходники
# оригинального проекта не требуется даже после SIGKILL: мы их не меняем.
if ! git -c core.quotePath=false ls-files -z -c -o --exclude-standard > "$W/files" \
   || [ ! -s "$W/files" ] \
   || ! mkdir "$W/repo" \
   || ! rsync -a --copy-links --from0 --files-from="$W/files" "$MUTATION_ROOT/" "$W/repo/"; then
  echo "снимок для мутаций не создан — живое дерево не трогаю"
  exit 1
fi
cd "$W/repo" || exit 1

save() { # $1 = путь к файлу
  local key; key="$(echo "$1" | tr '/' '_')"
  cp "$1" "$W/$key.orig"
}

apply_mutation() { # $1 файл, $2 что заменить, $3 на что
  python3 - "$1" "$2" "$3" <<'PY'
import io, sys
path, old, new = sys.argv[1], sys.argv[2], sys.argv[3]
s = io.open(path, encoding="utf-8").read()
if old not in s:
    sys.exit(3)                     # место мутации уехало — стенд должен сказать об этом
io.open(path, "w", encoding="utf-8").write(s.replace(old, new, 1))
PY
}

ok=0; blind=0; stale=0
test_event() { # $1 JSON-журнал go test, $2 ожидаемый исход конкретного теста
  python3 - "$1" "$2" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as events:
    for line in events:
        try:
            event = json.loads(line)
        except ValueError:
            continue
        if event.get("Action") == sys.argv[2] and event.get("Test"):
            sys.exit(0)
sys.exit(1)
PY
}

check() { # $1 имя, $2 файл, $3 старое, $4 новое, $5 -run фильтр, $6 пакет
  local name="$1" file="$2" old="$3" new="$4" filter="$5" pkg="$6"
  local result="$W/go-$(echo "$file" | tr '/' '_').json"
  # Иначе заранее красный тест выдаёт любую мутацию за пойманную.
  if ! (cd "$pkg" && GOWORK=off go test ./... -run "$filter" -count=1 -json >"$result" 2>&1); then
    echo "  $name: исходный прогон не зелёный — судить о мутации нельзя"
    stale=$((stale+1)); return
  fi
  if ! test_event "$result" pass; then
    echo "  $name: фильтр не выполнил ни одного теста — судить о мутации нельзя"
    stale=$((stale+1)); return
  fi
  save "$file"
  if ! apply_mutation "$file" "$old" "$new"; then
    echo "  $name: место мутации не найдено — стенд отстал от кода"
    stale=$((stale+1)); return
  fi
  if (cd "$pkg" && GOWORK=off go test ./... -run "$filter" -count=1 -json >"$result" 2>&1); then
    echo "  $name: ТЕСТЫ ПРОМОЛЧАЛИ — инвариант не охраняется"
    blind=$((blind+1))
  else
    # Ошибка сборки или запуска не доказывает, что инвариант охраняет тест.
    # Нужен failed у КОНКРЕТНОГО теста, не только ненулевой код процесса.
    if test_event "$result" fail; then
      echo "  $name: тесты покраснели — инвариант охраняется"
      ok=$((ok+1))
    else
      echo "  $name: тесты не выполнились — ошибка сборки/запуска не считается защитой"
      stale=$((stale+1))
    fi
  fi
  cp "$W/$(echo "$file" | tr '/' '_').orig" "$file"
}

# ── 1. ДЕНЬГИ: повтор запроса не должен списывать дважды ───────────────────
check "идемпотентность кошелька" "server/wallet.go" \
  '		if req.OpID != "" {' \
  '		if false && req.OpID != "" { // мутация' \
  "Idemp|Wallet" "server"

# ── 2. ДОСТАВКА: сервер называет изменившееся поимённо, а не «забирай всё» ──
check "разница контента" "server/content_delta.go" \
  '		out.Changed, out.Removed = diffVersions(prev, cur)' \
  '		out.Changed, out.Removed = diffVersions(prev, cur); out.Changed, out.Removed = map[string]string{}, []string{} // мутация' \
  "^Test(РазницаНазываетТолькоИзменившееся|УдалённыеФайлыНазваныОтдельно)$" "server"

# ── 3. ВЕС АРТА: сырой PNG не уходит игроку ────────────────────────────────
if [ -f server/rawpng.go ]; then
  check "лечение сырого PNG" "server/rawpng.go" \
    'if int64(buf.Len()) >= before {' \
    'if true { // мутация: не пережимаем сырой файл' \
    "Raw" "server"
fi

# ── C#: самая большая часть корпуса, и до сих пор не проверенная ───────────
#
# Прогон EditMode идёт минутами, поэтому каждая мутация здесь стоит дорого и
# берётся по одной. Фильтр сужает прогон до тех тестов, которые ОБЯЗАНЫ
# покраснеть: если сломанный инвариант не роняет свой же тест, охраны нет.
check_cs() { # $1 имя, $2 файл, $3 старое, $4 новое, $5 фильтр тестов
  local name="$1" file="$2" old="$3" new="$4" filter="$5"
  save "$file"
  if ! apply_mutation "$file" "$old" "$new"; then
    echo "  $name: место мутации не найдено — стенд отстал от кода"
    stale=$((stale+1)); return
  fi
  # Unity зовётся НАПРЯМУЮ, минуя общий прогон: тому перед тестами нужно
  # прогнать полсотни стендов, и мутация платила бы за них каждый раз.
  local log res
  log="$W/cs-$(echo "$name" | tr ' ' '_').log"
  res="$W/cs-$(echo "$name" | tr ' ' '_').xml"
  local unity="${UNITY:-/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity}"
  [ -x "$unity" ] || { echo "  $name: нет Unity — пропускаю"; stale=$((stale+1));     cp "$W/$(echo "$file" | tr '/' '_').orig" "$file"; return; }
  nice -n 10 "$unity" -batchmode -nographics -projectPath "$PWD/unity/TestHost" \
    -runTests -testPlatform EditMode -testFilter "$filter" \
    -testResults "$res" -logFile "$log" >/dev/null 2>&1
  # Красный ищем в отчёте, а не в коде возврата: Unity возвращает ненулевой код
  # и когда тесты упали, и когда не смог запуститься — а это разные новости.
  if python3 -c "
import sys, xml.etree.ElementTree as ET
try:
    r = ET.parse('$res').getroot()
except Exception:
    sys.exit(2)                      # отчёта нет — прогон не состоялся
sys.exit(0 if int(r.get('failed') or 0) > 0 else 1)
"; then
    echo "  $name: тесты покраснели — инвариант охраняется"
    ok=$((ok+1))
  elif [ "$?" = "2" ]; then
    echo "  $name: прогон не состоялся — судить не о чем"
    stale=$((stale+1))
  else
    echo "  $name: ТЕСТЫ ПРОМОЛЧАЛИ — инвариант не охраняется"
    blind=$((blind+1))
  fi
  cp "$W/$(echo "$file" | tr '/' '_').orig" "$file"
}

if [ -n "$CSHARP" ]; then
  # СЛОВАРЬ ДА-НЕТ. `show=no` обязано убирать фигуру со сцены; на этом месте
  # три рантайма однажды дали три разных ответа (см. корпус, слова-флаги).
  check_cs "словарь да-нет" "unity/Packages/com.lvn.engine/Runtime/LvnBool.cs" \
    'case "0": case "false": case "no": case "n": case "off": case "нет":' \
    'case "0": case "false": case "n": case "off": // мутация: «no» больше не ложь' \
    "Lvn.Tests.ConformanceCorpusTests"

  # БЮДЖЕТ ПАМЯТИ. Правило «меньшее из доли устройства и половины планки
  # приложения» — то самое, из-за которого игра ела треть памяти телефона.
  check_cs "бюджет памяти" \
    "unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.SpriteCache.cs" \
    'b = Math.Min(b, half);' \
    'b = Math.Max(b, half); // мутация: планка приложения перестала ограничивать' \
    "Lvn.Tests.WeakDeviceBudgetTests"
fi

echo
echo "  охраняется: $ok, без охраны: $blind, устарело мест: $stale"
[ "$blind" = "0" ] && [ "$stale" = "0" ] || exit 1
echo "держит: каждый проверенный инвариант охраняется тестом, а не надеждой"
