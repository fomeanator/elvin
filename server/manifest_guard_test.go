package main

// ГЕЙТ МАНИФЕСТА НА СТОРОНЕ СЕРВЕРА.
//
// Скрипты проходили структурную проверку на записи, манифест — нет: его писали
// на диск после разбора JSON, и всё. Здесь закреплено то, что сервер обязан
// делать с находками проверки: ошибку класть в Errors, предупреждение — в
// Warnings, и НЕ путать эти два ящика. Разница не косметическая: Errors
// блокируют запись, Warnings едут в ответ и пропускают.
//
// Отдельно закреплено, какой путь считается манифестом. Тёзка в подпапке — не
// манифест, черновик рядом — не манифест; ошибка в эту сторону либо гоняет
// проверку по чужому файлу, либо пропускает настоящий.

import (
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const cleanManifest = `{"ui":{"hud":{"mode":"always","bg_color":"#101010"}},
 "titles":[{"id":"t1","name":"Полночь"}]}`

// ── РАЗБОР НАХОДОК ПО ЯЩИКАМ ─────────────────────────────────────────────────

func TestНечитаемыйМанифестДаётОшибкуАНеПредупреждение(t *testing.T) {
	s := guardServer(t)
	f := s.checkManifest([]byte(`{"ui":{"hud":`))
	if len(f.Errors) != 1 {
		t.Fatalf("нечитаемый JSON обязан дать ровно одну ошибку, получили %v", f.Errors)
	}
	if len(f.Warnings) != 0 {
		t.Fatalf("ошибку разбора нельзя дублировать предупреждением: %v", f.Warnings)
	}
	if !f.blocked() {
		t.Fatal("находка-ошибка обязана считаться блокирующей")
	}
	if !strings.Contains(f.Errors[0], "не разбирается как JSON") {
		t.Fatalf("ошибка не объясняет причину: %q", f.Errors[0])
	}
}

func TestОпечаткаВМанифестеДаётПредупреждениеАНеОшибку(t *testing.T) {
	s := guardServer(t)
	f := s.checkManifest([]byte(`{"ui":{"hud":{"bg_colour":"#101010"}}}`))
	if len(f.Errors) != 0 {
		t.Fatalf("описка в имени поля не повод блокировать запись: %v", f.Errors)
	}
	if len(f.Warnings) != 1 {
		t.Fatalf("ждали одно предупреждение, получили %v", f.Warnings)
	}
	if f.blocked() {
		t.Fatal("предупреждение не имеет права блокировать запись")
	}
	if !strings.Contains(f.Warnings[0], "ui.hud.bg_colour") {
		t.Fatalf("предупреждение не называет место: %q", f.Warnings[0])
	}
}

// Чистый манифест обязан пройти БЕЗЗВУЧНО. Гейт, который на исправном файле
// что-то бормочет, приучает пролистывать ответ, и вместе с шумом теряется
// настоящая находка.
func TestЧистыйМанифестПроходитБеззвучно(t *testing.T) {
	s := guardServer(t)
	f := s.checkManifest([]byte(cleanManifest))
	if len(f.Errors) != 0 || len(f.Warnings) != 0 {
		t.Fatalf("на исправном манифесте ждали тишину: ошибки %v, предупреждения %v", f.Errors, f.Warnings)
	}
}

func TestПустойМанифестНеРоняетГейт(t *testing.T) {
	s := guardServer(t)
	for _, body := range []string{`{}`, `null`, `{"ui":null}`} {
		f := s.checkManifest([]byte(body))
		if len(f.Errors) != 0 || len(f.Warnings) != 0 {
			t.Fatalf("на %s ждали тишину: %v / %v", body, f.Errors, f.Warnings)
		}
	}
}

// ── КАКОЙ ПУТЬ СЧИТАЕТСЯ МАНИФЕСТОМ ──────────────────────────────────────────

func TestМанифестомСчитаетсяТолькоФайлВКорнеКонтента(t *testing.T) {
	yes := []string{"manifest.json"}
	no := []string{
		"ui/manifest.json",       // тёзка в подпапке — чужой файл
		"manifest.draft.json",    // черновик автора рядом с настоящим
		"content/manifest.json",  // не корень контента
		"manifest.json.bak",      // снимок истории
		"manifest",               // без расширения
		"scripts/manifest.json",  // тёзка у скриптов
		"titles/x/manifest.json", // тёзка у новеллы
	}
	for _, p := range yes {
		if !isManifestPath(p) {
			t.Errorf("%q обязан считаться манифестом — иначе главный файл проекта пойдёт мимо гейта", p)
		}
	}
	for _, p := range no {
		if isManifestPath(p) {
			t.Errorf("%q манифестом не является — проверка погонит по чужому файлу схему манифеста", p)
		}
	}
}

// Разделитель пути приводится к прямой косой: путь с обратными слэшами не
// должен внезапно переставать быть манифестом.
func TestПутьМанифестаНеЗависитОтРазделителя(t *testing.T) {
	if !isManifestPath(filepath.FromSlash("manifest.json")) {
		t.Fatal("манифест перестал узнаваться после приведения разделителя")
	}
}

// ── ЖИВАЯ ЗАПИСЬ ─────────────────────────────────────────────────────────────

// ПРЕДУПРЕЖДЕНИЕ НЕ БЛОКИРУЕТ. Хост вправе класть в манифест своё, схема у нас
// снятая и неполная — отказывать на неполном знании нельзя. Но и молчать
// нельзя: находка обязана доехать до автора в ответе.
func TestМанифестСОпечаткойПишетсяНоПредупреждаетВОтвете(t *testing.T) {
	s := guardServer(t)
	rec, out := putAsset(t, s, "manifest.json", `{"ui":{"hud":{"bg_colour":"#101010"}}}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("предупреждение не имеет права блокировать запись, получили %d: %s", rec.Code, rec.Body.String())
	}
	if _, err := os.Stat(filepath.Join(s.content, "manifest.json")); err != nil {
		t.Fatalf("манифест с предупреждением обязан лечь на диск: %v", err)
	}
	w := strList(out["warnings"])
	if len(w) == 0 {
		t.Fatal("находка не доехала до автора: в ответе нет предупреждений")
	}
	if !strings.Contains(strings.Join(w, "; "), "ui.hud.bg_colour") {
		t.Fatalf("предупреждение в ответе не называет место: %v", w)
	}
}

func TestЧистыйМанифестПишетсяБезПредупреждений(t *testing.T) {
	s := guardServer(t)
	rec, out := putAsset(t, s, "manifest.json", cleanManifest)
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d: %s", rec.Code, rec.Body.String())
	}
	if w := strList(out["warnings"]); len(w) != 0 {
		t.Fatalf("исправный манифест не должен нести предупреждений: %v", w)
	}
}

// НЕЧИТАЕМЫЙ МАНИФЕСТ ДО ДИСКА НЕ ДОХОДИТ, и прежняя копия остаётся целой.
// content/ отдаётся игроку без кэша — испорченный манифест был бы живым в ту же
// секунду и означал бы «приложение не открывается».
func TestНечитаемыйМанифестНеЗатираетПрежнююКопию(t *testing.T) {
	s := guardServer(t)
	if rec, _ := putAsset(t, s, "manifest.json", cleanManifest); rec.Code != http.StatusOK {
		t.Fatalf("подготовка: чистый манифест не записался (%d)", rec.Code)
	}
	before, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		t.Fatal(err)
	}
	rec, _ := putAsset(t, s, "manifest.json", `{"ui":{"hud":`)
	if rec.Code == http.StatusOK {
		t.Fatalf("нечитаемый манифест приняли: %s", rec.Body.String())
	}
	after, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		t.Fatalf("прежняя копия исчезла: %v", err)
	}
	if string(before) != string(after) {
		t.Fatal("отклонённая запись изменила файл на диске — прежняя версия должна оставаться нетронутой")
	}
}

// Гейт манифеста не должен трогать чужие файлы: JSON рядом (например,
// ext-grammar.json или свой конфиг хоста) пишется без разбора по схеме
// манифеста и без её предупреждений.
func TestТёзкаВПодпапкеПроверкуМанифестаНеПолучает(t *testing.T) {
	s := guardServer(t)
	rec, out := putAsset(t, s, "ui/manifest.json", `{"чепуха":{"и":"ещё"}}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("чужой JSON заблокировали как манифест: %d %s", rec.Code, rec.Body.String())
	}
	if w := strList(out["warnings"]); len(w) != 0 {
		t.Fatalf("чужой файл получил предупреждения гейта манифеста: %v", w)
	}
}
