package main

// СЛУЖЕБНОЕ НЕ ПОКИДАЕТ СЕРВЕР — тремя дверями сразу.
//
// Аудит 03.09.2026 нашёл и проверил живьём: файл учёток админки (соли и хэши
// паролей) отдавался статикой и числился в публичном индексе версий; роль
// редактора писала им же и кошельками через общую дверь ассетов; офлайн-
// экспорт увозил в APK кошельки, сейвы и .git. Правило «что служебное» было
// записано трижды по-разному. Теперь оно одно (privateRel), и этот файл
// держит все три двери разом — уйти из одной, оставшись в двух других,
// правило больше не может.

import (
	"archive/zip"
	"bytes"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

var privateRels = []string{
	"admin-users.json",
	"Admin-Users.json",
	"services/wallet/u_1.json",
	"services/lvn.db",
	"Services/admin-users.json",
	"state/u_1.json",
	".history/scripts/ch1.lvn/1.bak",
	".lvn-import/baseline.json",
	".git/HEAD",
	"scripts/ch1.lvn.incoming",
	"manifest.draft.json",
}

var publicRels = []string{
	"scripts/ch1.lvn",
	"bg/room.png",
	"ui/words.en.json",
}

func plantTree(t *testing.T, dir string, rels ...string) {
	t.Helper()
	for _, rel := range rels {
		p := filepath.Join(dir, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte("data:"+rel), 0o644); err != nil {
			t.Fatal(err)
		}
	}
}

func TestPrivateContentIsNotServedNorVersionedNorExported(t *testing.T) {
	dir := t.TempDir()
	plantTree(t, dir, privateRels...)
	plantTree(t, dir, publicRels...)
	plantTree(t, dir, "manifest.json")
	srv := &server{content: dir}

	// 1. Статика.
	h := srv.contentHandler(dir)
	get := func(rel string) int {
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/content/"+rel, nil))
		return rec.Code
	}
	for _, rel := range privateRels {
		if code := get(rel); code != http.StatusNotFound {
			t.Errorf("статика отдала служебный %s с кодом %d", rel, code)
		}
	}
	for _, rel := range publicRels {
		if code := get(rel); code != http.StatusOK {
			t.Errorf("статика обязана отдавать %s, а дала %d", rel, code)
		}
	}

	// 2. Индекс версий.
	versions := srv.computeVersions(true)
	for _, rel := range privateRels {
		if _, ok := versions[rel]; ok {
			t.Errorf("индекс версий выдаёт служебный %s", rel)
		}
	}
	for _, rel := range append(publicRels, "manifest.json") {
		if _, ok := versions[rel]; !ok {
			t.Errorf("индекс версий потерял %s", rel)
		}
	}

	// 3. Офлайн-экспорт.
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	srv.bundleContent(zw, "Game")
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	zr, err := zip.NewReader(bytes.NewReader(buf.Bytes()), int64(buf.Len()))
	if err != nil {
		t.Fatal(err)
	}
	names := map[string]int{}
	for _, f := range zr.File {
		names[f.Name]++
	}
	prefix := "Game/" + bundleDir + "/content/"
	for _, rel := range privateRels {
		if names[prefix+rel] > 0 {
			t.Errorf("экспорт увёз служебный %s", rel)
		}
	}
	for _, rel := range publicRels {
		if names[prefix+rel] != 1 {
			t.Errorf("экспорт обязан везти %s ровно раз, везёт %d", rel, names[prefix+rel])
		}
	}
	// Индекс версий пишется экспортом сам — и ровно один раз, а не поверх
	// скопированного с диска (дубль имени в zip).
	if n := names[prefix+"asset-versions.json"]; n != 1 {
		t.Errorf("asset-versions.json в архиве %d раз(а), должен быть один", n)
	}
}

func TestAdminAssetRefusesPrivatePaths(t *testing.T) {
	s := guardServer(t)
	for _, rel := range privateRels {
		rec, _ := putAsset(t, s, rel, "{}")
		if rec.Code != http.StatusForbidden {
			t.Errorf("PUT %s: %d, ждали 403 — редактор писал бы учётки и кошельки", rel, rec.Code)
		}
		if _, err := os.Stat(filepath.Join(s.content, filepath.FromSlash(rel))); err == nil {
			t.Errorf("PUT %s всё же записал файл", rel)
		}
		req := httptest.NewRequest(http.MethodDelete, "/v1/admin/assets/"+rel, nil)
		req.Header.Set("Authorization", "Bearer t")
		del := httptest.NewRecorder()
		s.handleAdminAsset(del, req)
		if del.Code != http.StatusForbidden {
			t.Errorf("DELETE %s: %d, ждали 403", rel, del.Code)
		}
	}
	// Правило узкое: обычная глава через ту же дверь пишется как раньше.
	rec, _ := putAsset(t, s, "scripts/ch1.lvn", validLvn)
	if rec.Code != http.StatusOK {
		t.Fatalf("PUT scripts/ch1.lvn: %d %s", rec.Code, rec.Body.String())
	}
}

func TestAdminUsersMoveOutOfThePublicRoot(t *testing.T) {
	content := t.TempDir()
	old := filepath.Join(content, adminUsersFile)
	if err := os.WriteFile(old, []byte(`[{"login":"ilya","salt":"00","hash":"00","role":"owner"}]`), 0o644); err != nil {
		t.Fatal(err)
	}
	dir := adminUsersDir(content)
	if dir != filepath.Join(content, "services") {
		t.Fatalf("учётки должны жить в services/, а не %s", dir)
	}
	if _, err := os.Stat(old); err == nil {
		t.Errorf("старый файл остался в публичном корне")
	}
	u, err := NewAdminUsers(dir)
	if err != nil {
		t.Fatal(err)
	}
	if got := len(u.List()); got != 1 {
		t.Errorf("после переезда учёток %d, ждали 1", got)
	}
	// Повторный старт ничего не ломает, а заново появившаяся копия в корне
	// (восстановили из старого бэкапа) убирается, действующая — в services/.
	if err := os.WriteFile(old, []byte(`[]`), 0o644); err != nil {
		t.Fatal(err)
	}
	adminUsersDir(content)
	if _, err := os.Stat(old); err == nil {
		t.Errorf("копия в корне пережила второй старт")
	}
	u2, _ := NewAdminUsers(dir)
	if got := len(u2.List()); got != 1 {
		t.Errorf("действующие учётки затёрты копией из корня: %d", got)
	}
}

func TestOpenStoreCreatesItsDirectory(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "fresh-content", "services")
	db, err := openStore(dir)
	if err != nil {
		t.Fatalf("на пустом каталоге контента сервер не стартует: %v", err)
	}
	db.Close()
}
