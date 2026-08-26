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
    public sealed class WardrobeTabScreen : LvnOverlayScreen
    {
        private readonly LvnManifest _manifest;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _panel;
        private WardrobeSheet _sheet;
        private bool _live;    // цикл показов листа жив, пока вкладка на экране
        private bool _peeking; // «Во весь рост»: плашка спрятана до касания

        /// <summary>Открыть быстрый магазин (модаль) — вешает NovelShell.</summary>
        public System.Func<Task> OpenStore;
        /// <summary>Подтверждение «не хватает — в магазин?» — вешает NovelShell.</summary>
        public System.Func<string, string, Task<bool>> ConfirmTopUp;
        /// <summary>Финальное «всё ещё не хватает» — вешает NovelShell.</summary>
        public System.Func<string, string, Task> Alert;

        // Текущий персонаж вкладки: фаворит меню, иначе героиня по умолчанию.
        private string Entity
        {
            get
            {
                var fav = LvnPrefs.MenuFavorite;
                if (!string.IsNullOrEmpty(fav) && _manifest?.sprites != null
                    && _manifest.sprites.ContainsKey(fav)) return fav;
                return _manifest?.ui?.wardrobe?.entity;
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
                        list.Add((id, d?.name ?? id));
            var entity = Entity;
            if (list.Count == 0 && !string.IsNullOrEmpty(entity))
                list.Add((entity, _manifest?.sprites != null
                    && _manifest.sprites.TryGetValue(entity, out var e) ? e?.name ?? entity : entity));
            return list;
        }

        public WardrobeTabScreen(LvnManifest manifest, ILvnAssets assets)
        {
            _manifest = manifest;
            _assets = assets;
            ScreenUi.Stretch(this);
            style.backgroundColor = Color.clear;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            pickingMode = PickingMode.Ignore;

            // Плашка листа — снизу, над нижним меню; героиня сцены видна над
            // ней. Вид — как игровая панель: Полночь, скруглённый верх,
            // акцентная кромка (то самое «дорого» единого стиля).
            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.left = 10; _panel.style.right = 10;
            _panel.style.bottom = 140;
            var bg = LvnTokens.PanelBg;
            _panel.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.94f);
            LvnChrome.Edge(_panel);
            LvnChrome.Round(_panel, 20f);
            _panel.style.borderTopWidth = 2.5f;
            _panel.style.borderTopColor = LvnTokens.Accent;
            _panel.style.paddingTop = 14; _panel.style.paddingBottom = 14;
            _panel.style.paddingLeft = 16; _panel.style.paddingRight = 16;
            Add(_panel);

            // «Во весь рост» прячет плашку — вернуть её обязано ЛЮБОЕ касание
            // (обещание шита): на время пика вкладка сама ловит тапы.
            RegisterCallback<ClickEvent>(_ =>
            {
                if (!_peeking) return;
                SetPeek(false);
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
            _sheet.SetManifest(_manifest);
            _sheet.HideBalances = true;    // валюты уже в навбаре
            _sheet.OnlySeen = false;       // ВИТРИНА: весь каталог скинов
            _sheet.MarkSeenOnShow = false; // …но коллекцию игры не раскрывает
            _sheet.OnPeek = on => SetPeek(on);
            // Смена персонажа в ростере = назначить фаворита меню: кукла
            // сцены меняется хостом (NovelApp слушает LvnPrefs.Changed).
            _sheet.OnCharacterPicked = (_, to) => LvnPrefs.MenuFavorite = to;
            _panel.Add(_sheet);
        }

        protected override void OnOpening()
        {
            EnsureSheet();
            // Хуки хоста могли повеситься после конструктора — пробрасываем
            // на каждый показ.
            _sheet.OpenStore = OpenStore;
            _sheet.ConfirmTopUp = ConfirmTopUp;
            _sheet.Alert = Alert;
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
