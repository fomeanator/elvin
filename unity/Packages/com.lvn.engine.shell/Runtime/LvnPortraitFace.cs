using Lvn.Content;
using Lvn.UI;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЛИЦО ГЕРОЯ НА КРУЖКЕ (TR-68) — одно место, где живой портрет надевается
    /// на элемент интерфейса.
    ///
    /// <para>Кружков три — в шапке витрины, в профиле и в наборе лиц, — и все
    /// задают один вопрос: «показывать ли сейчас портрет». Ответ обязан быть
    /// один: разъехавшись, они показали бы игроку два разных лица сразу.</para>
    ///
    /// <para>Сам портрет собирает движок (<see cref="LvnHeroPortrait"/>): здесь
    /// только решение ПОКАЗЫВАТЬ и уборка прежнего лица с кружка.</para>
    /// </summary>
    public static class LvnPortraitFace
    {
        private const string FaceName = "lvn-hero-face";
        private const string PictureName = "lvn-avatar-picture";

        /// <summary>Replace static/live art together. Each request owns its
        /// element, so a late download cannot paint over a newer selection.</summary>
        public static void Show(VisualElement circle, string url, LvnManifest manifest, ILvnAssets assets,
                                bool forceSelf = false, bool staticOnly = false)
        {
            if (circle == null) return;
            circle.Q<VisualElement>(PictureName)?.RemoveFromHierarchy();
            circle.style.backgroundImage = StyleKeyword.None;
            var backdrop = LvnTokens.SurfaceHi;
            backdrop.a = 1f;
            circle.style.backgroundColor = backdrop;
            circle.style.overflow = Overflow.Hidden;
            circle.Q<VisualElement>(FaceName)?.RemoveFromHierarchy();
            if (!staticOnly && Wear(circle, manifest, assets, forceSelf)) return;
            var picture = ScreenUi.Stretch(new VisualElement { name = PictureName, pickingMode = PickingMode.Ignore });
            circle.Add(picture);
            if (!string.IsNullOrEmpty(url) && assets != null) LvnPicture.Photo(picture, url, assets, cover: true);
            else
            {
                picture.style.alignItems = Align.Center;
                picture.style.justifyContent = Justify.Center;
                picture.Add(LvnIcons.Make(LvnIcon.Profile, LvnTokens.TextDisplay, LvnTokens.TextDim));
            }
        }

        /// <summary>Какое лицо у героя СЕЙЧАС — спрашиваем у того, кто ведёт
        /// сцену. Провод живёт ЗДЕСЬ, а не в движке: движок не знает, кто на
        /// сцене, а тянуть поле через границу сборок значит протянуть провод
        /// вместо переезда (страж швов).</summary>
        public static System.Func<string, string> EmotionOf;

        /// <summary>Надеть портрет на кружок. Вернёт false, если игрок выбрал
        /// картинку из набора или у новеллы нет героя гардероба — тогда зовущий
        /// рисует картинку сам, как рисовал раньше.</summary>
        public static bool Wear(VisualElement circle, LvnManifest manifest, ILvnAssets assets,
                                bool force = false)
        {
            if (circle == null) return false;
            circle.Q<VisualElement>(FaceName)?.RemoveFromHierarchy();
            if (manifest == null || assets == null) return false;
            if (!force && !LvnAvatars.ShowsSelf) return false;
            var face = LvnHeroPortrait.Face(manifest, assets,
                                            EmotionOf?.Invoke(LvnHeroPortrait.HeroOf(manifest)));
            if (face == null) return false;
            // Прежнее лицо снимаем ПОИМЁННО: у кружка бывает своя начинка
            // (глиф-заглушка, рамка), и чистка целиком унесла бы её вместе с
            // портретом.
            face.name = FaceName;
            circle.Add(face);
            return true;
        }
    }
}
