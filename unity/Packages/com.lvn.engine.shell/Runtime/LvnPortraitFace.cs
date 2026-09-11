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
            if (circle == null || manifest == null || assets == null) return false;
            if (!force && LvnAvatars.Picked != LvnAvatars.SelfId) return false;
            var face = LvnHeroPortrait.Face(manifest, assets,
                                            EmotionOf?.Invoke(LvnHeroPortrait.HeroOf(manifest)));
            if (face == null) return false;
            // Прежнее лицо снимаем ПОИМЁННО: у кружка бывает своя начинка
            // (глиф-заглушка, рамка), и чистка целиком унесла бы её вместе с
            // портретом.
            circle.Q<VisualElement>(FaceName)?.RemoveFromHierarchy();
            face.name = FaceName;
            circle.Add(face);
            return true;
        }
    }
}
