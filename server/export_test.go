package main

import (
	"bytes"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

const sampleManifest = `{
  "dependencies": {
    "com.coplaydev.unity-mcp": "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main",
    "com.lvn.engine": "file:../../unity/Packages/com.lvn.engine",
    "com.lvn.engine.shell": "file:../../unity/Packages/com.lvn.engine.shell",
    "com.unity.nuget.newtonsoft-json": "3.2.1"
  }
}`

func enginePkgURL(name string) string {
	return mirrorRepoURL(name)
}

// Each package resolves from its own read-only mirror repo, not the monorepo.
func TestMirrorRepoURL(t *testing.T) {
	cases := map[string]string{
		"com.lvn.engine":              "https://github.com/fomeanator/lvn-engine.git",
		"com.lvn.engine.shell":        "https://github.com/fomeanator/lvn-engine-shell.git",
		"com.lvn.engine.addressables": "https://github.com/fomeanator/lvn-engine-addressables.git",
	}
	for name, want := range cases {
		if got := mirrorRepoURL(name); got != want {
			t.Fatalf("mirrorRepoURL(%s) = %s, want %s", name, got, want)
		}
	}
}

// An exported manifest pins the engine to the release tag: updates are the
// project owner's explicit choice, not a side effect of our next push.
func TestPatchManifestPinsTheReleaseTag(t *testing.T) {
	out := string(patchManifest([]byte(sampleManifest), "v0.5.0"))
	want := `"com.lvn.engine": "` + enginePkgURL("com.lvn.engine") + `#v0.5.0"`
	if !strings.Contains(out, want) {
		t.Fatalf("engine not pinned:\n%s", out)
	}
	// every engine-family package rides the same repo + tag
	wantShell := `"com.lvn.engine.shell": "` + enginePkgURL("com.lvn.engine.shell") + `#v0.5.0"`
	if !strings.Contains(out, wantShell) {
		t.Fatalf("shell package not pinned:\n%s", out)
	}
	if strings.Contains(out, "unity-mcp") {
		t.Fatal("dev-only unity-mcp package must be stripped from exports")
	}
}

// Without a resolvable tag the URL stays unpinned (dev fallback) — but still
// valid JSON with the dev package stripped.
func TestPatchManifestUnpinnedFallback(t *testing.T) {
	out := string(patchManifest([]byte(sampleManifest), ""))
	if !strings.Contains(out, `"com.lvn.engine": "`+enginePkgURL("com.lvn.engine")+`"`) {
		t.Fatalf("unpinned URL malformed:\n%s", out)
	}
	if strings.Contains(out, "#") {
		t.Fatalf("no tag requested, but got a pin:\n%s", out)
	}
}

// The tag is the version of the engine package the template's file: entry
// points at — hermetic fixture, mirrors the sandbox layout.
func TestEngineReleaseTagDerivation(t *testing.T) {
	tmpl := t.TempDir()
	if err := os.MkdirAll(filepath.Join(tmpl, "Packages"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(tmpl, "engine"), 0o755); err != nil {
		t.Fatal(err)
	}
	must := func(p, s string) {
		if err := os.WriteFile(p, []byte(s), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	must(filepath.Join(tmpl, "Packages", "manifest.json"),
		`{"dependencies":{"com.lvn.engine":"file:../engine"}}`)
	must(filepath.Join(tmpl, "engine", "package.json"), `{"version":"1.2.3"}`)

	if got := engineReleaseTag(tmpl); got != "v1.2.3" {
		t.Fatalf("engineReleaseTag = %q, want v1.2.3", got)
	}
	if got := engineReleaseTag(t.TempDir()); got != "" {
		t.Fatalf("no manifest must mean no pin, got %q", got)
	}
}

// Against the REAL local template (gitignored — skipped in CI): the release
// process tags every published version as vX.Y.Z.
func TestEngineReleaseTagFromSandboxTemplate(t *testing.T) {
	if _, err := os.Stat(filepath.Join("..", "sandbox", "Packages", "manifest.json")); err != nil {
		t.Skip("local sandbox template not present")
	}
	tag := engineReleaseTag("../sandbox")
	if !regexp.MustCompile(`^v\d+\.\d+\.\d+`).MatchString(tag) {
		t.Fatalf("engineReleaseTag(../sandbox) = %q, want vX.Y.Z", tag)
	}
}

// Сервер стоит за обратным прокси, поэтому r.TLS пустой всегда, и наивная
// догадка по нему вшивала в экспортированный проект "http://". Цена отложенная
// и максимальная: Android с 9-й версии запрещает открытый HTTP, то есть
// собранный APK не грузил бы контент вообще, и узналось бы это на телефоне.
func TestExportPinsHttpsBehindAProxy(t *testing.T) {
	req := httptest.NewRequest(http.MethodPost, "/v1/export", strings.NewReader(`{"name":"G"}`))
	req.Host = "studio.example"
	req.Header.Set("X-Forwarded-Proto", "https")
	if got := requestBase(req); got != "https://studio.example" {
		t.Fatalf("адрес для вшивания = %q, ожидался https://studio.example", got)
	}
	// И наоборот: без заголовка (прямое соединение по http) остаётся http —
	// иначе локальная разработка на :8000 сломалась бы.
	plain := httptest.NewRequest(http.MethodPost, "/v1/export", nil)
	plain.Host = "localhost:8000"
	if got := requestBase(plain); got != "http://localhost:8000" {
		t.Errorf("локальный адрес = %q, ожидался http://localhost:8000", got)
	}
}

// bundleId не работал ВОВСЕ: замена одной строки-заголовка оставляла старые
// Standalone:/Android: на месте, YAML получал дубли ключей, Unity брал
// последний. Экспорт при этом отвечал 200, а APK собирался с идентификатором
// песочницы — и мог встать на телефоне поверх другого приложения.
func TestExportBundleIdReplacesTheWholeBlockIncludingAndroid(t *testing.T) {
	raw := []byte("PlayerSettings:\n" +
		"  productName: Sandbox\n" +
		"  companyName: Dev\n" +
		"  applicationIdentifier:\n" +
		"    Standalone: com.old.sandbox\n" +
		"    Android: com.old.sandbox\n" +
		"  defaultCursor: {fileID: 0}\n")
	got := string(patchProjectSettings(raw, exportConfig{
		Name: "MyGame", Company: "Me", BundleID: "com.me.mygame",
	}))

	if strings.Count(got, "com.old.sandbox") != 0 {
		t.Errorf("старый идентификатор остался:\n%s", got)
	}
	for _, want := range []string{"Android: com.me.mygame", "Standalone: com.me.mygame"} {
		if !strings.Contains(got, want) {
			t.Errorf("нет %q:\n%s", want, got)
		}
	}
	if strings.Count(got, "Android:") != 1 || strings.Count(got, "Standalone:") != 1 {
		t.Errorf("дубли ключей платформ:\n%s", got)
	}
	// Соседние настройки не должны пострадать от замены блока.
	if !strings.Contains(got, "defaultCursor: {fileID: 0}") {
		t.Errorf("замена блока съела следующую настройку:\n%s", got)
	}
	if !strings.Contains(got, "productName: MyGame") {
		t.Errorf("имя продукта не подставлено:\n%s", got)
	}
}

// Экспортированная игра обязана опрашивать контент раз в пять секунд: правка в
// студии должна доезжать до собранного приложения без пересборки.
func TestExportedBootUsesFiveSecondLiveSync(t *testing.T) {
	src := bootSource(exportConfig{ServerURL: "https://example.test"})
	if !strings.Contains(src, "app.SyncInterval = 5f;") {
		t.Fatalf("online export must poll content within five seconds:\n%s", src)
	}
}

// Запасные адреса — единственная защита установленной сборки от «имя сервера
// перестало работать»: движок гоняет их с основным наперегонки по /healthz.
// До этого поля их некуда было прописать, и падение одного имени превращало
// APK у всей команды в кирпич.
func TestExportedBootCarriesAlternateServers(t *testing.T) {
	src := bootSource(exportConfig{
		ServerURL: "https://main.test",
		AltServers: []altServer{
			{Name: "Запасной", URL: "https://alt.test"},
			{Name: "", URL: "https://second.test"},
		},
	})
	if !strings.Contains(src, `app.KnownServers = new (string, string)[] {`) {
		t.Fatalf("запасные адреса не попали в Boot.cs:\n%s", src)
	}
	for _, want := range []string{`"https://alt.test"`, `"https://second.test"`, `"Запасной"`} {
		if !strings.Contains(src, want) {
			t.Errorf("в Boot.cs нет %s:\n%s", want, src)
		}
	}
}

// Пустой список не должен оставлять в файле висящую строку присваивания.
func TestExportedBootSkipsEmptyAlternates(t *testing.T) {
	src := bootSource(exportConfig{ServerURL: "https://main.test"})
	if strings.Contains(src, "KnownServers") {
		t.Fatalf("без запасных адресов присваивания быть не должно:\n%s", src)
	}
	// Дубль основного адреса — не запасной вариант, а лишняя проба на старте.
	src = bootSource(exportConfig{ServerURL: "https://main.test", AltServers: []altServer{{URL: "https://main.test"}}})
	if strings.Contains(src, "KnownServers") {
		t.Fatalf("дубль основного адреса не должен попадать в список:\n%s", src)
	}
}

// Строки из запроса едут в исходный код — кавычка в имени не должна закрывать
// литерал и дописывать в Boot.cs что угодно.
func TestExportedBootEscapesAlternateServers(t *testing.T) {
	src := bootSource(exportConfig{
		ServerURL:  "https://main.test",
		AltServers: []altServer{{Name: `злой", Inject()`, URL: `https://evil.test/"+Inject()+"`}},
	})
	if strings.Contains(src, `Inject()+"`) || strings.Contains(src, `злой", Inject()`) {
		t.Fatalf("инъекция доехала до Boot.cs:\n%s", src)
	}
}

// Иконка автора приезжает из контента в то место, где её ждёт AppIcon.
func TestExportIconComesFromContent(t *testing.T) {
	dir := t.TempDir()
	if err := os.MkdirAll(filepath.Join(dir, "art"), 0o755); err != nil {
		t.Fatal(err)
	}
	png := []byte("\x89PNG\r\n\x1a\n не настоящий, но и не нужен")
	if err := os.WriteFile(filepath.Join(dir, "art", "cover.png"), png, 0o644); err != nil {
		t.Fatal(err)
	}
	s := &server{content: dir}

	got, ok := s.exportIcon("art/cover.png")
	if !ok || !bytes.Equal(got, png) {
		t.Fatalf("иконка не прочиталась: ok=%v", ok)
	}
	// Панель отдаёт пути с префиксом /content/ — он не должен ломать чтение.
	if _, ok := s.exportIcon("/content/art/cover.png"); !ok {
		t.Error("путь с префиксом /content/ должен работать")
	}
}

// Путь приходит из запроса: выйти за пределы контент-директории он не должен,
// иначе экспорт становится способом вынести с сервера любой файл.
func TestExportIconRefusesEscapesAndNonImages(t *testing.T) {
	dir := t.TempDir()
	secret := filepath.Join(filepath.Dir(dir), "секрет.png")
	if err := os.WriteFile(secret, []byte("не отдавать"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "notes.txt"), []byte("не картинка"), 0o644); err != nil {
		t.Fatal(err)
	}
	s := &server{content: dir}

	if _, ok := s.exportIcon("../" + filepath.Base(secret)); ok {
		t.Error("выход за пределы контента должен отвергаться")
	}
	if _, ok := s.exportIcon("notes.txt"); ok {
		t.Error("не-картинка иконкой быть не может")
	}
	if _, ok := s.exportIcon(""); ok {
		t.Error("пустой путь — просто нет иконки")
	}
}
