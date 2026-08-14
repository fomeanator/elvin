package main

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Экспорт с вшитым движком: исходники пакетов лежат В архиве, а манифест
// ссылается на них по-соседски. Иначе скачанный проект тянет движок из зеркал,
// а зеркала обновляются только по релизному тегу — и сборка молча едет на
// старом коде.
func TestExportBundlesEngineLocally(t *testing.T) {
	root := t.TempDir()
	// Дерево как в репозитории: <root>/unity/Packages/… и <root>/sandbox/…
	pkg := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime")
	if err := os.MkdirAll(pkg, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(pkg, "LvnPlayer.cs"), []byte("// движок"), 0o644); err != nil {
		t.Fatal(err)
	}
	tmpl := filepath.Join(root, "sandbox")
	if err := os.MkdirAll(filepath.Join(tmpl, "Packages"), 0o755); err != nil {
		t.Fatal(err)
	}
	manifest := `{"dependencies":{"com.lvn.engine":"file:../../unity/Packages/com.lvn.engine"}}`
	if err := os.WriteFile(filepath.Join(tmpl, "Packages", "manifest.json"), []byte(manifest), 0o644); err != nil {
		t.Fatal(err)
	}

	deps := engineDeps(tmpl)
	if len(deps) != 1 || deps["com.lvn.engine"] == "" {
		t.Fatalf("зависимости движка не найдены: %v", deps)
	}

	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	if err := copyEnginePackages(zw, "TimeRomance", deps); err != nil {
		t.Fatal(err)
	}
	zw.Close()

	zr, err := zip.NewReader(bytes.NewReader(buf.Bytes()), int64(buf.Len()))
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, f := range zr.File {
		// Путь обязан лежать ВНУТРИ папки проекта — снаружи Unity его не увидит.
		if f.Name == "TimeRomance/Packages/com.lvn.engine/Runtime/LvnPlayer.cs" {
			found = true
		}
	}
	if !found {
		names := []string{}
		for _, f := range zr.File {
			names = append(names, f.Name)
		}
		t.Errorf("исходники движка не попали в архив: %v", names)
	}

	// Манифест обязан указывать на соседний каталог, а не на путь песочницы:
	// в архиве нет ../../unity, и Unity молча не разрешит зависимость.
	out := localizeManifest([]byte(manifest))
	var m struct {
		Deps map[string]string `json:"dependencies"`
	}
	if err := json.Unmarshal(out, &m); err != nil {
		t.Fatalf("манифест сломан: %v — %s", err, out)
	}
	if m.Deps["com.lvn.engine"] != "file:com.lvn.engine" {
		t.Errorf("путь не локализован: %q", m.Deps["com.lvn.engine"])
	}
	if strings.Contains(string(out), "github.com") {
		t.Error("в локальном экспорте не должно быть ссылок на зеркала")
	}
}

// Проверка на НАСТОЯЩЕЙ песочнице репозитория: вычисленные пути должны
// существовать. Тест, который проходит только на выдуманном дереве, ловит
// ошибку в себе, а не в коде.
func TestEngineDepsResolveInRealSandbox(t *testing.T) {
	tmpl := filepath.Join("..", "sandbox")
	if _, err := os.Stat(filepath.Join(tmpl, "Packages", "manifest.json")); err != nil {
		t.Skip("песочницы нет рядом — нечего проверять")
	}
	deps := engineDeps(tmpl)
	if len(deps) == 0 {
		t.Fatal("в песочнице не найдено ни одного пакета движка")
	}
	for name, dir := range deps {
		if _, err := os.Stat(filepath.Join(dir, "package.json")); err != nil {
			t.Errorf("%s: путь %s не ведёт к пакету (%v)", name, dir, err)
		}
	}
}
