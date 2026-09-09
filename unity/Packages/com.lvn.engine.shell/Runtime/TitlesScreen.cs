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
    /// первой. Комната отвечает за своё — за прокрутку и полосу.</para>
    /// </summary>
    public sealed class TitlesScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ScrollView _list;
        private LvnManifest _manifest;

        /// <summary>Чем рисовать карточку новеллы. Ставит оболочка, источник —
        /// хаб: облик и поведение у списка и главной общие.</summary>
        public Func<LvnTitle, VisualElement> Card;

        /// <summary>Какие новеллы показывать. Пусто — все из манифеста.</summary>
        public Func<IReadOnlyList<LvnTitle>> Titles;

        public TitlesScreen()
        {
            style.backgroundColor = Color.clear;
            pickingMode = PickingMode.Ignore;
            LvnChrome.PhoneColumn(this);

            _list = LvnScroll.Vertical();
            _list.style.flexGrow = 1;
            _list.contentContainer.style.alignItems = Align.Center;
            Add(_list);
        }

        public void SetContent(LvnManifest manifest)
        {
            _manifest = manifest;
            LvnStageSkin.Apply(manifest?.ui?.browse?.skin_metrics);
            Rebuild();
        }

        protected override void OnOpening() => Rebuild();

        public override void Rebuild()
        {
            if (_list == null) return;
            _list.Clear();
            var top = LvnEdges.Top(this);
            _list.style.paddingTop = top + LvnStageKit.D(70f);   // под шапку с логотипом
            _list.style.paddingBottom = LvnStageKit.D(LvnStageSkin.Home.Bottom);
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
