#!/usr/bin/env bash
# ПРОВЕРКА C# БЕЗ UNITY — компиляция сборок движка на Roslyn из самого редактора.
#
# Зачем: правка в C# до сих пор проверялась только запуском Unity, а он занят
# (открыт проект, идёт импорт) или просто долог — и опечатка в рантайме
# обнаруживалась через минуты. Между тем компилятор лежит внутри редактора, а
# ссылочные сборки платформы — рядом с ним. Этого хватает, чтобы ответить за
# секунды.
#
# СБОРКИ ИДУТ В ПОРЯДКЕ ЗАВИСИМОСТЕЙ, и каждая следующая ссылается на СВЕЖИЙ
# результат предыдущей, а не на то, что лежит в Library собранного проекта.
# Иначе проверка врёт в обе стороны: устаревшая библиотека в Library даёт
# «нет такого типа» на здоровом коде, а свежая — прячет поломку.
#
# Что это НЕ заменяет: прогон тестов (qa/run-all.sh). Здесь проверяется, что
# код СОБИРАЕТСЯ, а не что он работает.
#
# Ссылки на ПАКЕТЫ (NUnit, Newtonsoft, KTX) берутся у любого собранного
# Unity-проекта: по умолчанию у песочницы движка (sandbox/), но подойдёт и
# другой — аргументом или в LVN_UNITY_PROJECT. Library/ там не под
# версионированием, поэтому на чистой машине проверка честно пропускается.
#
#   qa/csharp-check.sh [проект-с-Library]
set -uo pipefail
cd "$(dirname "$0")/.."

APP="${1:-${LVN_UNITY_PROJECT:-$PWD/sandbox}}"
UNITY="$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app 2>/dev/null | sort -V | tail -1)"
[ -n "$UNITY" ] || { echo "нет Unity — пропускаю"; exit 0; }

DOTNET="$UNITY/Contents/Resources/Scripting/NetCoreRuntime/dotnet"
CSC="$UNITY/Contents/Resources/Scripting/DotNetSdkRoslyn/csc.dll"
FW="$(ls -d "$UNITY/Contents/Resources/Scripting/NetCoreRuntime/shared/Microsoft.NETCore.App"/* 2>/dev/null | tail -1)"
UE="$UNITY/Contents/Resources/Scripting/Managed/UnityEngine"
LIB="$APP/Library/ScriptAssemblies"
for p in "$DOTNET" "$CSC" "$FW" "$UE" "$APP/Library/PackageCache" "$LIB"; do
  [ -e "$p" ] || { echo "нет $p — пропускаю (нужен собранный Unity-проект: $APP)"; exit 0; }
done

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/out"

# Общие ссылки: платформа, движок Unity и сборки пакетов (по одной на имя —
# один и тот же .dll лежит в кэше под несколькими редакциями).
{ for d in "$FW"/*.dll; do echo "-r:\"$d\""; done
  for d in "$UE"/*.dll; do echo "-r:\"$d\""; done
  for d in "$UNITY/Contents/Managed/UnityEngine"/*.dll "$UNITY/Contents/Managed"/UnityEditor*.dll; do
    [ -f "$d" ] && echo "-r:\"$d\""
  done
  # Модули поддержки платформ: редакторный код зовёт их напрямую (иконка
  # приложения — AndroidPlatformIconKind), а в базовом наборе редактора их нет.
  for d in "$UNITY/../PlaybackEngines"/*/UnityEditor.*.Extensions.dll; do
    [ -f "$d" ] && echo "-r:\"$d\""
  done
  # Сборки, которые мы НЕ строим сами: пакеты и то, что Unity скомпилировал в
  # проекте (Ktx, UnityEngine.UI). Одноимённое нашему исключается — его свежая
  # копия уже стоит выше по списку ссылок.
  python3 - "$APP" <<'PYEOF'
import glob, os, struct, sys
app = sys.argv[1]
ours = {os.path.basename(a)[:-7] for a in glob.glob("unity/Packages/**/*.asmdef", recursive=True)}

def managed(path):
    """Управляемая ли это сборка.

    В кэше пакетов лежат и РОДНЫЕ библиотеки (ktx_unity.dll для WSA/x64 — та
    самая, что валила проверку). Roslyn на такой ссылке не ругается вежливо, а
    ОБРЫВАЕТ компиляцию целиком: «error CS0009: образ не содержит управляемые
    метаданные». Отличить их по имени или пути нельзя — только по заголовку.
    """
    try:
        with open(path, "rb") as f:
            data = f.read(0x400)
        if data[:2] != b"MZ":
            return False
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe + 4] != b"PE\0\0":
            return False
        magic = struct.unpack_from("<H", data, pe + 24)[0]
        # Каталоги данных идут после необязательного заголовка (его размер
        # зависит от разрядности), CLI — пятнадцатая запись по восемь байт.
        cli = pe + 24 + (112 if magic == 0x20B else 96) + 14 * 8
        rva, size = struct.unpack_from("<II", data, cli)
        return rva != 0 and size != 0
    except Exception:
        return False

seen = {}
for p in glob.glob(os.path.join(app, "Library", "PackageCache", "**", "*.dll"), recursive=True):
    if not managed(p):
        continue
    seen.setdefault(os.path.basename(p), p)
for p in glob.glob(os.path.join(app, "Library", "ScriptAssemblies", "*.dll")):
    if os.path.basename(p)[:-4] in ours:
        continue
    seen.setdefault(os.path.basename(p), p)
for p in sorted(seen.values()):
    print(f'-r:"{p}"')
PYEOF
} > "$WORK/common.rsp"

# Порядок сборки и состав каждой — по графу asmdef.
python3 - "$APP" > "$WORK/plan.tsv" <<'PYEOF'
import glob, json, os, sys

app = sys.argv[1]
have_pkg = set()
for pat in (("Library", "PackageCache", "**", "*.dll"), ("Library", "ScriptAssemblies", "*.dll")):
    for p in glob.glob(os.path.join(app, *pat), recursive=True):
        have_pkg.add(os.path.basename(p)[:-4])
# Имя сборки в asmdef и имя файла .dll совпадают не всегда.
if "Newtonsoft.Json" in have_pkg:
    have_pkg.add("Unity.Nuget.Newtonsoft-Json")

asmdefs = sorted(glob.glob("unity/Packages/**/*.asmdef", recursive=True))
mine, dirs, refs = {}, {}, {}
for a in asmdefs:
    name = os.path.basename(a)[:-7]
    mine[name] = a
    dirs[name] = os.path.dirname(a)
    data = json.load(open(a))
    refs[name] = [r for r in (data.get("references") or []) if not r.startswith("GUID:")]

# Каталог принадлежит ближайшему asmdef вверх по дереву.
owner = {}
for name, d in dirs.items():
    owner[os.path.normpath(d)] = name

def owner_of(path):
    d = os.path.dirname(path)
    while True:
        n = owner.get(os.path.normpath(d))
        if n: return n
        parent = os.path.dirname(d)
        if parent == d: return None
        d = parent

sources = {n: [] for n in mine}
for cs in glob.glob("unity/Packages/**/*.cs", recursive=True):
    n = owner_of(cs)
    if n: sources[n].append(cs)

done, order = set(), []
while True:
    progress = False
    for name in sorted(mine):
        if name in done: continue
        deps = [r for r in refs[name] if r in mine]
        if all(d in done for d in deps):
            order.append(name); done.add(name); progress = True
    if not progress: break
for name in sorted(mine):
    if name not in done: order.append(name)   # цикл в графе — пусть падёт вслух

# Пустое поле пишется прочерком: TAB для оболочки — пробельный разделитель,
# и два подряд она схлопывает в один. Строка «имя, пусто, пусто, файлы»
# читалась как «имя, файлы» — и вся сборка молча пропускалась как «без
# исходников». Это стоило часа: скрипт бодро сообщал «C# собирается», не
# скомпилировав НИЧЕГО.
def field(v):
    return v or "-"

for name in order:
    missing = [r for r in refs[name] if r not in mine and r not in have_pkg]
    print("\t".join([name, field(",".join(missing)), field(",".join(sorted(
        v["define"] for v in (json.load(open(mine[name])).get("versionDefines") or []) if v.get("define")
    ))), field(" ".join(sorted(sources[name])))]))
PYEOF

fail=0
while IFS=$'\t' read -r name missing defines srcs; do
  [ "$srcs" = "-" ] && continue
  [ "$defines" = "-" ] && defines=""
  if [ "$missing" != "-" ]; then
    printf '· %-28s пропуск: нет ссылок (%s)\n' "$name" "${missing//,/ }"
    continue
  fi
  rsp="$WORK/$name.rsp"
  # НА КАЖДУЮ НАШУ СБОРКУ — РОВНО ОДНА ССЫЛКА. Свежесобранная, если она уже
  # готова; иначе прошлая из Library проекта (сборку могли пропустить —
  # например, Spine без своего пакета). Две ссылки с одним именем Roslyn не
  # принимает вовсе, а без ссылки код «не видит» половину движка.
  { while IFS=$'\t' read -r n _rest; do
      [ "$n" = "$name" ] && continue
      if [ -f "$WORK/out/$n.dll" ]; then echo "-r:\"$WORK/out/$n.dll\""
      elif [ -f "$LIB/$n.dll" ];      then echo "-r:\"$LIB/$n.dll\""
      fi
    done < "$WORK/plan.tsv"
    cat "$WORK/common.rsp"; } > "$rsp"

  def="-define:UNITY_2021_1_OR_NEWER"
  [ -n "$defines" ] && def="$def;${defines//,/;}"
  # ИМЯ ВЫХОДА = ИМЯ СБОРКИ. Из него Roslyn берёт имя ассембли, а по нему
  # проверяется InternalsVisibleTo: «Lvn.Engine.Tests.built» грант не получит.
  errs="$("$DOTNET" "$CSC" -nologo -nostdlib -noconfig -target:library \
      -nowarn:CS1998,CS0649,CS0414,CS0169 "$def" -out:"$WORK/out/$name.dll" "@$rsp" $srcs \
      </dev/null 2>&1 | grep -E "error CS" | head -8)"
  if [ -n "$errs" ]; then
    fail=1
    printf '✗ %-28s\n%s\n' "$name" "$errs"
  else
    printf '✓ %-28s %s файлов\n' "$name" "$(echo $srcs | wc -w | tr -d ' ')"
  fi
done < "$WORK/plan.tsv"

[ "$fail" = 0 ] && echo "C# собирается" || echo "C# НЕ СОБИРАЕТСЯ"
exit $fail
