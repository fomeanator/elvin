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
	      "canvas_options": [{"id": "hall", "title": "Зал", "url": "/bg/hall.jpg", "spine": "/spine/hall/", "price": 120, "currency": "crystals", "rarity": "rare"}],
	      "avatars": [{"id": "free1", "url": "/art/a.png"}, {"id": "vip", "url": "/art/v.png", "price": 50, "currency": "crystals"}]
	    }
	  },
	  "sprites": {
	    "hero": {"wardrobe": {"outfit": {"items": [
	      {"value": "__none__", "name": "Снять"},
	      {"value": "orchid", "name": "Орхидея", "icon": "/sprites/orchid.png", "price": 60, "currency": "crystals", "rarity": "rare"},
	      {"value": "gift", "name": "Подарочный", "icon": "/sprites/gift.png", "gacha": true}
	    ]}}}
	  }
	}`
	gacha := `{"spin_currency": "crystals", "spin_price": 50,
	  "sectors": [{"id": "super", "kind": "super", "weight": 1}],
	  "prizes": [{"sku": "wardrobe:hero:outfit:gift", "label": "Подарочный", "art": "/sprites/gift.png", "rarity": "mythical"}],
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
	if g := by["wardrobe:hero:outfit:gift"]; !g.Gacha || g.Rarity != "mythical" {
		t.Fatalf("приз барабана не помечен «в крутке» со ступенью из барабана: %+v", g)
	}
	if o := by["wardrobe:hero:outfit:orchid"]; o.Price != 60 || o.Rarity != "rare" || o.Kind != "wardrobe" || !o.Buy {
		t.Fatalf("наряд собран неверно (с ценой и без флага — продаётся): %+v", o)
	}
	if g := by["wardrobe:hero:outfit:gift"]; g.Buy {
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
		case "wardrobe:hero:outfit:orchid":
			cfg.Skins[i].Price = 99
			cfg.Skins[i].Gacha = true
			cfg.Skins[i].Description = "Шёлк и орхидеи"

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
	items := skinList(skinDig(m, "sprites", "hero", "wardrobe", "outfit"), "items")
	orchid := items[1].(map[string]any)
	if skinStr(orchid, "description") != "Шёлк и орхидеи" {
		t.Fatalf("описание не дошло до наряда: %+v", orchid)
	}
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
	if skinStr(hall, "spine") != "/spine/hall/" {
		t.Fatalf("живой фон (spine) не пережил сбор и раскладку: %+v", hall)
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

	// Новые записи из каталога заводятся в манифесте; скрытое не идёт в барабан;
	// порядок расставляет списки; цена копии — своя.
	cfg.Skins = append(cfg.Skins,
		skin{SKU: "avatar.newbie", Kind: "avatar", Name: "Новичок", Art: "/art/n.png", Order: 1},
		skin{SKU: "wardrobe:menu:backdrop:lake", Kind: "backdrop", Name: "Озеро", Art: "/bg/lake.jpg", Gacha: true, Hidden: true},
		skin{SKU: "wardrobe:hero:outfit:cape", Kind: "wardrobe", Name: "Плащ", Art: "/sprites/cape.png", Price: 10, Buy: true, Order: 1, SellPrice: 4})
	data, _ = json.MarshalIndent(cfg, "", "  ")
	must(os.WriteFile(filepath.Join(dir, skinsFile), data, 0o644))
	_, err = svc.Apply()
	must(err)
	m, err = readJSONMap(filepath.Join(dir, "manifest.json"))
	must(err)
	avatars := skinList(skinDig(m, "ui", "browse"), "avatars")
	if len(avatars) != 3 || skinStr(avatars[0].(map[string]any), "id") != "newbie" {
		t.Fatalf("новая аватарка не заведена или порядок не сработал: %+v", avatars)
	}
	if opts := skinList(skinDig(m, "ui", "browse"), "canvas_options"); len(opts) != 2 || !skinBool(opts[1].(map[string]any), "hidden") {
		t.Fatalf("новый фон не заведён или не скрыт: %+v", opts)
	}
	outfits := skinList(skinDig(m, "sprites", "hero", "wardrobe", "outfit"), "items")
	if len(outfits) != 4 || skinStr(outfits[0].(map[string]any), "value") != "cape" || skinNum(outfits[0].(map[string]any), "sell_price") != 4 {
		t.Fatalf("новый наряд не заведён первым с ценой копии: %+v", outfits)
	}
	g, err = readGacha(filepath.Join(dir, "gacha.json"))
	must(err)
	for _, p := range g.Prizes {
		if p.SKU == "wardrobe:menu:backdrop:lake" {
			t.Fatal("скрытый скин попал в барабан")
		}
	}
	if len(g.Cases) != 1 || g.Cases[0].ID != defaultCaseID || len(g.Cases[0].Prizes) != len(g.Prizes) {
		t.Fatalf("набор по умолчанию должен совпасть с верхним уровнем: %+v", g.Cases)
	}

	// ВТОРОЙ НАБОР: назван в каталоге, приз назначен ему — барабан получает
	// два набора с разными призами и ценой; верхний уровень = первый набор.
	cfg.Cases = []gachaCase{{ID: "base", Name: "Обычный"}, {ID: "vip", Name: "Золотой", Price: 200}}
	for i := range cfg.Skins {
		if cfg.Skins[i].SKU == "wardrobe:hero:outfit:orchid" {
			cfg.Skins[i].Cases = []string{"vip"}
		}
	}
	data, _ = json.MarshalIndent(cfg, "", "  ")
	must(os.WriteFile(filepath.Join(dir, skinsFile), data, 0o644))
	_, err = svc.Apply()
	must(err)
	g, err = readGacha(filepath.Join(dir, "gacha.json"))
	must(err)
	if len(g.Cases) != 2 || g.Cases[1].Price != 200 || len(g.Cases[1].Prizes) != 1 || g.Cases[1].Prizes[0].SKU != "wardrobe:hero:outfit:orchid" {
		t.Fatalf("наборы разложены неверно: %+v", g.Cases)
	}
	for _, p := range g.Cases[0].Prizes {
		if p.SKU == "wardrobe:hero:outfit:orchid" {
			t.Fatal("приз набора vip не должен быть в базовом")
		}
	}
	if len(g.Prizes) != len(g.Cases[0].Prizes) {
		t.Fatal("верхний уровень — призы первого набора")
	}
	cfg3, err := svc.Collect()
	must(err)
	if len(cfg3.Cases) != 2 {
		t.Fatalf("сбор поверх правленого потерял наборы: %+v", cfg3.Cases)
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
		if sk.SKU == "wardrobe:hero:outfit:orchid" && sk.Price != 99 {
			t.Fatalf("сбор затёр правку цены: %+v", sk)
		}
	}
	if len(cfg2.Skins) != 8 {
		t.Fatalf("после повторного сбора %d скинов, ожидалось 8", len(cfg2.Skins))
	}
}

// ВАЛЮТЫ — ОДНО МЕСТО (TR-117): облик из ui.currency_look и картинка из карты
// значков шапки собираются в каталог, правка раскладывается в обе стороны —
// и в облик, и в карту шапки (старые сборки читают только её).
func TestSkinsCurrenciesRoundTrip(t *testing.T) {
	manifest := map[string]any{}
	if err := json.Unmarshal([]byte(`{"ui": {
	  "currency_look": {"crystals": {"name": "Кристаллы", "unit": "кристаллов", "icon": "Gem", "color": "#f0c860"}},
	  "browse": {"currency_icons": {"crystals": "/ui/crystal.png", "energy": "/ui/watch.png"}}
	}}`), &manifest); err != nil {
		t.Fatal(err)
	}
	got := collectSkins(manifest, gachaConfig{}, skinsConfig{})
	if got.Currencies["crystals"].Image != "/ui/crystal.png" || got.Currencies["crystals"].Name != "Кристаллы" {
		t.Fatalf("кристаллы собраны не целиком: %+v", got.Currencies["crystals"])
	}
	if got.Currencies["energy"].Image != "/ui/watch.png" {
		t.Fatalf("энергия без облика, но с картинкой — должна быть в каталоге: %+v", got.Currencies)
	}
	// Правка: новая картинка кристаллов и снятая у энергии.
	got.Currencies["crystals"] = currencyLook{Name: "Кристаллы", Unit: "кристаллов", Icon: "Gem", Color: "#f0c860", Image: "/ui/crystal-2.png"}
	got.Currencies["energy"] = currencyLook{Name: "Энергия", Icon: "Energy"}
	applySkins(got, manifest, &gachaConfig{})
	look := skinDig(manifest, "ui", "currency_look", "crystals")
	icons := skinDig(manifest, "ui", "browse", "currency_icons")
	if skinStr(look, "image") != "/ui/crystal-2.png" || skinStr(icons, "crystals") != "/ui/crystal-2.png" {
		t.Fatalf("новая картинка не дошла до манифеста: облик %+v, шапка %+v", look, icons)
	}
	if _, still := icons["energy"]; still {
		t.Fatalf("снятая картинка энергии осталась в шапке: %+v", icons)
	}
	if skinStr(skinDig(manifest, "ui", "currency_look", "energy"), "name") != "Энергия" {
		t.Fatal("облик энергии не записан")
	}
}
