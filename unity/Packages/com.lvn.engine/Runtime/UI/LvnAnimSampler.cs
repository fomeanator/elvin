using System;
using System.Collections.Generic;
using System.Globalization;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements.Experimental;   // кривые сглаживания

namespace Lvn.UI
{
    /// <summary>
    /// КАК ЧИТАЕТСЯ АНИМАЦИЯ ВО ВРЕМЕНИ — часы, время канала, выборка дорожки,
    /// кадр, длина дуги, наклон пути.
    ///
    /// <para>Чистая математика показа: она не знает, ЧТО анимируют — плоскую
    /// фигуру, костяную или трёхмерную, — только как в момент <c>t</c>
    /// прочитать написанное автором. Отсюда живут все, кто двигает фигуру.</para>
    ///
    /// <para>Жила она половиной класса <c>ActorAnimator</c> — второй,
    /// НЕПОДКЛЮЧЁННОЙ реализации анимации для пути UI Toolkit, которую из игры
    /// не создавал никто, кроме тестов: сцена рисует канвасом. Пока эта
    /// половина стояла рядом, каждая правка правил обязана была трогать обе, и
    /// расхождения копились молча — так, например, разъехалась остановка
    /// дорожки. Половина удалена, математика переехала сюда.</para>
    /// </summary>
    internal static class LvnAnimSampler
    {
        // Time source — overridable so tests can drive Composite() deterministically.
        // Своя ручка остаётся: тесты анимаций двигают время покадрово и не
        // должны зависеть от общих часов интерфейса. Умолчание — общие.
        internal static Func<float> Clock = () => LvnClock.Now();

        // ── pure sampling (static, unit-tested) ──────────────────────────────
        // Разбор по типу, а не Convert.ToSingle: тот БРОСАЕТ на строке из
        // каталога — а ключ, записанный как "0.012", роняет всю анимацию вместо
        // одного кадра. Заодно быстрее: типовые случаи идут без преобразования.
        private static float F(object o)
        {
            switch (o)
            {
                case null: return 0f;
                case double d: return (float)d;
                case float f: return f;
                case long l: return l;
                case int i: return i;
            }
            return LvnNum.Parse(o.ToString(), 0f);
        }

        internal static float Sample(LvnAnimTrack tr, float t) => Sample(tr, t, easeless: false);

        internal static float Sample(LvnAnimTrack tr, float t, bool easeless)
        {
            var keys = tr.keys;
            float K0(object[] k) => k != null && k.Length > 0 ? F(k[0]) : 0f;
            float V(object[] k) => k != null && k.Length > 1 ? F(k[1]) : 0f;

            if (t <= K0(keys[0])) return V(keys[0]);
            var last = keys[keys.Count - 1];
            if (t >= K0(last)) return V(last);
            for (int i = 0; i < keys.Count - 1; i++)
            {
                float t0 = K0(keys[i]), t1 = K0(keys[i + 1]);
                if (t >= t0 && t <= t1)
                {
                    if (tr.interp == "step") return V(keys[i]); // hold until the next key
                    float u = t1 > t0 ? (t - t0) / (t1 - t0) : 0f;
                    u = easeless ? Mathf.Clamp01(u) : Ease(tr.ease, Mathf.Clamp01(u));
                    if (tr.interp == "spline")
                    {
                        // Catmull-Rom through the key values (ends clamped) — the
                        // curve passes through every key, unlike a fitted Bezier.
                        float p0 = V(keys[Mathf.Max(0, i - 1)]);
                        float p1 = V(keys[i]);
                        float p2 = V(keys[i + 1]);
                        float p3 = V(keys[Mathf.Min(keys.Count - 1, i + 2)]);
                        return 0.5f * ((2f * p1) + (-p0 + p2) * u
                            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u * u
                            + (-p0 + 3f * p1 - 3f * p2 + p3) * u * u * u);
                    }
                    return Mathf.Lerp(V(keys[i]), V(keys[i + 1]), u);
                }
            }
            return V(last);
        }

        // ── arc-length (constant speed along a spline path) ──────────────────
        // Per-axis Catmull-Rom makes speed vary with key spacing. For a spline
        // path pair we warp time so equal TIME covers equal DISTANCE, and the
        // easing curve drives progress along the length (the spec's model),
        // instead of easing each segment separately.

        /// <summary>Cumulative length of the raw (unesased) path at uniform time
        /// steps. Built once per playing anim; ~64 samples is visually exact.</summary>
        internal static float[] BuildArcTable(LvnAnimTrack x, LvnAnimTrack y, float dur, int samples = 64)
        {
            var cum = new float[samples + 1];
            float px = Sample(x, 0f, easeless: true), py = Sample(y, 0f, easeless: true);
            for (int i = 1; i <= samples; i++)
            {
                float t = dur * i / samples;
                float cx = Sample(x, t, easeless: true), cy = Sample(y, t, easeless: true);
                cum[i] = cum[i - 1] + Mathf.Sqrt((cx - px) * (cx - px) + (cy - py) * (cy - py));
                px = cx; py = cy;
            }
            return cum;
        }

        /// <summary>Map progress <paramref name="u01"/> (0..1 along the LENGTH)
        /// back to the raw sample time that reaches that distance.</summary>
        internal static float WarpProgress(float[] cum, float u01, float dur)
        {
            int n = cum.Length - 1;
            float total = cum[n];
            if (total <= 0f || dur <= 0f) return u01 * dur; // degenerate path → linear time
            float target = Mathf.Clamp01(u01) * total;
            int lo = 0, hi = n;
            while (lo < hi) { int mid = (lo + hi) / 2; if (cum[mid] < target) lo = mid + 1; else hi = mid; }
            if (lo == 0) return 0f;
            float seg = cum[lo] - cum[lo - 1];
            float frac = seg > 0f ? (target - cum[lo - 1]) / seg : 0f;
            return dur * (lo - 1 + frac) / n;
        }

        // The warped sample time for a spline path pair at wall time t: easing
        // drives progress along the length, the table converts it to raw time.
        /// <summary>
        /// ЧАСЫ КАНАЛА — где анимация находится ПРЯМО СЕЙЧАС.
        ///
        /// <para>Между «сколько прошло секунд» и «какое значение брать у
        /// дорожки» лежат три решения, и все три — про время, а не про то, что
        /// анимируют: закольцована ли анимация (и качается ли туда-обратно),
        /// доиграла ли она, и есть ли у неё ПУТЬ — пара сплайновых дорожек
        /// screen_x/screen_y, по которой фигура обязана двигаться с ПОСТОЯННОЙ
        /// СКОРОСТЬЮ, а не с постоянным приростом параметра.</para>
        ///
        /// <para>Эти три решения были записаны ДВАЖДЫ дословно — у плоской
        /// фигуры и у трёхмерной. Расхождение в них не падает и даже не видно
        /// на глаз: движение просто идёт «не так», рывками или не тем концом
        /// петли. Такое ищут неделями, поэтому у времени канала один дом.</para>
        /// </summary>
        internal struct ChannelClock
        {
            /// <summary>Время дорожек — по стенным часам, закольцованное или
            /// зажатое концом.</summary>
            public float T;
            /// <summary>Время дорожек ПУТИ — выправленное по длине дуги, чтобы
            /// скорость вдоль кривой была ровной.</summary>
            public float PathT;
            /// <summary>Длина анимации, не меньше мгновения (делить на неё
            /// придётся).</summary>
            public float Duration;
            /// <summary>Пара дорожек, образующих путь, — если она есть.</summary>
            public LvnAnimTrack PathX, PathY;
            /// <summary>Путь сплайновый: время вдоль него выправляется.</summary>
            public bool ArcPath;
            /// <summary>Незакольцованная анимация дошла до конца.</summary>
            public bool Finished;

            /// <summary>Эта дорожка — часть пути?</summary>
            public bool OnPath(LvnAnimTrack tr) => ArcPath && (tr == PathX || tr == PathY);
            /// <summary>Время выборки ЭТОЙ дорожки: дорожки пути живут по
            /// выправленному времени, остальные — по стенному.</summary>
            public float TimeOf(LvnAnimTrack tr) => OnPath(tr) ? PathT : T;
            /// <summary>Время, по которому берут наклон пути для разворота
            /// фигуры «лицом по движению».</summary>
            public float OrientT => ArcPath ? PathT : T;
        }

        /// <summary>Часы канала по прошедшему времени. <paramref name="arcCache"/> —
        /// таблица длины дуги этого канала: строится один раз и живёт с ним.</summary>
        internal static ChannelClock ClockOf(LvnAnim anim, float elapsed, ref float[] arcCache)
        {
            float dur = Mathf.Max(0.0001f, anim.duration);
            float t = anim.loop
                ? (anim.yoyo ? Mathf.PingPong(elapsed, dur) : Mathf.Repeat(elapsed, dur))
                : Mathf.Min(elapsed, dur);

            LvnAnimTrack px = null, py = null;
            if (anim.tracks != null)
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || !string.IsNullOrEmpty(tr.layer) || tr.keys == null) continue;
                    if (tr.prop == "screen_x") px = tr;
                    else if (tr.prop == "screen_y") py = tr;
                }
            bool arc = px != null && py != null && px.interp == "spline" && py.interp == "spline";

            return new ChannelClock
            {
                T = t,
                PathT = arc ? ArcTime(px, py, t, dur, ref arcCache) : t,
                Duration = dur,
                PathX = px, PathY = py, ArcPath = arc,
                Finished = !anim.loop && elapsed >= dur,
            };
        }

        internal static float ArcTime(LvnAnimTrack x, LvnAnimTrack y, float t, float dur, ref float[] cache)
        {
            cache ??= BuildArcTable(x, y, dur);
            // Путь — ОДНО движение, и разгон у него один. Брали его только с
            // дорожки screen_x: `ease`, написанный автором на screen_y, молча
            // не действовал — путь ехал ровно там, где написано «с ускорением»,
            // и сказать об этом было некому. Берём первый названный.
            var ease = !string.IsNullOrEmpty(x.ease) ? x.ease : y.ease;
            float u = Ease(ease, Mathf.Clamp01(t / dur));
            return WarpProgress(cache, u, dur);
        }

        /// <summary>Tangent angle of a screen-space path pair at time <paramref name="t"/>,
        /// in degrees, y-down clockwise-positive (the UI Toolkit rotate convention;
        /// the Canvas path negates it). Central difference over the sampled curve, so
        /// it respects easing and spline interpolation.</summary>
        internal static float OrientAngle(LvnAnimTrack xTr, LvnAnimTrack yTr, float t, float dur)
        {
            float eps = Mathf.Max(0.0005f, dur / 200f);
            float t0 = Mathf.Max(0f, t - eps), t1 = Mathf.Min(dur, t + eps);
            float dx = Sample(xTr, t1) - Sample(xTr, t0);
            float dy = Sample(yTr, t1) - Sample(yTr, t0);
            if (dx == 0f && dy == 0f) return 0f;
            return Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
        }

        // Frame tracks step: the value of the last key whose time is <= t.
        internal static string SampleFrame(LvnAnimTrack tr, float t)
        {
            var keys = tr.keys;
            string cur = keys[0].Length > 1 ? keys[0][1]?.ToString() : null;
            for (int i = 0; i < keys.Count; i++)
            {
                float time = keys[i].Length > 0 ? F(keys[i][0]) : 0f;
                if (time <= t && keys[i].Length > 1) cur = keys[i][1]?.ToString();
                else if (time > t) break;
            }
            return cur;
        }

        private static float Ease(string name, float u)
        {
            switch (name)
            {
                case "inOutSine": return Easing.InOutSine(u);
                case "outCubic": return Easing.OutCubic(u);
                case "outBack": return Easing.OutBack(u);
                case "inBack": return Easing.InBack(u);
                default: return u;
            }
        }
    }
}
