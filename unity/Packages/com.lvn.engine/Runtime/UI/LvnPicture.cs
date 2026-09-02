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

        /// <summary>ПОСТАВИТЬ ГОТОВЫЙ СПРАЙТ ФОНОМ — синхронная половина показа.
        ///
        /// <para>Жила отдельным домом `UiStyle` в одну работу, и дома
        /// разошлись в правиле про углы: этот сбрасывал скругление элемента,
        /// а показ по адресу — нет. Один и тот же спрайт на одной и той же
        /// кнопке получал разные углы в зависимости от того, каким путём его
        /// поставили.</para>
        ///
        /// <para>Правило теперь названо: <b>углы сбрасывает НАРЕЗАННЫЙ спрайт</b>
        /// — он рисует рамку своими краями, и скругление элемента поверх неё
        /// срезало бы её же углы. Ненарезанный спрайт — просто заливка, и
        /// скругление элемента остаётся его собственным делом.</para>
        ///
        /// <para>Пустой спрайт НЕ ТРОГАЕТ элемент: у вызывающего есть запасной
        /// цвет, и затирать его нечем.</para></summary>
        public static void Paint(VisualElement el, Sprite sprite, int slice)
        {
            if (el == null || sprite == null) return;
            el.style.backgroundImage = new StyleBackground(sprite);
            Dress(el, slice);
        }

        /// <summary>Всё, кроме самой картинки: убрать цвет под ней и, если
        /// спрайт нарезан, отдать ему углы. Отдельно от <see cref="Paint"/>,
        /// потому что показ ПО АДРЕСУ ставит картинку сам — ему нужна только
        /// эта половина.</summary>
        private static void Dress(VisualElement el, int slice)
        {
            el.style.backgroundColor = Color.clear; // пусть виден арт, а не цвет под ним
            if (slice > 0)
            {
                LvnChrome.Sharp(el);   // рамка держит углы сама
                Slice(el, slice);
            }
        }

        /// <summary>
        /// ДЕВЯТИСЛОЙКА — углы держат форму, стороны тянутся.
        ///
        /// <para>Обратное <see cref="Fit"/>: там картинку вписывают целиком,
        /// здесь она обязана растянуться, но НЕ ВЕЗДЕ — рамка, подложка поля,
        /// бабл говорящего. Без нарезки углы плывут вместе с размером
        /// элемента, и заметно это на широком экране, а не на том, где
        /// проверяли.</para>
        ///
        /// <para>Правило стояло ПЯТЬЮ написаниями: скин сцены, обшивка
        /// стиля, рамка диалога, бабл говорящего и <see cref="Frame"/> прямо
        /// здесь. У последнего в документации значилось «не позван НИ РАЗУ» —
        /// и это была неправда: зовут из двух мест. Оговорка, бывшая правдой
        /// вчера, врёт увереннее отсутствующей — у неё есть авторитет
        /// места.</para>
        ///
        /// <para>Масштаб (<paramref name="scale"/>) — отдельная ручка: арт
        /// рисуют под плотный экран, и на обычном срез надо ужимать, иначе
        /// рамка съедает содержимое.</para>
        /// </summary>
        public static void Slice(VisualElement el, int all, float scale = 1f)
        {
            if (el == null) return;
            el.style.unitySliceLeft = all;
            el.style.unitySliceRight = all;
            el.style.unitySliceTop = all;
            el.style.unitySliceBottom = all;
            el.style.unitySliceScale = scale;
        }

        /// <summary>Нарезка со СВОИМ срезом у каждой стороны: рамка бывает
        /// несимметричной (у бабла снизу хвостик). Порядок сторон — тот же,
        /// что у темы: x — слева, y — справа, z — сверху, w — снизу.</summary>
        public static void Slice(VisualElement el, Vector4 edges, float scale = 1f)
        {
            if (el == null) return;
            el.style.unitySliceLeft = (int)edges.x;
            el.style.unitySliceRight = (int)edges.y;
            el.style.unitySliceTop = (int)edges.z;
            el.style.unitySliceBottom = (int)edges.w;
            el.style.unitySliceScale = scale;
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
            bool wasHeld = _pins.Holds(el);
            _pins.Hold(el, loader, new[] { sprite });
            if (wasHeld) return;   // подписку вешаем ровно один раз на элемент
            el.RegisterCallback<DetachFromPanelEvent>(_ => _pins.Release(el));
        }

        // Ключ — сам элемент: пин живёт ровно столько, сколько картинка висит в
        // панели. Механизм общий со сценой и скелетами (LvnPinBoard); своё
        // здесь — только ключ и то, что отпускание вешается на уход из панели.
        //
        // Дубль-подписка после повторного Attach безвредна: Release по
        // отсутствующему ключу ничего не делает.
        private static readonly LvnPinBoard<VisualElement> _pins
            = new LvnPinBoard<VisualElement>();

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

        /// <summary>ФОТОСЛОЙ ВО ВЕСЬ РОДИТЕЛЬ — завести, растянуть, вложить и
        /// положить фото.
        ///
        /// <para>Обряд из четырёх шагов, и один из них незаметный:
        /// <c>pickingMode = Ignore</c>. Забудь его — и слой, лежащий поверх
        /// всего, начнёт ЛОВИТЬ НАЖАТИЯ вместо карточки под ним. Проверить это
        /// глазами нельзя: картинка выглядит правильно, не работает тап, и
        /// виноватой кажется кнопка.</para>
        ///
        /// <para>Возвращаем сам слой: вызывающему случается доложить сверху
        /// вуаль или тинт, и порядок этих слоёв — его дело, а не наше.</para>
        /// </summary>
        public static VisualElement Layer(VisualElement parent, string url, ILvnAssets assets,
                                          bool cover = true, string what = "photo")
        {
            if (parent == null) return null;
            var el = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(el);
            parent.Add(el);
            Photo(el, url, assets, cover, what);
            return el;
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
        /// РАМКА КАРТИНКОЙ: загрузить арт и растянуть его девятислойкой.
        ///
        /// <para>Раньше здесь стояла ВТОРАЯ нарезка, написанная своими
        /// строками, и в документации значилось «способ написан, но не позван
        /// НИ РАЗУ». Обе половины были неправдой: зовут её из двух мест
        /// (обшивка экранов и вкладка гардероба со своим артом), а нарезка
        /// теперь одна — <see cref="Slice"/>.</para>
        ///
        /// <para>Ждёт того же адреса, что и обычный показ: две просьбы к
        /// одному элементу обязаны разбираться по одному правилу, иначе
        /// победит не последняя, а доехавшая позже.</para>
        /// </summary>
        public static System.Threading.Tasks.Task Frame(
            VisualElement el, string url, int slice, ILvnAssets assets)
            => ShowAsync(el, url, assets, e => Dress(e, slice));

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
        public static System.Threading.Tasks.Task AssignAsync(
            VisualElement el, string url, ILvnAssets assets)
            => ShowAsync(el, url, assets, null);

        /// <summary>ПОКАЗАТЬ КАРТИНКУ, КОГДА ОНА ПРИЕДЕТ — общая часть всех
        /// присвоений фона.
        ///
        /// <para>Здесь живёт сторож устаревания: пока картинка едет, элементу
        /// могли назначить ДРУГОЙ адрес, и медленный ответ обязан промолчать.
        /// Сторож стоял в двух телах отдельно, и это самое опасное место для
        /// копии: заведи третье присвоение, забудь коробку — и на быстром
        /// пролистывании карточка покажет чужую обложку. Ошибки при этом нет
        /// нигде.</para>
        ///
        /// <para>Что делать сверх показа — довод: обычному присвоению ничего,
        /// рамке ещё очистить заливку и нарезать девять частей.</para>
        /// </summary>
        private static async System.Threading.Tasks.Task ShowAsync(
            VisualElement el, string url, ILvnAssets assets,
            System.Action<VisualElement> then)
        {
            if (el == null || string.IsNullOrEmpty(url) || assets == null) return;
            var box = _awaited.GetValue(el, _ => new System.Runtime.CompilerServices.StrongBox<string>(null));
            box.Value = url;
            try
            {
                var sprite = await assets.LoadSpriteAsync(url, System.Threading.CancellationToken.None);
                if (sprite == null || box.Value != url) return;
                el.style.backgroundImage = new StyleBackground(sprite);
                Pin(el, sprite, assets);
                then?.Invoke(el);
            }
            catch { /* пропавший арт не повод ронять экран */ }
        }
    }
}
