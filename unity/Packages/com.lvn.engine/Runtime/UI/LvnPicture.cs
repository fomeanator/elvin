using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// КАРТИНКА В СВОЁМ МЕСТЕ — как она вписывается в отведённый кадр.
    ///
    /// <para>Дом стоит в UI-слое ДВИЖКА, а не оболочки: вписывание нужно и фону
    /// темы, и слою интерфейса, а они про оболочку не знают (границы сборок).</para>
    /// </summary>
    public static class LvnPicture
    {
        /// <summary>
        /// КАК КАРТИНКА ВПИСЫВАЕТСЯ В СВОЁ МЕСТО — три строки стиля, которые
        /// стояли двадцатью пятью копиями.
        ///
        /// <para>Правило простое: заполнить кадр без полей (<c>Cover</c>) или
        /// показать целиком (<c>Contain</c>), по центру и без размножения. Но
        /// написано оно было по месту, и хватало забыть ОДНУ строку из трёх,
        /// чтобы получить свой баг: без центрирования картинка липнет к
        /// левому-верхнему углу, без запрета повтора мелкий арт замостит плитку
        /// собой, а без режима вписывания растянется по кадру.</para>
        ///
        /// <para>Здесь же место для будущего общего решения: рамка сглаживания,
        /// поведение при отсутствующем арте, ступень качества. Пока их правит
        /// каждый экран сам, менять правило нельзя — только повторять.</para>
        /// </summary>
        public static T Fit<T>(T el, bool cover = true) where T : VisualElement
        {
            if (el == null) return el;
            el.style.backgroundSize = new BackgroundSize(
                cover ? BackgroundSizeType.Cover : BackgroundSizeType.Contain);
            el.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            el.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            el.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            return el;
        }

        /// <summary>
        /// ЧТО НА ЭКРАНЕ — НЕ ТРОГАТЬ. Закрепить спрайт за элементом, пока тот
        /// в панели: кэш вытесняет по давности использования и не знает, что
        /// картинку прямо сейчас показывают.
        ///
        /// <para>Правило родилось из живого бага 27.08 — обложки в хабе белели
        /// после прогулки по гардеробу, арт героини после главы. Починили его
        /// в оболочке, там пин и остался; ядро сцены оболочку не видит, и
        /// галерея CG внутриигрового меню грузила картинки МИМО пина. Тот же
        /// баг, тот же экран, только другая дверь — а по коду не видно, потому
        /// что дом стоял этажом выше (см. роль «дом стоял не на том этаже»).</para>
        ///
        /// <para>Пин снимается сам, когда элемент уходит из панели. Повторный
        /// показ другой картинки отпускает прежнюю: держать обе значило бы
        /// запирать память ровно тем, что игрок уже пролистал.</para>
        /// </summary>
        public static void Pin(VisualElement el, Sprite sprite, ILvnAssets assets)
        {
            var loader = (assets as CachingAssets)?.Loader;
            if (el == null || loader == null || sprite == null) return;
            if (_pins.TryGetValue(el, out var old))
            {
                if (ReferenceEquals(old.sprite, sprite)) return;
                old.loader?.PinSprite(old.sprite, false);
                loader.PinSprite(sprite, true);
                _pins[el] = (loader, sprite);
                return;
            }
            loader.PinSprite(sprite, true);
            _pins[el] = (loader, sprite);
            el.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                // Guard по словарю: дубль-колбэк после повторного Attach
                // становится no-op — пин снимается ровно один раз.
                if (_pins.TryGetValue(el, out var cur))
                {
                    cur.loader?.PinSprite(cur.sprite, false);
                    _pins.Remove(el);
                }
            });
        }

        private static readonly System.Collections.Generic.Dictionary<
            VisualElement, (Lvn.Content.ContentLoader loader, Sprite sprite)> _pins
            = new System.Collections.Generic.Dictionary<
                VisualElement, (Lvn.Content.ContentLoader, Sprite)>();

        /// <summary>Подложка под картинку: элемент, который не ловит касания и
        /// уже знает, как вписывать арт. Ровно то, что писали руками перед
        /// каждой загрузкой фона.</summary>
        public static VisualElement Picture(bool cover = true)
            => Fit(new VisualElement { pickingMode = PickingMode.Ignore }, cover);

        /// <summary>
        /// ФОТОГРАФИЯ: обложка, фон главы, аватар, кадр галереи. Вписывается в
        /// своё место и НЕ искажается.
        ///
        /// <para>Раньше показ картинки был не одним действием, а двумя, и жили
        /// они на разных этажах: вписывание (<see cref="Fit"/>) — в движке,
        /// загрузка (<c>ScreenUi.SetBg</c>) — в оболочке. Загрузка при этом
        /// работает и без вписывания, молча: картинка встаёт, растянутая под
        /// форму своего места. На квадратной плитке это почти незаметно, на
        /// полноэкранном фоне — заметно всем, но только на устройстве с другим
        /// соотношением сторон, чем у того, где проверяли.</para>
        ///
        /// <para>Так и вышло: фон загрузочного экрана, фон подъёма и фон входа
        /// растягивались — три места из тридцати четырёх, и найти их можно было
        /// только пересчитав все.</para>
        ///
        /// <para><paramref name="cover"/>: заполнить место без полей (обложка,
        /// фон) или показать целиком (логотип, портрет в рамке).</para>
        /// </summary>
        public static void Photo(VisualElement el, string url, ILvnAssets assets,
                                 bool cover = true, string what = "photo")
        {
            if (el == null) return;
            Fit(el, cover);
            Lvn.LvnAsync.Fire(AssignAsync(el, url, assets), what);
        }

        /// <summary>
        /// ОБШИВКА: рамка карточки, подложка поля, полоса прогресса, туман.
        /// Тянется по своему месту — это и есть её работа, вписывать её нельзя.
        ///
        /// <para>Отдельный глагол нужен не ради красоты: пока показ был один на
        /// оба случая, «вписать» оставалось решением вызывающего — и решением
        /// НЕВИДИМЫМ, потому что забытое вписывание выглядит как обычная
        /// картинка. Теперь картинка обязана назвать, чем она пришла.</para>
        /// </summary>
        public static void Skin(VisualElement el, string url, ILvnAssets assets, string what = "skin")
            => Lvn.LvnAsync.Fire(AssignAsync(el, url, assets), what);

        /// <summary>
        /// РАМКА, ТЯНУЩАЯСЯ ПО КРАЯМ (девятислойка): углы держат форму, стороны
        /// растягиваются. Именно то, чего не хватает обшивке, — и потому здесь
        /// оговорка: способ написан, но не позван НИ РАЗУ. Рамки, подложки
        /// полей и полосы прогресса до сих пор показываются простым
        /// растяжением, отчего их углы плывут вместе с размером элемента.
        /// </summary>
        public static async System.Threading.Tasks.Task Frame(
            VisualElement el, string url, int slice, ILvnAssets assets)
        {
            if (el == null || string.IsNullOrEmpty(url) || assets == null) return;
            var box = _awaited.GetValue(el, _ => new System.Runtime.CompilerServices.StrongBox<string>(null));
            box.Value = url;
            try
            {
                var sprite = await assets.LoadSpriteAsync(url, System.Threading.CancellationToken.None);
                // Та же сверка адреса, что у показа: рамка живёт в той же
                // таблице ожиданий, иначе две просьбы к одному элементу
                // разошлись бы по разным правилам.
                if (sprite == null || box.Value != url) return;
                el.style.backgroundImage = new StyleBackground(sprite);
                Pin(el, sprite, assets);
                el.style.backgroundColor = Color.clear;
                if (slice > 0)
                {
                    el.style.unitySliceLeft = slice;
                    el.style.unitySliceRight = slice;
                    el.style.unitySliceTop = slice;
                    el.style.unitySliceBottom = slice;
                }
            }
            catch { /* пропавший арт не повод ронять экран */ }
        }

        // ЧЕГО ЖДЁТ ЭТОТ ЭЛЕМЕНТ ПРЯМО СЕЙЧАС.
        //
        // Один и тот же элемент просят показать разное быстрее, чем доезжает
        // первое: игрок листает галерею стрелкой, тапает свотчи цвета волос,
        // перелистывает карточки. Побеждала не последняя просьба, а та, что
        // доехала позже — картинка от одной сцены под подписью от другой.
        //
        // Сцена от этого класса гонок закрыта поколениями (LvnStageClock).
        // Здесь хватает адреса: показать надо ровно то, что попросили
        // последним. Таблица слабая — элемент, ушедший из дерева, уносит запись
        // с собой.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            VisualElement, System.Runtime.CompilerServices.StrongBox<string>> _awaited
            = new System.Runtime.CompilerServices.ConditionalWeakTable<
                VisualElement, System.Runtime.CompilerServices.StrongBox<string>>();

        /// <summary>Загрузить арт и поставить его фоном элемента. Отсутствующий
        /// арт — не беда: элемент остаётся с тем, что у него было. Устаревший —
        /// тем более: пришедший позже ответ на отменённую просьбу не имеет права
        /// перекрасить элемент.</summary>
        public static async System.Threading.Tasks.Task AssignAsync(
            VisualElement el, string url, ILvnAssets assets)
        {
            if (el == null || string.IsNullOrEmpty(url) || assets == null) return;
            var box = _awaited.GetValue(el, _ => new System.Runtime.CompilerServices.StrongBox<string>(null));
            box.Value = url;
            try
            {
                var sprite = await assets.LoadSpriteAsync(url, System.Threading.CancellationToken.None);
                if (sprite != null && box.Value == url)
                {
                    el.style.backgroundImage = new StyleBackground(sprite);
                    Pin(el, sprite, assets);
                }
            }
            catch { /* пропавший арт не повод ронять экран */ }
        }
    }
}
