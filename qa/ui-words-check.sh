#!/usr/bin/env bash
# ПОДПИСЬ, КОТОРУЮ ЗАБЫЛИ ПЕРЕВЕСТИ, ОБЯЗАНА НАЙТИСЬ ИНСТРУМЕНТОМ, А НЕ ИГРОКОМ.
#
# Экран движка не пишет слов сам: подпись идёт через ключ с английским
# умолчанием, а родное слово кладёт автор игры. Пока список ключей знали только
# исходники, пропуск ничем себя не выдавал — игра шла, экран работал, и одна
# строка посреди русского меню оставалась английской. Узнавалось со скриншота.
#
# Стенд проверяет ОБЕ стороны отчёта `lvnconv locale -ui`:
#
#   находит      забытый ключ назван, и назван ровно он;
#   не выдумывает ключ, названный ДРУГИМ законным способом (полем секции или
#                русской тройкой множественного числа), в список не попадает —
#                иначе автор, сделавший всё правильно, получает ложный долг.
#
#   qa/ui-words-check.sh [-bite]
#
# -bite строит манифест, где названо ВСЁ, и требует пустого отчёта: проверка,
# которая ругается всегда, ничего не проверяет.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT
go build -C tools/lvnconv -o "$W/lvnconv" . || { echo "lvnconv не собрался"; exit 1; }

BITE="$BITE" python3 - "$W" <<'PY'
import json, os, subprocess, sys

work = sys.argv[1]
bite = bool(os.environ.get("BITE"))
words = json.load(open("tools/lvnconv/lvn/ui-words.json", encoding="utf-8"))
if len(words) < 100:
    print(f"реестр подписей подозрительно мал ({len(words)}) — стенду не на чем стоять")
    sys.exit(2)

by_key = {w["key"]: w for w in words}
# Три законных способа назвать подпись — по одному представителю на каждый.
field_key = next((w["key"] for w in words if w.get("field")), None)
plural_key = next((k for k in by_key if k.endswith(".other")
                   and k[:-len(".other")] + ".one" in by_key), None)
plain = [w["key"] for w in words
         if not w.get("field") and not w["key"].endswith(".other")][:2]
if not field_key or not plural_key or len(plain) < 2:
    print("в реестре нет всех трёх форм — стенд не построить")
    sys.exit(2)

manifest = {"ui": {"words": {}, "menu": {"labels": {}}}}
for w in words:
    key = w["key"]
    if not bite and key in plain:
        continue                       # ЗАБЫТЫЕ: их отчёт обязан назвать
    if not bite and key == field_key:
        continue                       # назван полем секции — ниже
    if not bite and key == plural_key:
        continue                       # назван тройкой — ниже
    manifest["ui"]["words"][key] = "слово"
if not bite:
    section, field = by_key[field_key]["field"].split(".", 1)
    manifest["ui"][section] = {field: "слово"}
    base = plural_key[:-len(".other")]
    manifest["ui"]["words"][base + ".few"] = "слова"
    manifest["ui"]["words"][base + ".many"] = "слов"

path = os.path.join(work, "manifest.json")
json.dump(manifest, open(path, "w", encoding="utf-8"), ensure_ascii=False)

r = subprocess.run([os.path.join(work, "lvnconv"), "locale", "-ui", path, "-strict"],
                   capture_output=True, text=True)
print(r.stdout.strip() or r.stderr.strip())
named = {l.split()[0] for l in r.stdout.splitlines() if l.startswith("  ")}

if bite:
    if r.returncode == 0 and not named:
        print("укус чист: там, где названо всё, отчёт молчит")
        sys.exit(0)
    print("СТЕНД ВРЁТ: на полном словаре отчёт всё равно требует перевода — ", named)
    sys.exit(2)

problems = []
if r.returncode == 0:
    problems.append("-strict не упал, хотя две подписи не названы")
for k in plain:
    if k not in named:
        problems.append(f"забытая подпись {k} не названа — автор узнает о ней от игрока")
if field_key in named:
    problems.append(f"{field_key} названа полем секции, а отчёт всё равно требует словарь")
if plural_key in named:
    problems.append(f"{plural_key} закрыта русской тройкой, а отчёт её требует")
extra = named - set(plain)
if extra:
    problems.append("отчёт выдумал долг: " + ", ".join(sorted(extra)))

if problems:
    print("РВЁТСЯ:")
    for p in problems:
        print("  " + p)
    sys.exit(1)
print(f"держит: забытое названо ({', '.join(plain)}), названное другим способом — не выдумано")
PY
