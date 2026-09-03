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
	if err := copyEnginePackages(zw, "HostGame", deps); err != nil {
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
		if f.Name == "HostGame/Packages/com.lvn.engine/Runtime/LvnPlayer.cs" {
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

// СИД НЕСЁТ ПЕРВЫЙ КАДР. Вуаль запуска держится, пока не приедет интерфейсный
// арт и полотно витрины — на холодном первом запуске это единственное, чего
// игрок ждёт по-настоящему. В сиде его не было: APK, несущий главу целиком,
// всё равно шёл в сеть за рамкой реплики и фоном меню.
func TestSeedCarriesTheFirstFrameArt(t *testing.T) {
	content := t.TempDir()
	put := func(rel string, body string) {
		p := filepath.Join(content, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	put("ui/menu-canvas.jpg", "полотно")
	put("ui/menu-canvas@1k.jpg", "нижняя ступень полотна")
	put("bg/крит@1k.jpg", "нижняя ступень фона")
	put("bg/крит@1k.ktx2", "нижняя ступень фона для видеокарты")
	put("bg/крит@1440.jpg", "средняя ступень — не везём")
	put("ui/icons/back.png", "значок")
	put("ui/menu-canvas@1k.jpg", "ступень качества") // производная — не везём
	put("ui/loading/ch1.jpg", "фон загрузки главы")  // не первый кадр
	put("ui/words.en.json", "слова")                 // не картинка
	put("scripts/agency-ch0.lvn", `{"script":[]}`)
	put("bg/крит.jpg", "критичный фон главы")
	put("manifest.json", `{
		"ui": {"browse": {"canvas": "/content/ui/menu-canvas.jpg"}},
		"titles": [{"id":"agency","type":"intro","seasons":[{"chapters":[
			{"id":"agency-ch0","script_url":"/content/scripts/agency-ch0.lvn",
			 "assets":{"/content/bg/крит.jpg":{"critical":true},
			           "/content/bg/потом.jpg":{"critical":false}}}
		]}]}]
	}`)

	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	s := &server{content: content}
	s.bundleIntroSeed(zw, "проект")
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}

	zr, err := zip.NewReader(bytes.NewReader(buf.Bytes()), int64(buf.Len()))
	if err != nil {
		t.Fatal(err)
	}
	const base = "проект/Assets/StreamingAssets/lvn-seed/"
	got := map[string]bool{}
	var index []string
	for _, f := range zr.File {
		rel := strings.TrimPrefix(f.Name, base)
		got[rel] = true
		if rel == "index.json" {
			rc, _ := f.Open()
			var raw bytes.Buffer
			_, _ = raw.ReadFrom(rc)
			rc.Close()
			if err := json.Unmarshal(raw.Bytes(), &index); err != nil {
				t.Fatalf("опись сида не разобралась: %v", err)
			}
		}
	}

	for _, want := range []string{
		"content/ui/menu-canvas@1k.jpg", // полотно, которого ждёт вуаль — нижней ступенью
		"content/ui/icons/back.png",
		"content/bg/крит@1k.jpg",
		"content/bg/крит@1k.ktx2",
		"content/scripts/agency-ch0.lvn",
	} {
		if !got[want] {
			t.Errorf("%s не доехал в сид — на первом запуске за ним пойдут в сеть", want)
		}
	}
	for _, notWant := range []string{
		"content/ui/menu-canvas.jpg", // оригинал: у него есть ступень @1k
		"content/ui/loading/ch1.jpg", // фон загрузки — глава, а не первый кадр
		"content/ui/words.en.json",   // не картинка
		"content/bg/потом.jpg",       // не критичный
		"content/bg/крит.jpg",        // оригинал: у него есть ступень @1k
		"content/bg/крит@1440.jpg",   // средняя ступень в APK не окупается
	} {
		if got[notWant] {
			t.Errorf("%s уехал в сид — APK толстеет без пользы", notWant)
		}
	}
	// Опись — то, по чему клиент решает «есть в сиде» без слепых запросов
	// внутрь APK: файл, которого в ней нет, из APK не достанут никогда.
	inIndex := map[string]bool{}
	for _, e := range index {
		inIndex[e] = true
	}
	for rel := range got {
		if rel == "index.json" {
			continue
		}
		if !inIndex[rel] {
			t.Errorf("%s лежит в APK, но не назван в описи — клиент его не найдёт", rel)
		}
	}
}

// КАКИЕ ФАЙЛЫ ВЕЗТИ РАДИ ОДНОГО АДРЕСА. Манифест называет оригинал, а
// устройство просит ступень: сперва код для видеокарты, не вышло — уменьшенную
// картинку. На живом каталоге прода сид вёз 8,2 МБ героя, а телефон качал его
// же с сервера за 560 КБ — расширения не совпали. Десять мегабайт балласта.
func TestSeedShipsTheLowerRungNotTheOriginal(t *testing.T) {
	content := t.TempDir()
	put := func(rel string) {
		p := filepath.Join(content, filepath.FromSlash(rel))
		_ = os.MkdirAll(filepath.Dir(p), 0o755)
		if err := os.WriteFile(p, []byte(rel), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	put("art/герой.png")
	put("art/герой@1k.png")
	put("art/герой@1k.ktx2")
	put("art/герой@1440.png")
	put("audio/тема.ogg") // у звука ступеней не бывает
	put("ui/значок.png")  // мелочь без ступеней

	got := seedRungs(content, "art/герой.png")
	want := []string{"art/герой@1k.ktx2", "art/герой@1k.png"}
	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Errorf("для героя везём %v, а нужны обе нижние ступени %v — "+
			"иначе APK потолстеет на оригинал, которого никто не спросит", got, want)
	}
	for _, rel := range []string{"audio/тема.ogg", "ui/значок.png"} {
		if got := seedRungs(content, rel); len(got) != 1 || got[0] != rel {
			t.Errorf("%s: ступеней нет, надо везти оригинал, а везём %v", rel, got)
		}
	}
	// Файла нет вовсе — отвечаем им же: класть в архив всё равно нечего, и
	// тихо пропасть здесь значило бы недосчитаться его и в описи.
	if got := seedRungs(content, "нет/такого.png"); len(got) != 1 || got[0] != "нет/такого.png" {
		t.Errorf("пропавший файл: %v", got)
	}
}
