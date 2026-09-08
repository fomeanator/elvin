using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВКЛАДКА ГАРДЕРОБА — тонкий хост ЕДИНОГО листа (решение Ильи 27.08:
    /// «один UI гардероба, плашка из игры»). Весь интерфейс — сам
    /// <see cref="WardrobeSheet"/>: заголовок, «Во весь рост», ростер
    /// персонажей, оси, лента карточек + карусель, Отменить/Выбрать с
    /// покупкой. Вкладка даёт ему только плашку над нижним меню и режимы:
    /// зеркало — кукла сцены меню (фаворит), витрина — ПОЛНЫЙ каталог скинов
    /// (магазин), пилюли валют скрыты (их несёт навбар). Переключение
    /// персонажа в ростере назначает фаворита меню — он тут же встаёт на
    /// передний план всех экранов.
    /// </summary>
    public sealed class WardrobeTabScreen : LvnOverlayScreen, ILvnContentAware
    {
        // НЕ readonly: контент обновляется на лету (ApplyLiveUpdate), и вкладка
        // обязана узнать об этом наравне с каруселью и хабом. Пока поле было
        // неизменяемым, гардероб после обновления показывал ПРЕЖНИЙ каталог:
        // новых нарядов нет, снятые с продажи остались — и объяснить это игроку
        // нечем, потому что на соседних экранах всё уже новое.
        private LvnManifest _manifest;

        /// <summary>Принять свежий манифест. Лист пересоберётся при следующем
        /// открытии: перестраивать его сейчас значило бы дёрнуть примерку из-под
        /// игрока, если вкладка открыта.</summary>
        /// <inheritdoc cref="ILvnContentAware.SetContent"/>
        public void SetContent(LvnManifest manifest)
        {
            if (manifest == null) return;
            _manifest = manifest;
            _sheet?.SetContent(manifest);
            StageBackdrop(manifest.ui?.browse?.skin);
        }

        private bool _stageGlass;

        /// <summary>ЗАДНИК В ОБЛИКЕ «СЦЕНА» — СТЕКЛО. Пробовали рамку-арт
        /// card-back.png девятидольно: тёмная штрихованная середина, растянутая
        /// в широкий низкий короб, смотрелась глухой плитой («тёмный фон как-то
        /// неказисто» — Илья 08.09). Стекло показывает сцену сквозь лист, как
        /// панель реплики в новелле, а кромка и скругление темы остаются.
        /// Без облика — плашка темы, как была. Один раз: живые обновления
        /// манифеста стекло не пересобирают.</summary>
        private void StageBackdrop(string skin)
        {
            if (string.IsNullOrEmpty(skin) || _stageGlass) return;
            _stageGlass = true;
            // СТЕКЛО НЕ НА САМОЙ ПАНЕЛИ: оно включает обрезку по границам хоста, а
            // столбики героев и эмоций стоят ВЫШЕ панели — так они и пропали
            // («нет выбора героя, нет эмоций» — Илья 08.09). Стекло и рамка
            // живут на своих подложках внутри панели.
            LvnStageKit.GlassSheet(_panel, skin, _assets, LvnTokens.Radius);
            // содержимое ужимается внутрь рамки: 15 dp сверху и снизу, 10 по бокам («паддинг рамке 15 10» — Илья 08.09)
            LvnAir.Pad(_panel, LvnStageKit.D(10f), LvnStageKit.D(15f));
        }
        private readonly ILvnAssets _assets;
        private readonly VisualElement _panel;
        private WardrobeSheet _sheet;
        private bool _live;    // цикл показов листа жив, пока вкладка на экране
        private bool _peeking; // «Во весь рост»: плашка спрятана до касания

        // ── крючки хоста ────────────────────────────────────────────────────
        //
        // ВКЛАДКА ИХ НЕ ХРАНИТ ВТОРОЙ КОПИЕЙ, а передаёт листу сразу. Хранила:
        // лист создаётся лениво (первый показ), хост вешает крючки раньше — и
        // связывать их приходилось на КАЖДОМ показе, «на случай, если повесили
        // после конструктора». Две копии одного крючка живут врозь ровно до
        // первой правки: повесил новый обработчик между показами — до листа он
        // не дошёл, и кнопка «в магазин» молча ничего не делает.
        private System.Func<Task> _openStore;
        private System.Func<string, string, Task<bool>> _confirmTopUp;
        private System.Func<string, string, Task> _alert;

        /// <summary>Открыть быстрый магазин (модаль) — вешает NovelShell.</summary>
        public System.Func<Task> OpenStore
        {
            get => _openStore;
            set { _openStore = value; if (_sheet != null) _sheet.OpenStore = value; }
        }
        /// <summary>Подтверждение «не хватает — в магазин?» — вешает NovelShell.</summary>
        public System.Func<string, string, Task<bool>> ConfirmTopUp
        {
            get => _confirmTopUp;
            set { _confirmTopUp = value; if (_sheet != null) _sheet.ConfirmTopUp = value; }
        }
        /// <summary>Финальное «всё ещё не хватает» — вешает NovelShell.</summary>
        public System.Func<string, string, Task> Alert
        {
            get => _alert;
            set { _alert = value; if (_sheet != null) _sheet.Alert = value; }
        }

        // Текущий персонаж вкладки: фаворит меню, иначе героиня по умолчанию.
        private string Entity
        {
            get
            {
                // Строже прежнего: имя без облика — не выбор. Здесь запасную
                // брали как есть, и новелла без её облика давала пустоту.
                return LvnFavorite.Entity(_manifest);
            }
        }

        // Ростер персонажей: явный ui.wardrobe.characters, иначе одна героиня.
        private List<(string id, string name)> Roster()
        {
            var list = new List<(string, string)>();
            var explicitRoster = _manifest?.ui?.wardrobe?.characters;
            if (explicitRoster != null)
                foreach (var id in explicitRoster)
                    if (!string.IsNullOrEmpty(id) && _manifest.sprites != null
                        && _manifest.sprites.TryGetValue(id, out var d))
                        list.Add((id, LvnWords.Name("actor", id, d?.name)));
            var entity = Entity;
            if (list.Count == 0 && !string.IsNullOrEmpty(entity))
                list.Add((entity, _manifest?.sprites != null
                    && _manifest.sprites.TryGetValue(entity, out var e)
                        ? LvnWords.Name("actor", entity, e?.name) : entity));
            return list;
        }

        public WardrobeTabScreen(LvnManifest manifest, ILvnAssets assets)
        {
            _manifest = manifest;
            _assets = assets;
            style.backgroundColor = Color.clear;
            pickingMode = PickingMode.Ignore;

            // Плашка листа — снизу, над нижним меню; героиня сцены видна над
            // ней. Вид — как игровая панель: Полночь, скруглённый верх,
            // акцентная кромка (то самое «дорого» единого стиля).
            _panel = new VisualElement();
            LvnChrome.BottomStrip(_panel, 10f, PanelGap);   // низ уточняет LiftAboveNav
            var bg = LvnTokens.PanelBg;
            _panel.style.backgroundColor = UiColor.WithAlpha(bg, 0.94f);
            LvnChrome.Edged(_panel, LvnTokens.Radius);
            LvnChrome.Lid(_panel);
            LvnAir.Pad(_panel, LvnTokens.Space3, LvnTokens.Space2);
            Add(_panel);

            // «Во весь рост» прячет плашку — вернуть её обязано ЛЮБОЕ касание
            // (обещание шита): на время пика вкладка сама ловит тапы. Только
            // ПРЯМОЕ (target == вкладка): клик самой кнопки «Во весь рост»
            // всплывает сюда же и мгновенно возвращал плашку — кнопка «не
            // работала» (живой репорт 27.08).
            RegisterCallback<ClickEvent>(e =>
            {
                if (!_peeking || e.target != this) return;
                SetPeek(false);
                _sheet?.RefocusSection(); // вернуть зум раздела после «Во весь рост»
            });
        }

        /// <summary>«Во весь рост» просит убрать ХРОМ ОБОЛОЧКИ (навбар, шапку,
        /// кружок). Экран до него не дотягивается сам и не должен: чужие слои
        /// прячет их хозяин. Ставит оболочка при рождении вкладки.</summary>
        public System.Action<bool> PeekChrome;

        /// <summary>СКОЛЬКО ЗАНЯЛА НИЖНЯЯ ЛЕНТА — ставит оболочка (у неё есть
        /// хаб). Панель гардероба встаёт НАД лентой, и высоту ленты знает
        /// только она сама: в облике «сцена» она рисованная и выше обычной.
        /// Пусто — остаёмся на прежнем запасе.</summary>
        public System.Func<float> NavHeight;

        private void SetPeek(bool on)
        {
            _peeking = on;
            pickingMode = on ? PickingMode.Position : PickingMode.Ignore;
            // КАДР ОСВОБОЖДАЕТСЯ ЦЕЛИКОМ. Уезжала одна плашка листа, а навбар
            // с шапкой оставались поверх куклы — «нажимаю на полный экран, не
            // скрывается нижнее меню и навбар» (Илья 08.09). «Во весь рост»
            // означает рост целиком, а не «лист уехал».
            PeekChrome?.Invoke(on);
            LvnAsync.Fire(PeekAsync(on), "WardrobePeek");
        }

        private int _peekEpoch;

        /// <summary>ПЛАШКА УЕЗЖАЕТ, А НЕ ПРОПАДАЕТ. «Во весь рост» переключало
        /// <c>display</c>, и половина экрана исчезала между кадрами — это
        /// читается как сбой, а не как жест (Илья). Теперь она уходит вниз,
        /// растворяясь, и так же возвращается; из РАСКЛАДКИ убираем только
        /// после ухода — иначе кукла дёрнется в первый же кадр.</summary>
        private async System.Threading.Tasks.Task PeekAsync(bool on)
        {
            if (_panel == null) return;
            // Сторож поколения: по плашке можно щёлкать быстрее, чем идёт ход,
            // и два хода иначе доводили бы её каждый к своему концу.
            int mine = ++_peekEpoch;
            const float Drop = 28f;
            if (!on) _panel.style.display = DisplayStyle.Flex;   // вернуть ДО проявления
            await LvnMotion.PlayAsync(_panel, LvnMotion.Normal, (e, p) =>
            {
                if (mine != _peekEpoch) return;
                float k = on ? p : 1f - p;      // k: 0 — на месте, 1 — убрана
                e.style.opacity = 1f - k;
                e.style.translate = new Translate(0, Drop * k);
            });
            if (mine != _peekEpoch) return;     // нас обогнал следующий ход
            _panel.style.opacity = on ? 0f : 1f;
            _panel.style.translate = new Translate(0, on ? Drop : 0f);
            if (on) _panel.style.display = DisplayStyle.None;
        }

        private void EnsureSheet()
        {
            if (_sheet != null) return;
            var ui = _manifest?.ui;
            _sheet = new WardrobeSheet(ui?.wardrobe, ui?.dialogue, ui?.choices, _assets);
            _sheet.SetContent(_manifest);
            _sheet.HideBalances = true;    // валюты уже в навбаре
            _sheet.TabMode = true;         // уйти можно навбаром — «Отменить» вправе гаснуть
            _sheet.OnlySeen = false;       // ВИТРИНА: весь каталог скинов
            _sheet.MarkSeenOnShow = false; // …но коллекцию игры не раскрывает
            _sheet.OnPeek = on => SetPeek(on);
            // Смена персонажа в ростере = назначить фаворита меню: кукла
            // сцены меняется хостом (NovelApp слушает LvnPrefs.Changed).
            _sheet.OnCharacterPicked = (_, to) => LvnPrefs.MenuFavorite = to;
            // Крючки, повешенные до того, как лист появился, догоняют его здесь
            // — один раз, а не на каждом показе.
            _sheet.OpenStore = _openStore;
            _sheet.ConfirmTopUp = _confirmTopUp;
            _sheet.Alert = _alert;
            _panel.Add(_sheet);
        }

        /// <summary>Переодеться: шапка — базой, а ростер надо ПЕРЕСПРОСИТЬ.
        /// Имена персонажей уходят в лист готовыми строками (<c>SetRoster</c>),
        /// и после смены языка лист пересобрал бы их из прежних — тех же строк
        /// на прежнем языке.</summary>
        protected override void RedressBody()
        {
            if (_sheet != null) _sheet.SetRoster(Roster());
        }

        protected override void OnOpening()
        {
            EnsureSheet();
            LiftAboveNav();
            SetPeek(false);
            LvnAsync.Fire(RunSheetLoopAsync(), "WardrobeTabLoop");
        }

        /// <summary>ПОДНЯТЬ ПАНЕЛЬ НАД ЛЕНТОЙ. Запас снизу был числом (140),
        /// и с рисованной лентой облика «сцена» кнопки «Отменить/Выбрать»
        /// оказались под меню. Спрашиваем ленту, сколько она заняла, и
        /// добавляем прежний зазор — число остаётся только зазором.</summary>
        private void LiftAboveNav()
        {
            float nav = NavHeight?.Invoke() ?? 0f;
            _panel.style.bottom = PanelGap + nav;
        }

        /// <summary>Зазор между панелью и нижней лентой.</summary>
        private const float PanelGap = 24f;

        /// <summary>Доехали — ставим полки героев и лиц по НАСТОЯЩЕМУ месту
        /// листа: во время переезда оно было промежуточным.</summary>
        public override void Settled()
        {
            _sheet?.PlaceEmotions();
            LiftAboveNav();
        }

        protected override void OnClosed()
        {
            _live = false;
            _sheet?.Hide(); // снимает примерку и отпускает текущий ShowAsync
            SetPeek(false);
        }

        /// <summary>Жёсткое снятие (старт главы, ShowOnly): базовый Hide не
        /// зовёт OnClosed — цикл листа глушим сами, иначе застрявший ShowAsync
        /// не даст вкладке открыться в следующий раз.</summary>
        public override void Hide()
        {
            base.Hide();
            _live = false;
            _sheet?.Hide();
            SetPeek(false);
        }

        // Лист живёт циклом, пока вкладка на экране: «Выбрать»/«Отменить»
        // завершают один показ (примерка снята/закоммичена) — и лист тут же
        // открывается заново. Это его штатный жизненный цикл из игры,
        // страница просто крутит его без закрытия.
        private async Task RunSheetLoopAsync()
        {
            if (_live) return;
            _live = true;
            try
            {
                while (_live && style.display == DisplayStyle.Flex)
                {
                    _sheet.SetRoster(Roster());
                    await _sheet.ShowAsync(Entity);
                    if (!_live) break;
                    await Task.Yield();
                }
            }
            finally { _live = false; }
        }
    }
}
