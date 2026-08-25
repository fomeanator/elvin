using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// АТМОСФЕРА ЗА СОДЕРЖИМЫМ: сетка, сканлайны, виньетка, пятно света.
    ///
    /// <para>Разницу между «аккуратно» и «красиво» делает не компонент, а фон.
    /// Экран из правильно покрашенных панелей на ровной заливке читается как
    /// макет; те же панели над дышащей сеткой и прижатыми виньеткой краями — как
    /// сцена. Четыре слоя ниже стоят дешевле любого из этих компонентов и дают
    /// больше, чем все они вместе.</para>
    ///
    /// <para>ТЕКСТУРЫ СЧИТАЮТСЯ КОДОМ, а не лежат файлами. Так вышло не из
    /// экономии: сетка и сканлайны — это математика в четыре строки, и файл
    /// добавил бы к ним только лишний путь, который однажды не найдётся в
    /// сборке. Заодно тема не тащит за собой чужой арт и остаётся частью
    /// движка, а не набором картинок.</para>
    ///
    /// <para>Слои не ловят касания и не участвуют в раскладке: они кладутся
    /// первыми детьми и абсолютно, поэтому всё добавленное после рисуется
    /// поверх и ведёт себя так, будто фона нет.</para>
    /// </summary>
    public static class LvnBackdrop
    {
        /// <summary>
        /// Стелет фон темы в корень экрана. Тема без атмосферы не делает
        /// ничего — вызывать можно всегда.
        /// </summary>
        public static void Apply(VisualElement root, LvnTheme theme)
        {
            if (root == null) return;
            var t = theme ?? LvnTheme.Current;

            // Порядок снизу вверх и есть весь рецепт: структура, потом свет,
            // потом плёнка, потом затемнённые края.
            if (t.Grid)
                Layer(root, Grid(), true, 0.30f, Tint(t.Accent, 1f));

            if (t.Glow)
            {
                // Пятно света сверху задаёт, куда смотреть. Оно выходит за края
                // экрана, чтобы не читалось как нарисованный круг.
                var g = Layer(root, Glow(), false, 0.42f, Tint(t.Accent, 1f));
                g.style.top = -300; g.style.height = 1250;
                g.style.left = -160; g.style.right = -160;
                g.style.bottom = new StyleLength(StyleKeyword.Auto);
                Breathe(g, 0.42f, 0.58f, 4200);

                // Второе — у нижней кромки: горизонт за экраном. Он прижимает
                // навигацию и не даёт низу выглядеть обрезанным.
                //
                // ВЫСОТЫ ПОДОБРАНЫ ТАК, ЧТОБЫ ПЯТНА ПЕРЕКРЫВАЛИСЬ. В первой
                // версии верхнее кончалось раньше, чем начиналось нижнее, и
                // ровно посередине экрана оставалась мёртвая полоса — она и
                // читалась как «света нет». Свет обязан быть непрерывным:
                // видимая граница между освещённым и неосвещённым превращает
                // атмосферу в два наклеенных пятна.
                var f = Layer(root, Glow(), false, 0.26f, Tint(t.Accent, 1f));
                f.style.top = new StyleLength(StyleKeyword.Auto);
                f.style.bottom = -340; f.style.height = 900;
                f.style.left = -200; f.style.right = -200;
                Breathe(f, 0.26f, 0.36f, 5600);   // другой период: два синхронных пятна пульсируют как лампа
            }

            if (t.Scanlines)
                Layer(root, Scanlines(), true, 0.5f, Color.white);

            if (t.Vignette)
                Layer(root, Vignette(), false, 1f, Color.white);
        }

        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, a);

        /// <summary>
        /// Медленное дыхание слоя.
        ///
        /// <para>Неподвижный экран мёртв, даже если он безупречен. Достаточно
        /// ОДНОЙ вещи, которая никогда не останавливается, — и всё остальное
        /// начинает читаться как включённое. Свет подходит лучше всего: он не
        /// отвлекает, потому что не имеет краёв, и его нельзя «дочитать».</para>
        ///
        /// <para>Период в четыре секунды выбран не на глаз: быстрее — заметно и
        /// начинает раздражать, медленнее — уже не воспринимается как движение.
        /// Синус, а не пила: у пилы слышен щелчок разворота.</para>
        /// </summary>
        private static void Breathe(VisualElement el, float min, float max, int periodMs)
        {
            if (el == null) return;
            float phase = 0f;
            el.schedule.Execute((TimerState ts) =>
            {
                // Время у планировщика, а не у Time: он и так его считает, и в
                // редакторе вне игрового режима ведёт себя одинаково.
                phase += Mathf.Min(ts.deltaTime, 250f) / periodMs * Mathf.PI * 2f;
                if (phase > Mathf.PI * 2f) phase -= Mathf.PI * 2f;
                float k = (Mathf.Sin(phase) + 1f) * 0.5f;
                el.style.opacity = Mathf.Lerp(min, max, k);
            }).Every(33);   // 30 кадров в секунду хватает: движение медленное
        }

        private static VisualElement Layer(VisualElement parent, Texture2D tex,
                                           bool tile, float opacity, Color tint)
        {
            var v = new VisualElement { pickingMode = PickingMode.Ignore, name = "lvn-backdrop" };
            v.style.position = Position.Absolute;
            // Запас за краями: параллакс сдвигает слой, и без напуска по
            // периметру у кромки экрана показался бы «шов» фона.
            v.style.left = -40; v.style.right = -40; v.style.top = -40; v.style.bottom = -40;
            if (tex != null)
            {
                v.style.backgroundImage = new StyleBackground(tex);
                v.style.unityBackgroundImageTintColor = tint;
                v.style.backgroundRepeat = tile
                    ? new BackgroundRepeat(Repeat.Repeat, Repeat.Repeat)
                    : new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                if (!tile)
                    v.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            }
            v.style.opacity = opacity;
            parent.Add(v);
            // Первым ребёнком: всё содержимое экрана добавляется позже и,
            // значит, рисуется поверх.
            v.SendToBack();
            return v;
        }

        // ── процедурные текстуры ────────────────────────────────────────────
        private static Texture2D _grid, _scan, _vig, _glow;

        private static Texture2D New(int w, int h, Color32[] px, TextureWrapMode wrap)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = wrap,
                filterMode = FilterMode.Bilinear,
                // Переживает смену сцены и не оседает в ней объектом, который
                // потом ищут глазами в иерархии.
                hideFlags = HideFlags.HideAndDontSave,
            };
            t.SetPixels32(px);
            t.Apply(false, false);
            return t;
        }

        /// <summary>Сетка: мелкая клетка и жирная линия раз в четыре — без
        /// второго масштаба сетка читается как шум обоев, а не как разметка.</summary>
        private static Texture2D Grid()
        {
            if (_grid != null) return _grid;
            const int N = 256, Cell = 64;
            var px = new Color32[N * N];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    bool minor = (x % Cell == 0) || (y % Cell == 0);
                    bool major = (x == 0) || (y == 0);
                    if (major) px[y * N + x] = new Color32(255, 255, 255, 105);
                    else if (minor) px[y * N + x] = new Color32(255, 255, 255, 38);
                }
            return _grid = New(N, N, px, TextureWrapMode.Repeat);
        }

        /// <summary>Сканлайны: одна тёмная строка из четырёх. Дальше — только
        /// муар, ближе — плёнка перестаёт читаться.</summary>
        private static Texture2D Scanlines()
        {
            if (_scan != null) return _scan;
            const int W = 4, H = 4;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    px[y * W + x] = y == 0
                        ? new Color32(0, 0, 0, 70)
                        : new Color32(0, 0, 0, 0);
            return _scan = New(W, H, px, TextureWrapMode.Repeat);
        }

        /// <summary>Виньетка: прижимает края, чтобы центр читался. Затемнение
        /// начинается не от центра, а с середины пути к краю, иначе экран
        /// выглядит грязным, а не глубоким.</summary>
        private static Texture2D Vignette()
        {
            if (_vig != null) return _vig;
            const int N = 128;
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f;
                    float dy = (y + 0.5f) / N * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);          // 1 у кромки, 1.41 в углу
                    float a = Mathf.Clamp01((d - 0.55f) / 0.85f);
                    a = Mathf.Pow(a, 1.6f) * 0.82f;                    // мягкий вход
                    px[y * N + x] = new Color32(0, 0, 0, (byte)(a * 255f));
                }
            return _vig = New(N, N, px, TextureWrapMode.Clamp);
        }

        /// <summary>Пятно света: белое к краю в ноль, тонируется акцентом.</summary>
        private static Texture2D Glow()
        {
            if (_glow != null) return _glow;
            const int N = 128;
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f;
                    float dy = (y + 0.5f) / N * 2f - 1f;
                    float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
                    float a = Mathf.Pow(1f - d, 2.2f);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            return _glow = New(N, N, px, TextureWrapMode.Clamp);
        }
    }
}
