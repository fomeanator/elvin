#!/usr/bin/env bash
# ТИХИЙ ОТКАЗ — САМЫЙ ДОРОГОЙ КЛАСС ОШИБОК: АВТОР ПИШЕТ, А ПОЛУЧАЕТ МОЛЧА НЕ ТО.
#
# Строка, которую разбор не узнал, не пропадает — она становится РЕПЛИКОЙ и
# печатается игроку. Ни компилятор, ни валидатор при этом не обязаны сказать
# ни слова: синтаксически всё законно, просто автор имел в виду другое.
#
# Здесь собран корпус того, чем это болеет на самом деле: опечатка в имени
# команды, заглавная буква от автозамены, кириллический двойник латинской
# буквы, значение с пробелом без кавычек, `=` вместо `==`. Каждый случай
# прогоняется НАСТОЯЩИМ компилятором и валидатором, и у каждого записано
# ожидание — «ловится» или «пока тихо».
#
# ЗАЧЕМ ЗАПИСЫВАТЬ «ПОКА ТИХО». Граница, названная вслух, — это работа, а
# граница, о которой все забыли, — ловушка. Скрипт падает, когда ловившееся
# перестало ловиться; про обратное он говорит словами «стало лучше», не роняя
# прогон, — сузить границу можно и после.
#
#   qa/silent-traps-check.sh [-bite]
#
# -bite подменяет один заведомо ловимый случай на ПРАВИЛЬНЫЙ код: мерка,
# которая этого не заметит, ничего не стоит и в остальных строках.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT
go build -C tools/lvnconv -o "$W/lvnconv" . || { echo "lvnconv не собрался"; exit 1; }

BITE="$BITE" python3 - "$W" <<'PY'
import os, subprocess, sys

work, bite = sys.argv[1], bool(os.environ.get("BITE"))
lvnconv = os.path.join(work, "lvnconv")

# имя → (текст главы, ожидание: True = должно быть названо автору)
CASES = {
    "опечатка в имени команды":      ("actr Анна pose=idle\n", True),
    "команда с заглавной буквы":     ("Actor Анна emotion=happy\n", True),
    "команда заглавными целиком":    ("ACTOR Анна emotion=happy\n", True),
    "кириллическая буква в команде": ("аctor Анна emotion=happy\n", True),
    "кириллическая буква в clear":   ("сlear all=1\n", True),
    "заглавная перед путём":         ("Bg /content/bg/room.png\n", True),
    "опечатка в имени поля":         ("actor Анна emotoin=happy\n", True),
    "значение с пробелом":           ("text id=hp label=Очки жизни\n", True),
    "присваивание вместо сравнения": ("set x=1\nif x = 1 {\n  Анна: Да.\n}\n", True),
    "set без знака равенства":       ("set x 5\n", True),
    "wait без имени поля":           ("wait 500\n", True),
    "неизвестная команда":           ("shake power=3\n", True),
    # ── граница: сюда язык пока не смотрит, и это сказано вслух ──────────────
    "двойник в имени файла":         ("bg /content/bg/rооm.png\n", False),
    "ключ повторён дважды":          ("actor Анна x=0.2 x=0.8\n", False),
    "число строкой":                 ("actor Анна x=\"0.2\"\n", False),
}

if bite:
    # Подмена: заведомо ловимый случай становится ПРАВИЛЬНЫМ кодом.
    CASES["опечатка в имени команды"] = ("actor Анна pose=idle\n", True)

def говорит(name, body):
    """True, если автор узнает о беде: компилятор упал или валидатор сказал."""
    src = os.path.join(work, "case.lvns")
    out = os.path.join(work, "case.lvn")
    open(src, "w", encoding="utf-8").write("scene t\n\n" + body + "Анна: Дальше.\n-> __end\n")
    if subprocess.run([lvnconv, "convert", "-i", src, "-o", out],
                      capture_output=True).returncode != 0:
        return True, "компилятор"
    r = subprocess.run([lvnconv, "validate", "-strict", out], capture_output=True, text=True)
    if r.returncode != 0:
        first = next((l for l in r.stdout.splitlines() + r.stderr.splitlines()
                      if l.startswith(("error", "warning"))), "")
        return True, first.split(":", 2)[-1].strip()[:52]
    return False, ""

worse, better = [], []
for name, (body, expected) in CASES.items():
    said, how = говорит(name, body)
    mark = "говорит" if said else "ТИХО   "
    print(f"  {mark}  {name}" + (f"  — {how}" if said else ""))
    if expected and not said:
        worse.append(name)
    if not expected and said:
        better.append(name)

print(f"\nслучаев: {len(CASES)}; названо автору: {sum(1 for n,(b,e) in CASES.items() if e)}; "
      f"граница (пока тихо): {sum(1 for n,(b,e) in CASES.items() if not e)}")

if bite:
    if worse:
        print("укус замечен: подмена сделала случай тихим, стенд её увидел")
        sys.exit(0)
    print("СТЕНД СЛЕП: подменённый случай прошёл незамеченным — мерка не мерит")
    sys.exit(2)

for n in better:
    print(f"стало лучше: «{n}» теперь называется автору — обновите ожидание в скрипте")
if worse:
    print("РВЁТСЯ: перестало ловиться — " + ", ".join(worse))
    sys.exit(1)
print("держит: всё, что ловилось, ловится")
PY
