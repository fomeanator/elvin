#!/usr/bin/env bash
# СВЕРКА ДВУХ КОМПИЛЯТОРОВ ЯЗЫКА на живом корпусе .lvns.
#
# Реализаций языка в проекте несколько, и это главный структурный риск: Go
# (lvnconv, источник правды) собирает то, что уезжает на прод, а C#-порт —
# то, что видит автор, импортируя .lvns прямо в Unity. Расхождение между ними
# не падает и не логируется: глава просто ведёт себя иначе у автора и у
# игрока. Стражи проверяли это ЧТЕНИЕМ исходника C# регулярками из Go — то
# есть проверяли похожесть текста, а не одинаковость вывода.
#
# Здесь оба компилятора реально ЗАПУСКАЮТСЯ на одних и тех же файлах, и
# сравнивается разобранный JSON. Порядок ключей не считается расхождением,
# отказ обоих — тоже (файл может быть намеренно битым). Считается ровно то,
# что опасно: ОБА СОБРАЛИ, НО ПО-РАЗНОМУ.
#
# Громкий отказ C#-порта расхождением НЕ считается: у редакторного импорта
# есть задекларированные границы (`ui`, `voice`, функции-выражения, пакеты),
# и он о них говорит ошибкой.
set -uo pipefail
cd "$(dirname "$0")/.."
REPO="$PWD"

UNITY="$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app 2>/dev/null | sort -V | tail -1)"
[ -n "$UNITY" ] || { echo "нет Unity — сверка портов пропущена"; exit 0; }
command -v go >/dev/null 2>&1 || { echo "нет go — сверка портов пропущена"; exit 0; }

DOTNET="$UNITY/Contents/Resources/Scripting/NetCoreRuntime/dotnet"
CSC="$UNITY/Contents/Resources/Scripting/DotNetSdkRoslyn/csc.dll"
FW="$(ls -d "$UNITY/Contents/Resources/Scripting/NetCoreRuntime/shared/Microsoft.NETCore.App"/* | tail -1)"
NJ="$(find "$UNITY/.." -name "Newtonsoft.Json.dll" 2>/dev/null | head -1)"
for p in "$DOTNET" "$CSC" "$FW" "$NJ"; do
  [ -e "$p" ] || { echo "нет $p — сверка портов пропущена"; exit 0; }
done

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT

# Go-сторона: обычный lvnconv.
go build -C tools/lvnconv -o "$WORK/lvnconv" . || { echo "lvnconv не собрался"; exit 1; }

# C#-сторона: порт компилятора не зависит от UnityEngine ВООБЩЕ (только
# System.* и Newtonsoft), поэтому собирается и запускается сам по себе.
cat > "$WORK/Runner.cs" <<'CSEOF'
using System; using System.IO; using Lvn.Editor;
static class Runner {
    static int Main(string[] a) {
        if (a.Length < 1) { Console.Error.WriteLine("нужен путь к .lvns"); return 2; }
        try { Console.Out.Write(LvnsCompiler.CompileFile(a[0])); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
CSEOF
{ for d in "$FW"/*.dll; do echo "-r:\"$d\""; done; echo "-r:\"$NJ\""; } > "$WORK/refs.rsp"
ED="unity/Packages/com.lvn.engine/Editor"
"$DOTNET" "$CSC" -nologo -nostdlib -noconfig -target:exe -out:"$WORK/port.dll" "@$WORK/refs.rsp" \
  "$ED/LvnsCompiler.cs" "$ED/LvnsCompiler.Expand.cs" "$ED/LvnsCompiler.Anim.cs" "$WORK/Runner.cs" \
  2>&1 | grep ": error " | head -5
[ -f "$WORK/port.dll" ] || { echo "C#-порт компилятора не собрался"; exit 1; }
cat > "$WORK/port.runtimeconfig.json" <<'JEOF'
{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}
JEOF
cp "$NJ" "$WORK/"

WORK="$WORK" DOTNET="$DOTNET" python3 - "$REPO" <<'PYEOF'
import json, os, subprocess, sys
work, dotnet, repo = os.environ["WORK"], os.environ["DOTNET"], sys.argv[1]
files = sorted(l for l in subprocess.run(
    ["find", ".", "-name", "*.lvns", "-not", "-path", "*/node_modules/*"],
    capture_output=True, text=True, cwd=repo).stdout.split("\n") if l)
same = diverged = 0
bad = []
for f in files:
    g = subprocess.run([f"{work}/lvnconv", "convert", "-i", f], capture_output=True, text=True, cwd=repo)
    c = subprocess.run([dotnet, f"{work}/port.dll", f], capture_output=True, text=True, cwd=repo)
    if g.returncode != 0 or c.returncode != 0:
        continue  # хоть один отказал — граница, а не расхождение
    try:
        gj, cj = json.loads(g.stdout), json.loads(c.stdout)
    except Exception as e:
        bad.append((f, f"вывод не разобрался: {e}")); continue
    if gj == cj:
        same += 1
        continue
    diverged += 1
    gs, cs = gj.get("script", []), cj.get("script", [])
    why = f"команд Go={len(gs)} C#={len(cs)}"
    for i in range(min(len(gs), len(cs))):
        if gs[i] != cs[i]:
            why = (f"команда {i}:\n      Go: {json.dumps(gs[i], ensure_ascii=False)[:160]}"
                   f"\n      C#: {json.dumps(cs[i], ensure_ascii=False)[:160]}")
            break
    bad.append((f, why))
print(f"сверка портов: совпало {same}, расхождений {diverged}")
for f, why in bad[:10]:
    print(f"  ✗ {f}\n      {why}")
sys.exit(1 if bad else 0)
PYEOF
