#!/usr/bin/env bash
# ОДИН ПАКЕТ — ЦЕЛАЯ ИГРА. ПРОВЕРКА УСТАНОВКОЙ, А НЕ ОБЪЯВЛЕНИЕМ.
#
# Довод, которым движок отвечает Ink на вопрос о встраиваемости: команда с
# готовой игрой на Unity ставит ОДИН пакет `com.lvn.engine` и получает язык,
# плеер и постановку — без хаба, магазина и учёток.
#
# До этой проверки довод держался на списке `references` в asmdef, то есть на
# обещании пакета о самом себе. А 2090 зелёных тестов его не проверяли вовсе:
# сборки тестов ядра ссылаются на `Lvn.Engine.Shell` и `Lvn.Engine.Services`,
# то есть идут в проекте, где оболочка и сервисы УЖЕ стоят.
#
# Здесь собирается пустой проект, куда кладётся ровно один пакет — ни оболочки,
# ни сервисов, ни ktx, — и в нём играется глава с развилкой.
#
# ЧТО ИМЕННО УТВЕРЖДАЕТСЯ, по частям:
#   язык      скрипт компилируется lvnconv прямо здесь, а не берётся готовым
#   плеер     LvnPlayer доигрывает главу до конца, пройдя развилку
#   постановка Lvn.UI.VnStage разрешается — значит сборка UI собралась тоже
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ. Флаг -bite подкладывает в тест обращение к
# пространству оболочки: если стенд честный, проект перестаёт компилироваться.
# Зелёный прогон без такой проверки означал бы только «мы ничего не заметили».
#
# Прогон долгий (импорт чистого проекта, ~1-2 мин) — поэтому он НЕ входит в
# qa/run-all.sh. Дешёвый страж той же границы живёт рядом и идёт с каждым
# `go test`: tools/lvnconv/lvn/solo_package_guard_test.go.
#
#   qa/solo-package-check.sh [-bite]
set -uo pipefail
cd "$(dirname "$0")/.."
REPO="$PWD"
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

UNITY="$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app 2>/dev/null | sort -V | tail -1)"
[ -n "$UNITY" ] || { echo "нет Unity — пропускаю"; exit 0; }
VER="$(basename "$(dirname "$UNITY")")"   # .../Hub/Editor/<версия>/Unity.app

P="$(mktemp -d)"; trap 'rm -rf "$P"' EXIT
mkdir -p "$P/Packages" "$P/ProjectSettings" "$P/Assets/SoloTests"
echo "m_EditorVersion: $VER" > "$P/ProjectSettings/ProjectVersion.txt"

# Ровно один наш пакет. Newtonsoft и ugui приедут сами — они объявлены его
# зависимостями, и в этом половина проверки: манифест обязан быть полным.
cat > "$P/Packages/manifest.json" <<EOF
{
  "dependencies": {
    "com.lvn.engine": "file:$REPO/unity/Packages/com.lvn.engine",
    "com.unity.test-framework": "1.4.5"
  }
}
EOF

cat > "$P/Assets/SoloTests/Solo.Tests.asmdef" <<'EOF'
{
  "name": "Solo.Tests",
  "references": ["Lvn.Engine", "Lvn.Engine.UI", "Unity.Nuget.Newtonsoft-Json",
                 "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
  "includePlatforms": ["Editor"],
  "overrideReferences": true,
  "precompiledReferences": ["nunit.framework.dll", "Newtonsoft.Json.dll"],
  "defineConstraints": ["UNITY_INCLUDE_TESTS"],
  "autoReferenced": false
}
EOF

# Скрипт компилируется ЗДЕСЬ И СЕЙЧАС: заготовленный .lvn протух бы молча, а
# так проверка идёт от исходного текста через настоящий компилятор.
cat > "$P/story.lvns" <<'EOF'
scene alone

bg room
Мира: Один пакет.
- Спросить -> ask
- Промолчать -> hush

:ask
Мира: Язык, плеер и постановка.
goto tail

:hush
Мира: Тишина тоже ответ.

:tail
Мира: Конец.
EOF
go run ./tools/lvnconv convert -i "$P/story.lvns" -o "$P/story.lvn" >/dev/null 2>&1 \
  || { echo "компилятор не собрал скрипт"; exit 1; }

python3 - "$P/story.lvn" "$P/Assets/SoloTests/OnePackagePlaysTests.cs" "${BITE:-}" <<'PY'
import json, sys
lvn = open(sys.argv[1], encoding="utf-8").read()
bite = "using Lvn.UI.Screens;  // УКУС\n" if len(sys.argv) > 3 and sys.argv[3] else ""
open(sys.argv[2], "w", encoding="utf-8").write('''// Собран qa/solo-package-check.sh. Проект содержит ТОЛЬКО com.lvn.engine.
using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
''' + bite + '''
public class OnePackagePlaysTests
{
    const string Lvn = ''' + json.dumps(lvn, ensure_ascii=False) + ''';

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

    [Test]
    public void CorePackageAlonePlaysAChapterWithABranch()
    {
        var stage  = new Recorder();
        var player = new LvnPlayer(LvnDocument.Parse(Lvn), stage);

        player.Advance();
        Assert.That(stage.Stage, Does.Contain("bg"), "постановка не доехала: фон не поставлен");
        Assert.That(stage.Lines, Does.Contain("Один пакет."));

        player.Advance();
        Assert.That(stage.Options, Is.Not.Null, "развилка не показана");
        Assert.That(stage.Options.Count, Is.EqualTo(2));

        // Договор Choose: он ТОЛЬКО ставит следующую позицию, играет — Advance.
        player.Choose(0);
        player.Advance();
        Assert.That(stage.Lines, Does.Contain("Язык, плеер и постановка."));
        Assert.That(stage.Lines, Does.Not.Contain("Тишина тоже ответ."),
                    "выбран первый путь, а сыграна невыбранная ветка");

        for (int i = 0; i < 8 && !stage.Ended; i++) player.Advance();
        Assert.That(stage.Lines, Does.Contain("Конец."));
        Assert.That(stage.Ended, Is.True, "глава не доиграна до конца");
    }

    [Test]
    public void StagingShipsInTheSamePackage()
    {
        // Готовая сцена — половина довода: без неё «один пакет» давал бы язык и
        // плеер, но рисовать пришлось бы самому. Ссылка на тип разрешается на
        // компиляции — значит сборка Lvn.Engine.UI собралась в этом проекте.
        Assert.That(typeof(ILvnStage).IsAssignableFrom(typeof(Lvn.UI.VnStage)), Is.True,
                    "VnStage не реализует ILvnStage");
    }
}
''')
PY

LOG="$P/unity.log"; RES="$P/results.xml"
"$UNITY/Contents/MacOS/Unity" -batchmode -projectPath "$P" -runTests \
  -testPlatform EditMode -testResults "$RES" -logFile "$LOG" -nographics >/dev/null 2>&1
errs=$(grep -c "error CS" "$LOG" 2>/dev/null || true); errs=${errs:-0}

if [ -n "$BITE" ]; then
  echo "УКУС: ошибок компиляции $errs (ждём больше нуля)"
  grep -m1 "error CS" "$LOG" | sed 's|.*Assets/|  Assets/|'
  [ "$errs" -gt 0 ] && { echo "стенд честный: без оболочки проект не собирается"; exit 0; }
  echo "СТЕНД ВРЁТ: оболочка оказалась доступна там, где её быть не должно"; exit 2
fi

# Числа, а не пустой экран: ноль тестов — это провал, а не тишина.
read -r total failed < <(python3 - "$RES" <<'PY' 2>/dev/null || echo "0 0"
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
print(r.get('total') or 0, r.get('failed') or 0)
PY
)
echo "ошибок компиляции: $errs"
echo "тестов: $total, провалов: $failed"
if [ "$errs" -gt 0 ]; then grep -m3 "error CS" "$LOG" | sed 's|.*Assets/|  Assets/|'; fi
if [ "$errs" = "0" ] && [ "$total" = "2" ] && [ "$failed" = "0" ]; then
  echo "ОДИН ПАКЕТ ИГРАЕТ ГЛАВУ — довод держится"; exit 0
fi
echo "ДОВОД НЕ ПОДТВЕРЖДЁН"; exit 1
