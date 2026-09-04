#!/usr/bin/env bash
# ИНДИКАТОР ЗАГРУЗКИ ОБЯЗАН ГОВОРИТЬ ПРАВДУ — И ЭТО СЧИТАЕТСЯ, А НЕ КАЖЕТСЯ.
#
# Живые снимки, с которых всё началось: кольцо почти полное на первых процентах
# («крутилка всегда на 100 %, прогресса не видно»), «Скачано 296 МБ из 298 МБ»
# рядом с «Осталось ≈114 МБ», и скорость 21,9 МБ/с на вставшей загрузке.
#
# Корень был один: доля считалась не планом, а догадкой — «принято + 64 КБ ×
# непочатые файлы», при настоящей медиане файла в четверть мегабайта. Веса
# лежат в манифесте с самого начала, их просто не доносили до загрузчика.
#
# Стенд считает обе формулы на одном и том же наборе файлов и требует от новой
# совпадения с правдой. Unity для этого не нужен: DownloadTally — чистый C#,
# он компилируется компилятором из редактора и запускается как консольная
# программа. Редактор при этом может быть занят.
#
#   qa/download-progress-check.sh [-bite]
#
# -bite подсовывает проверке СТАРУЮ формулу вместо новой: стенд, который этого
# не заметит, не заметит и возвращения бага.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

UNITY="$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app 2>/dev/null | sort -V | tail -1)"
[ -n "$UNITY" ] || { echo "нет Unity (нужен только его компилятор) — пропускаю"; exit 0; }
DOTNET="$UNITY/Contents/Resources/Scripting/NetCoreRuntime/dotnet"
CSC="$UNITY/Contents/Resources/Scripting/DotNetSdkRoslyn/csc.dll"
FW="$(ls -d "$UNITY/Contents/Resources/Scripting/NetCoreRuntime/shared/Microsoft.NETCore.App"/* 2>/dev/null | tail -1)"
for p in "$DOTNET" "$CSC" "$FW"; do
  [ -e "$p" ] || { echo "нет $p — пропускаю"; exit 0; }
done

W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT

cat > "$W/Scenario.cs" <<'CS'
using System;
using Lvn.Content;

// Сценарий: пакет из 161 файла с распределением весов живой новеллы (медиана
// около четверти мегабайта, длинный хвост крупного арта), шесть полос сети.
static class Scenario
{
    const bool UseOldFormula =
#if BITE
        true;
#else
        false;
#endif
    const long Unknown = 64 << 10; // прежняя догадка о непочатом файле

    static long[] Sizes(int n)
    {
        var rnd = new Random(20260905);
        var a = new long[n];
        for (int i = 0; i < n; i++)
        {
            double r = rnd.NextDouble();
            a[i] = r < 0.55 ? (long)(40_000 + r * 400_000)      // значки, звуки, куски текста
                 : r < 0.9  ? (long)(300_000 + r * 1_500_000)   // спрайты, фоны
                            : (long)(2_000_000 + r * 6_000_000); // крупный арт
        }
        return a;
    }

    static int Main()
    {
        var sizes = Sizes(161);
        long plan = 0;
        foreach (var s in sizes) plan += s;
        Console.WriteLine($"файлов {sizes.Length}, план {plan / 1e6:F1} МБ");

        int lanes = 6, done = 0, i = 0;
        long closed = 0;
        double worstNew = 0, worstOld = 0;
        var problems = new System.Collections.Generic.List<string>();

        while (i < sizes.Length)
        {
            int take = Math.Min(lanes, sizes.Length - i);
            for (int step = 1; step <= 2; step++)
            {
                long inflight = 0;
                for (int k = 0; k < take; k++) inflight += sizes[i + k] * step / 2;
                long got = closed + inflight;
                double real = (double)got / plan;

                // Новая правда: план известен заранее, доля — принято / план.
                var tally = new DownloadTally(got, plan, done, sizes.Length, 5e6f,
                                              DownloadTally.Phase.Running);
                // Прежняя догадка — ради замера разрыва (и ради -bite).
                long expOld = closed + inflight;
                for (int k = 0; k < take; k++) expOld += sizes[i + k] - sizes[i + k] * step / 2;
                expOld += Math.Max(0, sizes.Length - done - take) * Unknown;
                double old = expOld > 0 ? (double)got / expOld : 0;

                double shown = UseOldFormula ? old : tally.Fraction;
                worstNew = Math.Max(worstNew, Math.Abs(tally.Fraction - real));
                worstOld = Math.Max(worstOld, Math.Abs(old - real));

                if (Math.Abs(shown - real) > 0.02)
                    problems.Add($"на {real * 100:F1}% показано {shown * 100:F1}%");

                if (tally.LeftBytes != plan - got)
                    problems.Add("«осталось» считается не планом");
            }
            closed += SumRange(sizes, i, take);
            done += take;
            i += take;
        }

        Console.WriteLine($"худшее расхождение: новая формула {worstNew * 100:F1} п.п., прежняя {worstOld * 100:F1} п.п.");

        // Плана нет — кольцу нечего показывать: спиннер, а не полное кольцо.
        var blind = new DownloadTally(5_000_000, 0, 3, 10, 1e6f, DownloadTally.Phase.Running);
        if (blind.Fraction >= 0f) problems.Add("без плана доля притворилась известной");
        if (blind.LeftBytes != 0) problems.Add("без плана «осталось» выдумано");

        // Принято больше плана (файл вырос) — единица, а не полтора.
        var over = new DownloadTally(12_000_000, 10_000_000, 10, 10, 0f, DownloadTally.Phase.Running);
        if (Math.Abs(over.Fraction - 1f) > 0.001f) problems.Add($"переполнение плана дало {over.Fraction}");

        // Состояния: тишина дольше порога — «встало», и время дожития не врёт.
        if (DownloadTally.PhaseOf(true, false, 0, 0.5f) != DownloadTally.Phase.Running)
            problems.Add("идущая загрузка названа вставшей");
        if (DownloadTally.PhaseOf(true, false, 0, 6f) != DownloadTally.Phase.Stalled)
            problems.Add("вставшая загрузка названа идущей — та самая застывшая скорость");
        if (DownloadTally.PhaseOf(true, true, 0, 0.1f) != DownloadTally.Phase.Offline)
            problems.Add("офлайн не назван офлайном");
        if (DownloadTally.PhaseOf(false, false, 4, 0f) != DownloadTally.Phase.Syncing)
            problems.Add("несинхроненные события не названы");
        if (DownloadTally.PhaseOf(false, false, 0, 0f) != DownloadTally.Phase.Idle)
            problems.Add("простой не назван простоем");
        var stalled = new DownloadTally(1, 100, 0, 5, 5e6f, DownloadTally.Phase.Stalled);
        if (stalled.EtaSeconds >= 0f) problems.Add("на вставшей загрузке обещано время");

        foreach (var p in problems) Console.WriteLine("  РВЁТСЯ: " + p);
        if (problems.Count > 0) return 1;
        Console.WriteLine("держит: доля равна правде, «осталось» — план минус принятое, состояния названы");
        return 0;
    }

    static long SumRange(long[] a, int from, int count)
    {
        long s = 0;
        for (int k = 0; k < count; k++) s += a[from + k];
        return s;
    }
}
CS

DEF=""
[ -n "$BITE" ] && DEF="-define:BITE"
REFS=""
for d in "$FW"/*.dll; do REFS="$REFS -r:$d"; done

# shellcheck disable=SC2086
"$DOTNET" "$CSC" -nologo -nostdlib -target:exe -optimize- $DEF $REFS \
  -out:"$W/tally.dll" \
  unity/Packages/com.lvn.engine/Runtime/Content/DownloadTally.cs "$W/Scenario.cs" \
  > "$W/build.log" 2>&1 || { echo "не собралось:"; tail -12 "$W/build.log"; exit 1; }

cat > "$W/tally.runtimeconfig.json" <<JSON
{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"$(basename "$FW")"}}}
JSON

"$DOTNET" "$W/tally.dll"
code=$?

if [ -n "$BITE" ]; then
  if [ "$code" != "0" ]; then
    echo "укус замечен: стенд забраковал прежнюю формулу"
    exit 0
  fi
  echo "СТЕНД СЛЕП: прежняя формула прошла проверку — он не заметил бы и возврата бага"
  exit 2
fi
exit "$code"
