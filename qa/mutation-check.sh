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
#   qa/mutation-check.sh
#
# Код возвращается ВСЕГДА (trap на выходе), даже если прогон прерван.
set -uo pipefail
cd "$(dirname "$0")/.."

command -v go >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }

W="$(mktemp -d)"
restore() {
  # Возврат кода — первое дело: прерванный прогон не должен оставить
  # мутацию в рабочем дереве.
  for f in "$W"/*.orig; do
    [ -e "$f" ] || continue
    base="$(basename "$f" .orig)"
    target="$(cat "$W/$base.path")"
    cp "$f" "$target"
  done
  rm -rf "$W"
}
trap restore EXIT

save() { # $1 = путь к файлу
  local key; key="$(echo "$1" | tr '/' '_')"
  cp "$1" "$W/$key.orig"
  printf '%s' "$1" > "$W/$key.path"
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
check() { # $1 имя, $2 файл, $3 старое, $4 новое, $5 -run фильтр, $6 пакет
  local name="$1" file="$2" old="$3" new="$4" filter="$5" pkg="$6"
  save "$file"
  if ! apply_mutation "$file" "$old" "$new"; then
    echo "  $name: место мутации не найдено — стенд отстал от кода"
    stale=$((stale+1)); return
  fi
  if (cd "$pkg" && go test ./... -run "$filter" -count=1 >/dev/null 2>&1); then
    echo "  $name: ТЕСТЫ ПРОМОЛЧАЛИ — инвариант не охраняется"
    blind=$((blind+1))
  else
    echo "  $name: тесты покраснели — инвариант охраняется"
    ok=$((ok+1))
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
  '		out.Changed, out.Removed = map[string]string{}, []string{} // мутация' \
  "Delta|Changes|Content" "server"

# ── 3. ВЕС АРТА: сырой PNG не уходит игроку ────────────────────────────────
if [ -f server/rawpng.go ]; then
  check "лечение сырого PNG" "server/rawpng.go" \
    'func healRawPNG' \
    'func healRawPNG_disabled' \
    "Raw" "server"
fi

echo
echo "  охраняется: $ok, без охраны: $blind, устарело мест: $stale"
[ "$blind" = "0" ] && [ "$stale" = "0" ] || exit 1
echo "держит: каждый проверенный инвариант охраняется тестом, а не надеждой"
