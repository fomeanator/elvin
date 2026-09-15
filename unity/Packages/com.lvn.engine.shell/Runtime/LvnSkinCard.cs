using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ПЛИТКА СКИНА — одна на гардероб, пул круток и ленту (Илья 15.09: «одну
    /// карточку, которая работает в разных размерах, имеет лоадер с крутилкой,
    /// выдаёт экшены при нажатии, а при долгом — подробности»).
    ///
    /// <para>Платиновый задник со скруглением, арт с кадрированием по разделу,
    /// крутилка, пока арт едет (мини-вариант, иначе полный), вешалка, если не
    /// приехал; подложка имени и имя — в цвет ступени, полоса понизу; ценник
    /// или подарок; малозаметная пометка в углу (шанс). Размер любой: фонты и
    /// значки масштабируются от высоты (<see cref="SetSize"/>, <see cref="Fill"/>).</para>
    ///
    /// <para>Нажатия: короткое — <see cref="Tapped"/> хозяина, долгое —
    /// <see cref="Held"/>; без подписчиков подробности (<see cref="LvnSkinDetail"/>)
    /// открываются и по долгому, и по короткому.</para>
    /// </summary>
    public sealed class LvnSkinCard : VisualElement
    {
        /// <summary>Что показывать. Хозяин заполняет и зовёт <see cref="Bind"/>;
        /// повторный Bind с тем же артом картинку не перегружает.</summary>
        public sealed class Info
        {
            public string Title;
            public string Art;                 // адрес арта; мини-вариант ищется сам
            public string CurrencyIcon;        // вместо арта — значок валюты (плитка выигрыша)
            public bool SharpArt;              // сильный зум — сразу чёткий, мини даёт кашу
            public float Frame = 1f, FrameY = 0.5f;   // кадр по разделу: увеличение и якорь по высоте
            public bool Cover;                 // фон — заливкой окна, а не вписыванием
            public bool None;                  // пункт «снять»
            public Color? Rarity;              // цвет ступени; null — без ступени
            public string RarityWord;
            public long Price; public string Currency;
            public bool Gift;                  // приз круток: подарок вместо цены
            public bool Owned;                 // есть у игрока: ценника нет…
            public bool PriceAlways;           // …кроме мест, где цена — справка (пул круток: «есть» не прячет цену)
            public string Corner;              // малозаметная пометка в углу (шанс)
            public string Obtain;              // как получить: покупка, крутки, бесплатно
            public string Description;         // описание — в подробностях (Илья 15.09)
            public float? Radius; public Color? TextColor;
        }

        public const float BaseWidth = 150f, BaseHeight = 208f;
        // Платина #D1D1D6 (Илья 26.08) вместо прежней тускло-серой заливки:
        // арт скинов тёмный, и светлый задник держит его силуэт.
        public static readonly Color Platinum = UiColor.Named("#D1D1D6", new Color(0.82f, 0.82f, 0.84f));
        public static readonly Color PlateDark = new Color(0.16f, 0.16f, 0.19f, 0.85f);
        // Тёмный глиф вешалки: задник светлый, светлый значок на нём растворялся бы.
        private static readonly Color Glyph = new Color(0.18f, 0.18f, 0.22f);
        private const int HoldMs = 450;

        /// <summary>Короткое нажатие — действие хозяина (примерить, выбрать).</summary>
        public event Action Tapped;
        /// <summary>Долгое нажатие; без подписчика — подробности.</summary>
        public event Action Held;

        public Info Current { get; private set; }
        public VisualElement Art => _art;

        private readonly VisualElement _art, _hanger, _plate, _bar;
        private readonly LvnSpinner _spinner;
        private readonly Label _name;
        private Label _corner;
        private VisualElement _price;
        private ILvnAssets _assets;
        private float _scale = 1f;
        private string _artUrl;
        private int _artEpoch;
        private IVisualElementScheduledItem _holdTimer;
        private Vector2 _downAt;
        private bool _held, _fill;

        public LvnSkinCard()
        {
            AddToClassList("lvn-skin-card");
            style.width = BaseWidth; style.height = BaseHeight;
            style.flexShrink = 0;
            style.backgroundColor = Platinum;
            LvnChrome.Round(this, LvnTokens.RadiusSm);
            style.overflow = Overflow.Hidden; // арт и подложка не выходят за скругление

            _art = new VisualElement { name = "card-art", pickingMode = PickingMode.Ignore };
            _art.style.position = Position.Absolute;
            LvnPicture.Fit(_art, cover: false);
            Add(_art);

            // Вешалка — когда арта нет или он не приехал.
            _hanger = LvnIcons.Make(LvnIcon.Wardrobe, 42f, Glyph);
            _hanger.pickingMode = PickingMode.Ignore;
            _hanger.name = "card-ph";
            Centre(_hanger, 38f);
            _hanger.style.opacity = 0.55f;
            _hanger.style.display = DisplayStyle.None;
            Add(_hanger);

            // Крутилка — пока арт едет: дуга кольца, вращается. В дереве живёт
            // только на время загрузки: снятая с панели она не тикает, а плиток
            // на ленте — сотни.
            _spinner = new LvnSpinner { name = "card-spinner", Tint = LvnTokens.Gold };
            Centre(_spinner, 42f);

            _plate = new VisualElement { name = "card-plate", pickingMode = PickingMode.Ignore };
            LvnChrome.BottomStrip(_plate);
            _plate.style.backgroundColor = PlateDark;
            Add(_plate);

            // Полоса редкости понизу — как у карточек Доты.
            _bar = new VisualElement { name = "card-rarity", pickingMode = PickingMode.Ignore };
            LvnChrome.BottomStrip(_bar);
            _bar.style.display = DisplayStyle.None;
            Add(_bar);

            _name = new Label { name = "card-name", pickingMode = PickingMode.Ignore };
            _name.style.unityTextAlign = TextAnchor.MiddleCenter;
            _name.style.overflow = Overflow.Hidden;
            _name.style.textOverflow = TextOverflow.Ellipsis;
            _name.style.whiteSpace = WhiteSpace.NoWrap;
            _plate.Add(_name);

            ApplyScale();
            WirePresses();
            RegisterCallback<DetachFromPanelEvent>(_ => _holdTimer?.Pause());
        }

        private static void Centre(VisualElement el, float topPercent)
        {
            el.style.position = Position.Absolute;
            el.style.left = Length.Percent(50f);
            el.style.top = Length.Percent(topPercent);
            el.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
        }

        /// <summary>Свой размер: фонты и значки идут за высотой.</summary>
        public LvnSkinCard SetSize(float width, float height)
        {
            _fill = false;
            style.width = width; style.height = height;
            _scale = height / BaseHeight;
            ApplyScale();
            return this;
        }

        /// <summary>Заполнить хозяина целиком (клетка ленты): размер узнаётся по
        /// геометрии, фонты — за ним.</summary>
        public LvnSkinCard Fill()
        {
            _fill = true;
            style.width = Length.Percent(100f); style.height = Length.Percent(100f);
            RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float h = resolvedStyle.height;
                if (float.IsNaN(h) || h <= 1f) return;
                float k = h / BaseHeight;
                if (Mathf.Abs(k - _scale) < 0.01f) return;
                _scale = k;
                ApplyScale();
            });
            return this;
        }

        private float Text => Mathf.Clamp(_scale, 0.7f, 1.4f);

        private void ApplyScale()
        {
            float k = _scale, t = Text;
            _name.style.fontSize = LvnTokens.TextSm * t;
            LvnAir.Pad(_plate, LvnTokens.Space1 * k);
            _plate.style.paddingBottom = LvnTokens.Space1 * k + (_bar.style.display == DisplayStyle.Flex ? 4f * k : 0f);
            _bar.style.height = 4f * k;
            _hanger.style.width = 42f * k; _hanger.style.height = 42f * k;
            float ring = 26f * k;
            _spinner.style.width = ring; _spinner.style.height = ring;
            if (_corner != null) _corner.style.fontSize = LvnTokens.TextMicro * t;
        }

        /// <summary>Показать <paramref name="info"/>. Арт перегружается, только
        /// если сменился адрес: сверка ленты трогает плитку на каждый чих.</summary>
        public void Bind(Info info, ILvnAssets assets)
        {
            Current = info; _assets = assets;
            if (info.Radius.HasValue) LvnChrome.Round(this, info.Radius.Value);
            var text = info.TextColor ?? LvnTokens.Text;

            // Кадр по разделу: элемент больше плитки, плитка клипует излишек.
            float frame = Mathf.Max(0.01f, info.Frame);
            _art.style.width = Length.Percent(frame * 100f);
            _art.style.height = Length.Percent(frame * 100f);
            _art.style.left = Length.Percent(50f - frame * 100f * 0.50f);
            _art.style.top = Length.Percent(50f - frame * 100f * info.FrameY);
            LvnPicture.Fit(_art, cover: info.Cover);

            _name.text = info.Title ?? "";
            DressRarity(info.Rarity, text);

            _price?.RemoveFromHierarchy(); _price = null;
            if (!info.Owned && info.Gift) Add(_price = GiftBadge(_scale));
            else if ((!info.Owned || info.PriceAlways) && info.Price > 0) Add(_price = PriceBadge(info.Currency, info.Price, _scale));

            _corner?.RemoveFromHierarchy(); _corner = null;
            if (!string.IsNullOrEmpty(info.Corner))
            {
                _corner = new Label(info.Corner) { name = "card-corner", pickingMode = PickingMode.Ignore };
                _corner.style.position = Position.Absolute;
                _corner.style.top = 4f * _scale; _corner.style.left = 6f * _scale;
                _corner.style.color = UiColor.WithAlpha(Color.white, 0.72f);
                _corner.style.fontSize = LvnTokens.TextMicro * Text;
                _corner.style.backgroundColor = LvnTokens.Veil(0.35f);
                LvnAir.Pad(_corner, LvnTokens.Hair * _scale);
                LvnChrome.Round(_corner, LvnTokens.RadiusSm * 0.6f);
                Add(_corner);
            }

            if (!string.IsNullOrEmpty(info.CurrencyIcon))
            {
                // ВАЛЮТА — ТОЙ ЖЕ ПЛИТКОЙ (Илья 15.09: «кристаллы показывать ровно
                // так же, как скины»): значок валюты вместо арта, сумма — именем.
                _art.style.backgroundImage = StyleKeyword.None;
                _hanger.Clear();
                _hanger.Add(LvnPriceTag.Icon(info.CurrencyIcon, 64f * _scale));
                _hanger.style.opacity = 1f;
                _hanger.style.display = DisplayStyle.Flex;
                Spinning(false);
                _artUrl = null;
                return;
            }
            _hanger.style.opacity = 0.55f;
            if (info.None) { ShowGlyph(LvnIcon.Close); _artUrl = null; return; }
            if (string.IsNullOrEmpty(info.Art)) { ShowGlyph(LvnIcon.Wardrobe); _artUrl = null; return; }
            if (info.Art == _artUrl) return;
            _artUrl = info.Art;
            LvnAsync.Fire(LoadArtAsync(info.Art, info.SharpArt, ++_artEpoch), "SkinCardArt");
        }

        private void ShowGlyph(LvnIcon icon)
        {
            _art.style.backgroundImage = StyleKeyword.None;
            _hanger.Clear();
            _hanger.Add(LvnIcons.Make(icon, 42f * _scale, Glyph));
            _hanger.style.display = DisplayStyle.Flex;
            Spinning(false);
        }

        private void Spinning(bool on)
        {
            if (on) { if (_spinner.parent == null) Add(_spinner); _spinner.BringToFront(); }
            else _spinner.RemoveFromHierarchy();
        }

        /// <summary>Арт — МИНИ-ВЕРСИЯ (Илья 27.08: «не тянуть огромные, если юзер
        /// даже не тыкнет»), полный — только если мини нет или кадр резкий.
        /// Адрес, за которым ходили, обязан быть тем же, что запрошен сейчас:
        /// два быстрых тапа по свотчу слали две загрузки, побеждала не
        /// последняя, а та, что доехала позже.</summary>
        private async Task LoadArtAsync(string url, bool sharp, int epoch)
        {
            _hanger.style.display = DisplayStyle.None;
            _art.style.backgroundImage = StyleKeyword.None;
            Spinning(true);
            var sprite = await LoadSpriteAsync(_assets, url, sharp);
            if (epoch != _artEpoch || url != _artUrl) return;
            Spinning(false);
            if (sprite == null) { ShowGlyph(LvnIcon.Wardrobe); return; }
            LvnPicture.Paint(_art, sprite, slice: 0);
            LvnPicture.Pin(_art, sprite, _assets); // видимый арт LRU не трогает
        }

        /// <summary>Спрайт по адресу: мини-вариант, иначе полный. Полный файловый
        /// след тракта — «одни вешалки» разбираются по логу, а не догадками.</summary>
        public static async Task<Sprite> LoadSpriteAsync(ILvnAssets assets, string url, bool sharp)
        {
            if (assets == null || string.IsNullOrEmpty(url)) return null;
            Sprite s = null;
            var mini = sharp ? null : Lvn.Content.DownloadPolicy.MiniVariant(url);
            try { if (!string.IsNullOrEmpty(mini)) s = await assets.LoadSpriteAsync(mini, CancellationToken.None); }
            catch (Exception ex) { LvnLog.Warn($"[lvn-card-art] mini {mini}: {ex.Message}"); }
            if (s == null)
            {
                try { s = await assets.LoadSpriteAsync(url, CancellationToken.None); }
                catch (Exception ex) { LvnLog.Warn($"[lvn-card-art] full {url}: {ex.Message}"); }
            }
            if (s == null) LvnLog.Warn($"[lvn-card-art] ПУСТО: mini={mini ?? "-"} и full={url} не дали спрайта");
            return s;
        }

        /// <summary>ОБЛИК РЕДКОСТИ «КАК В ДОТЕ» (TR-109): подложка имени темнеет в
        /// цвет ступени, понизу — яркая полоса, по краю — тонкий ободок. Задник
        /// под фото НЕ красится, и ИМЯ — БЕЛЫМ (Илья 15.09: «текст в карточке
        /// белым, перекрашивать только подложку имени и рамку»).</summary>
        private void DressRarity(Color? rarity, Color text)
        {
            if (!rarity.HasValue)
            {
                LvnChrome.Border(this, LvnTokens.Border, LvnStyler.QuietEdge);
                _plate.style.backgroundColor = PlateDark;
                _name.style.color = text;
                _bar.style.display = DisplayStyle.None;
                ApplyScale();
                return;
            }
            var c = rarity.Value;
            LvnChrome.Border(this, c, 2f);
            var tinted = Color.Lerp(PlateDark, c, 0.35f); tinted.a = 0.9f;
            _plate.style.backgroundColor = tinted;
            _name.style.color = text;
            _bar.style.backgroundColor = c;
            _bar.style.display = DisplayStyle.Flex;
            ApplyScale();
        }

        /// <summary>Отметить выбранное: акцент и грань потолще; снятое — обратно
        /// ободок ступени (или тихая грань темы).</summary>
        public void SetChosen(bool on, Color ink)
        {
            if (on) LvnChrome.Border(this, ink, LvnStyler.ChosenEdge);
            else if (Current?.Rarity != null) LvnChrome.Border(this, Current.Rarity.Value, 2f);
            else LvnChrome.Border(this, LvnTokens.Border, LvnStyler.QuietEdge);
        }

        private void WirePresses()
        {
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                _downAt = e.position; _held = false;
                _holdTimer?.Pause();
                _holdTimer = schedule.Execute(() => { _held = true; if (Held != null) Held(); else ShowDetail(); }).StartingIn(HoldMs);
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                if (((Vector2)e.position - _downAt).sqrMagnitude > 144f) _holdTimer?.Pause();
            });
            RegisterCallback<PointerUpEvent>(_ => _holdTimer?.Pause());
            RegisterCallback<PointerLeaveEvent>(_ => _holdTimer?.Pause());
            RegisterCallback<ClickEvent>(e =>
            {
                if (_held) { e.StopPropagation(); return; }
                if (Tapped != null) Tapped(); else ShowDetail();
            });
            LvnMotion.Tappable(this);
        }

        /// <summary>Подробности этой плитки крупно.</summary>
        public void ShowDetail()
        {
            if (Current != null) LvnSkinDetail.Show(this, Current, _assets);
        }

        /// <summary>Подарок вместо ценника: приз круток (TR-93) не покупают,
        /// а выигрывают — ценник «0» врал бы «бесплатно».</summary>
        public static VisualElement GiftBadge(float scale = 1f)
        {
            var gift = Chip("card-price", scale);
            gift.Add(LvnIcons.Make(LvnIcon.Gift, 20f * scale, LvnTokens.Gold));
            return gift;
        }

        /// <summary>Ценник значком, а не словом: со словом ярлык шире плитки
        /// (Илья 28.08). Цвет — у ценника по валюте предмета.</summary>
        public static VisualElement PriceBadge(string currency, long price, float scale = 1f)
        {
            var badge = LvnPriceTag.Tag(currency, price, new LvnPriceTag.Row { FontSize = 19f * Mathf.Clamp(scale, 0.7f, 1.4f), Gap = 3f });
            badge.name = "card-price";
            badge.style.position = Position.Absolute;
            badge.style.top = 6f * scale; badge.style.right = 6f * scale;
            badge.style.backgroundColor = LvnTokens.Veil(0.62f);
            LvnAir.Pad(badge, LvnTokens.Space1 * scale, LvnTokens.Hair * scale);
            LvnChrome.Round(badge, LvnTokens.RadiusSm);
            return badge;
        }

        private static VisualElement Chip(string name, float scale)
        {
            var chip = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            chip.style.position = Position.Absolute;
            chip.style.top = 6f * scale; chip.style.right = 6f * scale;
            chip.style.backgroundColor = LvnTokens.Veil(0.62f);
            LvnAir.Pad(chip, LvnTokens.Space1 * scale, LvnTokens.Hair * scale);
            LvnChrome.Round(chip, LvnTokens.RadiusSm);
            return chip;
        }
    }
}
