using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// НАБОР ДЕТАЛЕЙ ОБЛИКА «СЦЕНА» — кнопка, плашка-заголовок, полоса
    /// прогресса, картинка рамки и подпись, из которых собирается главная по
    /// макету партнёра (см. <see cref="BrowseHub"/>, Stage).
    ///
    /// <para>Детали здесь, а не в хабе, потому что тот же рисунок ждут магазин
    /// и профиль: одна кнопка на три экрана, а не три похожих. Правило деления
    /// простое — РАМКА нарисована (свечение, срезы, блик код не рисует), всё
    /// ЖИВОЕ собрано элементами: слово из словаря и шрифта темы, число из
    /// данных, ход полосы из прогресса, нажатие с откликом.</para>
    ///
    /// <para>Размеры — в единицах МАКЕТА (390 dp шириной) через один множитель
    /// <see cref="D"/>: числа макета читаются как есть.</para>
    /// </summary>
    internal static class LvnStageKit
    {
        /// <summary>Панель UITK, на которую разложен макет.</summary>
        public const float PanelWidth = 1080f;

        /// <summary>Холст макета против панели: один множитель на все размеры.
        /// Ширину макета держит паспорт облика (<see cref="LvnStageSkin"/>), а
        /// не константа здесь: другой арт рисуют на другом холсте.</summary>
        public static float K => PanelWidth / LvnStageSkin.DesignWidth;
        public static float D(float dp) => Mathf.Round(dp * K);

        /// <summary>Запас, с которым экспортированы рамки: свечение выходит за
        /// край элемента. Число — из паспорта облика.</summary>
        public static float Bleed => LvnStageSkin.Bleed;

        /// <summary>Имя картинки рамки — по нему фотограф ждёт, пока весь арт
        /// облика доедет.</summary>
        public const string ArtName = "stage-img";

        /// <summary>РАМКИ ОБЛИКА — ровно те файлы, что кладёт главная
        /// (BrowseHub.Stage) и шапка («плюс»). Список один: по нему бут греет
        /// витрину до снятия вуали, по нему же автор знает, что рисовать.</summary>
        public static readonly string[] SkinFiles =
            { "nav.png", "panel.png", "card-back.png", "card-front.png", "adv.png", "plus.png" };

        /// <summary>Адрес файла в папке облика — папка с косой чертой на
        /// конце или без неё.</summary>
        public static string SkinUrl(string root, string file)
        {
            root ??= "";
            return (root.EndsWith("/") ? root : root + "/") + file;
        }

        /// <summary>Поставить элемент абсолютно: место и размер одним вызовом.</summary>
        public static T At<T>(T el, float x, float y, float w, float h) where T : VisualElement
        {
            el.style.position = Position.Absolute;
            el.style.left = x; el.style.top = y;
            el.style.width = w; el.style.height = h;
            return el;
        }

        /// <summary>Картинка рамки на своём месте: больше места на запас
        /// свечения с каждой стороны — так нарисована.</summary>
        public static VisualElement Art(string url, ILvnAssets assets,
                                        float x, float y, float w, float h, float bleed = -1f)
        {
            // Запас по умолчанию берётся из паспорта облика, а он не константа
            // времени компиляции — отсюда отрицательное «не названо».
            if (bleed < 0f) bleed = Bleed;
            var img = new VisualElement { name = ArtName, pickingMode = PickingMode.Ignore };
            At(img, x - D(bleed), y - D(bleed), w + D(bleed * 2f), h + D(bleed * 2f));
            LvnPicture.Skin(img, url, assets, what: "StageSkin");
            return img;
        }

        /// <summary>Подпись облика по центру своего места. Источник слова
        /// привязывается к переодеванию (смена языка); без источника — подпись,
        /// чей текст ведёт сам экран (число награды). Среднее начертание —
        /// заголовочное темы, и берётся у неё, а не файлом по имени.</summary>
        public static Label Text(Func<string> text, float size, Color color, bool medium = false)
        {
            var l = text != null ? LvnRedress.Bind(new Label(), text) : new Label();
            l.pickingMode = PickingMode.Ignore;
            l.style.fontSize = size;
            l.style.color = color;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            if (medium) LvnFonts.Apply(l, LvnFonts.Display);
            return l;
        }

        /// <summary>Плашка-заголовок: слово прописными на нарисованной плашке.
        /// Место задаёт вызывающий — плашка нарисована в рамке.</summary>
        /// <summary>Прижать кусок к левому или правому краю (одна ось —
        /// одно решение; вместе четыре стороны читались бы как растяжка).</summary>
        private static void PinX(VisualElement p, bool far)
        {
            if (far) p.style.right = 0; else p.style.left = 0;
        }

        /// <summary>Прижать кусок к верхнему или нижнему краю.</summary>
        private static void PinY(VisualElement p, bool far)
        {
            if (far) p.style.bottom = 0; else p.style.top = 0;
        }

        /// <summary>РАМКА ИЗ КУСКОВ АРТА. Девятидольная нарезка UITK тянет
        /// середину вместе с углами, и на широком низком коробе угловые скобы
        /// расплывались; поэтому рамка собирается вручную: четыре угла
        /// показывают углы арта как нарисованы, четыре кромки — его тонкие
        /// стороны между скобами, растянутые по своей оси.
        ///
        /// <para><paramref name="solid"/> добавляет девятый кусок — СЕРЕДИНУ
        /// арта, растянутую на всё нутро. Она и есть «полный задний фон, как на
        /// главной у блока текущих экспедиций» (Илья 09.09): та же штриховка
        /// того же тона, что внутри панели новостей. Пустая середина оставалась
        /// от опыта со стеклом сцены — оно оказалось дорогим (каждый кадр
        /// перерисовывает подложку мира) и лагало на устройстве.</para>
        ///
        /// <para>Кромки и середина меряются по раскладке контейнера. Кладётся в
        /// <paramref name="host"/> на место <paramref name="index"/>, под
        /// содержимым; глоу выходит за край на <see cref="Bleed"/>.</para></summary>
        public static VisualElement HollowFrame(VisualElement host, string url, ILvnAssets assets,
                                                float imgW, float imgH, float cornerPx, float pxPerDp,
                                                int index = 0, bool solid = false)
        {
            var f = new VisualElement { name = "stage-frame", pickingMode = PickingMode.Ignore };
            f.style.position = Position.Absolute;
            float bleed = D(Bleed);
            f.style.left = -bleed; f.style.right = -bleed; f.style.top = -bleed; f.style.bottom = -bleed;
            float s = D(1f) / pxPerDp;               // экранных px на px арта
            float c = cornerPx * s;                  // угол на экране
            float wi = imgW * s, hi = imgH * s;      // арт целиком на экране
            // девятый кусок — середина; рисуется ПЕРВЫМ, чтобы кромки легли поверх её края
            int count = solid ? 9 : 8;
            var pieces = new VisualElement[count];
            for (int i = 0; i < count; i++)
            {
                var p = new VisualElement { pickingMode = PickingMode.Ignore };
                p.style.position = Position.Absolute;
                p.style.overflow = Overflow.Hidden;
                p.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                LvnPicture.Skin(p, url, assets, what: "StageSkin");
                pieces[i] = p; f.Add(p);
            }
            void Corner(VisualElement p, bool right, bool bottom)
            {
                p.style.width = c; p.style.height = c;
                PinX(p, right); PinY(p, bottom);
                p.style.backgroundSize = new BackgroundSize(wi, hi);
                p.style.backgroundPositionX = new BackgroundPosition(right ? BackgroundPositionKeyword.Right : BackgroundPositionKeyword.Left);
                p.style.backgroundPositionY = new BackgroundPosition(bottom ? BackgroundPositionKeyword.Bottom : BackgroundPositionKeyword.Top);
            }
            Corner(pieces[0], false, false); Corner(pieces[1], true, false);
            Corner(pieces[2], false, true);  Corner(pieces[3], true, true);
            // кромки: между углами, своя ось тянется так, чтобы скобы арта остались за краем куска
            void Edge(VisualElement p, bool horizontal, bool far)
            {
                if (horizontal)
                {
                    p.style.left = c; p.style.right = c; p.style.height = c;
                    PinY(p, far);
                    p.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                    p.style.backgroundPositionY = new BackgroundPosition(far ? BackgroundPositionKeyword.Bottom : BackgroundPositionKeyword.Top);
                }
                else
                {
                    p.style.top = c; p.style.bottom = c; p.style.width = c;
                    PinX(p, far);
                    p.style.backgroundPositionX = new BackgroundPosition(far ? BackgroundPositionKeyword.Right : BackgroundPositionKeyword.Left);
                    p.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                }
            }
            Edge(pieces[4], true, false); Edge(pieces[5], true, true);
            Edge(pieces[6], false, false); Edge(pieces[7], false, true);
            if (solid)
            {
                var mid = pieces[8];
                mid.style.left = c * 0.5f; mid.style.right = c * 0.5f;
                mid.style.top = c * 0.5f; mid.style.bottom = c * 0.5f;
                mid.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                mid.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                mid.SendToBack();
            }
            f.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float w = f.resolvedStyle.width, h = f.resolvedStyle.height;
                if (float.IsNaN(w) || w <= 1f || float.IsNaN(h) || h <= 1f) return;
                float innerW = Mathf.Max(1f, w - 2f * c), innerH = Mathf.Max(1f, h - 2f * c);
                float stretchX = innerW * imgW / Mathf.Max(1f, imgW - 2f * cornerPx);
                float stretchY = innerH * imgH / Mathf.Max(1f, imgH - 2f * cornerPx);
                pieces[4].style.backgroundSize = new BackgroundSize(stretchX, hi);
                pieces[5].style.backgroundSize = new BackgroundSize(stretchX, hi);
                pieces[6].style.backgroundSize = new BackgroundSize(wi, stretchY);
                pieces[7].style.backgroundSize = new BackgroundSize(wi, stretchY);
                // СЕРЕДИНА: обе оси растянуты так, что углы арта уходят за края
                // куска, и внутри видна только его штрихованная середина.
                if (solid) pieces[8].style.backgroundSize = new BackgroundSize(stretchX, stretchY);
            });
            host.Insert(Mathf.Clamp(index, 0, host.childCount), f);
            return f;
        }

        /// <summary>Задник карточки — по паспорту облика: размеры картинки,
        /// угловые скобы и во сколько пикселей картинки ложится dp макета.</summary>
        public static float CardBackW => LvnStageSkin.CardBack.ImageW;
        public static float CardBackH => LvnStageSkin.CardBack.ImageH;
        public static float CardBackCornerPx => LvnStageSkin.CardBack.CornerPx;
        public static float CardBackPxPerDp => LvnStageSkin.CardBack.PxPerDp(0f);

        /// <summary>ЗАДНИК ЛИСТА В ОБЛИКЕ: своя заливка снимается, вместо неё —
        /// рамка арта со своей серединой. Содержимое отступает от рамки на
        /// 20 dp со всех сторон («паддинг 20 20, и в профиль тоже» — Илья 08.09).
        ///
        /// <para>Стояло стекло сцены (<c>UiGlass</c>) — оно снимает подложку
        /// мира каждый кадр и на устройстве лагало; вместо него полный фон,
        /// как у панели новостей на главной (Илья 09.09).</para></summary>
        public static float SheetPadDp => LvnStageSkin.SheetPad;

        /// <summary>НИЗ НАД ЛЕНТОЙ: <paramref name="dp"/> от низа макета, где
        /// домашняя полоса телефона уже учтена, — на живом экране её место
        /// занимает настоящий вырез, если он больше. Одна формула для столбиков
        /// и листов; раньше её носили в трёх местах.</summary>
        public static float BottomAboveBar(VisualElement host, float dp)
            => D(dp - LvnStageSkin.HomeBar) + Mathf.Max(LvnEdges.Bottom(host), D(LvnStageSkin.HomeBar));

        /// <summary>ЛИСТ ВИТРИНЫ ПО ПАСПОРТУ. Поля, верх и низ — из
        /// <see cref="LvnStageSkin.Sheet"/>: лист-вкладка (<paramref name="tab"/>)
        /// начинается долей высоты, чтобы героиня оставалась в кадре, и стоит
        /// над лентой меню; попап (деталь) идёт от шапки до домашней полосы —
        /// лента ложится на него сверху, а его кнопки поднимаются на
        /// <see cref="LvnStageSkin.SheetBox.Under"/>. Следит за вырезами сам.</summary>
        public static void SheetFrame(VisualElement sheet, VisualElement host, bool tab)
        {
            if (sheet == null || host == null) return;
            sheet.style.position = Position.Absolute;
            SheetEdges(sheet, host, tab);
            LvnEdges.Follow(host, _ => SheetEdges(sheet, host, tab));
        }

        public static void SheetEdges(VisualElement sheet, VisualElement host, bool tab)
        {
            var box = LvnStageSkin.Sheet;
            sheet.style.left = D(box.Side); sheet.style.right = D(box.Side);
            if (tab) sheet.style.top = Length.Percent(box.TabTop01 * 100f);
            else sheet.style.top = LvnEdges.Top(host) + D(box.Top);
            sheet.style.bottom = BottomAboveBar(host, tab ? box.Bottom : LvnStageSkin.HomeBar);
        }

        /// <summary>ПРИЁМ ОБЛИКА ИЗ МАНИФЕСТА — один дом для всех экранов.
        /// Применяет паспорт метрик, сверяет имя облика с прежним и, если оно
        /// сменилось, запоминает и зовёт <paramref name="onChanged"/>. Раньше
        /// каждый экран носил свою копию этих четырёх строк.</summary>
        public static void TakeSkin(Lvn.Content.LvnManifest manifest, ref string skin, Action onChanged)
        {
            LvnStageSkin.Apply(manifest?.ui?.browse?.skin_metrics);
            var next = manifest?.ui?.browse?.skin;
            if (next == skin) return;
            skin = next;
            onChanged?.Invoke();
        }

        /// <summary>
        /// ОДЕТЬ ЛИСТ ЭКРАНА В ОБЛИК ВИТРИНЫ — стекло в рисованной рамке и
        /// заголовок золотом, ровно один раз.
        ///
        /// <para>Приём стоял копией в каждом экране облика: рамка рисуется
        /// КАРТИНКОЙ, и второй слой лёг бы поверх первого, поэтому у всех был
        /// свой флажок «уже одет». Копии не падают, они расходятся: одному
        /// экрану поправят радиус, другому забудут.</para>
        ///
        /// <para>Возвращает новое значение флажка — вызывающий держит его у
        /// себя, потому что одевается ЕГО лист.</para>
        /// </summary>
        public static bool DressSheet(VisualElement sheet, string skin, ILvnAssets assets,
                                      bool alreadyDressed, Label heading = null)
        {
            if (alreadyDressed || sheet == null || string.IsNullOrEmpty(skin)) return alreadyDressed;
            GlassSheet(sheet, skin, assets, LvnTokens.Radius);
            if (heading != null) heading.style.color = LvnTokens.Gold;   // заголовки витрины — золотом
            return true;
        }

        public static void GlassSheet(VisualElement host, string skin, ILvnAssets assets, float radius)
        {
            host.style.backgroundColor = Color.clear;
            LvnChrome.ClearBorder(host);
            LvnAir.Pad(host, D(SheetPadDp));
            HollowFrame(host, SkinUrl(skin, "card-back.png"), assets,
                        CardBackW, CardBackH, CardBackCornerPx, CardBackPxPerDp, index: 0, solid: true);
        }

        public static Label Plaque(Func<string> text)
            => Text(() => (text() ?? string.Empty).ToUpperInvariant(), LvnTokens.TextSm, LvnTokens.Text);

        /// <summary>Кнопка облика: рамка нарисована в панели, здесь слово и зона
        /// нажатия с откликом. Подпись стоит чуть выше центра — у нарисованной
        /// кнопки нижняя грань толще.</summary>
        public static VisualElement Button(Func<string> text, Action onTap)
        {
            var b = new VisualElement();
            b.style.justifyContent = Justify.Center;
            b.style.alignItems = Align.Center;
            b.style.paddingBottom = D(3f);
            b.Add(Text(() => (text() ?? string.Empty).ToUpperInvariant(), LvnTokens.TextBase, LvnTokens.Gold, medium: true));
            b.AddManipulator(new Clickable(onTap));
            LvnMotion.Tappable(b);
            return b;
        }

        /// <summary>Полоса прогресса макета: чёрная дорожка, тёмная канавка,
        /// светлый ход слева. Ход двигает <see cref="Fill"/>.</summary>
        public static VisualElement Progress(out VisualElement fill)
        {
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.height = D(9f);
            bar.style.backgroundColor = Color.black;
            LvnChrome.Round(bar, D(3f));
            var groove = new VisualElement { pickingMode = PickingMode.Ignore };
            groove.style.position = Position.Absolute;
            groove.style.left = D(2f); groove.style.right = D(2f); groove.style.top = D(2f); groove.style.bottom = D(2f);
            groove.style.backgroundColor = LvnTokens.Track;
            LvnChrome.Round(groove, D(3f));
            bar.Add(groove);
            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.style.position = Position.Absolute;
            fill.style.left = D(2f); fill.style.top = D(2f); fill.style.bottom = D(2f);
            fill.style.width = Length.Percent(0f);
            fill.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.Round(fill, D(3f));
            bar.Add(fill);
            return bar;
        }

        /// <summary>Доля хода 0..1 — от ширины канавки.</summary>
        public static void Fill(VisualElement fill, float fraction)
        {
            if (fill != null) fill.style.width = Length.Percent(Mathf.Clamp01(fraction) * 100f);
        }
    }
}
