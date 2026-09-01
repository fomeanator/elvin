using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>Что рисуем. Набор закрыт намеренно: иконка, которой нет в
    /// списке, — это повод дорисовать её здесь, а не подставить случайный
    /// символ и надеяться на шрифт.</summary>
    public enum LvnIcon
    {
        None = 0,
        Home, Store, Wardrobe, Gallery, Archive, Profile, Settings, Gift,
        Energy, Gem, Coin, Lock, Play, Check, Close, Alert, Chevron, Plus, Star, Heart, Clock,
        Crown, Trophy, Key, Book, Mask, Flame, Chart, Refresh,
    }

    /// <summary>
    /// Иконки, НАРИСОВАННЫЕ ВЕКТОРОМ, а не набранные символами.
    ///
    /// <para>Причина конкретная. Иконка-символ живёт в шрифте, а шрифт на
    /// Android свой у каждого производителя: тот же «⚡» на одном телефоне
    /// молния, на другом — пустой квадрат. В коде оболочки даже завёлся
    /// «безопасный набор» глифов — попытка обойти это перебором, которая всё
    /// равно проваливается, стоит смениться шрифту. Здесь шрифт не участвует
    /// вовсе: контур считается на месте и рисуется линиями.</para>
    ///
    /// <para>Второе следствие важнее первого. Символ можно выбрать только из
    /// того, что придумал Юникод, поэтому «гардероб» превращался в звёздочку, а
    /// «магазин» в ромб — фигуры, ничего не сообщающие. Вектор даёт вешалку,
    /// сумку и человека, то есть иконку, которую читают, а не запоминают.</para>
    ///
    /// <para>Рисуется всё на сетке 24×24 и масштабируется под нужный размер,
    /// поэтому иконка одинаково чиста и в строке состояния, и на пол-экрана —
    /// растровой картинке для этого понадобилось бы три файла.</para>
    /// </summary>
    public static class LvnIcons
    {
        private const float Grid = 24f;

        /// <summary>
        /// Готовый элемент с иконкой.
        /// </summary>
        /// <param name="icon">что рисуем</param>
        /// <param name="size">сторона в пикселях холста</param>
        /// <param name="color">цвет линии</param>
        /// <param name="stroke">толщина линии; 0 — пропорционально размеру</param>
        /// <param name="glow">свечение под линией: 0 — нет, 1 — заметное</param>
        /// <summary>
        /// Значок нужного размера и цвета.
        ///
        /// <para>СВЕЧЕНИЕ ПО УМОЛЧАНИЮ БЕРЁТСЯ У ТЕМЫ. Оно её примета:
        /// «Кибер» светит в полную силу, «Романс» — в две трети, «Полночь» не
        /// светит вовсе. Раньше умолчанием был НОЛЬ, и тему приходилось
        /// называть в каждом вызове руками — из тридцати называли в
        /// двенадцати. На светящейся теме восемнадцать значков оставались
        /// тусклыми не по замыслу, а потому что о теме забыли, и в одном ряду
        /// оказывались светящиеся и погашенные.</para>
        ///
        /// <para>Явный ноль остаётся законным: нижняя навигация гасит свечение
        /// намеренно — там значок сам по себе приглушён, и ореол вокруг
        /// тусклого читается как грязь.</para>
        /// </summary>
        public static VisualElement Make(LvnIcon icon, float size, Color color,
                                         float stroke = 0f, float? glow = null)
        {
            float g = glow ?? LvnTheme.Current.IconGlow;
            var el = new Drawn { pickingMode = PickingMode.Ignore };
            el.style.width = size;
            el.style.height = size;
            el.style.flexShrink = 0;   // иначе в тесной строке иконка схлопнется в ноль
            Paint(el, icon, color, stroke, g);
            return el;
        }

        /// <summary>
        /// ЗНАЧОК, КОТОРЫЙ МОЖНО ПЕРЕКРАСИТЬ, не пересоздавая.
        ///
        /// <para>Рисование подписывается на <c>generateVisualContent</c>, и
        /// подписка держит цвет в замыкании: позвать <see cref="Paint"/> второй
        /// раз значило бы нарисовать значок ДВАЖДЫ — старым цветом и новым. Из
        /// этого и выросло «пересоздать иконку», а из пересоздания — мигание
        /// вкладок, которым подмену прикрывали.</para>
        ///
        /// <para>Здесь подписка одна, а цвет — поле: сменить его и попросить
        /// перерисовку. Значок остаётся тем же элементом, и менять его можно
        /// хоть каждый кадр.</para>
        /// </summary>
        private sealed class Drawn : VisualElement
        {
            public LvnIcon Icon;
            public Color Color;
            public float Stroke, Glow;
            public bool Subscribed;
        }

        /// <summary>Перекрасить значок НА МЕСТЕ. Не значок (чужой элемент) —
        /// молча ничего: звонящий не обязан знать, что ему досталось.
        ///
        /// <para>Свечение по умолчанию — темы, как и у <see cref="Make"/>:
        /// умолчания разъехались бы, и перекраска, написанная без третьего
        /// довода, молча гасила бы значок на светящейся теме.</para></summary>
        public static void Tint(VisualElement el, Color color, float? glow = null)
        {
            float want = glow ?? LvnTheme.Current.IconGlow;
            TintTo(el, color, want);
        }

        private static void TintTo(VisualElement el, Color color, float glow)
        {
            if (el is not Drawn drawn) return;
            if (drawn.Color == color && Mathf.Approximately(drawn.Glow, glow)) return;
            drawn.Color = color;
            drawn.Glow = glow;
            drawn.MarkDirtyRepaint();
        }

        /// <summary>Сменить сам значок на месте (иконка вкладки, значок
        /// валюты после смены новеллы).</summary>
        public static void Retarget(VisualElement el, LvnIcon icon)
        {
            if (el is not Drawn drawn || drawn.Icon == icon) return;
            drawn.Icon = icon;
            drawn.MarkDirtyRepaint();
        }

        /// <summary>ЗНАЧОК ВАЛЮТЫ — один на всю оболочку. Кошелёк показывают
        /// минимум три места (строка состояния, магазин, гардероб), и пока
        /// каждое решало само, гардероб писал «13 060 crystals» словом там, где
        /// строка состояния рисовала кристалл: одна и та же валюта выглядела
        /// двумя разными вещами. Имя валюты придумывает автор новеллы, поэтому
        /// узнаём по смыслу, а незнакомое считаем самоцветом.</summary>
        public static LvnIcon ForCurrency(string currency)
        {
            var c = (currency ?? "").ToLowerInvariant();
            if (c.Contains("energy") || c.Contains("stamina") || c.Contains("энерг")) return LvnIcon.Energy;
            if (c.Contains("crystal") || c.Contains("gem") || c.Contains("кристалл")) return LvnIcon.Gem;
            if (c.Contains("gold") || c.Contains("coin") || c.Contains("золот") || c.Contains("монет")) return LvnIcon.Coin;
            if (c.Contains("ticket") || c.Contains("key") || c.Contains("ключ")) return LvnIcon.Key;
            if (c.Contains("heart") || c.Contains("серд")) return LvnIcon.Heart;
            return LvnIcon.Gem;   // незнакомая валюта — всё-таки ценность
        }

        /// <summary>Цвет значка валюты: энергия — акцентом темы, всё
        /// ценное — золотом.</summary>
        public static Color CurrencyColor(string currency)
            => ForCurrency(currency) == LvnIcon.Energy ? LvnTokens.Accent : LvnTokens.Gold;

        /// <summary>Готовый значок валюты нужного размера.</summary>
        public static VisualElement MakeCurrency(string currency, float size)
            => Make(ForCurrency(currency), size, CurrencyColor(currency));

        /// <summary>Рисует иконку в уже существующем элементе — когда размер
        /// задаёт раскладка, а не мы.</summary>
        public static void Paint(VisualElement el, LvnIcon icon, Color color,
                                 float stroke = 0f, float glow = 0f)
        {
            if (el == null) return;
            if (el is Drawn own)
            {
                own.Icon = icon; own.Color = color; own.Stroke = stroke; own.Glow = glow;
                if (own.Subscribed) { own.MarkDirtyRepaint(); return; }
                own.Subscribed = true;
                // Дальше идёт та же отрисовка, но читает она ПОЛЯ элемента, а
                // не замыкание, поэтому подписка нужна одна на всю его жизнь.
                icon = own.Icon; color = own.Color; stroke = own.Stroke; glow = own.Glow;
            }
            el.generateVisualContent += ctx =>
            {
                if (ctx.visualElement is Drawn d)
                {
                    icon = d.Icon; color = d.Color; stroke = d.Stroke; glow = d.Glow;
                }
                var r = ctx.visualElement.contentRect;
                // До первой раскладки размер — NaN. Рисовать по нему значит
                // получить мусор в буфере вершин, а не пустоту.
                if (float.IsNaN(r.width) || r.width <= 1f || r.height <= 1f) return;

                float s = Mathf.Min(r.width, r.height) / Grid;
                var o = new Vector2(r.x + (r.width - Grid * s) * 0.5f,
                                    r.y + (r.height - Grid * s) * 0.5f);
                float w = stroke > 0f ? stroke : Mathf.Max(1f, Grid * s / 12f);

                var p = ctx.painter2D;
                p.lineJoin = LineJoin.Miter;   // острый стык: мягкий скругляет
                p.lineCap = LineCap.Butt;      // жанр держится на прямых срезах

                bool fill = IsFilled(icon);
                if (glow > 0f && !fill)
                {
                    // Свечение — та же линия шире и почти прозрачная. Дешевле
                    // размытия и не требует ни одного дополнительного прохода
                    // по экрану.
                    p.BeginPath();
                    Build(p, icon, o, s);
                    p.lineWidth = w * 2.8f;
                    p.strokeColor = UiColor.WithAlpha(color, color.a * 0.22f * glow);
                    p.Stroke();
                }

                p.BeginPath();
                Build(p, icon, o, s);
                if (fill)
                {
                    p.fillColor = color;
                    p.Fill();
                }
                else
                {
                    p.lineWidth = w;
                    p.strokeColor = color;
                    p.Stroke();
                }
            };
            el.MarkDirtyRepaint();
        }

        // Заливкой рисуются только те, у которых контур читается хуже пятна:
        // молния и треугольник воспроизведения линией выглядят как оригами.
        private static bool IsFilled(LvnIcon i) =>
            i == LvnIcon.Energy || i == LvnIcon.Play || i == LvnIcon.Star;

        // ── сами контуры ────────────────────────────────────────────────────
        // Координаты — в клетках 24×24, начало в левом верхнем углу, Y вниз
        // (как везде в UI Toolkit). Одного BeginPath хватает на всю иконку:
        // каждый MoveTo начинает новый подконтур.
        private static void Build(Painter2D p, LvnIcon icon, Vector2 o, float s)
        {
            Vector2 V(float x, float y) => new Vector2(o.x + x * s, o.y + y * s);

            switch (icon)
            {
                case LvnIcon.Home:
                    p.MoveTo(V(3f, 11f)); p.LineTo(V(12f, 3.5f)); p.LineTo(V(21f, 11f));
                    p.MoveTo(V(5.5f, 10f)); p.LineTo(V(5.5f, 21f)); p.LineTo(V(18.5f, 21f)); p.LineTo(V(18.5f, 10f));
                    p.MoveTo(V(9.5f, 21f)); p.LineTo(V(9.5f, 14.5f)); p.LineTo(V(14.5f, 14.5f)); p.LineTo(V(14.5f, 21f));
                    break;

                case LvnIcon.Store:
                    p.MoveTo(V(4.5f, 8f)); p.LineTo(V(19.5f, 8f)); p.LineTo(V(18f, 21f));
                    p.LineTo(V(6f, 21f)); p.ClosePath();
                    // Ручка: половина окружности сверху. Углы считаются от оси X
                    // по часовой, а Y смотрит вниз, поэтому верх — это 180°→360°.
                    p.MoveTo(V(9f, 8f)); p.LineTo(V(9f, 6.5f));
                    p.Arc(V(12f, 6.5f), 3f * s, Angle.Degrees(180f), Angle.Degrees(360f));
                    p.LineTo(V(15f, 8f));
                    break;

                case LvnIcon.Wardrobe: // вешалка
                    p.MoveTo(V(12f, 8.5f)); p.LineTo(V(12f, 6.5f));
                    p.Arc(V(10f, 6.5f), 2f * s, Angle.Degrees(0f), Angle.Degrees(300f));
                    p.MoveTo(V(12f, 8.5f)); p.LineTo(V(3.5f, 16f)); p.LineTo(V(20.5f, 16f)); p.ClosePath();
                    break;

                case LvnIcon.Gallery:
                    p.MoveTo(V(3.5f, 5f)); p.LineTo(V(20.5f, 5f)); p.LineTo(V(20.5f, 19f));
                    p.LineTo(V(3.5f, 19f)); p.ClosePath();
                    p.MoveTo(V(4f, 16.5f)); p.LineTo(V(10f, 11f)); p.LineTo(V(13.5f, 14.5f));
                    p.LineTo(V(16.5f, 11.5f)); p.LineTo(V(20f, 15f));
                    p.MoveTo(V(9.6f, 9f));
                    p.Arc(V(8.2f, 9f), 1.4f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    break;

                case LvnIcon.Archive:
                    p.MoveTo(V(3f, 4.5f)); p.LineTo(V(21f, 4.5f)); p.LineTo(V(21f, 9f));
                    p.LineTo(V(3f, 9f)); p.ClosePath();
                    p.MoveTo(V(4.8f, 9f)); p.LineTo(V(4.8f, 20f)); p.LineTo(V(19.2f, 20f)); p.LineTo(V(19.2f, 9f));
                    p.MoveTo(V(9.5f, 13.5f)); p.LineTo(V(14.5f, 13.5f));
                    break;

                case LvnIcon.Profile:
                    p.MoveTo(V(15.6f, 8.5f));
                    p.Arc(V(12f, 8.5f), 3.6f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    // Плечи — верхняя половина большой окружности.
                    p.MoveTo(V(4f, 21f));
                    p.Arc(V(12f, 21f), 8f * s, Angle.Degrees(180f), Angle.Degrees(360f));
                    break;

                case LvnIcon.Settings:
                    p.MoveTo(V(15.5f, 12f));
                    p.Arc(V(12f, 12f), 3.5f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i * Mathf.PI / 3f;
                        float cx = 12f + Mathf.Cos(a) * 6.2f, cy = 12f + Mathf.Sin(a) * 6.2f;
                        float ex = 12f + Mathf.Cos(a) * 9.5f, ey = 12f + Mathf.Sin(a) * 9.5f;
                        p.MoveTo(V(cx, cy)); p.LineTo(V(ex, ey));
                    }
                    break;

                case LvnIcon.Energy: // молния
                    p.MoveTo(V(13.5f, 2f)); p.LineTo(V(5f, 13.8f)); p.LineTo(V(10.6f, 13.8f));
                    p.LineTo(V(9.5f, 22f)); p.LineTo(V(19f, 9.6f)); p.LineTo(V(13f, 9.6f));
                    p.ClosePath();
                    break;

                case LvnIcon.Gem:
                    p.MoveTo(V(12f, 3f)); p.LineTo(V(20.5f, 9.5f)); p.LineTo(V(12f, 21f));
                    p.LineTo(V(3.5f, 9.5f)); p.ClosePath();
                    p.MoveTo(V(3.5f, 9.5f)); p.LineTo(V(20.5f, 9.5f));
                    p.MoveTo(V(12f, 3f)); p.LineTo(V(8.2f, 9.5f)); p.LineTo(V(12f, 21f));
                    p.MoveTo(V(12f, 3f)); p.LineTo(V(15.8f, 9.5f)); p.LineTo(V(12f, 21f));
                    break;

                case LvnIcon.Coin:
                    p.MoveTo(V(20.5f, 12f));
                    p.Arc(V(12f, 12f), 8.5f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.MoveTo(V(16.5f, 12f));
                    p.Arc(V(12f, 12f), 4.5f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    break;

                case LvnIcon.Lock:
                    p.MoveTo(V(4.5f, 10.5f)); p.LineTo(V(19.5f, 10.5f)); p.LineTo(V(19.5f, 21f));
                    p.LineTo(V(4.5f, 21f)); p.ClosePath();
                    p.MoveTo(V(7.5f, 10.5f));
                    p.Arc(V(12f, 10.5f), 4.5f * s, Angle.Degrees(180f), Angle.Degrees(360f));
                    break;

                case LvnIcon.Play:
                    p.MoveTo(V(7f, 4.5f)); p.LineTo(V(20f, 12f)); p.LineTo(V(7f, 19.5f)); p.ClosePath();
                    break;

                case LvnIcon.Check:
                    p.MoveTo(V(4.5f, 12.5f)); p.LineTo(V(9.5f, 17.5f)); p.LineTo(V(19.5f, 6.5f));
                    break;

                case LvnIcon.Close:
                    p.MoveTo(V(5.5f, 5.5f)); p.LineTo(V(18.5f, 18.5f));
                    p.MoveTo(V(18.5f, 5.5f)); p.LineTo(V(5.5f, 18.5f));
                    break;

                case LvnIcon.Gift:
                    p.MoveTo(V(3f, 8.5f)); p.LineTo(V(21f, 8.5f)); p.LineTo(V(21f, 12.5f));
                    p.LineTo(V(3f, 12.5f)); p.ClosePath();
                    p.MoveTo(V(5f, 12.5f)); p.LineTo(V(5f, 21f)); p.LineTo(V(19f, 21f)); p.LineTo(V(19f, 12.5f));
                    p.MoveTo(V(12f, 8.5f)); p.LineTo(V(12f, 21f));
                    // Бант: две петли, каждая упирается в центр верхней кромки.
                    p.MoveTo(V(12f, 8.5f));
                    p.BezierCurveTo(V(9.5f, 8.5f), V(6.5f, 7f), V(7.6f, 4.9f));
                    p.BezierCurveTo(V(8.7f, 2.9f), V(11.3f, 5.2f), V(12f, 8.5f));
                    p.MoveTo(V(12f, 8.5f));
                    p.BezierCurveTo(V(14.5f, 8.5f), V(17.5f, 7f), V(16.4f, 4.9f));
                    p.BezierCurveTo(V(15.3f, 2.9f), V(12.7f, 5.2f), V(12f, 8.5f));
                    break;

                case LvnIcon.Alert:
                    p.MoveTo(V(12f, 3f)); p.LineTo(V(22f, 20.5f)); p.LineTo(V(2f, 20.5f)); p.ClosePath();
                    p.MoveTo(V(12f, 9.5f)); p.LineTo(V(12f, 15f));
                    p.MoveTo(V(12f, 17.6f)); p.LineTo(V(12f, 18.2f));
                    break;

                case LvnIcon.Chevron:
                    p.MoveTo(V(9f, 5f)); p.LineTo(V(16f, 12f)); p.LineTo(V(9f, 19f));
                    break;

                case LvnIcon.Plus:
                    p.MoveTo(V(12f, 5f)); p.LineTo(V(12f, 19f));
                    p.MoveTo(V(5f, 12f)); p.LineTo(V(19f, 12f));
                    break;

                case LvnIcon.Star:
                    for (int i = 0; i < 10; i++)
                    {
                        // Чередование длинного и короткого радиуса и есть звезда.
                        float rad = (i % 2 == 0) ? 9.5f : 4.1f;
                        float a = -Mathf.PI / 2f + i * Mathf.PI / 5f;
                        var pt = V(12f + Mathf.Cos(a) * rad, 12f + Mathf.Sin(a) * rad);
                        if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                    }
                    p.ClosePath();
                    break;

                case LvnIcon.Heart:
                    p.MoveTo(V(12f, 20.2f));
                    p.BezierCurveTo(V(3.4f, 14.6f), V(3f, 9.4f), V(6.6f, 7.2f));
                    p.BezierCurveTo(V(9.1f, 5.7f), V(11.2f, 6.9f), V(12f, 8.7f));
                    p.BezierCurveTo(V(12.8f, 6.9f), V(14.9f, 5.7f), V(17.4f, 7.2f));
                    p.BezierCurveTo(V(21f, 9.4f), V(20.6f, 14.6f), V(12f, 20.2f));
                    break;

                case LvnIcon.Crown:
                    p.MoveTo(V(3f, 19f)); p.LineTo(V(5f, 6.5f)); p.LineTo(V(9.5f, 12f));
                    p.LineTo(V(12f, 4.5f)); p.LineTo(V(14.5f, 12f)); p.LineTo(V(19f, 6.5f));
                    p.LineTo(V(21f, 19f)); p.ClosePath();
                    break;

                case LvnIcon.Trophy:
                    p.MoveTo(V(7f, 3.5f)); p.LineTo(V(17f, 3.5f)); p.LineTo(V(17f, 9f));
                    p.BezierCurveTo(V(17f, 13f), V(14.6f, 15f), V(12f, 15f));
                    p.BezierCurveTo(V(9.4f, 15f), V(7f, 13f), V(7f, 9f));
                    p.ClosePath();
                    p.MoveTo(V(7f, 5.5f)); p.BezierCurveTo(V(3.4f, 5.5f), V(3.4f, 10.4f), V(7f, 11f));
                    p.MoveTo(V(17f, 5.5f)); p.BezierCurveTo(V(20.6f, 5.5f), V(20.6f, 10.4f), V(17f, 11f));
                    p.MoveTo(V(12f, 15f)); p.LineTo(V(12f, 18.6f));
                    p.MoveTo(V(9f, 18.6f)); p.LineTo(V(15f, 18.6f));
                    p.MoveTo(V(7.5f, 21f)); p.LineTo(V(16.5f, 21f));
                    break;

                case LvnIcon.Key:
                    p.MoveTo(V(12.2f, 8f));
                    p.Arc(V(8f, 8f), 4.2f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.MoveTo(V(11f, 11f)); p.LineTo(V(20f, 20f));
                    p.MoveTo(V(16.4f, 16.4f)); p.LineTo(V(14.3f, 18.5f));
                    p.MoveTo(V(18.2f, 18.2f)); p.LineTo(V(16.1f, 20.3f));
                    break;

                case LvnIcon.Book:
                    p.MoveTo(V(12f, 6.6f));
                    p.BezierCurveTo(V(9.8f, 4.6f), V(6.4f, 4.4f), V(3.5f, 5.4f));
                    p.LineTo(V(3.5f, 19f));
                    p.BezierCurveTo(V(6.4f, 18f), V(9.8f, 18.2f), V(12f, 20.2f));
                    p.BezierCurveTo(V(14.2f, 18.2f), V(17.6f, 18f), V(20.5f, 19f));
                    p.LineTo(V(20.5f, 5.4f));
                    p.BezierCurveTo(V(17.6f, 4.4f), V(14.2f, 4.6f), V(12f, 6.6f));
                    p.ClosePath();
                    p.MoveTo(V(12f, 6.6f)); p.LineTo(V(12f, 20.2f));
                    break;

                case LvnIcon.Mask: // театральная маска — «все концовки»
                    p.MoveTo(V(4f, 6f));
                    p.BezierCurveTo(V(4f, 4.4f), V(20f, 4.4f), V(20f, 6f));
                    p.BezierCurveTo(V(20f, 14f), V(16f, 20.5f), V(12f, 20.5f));
                    p.BezierCurveTo(V(8f, 20.5f), V(4f, 14f), V(4f, 6f));
                    p.ClosePath();
                    p.MoveTo(V(8f, 10.6f)); p.BezierCurveTo(V(8.8f, 9.2f), V(10.4f, 9.2f), V(11.2f, 10.6f));
                    p.MoveTo(V(12.8f, 10.6f)); p.BezierCurveTo(V(13.6f, 9.2f), V(15.2f, 9.2f), V(16f, 10.6f));
                    p.MoveTo(V(9f, 15f)); p.BezierCurveTo(V(10.4f, 16.6f), V(13.6f, 16.6f), V(15f, 15f));
                    break;

                case LvnIcon.Flame:
                    p.MoveTo(V(12f, 2.2f));
                    p.BezierCurveTo(V(16.5f, 7f), V(18f, 10.5f), V(18f, 14f));
                    p.BezierCurveTo(V(18f, 17.9f), V(15.3f, 21f), V(12f, 21f));
                    p.BezierCurveTo(V(8.7f, 21f), V(6f, 17.9f), V(6f, 14f));
                    p.BezierCurveTo(V(6f, 10.5f), V(9f, 7.5f), V(12f, 2.2f));
                    p.ClosePath();
                    p.MoveTo(V(12f, 12.2f));
                    p.BezierCurveTo(V(13.9f, 14.2f), V(14.6f, 15.6f), V(14.6f, 17f));
                    p.BezierCurveTo(V(14.6f, 18.9f), V(13.4f, 20.2f), V(12f, 20.2f));
                    p.BezierCurveTo(V(10.6f, 20.2f), V(9.4f, 18.9f), V(9.4f, 17f));
                    p.BezierCurveTo(V(9.4f, 15.5f), V(10.3f, 14.1f), V(12f, 12.2f));
                    p.ClosePath();
                    break;

                case LvnIcon.Chart:
                    p.MoveTo(V(3.5f, 20.5f)); p.LineTo(V(20.5f, 20.5f));
                    p.MoveTo(V(7.5f, 20.5f)); p.LineTo(V(7.5f, 13.5f));
                    p.MoveTo(V(12f, 20.5f)); p.LineTo(V(12f, 7.5f));
                    p.MoveTo(V(16.5f, 20.5f)); p.LineTo(V(16.5f, 11f));
                    break;

                case LvnIcon.Refresh:
                    // Почти полный круг с разрывом сверху и стрелкой на конце.
                    p.MoveTo(V(17.75f, 7.18f));
                    p.Arc(V(12f, 12f), 7.5f * s, Angle.Degrees(-40f), Angle.Degrees(250f));
                    p.MoveTo(V(17.75f, 7.18f)); p.LineTo(V(21f, 6.4f));
                    p.MoveTo(V(17.75f, 7.18f)); p.LineTo(V(17.4f, 3.6f));
                    break;

                case LvnIcon.Clock:
                    p.MoveTo(V(20.5f, 12f));
                    p.Arc(V(12f, 12f), 8.5f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.MoveTo(V(12f, 6.5f)); p.LineTo(V(12f, 12.5f)); p.LineTo(V(16f, 14.8f));
                    break;
            }
        }
    }
}
