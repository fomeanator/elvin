#!/usr/bin/env bash
# СБРОС НЕ ВОСКРЕСАЕТ, А ЧУЖОЙ СБРОС НЕ СЪЕДАЕТ МОЮ ИГРУ.
#
# У хранилища переменных НЕТ глагола удаления, и это осознанно: сброс статов —
# это запись ПУСТОГО набора. Докблок обряда сам называет способ сломаться:
# «удали запись — и следующая синхронизация с другого устройства вернёт старые
# статы как более свежие». Воскресшее удаление — классическая поломка любой
# синхронизации, и проверяется она только двумя настоящими устройствами.
#
# Обещаний здесь два, и они тянут в РАЗНЫЕ стороны — потому и проверяются вместе:
#
#   СБРОС ДЕРЖИТСЯ   игрок сбросил новеллу на телефоне; планшет, который с тех
#                    пор ничего не менял, обязан увидеть пустоту, а не вернуть
#                    старое;
#   ИГРА ДЕРЖИТСЯ    планшет играл БЕЗ СЕТИ после последней сходимости; чужой
#                    сброс не смеет съесть эту сессию.
#
# Одно правило обязано давать оба ответа. Правило такое: сведение стартует с
# ЧУЖОГО документа и накладывает только те ключи, которые ЭТО устройство меняло
# с последней сходимости. Первый случай — не меняло, значит чужая пустота
# побеждает. Второй — меняло, значит своё переживает.
#
# ДВА УСТРОЙСТВА В ОДНОМ ПРОЦЕССЕ ДЕЛЯТ PlayerPrefs, и без разведения локальных
# копий стенд мерил бы одно устройство дважды — то есть всегда «сходится».
# Поэтому у каждого устройства свой снимок локальной копии и своей базы, и он
# подставляется на время его хода.
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ: тестов ровно два, и оба обязаны отработать.
#
# УКУС (-bite) выключает разведение устройств. Стенд обязан покраснеть: зелёный
# с одной памятью на двоих означал бы, что он всё это время мерил ОДНО
# устройство дважды, и «сходится» у него по построению.
#
#   qa/wipe-sync-check.sh [-bite]
set -uo pipefail
cd "$(dirname "$0")/.."
REPO="$PWD"
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

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
mkdir -p "$P/Packages" "$P/ProjectSettings" "$P/Assets/WipeTests"
echo "m_EditorVersion: $VER" > "$P/ProjectSettings/ProjectVersion.txt"
cat > "$P/Packages/manifest.json" <<EOF
{
  "dependencies": {
    "com.lvn.engine": "file:$REPO/unity/Packages/com.lvn.engine",
    "com.unity.test-framework": "1.4.5"
  }
}
EOF
cat > "$P/Assets/WipeTests/Wipe.Tests.asmdef" <<'EOF'
{
  "name": "Wipe.Tests",
  "references": ["Lvn.Engine", "Lvn.Engine.Content", "Unity.Nuget.Newtonsoft-Json",
                 "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
  "includePlatforms": ["Editor"],
  "overrideReferences": true,
  "precompiledReferences": ["nunit.framework.dll", "Newtonsoft.Json.dll"],
  "defineConstraints": ["UNITY_INCLUDE_TESTS"],
  "autoReferenced": false
}
EOF

cat > "$P/Assets/WipeTests/WipeSyncTests.cs" <<'EOF'
// Собран qa/wipe-sync-check.sh. Настоящий сервер, настоящий HttpStateStore.
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Lvn;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class WipeSyncTests
{
    static string Base => Environment.GetEnvironmentVariable("LVN_STATE_BASE");
    // Укус приходит ОКРУЖЕНИЕМ, а не правкой файла: подделка не может уехать
    // в репозиторий вместе с настоящим стендом.
    static bool Bite => Environment.GetEnvironmentVariable("LVN_STAND_BITE") == "1";

    // ОТДЕЛЬНОЕ УСТРОЙСТВО. Локальная копия и база живут в PlayerPrefs, общих на
    // весь процесс; у настоящих телефонов они свои. Каждое устройство держит
    // СВОЙ снимок и подставляет его на время своего хода — иначе второй телефон
    // унаследовал бы память первого и стенд всегда «сходился» бы.
    sealed class Device
    {
        readonly string _local, _base;
        string _localVal = "", _baseVal = "";
        public readonly HttpStateStore Store;

        public Device(string title, string user)
        {
            _local = LvnKeep.Scoped("lvn_state_", title);
            _base  = LvnKeep.Scoped("lvn_state_base_", title);
            Store  = new HttpStateStore(Base, user);
        }
        public void Enter()
        {
            if (Bite) return;   // УКУС: устройства не разведены, память одна на двоих
            PlayerPrefs.SetString(_local, _localVal);
            PlayerPrefs.SetString(_base, _baseVal);
        }
        public void Leave()
        {
            if (Bite) return;
            _localVal = PlayerPrefs.GetString(_local, "");
            _baseVal  = PlayerPrefs.GetString(_base, "");
        }
    }

    static IEnumerator Await(Task t)
    {
        while (!t.IsCompleted) yield return null;
        if (t.IsFaulted) throw t.Exception;
    }

    [UnityTest]
    public IEnumerator WipeOnOneDeviceIsNotResurrectedByTheOther()
    {
        Assert.That(Base, Is.Not.Null.And.Not.Empty, "адрес сервера не передан");
        const string T = "wipe-title";
        LvnNetworkStatus.MarkOnline("стенд");
        var a = new Device(T, "p1");
        var b = new Device(T, "p1");

        a.Enter();
        yield return Await(a.Store.SaveVarsAsync(T, new JObject { ["золото"] = 100 }, CancellationToken.None));
        a.Leave();

        b.Enter();
        var seen = b.Store.LoadVarsAsync(T, CancellationToken.None);
        yield return Await(seen);
        Assert.That((int?)seen.Result?["золото"], Is.EqualTo(100), "прогресс не доехал до второго устройства");
        b.Leave();

        // Сброс: пустой набор вместо глагола удаления.
        a.Enter();
        yield return Await(a.Store.SaveVarsAsync(T, new JObject(), CancellationToken.None));
        a.Leave();

        b.Enter();
        var after = b.Store.LoadVarsAsync(T, CancellationToken.None);
        yield return Await(after);
        b.Leave();
        Debug.Log("[стенд] после сброса второе устройство видит: " +
                  (after.Result?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"));
        Assert.That((int?)after.Result?["золото"], Is.Null,
                    "СБРОС ВОСКРЕС: устройство, ничего не менявшее, вернуло стёртое");
    }

    [UnityTest]
    public IEnumerator OfflinePlaySurvivesTheOtherDevicesWipe()
    {
        Assert.That(Base, Is.Not.Null.And.Not.Empty);
        const string T = "airplane-title";
        LvnNetworkStatus.MarkOnline("стенд");
        var a = new Device(T, "p2");
        var b = new Device(T, "p2");

        a.Enter();
        yield return Await(a.Store.SaveVarsAsync(T, new JObject { ["золото"] = 100 }, CancellationToken.None));
        a.Leave();

        b.Enter();
        yield return Await(b.Store.LoadVarsAsync(T, CancellationToken.None)); // сходимость: база = 100
        // Играли в самолёте: локально записано, на сервер не ушло.
        LvnNetworkStatus.MarkOffline("стенд: самолёт");
        yield return Await(b.Store.SaveVarsAsync(T, new JObject { ["золото"] = 150 }, CancellationToken.None));
        LvnNetworkStatus.MarkOnline("стенд: сеть вернулась");
        b.Leave();

        a.Enter();
        yield return Await(a.Store.SaveVarsAsync(T, new JObject(), CancellationToken.None)); // чужой сброс
        a.Leave();

        b.Enter();
        var back = b.Store.LoadVarsAsync(T, CancellationToken.None);
        yield return Await(back);
        b.Leave();
        Debug.Log("[стенд] офлайн-сессия против чужого сброса: " +
                  (back.Result?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"));
        Assert.That((int?)back.Result?["золото"], Is.EqualTo(150),
                    "ЧУЖОЙ СБРОС СЪЕЛ офлайн-сессию — ровно та потеря, ради которой заведено пополевое слияние");
    }
}
EOF

LOG="$W/unity.log"; RES="$W/results.xml"
LVN_STATE_BASE="http://127.0.0.1:$PORT" LVN_STAND_BITE="${BITE:-0}" "$UNITY/Contents/MacOS/Unity" \
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
grep -o "\[стенд\] .*" "$LOG" | sed 's/^/  /'
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
if [ -n "$BITE" ]; then
  if [ "$total" = "2" ] && [ "$failed" != "0" ]; then
    echo "стенд честный: с одной памятью на двоих проверка краснеет"; exit 0
  fi
  echo "СТЕНД ВРЁТ: неразведённые устройства прошли как два — он мерил одно, дважды"
  exit 2
fi

if [ "$errs" = "0" ] && [ "$total" = "2" ] && [ "$failed" = "0" ]; then
  echo "СБРОС ДЕРЖИТСЯ И ИГРУ НЕ ЕСТ"; exit 0
fi
echo "ПРОВЕРКА НЕ ПОДТВЕРДИЛА"; exit 1
