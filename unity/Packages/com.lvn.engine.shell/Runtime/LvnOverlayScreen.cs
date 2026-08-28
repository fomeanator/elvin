using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// НАКЛАДНОЙ ЭКРАН — общий жизненный цикл: проявиться, дождаться закрытия,
    /// погаснуть.
    ///
    /// <para>Восемь экранов (профиль, настройки, магазин, наборы, скины,
    /// лидерборд, ежедневные награды, деталь новеллы) держали этот цикл
    /// СВОЕЙ копией — в коде даже стояло «mirrors StoreScreen». Вместе с ним
    /// копировались `Round`, `ClearBorder`, `Hide` и поля состояния: без общего
    /// предка у каждого экрана заводится собственная версия одного и того же,
    /// и однажды одна из них расходится.</para>
    ///
    /// <para>Тонкость, ради которой цикл и вынесен целиком: <see cref="Hide"/>,
    /// вызванный ВО ВРЕМЯ проявления, обязан отменить открытие. Иначе ожидание
    /// остаётся висеть на обещании, которое никто уже не выполнит, — экран
    /// закрыт, а вызвавший его код ждёт вечно.</para>
    /// </summary>
    public abstract class LvnOverlayScreen : VisualElement
    {
        // Ожидание закрытия — через LvnCloseGate: связка «создать, подписать
        // отмену, дождаться, отпустить» стояла тут, в галерее и в гардеробе, и
        // каждая её часть обязательна по своей причине.
        private readonly LvnCloseGate _gate = new LvnCloseGate();
        private bool _open;
        private VisualElement _sheet;

        /// <summary>Длительность проявления и угасания — В ТЕМП АКТЁРОВ:
        /// фактический вход персонажа ~0,2 с (0.35 × шкалы), и попапы дышат
        /// той же длительностью — «дорого» читается именно из согласованности
        /// (решение Ильи 25.08).</summary>
        protected virtual float FadeSeconds => 0.2f;

        /// <summary>ЕДИНЫЙ ВРАППЕР ЛИСТА (решение Ильи 25.08): накладные
        /// экраны — эдакие попапы, а не экраны, и выглядели «коротким
        /// контентом на тёмном полотне». Наследник отдаёт сюда свой лист —
        /// и получает общий вид (стекло UiGlass, окантовка с акцентной
        /// верхней кромкой) и общую хореографию: скрим фейдится, лист
        /// подъезжает снизу со scale — это делает ShowAsync сам.</summary>
        protected void AdoptSheet(VisualElement sheet) => AdoptSheet(sheet, fullscreen: false);

        /// <summary>Полноэкранный режим (решение Ильи 26.08: «магазин и профиль
        /// не модалки»): лист занимает весь экран под навбаром — раздел, а не
        /// окно; атмосфера меню дышит сквозь полупрозрачный тон.</summary>
        protected void AdoptSheet(VisualElement sheet, bool fullscreen)
            => AdoptSheet(sheet, fullscreen, null);

        /// <summary>
        /// То же, но с ЦВЕТОМ ОТ ЭКРАНА — когда новелла назвала свой
        /// (<c>panel_color</c>).
        ///
        /// <para>Без этого параметра авторский цвет молча пропадал: экран
        /// настроек ставил его строкой выше, а обёртка тут же перекрывала своим
        /// — «полупрозрачная Полночь». Настройка в манифесте есть, работает
        /// наполовину: попап её слушает, лист настроек нет.</para>
        /// </summary>
        protected void AdoptSheet(VisualElement sheet, bool fullscreen, Color? tint)
        {
            _sheet = sheet;
            if (sheet == null) return;
            if (fullscreen)
            {
                sheet.style.left = 0; sheet.style.right = 0;
                sheet.style.top = 96; // под строкой единого навбара
                sheet.style.bottom = 0;
            }
            // Просто полупрозрачная Полночь (решение Ильи 26.08): блюр-стекло
            // на живом контенте давало грязь и на попапах снято совсем. Цвет от
            // новеллы берём КАК ЕСТЬ, вместе с его прозрачностью: раз автор его
            // назвал, он знает, чего хочет.
            var bg = tint ?? LvnTokens.PanelBg;
            sheet.style.backgroundColor = tint.HasValue
                ? bg : new Color(bg.r, bg.g, bg.b, 0.94f);
            LvnChrome.Edge(sheet);
            LvnChrome.Round(sheet, fullscreen ? 0f : LvnTokens.Radius + 6f);
            // Акцентная кромка сверху — «крышка» попапа: даёт листу край,
            // которого не хватало на тёмном полотне.
            sheet.style.borderTopWidth = 2.5f;
            sheet.style.borderTopColor = LvnTokens.Accent;
        }

        /// <summary>
        /// ЛИСТ НАКЛАДНОГО ЭКРАНА: положение, поля, вид — одним вызовом.
        ///
        /// <para>Собирался в четырёх экранах руками, и числа разошлись: 5%/6%,
        /// 6%/8%, 4%/5%. Разница не задумана — нигде нет ни слова, почему
        /// настройки уже витрины скинов; это ровно «подобранное на месте
        /// число», от которого предостерегает карта домов.</para>
        ///
        /// <para>Хуже: экраны задавали ещё и ВИД — фон, скругление, кромку, — а
        /// <see cref="AdoptSheet"/> строкой ниже перекрывал его своим. Эти
        /// строки ничего не делали, но выглядели работающими: правишь
        /// скругление в экране — ничего не меняется.</para>
        ///
        /// <para>Отступы остаются у экрана: сколько воздуха внутри — вопрос его
        /// содержимого, а не общего облика.</para>
        /// </summary>
        /// <summary>Перечитать подписи ШАПКИ (заголовок, кнопка закрытия).
        /// Список экран пересобирает сам; шапку он строит один раз, и без этого
        /// она остаётся на прежнем языке — а её игрок видит первой.</summary>
        protected virtual void RedressChrome() { }

        protected VisualElement Sheet(float sideInset = 5f, float topInset = 6f, Color? tint = null)
        {
            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Length.Percent(sideInset);
            sheet.style.right = Length.Percent(sideInset);
            sheet.style.top = Length.Percent(topInset);
            sheet.style.bottom = Length.Percent(topInset);
            Add(sheet);
            AdoptSheet(sheet, fullscreen: false, tint);
            return sheet;
        }

        /// <summary>
        /// ЗАГОЛОВОК РАЗДЕЛА — один вид на все накладные экраны.
        ///
        /// <para>Собирался пятью экранами одинаковыми четырьмя строками, и
        /// размер разошёлся: 44 у профиля и магазина, 42 у ежедневной награды,
        /// 40 у таблицы лидеров и витрины скинов. Причина нигде не названа —
        /// это не замысел, а след того, что каждый экран набирал заголовок
        /// заново. Игрок, переходя между разделами, видит, как заголовок
        /// «дышит» без причины.</para>
        ///
        /// <para>Размер взят крупный (44): заголовок раздела — верхняя ступень
        /// иерархии на экране, и уменьшать её ради того, чтобы уместить рядом
        /// вкладки, значит чинить компоновку не тем местом.</para>
        ///
        /// <para>ПРИНИМАЕТ ИСТОЧНИК, А НЕ СТРОКУ. Заголовок стоит в шапке, а
        /// шапку экраны собирают в конструкторе и при переодевании не трогают:
        /// пересобирается тело. Готовая строка обрывала связь со словарём — и
        /// «Профиль», «Магазин», «Гардероб» оставались на прежнем языке, пока
        /// всё под ними уже переключилось. Со источником подпись перечитывает
        /// себя сама.</para>
        /// </summary>
        protected static Label SectionTitle(System.Func<string> text, float size = 44f)
        {
            var title = Lvn.UI.LvnRedress.Bind(new Label(), text);
            LvnChrome.Heading(title);
            title.style.color = LvnTokens.Text;
            title.style.fontSize = size;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            return title;
        }

        /// <summary>ЛЕНТА ВКЛАДОК (решение Ильи 26.08): раздел въезжает сбоку
        /// по направлению навигации (+1 справа, −1 слева; 0 — прежний подъезд
        /// снизу для попапов). Закрытие — обратно в ту же сторону.</summary>
        public int SlideDirection;

        /// <summary>Пролёт промежуточной вкладки: переход «Главная → Профиль»
        /// ПРОЕЗЖАЕТ магазин — экран проносится через кадр без остановки.</summary>
        public async Task FlyThroughAsync(int dir, int ms = 240)
        {
            style.display = DisplayStyle.Flex;
            style.opacity = 1f;
            OnOpening();
            float w = resolvedStyle.width > 0 ? resolvedStyle.width : 1080f;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.experimental.animation.Start(0f, 1f, ms, (e, p) =>
            {
                e.style.translate = new Translate(Mathf.Lerp(dir * w, -dir * w, p), 0f);
                if (p >= 1f) tcs.TrySetResult(true);
            });
            await tcs.Task;
            style.display = DisplayStyle.None;
            style.translate = new Translate(0f, 0f);
            OnClosed();
        }

        // Хореография листа: подъезд снизу + лёгкий scale. Скрим (сам экран)
        // фейдится параллельно базовым FadeAsync; ждать лист отдельно не надо —
        // длительность одна.
        private void PlaySheet(bool opening)
        {
            if (SlideDirection != 0)
            {
                // Слайд ВСЕГО экрана по ленте вкладок.
                float w = resolvedStyle.width > 0 ? resolvedStyle.width : 1080f;
                float from2 = opening ? SlideDirection * w : 0f;
                float to2 = opening ? 0f : SlideDirection * w;
                this.experimental.animation.Start(0f, 1f,
                    Mathf.RoundToInt(FadeSeconds * 1000f * 1.4f), (e, p) =>
                {
                    float k = 1f - Mathf.Pow(1f - p, 3f);
                    e.style.translate = new Translate(Mathf.Lerp(from2, to2, k), 0f);
                });
                return;
            }
            var s = _sheet;
            if (s == null) return;
            int ms = Mathf.RoundToInt(FadeSeconds * 1000f * (opening ? 1f : 0.8f));
            s.experimental.animation.Start(0f, 1f, Mathf.Max(1, ms), (_, p) =>
            {
                float e = 1f - Mathf.Pow(1f - p, 3f);
                float k = opening ? e : 1f - e;
                s.style.translate = new Translate(0f, Mathf.Lerp(26f, 0f, k));
                float sc = Mathf.Lerp(0.965f, 1f, k);
                s.style.scale = new Scale(new Vector2(sc, sc));
            });
        }

        /// <summary>Открыт ли экран сейчас.</summary>
        protected bool IsOpen => _open;

        /// <summary>Открыть и ждать закрытия. Возвращает true, если закрыли
        /// подтверждением (<see cref="Close"/>), и false — если отменой.</summary>
        public async Task<bool> ShowAsync(CancellationToken ct = default)
        {
            if (_open) return false;
            _open = true;
            style.display = DisplayStyle.Flex;
            OnOpening();
            PlaySheet(opening: true);
            await ScreenFx.FadeAsync(this, 0f, 1f, FadeSeconds, ct);
            if (!_open) return false;   // закрыли прямо во время проявления

            bool confirmed;
            try { confirmed = await _gate.WaitAsync(ct); }
            finally
            {
                PlaySheet(opening: false);
                await ScreenFx.FadeAsync(this, 1f, 0f, FadeSeconds, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
                OnClosed();
            }
            return confirmed;
        }

        /// <summary>ВКЛАДОЧНЫЙ режим (навигатор ленты): показать/спрятать как
        /// страницу — без модального ожидания Close. Анимацию везёт навигатор.</summary>
        public void ShowAsTab()
        {
            style.display = DisplayStyle.Flex;
            OnOpening();   // экраны пересобирают тут своё тело: Clear + сборка

            // Показываем ПОСЛЕ первой раскладки: правило и страховку держит
            // Монтажёр, здесь только просьба.
            Lvn.UI.LvnMontage.RevealWhenLaidOut(this);
        }

        public void HideAsTab()
        {
            style.display = DisplayStyle.None;
            style.translate = new Translate(0f, 0f);
            OnClosed();
        }

        /// <summary>Убрать немедленно, без угасания: смена главы, выход в меню.</summary>
        public virtual void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _gate.Release(false);
        }

        /// <summary>
        /// Закрыть ПОДТВЕРЖДЕНИЕМ — тем, чего экран и ждал: «играть», «купить»,
        /// «сохранить».
        ///
        /// <para>⚠️ Смысл противоположен прежнему одноимённому методу экранов:
        /// там <c>Close</c> означал «уйти ни с чем» и возвращал false. При
        /// переводе экрана детали на этот класс кнопка «назад» продолжала звать
        /// Close — и стала бы ЗАПУСКАТЬ игру. Поймано глазами; автотестом это не
        /// ловится: асинхронный цикл в EditMode не прокручивается ни блокирующим
        /// ожиданием (дедлок главного потока), ни покадровым (нет кадров).
        /// Поэтому — отмена всегда через <see cref="Cancel"/>.</para>
        /// </summary>
        protected void Close() => _gate.Release(true);

        /// <summary>Отменить: крестик, «назад», системная кнопка возврата.</summary>
        protected void Cancel() => _gate.Release(false);

        /// <summary>Отмена СНАРУЖИ — роутер оболочки закрывает верхнюю модаль
        /// по системной «назад». Семантика ровно как у <see cref="Cancel"/>.</summary>
        public void RequestCancel() => Cancel();

        /// <summary>Наследник может подготовить данные перед проявлением.</summary>
        protected virtual void OnOpening() { }

        /// <summary>И прибраться после угасания.</summary>
        protected virtual void OnClosed() { }
    }
}
