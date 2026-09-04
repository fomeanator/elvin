#!/usr/bin/env bash
# ДВА УСТРОЙСТВА, ОДИН АККАУНТ: ВТОРОЕ НЕ ДОЛЖНО СТЕРЕТЬ ПЕРВОЕ.
#
# Серверная половина замерена отдельно (qa/state-durability-check.sh): OCC
# держит, 40 писателей — 0 потерь, а клиент БЕЗ версии теряет 39 из 40. Там же
# осталась честно названная граница: наш клиент шлёт версию только если знает
# её, а знает — после успешного чтения. Достижимо ли «сохранить, не прочитав» в
# живом клиенте, из серверного стенда не видно.
#
# Здесь эта граница закрывается: настоящий сервер и НАСТОЯЩИЙ КЛИЕНТ
# (HttpStateStore из пакета движка) в пустом проекте с одним пакетом.
#
# Меряются два пути, и оба нужны:
#   ОБЫЧНЫЙ  второе устройство открывает новеллу — то есть ЧИТАЕТ, потом пишет.
#            Прогресс первого обязан уцелеть. Это путь оболочки.
#   ГОНКА    второе устройство пишет, НЕ прочитав. Так выглядит синхронизация,
#            успевшая выстрелить раньше загрузки. Здесь замеряется ущерб, а не
#            выносится приговор: цифра важнее слова «возможно».
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ: тестов ровно два, и оба обязаны отработать.
# Ноль тестов при нуле провалов — самый частый способ соврать зелёным.
#
#   qa/two-devices-check.sh
set -uo pipefail
cd "$(dirname "$0")/.."
REPO="$PWD"

command -v go >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
UNITY="$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app 2>/dev/null | sort -V | tail -1)"
[ -n "$UNITY" ] || { echo "нет Unity — пропускаю"; exit 0; }
VER="$(basename "$(dirname "$UNITY")")"

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT="${LVN_PORT:-8077}"
probe() { curl -fsS -m 1 "http://127.0.0.1:$1/healthz" >/dev/null 2>&1; }
if probe "$PORT"; then
  PORT=0
  for p in 8078 8079 8081 8082; do probe "$p" || { PORT=$p; break; }; done
  [ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }
fi
mkdir -p "$W/content"; echo '{"titles":[]}' > "$W/content/manifest.json"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

P="$W/proj"
mkdir -p "$P/Packages" "$P/ProjectSettings" "$P/Assets/DeviceTests"
echo "m_EditorVersion: $VER" > "$P/ProjectSettings/ProjectVersion.txt"
cat > "$P/Packages/manifest.json" <<EOF
{
  "dependencies": {
    "com.lvn.engine": "file:$REPO/unity/Packages/com.lvn.engine",
    "com.unity.test-framework": "1.4.5"
  }
}
EOF
cat > "$P/Assets/DeviceTests/Device.Tests.asmdef" <<'EOF'
{
  "name": "Device.Tests",
  "references": ["Lvn.Engine", "Lvn.Engine.Content", "Unity.Nuget.Newtonsoft-Json",
                 "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
  "includePlatforms": ["Editor"],
  "overrideReferences": true,
  "precompiledReferences": ["nunit.framework.dll", "Newtonsoft.Json.dll"],
  "defineConstraints": ["UNITY_INCLUDE_TESTS"],
  "autoReferenced": false
}
EOF

cat > "$P/Assets/DeviceTests/TwoDevicesTests.cs" <<'EOF'
// Собран qa/two-devices-check.sh. Настоящий сервер, настоящий HttpStateStore.
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

public class TwoDevicesTests
{
    static string Base => Environment.GetEnvironmentVariable("LVN_STATE_BASE");
    const string Title = "shared-title";

    // «Свежее устройство»: локальная копия и запомненный токен версии живут в
    // экземпляре и в PlayerPrefs — оба надо обнулить, иначе второй store знал бы
    // то, чего настоящему второму телефону знать неоткуда.
    static HttpStateStore FreshDevice(string user)
    {
        LocalStateStore.Forget(Title);
        return new HttpStateStore(Base, user);
    }

    static IEnumerator Await(Task t)
    {
        while (!t.IsCompleted) yield return null;
        if (t.IsFaulted) throw t.Exception;
    }

    [UnityTest]
    public IEnumerator SecondDeviceThatOpensTheNovelKeepsTheFirstDevicesProgress()
    {
        Assert.That(Base, Is.Not.Null.And.Not.Empty, "адрес сервера не передан — мерить нечего");

        var a = FreshDevice("player1");
        yield return Await(a.SaveVarsAsync(Title, new JObject { ["золото"] = 100 }, CancellationToken.None));

        // Второе устройство делает то, что делает оболочка при открытии новеллы:
        // сперва ЧИТАЕТ (это и запоминает токен версии), потом пишет своё.
        var b = FreshDevice("player1");
        var load = b.LoadVarsAsync(Title, CancellationToken.None);
        yield return Await(load);
        Assert.That((int?)load.Result?["золото"], Is.EqualTo(100),
                    "прогресс первого устройства не доехал до второго");

        var merged = new JObject { ["золото"] = 100, ["глава"] = 5 };
        yield return Await(b.SaveVarsAsync(Title, merged, CancellationToken.None));

        var c = FreshDevice("player1");
        var back = c.LoadVarsAsync(Title, CancellationToken.None);
        yield return Await(back);
        Assert.That((int?)back.Result?["золото"], Is.EqualTo(100), "золото первого устройства стёрто");
        Assert.That((int?)back.Result?["глава"], Is.EqualTo(5), "правка второго устройства не сохранилась");
    }

    [UnityTest]
    public IEnumerator WritingWithoutReadingFirstIsMeasuredNotAssumed()
    {
        Assert.That(Base, Is.Not.Null.And.Not.Empty);
        const string T2 = "race-title";

        var a = new HttpStateStore(Base, "player2");
        LocalStateStore.Forget(T2);
        yield return Await(a.SaveVarsAsync(T2, new JObject { ["золото"] = 100 }, CancellationToken.None));

        // Устройство, которое НЕ читало: ровно так выглядит синхронизация,
        // выстрелившая раньше загрузки.
        LocalStateStore.Forget(T2);
        var b = new HttpStateStore(Base, "player2");
        yield return Await(b.SaveVarsAsync(T2, new JObject { ["глава"] = 9 }, CancellationToken.None));

        LocalStateStore.Forget(T2);
        var c = new HttpStateStore(Base, "player2");
        var back = c.LoadVarsAsync(T2, CancellationToken.None);
        yield return Await(back);

        bool survived = (int?)back.Result?["золото"] == 100;
        UnityEngine.Debug.Log("[стенд] запись без чтения: золото " +
                              (survived ? "УЦЕЛЕЛО" : "СТЁРТО") +
                              ", документ = " + back.Result?.ToString(Newtonsoft.Json.Formatting.None));
        // Приговор не выносится — замеряется. Тест обязан лишь дойти до конца и
        // назвать исход; трактовка живёт в docs/world-position.md.
        Assert.That(back.Result, Is.Not.Null, "документ пропал целиком — это уже поломка");
    }
}
EOF

LOG="$W/unity.log"; RES="$W/results.xml"
LVN_STATE_BASE="http://127.0.0.1:$PORT" "$UNITY/Contents/MacOS/Unity" \
  -batchmode -projectPath "$P" -runTests -testPlatform EditMode \
  -testResults "$RES" -logFile "$LOG" -nographics >/dev/null 2>&1

errs=$(grep -c "error CS" "$LOG" 2>/dev/null || true); errs=${errs:-0}
read -r total failed < <(python3 - "$RES" <<'PY' 2>/dev/null || echo "0 0"
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
print(r.get('total') or 0, r.get('failed') or 0)
PY
)
echo "ошибок компиляции: $errs"
echo "тестов: $total, провалов: $failed"
grep -o "\[стенд\] .*" "$LOG" | tail -1 | sed 's/^/  /'
[ "$errs" -gt 0 ] && grep -m3 "error CS" "$LOG" | sed 's|.*Assets/|  Assets/|'
if [ "$failed" != "0" ]; then
  python3 - "$RES" <<'PY' 2>/dev/null
import sys, xml.etree.ElementTree as ET
for tc in ET.parse(sys.argv[1]).getroot().iter('test-case'):
    if tc.get('result') != 'Passed':
        f = tc.find('failure')
        print("  ПРОВАЛ", tc.get('name'))
        if f is not None: print("   ", (f.findtext('message') or '').strip()[:400])
PY
fi
if [ "$errs" = "0" ] && [ "$total" = "2" ] && [ "$failed" = "0" ]; then
  echo "ДВА УСТРОЙСТВА — прогресс первого цел"; exit 0
fi
echo "ПРОВЕРКА НЕ ПОДТВЕРДИЛА"; exit 1
