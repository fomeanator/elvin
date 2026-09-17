using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КОМНАТА «НОВЕЛЛЫ» — отдельный экран витрины, а не вид внутри хаба.
    ///
    /// <para>Список открывался подменой вида: витрина исчезала, полотно не
    /// ехало, а нажатие «Главная» ничего не закрывало — снаружи это читалось
    /// как всплывший сбоку попап (Илья 09.09). Комната обязана быть отдельной
    /// страницей: тогда переезд между вкладками — обычный переезд оболочки,
    /// полотно едет к своей точке, а возврат работает сам собой.</para>
    ///
    /// <para>Карточки собирает ХАБ и отдаёт сюда готовыми: там живут прогресс,
    /// замки и открытие детали, и вторая копия этой логики разошлась бы с
    /// первой. Комната отвечает за своё — за шапку, прокрутку и полосу.</para>
    ///
    /// <para>Шапка — по макету «Текущие экспедиции» (Figma 17.09): стрелка
    /// «назад» артом облика, заголовок и подзаголовок прописными, затемнения
    /// под шапкой и над лентой. Стрелка возвращает на главную.</para>
    /// </summary>
    public sealed class TitlesScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ILvnAssets _assets;
        private readonly VisualElement _top;
        private readonly ScrollView _list;
        private LvnManifest _manifest;
        private string _skin;

        /// <summary>Чем рисовать карточку новеллы. Ставит оболочка, источник —
        /// хаб: облик и поведение у списка и главной общие.</summary>
        public Func<LvnTitle, VisualElement> Card;

        /// <summary>Какие новеллы показывать. Пусто — все из манифеста.</summary>
        public Func<IReadOnlyList<LvnTitle>> Titles;

        /// <summary>Стрелка «назад» в шапке; ставит оболочка (домой).</summary>
        public Action Back;

        public TitlesScreen(ILvnAssets assets = null)
        {
            _assets = assets;
            style.backgroundColor = Color.clear;
            pickingMode = PickingMode.Ignore;

            // ПОЛОСУ ЦЕНТРИРУЕМ ВНУТРИ, А НЕ КОРНЕМ. Переезд между комнатами
            // анимирует translate САМОГО экрана; если центрировать корень тем
            // же свойством, переезд затирает сдвиг и комната застывает съехавшей
            // на пол-экрана вправо (Илья 09.09). Корень остаётся растянутым —
            // им распоряжается оболочка, полосой — содержимое.
            _top = new VisualElement { name = "titles-top" };
            _top.style.flexShrink = 0;
            _top.style.width = Length.Percent(100f);
            _top.style.maxWidth = LvnPanel.ReferenceWidth;
            _top.style.alignSelf = Align.Center;
            Add(_top);

            _list = LvnScroll.Vertical();
            _list.style.flexGrow = 1;
            _list.contentContainer.style.alignItems = Align.Center;
            _list.style.width = Length.Percent(100f);
            _list.style.maxWidth = LvnPanel.ReferenceWidth;
            _list.style.alignSelf = Align.Center;
            Add(_list);
        }

        public void SetContent(LvnManifest manifest)
        {
            _manifest = manifest;
            LvnStageKit.TakeSkin(manifest, ref _skin, () => { });
            Rebuild();
        }

        protected override void OnOpening() => Rebuild();

        public override void Rebuild()
        {
            if (_list == null) return;
            // Затемнения — один раз, под всем: сверху под шапку, снизу под ленту.
            if (!string.IsNullOrEmpty(_skin) && this.Q(name: "stage-scrim-top") == null) LvnStageKit.Scrims(this);

            _top.Clear();
            // Шапка стоит под шапкой оболочки (аватар, валюты) с воздухом макета.
            _top.style.paddingTop = LvnEdges.Top(this) + LvnStageKit.D(LvnStageSkin.Sheet.Top + 12f);
            LvnAir.PadX(_top, LvnStageKit.D(LvnStageSkin.Sheet.Side));
            _top.Add(LvnStageKit.Header(_skin, _assets,
                () => LvnWords.Of("hub.titles_head", "Current expeditions"),
                () => LvnWords.Of("hub.titles_hint", "Choose an era"),
                () => Back?.Invoke()));

            _list.Clear();
            // Список — с воздухом макета под шапкой, низ над лентой.
            _list.style.paddingTop = LvnStageKit.D(24f);
            _list.style.paddingBottom = LvnStageKit.BottomAboveBar(this, LvnStageSkin.Sheet.Bottom);
            foreach (var t in Source())
            {
                var card = Card?.Invoke(t);
                if (card != null) _list.Add(card);
            }
        }

        /// <summary>Список новелл: что дала оболочка, иначе весь манифест по
        /// порядку — комната не решает, что показывать, она показывает.</summary>
        private IEnumerable<LvnTitle> Source()
        {
            var given = Titles?.Invoke();
            if (given != null) { foreach (var t in given) if (t != null) yield return t; yield break; }
            var all = _manifest?.titles;
            if (all == null) yield break;
            foreach (var t in all) if (t != null) yield return t;
        }
    }
}
