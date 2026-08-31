namespace Lvn
{
    /// <summary>
    /// РЫВКИ КАДРА — сколько раз игра запнулась и насколько сильно.
    ///
    /// <para>Порог и сам вопрос «это уже рывок?» жили строкой внутри цикла
    /// сцены, и ответ был только в логе: строка на каждый долгий кадр. По логу
    /// видно ЧТО случилось, но не видно, СТАЛО ЛИ ЛУЧШЕ — а именно это и
    /// спрашивают после каждой правки («приложение прям очень плавное стало» —
    /// проверить это было нечем).</para>
    ///
    /// <para>Здесь счёт: сколько запинок и какая худшая. Числа снимаются вместе
    /// с концом главы и уходят в отчёт свойствами события — так «плавнее» из
    /// ощущения становится величиной, которую видно в воронке.</para>
    ///
    /// <para>Порог 150 мс — не «просело до 6 кадров», а «глаз заметил остановку»:
    /// ниже него глаз считает движение непрерывным, выше — видит рывок.</para>
    /// </summary>
    public static class LvnFrameWatch
    {
        public const float HitchSeconds = 0.15f;

        /// <summary>Первые кадры после загрузки сцены тяжелы всегда и о плавности
        /// ничего не говорят.</summary>
        public const int WarmupFrames = 10;

        private static int _hitches;
        private static float _worst;

        /// <summary>Сколько запинок с прошлого снятия и какая была худшей.</summary>
        public static int Hitches => _hitches;
        public static int WorstMs => (int)(_worst * 1000f);

        /// <summary>Кадр прожит. <paramref name="note"/> — чем движок был занят;
        /// спрашивается ТОЛЬКО когда кадр оказался рывком, чтобы обычный кадр не
        /// платил за диагностику.</summary>
        public static void Frame(float dt, int frameCount, System.Func<string> note = null)
        {
            if (dt <= HitchSeconds || frameCount <= WarmupFrames) return;
            _hitches++;
            if (dt > _worst) _worst = dt;
            UnityEngine.Debug.Log($"[lvn-perf] FRAME HITCH {(dt * 1000f):F0}ms at frame {frameCount}"
                                  + (note != null ? note() : ""));
        }

        /// <summary>Снять счёт и начать заново — конец главы, конец сессии.</summary>
        public static (int hitches, int worstMs) Take()
        {
            var r = (_hitches, WorstMs);
            _hitches = 0; _worst = 0f;
            return r;
        }
    }
}
