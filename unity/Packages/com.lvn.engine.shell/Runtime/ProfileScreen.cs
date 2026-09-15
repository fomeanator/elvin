using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Профиль показывает данные хоста и объясняет пустые разделы. Имя
    /// читает у LvnPlayerName, аватар — у набора новеллы или живого портрета.
    /// Minimal оставляет только имя и копируемый ID. Значения прогресса и
    /// награды не выдумываются; хост обновляет поля и зовёт Rebuild.
    /// </summary>
    public sealed partial class ProfileScreen : LvnOverlayScreen
    {

        /// <summary>One earned/locked achievement badge.</summary>
        public struct Achievement
        {
            public LvnIcon Icon;
            public string Title;
            public bool Unlocked;
            public Achievement(LvnIcon icon, string title, bool unlocked)
            { Icon = icon; Title = title; Unlocked = unlocked; }
        }

        /// <summary>One character relationship row (0..1 affection).</summary>
        public struct Relation
        {
            public string Id; // title + stat key; optional for standalone hosts
            public string Name;
            public float Affection; // 0..1
            public Relation(string name, float affection) : this(name, affection, null) { }
            public Relation(string name, float affection, string id)
            { Id = id; Name = name; Affection = Mathf.Clamp01(affection); }
        }

        /// <summary>One stat tile: a big number over a caption.</summary>
        public struct Stat
        {
            public string Value;
            public string Caption;
            public Stat(string value, string caption)
            { Value = value; Caption = caption; }
        }

        // ── Данные хоста ────────────────────────────────────────────────
        // Копия имени не хранится — см. BrowseHub: одна правда у роли
        // LvnPlayerName, экран её только показывает.

        /// <summary>TR-25: минимальный профиль — только имя и ID (уровень, XP,
        /// статы, достижения и отношения спрятаны). ui.browse.profile_full=false.</summary>
        public bool Minimal;
        public LvnIcon AvatarIcon = LvnIcon.Profile;
        public string AvatarUrl;               // optional art; falls back to the glyph

        /// <summary>Игрок нажал аватар — открыть выбор лица. Пусто — кружок
        /// остаётся картинкой (набора аватарок у новеллы нет).</summary>
        public Action OnPickAvatar;
        // ЧЕГО ДВИЖОК НЕ СЧИТАЕТ, ТОГО ОН И НЕ ПОКАЗЫВАЕТ. Здесь стояли
        // «уровень 7», «1240 из 2000 XP» и чужой идентификатор — демо-значения,
        // которые никто никогда не задавал. Системы уровней в движке нет вовсе,
        // и игрок видел свой «седьмой уровень» с первого запуска: цифра из
        // воздуха читается как настоящая, потому что стоит там, где обычно
        // настоящая.
        //
        // Ноль значит «неизвестно» — блок уровня и полоса опыта не рисуются,
        // пока хост не выставит их сам (он же и ведёт счёт, если ведёт).
        public int Level;
        public int Xp;
        public int XpNext;
        public string Uid;

        // Пустые списки объясняются интерфейсом, не заполняются демо-данными.
        // Замки пустой сетки — оформление, а не выдуманные достижения.
        public List<Stat> Stats = new List<Stat>();
        public List<Achievement> Achievements = new List<Achievement>();
        public List<Relation> Relations = new List<Relation>();

        /// <summary>Реально пройдено глав по всем историям — хост считает по
        /// прогрессу перед открытием. 0 показывает пояснение.</summary>
        public int ChaptersDone;

        /// <summary>Открыть экран настроек — профиль даёт на них ссылку
        /// («звук, язык, загрузка»), это ближайшее место, где их ищут.</summary>
        public System.Action OnOpenSettings;

        /// <summary>Открыть галерею катсцен. Пусто — пункта в профиле нет.</summary>
        public System.Action OnOpenCutscenes;

        /// <summary>Сколько катсцен игрок уже открыл — подпись пункта. Ставит
        /// хост: сколько их всего, знает игра, а не экран профиля.</summary>
        public int CutsceneCount;

        /// <summary>НЕ ПОКАЗЫВАЕТСЯ. Балансы живут в шапке; поле оставлено,
        /// чтобы не ломать хосты, которые его заполняют.</summary>
        public List<Stat> Wallet = new List<Stat>();

        /// <summary>«Удалить аккаунт» (стор-требование): хост стирает аккаунт
        /// на сервере и локально. true = удалено, экран закрывается; false =
        /// не вышло (нет сети), кнопка объясняет. null прячет строку.</summary>
        public Func<Task<bool>> OnDeleteAccount;

        /// <summary>«Выйти из аккаунта»: хост забывает пропуск игрока и
        /// возвращается к учётке устройства. Ничего не удаляет. null прячет
        /// строку — играм без входа она не нужна.</summary>
        public Func<Task<bool>> OnSignOut;

        private readonly ILvnAssets _assets;
        private readonly ScrollView _body;


        public ProfileScreen(ILvnAssets assets)
        {
            _assets = assets;

            // ВКЛАДКА как главная (Илья 26.08): без листа и скрима, контент на
            // общей атмосфере, дырка под нижнее меню, root не ловит тапы.
            var sheet = _sheet = new VisualElement { name = "profile-sheet" };
            ScreenUi.HubTabSheet(this, sheet);
            Add(sheet);

            // ── Top bar: back (‹) + "Профиль" ─────────────────────────────
            var top = ScreenUi.Row();
            top.style.marginBottom = LvnTokens.Space2;
            sheet.Add(top);

            var titleBlock = new VisualElement();
            titleBlock.Add(ScreenUi.Eyebrow(() => LvnWords.Of("profile.eyebrow", "PROFILE")));
            var title = _title = SectionTitle(() => LvnWords.Of("profile.title", "Profile"));
            titleBlock.Add(title);
            top.Add(titleBlock);

            // ── Scrollable body ───────────────────────────────────────────
            _body = Lvn.UI.LvnScroll.Vertical();
            _body.name = "profile-body";
            _body.style.flexGrow = 1;
            _body.style.minHeight = 0;
            sheet.Add(_body);

            Rebuild();
        }

        // Тело собирается на КАЖДОМ открытии: поля (Minimal, Relations)
        // хост ставит после конструктора — снимок из конструктора показывал
        // бы вечную заглушку (класс бага «пустых настроек», зеркальный).
        protected override void OnOpening() => Rebuild();

        /// <summary>Слова, шрифт или размеры сменились — перечитать их.</summary>

        public override void Rebuild()
        {
            _body.Clear();
            _body.Add(BuildIdentityCard());
            if (Minimal)
            {
                _body.Add(BuildFooter());
                return;
            }

            _body.Add(StageHeader(ScreenUi.SectionHeader(LvnWords.Of("profile.stats", "Story stats"))));
            _body.Add(Stats.Count > 0 ? BuildStatRow() : HintCard(
                LvnWords.Of("profile.stats_empty", "Your story stats will appear here."), "stats"));

            _body.Add(StageHeader(ScreenUi.SectionHeader(LvnWords.Of("profile.progress", "Reading"))));
            _body.Add(ChaptersDone > 0 ? ProgressLine() : HintCard(
                LvnWords.Of("profile.progress_empty", "No chapters completed yet."), "progress"));

            // КОШЕЛЬКА ЗДЕСЬ НЕТ. Балансы живут в шапке — она видна всегда и
            // обновляется сама; вторая копия в профиле показывала те же числа
            // с задержкой на открытие экрана и расходилась с шапкой ровно в тот
            // момент, когда игрок сверял их глазами (решение Ильи, 28.08).

            _body.Add(StageHeader(ScreenUi.SectionHeader(LvnWords.Of("profile.achievements", "Achievements"))));
            if (Achievements.Count == 0) _body.Add(HintCard(
                LvnWords.Of("profile.achievements_empty", "No achievements to display yet."), "achievements"));
            _body.Add(BuildAchievements());

            // Полный профиль объясняет отсутствие встреч; Minimal выше
            // завершился до всех разделов и служебных ссылок.
            _body.Add(StageHeader(ScreenUi.SectionHeader(LvnWords.Of("profile.relations", "Relationships"))));
            if (Relations.Count > 0) _body.Add(BuildRelations());
            else _body.Add(HintCard(
                LvnWords.Of("profile.relations_empty", "The first choice already bends the story. Start a chapter and your ties appear here."), "relations"));

            if (OnGiveShare != null) _body.Add(GiveShareLink());
            if (OnTakeShare != null) _body.Add(TakeShareLink());
            if (OnOpenCutscenes != null) _body.Add(CutscenesLink());
            if (OnOpenSettings != null) _body.Add(SettingsLink());
            if (OnSignOut != null) _body.Add(SignOutRow());
            if (OnDeleteAccount != null) _body.Add(DeleteAccountRow());

            _body.Add(BuildFooter());
        }

        // Честная строка прогресса: единственная цифра, которую профиль
        // может показать без выдумок на любом аккаунте.
        private VisualElement ProgressLine()
        {
            var row = StageCard(LvnStyler.CardRow(ScreenUi.Row()));
            LvnAir.PadX(row, LvnTokens.Space3);   // поля по горизонтали — у экрана
            row.style.marginBottom = LvnTokens.Space2;
            var ic = LvnIcons.Make(LvnIcon.Book, 22f, LvnTokens.Accent);
            ic.style.marginRight = LvnTokens.Space2;
            row.Add(ic);
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("profile.chapters_read", "Chapters read: {0}",
                                            $"{ChaptersDone} {ChapterWord(ChaptersDone)}"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = LvnTokens.TextSm;
            row.Add(lbl);
            return row;
        }

        // Мягкая карточка-пояснение вместо пустого места.
        private VisualElement HintCard(string text, string section)
        {
            var card = new VisualElement { name = "profile-" + section + "-empty" };
            LvnChrome.Card(card, LvnTokens.SurfaceSoft);
            StageCard(card);
            LvnAir.Pad(card, LvnTokens.Space3);
            card.style.marginBottom = LvnTokens.Space2;
            var lbl = new Label(text);
            ScreenUi.Quiet(lbl, LvnTokens.TextSm);
            card.Add(lbl);
            return card;
        }


        // ── Section 2: identity card ───────────────────────────────────────
        private VisualElement BuildIdentityCard()
        {
            var card = new VisualElement { name = "profile-identity" };
            card.style.flexDirection = FlexDirection.Column;
            LvnChrome.Card(card, LvnTokens.SurfaceHi, LvnTokens.Radius);
            StageCard(card);
            LvnAir.Pad(card, LvnTokens.Space3);
            card.style.marginBottom = LvnTokens.Space3;

            if (Minimal)
            {
                card.Add(PlayerName());
                return card;
            }

            var dossier = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("profile.dossier", "STORY RECORD"));
            dossier.style.color = LvnTokens.Gold;
            dossier.style.fontSize = LvnTokens.TextMicro;
            dossier.style.letterSpacing = 1.9f;
            dossier.style.unityFontStyleAndWeight = FontStyle.Bold;
            dossier.style.marginBottom = LvnTokens.Space2;
            card.Add(dossier);

            var identity = ScreenUi.Row();
            card.Add(identity);

            // Circular avatar with an Accent ring.
            float avatarSize = LvnTokens.TouchLg * 2f;
            var avatar = new VisualElement { name = "profile-avatar" };
            avatar.style.width = avatarSize;
            avatar.style.height = avatarSize;
            avatar.style.flexShrink = 0;
            avatar.style.marginRight = LvnTokens.Space3;
            avatar.style.alignItems = Align.Center;
            avatar.style.justifyContent = Justify.Center;
            avatar.style.backgroundColor = LvnTokens.SurfaceHi;
            LvnChrome.Frame(avatar, avatarSize / 2f, LvnTokens.Accent, 3f);

            // ЖИВОЙ ПОРТРЕТ СИЛЬНЕЕ КАРТИНКИ (TR-68): игрок выбрал «мой облик»
            // — в кружке лицо его героя, собранное в гардеробе, а не картинка
            // из набора.
            LvnPortraitFace.Show(avatar, AvatarUrl, _manifest, _assets);
            // ПО АВАТАРУ ОТКРЫВАЕТСЯ ВЫБОР ЛИЦА (TR-79). Кружок и раньше
            // выглядел кнопкой — по нему жали и ничего не происходило.
            if (OnPickAvatar != null)
            {
                avatar.AddManipulator(new Clickable(() => OnPickAvatar()));
                LvnMotion.Tappable(avatar);
            }
            identity.Add(avatar);

            // Name + level + XP.
            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;
            col.style.minWidth = 0;
            identity.Add(col);
            col.Add(PlayerName());
            // Уровня нет — секции нет: пустая полоса опыта врёт не меньше
            // выдуманной, а «Уровень 0» выглядит поломкой.
            if (Level <= 0 && XpNext <= 0) return card;

            var level = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("profile.level", "Level {0}", Level));
            level.style.color = LvnTokens.Accent;
            level.style.fontSize = LvnTokens.TextSm;
            LvnAir.MarginY(level, LvnTokens.Hair, LvnTokens.Space2);
            col.Add(level);

            // XP progress: a track with an Accent fill.
            int next = XpNext > 0 ? XpNext : 1;
            float frac = Mathf.Clamp01((float)Xp / next);

            col.Add(LvnStyler.Bar(16f, frac, LvnTokens.SurfaceHi));

            var xpLabel = new Label($"{LvnPriceTag.Amount(Xp)} / {LvnPriceTag.Amount(next)} XP");
            xpLabel.style.color = LvnTokens.TextDim;
            xpLabel.style.fontSize = LvnTokens.TextXs;
            xpLabel.style.marginTop = LvnTokens.Space1;
            col.Add(xpLabel);

            return card;
        }

        private static Label PlayerName()
        {
            var name = LvnRedress.Bind(new Label { name = "profile-player-name" }, () => LvnPlayerName.Display);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextLg;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.minWidth = 0;
            return name;
        }

        // ── Section 5: relationships ───────────────────────────────────────
        private VisualElement BuildRelations()
        {
            var list = new VisualElement();
            list.style.marginBottom = LvnTokens.Space1;
            var ordered = new List<Relation>(Relations);
            ordered.Sort((a, b) =>
            {
                int order = b.Affection.CompareTo(a.Affection);
                if (order == 0) order = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                if (order == 0) order = StringComparer.Ordinal.Compare(a.Id, b.Id);
                return order != 0 ? order : StringComparer.Ordinal.Compare(a.Name, b.Name);
            });
            foreach (var r in ordered) list.Add(RelationRow(r));
            return list;
        }

        private VisualElement RelationRow(Relation r)
        {
            var row = new VisualElement { name = "profile-relation", userData = r.Id };
            LvnChrome.Card(row);
            StageCard(row);
            LvnAir.Pad(row, LvnTokens.Space3, LvnTokens.Space2);
            row.style.marginBottom = LvnTokens.Space2;

            var head = ScreenUi.Row(spread: true);
            head.style.marginBottom = LvnTokens.Space1;
            row.Add(head);

            var nameRow = ScreenUi.Row();
            nameRow.style.flexGrow = 1;
            nameRow.style.flexShrink = 1;
            nameRow.style.flexBasis = 0;
            nameRow.style.minWidth = 0;
            nameRow.style.marginRight = LvnTokens.Space2;
            var heart = LvnIcons.Make(LvnIcon.Heart, 20f, LvnTokens.Accent);
            heart.style.marginRight = LvnTokens.Space1;
            nameRow.Add(heart);
            var name = new Label(r.Name) { name = "profile-relation-name" };
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextSm;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.flexGrow = 1;
            name.style.flexShrink = 1;
            name.style.flexBasis = 0;
            name.style.minWidth = 0;
            nameRow.Add(name);
            head.Add(nameRow);

            var pct = new Label($"{Mathf.RoundToInt(r.Affection * 100f)}%") { name = "profile-relation-percent" };
            pct.style.color = LvnTokens.Accent;
            pct.style.fontSize = LvnTokens.TextSm;
            pct.style.unityFontStyleAndWeight = FontStyle.Bold;
            pct.style.flexShrink = 0;
            head.Add(pct);

            row.Add(LvnStyler.Bar(14f, r.Affection, LvnTokens.SurfaceHi));

            return row;
        }



        // Склонение держит словарь: правило зависит от языка, а не от экрана.
        private static string ChapterWord(int count)
            => LvnWords.Plural("chapter", count, "chapter", "chapters");

        // Жизненный цикл накладного экрана — в базовом классе
        // (LvnOverlayScreen): проявление, ожидание, угасание и отмена открытия
        // из Hide() одинаковы у всех восьми экранов оболочки.

    }
}
