#!/usr/bin/env bash
# ПРОВЕРКА C# БЕЗ UNITY — компиляция сборок движка на Roslyn из самого редактора.
#
# Зачем: правка в C# до сих пор проверялась только запуском Unity, а он занят
# (открыт проект, идёт импорт) или просто долог — и опечатка в рантайме
# обнаруживалась через минуты. Между тем компилятор лежит внутри редактора, а
# ссылочные сборки — и в редакторе, и в Library/ScriptAssemblies уже собранного
# проекта. Этого хватает, чтобы ответить за секунды.
#
# Что это НЕ заменяет: прогон тестов (qa/run-all.sh). Здесь проверяется, что
# код СОБИРАЕТСЯ, а не что он работает.
#
#   qa/csharp-check.sh [проект-с-Library]     (по умолчанию ~/ominis/timeromance/app)
set -uo pipefail
cd "$(dirname "$0")/.."

APP="${1:-$HOME/ominis/timeromance/app}"
UNITY="$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app 2>/dev/null | sort -V | tail -1)"
[ -n "$UNITY" ] || { echo "нет Unity — пропускаю"; exit 0; }

DOTNET="$UNITY/Contents/Resources/Scripting/NetCoreRuntime/dotnet"
CSC="$UNITY/Contents/Resources/Scripting/DotNetSdkRoslyn/csc.dll"
FW="$(ls -d "$UNITY/Contents/Resources/Scripting/NetCoreRuntime/shared/Microsoft.NETCore.App"/* | tail -1)"
UE="$UNITY/Contents/Resources/Scripting/Managed/UnityEngine"
LIB="$APP/Library/ScriptAssemblies"
for p in "$DOTNET" "$CSC" "$FW" "$UE" "$LIB"; do
  [ -e "$p" ] || { echo "нет $p — пропускаю (нужен собранный проект: $APP)"; exit 0; }
done

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
fail=0

for asmdef in $(find unity/Packages -name "*.asmdef" | sort); do
  dir="$(dirname "$asmdef")"
  name="$(basename "$asmdef" .asmdef)"
  # Сборка владеет своим каталогом ВСЕМ, кроме подкаталогов с собственным asmdef.
  mapfile -t srcs < <(find "$dir" -name "*.cs" \
    -not -path "*/$(basename "$dir")/*/*asmdef*" | while read -r f; do
      d="$(dirname "$f")"
      # ближайший вверх asmdef должен быть нашим
      while [ "$d" != "$dir" ] && [ "$d" != "." ]; do
        if ls "$d"/*.asmdef >/dev/null 2>&1; then break; fi
        d="$(dirname "$d")"
      done
      [ "$d" = "$dir" ] && echo "$f"
    done)
  [ "${#srcs[@]}" -gt 0 ] || continue

  # Сборка, чья ссылка не установлена в проекте-эталоне (необязательный
  # пакет вроде Addressables), не проверяется — это не поломка кода, а
  # отсутствие зависимости, и молчаливый «провал» тут врал бы.
  missing="$(python3 - "$asmdef" "$LIB" <<'PYEOF'
import json, os, sys, glob
a = json.load(open(sys.argv[1])); lib = sys.argv[2]
have = {os.path.basename(p)[:-4] for p in glob.glob(os.path.join(lib, "*.dll"))}
have |= {os.path.basename(p)[:-7] for p in glob.glob("unity/Packages/*/**/*.asmdef", recursive=True)}
# Кэш пакетов: имя сборки в asmdef и имя файла .dll совпадают не всегда
# (Unity.Nuget.Newtonsoft-Json лежит как Newtonsoft.Json.dll).
cache = os.path.join(os.path.dirname(os.path.dirname(lib)), "Library", "PackageCache")
for p in glob.glob(os.path.join(cache, "**", "*.dll"), recursive=True):
    have.add(os.path.basename(p)[:-4])
have |= {"Unity.Nuget.Newtonsoft-Json"} if "Newtonsoft.Json" in have else set()
print(" ".join(r for r in (a.get("references") or [])
                if not r.startswith("GUID:") and r not in have))
PYEOF
)"
  if [ -n "$missing" ]; then
    printf '· %-28s пропуск: нет ссылок (%s)\n' "$name" "$missing"
    continue
  fi

  rsp="$WORK/$name.rsp"
  { for d in "$FW"/*.dll; do echo "-r:\"$d\""; done
    for d in "$UE"/*.dll; do echo "-r:\"$d\""; done
    for d in "$LIB"/*.dll; do
      [ "$(basename "$d" .dll)" = "$name" ] && continue
      echo "-r:\"$d\""
    done
    # Сборки пакетов (NUnit для тестов, Newtonsoft, KTX2) — по одной на имя:
    # один и тот же .dll лежит в кэше под несколькими редакциями.
    python3 - "$APP" <<'PYEOF'
import glob, os, sys
seen = {}
for p in glob.glob(os.path.join(sys.argv[1], "Library", "PackageCache", "**", "*.dll"), recursive=True):
    seen.setdefault(os.path.basename(p), p)
for p in sorted(seen.values()):
    print(f'-r:"{p}"')
PYEOF
    # Редакторные сборки нужны Editor-коду и тестам.
    for d in "$UNITY/Contents/Managed/UnityEngine"/*.dll "$UNITY/Contents/Managed"/UnityEditor*.dll; do
      [ -f "$d" ] && echo "-r:\"$d\""
    done
  } > "$rsp"

  # Флаги сборки берём из asmdef: без них код под #if LVN_KTX2 не проверяется.
  defines="-define:UNITY_2021_1_OR_NEWER"
  while read -r d; do defines="$defines;$d"; done < <(
    python3 -c "
import json,sys
a=json.load(open('$asmdef'))
for v in a.get('versionDefines') or []:
    if v.get('define'): print(v['define'])" 2>/dev/null)

  # ИМЯ ВЫХОДА = ИМЯ СБОРКИ. Из него Roslyn берёт имя ассембли, а по нему
  # проверяется InternalsVisibleTo: "Lvn.Engine.Tests.built" грант не получит.
  mkdir -p "$WORK/out"
  out="$WORK/out/$name.dll"
  errs="$("$DOTNET" "$CSC" -nologo -nostdlib -noconfig -target:library -nowarn:CS1998,CS0649,CS0414,CS0169 \
      "$defines" -out:"$out" "@$rsp" "${srcs[@]}" 2>&1 | grep -E ": error " | head -12)"
  if [ -n "$errs" ]; then
    fail=1
    printf '✗ %-28s\n%s\n' "$name" "$errs"
  else
    printf '✓ %-28s %s файлов\n' "$name" "${#srcs[@]}"
  fi
done

[ "$fail" = 0 ] && echo "C# собирается" || echo "C# НЕ СОБИРАЕТСЯ"
exit $fail
