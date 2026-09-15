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
	SKU      string  `json:"sku"`                    // ключ инвентаря кошелька
	Kind     string  `json:"kind"`                   // wardrobe | backdrop | avatar
	Name     string  `json:"name,omitempty"`         // подпись
	Art      string  `json:"art,omitempty"`          // полный арт (слой, картина, аватар)
	Preview  string  `json:"preview,omitempty"`      // мини для витрины (фоны)
	Rarity   string  `json:"rarity,omitempty"`       // ступень; цвет — у палитры ступеней
	Price    int64   `json:"price,omitempty"`        // 0 — не продаётся (бесплатно или только крутка)
	Currency string  `json:"currency,omitempty"`     //
	Gacha    bool    `json:"gacha,omitempty"`        // выпадает в крутках
	Weight   float64 `json:"gacha_weight,omitempty"` // свой вес в барабане; 0 — по ступени
}

type skinsConfig struct {
	RarityColors  map[string]string  `json:"rarity_colors,omitempty"`
	RarityWeights map[string]float64 `json:"rarity_weights,omitempty"`
	Skins         []skin             `json:"skins"`
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
	out := skinsConfig{RarityColors: existing.RarityColors, RarityWeights: existing.RarityWeights}
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
				add(skin{SKU: wardrobeSKU(entity, axis, value), Kind: "wardrobe",
					Name: skinStr(it, "name"), Art: skinStr(it, "icon"), Rarity: skinStr(it, "rarity"),
					Price: int64(skinNum(it, "price")), Currency: skinStr(it, "currency"), Gacha: skinBool(it, "gacha")})
			}
		}
	}
	for _, raw := range skinList(skinDig(manifest, "ui", "browse"), "canvas_options") {
		o, _ := raw.(map[string]any)
		if o == nil || skinStr(o, "id") == "" {
			continue
		}
		add(skin{SKU: backdropSKU(skinStr(o, "id")), Kind: "backdrop", Name: skinStr(o, "title"),
			Art: skinStr(o, "url"), Preview: skinStr(o, "preview"), Rarity: skinStr(o, "rarity"),
			Price: int64(skinNum(o, "price")), Currency: skinStr(o, "currency")})
	}
	for _, raw := range skinList(skinDig(manifest, "ui", "browse"), "avatars") {
		a, _ := raw.(map[string]any)
		if a == nil || skinStr(a, "id") == "" {
			continue
		}
		add(skin{SKU: avatarSKU(skinStr(a, "id"), skinStr(a, "sku")), Kind: "avatar", Name: skinStr(a, "id"),
			Art: skinStr(a, "url"), Price: int64(skinNum(a, "price")), Currency: skinStr(a, "currency")})
	}
	// Правленые записи, которых в манифесте уже нет, — не теряем.
	for _, sku := range skinKeysOf(known) {
		out.Skins = append(out.Skins, known[sku])
	}
	return out
}

func skinKeys(m map[string]any) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}
func skinKeysOf(m map[string]skin) []string {
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

// applySkins — разложить каталог по манифесту и барабану. Возвращает, сколько
// скинов нашли своё место в манифесте.
func applySkins(cfg skinsConfig, manifest map[string]any, gacha *gachaConfig) int {
	bySKU := map[string]skin{}
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
				setIf(it, "name", sk.Name)
				setIf(it, "icon", sk.Art)
				setOrDrop(it, "rarity", sk.Rarity, sk.Rarity != "")
				setOrDrop(it, "price", sk.Price, sk.Price > 0)
				setIf(it, "currency", sk.Currency)
				// ФЛАГ gacha В ГАРДЕРОБЕ ЗНАЧИТ «ТОЛЬКО ИЗ КРУТКИ — не покупается»
				// (TR-93). Скин с ценой и в крутке продаётся как прежде, и на ленте
				// тоже есть; флаг ставим лишь тем, у кого цены нет.
				setOrDrop(it, "gacha", true, sk.Gacha && sk.Price <= 0)
				placed++
			}
		}
	}
	for _, raw := range skinList(skinDig(manifest, "ui", "browse"), "canvas_options") {
		o, _ := raw.(map[string]any)
		if o == nil {
			continue
		}
		sk, ok := bySKU[backdropSKU(skinStr(o, "id"))]
		if !ok {
			continue
		}
		setIf(o, "title", sk.Name)
		setIf(o, "url", sk.Art)
		setIf(o, "preview", sk.Preview)
		setOrDrop(o, "rarity", sk.Rarity, sk.Rarity != "")
		setOrDrop(o, "price", sk.Price, sk.Price > 0)
		setIf(o, "currency", sk.Currency)
		placed++
	}
	for _, raw := range skinList(skinDig(manifest, "ui", "browse"), "avatars") {
		a, _ := raw.(map[string]any)
		if a == nil {
			continue
		}
		sk, ok := bySKU[avatarSKU(skinStr(a, "id"), skinStr(a, "sku"))]
		if !ok {
			continue
		}
		setIf(a, "url", sk.Art)
		if sk.Price > 0 {
			a["price"] = sk.Price
			setIf(a, "currency", sk.Currency)
		} else {
			delete(a, "price")
		}
		placed++
	}
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
	// Барабан: призы — все скины с флагом; сектора и цена крутки — как были.
	prizes := make([]gachaPrize, 0)
	for _, sk := range cfg.Skins {
		if !sk.Gacha {
			continue
		}
		prizes = append(prizes, gachaPrize{SKU: sk.SKU, Label: sk.Name, Art: sk.Art, Rarity: sk.Rarity,
			Price: sk.Price, Currency: sk.Currency, Weight: sk.Weight})
	}
	gacha.Prizes = prizes
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

func (s *AdminService) handleSkinsCollect(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) || !onlyMethod(w, r, http.MethodPost) {
		return
	}
	if s.skins == nil {
		http.Error(w, "skins service is off", http.StatusServiceUnavailable)
		return
	}
	cfg, err := s.skins.Collect()
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"collected": len(cfg.Skins)})
}

func (s *AdminService) handleSkinsApply(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) || !onlyMethod(w, r, http.MethodPost) {
		return
	}
	if s.skins == nil {
		http.Error(w, "skins service is off", http.StatusServiceUnavailable)
		return
	}
	placed, err := s.skins.Apply()
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"applied": placed})
}

var _ = strings.TrimSpace
