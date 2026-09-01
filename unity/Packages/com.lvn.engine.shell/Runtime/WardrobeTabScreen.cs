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
            _panel.style.position = Position.Absolute;
            _panel.style.left = 10; _panel.style.right = 10;
            _panel.style.bottom = 140;
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

        private void SetPeek(bool on)
        {
            _peeking = on;
            _panel.style.display = on ? DisplayStyle.None : DisplayStyle.Flex;
            pickingMode = on ? PickingMode.Position : PickingMode.Ignore;
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
            SetPeek(false);
            LvnAsync.Fire(RunSheetLoopAsync(), "WardrobeTabLoop");
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
