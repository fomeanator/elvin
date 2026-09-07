package main

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
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
	}, nil))

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

func TestPatchProjectSettingsPreservesUnrelatedData(t *testing.T) {
	const ref = "{fileID: 2800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}"
	const retained = "{fileID: 2800000, guid: dddddddddddddddddddddddddddddddd, type: 3}"
	for _, tc := range []struct {
		name, raw, want string
	}{
		{
			name: "icon_spacing",
			raw:  "      m_Icon: " + ref + "  \n",
			want: "      m_Icon: {fileID: 0}  \n",
		},
		{
			name: "empty_textures_at_eof",
			raw:  "    - m_Textures:\n      - " + ref,
			want: "    - m_Textures: []",
		},
		{
			name: "indented_textures",
			raw:  "  m_Textures:  \n    - " + ref + "\n  next: 1\n",
			want: "  m_Textures: []  \n  next: 1\n",
		},
		{
			name: "null_and_retained_textures",
			raw:  "    - m_Textures:\n      - " + ref + "\n      - {fileID: 0}\n      - " + retained + "\n",
			want: "    - m_Textures:\n      - {fileID: 0}\n      - " + retained + "\n",
		},
		{
			name: "retained_textures_at_eof",
			raw:  "    - m_Textures:\n      - " + retained + "\n      - " + ref,
			want: "    - m_Textures:\n      - " + retained,
		},
		{
			name: "unrelated_fields_and_lists",
			raw: "  defaultCursor: " + ref + "\n  otherAssets:\n  - " + ref +
				"\n  m_Icon: " + retained + "\n  m_Textures: []\n  nextAssets:\n  - " + ref + "\n",
		},
		{
			name: "other_reference_types",
			raw: "  m_Icon: {fileID: 4800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}\n" +
				"  m_Textures:\n  - {fileID: 2800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 2}\n",
		},
	} {
		for _, eol := range []string{"\n", "\r\n"} {
			t.Run(tc.name+"/"+fmt.Sprintf("%q", eol), func(t *testing.T) {
				raw := strings.ReplaceAll(tc.raw, "\n", eol)
				want := tc.want
				if want == "" {
					want = tc.raw
				}
				want = strings.ReplaceAll(want, "\n", eol)
				got := patchProjectSettings([]byte(raw), exportConfig{}, map[string]bool{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": true})
				if string(got) != want {
					t.Fatalf("got %q, want %q", got, want)
				}
				if unchanged := patchProjectSettings([]byte(raw), exportConfig{}, nil); string(unchanged) != raw {
					t.Fatalf("empty GUID set changed settings: %q", unchanged)
				}
			})
		}
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
			{Name: "Запасной адрес", URL: "https://alt.test"},
			{Name: "", URL: "https://second.test"},
		},
	})
	if !strings.Contains(src, `app.KnownServers = new (string, string)[] {`) {
		t.Fatalf("запасные адреса не попали в Boot.cs:\n%s", src)
	}
	// Кириллическая подпись обязана доехать целиком: sanitizeName вырезал бы
	// её под ноль, и все запасные адреса назывались бы одинаково.
	for _, want := range []string{`"https://alt.test"`, `"https://second.test"`, `"Запасной адрес"`, `"Запасной"`} {
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

// Every asset reference in exported ProjectSettings must resolve to an asset
// and its .meta in the ZIP, including references unrelated to app icons.
func TestExportProjectSettingsReferencesOnlyArchivedAssets(t *testing.T) {
	const prefix = `PlayerSettings:
  productName: Game
  companyName: Studio
  applicationIdentifier:
    Android: com.example.game
  defaultCursor: {fileID: 2800000, guid: dddddddddddddddddddddddddddddddd, type: 3}
  preloadedAssets:
  - {fileID: 4800000, guid: eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee, type: 3}
`
	const icons = `  m_BuildTargetIcons:
  - m_BuildTarget:
    m_Icons:
    - serializedVersion: 2
      m_Icon: {fileID: 2800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
      m_Width: 128
    - serializedVersion: 2
      m_Icon: {fileID: 2800000, guid: dddddddddddddddddddddddddddddddd, type: 3}
      m_Width: 64
  m_BuildTargetPlatformIcons:
  - m_BuildTarget: iPhone
    m_Icons:
    - m_Textures: []
      m_Kind: 0
  - m_BuildTarget: Android
    m_Icons:
    - m_Textures:
      - {fileID: 2800000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 3}
      - {fileID: 2800000, guid: cccccccccccccccccccccccccccccccc, type: 3}
      m_Kind: 2
    - m_Textures:
      - {fileID: 2800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
      m_Kind: 1
    - m_Textures:
      - {fileID: 2800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
      m_Kind: 0
    - m_Textures:
      - {fileID: 2800000, guid: dddddddddddddddddddddddddddddddd, type: 3}
      - {fileID: 2800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
      - {fileID: 2800000, guid: dddddddddddddddddddddddddddddddd, type: 3}
      m_Kind: 2
`
	const cleanIcons = `  m_BuildTargetIcons:
  - m_BuildTarget:
    m_Icons:
    - serializedVersion: 2
      m_Icon: {fileID: 0}
      m_Width: 128
    - serializedVersion: 2
      m_Icon: {fileID: 2800000, guid: dddddddddddddddddddddddddddddddd, type: 3}
      m_Width: 64
  m_BuildTargetPlatformIcons:
  - m_BuildTarget: iPhone
    m_Icons:
    - m_Textures: []
      m_Kind: 0
  - m_BuildTarget: Android
    m_Icons:
    - m_Textures: []
      m_Kind: 2
    - m_Textures: []
      m_Kind: 1
    - m_Textures: []
      m_Kind: 0
    - m_Textures:
      - {fileID: 2800000, guid: dddddddddddddddddddddddddddddddd, type: 3}
      - {fileID: 2800000, guid: dddddddddddddddddddddddddddddddd, type: 3}
      m_Kind: 2
`
	const suffix = "  m_BuildTargetBatching: []\n  runInBackground: 0\n"
	for _, tc := range []struct {
		name          string
		templateIcons bool
		authorIcon    bool
	}{
		{name: "author_icon", templateIcons: true, authorIcon: true},
		{name: "no_author_icon", templateIcons: true},
		{name: "no_icon_directory", authorIcon: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			tmpl, content := t.TempDir(), t.TempDir()
			put := func(root, rel, body string) {
				t.Helper()
				path := filepath.Join(root, filepath.FromSlash(rel))
				if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
					t.Fatal(err)
				}
			}
			put(tmpl, "Assets/Sandbox/Boot.cs", "// template entry point")
			for rel, guid := range map[string]string{
				"Assets/Resources/UI/cursor.png":   "dddddddddddddddddddddddddddddddd",
				"Assets/Resources/UI/theme.shader": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
			} {
				put(tmpl, rel, "retained asset")
				put(tmpl, rel+".meta", "fileFormatVersion: 2\nguid: "+guid+"\n")
			}
			settings := prefix + cleanIcons + suffix
			if tc.templateIcons {
				settings = prefix + icons + suffix
				for rel, guid := range map[string]string{
					"app-icon.png":             "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
					"app-icon-fg.png":          "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
					"alternate-background.png": "cccccccccccccccccccccccccccccccc",
				} {
					put(tmpl, "Assets/Icon/"+rel, "template icon")
					put(tmpl, "Assets/Icon/"+rel+".meta", "fileFormatVersion: 2\nguid: "+guid+"\n")
				}
				// Only immediate *.png.meta files identify excluded icons.
				put(tmpl, "Assets/Icon/notes.txt.meta", "guid: dddddddddddddddddddddddddddddddd\n")
				put(tmpl, "Assets/Icon/nested/unused.png.meta", "guid: dddddddddddddddddddddddddddddddd\n")
			}
			put(tmpl, "ProjectSettings/ProjectSettings.asset", settings)
			put(content, "art/app-icon.png", "author icon")
			body := `{"name":"Game","company":"Studio"}`
			if tc.authorIcon {
				body = `{"name":"Game","company":"Studio","icon":"art/app-icon.png"}`
			}
			s := &server{content: content, templateDir: tmpl, adminToken: "devtoken"}
			req := httptest.NewRequest(http.MethodPost, "/v1/export", strings.NewReader(body))
			req.Header.Set("Authorization", "Bearer devtoken")
			rec := httptest.NewRecorder()
			s.handleExport(rec, req)
			if rec.Code != http.StatusOK || rec.Header().Get("Content-Type") != "application/zip" {
				t.Fatalf("expected a ZIP response, got %d %s", rec.Code, rec.Body.String())
			}
			zr, err := zip.NewReader(bytes.NewReader(rec.Body.Bytes()), int64(rec.Body.Len()))
			if err != nil {
				t.Fatal(err)
			}
			files := map[string]string{}
			for _, f := range zr.File {
				r, err := f.Open()
				if err != nil {
					t.Fatal(err)
				}
				data, err := io.ReadAll(r)
				r.Close()
				if err != nil {
					t.Fatalf("read ZIP entry %s: %v", f.Name, err)
				}
				files[strings.TrimPrefix(f.Name, "Game/")] = string(data)
			}
			metaGUID := regexp.MustCompile(`(?m)^guid: ([0-9a-fA-F]{32})\r?$`)
			archivedGUIDs := map[string]bool{}
			for rel, data := range files {
				if !strings.HasSuffix(rel, ".meta") {
					continue
				}
				if _, ok := files[strings.TrimSuffix(rel, ".meta")]; !ok {
					t.Errorf("ZIP contains %s without its asset", rel)
					continue
				}
				if m := metaGUID.FindStringSubmatch(data); m != nil {
					archivedGUIDs[strings.ToLower(m[1])] = true
				}
			}
			got, ok := files["ProjectSettings/ProjectSettings.asset"]
			if !ok {
				t.Fatal("ZIP is missing ProjectSettings.asset")
			}
			for _, m := range regexp.MustCompile(`\bguid:\s*([0-9a-fA-F]{32})\b`).FindAllStringSubmatch(got, -1) {
				if !archivedGUIDs[strings.ToLower(m[1])] {
					t.Errorf("ProjectSettings references GUID %s with no asset and .meta in ZIP", m[1])
				}
			}
			if want := prefix + cleanIcons + suffix; got != want {
				t.Error("ProjectSettings must preserve all bytes except removed template icon references")
			}
			for rel, data := range files {
				if strings.HasPrefix(rel, "Assets/Icon/") &&
					(!tc.authorIcon || rel != "Assets/Icon/app-icon.png" || data != "author icon") {
					t.Errorf("unexpected icon entry in ZIP: %s", rel)
				}
			}
			if tc.authorIcon && files["Assets/Icon/app-icon.png"] != "author icon" {
				t.Error("ZIP is missing the author's icon")
			}
		})
	}
}

// Шаблон — рабочая песочница движка: покупные 3D-киты и редакторные скрипты
// для них. Уезжая в экспорт, они не просто раздували архив до полугигабайта —
// экспортированный проект из-за них НЕ КОМПИЛИРОВАЛСЯ, и сборка APK падала на
// кухне, к игре отношения не имеющей.
func TestExportShipsOnlyWhatTheGameNeeds(t *testing.T) {
	keep := []string{
		"Assets/Sandbox/Boot.cs",
		"Assets/Resources/UI/AppLoading/theme.asset",
		"Assets/Resources.meta",
		"ProjectSettings/ProjectSettings.asset",
		"Packages/manifest.json",
	}
	drop := []string{
		"Assets/Editor/PaintBlacksmithGround.cs",
		"Assets/Kenney/Editor/SetProbe.cs",
		"Assets/Proxy Games/Stylized Nature Kit Lite/Materials/Skybox.mat",
		"Assets/3DForge/FantasyExteriors/Textures/fe_vil_grass_03_DIF.png",
		"Assets/ServerSets/blacksmith.prefab",
		"Assets/Screenshots/screenshot.png",
		"Assets/Resources/Sets/forest.prefab",
		// Иконки шаблона — иконки ЕГО игры: свою кладём отдельно, после обхода.
		"Assets/Icon/app-icon-fg.png",
	}
	for _, rel := range keep {
		if !exportAssetAllowed(rel) {
			t.Errorf("%s должен ехать в экспорт", rel)
		}
	}
	for _, rel := range drop {
		if exportAssetAllowed(rel) {
			t.Errorf("%s — кухня песочницы, в экспорте ему не место", rel)
		}
	}
}

// «build» в шаблоне — собранные 3D-наборы, 198 МБ. Список исключений был
// написан с заглавной, а на macOS это та же папка: экспорт молча вёз их в
// каждый архив.
func TestExportSkipsBuildOutputWhateverTheCase(t *testing.T) {
	for _, dir := range []string{"build", "Build", "Library", "library", "Temp"} {
		if !exportSkipDirs[strings.ToLower(dir)] {
			t.Errorf("каталог %s должен исключаться из экспорта", dir)
		}
	}
}

func TestExportOnlineSeedManifest(t *testing.T) {
	tmpl, content := t.TempDir(), t.TempDir()
	plantTree(t, tmpl, "Assets/Sandbox/Boot.cs")
	plantTree(t, content, "scripts/intro.lvn", "bg/intro.png")
	manifest := `{
		"titles":[{"id":"intro","type":"intro","seasons":[{"chapters":[{
			"id":"intro-1","script_url":"/content/scripts/intro.lvn",
			"assets":{"/content/bg/intro.png":{"critical":true}}
		}]}]}]
	}`
	if err := os.WriteFile(filepath.Join(content, "manifest.json"), []byte(manifest), 0o644); err != nil {
		t.Fatal(err)
	}
	s := &server{content: content, templateDir: tmpl, adminToken: "devtoken"}
	req := httptest.NewRequest(http.MethodPost, "/v1/export", strings.NewReader(`{"name":"Game","offline":false}`))
	req.Header.Set("Authorization", "Bearer devtoken")
	rec := httptest.NewRecorder()
	s.handleExport(rec, req)
	if rec.Code != http.StatusOK || rec.Header().Get("Content-Type") != "application/zip" {
		t.Fatalf("expected a ZIP response, got %d %s", rec.Code, rec.Body.String())
	}
	zr, err := zip.NewReader(bytes.NewReader(rec.Body.Bytes()), int64(rec.Body.Len()))
	if err != nil {
		t.Fatal(err)
	}
	const base = "Game/Assets/StreamingAssets/lvn-seed/"
	files := map[string]string{}
	for _, f := range zr.File {
		if !strings.HasPrefix(f.Name, base) {
			continue
		}
		r, err := f.Open()
		if err != nil {
			t.Fatal(err)
		}
		data, err := io.ReadAll(r)
		r.Close()
		if err != nil {
			t.Fatalf("read ZIP entry %s: %v", f.Name, err)
		}
		files[strings.TrimPrefix(f.Name, base)] = string(data)
	}
	t.Run("manifest_matches_content", func(t *testing.T) {
		if got, ok := files["manifest.json"]; !ok || got != manifest {
			t.Errorf("seed root manifest = %q (present=%t), want exact content manifest", got, ok)
		}
	})
	t.Run("manifest_is_outside_content", func(t *testing.T) {
		for rel, data := range files {
			if strings.HasPrefix(rel, "content/") && (filepath.Base(rel) == "manifest.json" || data == manifest) {
				t.Errorf("manifest must not be reachable through the content seed: %s", rel)
			}
		}
	})
	t.Run("manifest_is_not_indexed", func(t *testing.T) {
		var index []string
		if err := json.Unmarshal([]byte(files["index.json"]), &index); err != nil {
			t.Fatalf("read seed index: %v", err)
		}
		indexed := map[string]bool{}
		for _, rel := range index {
			indexed[rel] = true
			if filepath.Base(rel) == "manifest.json" || files[rel] == manifest {
				t.Errorf("manifest must not enter the content seed index: %s", rel)
			}
		}
		for _, rel := range []string{"content/scripts/intro.lvn", "content/bg/intro.png"} {
			if !indexed[rel] || files[rel] == "" {
				t.Errorf("intro asset must still be bundled and indexed: %s", rel)
			}
		}
	})
}

func TestExportOfflineContent(t *testing.T) {
	for _, tc := range []struct {
		name       string
		unreadable string
	}{
		{name: "healthy"},
		{name: "unreadable_asset", unreadable: "art/required.png"},
		{name: "unreadable_manifest", unreadable: "manifest.json"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			tmpl, content := t.TempDir(), t.TempDir()
			plantTree(t, tmpl, "Assets/Sandbox/Boot.cs")
			plantTree(t, content, "manifest.json", "art/required.png")
			manifest := `{"titles":[],"ui":{"browse":{"canvas":"/content/art/required.png"}}}`
			if err := os.WriteFile(filepath.Join(content, "manifest.json"), []byte(manifest), 0o644); err != nil {
				t.Fatal(err)
			}

			// Walk uses Lstat, so a symlink to a directory reaches ReadFile
			// and fails on macOS even as root (unlike chmod 000).
			unreadable := func(rel string) {
				t.Helper()
				path := filepath.Join(content, filepath.FromSlash(rel))
				if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
					t.Fatal(err)
				}
				if err := os.Symlink(t.TempDir(), path); err != nil {
					t.Fatal(err)
				}
				if _, err := os.ReadFile(path); err == nil {
					t.Fatalf("fixture %s must fail ReadFile", rel)
				}
			}
			// These exclusions are intentional, even when unreadable.
			excluded := []string{"services/internal.json", "manifest.draft.json", "scripts/source.lvns", "art/source.psd"}
			for _, rel := range excluded {
				unreadable(rel)
			}
			if tc.unreadable != "" {
				if err := os.Remove(filepath.Join(content, filepath.FromSlash(tc.unreadable))); err != nil {
					t.Fatal(err)
				}
				if tc.unreadable == "manifest.json" {
					// The manifest API copy must also reject a directory that
					// the content walk legitimately skips.
					if err := os.Mkdir(filepath.Join(content, tc.unreadable), 0o755); err != nil {
						t.Fatal(err)
					}
				} else {
					unreadable(tc.unreadable)
				}
			}

			s := &server{content: content, templateDir: tmpl, adminToken: "devtoken"}
			req := httptest.NewRequest(http.MethodPost, "/v1/export", strings.NewReader(`{"name":"Game","offline":true}`))
			req.Header.Set("Authorization", "Bearer devtoken")
			rec := httptest.NewRecorder()
			s.handleExport(rec, req)
			if rec.Code != http.StatusOK || rec.Header().Get("Content-Type") != "application/zip" {
				t.Fatalf("expected a ZIP response, got %d %s", rec.Code, rec.Body.String())
			}
			zr, err := zip.NewReader(bytes.NewReader(rec.Body.Bytes()), int64(rec.Body.Len()))
			if tc.unreadable != "" {
				if err == nil {
					t.Fatalf("unreadable %s produced a complete ZIP: the author cannot detect missing content", tc.unreadable)
				}
				return
			}
			if err != nil {
				t.Fatalf("healthy export produced an incomplete ZIP: %v", err)
			}
			files := map[string]string{}
			for _, f := range zr.File {
				r, err := f.Open()
				if err != nil {
					t.Fatal(err)
				}
				data, err := io.ReadAll(r)
				r.Close()
				if err != nil {
					t.Fatalf("read ZIP entry %s: %v", f.Name, err)
				}
				files[f.Name] = string(data)
			}
			base := "Game/" + bundleDir
			for rel, want := range map[string]string{
				"/content/art/required.png": "data:art/required.png",
				"/content/manifest.json":    manifest,
				"/v1/content/manifest":      manifest,
			} {
				if got := files[base+rel]; got != want {
					t.Errorf("ZIP entry %s = %q, want %q", rel, got, want)
				}
			}
			if _, ok := files[base+"/content/asset-versions.json"]; !ok {
				t.Error("ZIP is missing the generated version index")
			}
			for _, rel := range excluded {
				if _, ok := files[base+"/content/"+rel]; ok {
					t.Errorf("ZIP includes excluded file %s", rel)
				}
			}
		})
	}
}

// ИГРА НЕ РАБОТАЕТ В ФОНЕ — И НА ЭТОМ ДЕРЖИТСЯ ЧЕСТНОСТЬ СРОКОВ.
//
// Выбор со сроком, автопродвижение и печать реплики меряют время КАДРОВЫМИ
// часами (LvnClock, намеренно Time.unscaledTime): нет кадров — нет и времени.
// Ровно поэтому телефонный звонок посреди развилки не стоит игроку ветки:
// свёрнутая игра кадров не получает, и срок стоит вместе с ней.
//
// Включи в проекте работу в фоне — и вся эта конструкция рассыпается молча:
// кадры идут, время идёт, а игрок разговаривает по телефону. Заметить это на
// машине разработчика нельзя, потому что редактор не сворачивают.
//
// Шаблон экспорта — тот самый проект, который уезжает автору и становится
// игрой у игрока (см. resolveTemplate).
func TestИграНеРаботаетВФоне(t *testing.T) {
	tmpl := resolveTemplate("")
	path := filepath.Join(tmpl, "ProjectSettings", "ProjectSettings.asset")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Skipf("шаблон экспорта не найден (%v) — проверять нечего", err)
	}
	re := regexp.MustCompile(`(?m)^\s*runInBackground:\s*(\d+)\s*$`)
	m := re.FindSubmatch(data)
	if m == nil {
		t.Fatalf("в %s нет строки runInBackground — молчаливое умолчание Unity"+
			" решает за нас, идёт ли время у свёрнутой игры", path)
	}
	if string(m[1]) != "0" {
		t.Fatalf("игра объявлена работающей в фоне (runInBackground: %s).\n\n"+
			"Тогда у свёрнутой игры идут кадры, а значит идёт и время: срок выбора"+
			" дотикает во время телефонного звонка и уведёт игрока по своей ветке,"+
			" а автопродвижение пролистает реплики, которых он не видел.", m[1])
	}
}
