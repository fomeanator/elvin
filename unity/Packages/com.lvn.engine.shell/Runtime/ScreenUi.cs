using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Shared UI Toolkit building blocks for the shell screens. Every screen used
    /// to re-declare these privately — the stretch-to-fill layout, the async
    /// background loader, and a couple of label helpers — so they lived in five or
    /// six copies. Centralising them keeps the screens to their actual layout and
    /// removes the drift that copy-paste invites.
    /// </summary>
    internal static class ScreenUi
    {
        /// <summary>Pin an element to all four edges of its parent (absolute,
        /// full-bleed). Returns the element so it can be used inline.</summary>
        // ОКНО В РАМОЧНИКА: сама растяжка живёт в движке — её одинаково нужно
        // и сцене, и оболочке, а сцена этой сборки не видит.
        public static T Stretch<T>(T el) where T : VisualElement
            => Lvn.UI.LvnChrome.Stretch(el);

        /// <summary>
        /// The substantial lower sheet used by menu sections that live over the
        /// heroine and the scene canvas. This is deliberately not a glowing card:
        /// the sheet is the one object allowed to have a strong edge, while its
        /// contents stay quiet and let the scene remain the hero.
        /// </summary>
        public static void SceneSheet(VisualElement el, float opacity = 0.94f)
        {
            if (el == null) return;
            var bg = LvnTokens.PanelBg;
            el.style.backgroundColor = UiColor.WithAlpha(bg, opacity);
            LvnChrome.Round(el, LvnTokens.Radius);
            var edge = LvnTokens.Accent;
            LvnChrome.Border(el, UiColor.WithAlpha(edge, 0.30f), 1f);
            LvnChrome.EdgeOn(el, LvnSide.Top, UiColor.WithAlpha(edge, 0.72f), 2f);
        }

        /// <summary>
        /// КНОПКА «НАЗАД» — знак «‹» в гнезде под значок.
        ///
        /// <para>Форма у неё одна на всех: квадратное гнездо, знак по центру,
        /// плашка и скругление темы. А вот РАЗМЕР честно разный — на экране
        /// с крупной шапкой она крупнее, на тесном списке мельче, — поэтому
        /// размер и кегль называет экран, а не дом. Здесь и была ловушка:
        /// вместе с размером каждый экран копировал и форму, и различие
        /// оставалось невидимым, пока не сложишь файлы рядом.</para>
        ///
        /// <para>Плавающая кнопка поверх обложки (экран новеллы) сюда не
        /// относится: она не в шапке, а НАД картинкой, с собственной тёмной
        /// подложкой — другая вещь, а не та же с другими числами.</para>
        /// </summary>
        public static Button BackButton(System.Action onClick, float size, float fontSize)
        {
            var back = new Button(onClick) { text = "‹" };
            LvnStyler.IconSlot(back, size);
            back.style.fontSize = Lvn.UI.LvnFonts.Size(fontSize);
            return back;
        }

        /// <summary>
        /// ВКЛАДКА ХАБА — экран, который не закрывает собой мир.
        ///
        /// <para>Это не окно поверх игры, а ещё одна вкладка той же витрины:
        /// скрима нет, корень прозрачен и НЕ ЛОВИТ ТАПЫ (нижнее меню хаба живёт
        /// под ним и обязано нажиматься), содержимое прижато вниз — верх экрана
        /// остаётся воздухом с героиней и полотном, а внизу оставлена дырка под
        /// то же нижнее меню.</para>
        ///
        /// <para>Все эти числа — одно решение (Илья, 26.08, «как гардероб»), и
        /// записано оно было ДВАЖДЫ: в профиле и в лавке. Разъехались бы они
        /// молча — две вкладки одной витрины с разной высотой воздуха читаются
        /// как небрежность, а не как разные экраны.</para>
        /// </summary>
        public static void HubTabSheet(VisualElement root, VisualElement sheet)
        {
            if (root != null)
            {
                root.style.backgroundColor = Color.clear;
                root.pickingMode = PickingMode.Ignore;
            }
            if (sheet == null) return;
            sheet.style.position = Position.Absolute;
            sheet.style.left = 10; sheet.style.right = 10;
            sheet.style.top = Length.Percent(39f);   // лицо героини остаётся в чистой зоне
            sheet.style.bottom = 132;                // дырка нижнего меню
            sheet.style.paddingTop = LvnTokens.Space3;
            sheet.style.paddingBottom = LvnTokens.Space2;
            sheet.style.paddingLeft = LvnTokens.Space3;
            sheet.style.paddingRight = LvnTokens.Space3;
            SceneSheet(sheet, 0.92f);
        }

        // ПОКАЗ КАРТИНКИ ЖИВЁТ В ДВИЖКЕ (роль 212). Вписывание стояло здесь
        // отдельно от загрузки — и работало БЕЗ неё молча: картинка вставала
        // растянутой под форму своего места. Теперь картинка обязана назвать,
        // чем она пришла: Lvn.UI.LvnPicture.Photo (обложка, фон, аватар —
        // вписывается) или .Skin (рамка, подложка, полоса — тянется).

        /// <summary>Окно в дом картинок: девятислойная рамка.</summary>
        public static Task AssignNineSliceAsync(VisualElement el, string url, int slice, ILvnAssets assets)
            => Lvn.UI.LvnPicture.Frame(el, url, slice, assets);

        /// <summary>A full-width, centre-aligned absolute label placed at a vertical
        /// fraction of its parent. Ignores pointer input (overlay text).</summary>
        /// <summary>
        /// ЗАГОЛОВОК РАЗДЕЛА внутри экрана — «Главы», «Достижения», «Ваши
        /// статы», шапка окна перезапуска.
        ///
        /// <para>Собирался четырьмя способами: две частные копии в двух
        /// экранах (30 и 28 кеглем) и два набора строк на месте. Двое из
        /// четверых ПРОПУСКАЛИ огранку темы — и на «Кибере» половина
        /// заголовков шла капсом с разрядкой, а половина обычным текстом, в
        /// одном и том же экране.</para>
        ///
        /// <para>Не путать с <c>LvnOverlayScreen.SectionTitle</c>: тот —
        /// заголовок ЭКРАНА в шапке (крупнее, живёт всю жизнь экрана и потому
        /// принимает источник). Этот живёт внутри тела, которое пересобирают
        /// целиком.</para>
        /// </summary>
        public static Label SectionHeader(string text) => DressHeader(new Label(text));

        /// <summary>Тот же заголовок, но со связью со словарём — для тела,
        /// которое пересобирают НЕ целиком.</summary>
        public static Label SectionHeader(System.Func<string> text)
            => DressHeader(Lvn.UI.LvnRedress.Bind(new Label(), text));

        /// <summary>
        /// РАЗДЕЛ СТРАНИЦЫ: отступ сверху и заголовок в одной коробке.
        ///
        /// <para>Обёртка стояла тремя копиями (статы новеллы, главы, сохранения)
        /// — и отступы в них уже разошлись: 34 у одной, 36 у двух других.
        /// Разницу в два пикселя никто не задумывал и никто не заметит; она
        /// просто означает, что общего решения нет.</para>
        ///
        /// <para>Заголовок принимается ТОЛЬКО живой связью со словарём. Две из
        /// трёх копий передавали готовую строку — и заголовок замерзал в момент
        /// сборки: игрок менял язык, а «Ваши статы» оставались на прежнем.
        /// Именно этот сорт отказа Илья ловил трижды подряд.</para>
        /// </summary>
        public static VisualElement Section(System.Func<string> title)
        {
            var section = new VisualElement();
            section.style.flexShrink = 0;
            section.style.marginTop = LvnTokens.Space5;
            section.Add(SectionHeader(title));
            return section;
        }

        private static Label DressHeader(Label lbl)
        {
            lbl.style.flexShrink = 0;
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = LvnTokens.TextBase;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.whiteSpace = WhiteSpace.Normal;   // длинный заголовок переносится, а не режется
            lbl.style.marginTop = LvnTokens.Space1;
            lbl.style.marginBottom = LvnTokens.Space2;
            return LvnChrome.Heading(lbl);
        }

        /// <summary>
        /// НАДЗАГОЛОВОК — маленькая разрядка заглавными над заголовком
        /// («PROFILE», «TOP UP», «FEATURED»).
        ///
        /// <para>Сверстан был пятью правилами в четырёх местах: цвет золотой
        /// или акцентный, кегль 18/24/30, разрядка 2.2/3/4, — и связь между
        /// кеглем и разрядкой каждый раз выводили заново, на глаз. Разрядка
        /// ПРОПОРЦИОНАЛЬНА кеглю (одна восьмая): это её типографское правило, а
        /// не совпадение трёх подобранных чисел.</para>
        ///
        /// <para>Отступ снизу остаётся вызывающему: у надзаголовка карточки и
        /// надзаголовка экрана разное окружение, и это единственное, чем они
        /// вправе отличаться.</para>
        /// </summary>
        public static Label Eyebrow(System.Func<string> text, float size = 18f, Color? color = null)
        {
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), text);
            lbl.style.color = color ?? LvnTokens.Gold;
            lbl.style.fontSize = Lvn.UI.LvnFonts.Size(size);
            lbl.style.letterSpacing = size / 8f;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            return lbl;
        }

        public static Label CenterLabel(float topFraction, Color color, float size)
        {
            var l = new Label();
            l.style.position = Position.Absolute;
            l.style.left = 0;
            l.style.right = 0;
            l.style.top = Length.Percent(topFraction * 100f);
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.color = color;
            l.style.fontSize = size;
            l.pickingMode = PickingMode.Ignore;
            return l;
        }

        /// <summary>Null-safe label text setter.</summary>
        public static void SetText(Label l, string t) { if (l != null) l.text = t; }

        /// <summary>The device safe-area insets (notch / home indicator) converted
        /// to panel units for <paramref name="el"/>'s panel: x = top, y = bottom.
        /// Zero before the element is attached (or on notchless screens) — call it
        /// from a <see cref="GeometryChangedEvent"/> so it re-resolves once real.</summary>
        public static Vector2 SafeVerticalInsets(VisualElement el)
            => Lvn.UI.LvnEdges.Insets(el);   // окно в Кромочника

        /// <summary>ОТСТУП СВЕРХУ — сколько единиц панели занимает вырез камеры
        /// (чёлка, «остров», статус-бар).
        ///
        /// <para>Считался в четырёх местах и ДВУМЯ разными формулами: одни
        /// переводили пиксели через RuntimePanelUtils, другие — множителем
        /// «высота панели / высота экрана». На обычном телефоне это одно и то
        /// же число, но стоит панели получить нестандартный масштаб, и элементы,
        /// которые обязаны стоять на одной линии (бар, колонка эмоций, шапка
        /// хаба), разъезжаются.</para></summary>
        public static float SafeTop(VisualElement el) => Lvn.UI.LvnEdges.Insets(el).x;

        // Экранные пиксели по вертикали → единицы панели. ScreenToPanel
        // отображает позиции, но для scale-only рантайм-панели это ровно тот
        // масштаб, который нужен и для расстояний.
        private static float ToPanel(IPanel panel, float pixels)
            => Mathf.Max(0f, RuntimePanelUtils.ScreenToPanel(panel, new Vector2(0f, pixels)).y);

        /// <summary>Build a horizontal progress bar centred on (<paramref name="xFrac"/>,
        /// <paramref name="yFrac"/>) of its parent, sized <paramref name="wFrac"/>×
        /// <paramref name="hFrac"/>: a coloured <paramref name="track"/> under a
        /// left-anchored <paramref name="fill"/> the caller animates by setting its
        /// width. Both the boot splash and the chapter loader built this identically;
        /// callers add their own extras (a frame overlay, art) on top.</summary>
        /// <summary>
        /// РЯД — горизонтальная строка по центру.
        ///
        /// <para>Три строки стиля, написанные ТРИДЦАТЬ ЧЕТЫРЕ раза: шапка
        /// экрана, строка значения, полоса кнопок, чип, карусель. Ни одна не
        /// выглядит нарушением — «просто стиль», — но именно из таких строк и
        /// набирается разнобой: где-то забыли выравнивание, где-то поставили
        /// другое, и одинаковые на вид ряды ведут себя по-разному.</para>
        ///
        /// <para><paramref name="spread"/> — «содержимое по краям»: тот же ряд,
        /// но с разгоном. Внешние поля остаются вызывающему: они про его
        /// компоновку, а не про сам ряд.</para>
        /// </summary>
        public static VisualElement Row(bool spread = false) => Row(new VisualElement(), spread);

        /// <summary>Тот же ряд, но из готового элемента (кнопка-ряд, карточка).</summary>
        public static T Row<T>(T el, bool spread = false) where T : VisualElement
        {
            if (el == null) return el;
            el.style.flexDirection = FlexDirection.Row;
            el.style.alignItems = Align.Center;
            if (spread) el.style.justifyContent = Justify.SpaceBetween;
            return el;
        }

        /// <summary>
        /// ПОЛОСА РАСТЁТ, А НЕ ПЕРЕПРЫГИВАЕТ.
        ///
        /// <para>Заполнение ставили присваиванием ширины: данные приходят раз в
        /// треть секунды, и полоса дёргалась ступеньками — на глаз это читается
        /// как подвисание, а не как ход. Здесь она доезжает до новой доли за
        /// один короткий ход, поэтому движение непрерывно даже на редких
        /// данных.</para>
        ///
        /// <para>Назад — БЕЗ анимации: откат (сменилась глава, пересчитали
        /// знаменатель) не событие для глаза, а поправка учёта; ползти назад
        /// значило бы показывать несуществующее «разгружается».</para>
        /// </summary>
        public static void SetFill(VisualElement fill, float frac)
        {
            if (fill == null) return;
            frac = Mathf.Clamp01(frac);
            // Откуда ехать: у ВЫСТАВЛЕННОЙ доли ключевое слово Undefined (Null
            // значит «свойство не трогали»). Перепутать их значит каждый раз
            // начинать ход от нуля — полоса дёргалась бы к началу на каждом
            // обновлении.
            var w = fill.style.width;
            float now = w.keyword == StyleKeyword.Undefined && w.value.unit == LengthUnit.Percent
                ? Mathf.Clamp01(w.value.value / 100f) : 0f;
            if (frac <= now + 0.0005f)   // назад и на месте — сразу
            {
                fill.style.width = new Length(frac * 100f, LengthUnit.Percent);
                return;
            }
            fill.experimental.animation.Start(now, frac, Lvn.UI.LvnMotion.Ms(Lvn.UI.LvnMotion.Calm),
                (e, v) => e.style.width = new Length(v * 100f, LengthUnit.Percent));
        }

        /// <summary>
        /// ЗНАЧЕНИЕ, КОТОРОЕ СМЕНИЛОСЬ, — МИГАЕТ.
        ///
        /// <para>Цифры в сводке загрузок меняются молча, и глаз не замечает,
        /// что показатель ожил: скорость, остаток и очередь выглядят
        /// застывшими, даже когда идут. Короткий полупрозрачный вдох на смене
        /// — самый дешёвый способ показать «это только что обновилось».</para>
        ///
        /// <para>Только НА СМЕНУ: та же строка не мигает, иначе сводка
        /// мельтешит при каждом тике.</para>
        /// </summary>
        public static void SetValue(Label label, string text)
        {
            if (label == null || label.text == text) return;
            label.text = text;
            label.experimental.animation.Start(0.45f, 1f, Lvn.UI.LvnMotion.Ms(Lvn.UI.LvnMotion.Quick),
                (e, v) => e.style.opacity = v);
        }

        public static VisualElement ProgressBar(
            float xFrac, float yFrac, float wFrac, float hFrac,
            Color trackColor, Color fillColor,
            out VisualElement track, out VisualElement fill)
        {
            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = Length.Percent(xFrac * 100f);
            bar.style.top = Length.Percent(yFrac * 100f);
            bar.style.width = Length.Percent(wFrac * 100f);
            bar.style.height = Length.Percent(hFrac * 100f);
            bar.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f), 0f);
            bar.pickingMode = PickingMode.Ignore;

            track = Stretch(new VisualElement());
            track.style.backgroundColor = trackColor;
            bar.Add(track);

            fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.left = 0;
            fill.style.top = 0;
            fill.style.bottom = 0;
            fill.style.width = Length.Percent(0f);
            fill.style.backgroundColor = fillColor;
            bar.Add(fill);

            return bar;
        }
    }
}
