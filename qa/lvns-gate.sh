#!/usr/bin/env bash
# ГЕЙТ СОДЕРЖИМОГО: ТО, ЧТО СЛОМАЕТСЯ У ИГРОКА, НЕ ДОЛЖНО УЕХАТЬ.
#
# Корпус — всё, что мы РАЗДАЁМ как образец: howto/, examples/ и UPM-Sample,
# который потребитель импортирует через Unity Samples. Автор копирует эти файлы
# как эталон, поэтому дефект в них расходится дальше собственного файла.
#
# ПОЧЕМУ ЭТО ОТДЕЛЬНЫЙ СКРИПТ, А НЕ СТРОКИ В ci.yml. Прежний гейт жил прямо в
# workflow и проверял ТЕКСТ вывода валидатора:
#
#     case "$v" in *" 0 warning(s)"*) ;; *) fail=1;; esac
#
# Валидатор на битом скрипте печатает «FAIL: 1 error(s), 0 warning(s)» — строка
# УДОВЛЕТВОРЯЕТ образцу, и гейт пропускал ошибку. Код возврата при этом
# отбрасывался подстановкой $(…). Замерено: глава с опечаткой в имени команды
# проходила гейт целиком. Проверка, живущая в одном экземпляре и умеющая падать
# на заведомо битом входе, — единственная защита от повторения.
#
# ДОГОВОР: код возврата инструмента — судья. Никаких сравнений с текстом.
#   convert          собирает; ненулевой код — не собралось;
#   validate -strict ноль ошибок И ноль предупреждений;
#   walk -strict     ни одного недостижимого блока во всём корпусе.
#
# ВКЛЕИВАЕМЫЕ ФАЙЛЫ ПРОПУСКАЮТСЯ. Файл, который другой файл корпуса подключает
# через include, в одиночку не играется никогда: у него нет ни сцены, ни входа,
# и его метки достигаются прыжком из подключающего. Прежний гейт компилировал
# такой файл отдельно и краснел на нём — то есть требовал невозможного.
#
#   qa/lvns-gate.sh [-selftest]
set -uo pipefail
cd "$(dirname "$0")/.."

command -v go >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT
go build -C tools/lvnconv -o "$W/lvnconv" . || { echo "lvnconv не собрался"; exit 1; }

corpus() {
  ls howto/*/*.lvns examples/*.lvns \
     unity/Packages/com.lvn.engine/Samples~/*/Resources/*.lvns 2>/dev/null
}

# Кого подключают — тех не проверяем отдельно.
included() {
  corpus | while read -r f; do
    grep -oE '^[[:space:]]*include[[:space:]]+"[^"]+"' "$f" 2>/dev/null |
      sed 's/.*"\(.*\)"/\1/' | while read -r rel; do
        printf '%s\n' "$(cd "$(dirname "$f")" && pwd)/$rel"
      done
  done | sort -u
}

check_one() { # $1 = файл; печатает причину и возвращает 1
  local f="$1" out="$W/$(basename "${1%.lvns}").lvn"
  if ! "$W/lvnconv" convert -i "$f" -o "$out" >"$W/err" 2>&1; then
    echo "  НЕ СОБРАЛОСЬ: $f — $(tail -1 "$W/err")"; return 1
  fi
  if ! "$W/lvnconv" validate -strict "$out" >"$W/err" 2>&1; then
    echo "  НЕ ПРОШЛО: $f"
    grep -E "^(error|warning):" "$W/err" | sed 's/^/     /'
    return 1
  fi
  return 0
}

if [ "${1:-}" = "-selftest" ]; then
  # Гейт обязан уметь падать. Подкладываем опечатку в имени команды — ровно тот
  # случай, что проходил насквозь: строка молча становится репликой и уезжает
  # игроку как текст.
  printf 'scene selftest\n\nbg room\nactr Мира pose=idle\nМира: Меня должно быть видно.\n' \
    > "$W/selftest.lvns"
  if check_one "$W/selftest.lvns" >/dev/null 2>&1; then
    echo "САМОПРОВЕРКА ПРОВАЛЕНА: гейт принял скрипт с опечаткой в команде"; exit 2
  fi
  echo "самопроверка: опечатка в команде отвергнута — гейт умеет падать"; exit 0
fi

SKIP="$(included)"
n=0; skipped=0; bad=0
while read -r f; do
  [ -n "$f" ] || continue
  abs="$(cd "$(dirname "$f")" && pwd)/$(basename "$f")"
  if printf '%s\n' "$SKIP" | grep -qxF "$abs"; then
    skipped=$((skipped+1)); continue
  fi
  n=$((n+1))
  check_one "$f" || bad=$((bad+1))
done <<EOF
$(corpus)
EOF

# МЁРТВЫЙ КОНТЕНТ — ОТДЕЛЬНАЯ БЕДА, И ВАЛИДАТОР ЕЁ НЕ ЛОВИТ ПО ПОСТРОЕНИЮ:
# ссылки целы, команды известны, а пути к куску скрипта всё равно нет. Находит
# такое только обход всех ветвей.
#
# Проверка стояла в ci.yml и БОЛЬШЕ НИГДЕ — ровно как gofmt до этой ночи. А
# включён ли workflow на площадке, отсюда не видно (см. таблицу решений в
# docs/world-position.md). Здесь она идёт по тому же списку, что и в CI, и
# судит КОДОМ ВОЗВРАТА: walk -strict возвращает 1 на мёртвом блоке.
#
# Список берётся из git, а не из глоба: пустой список молча дал бы зелёный
# обход ни по чему.
walked=0
if command -v git >/dev/null 2>&1; then
  lvns_all=()
  while IFS= read -r f; do [ -n "$f" ] && lvns_all+=("$f"); done < <(git -c core.quotePath=false ls-files '*.lvns')
  walked=${#lvns_all[@]}
  if [ "$walked" -lt 10 ]; then
    echo "  СПИСОК ДЛЯ ОБХОДА СХЛОПНУЛСЯ ($walked файлов) — обход шёл бы по пустоте"
    bad=$((bad+1))
  elif ! "$W/lvnconv" walk -strict "${lvns_all[@]}" > "$W/walk.txt" 2>&1; then
    echo "  НЕДОСТИЖИМЫЙ КОНТЕНТ:"
    grep -E "МЁРТВО|метки:" "$W/walk.txt" | sed 's/^/     /'
    bad=$((bad+1))
  fi
fi

echo "проверено: $n, пропущено как вклеиваемые: $skipped, обойдено: $walked, не прошли: $bad"
[ "$n" -ge 15 ] || { echo "КОРПУС СХЛОПНУЛСЯ ($n файлов) — гейт проверял бы пустоту"; exit 2; }
[ "$bad" = "0" ] || { echo "ГЕЙТ ЗАКРЫТ"; exit 1; }
echo "ГЕЙТ ОТКРЫТ"; exit 0
