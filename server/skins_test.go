package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// ОДНО МЕСТО НАСТРОЙКИ СКИНОВ (TR-114): каталог собирается из манифеста и
// барабана без потерь, правка в каталоге раскладывается обратно, повторное
// применение ничего не меняет, а сбор поверх правленого каталога правки не
// затирает.
func TestSkinsCollectApplyRoundTrip(t *testing.T) {
	dir := t.TempDir()
	manifest := `{
	  "ui": {
	    "wardrobe": {"rarity_colors": {"rare": "#4b69ff"}},
	    "browse": {
	      "canvas_options": [{"id": "hall", "title": "Зал", "url": "/bg/hall.jpg", "price": 120, "currency": "crystals", "rarity": "rare"}],
	      "avatars": [{"id": "free1", "url": "/art/a.png"}, {"id": "vip", "url": "/art/v.png", "price": 50, "currency": "crystals"}]
	    }
	  },
	  "sprites": {
	    "victoria": {"wardrobe": {"outfit": {"items": [
	      {"value": "__none__", "name": "Снять"},
	      {"value": "orchid", "name": "Орхидея", "icon": "/sprites/orchid.png", "price": 60, "currency": "crystals", "rarity": "rare"},
	      {"value": "gift", "name": "Подарочный", "icon": "/sprites/gift.png", "gacha": true}
	    ]}}}
	  }
	}`
	gacha := `{"spin_currency": "crystals", "spin_price": 50,
	  "sectors": [{"id": "super", "kind": "super", "weight": 1}],
	  "prizes": [{"sku": "wardrobe:victoria:outfit:gift", "label": "Подарочный", "art": "/sprites/gift.png", "rarity": "mythical"}],
	  "rarity_weights": {"rare": 16, "mythical": 10}}`
	must := func(err error) {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
	}
	must(os.WriteFile(filepath.Join(dir, "manifest.json"), []byte(manifest), 0o644))
	must(os.WriteFile(filepath.Join(dir, "gacha.json"), []byte(gacha), 0o644))
	svc := NewSkinsService(dir)

	cfg, err := svc.Collect()
	must(err)
	if len(cfg.Skins) != 5 {
		t.Fatalf("собрано %d скинов, ожидалось 5 (2 наряда без «снять», 1 фон, 2 аватарки): %+v", len(cfg.Skins), cfg.Skins)
	}
	by := map[string]skin{}
	for _, sk := range cfg.Skins {
		by[sk.SKU] = sk
	}
	if g := by["wardrobe:victoria:outfit:gift"]; !g.Gacha || g.Rarity != "mythical" {
		t.Fatalf("приз барабана не помечен «в крутке» со ступенью из барабана: %+v", g)
	}
	if o := by["wardrobe:victoria:outfit:orchid"]; o.Price != 60 || o.Rarity != "rare" || o.Kind != "wardrobe" || !o.Buy {
		t.Fatalf("наряд собран неверно (с ценой и без флага — продаётся): %+v", o)
	}
	if g := by["wardrobe:victoria:outfit:gift"]; g.Buy {
		t.Fatalf("приз с флагом gacha в манифесте не продаётся: %+v", g)
	}
	if b := by["wardrobe:menu:backdrop:hall"]; b.Kind != "backdrop" || b.Name != "Зал" || b.Price != 120 {
		t.Fatalf("фон собран неверно: %+v", b)
	}
	if a := by["avatar.vip"]; a.Kind != "avatar" || a.Price != 50 {
		t.Fatalf("аватарка собрана неверно: %+v", a)
	}
	if cfg.RarityColors["rare"] != "#4b69ff" || cfg.RarityWeights["rare"] != 16 {
		t.Fatalf("палитра и веса ступеней не перенесены: %+v %+v", cfg.RarityColors, cfg.RarityWeights)
	}

	// Правка в каталоге: орхидея дорожает и идёт в крутку, фон — необычный.
	for i := range cfg.Skins {
		switch cfg.Skins[i].SKU {
		case "wardrobe:victoria:outfit:orchid":
			cfg.Skins[i].Price = 99
			cfg.Skins[i].Gacha = true
		case "wardrobe:menu:backdrop:hall":
			cfg.Skins[i].Rarity = "uncommon"
		}
	}
	cfg.RarityColors["uncommon"] = "#5e98d9"
	data, _ := json.MarshalIndent(cfg, "", "  ")
	must(os.WriteFile(filepath.Join(dir, skinsFile), data, 0o644))

	placed, err := svc.Apply()
	must(err)
	if placed != 5 {
		t.Fatalf("применено к %d записям манифеста, ожидалось 5", placed)
	}
	m, err := readJSONMap(filepath.Join(dir, "manifest.json"))
	must(err)
	items := skinList(skinDig(m, "sprites", "victoria", "wardrobe", "outfit"), "items")
	orchid := items[1].(map[string]any)
	if skinNum(orchid, "price") != 99 || skinBool(orchid, "gacha") {
		t.Fatalf("продаваемый наряд и в крутке: цена 99, а флаг gacha (только из крутки) стоять не должен: %+v", orchid)
	}
	gift := items[2].(map[string]any)
	if !skinBool(gift, "gacha") || gift["price"] != nil {
		t.Fatalf("приз без цены: флаг gacha есть, цены нет: %+v", gift)
	}
	if _, has := items[0].(map[string]any)["rarity"]; has {
		t.Fatalf("пустая ступень не должна дописываться: %+v", items[0])
	}
	hall := skinList(skinDig(m, "ui", "browse"), "canvas_options")[0].(map[string]any)
	if skinStr(hall, "rarity") != "uncommon" {
		t.Fatalf("правка не дошла до фона: %+v", hall)
	}
	colors := skinDig(m, "ui", "wardrobe", "rarity_colors")
	if skinStr(colors, "uncommon") != "#5e98d9" {
		t.Fatalf("палитра ступеней не разложилась: %+v", colors)
	}
	g, err := readGacha(filepath.Join(dir, "gacha.json"))
	must(err)
	if len(g.Prizes) != 2 || g.Sectors[0].ID != "super" || g.Price != 50 {
		t.Fatalf("барабан: призов %d (ждали 2), сектора и цена должны остаться: %+v", len(g.Prizes), g)
	}

	// Повторное применение — без изменений на диске.
	before, _ := os.ReadFile(filepath.Join(dir, "manifest.json"))
	_, err = svc.Apply()
	must(err)
	after, _ := os.ReadFile(filepath.Join(dir, "manifest.json"))
	if string(before) != string(after) {
		t.Fatal("повторное применение изменило манифест")
	}

	// Сбор поверх правленого: правки целы, новое из манифеста добавляется.
	cfg2, err := svc.Collect()
	must(err)
	for _, sk := range cfg2.Skins {
		if sk.SKU == "wardrobe:victoria:outfit:orchid" && sk.Price != 99 {
			t.Fatalf("сбор затёр правку цены: %+v", sk)
		}
	}
	if len(cfg2.Skins) != 5 {
		t.Fatalf("после повторного сбора %d скинов, ожидалось 5", len(cfg2.Skins))
	}
}
