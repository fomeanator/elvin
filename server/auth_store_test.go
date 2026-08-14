package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// Перенос из users.json — самое рискованное место: если он потеряет игрока,
// человек лишится аккаунта, кошелька и сейвов разом.
func TestAuthImportsUsersFileOnce(t *testing.T) {
	dir := t.TempDir()
	old := map[string]*authUser{
		"u_первый": {
			DeviceHash: "dev1", TokenHash: "tok1", Created: "2026-08-01T00:00:00Z",
			Name:      "Аня",
			Providers: map[string]string{"google": "g-123"},
			Attr:      &playerAttribution{Source: "telegram", Campaign: "aug"},
		},
		"u_второй": {DeviceHash: "dev2", TokenHash: "tok2", Created: "2026-08-02T00:00:00Z"},
	}
	data, _ := json.MarshalIndent(old, "", "  ")
	if err := os.WriteFile(filepath.Join(dir, "users.json"), data, 0o600); err != nil {
		t.Fatal(err)
	}

	db, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	svc, err := NewAuthServiceDB(dir, db)
	if err != nil {
		t.Fatal(err)
	}

	if len(svc.users) != 2 {
		t.Fatalf("перенесено %d игроков вместо двух", len(svc.users))
	}
	u := svc.users["u_первый"]
	if u == nil || u.Name != "Аня" || u.TokenHash != "tok1" {
		t.Fatalf("игрок перенесён неполно: %+v", u)
	}
	if u.Providers["google"] != "g-123" {
		t.Errorf("внешняя идентичность потеряна: %+v", u.Providers)
	}
	if u.Attr == nil || u.Attr.Campaign != "aug" {
		t.Errorf("канал привлечения потерян: %+v", u.Attr)
	}
	// Индексы восстановлены: без них вход по устройству и по Google перестанет
	// находить существующий аккаунт и заведёт новый.
	if svc.byDev["dev1"] != "u_первый" || svc.byProv["google:g-123"] != "u_первый" {
		t.Errorf("индексы не восстановлены: byDev=%v byProv=%v", svc.byDev, svc.byProv)
	}
	// Исходник сохранён под другим именем: пока новый путь не проверен на
	// живых данных, это единственная копия аккаунтов.
	if _, err := os.Stat(filepath.Join(dir, "users.json.imported")); err != nil {
		t.Errorf("исходный users.json не сохранён: %v", err)
	}
	if _, err := os.Stat(filepath.Join(dir, "users.json")); err == nil {
		t.Error("users.json остался на месте — следующий старт импортирует повторно")
	}
	db.Close()

	// Повторный старт не должен ни терять, ни задваивать.
	db2, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer db2.Close()
	again, err := NewAuthServiceDB(dir, db2)
	if err != nil {
		t.Fatal(err)
	}
	if len(again.users) != 2 {
		t.Errorf("после перезапуска %d игроков", len(again.users))
	}
}

// Запись идёт ПОСТРОЧНО: это и было целью — раньше весь файл переписывался
// ради одного изменённого поля.
func TestAuthSavesSingleUser(t *testing.T) {
	dir := t.TempDir()
	db, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	svc, err := NewAuthServiceDB(dir, db)
	if err != nil {
		t.Fatal(err)
	}
	svc.mu.Lock()
	svc.users["u1"] = &authUser{Created: "2026-08-14T00:00:00Z", Name: "первый"}
	svc.users["u2"] = &authUser{Created: "2026-08-14T00:00:00Z", Name: "второй"}
	if err := svc.saveUserLocked("u1"); err != nil {
		svc.mu.Unlock()
		t.Fatal(err)
	}
	svc.mu.Unlock()

	var n int
	if err := db.QueryRow(`SELECT count(*) FROM users`).Scan(&n); err != nil {
		t.Fatal(err)
	}
	if n != 1 {
		t.Errorf("записан должен быть только один игрок, записано %d", n)
	}

	// Смена идентичностей переписывает их целиком, не накапливая мусор.
	svc.mu.Lock()
	svc.users["u1"].Providers = map[string]string{"google": "g-1"}
	_ = svc.saveUserLocked("u1")
	svc.users["u1"].Providers = map[string]string{"apple": "a-1"}
	_ = svc.saveUserLocked("u1")
	svc.mu.Unlock()
	var provs int
	if err := db.QueryRow(`SELECT count(*) FROM user_providers WHERE user_id='u1'`).Scan(&provs); err != nil {
		t.Fatal(err)
	}
	if provs != 1 {
		t.Errorf("осталась старая связь: %d вместо одной", provs)
	}
}

// Атрибуция должна пережить перезапуск и через базу — это половина условия
// приёмки ELVIN-37.
func TestAuthAttributionSurvivesThroughDB(t *testing.T) {
	dir := t.TempDir()
	db, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	svc, err := NewAuthServiceDB(dir, db)
	if err != nil {
		t.Fatal(err)
	}
	svc.mu.Lock()
	svc.users["u1"] = &authUser{Created: "2026-08-14T00:00:00Z"}
	svc.mu.Unlock()
	if _, ok := svc.SetAttributionFirstTouch("u1", parseAttribution("?utm_source=vk&utm_campaign=осень")); !ok {
		t.Fatal("атрибуция не записалась")
	}
	db.Close()

	db2, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer db2.Close()
	again, err := NewAuthServiceDB(dir, db2)
	if err != nil {
		t.Fatal(err)
	}
	if got := again.AttributionOf("u1").Channel(); got != "vk/осень" {
		t.Errorf("канал после перезапуска %q", got)
	}
}
