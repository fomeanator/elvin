#!/usr/bin/env bash
# ПЕСОЧНИЦА НАЗЫВАЕТ ТО, ЧЕГО НЕ ДЕЛАЕТ.
#
# Веб-плеер — объявленное подмножество движка (решение 01.09): новая операция
# не обязана приезжать в браузер, обязана лишь не ломать его. Цена этого
# решения ложится на АВТОРА: он пишет `voice` и слышит тишину, пишет `scale` и
# видит прежний размер — и молчание неотличимо от поломки. Замер 06.09: таких
# полей тридцать, и о них не было сказано нигде.
#
# Отсюда договор: песочница показывает автору список полей, которые она
# пропустила. Список читаемых полей (READ_FIELDS в app.js) ведётся руками —
# значит он обязан сходиться с кодом, иначе объявление врёт в обе стороны:
# промолчит о непрочитанном или пообещает несуществующее.
#
#   ЧИТАЕТ       каждое поле, которое код песочницы реально берёт из команды,
#                объявлено в READ_FIELDS;
#   НЕ ОБЕЩАЕТ   в READ_FIELDS нет полей, которых код не читает;
#   ГОВОРИТ      механизм объявления на месте (collectSilentFields вызывается).
#
#   qa/sandbox-silence-check.sh [-bite]
#
# -bite добавляет в код чтение выдуманного поля: стенд обязан заметить, что
# список отстал. Страж, не замечающий расхождения, охраняет пустоту.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

APP="panel/public/play/app.js"
CORE="panel/public/play/core.js"
[ -f "$APP" ] || { echo "нет $APP — пропускаю"; exit 0; }

W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT
cp "$APP" "$W/app.js"
if [ -n "$BITE" ]; then
  # Укус: код начинает читать поле, которого нет в списке.
  printf '\nconst biteProbe = (cmd) => cmd.выдуманное_поле;\n' >> "$W/app.js"
fi

python3 - "$W/app.js" "$CORE" <<'PY'
import re, sys

app_path, core_path = sys.argv[1], sys.argv[2]
app = open(app_path, encoding="utf-8").read()
core = open(core_path, encoding="utf-8").read()

m = re.search(r"const READ_FIELDS = new Set\(\[(.*?)\]\)", app, re.S)
if not m:
    print("в app.js нет списка READ_FIELDS — объявлять автору нечем")
    sys.exit(1)
declared = set(re.findall(r'"([^"]+)"', m.group(1)))

# Что код РЕАЛЬНО читает из команды: cmd.поле и cmd["поле"].
used = set(re.findall(r"\bcmd\.([A-Za-z_а-яА-Я][\w_а-яА-Я]*)", app + core))
used |= set(re.findall(r'\bcmd\["([^"]+)"\]', app + core))
used -= {"op"}          # служебное, всегда есть
used = {f for f in used if not f.startswith("_")}

missing = sorted(used - declared)      # читаем, но не объявили
extra = sorted(declared - used)        # объявили, но не читаем

print(f"  читает полей: {len(used)}, объявлено: {len(declared)}")
print(f"  механизм объявления: {'на месте' if 'collectSilentFields(doc)' in app else 'ОТСУТСТВУЕТ'}")

bad = []
if missing:
    bad.append("код читает поля, которых нет в списке: " + ", ".join(missing)
               + "\n     автору о них скажут «не работает», хотя они работают")
if "collectSilentFields(doc)" not in app:
    bad.append("объявление молчаливых полей не вызывается — автор снова остаётся с тишиной")

# Лишнее в списке терпимо (поля из будущего/из других команд), но называем.
if extra:
    print(f"  в списке есть необязательные: {len(extra)} (не ошибка)")

if bad:
    print("РВЁТСЯ:")
    for b in bad:
        print("   " + b)
    sys.exit(1)
sys.exit(0)
PY
code=$?

if [ -n "$BITE" ]; then
  if [ "$code" != "0" ]; then
    echo "укус чист: код начал читать необъявленное поле — стенд это увидел"
    exit 0
  fi
  echo "СТЕНД СЛЕП: в коде появилось чтение поля мимо списка, а он промолчал"
  exit 2
fi
[ "$code" = "0" ] || exit 1
echo "держит: список читаемых полей сходится с кодом, автору говорят о пропущенном"
