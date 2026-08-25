using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЕДИНЫЙ индикатор загрузки контента, двух видов (решение Ильи 25.08):
    /// МИНИ — хромовский кружок: стрелка с полочкой и кольцо прогресса вокруг
    /// (painter2D, без единого ассета); тап ПЕРЕТЕКАЕТ капсулу в ПОЛНУЮ
    /// карточку — имя файла, текущая скорость, очередь и скачанный объём.
    /// Морф — одна анимация ширины/высоты/радиуса с кроссфейдом содержимого:
    /// UITK анимирует style-свойства по тикам, поэтому «перетекание» здесь
    /// такое же родное, как в вебе FLIP.
    ///
    /// Сюда стекается ВСЯ сеть («Скачать всё», прелоад главы, стриминг) через
    /// ContentLoader.Transfers() — раньше прогресс жил только в настройках, и
    /// «закрыл настройки — загрузка пропала» (живой репорт).
    /// </summary>
    public sealed class DownloadHud : VisualElement
    {
        // Геометрия двух состояний капсулы.
        private const float MiniSize = 54f; // чуть шире (просьба Ильи 26.08)
        private const float FullW = 520f;
        private const float FullH = 560f;

        // ── швы к хосту (NovelApp навешивает после Build) ────────────────────
        /// <summary>Очередь глав «Скачать всё» — для списка и крестиков.</summary>
        public DownloadCenter Center;
        /// <summary>Сеть пропала? (LvnNetworkStatus)</summary>
        public Func<bool> Offline;
        /// <summary>Событий кошелька/прогресса, ждущих отправки на сервер.</summary>
        public Func<int> PendingOps;
        /// <summary>Главы и их офлайн-доступность (полностью в кэше?). Зовётся
        /// при развороте попапа — проверка по диску не для каждого тика.</summary>
        public Func<List<(string label, bool cached)>> ChaptersInfo;
        /// <summary>«Скачать всю игру» — тот же хук, что в настройках.</summary>
        public Func<Task> DownloadAll;
        /// <summary>Сколько осталось скачать (байт, файлов) — подпись кнопки.</summary>
        public Func<(long bytes, int files)> MissingInfo;
        /// <summary>Подтолкнуть отправку накопленных событий: кошелёк флашится
        /// только на операциях, и без пинка «↑ Синхронизация» висела бы до
        /// следующего действия игрока.</summary>
        public Func<Task> FlushPending;
        private float _lastFlushKick;
        private float _lastMissingAt = -999f;

        private readonly VisualElement _capsule;
        private readonly ProgressRing _miniRing;
        private readonly VisualElement _full;
        private ProgressRing _fullRing;
        private Label _file, _kind;
        private Label _vSpeed, _vQueue, _vGot, _vLeft;
        private ScrollView _sections;
        /// <summary>Текущий качаемый url — для человеческой подписи
        /// («Персонажи и наряды», а не имя файла).</summary>
        public Func<string> ActiveUrl;

        private bool _expanded;
        private float _morph;          // 0 = мини, 1 = полная (текущее положение)
        private long _lastBytes;
        private float _lastAt = -1f;
        private float _speed;          // байт/с, EMA

        private readonly VisualElement _scrim;

        public DownloadHud()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            style.display = DisplayStyle.None; // до первой работы кружка нет

            // Ловец тапов «мимо попапа»: невидим и не мешает, пока попап
            // свёрнут; при развороте ловит клик В ЛЮБОЙ точке экрана и утекает
            // попап обратно в кружок (решение Ильи: крестик или тап вне).
            _scrim = new VisualElement();
            _scrim.style.position = Position.Absolute;
            _scrim.style.left = 0; _scrim.style.right = 0;
            _scrim.style.top = 0; _scrim.style.bottom = 0;
            _scrim.style.display = DisplayStyle.None;
            _scrim.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                SetExpanded(false);
            });
            Add(_scrim);

            _capsule = new VisualElement();
            // СТАТИЧНЫЙ элемент шапки, справа (решение Ильи 26.08: кружок не
            // мигает появлением/исчезновением — он живёт всегда, в простое
            // приглушён). Якорь right/top: морф растёт влево-вниз из его точки.
            _capsule.style.position = Position.Absolute;
            _capsule.style.top = 112;
            _capsule.style.right = 14;
            var bg = LvnTokens.PanelBg;
            // Просто полупрозрачный тон — блюр-стекло снято (Илья, 26.08).
            _capsule.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.94f);
            LvnChrome.Edge(_capsule);
            _capsule.style.overflow = Overflow.Hidden;
            _capsule.style.alignItems = Align.Center;
            _capsule.style.justifyContent = Justify.Center;
            // Разворачивает клик по МИНИ; свёртывание — только крестик или
            // тап мимо (клик по самой карточке ничего не делает — иначе любое
            // случайное касание закрывало бы её).
            _capsule.RegisterCallback<ClickEvent>(_ => { if (!_expanded) SetExpanded(true); });
            _capsule.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            Add(_capsule);

            _miniRing = new ProgressRing(MiniSize * 0.5f - 5f, 3.5f, drawArrow: true);
            _miniRing.style.width = MiniSize; _miniRing.style.height = MiniSize;
            _miniRing.pickingMode = PickingMode.Ignore;
            _capsule.Add(_miniRing);

            // Полное содержимое живёт всегда и кроссфейдится морфом.
            _full = new VisualElement();
            _full.pickingMode = PickingMode.Ignore;
            _full.style.position = Position.Absolute;
            _full.style.left = 18; _full.style.right = 18;
            _full.style.top = 14; _full.style.bottom = 14;
            _full.style.opacity = 0f;
            _capsule.Add(_full);

            var head = new VisualElement();
            head.pickingMode = PickingMode.Ignore;
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.justifyContent = Justify.SpaceBetween;
            _full.Add(head);

            var title = new Label("Загрузки");
            title.pickingMode = PickingMode.Ignore;
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 28;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.Add(title);

            var close = new Label("×");
            close.style.color = LvnTokens.TextDim;
            close.style.fontSize = 24;
            close.style.paddingTop = 6; close.style.paddingBottom = 6;
            close.style.paddingLeft = 10; close.style.paddingRight = 6;
            close.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); SetExpanded(false); });
            head.Add(close);

            var active = new VisualElement();
            active.pickingMode = PickingMode.Ignore;
            active.style.flexDirection = FlexDirection.Row;
            active.style.alignItems = Align.Center;
            active.style.marginTop = 10;
            _full.Add(active);

            _fullRing = new ProgressRing(24f, 4.5f, drawArrow: true);
            _fullRing.style.width = 58; _fullRing.style.height = 58;
            _fullRing.style.marginRight = 12;
            _fullRing.style.flexShrink = 0;
            _fullRing.pickingMode = PickingMode.Ignore;
            active.Add(_fullRing);

            var col = new VisualElement();
            col.pickingMode = PickingMode.Ignore;
            col.style.flexGrow = 1; col.style.flexShrink = 1;
            active.Add(col);

            _file = new Label("");
            _file.pickingMode = PickingMode.Ignore;
            _file.style.color = LvnTokens.Text;
            _file.style.fontSize = 22;
            _file.style.unityFontStyleAndWeight = FontStyle.Bold;
            _file.style.overflow = Overflow.Hidden;
            _file.style.textOverflow = TextOverflow.Ellipsis;
            _file.style.whiteSpace = WhiteSpace.NoWrap;
            col.Add(_file);

            _kind = new Label("");
            _kind.pickingMode = PickingMode.Ignore;
            _kind.style.color = LvnTokens.TextDim;
            _kind.style.fontSize = 19;
            _kind.style.marginTop = 3;
            col.Add(_kind);

            // Поля — ТАБЛИЦЕЙ, по строке на факт (не лапшой через «·»):
            // скорость, очередь, скачано, осталось — всё, что просилось.
            var info = new VisualElement();
            info.pickingMode = PickingMode.Ignore;
            info.style.marginTop = 10;
            info.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Edge(info);
            LvnChrome.Round(info, 14f);
            info.style.paddingTop = 10; info.style.paddingBottom = 10;
            info.style.paddingLeft = 14; info.style.paddingRight = 14;
            _full.Add(info);
            _vSpeed = InfoRow(info, "Скорость");
            _vQueue = InfoRow(info, "В очереди");
            _vGot   = InfoRow(info, "Скачано");
            _vLeft  = InfoRow(info, "Осталось скачать");

            // Секции (офлайн-правила, синк, очередь глав, «скачать всё») —
            // перестраиваются при развороте и по изменению очереди.
            _sections = new ScrollView(ScrollViewMode.Vertical);
            _sections.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _sections.style.flexGrow = 1;
            _sections.style.marginTop = 10;
            _full.Add(_sections);

            ApplyMorph(0f);
        }

        // ── морф мини ↔ полная ────────────────────────────────────────────────

        private void SetExpanded(bool on)
        {
            if (_expanded == on) return;
            _expanded = on;
            _scrim.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            // Секции собираются ПОСЛЕ старта морфа (офлайн-ветка проверяет
            // кэш на диске — десятки миллисекунд, и они не должны съедать
            // первые кадры разворота). Каскад — только при развороте.
            if (on) _capsule.schedule.Execute(() => RebuildSections(animate: true)).ExecuteLater(70);
            float from = _morph, to = on ? 1f : 0f;
            _capsule.experimental.animation.Start(0f, 1f, 260, (_, p) =>
            {
                float e = 1f - Mathf.Pow(1f - p, 3f); // OutCubic — тормозит у цели
                ApplyMorph(Mathf.Lerp(from, to, e));
            });
        }

        private void ApplyMorph(float k)
        {
            _morph = k;
            _capsule.style.width = Mathf.Lerp(MiniSize, FullW, k);
            _capsule.style.height = Mathf.Lerp(MiniSize, FullH, k);
            LvnChrome.Round(_capsule, Mathf.Lerp(MiniSize * 0.5f, 22f, k));
            // Верхняя кромка наливается акцентом по мере разворота — та же
            // «крышка», что у попап-экранов оболочки (AdoptSheet).
            _capsule.style.borderTopWidth = Mathf.Lerp(1f, 2.5f, k);
            _capsule.style.borderTopColor = Color.Lerp(LvnTokens.Border, LvnTokens.Accent, k);
            // Кроссфейд содержимого: мини-кольцо гаснет в первой трети морфа,
            // полная карточка проявляется во второй — в середине капсула
            // «пустая», и перетекание читается формой, а не мешаниной слоёв.
            _miniRing.style.opacity = Mathf.Clamp01(1f - k * 3f);
            _full.style.opacity = Mathf.Clamp01((k - 0.55f) / 0.45f);
        }

        // ── данные ────────────────────────────────────────────────────────────

        /// <summary>Скормить свежий снимок сети (таймер оболочки, ~300 мс).</summary>
        public void Tick((int inflight, int batchTotal, int batchDone, long received, long expected, string label) t)
        {
            float now = Time.realtimeSinceStartup;
            bool act = t.inflight > 0 || (t.batchTotal > 0 && t.batchDone < t.batchTotal);
            bool off = Offline?.Invoke() ?? false;
            bool queued = Center != null && (Center.Running || Center.Queue.Count > 0);
            int pend = PendingOps?.Invoke() ?? 0;
            // Кружок видим, пока ЕСТЬ РАБОТА: активная загрузка, непустая
            // очередь глав (паузы между файлами и главами НЕ прячут его —
            // «мигает», живой репорт), офлайн с очередью или несинхроненные
            // события. Скрывается только в настоящем простое.
            bool visible = act || queued || pend > 0;

            if (_lastAt > 0f && t.received >= _lastBytes)
            {
                float dt = now - _lastAt;
                if (dt > 0.05f)
                {
                    float inst = (t.received - _lastBytes) / dt;
                    _speed = _speed <= 0f ? inst : Mathf.Lerp(_speed, inst, 0.35f);
                }
            }
            else if (t.received < _lastBytes) _speed = 0f;
            _lastBytes = t.received;
            _lastAt = now;

            if (visible)
            {
                _quietSince = -1f;
                if (!_shown)
                {
                    _shown = true;
                    style.display = DisplayStyle.Flex;
                    _capsule.experimental.animation.Start(0f, 1f, 180,
                        (_, p) => _capsule.style.opacity = p);
                }

                var glyph = off && (act || queued) ? RingGlyph.Alert
                    : act ? RingGlyph.Down
                    : RingGlyph.Up;
                if (glyph == RingGlyph.Up && !off && FlushPending != null
                    && now - _lastFlushKick > 5f)
                {
                    _lastFlushKick = now;
                    _ = FlushPending();
                }
                _miniRing.Glyph = glyph;
                _fullRing.Glyph = glyph;

                // Байтовые счётчики честны только ВНУТРИ батча (в конце он их
                // чистит); фоновый стриминг копит их за сессию — его кольцо
                // врало бы «почти готово». Стриминг — спиннер.
                float frac = act && t.batchTotal > 0
                    ? (t.expected > 0 ? Mathf.Clamp01((float)t.received / t.expected)
                        : Mathf.Clamp01((float)t.batchDone / Mathf.Max(1, t.batchTotal)))
                    : -1f;
                _miniRing.Progress = frac;
                _fullRing.Progress = frac;

                if (glyph == RingGlyph.Alert)
                {
                    _file.text = "Нет соединения";
                    _kind.text = "Загрузка продолжится сама";
                }
                else if (glyph == RingGlyph.Up)
                {
                    _file.text = "Синхронизация";
                    _kind.text = $"Событий к отправке: {pend}";
                }
                else
                {
                    _file.text = string.IsNullOrEmpty(t.label) ? "Загрузка контента" : t.label;
                    var activeEntry = ActiveEntry();
                    _kind.text = Humanize(ActiveUrl?.Invoke(), null)
                        + (activeEntry != null ? " · " + activeEntry.Label : "");
                }
                if (_expanded)
                {
                    _vSpeed.text = _speed > 1024f ? Speed(_speed) : "—";
                    int filesLeft = t.batchTotal > 0 ? Mathf.Max(0, t.batchTotal - t.batchDone) : t.inflight;
                    int chLeft = 0;
                    if (Center != null) foreach (var e in Center.Queue) if (!e.Active) chLeft++;
                    _vQueue.text = chLeft > 0 ? $"{chLeft} глав · {filesLeft} файлов" : $"{filesLeft} файлов";
                    _vGot.text = Mb(t.received) + (t.expected > 0 ? " из " + Mb(t.expected) : "");
                    if (now - _lastMissingAt > 3f)
                    {
                        _lastMissingAt = now;
                        var miss = MissingInfo?.Invoke() ?? (0, 0);
                        _vLeft.text = miss.Item2 > 0 ? $"≈{Mathf.Max(1, miss.Item1 >> 20)} МБ" : "всё скачано";
                    }
                }
                if (_expanded && Center != null && _centerDirty) { _centerDirty = false; RebuildSections(animate: false); }
            }
            else if (_shown && !_expanded)
            {
                // Настоящий простой (работы нет) — мягко уходим, с запасом
                // против мигания на коротких паузах.
                if (_quietSince < 0f) _quietSince = now;
                if (now - _quietSince > 2f)
                {
                    _shown = false;
                    _quietSince = -1f;
                    _speed = 0f;
                    _capsule.experimental.animation.Start(1f, 0f, 200, (_, p) =>
                    {
                        _capsule.style.opacity = p;
                        if (p <= 0.01f) style.display = DisplayStyle.None;
                    });
                }
            }
        }

        private bool _shown;
        private float _quietSince = -1f;

        private bool _centerDirty;
        private DownloadCenter _watched;

        private DownloadCenter.Entry ActiveEntry()
        {
            if (Center == null) return null;
            // Подписка на очередь — лениво, когда центр появился у хоста.
            if (_watched != Center)
            {
                if (_watched != null) _watched.Changed -= MarkCenterDirty;
                _watched = Center;
                _watched.Changed += MarkCenterDirty;
            }
            foreach (var e in Center.Queue) if (e.Active) return e;
            return null;
        }

        private void MarkCenterDirty() => _centerDirty = true;

        /// <summary>Человеческая подпись того, что качается: класс файла
        /// словами игрока, не именем файла (решение Ильи: «скачиваем героиню
        /// и фаворитов», а не cr_transcoded_layer_0000).</summary>
        private static string Humanize(string url, string fallback)
        {
            if (string.IsNullOrEmpty(url))
                return string.IsNullOrEmpty(fallback) ? "Файлы игры" : fallback;
            switch (Lvn.Content.DownloadPolicy.Classify(url))
            {
                case Lvn.Content.AssetClass.Actor: return "Персонажи и наряды";
                case Lvn.Content.AssetClass.SceneBg: return "Фоны сцен";
                case Lvn.Content.AssetClass.ChapterBg: return "Экраны глав";
                case Lvn.Content.AssetClass.Cover: return "Обложки историй";
                case Lvn.Content.AssetClass.Audio: return "Музыка и звуки";
                case Lvn.Content.AssetClass.Script: return "Текст глав";
                case Lvn.Content.AssetClass.Ui: return "Интерфейс";
            }
            if (url.Contains("/sprites/")) return "Персонажи и наряды";
            return "Файлы игры";
        }

        // ── секции попапа ─────────────────────────────────────────────────────

        private void RebuildSections(bool animate = false)
        {
            if (_sections == null) return;
            _sections.Clear();

            bool off = Offline?.Invoke() ?? false;
            int pend = PendingOps?.Invoke() ?? 0;

            if (off)
            {
                var card = SectionCard();
                card.Add(SectionTitle("Играть без интернета"));
                card.Add(Hint("Доступно всё, что уже скачано: главы ниже с галочкой "
                    + "откроются в самолёте и без сети. Скачайте игру целиком — "
                    + "и читайте где угодно; покупки за кристаллы тоже работают "
                    + "офлайн и синхронизируются позже."));
                var chapters = ChaptersInfo?.Invoke();
                if (chapters != null)
                    foreach (var (label, cached) in chapters)
                        card.Add(ChapterRow(label, cached));
                _sections.Add(card);
            }

            if (pend > 0)
            {
                var card = SectionCard();
                card.Add(SectionTitle("Ждут отправки"));
                card.Add(Hint(off
                    ? $"Событий: {pend} — покупки и прогресс сохранены на устройстве и уедут на сервер, как только появится сеть."
                    : $"Отправляем на сервер: {pend} событий (покупки, прогресс)."));
                _sections.Add(card);
            }

            if (Center != null && Center.Queue.Count > 0)
            {
                var card = SectionCard();
                card.Add(SectionTitle("Очередь загрузки"));
                foreach (var e in Center.Queue)
                    card.Add(QueueRow(e));
                _sections.Add(card);
            }

            var missingPlaceholder = 0; // (маркер позиции — каскад ниже)
            var missing = MissingInfo?.Invoke() ?? (0, 0);
            if (missing.Item2 > 0 && DownloadAll != null && !(Center != null && Center.Queue.Count > 0))
            {
                var card = SectionCard();
                card.Add(SectionTitle("Вся игра — с собой"));
                card.Add(Hint("Скачайте один раз и играйте без интернета: главы, арт и музыка останутся на устройстве."));
                var btn = new Button { text = $"Скачать всё ≈{Mathf.Max(1, missing.Item1 >> 20)} МБ" };
                btn.style.height = 52;
                btn.style.fontSize = 22;
                btn.style.marginTop = 8;
                btn.style.color = LvnTokens.OnAccent;
                btn.style.backgroundColor = LvnTokens.Accent;
                LvnChrome.ClearBorder(btn);
                LvnChrome.Round(btn, 14f);
                btn.clicked += () => { btn.SetEnabled(false); _ = DownloadAll(); };
                card.Add(btn);
                _sections.Add(card);
            }
            if (animate) CascadeIn();
        }

        private VisualElement SectionCard()
        {
            var card = new VisualElement();
            card.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Edge(card); // тонкий бордер токеном — карточка, не пятно
            LvnChrome.Round(card, 14f);
            card.style.paddingTop = 12; card.style.paddingBottom = 12;
            card.style.paddingLeft = 14; card.style.paddingRight = 14;
            card.style.marginBottom = 10;
            return card;
        }

        // Каскад: карточки прибывают одна за другой (fade + подъём) — попап
        // «наполняется», а не вспыхивает готовым.
        private void CascadeIn()
        {
            int i = 0;
            foreach (var child in _sections.Children())
            {
                var el = child;
                el.style.opacity = 0f;
                el.style.translate = new Translate(0f, 10f);
                int delay = 60 + i * 55;
                el.schedule.Execute(() =>
                    el.experimental.animation.Start(0f, 1f, 220, (e2, t) =>
                    {
                        float e = 1f - Mathf.Pow(1f - t, 3f);
                        e2.style.opacity = e;
                        e2.style.translate = new Translate(0f, Mathf.Lerp(10f, 0f, e));
                    })).ExecuteLater(delay);
                i++;
            }
        }

        // Строка «подпись … значение»: подпись тусклая слева, значение
        // ярко справа — читается таблицей, а не предложением.
        private Label InfoRow(VisualElement host, string caption)
        {
            var row = new VisualElement();
            row.pickingMode = PickingMode.Ignore;
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 4; row.style.marginBottom = 4;
            var c = new Label(caption);
            c.pickingMode = PickingMode.Ignore;
            c.style.color = LvnTokens.TextDim;
            c.style.fontSize = 20;
            row.Add(c);
            var v = new Label("—");
            v.pickingMode = PickingMode.Ignore;
            v.style.color = LvnTokens.Text;
            v.style.fontSize = 20;
            v.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(v);
            host.Add(row);
            return v;
        }

        private Label SectionTitle(string text)
        {
            var l = new Label(text);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.Text;
            l.style.fontSize = 22;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginBottom = 4;
            return l;
        }

        private Label Hint(string text)
        {
            var l = new Label(text);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.TextDim;
            l.style.fontSize = 19;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        private VisualElement ChapterRow(string label, bool cached)
        {
            var row = new VisualElement();
            row.pickingMode = PickingMode.Ignore;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 6;
            var mark = new Label(cached ? "√" : "○");
            mark.pickingMode = PickingMode.Ignore;
            mark.style.color = cached ? LvnTokens.Accent : LvnTokens.TextDim;
            mark.style.fontSize = 20;
            mark.style.width = 26;
            row.Add(mark);
            var l = new Label(label);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = cached ? LvnTokens.Text : LvnTokens.TextDim;
            l.style.fontSize = 20;
            row.Add(l);
            return row;
        }

        private VisualElement QueueRow(DownloadCenter.Entry e)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginTop = 6;
            if (e.Active)
            {
                // Активная глава помечена акцентной кромкой слева — бордер
                // работает как маркер состояния, в языке Полуночи.
                row.style.borderLeftWidth = 3f;
                row.style.borderLeftColor = LvnTokens.Accent;
                row.style.paddingLeft = 8;
            }
            var l = new Label((e.Active ? "▶ " : "") + e.Label
                + (e.Bytes > 0 ? $" · {Mathf.Max(1, e.Bytes >> 20)} МБ" : ""));
            l.pickingMode = PickingMode.Ignore;
            l.style.color = e.Active ? LvnTokens.Text : LvnTokens.TextDim;
            l.style.fontSize = 20;
            l.style.overflow = Overflow.Hidden;
            l.style.textOverflow = TextOverflow.Ellipsis;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            l.style.flexShrink = 1;
            row.Add(l);
            var x = new Label("×");
            x.style.color = LvnTokens.TextDim;
            x.style.fontSize = 20;
            x.style.paddingLeft = 10; x.style.paddingRight = 4;
            x.style.flexShrink = 0;
            var entry = e;
            x.RegisterCallback<ClickEvent>(ev =>
            {
                ev.StopPropagation();
                // Строка уезжает и схлопывается — и только потом выбывает из
                // очереди: снятие видно, а не «мигнуло и нет».
                float h0 = row.resolvedStyle.height;
                row.experimental.animation.Start(0f, 1f, 180, (r, t) =>
                {
                    r.style.opacity = 1f - t;
                    r.style.translate = new Translate(Mathf.Lerp(0f, 40f, t * t), 0f);
                    if (h0 > 1f) r.style.height = Mathf.Lerp(h0, 0f, t);
                    if (t >= 1f) { Center?.Remove(entry); RebuildSections(); }
                });
            });
            row.Add(x);
            return row;
        }

        private static string Mb(long bytes)
            => bytes >= 100L << 20 ? $"{bytes >> 20} МБ"
             : $"{bytes / 1048576f:0.#} МБ".Replace('.', ',');

        private static string Speed(float bytesPerSec)
            => bytesPerSec >= 1048576f
                ? $"{bytesPerSec / 1048576f:0.#} МБ/с".Replace('.', ',')
                : $"{Mathf.RoundToInt(bytesPerSec / 1024f)} КБ/с";

        /// <summary>Что рисуется внутри кольца: стрелка вниз (загрузка),
        /// «!» (офлайн при живой очереди), стрелка вверх (синхронизация —
        /// события уезжают на сервер).</summary>
        public enum RingGlyph { Down, Alert, Up }

        /// <summary>Хромовский значок загрузки чистым painter2D: глиф в центре
        /// и кольцо прогресса вокруг. Progress &lt; 0 — прогресс неизвестен:
        /// короткая дуга крутится сама (спиннер).</summary>
        private sealed class ProgressRing : VisualElement
        {
            private readonly float _radius, _stroke;
            private readonly bool _arrow;
            private float _progress = -1f;  // цель
            private float _shown = -1f;     // что нарисовано: плывёт к цели
            private float _spin;
            private RingGlyph _glyph = RingGlyph.Down;

            public RingGlyph Glyph
            {
                get => _glyph;
                set
                {
                    if (_glyph == value) return;
                    _glyph = value;
                    // Смена состояния — короткий пульс: глаз ловит перемену.
                    this.experimental.animation.Start(0f, 1f, 240, (e, t) =>
                    {
                        float k = 1f + 0.14f * Mathf.Sin(t * Mathf.PI);
                        e.style.scale = new Scale(new Vector2(k, k));
                    });
                    MarkDirtyRepaint();
                }
            }

            public float Progress
            {
                get => _progress;
                set { _progress = value; MarkDirtyRepaint(); }
            }

            public ProgressRing(float radius, float stroke, bool drawArrow)
            {
                _radius = radius; _stroke = stroke; _arrow = drawArrow;
                generateVisualContent += Draw;
                // Спиннеру нужен ход времени; при известном прогрессе тик
                // просто перерисовывает свежую дугу.
                schedule.Execute(() =>
                {
                    _spin = (_spin + 5f) % 360f;
                    // Дуга не скачет между тиками данных (300 мс), а плывёт.
                    if (_progress >= 0f)
                        _shown = _shown < 0f ? _progress
                            : Mathf.Lerp(_shown, _progress, 0.18f);
                    else _shown = -1f;
                    MarkDirtyRepaint();
                }).Every(33);
            }

            private void Draw(MeshGenerationContext mgc)
            {
                var p = mgc.painter2D;
                var c = new Vector2(resolvedStyle.width * 0.5f, resolvedStyle.height * 0.5f);

                // Фоновое кольцо.
                p.lineWidth = _stroke;
                p.strokeColor = LvnTokens.Faint;
                p.BeginPath();
                p.Arc(c, _radius, 0f, 360f);
                p.Stroke();

                // Дуга прогресса — от «12 часов» по часовой; спиннер — бегущая
                // четверть круга.
                p.strokeColor = LvnTokens.Accent;
                p.lineCap = LineCap.Round;
                p.BeginPath();
                if (_shown >= 0f)
                    p.Arc(c, _radius, -90f, -90f + 360f * Mathf.Clamp01(_shown));
                else
                    p.Arc(c, _radius, _spin, _spin + 90f);
                p.Stroke();

                if (!_arrow) return;
                float a = _radius * 0.52f;
                p.lineWidth = Mathf.Max(2f, _stroke * 0.8f);
                p.lineJoin = LineJoin.Round;
                if (_glyph == RingGlyph.Alert)
                {
                    // «!»: штрих + точка — сеть пропала, загрузка ждёт.
                    p.strokeColor = new Color(1f, 0.76f, 0.3f);
                    p.BeginPath();
                    p.MoveTo(new Vector2(c.x, c.y - a));
                    p.LineTo(new Vector2(c.x, c.y + a * 0.35f));
                    p.Stroke();
                    p.BeginPath();
                    p.Arc(new Vector2(c.x, c.y + a * 0.85f), p.lineWidth * 0.55f, 0f, 360f);
                    p.Stroke();
                    return;
                }
                // Стрелка (вниз — загрузка; вверх — синк) + полочка, как у Chrome.
                float dirY = _glyph == RingGlyph.Up ? -1f : 1f;
                p.strokeColor = LvnTokens.Text;
                p.BeginPath();
                p.MoveTo(new Vector2(c.x, c.y - a * dirY));
                p.LineTo(new Vector2(c.x, c.y + a * 0.55f * dirY));
                p.Stroke();
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - a * 0.6f, c.y - a * 0.05f * dirY));
                p.LineTo(new Vector2(c.x, c.y + a * 0.62f * dirY));
                p.LineTo(new Vector2(c.x + a * 0.6f, c.y - a * 0.05f * dirY));
                p.Stroke();
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - a * 0.7f, c.y + a));
                p.LineTo(new Vector2(c.x + a * 0.7f, c.y + a));
                p.Stroke();
            }
        }
    }
}
