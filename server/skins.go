package main

// ОДНО МЕСТО НАСТРОЙКИ СКИНОВ (TR-114, Илья 15.09: «любой контент — скин;
// единообразная система, настраиваемая через админку; фул настройка в одном
// месте»). Скины жили в четырёх местах с разными полями: наряды в
// sprites.<герой>.wardrobe, фоны меню в ui.browse.canvas_options, аватарки в
// ui.browse.avatars, призы круток в gacha.json — и каждое поле (цена, ступень,
// арт, «в крутке») приходилось править в двух-трёх местах руками.
//
// Теперь источник один — skins.json. Сервер умеет две вещи:
//   - СОБРАТЬ каталог из манифеста и барабана (миграция без потерь; повтор
//     добавляет новое и не трогает правленое);
//   - ПРИМЕНИТЬ каталог: разложить цены, ступени, арт, названия и флаг «в
//     крутке» обратно в manifest.json и prizes барабана. Сохранение skins.json
//     из админки применяет его само — «сохранил = задеплоил», как у манифеста.
// Цвет — у СТУПЕНИ (палитра rarity_colors), не у скина: решение Ильи.
// Клиент пока читает манифест, как читал; отдельный реестр — следующий этап.

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
)

// skin — одна вещь, которую можно надеть, показать или включить.
type skin struct {
	SKU         string   `json:"sku"`                    // ключ инвентаря кошелька
	Kind        string   `json:"kind"`                   // wardrobe | backdrop | avatar
	Name        string   `json:"name,omitempty"`         // подпись
	Description string   `json:"description,omitempty"`  // описание — в подробностях плитки (Илья 15.09)
	Art         string   `json:"art,omitempty"`          // полный арт (слой, картина, аватар)
	Preview     string   `json:"preview,omitempty"`      // мини для витрины (фоны)
	Spine       string   `json:"spine,omitempty"`        // живой фон меню: папка спайна (/content/spine/имя/); art остаётся обложкой и подложкой (TR-133)
	Rarity      string   `json:"rarity,omitempty"`       // ступень; цвет — у палитры ступеней
	Price       int64    `json:"price,omitempty"`        // цена скина: покупка в гардеробе и продажа копии
	Currency    string   `json:"currency,omitempty"`     //
	Buy         bool     `json:"buy,omitempty"`          // продаётся в гардеробе; false при цене — «только из крутки»
	SellPrice   int64    `json:"sell_price,omitempty"`   // за сколько продаётся копия из крутки; 0 — за цену
	Hidden      bool     `json:"hidden,omitempty"`       // не показывать нигде (в инвентаре у игроков остаётся)
	Order       int      `json:"order,omitempty"`        // порядок показа внутри домена/оси; 0 — как в манифесте
	Tags        string   `json:"tags,omitempty"`         // метки через запятую — для поиска и будущих наборов
	Gacha       bool     `json:"gacha,omitempty"`        // выпадает в крутках
	Weight      float64  `json:"gacha_weight,omitempty"` // свой вес в барабане; 0 — по ступени
	Cases       []string `json:"cases,omitempty"`        // в каких наборах приз; пусто при gacha — набор по умолчанию
}

// currencyLook — КАК ВЫГЛЯДИТ ВАЛЮТА (TR-117): имя, единица при сумме, цвет,
// вектор движка и картинка. Одно место вместо четырёх карт манифеста
// (ui.currency_look и currency_icons шапки/гардероба/магазина): картинка,
// заданная здесь, показывается везде — шапка, ценники, плитки, крутки.
type currencyLook struct {
	Name  string `json:"name,omitempty"`
	Unit  string `json:"unit,omitempty"`
	Icon  string `json:"icon,omitempty"`
	Color string `json:"color,omitempty"`
	Image string `json:"image,omitempty"`
}

type skinsConfig struct {
	RarityColors  map[string]string       `json:"rarity_colors,omitempty"`
	RarityWeights map[string]float64      `json:"rarity_weights,omitempty"`
	Currencies    map[string]currencyLook `json:"currencies,omitempty"`
	// НАБОРЫ КРУТОК — тоже здесь, одно место: имя, описание, обложка, цена,
	// сектора; призы набора — скины, у которых он назван в cases.
	Cases []gachaCase `json:"cases,omitempty"`
	Skins []skin      `json:"skins"`
}

const skinsFile = "skins.json"

// Ключи инвентаря — те же, что уже живут в кошельках игроков: менять их
// значило бы отнять купленное.
func wardrobeSKU(entity, axis, value string) string {
	return "wardrobe:" + entity + ":" + axis + ":" + value
}
func backdropSKU(id string) string { return "wardrobe:menu:backdrop:" + id }
func avatarSKU(id, sku string) string {
	if sku != "" {
		return sku
	}
	return "avatar." + id
}

type SkinsService struct {
	content string
	mu      sync.Mutex
	cfg     *hotJSON[skinsConfig]
}

func NewSkinsService(content string) *SkinsService {
	return &SkinsService{content: content, cfg: newHotJSON(filepath.Join(content, skinsFile), skinsConfig{})}
}

func (s *SkinsService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/skins", s.handleCatalog)
}

// Каталог — публичный: клиенту он нужен для витрины и подробностей.
func (s *SkinsService) handleCatalog(w http.ResponseWriter, r *http.Request) {
	cfg := s.cfg.Get()
	if cfg.Skins == nil {
		cfg.Skins = []skin{}
	}
	writeJSON(w, http.StatusOK, cfg)
}

// ── сбор из манифеста ───────────────────────────────────────────────────────

func skinStr(m map[string]any, key string) string {
	if v, ok := m[key].(string); ok {
		return v
	}
	return ""
}
func skinNum(m map[string]any, key string) float64 {
	switch v := m[key].(type) {
	case float64:
		return v
	case int64:
		return float64(v)
	case int:
		return float64(v)
	}
	return 0
}
func skinBool(m map[string]any, key string) bool { v, _ := m[key].(bool); return v }
func skinDig(m map[string]any, path ...string) map[string]any {
	cur := m
	for _, p := range path {
		next, ok := cur[p].(map[string]any)
		if !ok {
			return nil
		}
		cur = next
	}
	return cur
}
func skinList(m map[string]any, key string) []any {
	if m == nil {
		return nil
	}
	v, _ := m[key].([]any)
	return v
}

func readJSONMap(path string) (map[string]any, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var m map[string]any
	if err := json.Unmarshal(data, &m); err != nil {
		return nil, fmt.Errorf("%s: %w", filepath.Base(path), err)
	}
	return m, nil
}

// collectSkins — каталог из манифеста и барабана. Существующие записи (по sku)
// остаются как есть: сбор добавляет новое, а не переписывает правленое.
func collectSkins(manifest map[string]any, gacha gachaConfig, existing skinsConfig) skinsConfig {
	out := skinsConfig{RarityColors: existing.RarityColors, RarityWeights: existing.RarityWeights, Currencies: existing.Currencies}
	if out.Currencies == nil {
		out.Currencies = collectCurrencies(manifest)
	}
	known := map[string]skin{}
	for _, sk := range existing.Skins {
		known[sk.SKU] = sk
	}
	inGacha := map[string]gachaPrize{}
	for _, p := range gacha.Prizes {
		inGacha[p.SKU] = p
	}
	if out.RarityColors == nil {
		if colors := skinDig(manifest, "ui", "wardrobe", "rarity_colors"); colors != nil {
			out.RarityColors = map[string]string{}
			for k, v := range colors {
				if s, ok := v.(string); ok {
					out.RarityColors[k] = s
				}
			}
		}
	}
	if out.RarityWeights == nil && len(gacha.RarityWeights) > 0 {
		out.RarityWeights = gacha.RarityWeights
	}
	// Наборы: из файла барабана, а верхний уровень — набор по умолчанию.
	// Правленые наборы каталога остаются как есть.
	if len(existing.Cases) > 0 {
		out.Cases = existing.Cases
	} else {
		for _, k := range gacha.cases() {
			k.Prizes = nil // призы набора — у скинов (cases), не второй список
			if k.ID == defaultCaseID && k.Name == "" {
				k.Name = "Набор"
			}
			out.Cases = append(out.Cases, k)
		}
	}
	memberOf := map[string][]string{}
	for _, k := range gacha.cases() {
		for _, p := range k.Prizes {
			memberOf[p.SKU] = append(memberOf[p.SKU], k.ID)
		}
	}
	add := func(sk skin) {
		if old, ok := known[sk.SKU]; ok {
			out.Skins = append(out.Skins, old)
			delete(known, sk.SKU)
			return
		}
		if p, ok := inGacha[sk.SKU]; ok {
			sk.Gacha = true
			if sk.Rarity == "" {
				sk.Rarity = p.Rarity
			}
		}
		if cases := memberOf[sk.SKU]; len(cases) > 0 {
			sk.Gacha = true
			if !(len(cases) == 1 && cases[0] == defaultCaseID) {
				sk.Cases = cases
			}
		}
		out.Skins = append(out.Skins, sk)
	}
	// Наряды — по героям и осям, в порядке манифеста.
	sprites := skinDig(manifest, "sprites")
	for _, entity := range skinKeys(sprites) {
		wardrobe := skinDig(sprites, entity, "wardrobe")
		for _, axis := range skinKeys(wardrobe) {
			for _, raw := range skinList(skinDig(wardrobe, axis), "items") {
				it, _ := raw.(map[string]any)
				value := skinStr(it, "value")
				if it == nil || value == "" || value == "__none__" {
					continue
				}
				// В гардеробе флаг gacha значит «только из крутки, не купить» (TR-93).
				price := int64(skinNum(it, "price"))
				add(skin{SKU: wardrobeSKU(entity, axis, value), Kind: "wardrobe",
					Name: skinStr(it, "name"), Description: skinStr(it, "description"), Art: skinStr(it, "icon"), Rarity: skinStr(it, "rarity"),
					Price: price, Currency: skinStr(it, "currency"), Gacha: skinBool(it, "gacha"),
					Buy: price > 0 && !skinBool(it, "gacha"), Hidden: skinBool(it, "hidden"),
					SellPrice: int64(skinNum(it, "sell_price")), Order: int(skinNum(it, "order")), Tags: skinStr(it, "tags")})
			}
		}
	}
	for _, raw := range skinList(skinDig(manifest, "ui", "browse"), "canvas_options") {
		o, _ := raw.(map[string]any)
		if o == nil || skinStr(o, "id") == "" {
			continue
		}
		add(skin{SKU: backdropSKU(skinStr(o, "id")), Kind: "backdrop", Name: skinStr(o, "title"), Description: skinStr(o, "description"),
			Art: skinStr(o, "url"), Preview: skinStr(o, "preview"), Spine: skinStr(o, "spine"), Rarity: skinStr(o, "rarity"),
			Price: int64(skinNum(o, "price")), Currency: skinStr(o, "currency"), Buy: skinNum(o, "price") > 0,
			Hidden: skinBool(o, "hidden"), SellPrice: int64(skinNum(o, "sell_price")), Order: int(skinNum(o, "order")), Tags: skinStr(o, "tags")})
	}
	for _, raw := range skinList(skinDig(manifest, "ui", "browse"), "avatars") {
		a, _ := raw.(map[string]any)
		if a == nil || skinStr(a, "id") == "" {
			continue
		}
		add(skin{SKU: avatarSKU(skinStr(a, "id"), skinStr(a, "sku")), Kind: "avatar", Name: skinStr(a, "id"), Description: skinStr(a, "description"),
			Art: skinStr(a, "url"), Price: int64(skinNum(a, "price")), Currency: skinStr(a, "currency"), Buy: skinNum(a, "price") > 0,
			Hidden: skinBool(a, "hidden"), SellPrice: int64(skinNum(a, "sell_price")), Order: int(skinNum(a, "order")), Tags: skinStr(a, "tags")})
	}
	// Правленые записи, которых в манифесте уже нет, — не теряем.
	for _, sku := range skinKeys(known) {
		out.Skins = append(out.Skins, known[sku])
	}
	return out
}

func skinKeys[T any](m map[string]T) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// ── применение к манифесту и барабану ───────────────────────────────────────

func setIf(m map[string]any, key, value string) {
	if value != "" {
		m[key] = value
	}
}

// setOrDrop — поле либо со смыслом, либо его нет: применение не должно
// засорять манифест нулями и пустыми строками там, где их не было.
func setOrDrop(m map[string]any, key string, value any, keep bool) {
	if keep {
		m[key] = value
	} else {
		delete(m, key)
	}
}

// applyCommon — поля, одинаковые у всех домов манифеста: описание, ступень,
// цена, «скрыт», порядок, метки, цена копии.
func applyCommon(m map[string]any, sk skin) {
	setOrDrop(m, "description", sk.Description, sk.Description != "")
	setOrDrop(m, "rarity", sk.Rarity, sk.Rarity != "")
	setOrDrop(m, "price", sk.Price, sk.Price > 0)
	setIf(m, "currency", sk.Currency)
	setOrDrop(m, "hidden", true, sk.Hidden)
	setOrDrop(m, "order", sk.Order, sk.Order != 0)
	setOrDrop(m, "tags", sk.Tags, sk.Tags != "")
	setOrDrop(m, "sell_price", sk.SellPrice, sk.SellPrice > 0)
}

// sortByOrder — записи с порядком встают по нему, остальные — как были, после.
func sortByOrder(items []any) {
	sort.SliceStable(items, func(i, j int) bool {
		a, _ := items[i].(map[string]any)
		b, _ := items[j].(map[string]any)
		oa, ob := int(skinNum(a, "order")), int(skinNum(b, "order"))
		if oa == 0 || ob == 0 {
			return oa != 0 && ob == 0
		}
		return oa < ob
	})
}

// ensureList — список в карте по ключу; нет — создаётся.
// ensureMap — вложенная карта манифеста: есть — та же, нет — заводится.
func ensureMap(m map[string]any, key string) map[string]any {
	if sub, ok := m[key].(map[string]any); ok {
		return sub
	}
	sub := map[string]any{}
	m[key] = sub
	return sub
}

// collectCurrencies — валюты из манифеста: облик из ui.currency_look, картинка
// — первая найденная по картам значков шапки, гардероба и магазина.
func collectCurrencies(manifest map[string]any) map[string]currencyLook {
	out := map[string]currencyLook{}
	for cur, raw := range skinDig(manifest, "ui", "currency_look") {
		look, _ := raw.(map[string]any)
		if look == nil {
			continue
		}
		out[cur] = currencyLook{Name: skinStr(look, "name"), Unit: skinStr(look, "unit"), Icon: skinStr(look, "icon"), Color: skinStr(look, "color"), Image: skinStr(look, "image")}
	}
	for _, home := range [][]string{{"ui", "browse", "currency_icons"}, {"ui", "wardrobe", "currency_icons"}, {"ui", "store", "currency_icons"}} {
		for cur, raw := range skinDig(manifest, home...) {
			url, _ := raw.(string)
			look := out[cur]
			if url != "" && look.Image == "" {
				look.Image = url
				out[cur] = look
			}
		}
	}
	if len(out) == 0 {
		return nil
	}
	return out
}

// applyCurrencies — валюты каталога в манифест: облик в ui.currency_look,
// картинка — и в карту значков шапки (старые сборки читают только её).
func applyCurrencies(currencies map[string]currencyLook, manifest map[string]any) {
	if len(currencies) == 0 {
		return
	}
	ui := ensureMap(manifest, "ui")
	looks := ensureMap(ui, "currency_look")
	icons := ensureMap(ensureMap(ui, "browse"), "currency_icons")
	for _, cur := range skinKeys(currencies) {
		c := currencies[cur]
		look := ensureMap(looks, cur)
		for key, v := range map[string]string{"name": c.Name, "unit": c.Unit, "icon": c.Icon, "color": c.Color, "image": c.Image} {
			setOrDrop(look, key, v, v != "")
		}
		setOrDrop(icons, cur, c.Image, c.Image != "")
	}
}

func ensureList(m map[string]any, key string) []any {
	if l, ok := m[key].([]any); ok {
		return l
	}
	l := []any{}
	m[key] = l
	return l
}

// applySkins — разложить каталог по манифесту и барабану. Возвращает, сколько
// скинов нашли своё место в манифесте. Аватарки и фоны, которых в манифесте
// нет, заводятся (Илья: «через админку по максимуму»); наряд заводится, если
// есть его герой и ось.
func applySkins(cfg skinsConfig, manifest map[string]any, gacha *gachaConfig) int {
	bySKU := map[string]skin{}
	seen := map[string]bool{}
	for _, sk := range cfg.Skins {
		bySKU[sk.SKU] = sk
	}
	placed := 0
	sprites := skinDig(manifest, "sprites")
	for entity := range sprites {
		wardrobe := skinDig(sprites, entity, "wardrobe")
		for axis := range wardrobe {
			for _, raw := range skinList(skinDig(wardrobe, axis), "items") {
				it, _ := raw.(map[string]any)
				if it == nil {
					continue
				}
				sk, ok := bySKU[wardrobeSKU(entity, axis, skinStr(it, "value"))]
				if !ok {
					continue
				}
				seen[sk.SKU] = true
				setIf(it, "name", sk.Name)
				setIf(it, "icon", sk.Art)
				applyCommon(it, sk)
				// ФЛАГ gacha В ГАРДЕРОБЕ ЗНАЧИТ «ТОЛЬКО ИЗ КРУТКИ — не покупается»
				// (TR-93): его несут скины из крутки, которые не продаются (buy
				// false); цена у них остаётся — за неё продаётся копия.
				setOrDrop(it, "gacha", true, sk.Gacha && !sk.Buy)
				placed++
			}
		}
	}
	// Новые наряды из каталога — в существующие ось и героя.
	for _, sk := range cfg.Skins {
		if sk.Kind != "wardrobe" || seen[sk.SKU] {
			continue
		}
		parts := strings.Split(sk.SKU, ":")
		if len(parts) != 4 {
			continue
		}
		slot := skinDig(sprites, parts[1], "wardrobe", parts[2])
		if slot == nil {
			continue
		}
		it := map[string]any{"value": parts[3]}
		setIf(it, "name", sk.Name)
		setIf(it, "icon", sk.Art)
		applyCommon(it, sk)
		setOrDrop(it, "gacha", true, sk.Gacha && !sk.Buy)
		slot["items"] = append(ensureList(slot, "items"), it)
		seen[sk.SKU] = true
		placed++
	}
	for entity := range sprites {
		wardrobe := skinDig(sprites, entity, "wardrobe")
		for axis := range wardrobe {
			if slot := skinDig(wardrobe, axis); slot != nil {
				sortByOrder(skinList(slot, "items"))
			}
		}
	}
	browse := skinDig(manifest, "ui", "browse")
	if browse == nil {
		ui, _ := manifest["ui"].(map[string]any)
		if ui == nil {
			ui = map[string]any{}
			manifest["ui"] = ui
		}
		browse = map[string]any{}
		ui["browse"] = browse
	}
	for _, raw := range skinList(browse, "canvas_options") {
		o, _ := raw.(map[string]any)
		if o == nil {
			continue
		}
		sk, ok := bySKU[backdropSKU(skinStr(o, "id"))]
		if !ok {
			continue
		}
		seen[sk.SKU] = true
		setIf(o, "title", sk.Name)
		setIf(o, "url", sk.Art)
		setIf(o, "preview", sk.Preview)
		setOrDrop(o, "spine", sk.Spine, sk.Spine != "")
		applyCommon(o, sk)
		placed++
	}
	for _, raw := range skinList(browse, "avatars") {
		a, _ := raw.(map[string]any)
		if a == nil {
			continue
		}
		sk, ok := bySKU[avatarSKU(skinStr(a, "id"), skinStr(a, "sku"))]
		if !ok {
			continue
		}
		seen[sk.SKU] = true
		setIf(a, "url", sk.Art)
		applyCommon(a, sk)
		if sk.Price <= 0 {
			delete(a, "price")
		}
		placed++
	}
	// Новые фоны и аватарки из каталога — заводятся в манифесте.
	for _, sk := range cfg.Skins {
		if seen[sk.SKU] || sk.Art == "" {
			continue
		}
		switch sk.Kind {
		case "backdrop":
			o := map[string]any{"id": strings.TrimPrefix(sk.SKU, "wardrobe:menu:backdrop:"), "url": sk.Art}
			setIf(o, "title", sk.Name)
			setIf(o, "preview", sk.Preview)
		setOrDrop(o, "spine", sk.Spine, sk.Spine != "")
			applyCommon(o, sk)
			browse["canvas_options"] = append(ensureList(browse, "canvas_options"), o)
			placed++
		case "avatar":
			a := map[string]any{"id": strings.TrimPrefix(sk.SKU, "avatar."), "url": sk.Art}
			if !strings.HasPrefix(sk.SKU, "avatar.") {
				a["sku"] = sk.SKU
			}
			applyCommon(a, sk)
			if sk.Price <= 0 {
				delete(a, "price")
			}
			browse["avatars"] = append(ensureList(browse, "avatars"), a)
			placed++
		}
	}
	sortByOrder(skinList(browse, "canvas_options"))
	sortByOrder(skinList(browse, "avatars"))
	if len(cfg.RarityColors) > 0 {
		ui, _ := manifest["ui"].(map[string]any)
		if ui == nil {
			ui = map[string]any{}
			manifest["ui"] = ui
		}
		wardrobe, _ := ui["wardrobe"].(map[string]any)
		if wardrobe == nil {
			wardrobe = map[string]any{}
			ui["wardrobe"] = wardrobe
		}
		colors := map[string]any{}
		for k, v := range cfg.RarityColors {
			colors[k] = v
		}
		wardrobe["rarity_colors"] = colors
	}
	applyCurrencies(cfg.Currencies, manifest)
	// Барабан: наборы из каталога, призы набора — скины, назвавшие его в
	// cases (gacha без cases — набор по умолчанию). Верхний уровень файла =
	// набор по умолчанию, чтобы старые клиенты жили как жили.
	prizeOf := func(sk skin) gachaPrize {
		return gachaPrize{SKU: sk.SKU, Label: sk.Name, Art: sk.Art, Rarity: sk.Rarity,
			Price: sk.Price, Currency: sk.Currency, Weight: sk.Weight, SellPrice: sk.SellPrice}
	}
	cases := cfg.Cases
	if len(cases) == 0 {
		cases = []gachaCase{{ID: defaultCaseID, Name: "Набор"}}
	}
	inCase := func(sk skin, id string) bool {
		if !sk.Gacha || sk.Hidden {
			return false
		}
		if len(sk.Cases) == 0 {
			return id == defaultCaseID || id == cases[0].ID
		}
		for _, c := range sk.Cases {
			if c == id {
				return true
			}
		}
		return false
	}
	applied := make([]gachaCase, 0, len(cases))
	for _, k := range cases {
		k.Prizes = nil
		for _, sk := range cfg.Skins {
			if inCase(sk, k.ID) {
				k.Prizes = append(k.Prizes, prizeOf(sk))
			}
		}
		if k.Sectors == nil {
			k.Sectors = gacha.Sectors // сектора набора по умолчанию — с верхнего уровня
		}
		applied = append(applied, k)
	}
	gacha.Cases = applied
	gacha.Prizes = applied[0].Prizes
	if applied[0].Price > 0 {
		gacha.Price = applied[0].Price
	}
	if applied[0].Currency != "" {
		gacha.Currency = applied[0].Currency
	}
	if len(cfg.RarityWeights) > 0 {
		gacha.RarityWeights = cfg.RarityWeights
	}
	return placed
}

// ── файлы: собрать и применить на диске ─────────────────────────────────────

func (s *SkinsService) paths() (manifest, gacha, skins string) {
	return filepath.Join(s.content, "manifest.json"), filepath.Join(s.content, "gacha.json"), filepath.Join(s.content, skinsFile)
}

func readGacha(path string) (gachaConfig, error) {
	var g gachaConfig
	data, err := os.ReadFile(path)
	if err != nil {
		return g, err
	}
	return g, json.Unmarshal(data, &g)
}

// Collect — собрать (или досыпать) skins.json из манифеста и барабана.
func (s *SkinsService) Collect() (skinsConfig, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	manifestPath, gachaPath, skinsPath := s.paths()
	manifest, err := readJSONMap(manifestPath)
	if err != nil {
		return skinsConfig{}, err
	}
	gacha, err := readGacha(gachaPath)
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return skinsConfig{}, err
	}
	cfg := collectSkins(manifest, gacha, s.cfg.Get())
	data, _ := json.MarshalIndent(cfg, "", "  ")
	snapshotHistory(s.content, skinsFile)
	if err := atomicWrite(skinsPath, data, 0o644); err != nil {
		return cfg, err
	}
	return cfg, nil
}

// Apply — разложить skins.json по manifest.json и gacha.json (с историей).
func (s *SkinsService) Apply() (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	manifestPath, gachaPath, _ := s.paths()
	cfg := s.cfg.Get()
	if len(cfg.Skins) == 0 {
		return 0, errors.New("skins.json пуст — сначала соберите каталог")
	}
	manifest, err := readJSONMap(manifestPath)
	if err != nil {
		return 0, err
	}
	gacha, err := readGacha(gachaPath)
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return 0, err
	}
	placed := applySkins(cfg, manifest, &gacha)
	mdata, _ := json.MarshalIndent(manifest, "", "  ")
	gdata, _ := json.MarshalIndent(gacha, "", "  ")
	snapshotHistory(s.content, "manifest.json")
	if err := atomicWrite(manifestPath, mdata, 0o644); err != nil {
		return placed, err
	}
	snapshotHistory(s.content, "gacha.json")
	if err := atomicWrite(gachaPath, gdata, 0o644); err != nil {
		return placed, err
	}
	return placed, nil
}

// ── ручки админки ───────────────────────────────────────────────────────────

// handleSkins — «собрать» и «применить» одной ручкой: действие в хвосте пути.
func (s *AdminService) handleSkins(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) || !onlyMethod(w, r, http.MethodPost) {
		return
	}
	if s.skins == nil {
		http.Error(w, "skins service is off", http.StatusServiceUnavailable)
		return
	}
	switch strings.TrimPrefix(r.URL.Path, "/v1/admin/skins/") {
	case "collect":
		cfg, err := s.skins.Collect()
		if err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"collected": len(cfg.Skins)})
	case "apply":
		placed, err := s.skins.Apply()
		if err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"applied": placed})
	default:
		http.Error(w, "collect or apply", http.StatusNotFound)
	}
}
