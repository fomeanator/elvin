#!/usr/bin/env bash
# ПУТЬ АВТОРА В РЕДАКТОРЕ: ПОЛОЖИЛ ФАЙЛ — ИГРАЕТСЯ. ПРОВЕРКА ИМПОРТОМ.
#
# README обещает это первой же строкой: «Drop a .lvns script into Assets/ — it
# compiles automatically — and the runtime plays it as a real game». Это первое,
# что делает новичок, и до сих пор оно не проверялось ни разу.
#
# ЧТО УЖЕ ПРОВЕРЕНО ДРУГИМИ, И ПОЧЕМУ ЭТОГО НЕ ХВАТАЕТ. Сверка портов
# (qa/compiler-parity.sh) запускает C#-компилятор ВНЕ Unity, отдельным
# исполняемым файлом: она отвечает за компилятор, но не за импортёр. Цепочка
# «файл в Assets/ → ScriptedImporter → TextAsset → LvnPlayer» не прогонялась.
#
# ЧТО ИМЕННО УТВЕРЖДАЕТСЯ, по частям:
#   импорт     редактор сам замечает .lvns и делает из него TextAsset
#   include    вклейка резолвится ОТНОСИТЕЛЬНО ФАЙЛА — ради этого импортёр
#              зовёт CompileFile(path), а не Compile(text); проверяется тем,
#              что в сыгранной главе звучит реплика из вклеенного файла
#   игра       LvnPlayer доигрывает импортированное до конца через развилку
#   единство   импортированный JSON совпадает с тем, что даёт lvnconv на том
#              же исходнике — автор видит ровно то, что уедет игроку
#
# УКУС (-bite) бьёт в самое опасное место обещания. Битый по СТРУКТУРЕ скрипт
# (переход на несуществующую метку) компилируется без единой синтаксической
# ошибки: у автора он молча превращается в главу, которая «просто кончилась».
# Ловить это обязан LvnsStructureCheck на импорте — и укус требует, чтобы
# редактор об этом КРИЧАЛ. Зелёный прогон без укуса означал бы лишь, что мы
# ничего не заметили.
#
# Прогон долгий (импорт чистого проекта), поэтому он НЕ в qa/run-all.sh.
#
#   qa/editor-authoring-check.sh [-bite]
set -uo pipefail
cd "$(dirname "$0")/.."
REPO="$PWD"
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

UNITY="$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app 2>/dev/null | sort -V | tail -1)"
[ -n "$UNITY" ] || { echo "нет Unity — пропускаю"; exit 0; }
VER="$(basename "$(dirname "$UNITY")")"   # .../Hub/Editor/<версия>/Unity.app
command -v go >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }

P="$(mktemp -d)"; trap 'rm -rf "$P"' EXIT
mkdir -p "$P/Packages" "$P/ProjectSettings" "$P/Assets/Story" "$P/Assets/AuthoringTests"
echo "m_EditorVersion: $VER" > "$P/ProjectSettings/ProjectVersion.txt"

cat > "$P/Packages/manifest.json" <<EOF
{
  "dependencies": {
    "com.lvn.engine": "file:$REPO/unity/Packages/com.lvn.engine",
    "com.unity.test-framework": "1.4.5"
  }
}
EOF

# Вклеиваемый файл. Он же импортируется сам по себе — поэтому обязан быть
# валидным и в одиночку, иначе его собственные ошибки смешались бы с укусом.
cat > "$P/Assets/Story/lines.lvns" <<'EOF'
// Подключаемый файл не объявляет сцену и не играется сам — так же устроен
// свидетель include в howto/every-command/endings.lvns.
:from_the_included_file
Мира: Эта реплика пришла из вклеенного файла.
EOF

cat > "$P/Assets/Story/chapter.lvns" <<'EOF'
scene chapter

bg room
Мира: Положили файл в проект.
- Спросить -> ask
- Промолчать -> hush

:ask
goto from_the_included_file

:hush
Мира: Тишина тоже ответ.
goto __end

// ВКЛЕЙКА ИДЁТ НА МЕСТО ДИРЕКТИВЫ, а метка исполнение не останавливает —
// поэтому include стоит в конце, за `goto __end`. Стой он вверху, вклеенные
// строки сыграли бы первыми, и тест проверял бы не то (поймано прогоном).
include "lines.lvns"
EOF

if [ -n "$BITE" ]; then
  # Синтаксис безупречен, метки не существует. Компилятор промолчит —
  # закричать обязан LvnsStructureCheck.
  cat > "$P/Assets/Story/chapter.lvns" <<'EOF'
scene chapter

bg room
Мира: Положили файл в проект.
goto label_that_does_not_exist
EOF
fi

# Эталон командной строки — на ТОМ ЖЕ файле, тем же компилятором, что уезжает
# на прод. Сравнение с ним и есть проверка единства реализаций на пути автора.
go run ./tools/lvnconv convert -i "$P/Assets/Story/chapter.lvns" -o "$P/cli.lvn" >/dev/null 2>&1
[ -s "$P/cli.lvn" ] || { [ -n "$BITE" ] || { echo "lvnconv не собрал эталон"; exit 1; }; echo '{}' > "$P/cli.lvn"; }

cat > "$P/Assets/AuthoringTests/Authoring.Tests.asmdef" <<'EOF'
{
  "name": "Authoring.Tests",
  "references": ["Lvn.Engine", "Unity.Nuget.Newtonsoft-Json",
                 "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
  "includePlatforms": ["Editor"],
  "overrideReferences": true,
  "precompiledReferences": ["nunit.framework.dll", "Newtonsoft.Json.dll"],
  "defineConstraints": ["UNITY_INCLUDE_TESTS"],
  "autoReferenced": false
}
EOF

python3 - "$P/cli.lvn" "$P/Assets/AuthoringTests/DroppedScriptPlaysTests.cs" <<'PY'
import json, sys
cli = open(sys.argv[1], encoding="utf-8").read()
open(sys.argv[2], "w", encoding="utf-8").write('''// Собран qa/editor-authoring-check.sh. Скрипт лежит в Assets/ ИСХОДНИКОМ —
// компилирует его сам редактор, а не подготовленная заранее строка.
using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class DroppedScriptPlaysTests
{
    const string Path = "Assets/Story/chapter.lvns";
    // Что на этом же файле даёт lvnconv — источник правды для прода.
    const string FromTheCommandLine = ''' + json.dumps(cli, ensure_ascii=False) + ''';

    sealed class Recorder : ILvnStage
    {
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Stage = new List<string>();
        public IReadOnlyList<LvnOption> Options;
        public bool Ended;

        public void ShowSay(string who, string text, string style) => Lines.Add(text);
        public void ShowChoice(IReadOnlyList<LvnOption> options) => Options = options;
        public void ApplyStage(JObject command) => Stage.Add((string)command["op"]);
        public void ApplyStage(JObject command, LvnSender sender) => ApplyStage(command);
        public void OnEnd() => Ended = true;
    }

    static TextAsset Imported()
    {
        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(Path);
        Assert.That(asset, Is.Not.Null, "редактор не сделал из .lvns ничего — импортёр не сработал");
        return asset;
    }

    [Test]
    public void EditorCompilesTheDroppedScriptItself()
    {
        var text = Imported().text;
        // Отказ импортёра выглядит как ПУСТАЯ, но валидная глава. Не сверив с
        // ней, тест зеленел бы на молчаливом провале компиляции.
        Assert.That(text.Replace(" ", ""), Is.Not.EqualTo("{\\"script\\":[]}"),
                    "импорт свалился в пустую заглушку — компиляция не прошла");
        Assert.That(JObject.Parse(text)["script"], Is.Not.Null);
    }

    [Test]
    public void ImportedChapterPlaysThroughItsBranchAndInclude()
    {
        var stage  = new Recorder();
        var player = new LvnPlayer(LvnDocument.Parse(Imported().text), stage);

        player.Advance();
        Assert.That(stage.Stage, Does.Contain("bg"), "постановка не доехала");
        Assert.That(stage.Lines, Does.Contain("Положили файл в проект."));

        player.Advance();
        Assert.That(stage.Options, Is.Not.Null, "развилка не показана");

        player.Choose(0);   // Choose только ставит позицию, играет Advance
        player.Advance();

        // Реплика живёт в ДРУГОМ файле и попала сюда только через include,
        // резолвленный относительно пути импортируемого файла.
        Assert.That(stage.Lines, Does.Contain("Эта реплика пришла из вклеенного файла."),
                    "include не резолвился — вклеенного файла в главе нет");

        for (int i = 0; i < 8 && !stage.Ended; i++) player.Advance();
        Assert.That(stage.Ended, Is.True, "глава не доиграна до конца");
    }

    [Test]
    public void EditorAndCommandLineAgreeOnTheSameFile()
    {
        // Автор правит в редакторе, игрок получает собранное lvnconv. Разойдись
        // они — глава вела бы себя по-разному, и никто бы не упал.
        var mine  = JObject.Parse(Imported().text);
        var yours = JObject.Parse(FromTheCommandLine);
        Assert.That(JToken.DeepEquals(mine, yours), Is.True,
                    "редакторный импорт и lvnconv собрали ОДИН файл ПО-РАЗНОМУ:\\n" +
                    "редактор: " + mine.ToString(Newtonsoft.Json.Formatting.None) + "\\n" +
                    "lvnconv:  " + yours.ToString(Newtonsoft.Json.Formatting.None));
    }
}
''')
PY

LOG="$P/unity.log"; RES="$P/results.xml"
"$UNITY/Contents/MacOS/Unity" -batchmode -projectPath "$P" -runTests \
  -testPlatform EditMode -testResults "$RES" -logFile "$LOG" -nographics >/dev/null 2>&1
errs=$(grep -c "error CS" "$LOG" 2>/dev/null || true); errs=${errs:-0}

if [ -n "$BITE" ]; then
  said=$(grep -c "LVNScript in chapter.lvns" "$LOG" 2>/dev/null || true); said=${said:-0}
  echo "УКУС: жалоб импортёра на висячий переход — $said (ждём больше нуля)"
  grep -m1 "LVNScript in chapter.lvns" "$LOG" | sed 's/^/  /'
  [ "$said" -gt 0 ] && { echo "редактор кричит: висячий переход пойман на импорте"; exit 0; }
  echo "РЕДАКТОР ПРОМОЛЧАЛ — глава «просто кончится» у игрока"; exit 2
fi

read -r total failed < <(python3 - "$RES" <<'PY' 2>/dev/null || echo "0 0"
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
print(r.get('total') or 0, r.get('failed') or 0)
PY
)
echo "ошибок компиляции: $errs"
echo "тестов: $total, провалов: $failed"
[ "$errs" -gt 0 ] && grep -m3 "error CS" "$LOG" | sed 's|.*Assets/|  Assets/|'
if [ "$failed" != "0" ]; then
  python3 - "$RES" <<'PY' 2>/dev/null
import sys, xml.etree.ElementTree as ET
for tc in ET.parse(sys.argv[1]).getroot().iter('test-case'):
    if tc.get('result') != 'Passed':
        f = tc.find('failure')
        print("  ПРОВАЛ", tc.get('name'))
        if f is not None: print("   ", (f.findtext('message') or '').strip()[:600])
PY
fi
if [ "$errs" = "0" ] && [ "$total" = "3" ] && [ "$failed" = "0" ]; then
  echo "ПОЛОЖИЛ ФАЙЛ — ИГРАЕТСЯ; редактор и lvnconv согласны"; exit 0
fi
echo "ОБЕЩАНИЕ НЕ ПОДТВЕРЖДЕНО"; exit 1
