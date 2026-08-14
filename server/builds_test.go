package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func newBuildsTestService(t *testing.T) (*BuildsService, string) {
	t.Helper()
	dir := t.TempDir()
	content := filepath.Join(dir, "content")
	if err := os.MkdirAll(content, 0o755); err != nil {
		t.Fatal(err)
	}
	svc := NewBuildsService(content, "sekret")
	if err := os.MkdirAll(svc.staging, 0o755); err != nil {
		t.Fatal(err)
	}
	return svc, svc.staging
}

// stage кладёт файл так, как его оставил бы кусочный залив.
func stage(t *testing.T, dir, name string, body []byte) string {
	t.Helper()
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, body, 0o644); err != nil {
		t.Fatal(err)
	}
	return path
}

func registerBuild(svc *BuildsService, doc map[string]string) *httptest.ResponseRecorder {
	raw, _ := json.Marshal(doc)
	req := httptest.NewRequest(http.MethodPost, "/v1/admin/builds", bytes.NewReader(raw))
	req.Header.Set("Authorization", "Bearer sekret")
	w := httptest.NewRecorder()
	svc.handleList(w, req)
	return w
}

func listBuilds(t *testing.T, svc *BuildsService) (builds []buildMeta, latest *buildMeta) {
	t.Helper()
	req := httptest.NewRequest(http.MethodGet, "/v1/admin/builds", nil)
	req.Header.Set("Authorization", "Bearer sekret")
	w := httptest.NewRecorder()
	svc.handleList(w, req)
	if w.Code != http.StatusOK {
		t.Fatalf("список сборок: %d %s", w.Code, w.Body.String())
	}
	var body struct {
		Builds []buildMeta `json:"builds"`
		Latest *buildMeta  `json:"latest"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &body); err != nil {
		t.Fatalf("разбор ответа %q: %v", w.Body.String(), err)
	}
	return body.Builds, body.Latest
}

func fetchBuild(svc *BuildsService, path string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(http.MethodGet, path, nil)
	req.Header.Set("Authorization", "Bearer sekret")
	w := httptest.NewRecorder()
	svc.handleItem(w, req)
	return w
}

func TestBuildRegisterAndDownload(t *testing.T) {
	svc, staging := newBuildsTestService(t)
	apk := []byte("PK\x03\x04 это как бы апк")
	src := stage(t, staging, "app-release.apk", apk)

	w := registerBuild(svc, map[string]string{"path": src, "version": "1.4.2", "notes": "гардероб чинили"})
	if w.Code != http.StatusOK {
		t.Fatalf("регистрация: %d %s", w.Code, w.Body.String())
	}

	builds, latest := listBuilds(t, svc)
	if len(builds) != 1 || latest == nil {
		t.Fatalf("ожидалась одна сборка, получили %d (latest=%v)", len(builds), latest)
	}
	got := builds[0]
	if got.Version != "1.4.2" || got.Platform != "android" {
		t.Errorf("версия/платформа разъехались: %+v", got)
	}
	if got.Notes != "гардероб чинили" {
		t.Errorf("заметка потерялась: %q", got.Notes)
	}
	sum := sha256.Sum256(apk)
	if got.SHA256 != hex.EncodeToString(sum[:]) {
		t.Errorf("контрольная сумма не сходится: %s", got.SHA256)
	}
	if _, err := os.Stat(src); !os.IsNotExist(err) {
		t.Error("залитый файл должен быть перенесён, а не скопирован")
	}

	// Скачивание по id и по стабильной ссылке «последняя версия» — один файл.
	for _, path := range []string{"/v1/admin/builds/" + got.ID, "/v1/admin/builds/latest"} {
		w := fetchBuild(svc, path)
		if w.Code != http.StatusOK {
			t.Fatalf("%s: %d %s", path, w.Code, w.Body.String())
		}
		if !bytes.Equal(w.Body.Bytes(), apk) {
			t.Errorf("%s: отдались не те байты", path)
		}
		if cd := w.Header().Get("Content-Disposition"); !strings.Contains(cd, got.File) {
			t.Errorf("%s: имя файла при скачивании: %q", path, cd)
		}
	}
}

// Свежая сборка обязана вытеснять предыдущую из «последней версии»: ради этой
// ссылки всё и заводилось.
func TestBuildLatestIsTheNewest(t *testing.T) {
	svc, staging := newBuildsTestService(t)
	for _, v := range []string{"1.0.0", "1.1.0"} {
		src := stage(t, staging, "app-"+v+".apk", []byte("сборка "+v))
		if w := registerBuild(svc, map[string]string{"path": src, "version": v}); w.Code != http.StatusOK {
			t.Fatalf("регистрация %s: %d %s", v, w.Code, w.Body.String())
		}
	}
	builds, latest := listBuilds(t, svc)
	if latest == nil || latest.Version != "1.1.0" {
		t.Fatalf("последней должна быть 1.1.0, получили %+v", latest)
	}
	// Порядок держится на метке времени в миллисекундах: с нулями сортировка
	// молча выродилась бы в «как лежало в файле».
	for _, b := range builds {
		if b.MS == 0 {
			t.Fatalf("у сборки %s нет метки времени", b.Version)
		}
	}
	if body := fetchBuild(svc, "/v1/admin/builds/latest").Body.String(); body != "сборка 1.1.0" {
		t.Errorf("ссылка «последняя версия» отдала %q", body)
	}
	// Платформа сужает выбор, а не отдаёт первое попавшееся.
	if w := fetchBuild(svc, "/v1/admin/builds/latest?platform=ios"); w.Code != http.StatusNotFound {
		t.Errorf("ios-сборок нет — ожидали 404, получили %d", w.Code)
	}
}

// Регистрация принимает байты ТОЛЬКО из каталога залива. Иначе она становится
// способом скачать через админку любой файл сервера.
func TestBuildRejectsPathOutsideStaging(t *testing.T) {
	svc, _ := newBuildsTestService(t)
	secret := filepath.Join(t.TempDir(), "секрет.apk")
	if err := os.WriteFile(secret, []byte("не для скачивания"), 0o644); err != nil {
		t.Fatal(err)
	}
	w := registerBuild(svc, map[string]string{"path": secret, "version": "1.0"})
	if w.Code != http.StatusForbidden {
		t.Fatalf("чужой путь должен отвергаться, получили %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(secret); err != nil {
		t.Error("файл за пределами залива трогать нельзя")
	}
}

// Кусочный залив хранит файл под своим id — «app-1.4.2.apk-307200». Именно
// такое имя и приходит в регистрацию из панели, и именно на нём фича сначала
// не работала: расширение оказалось в середине строки.
func TestBuildAcceptsStagedIDName(t *testing.T) {
	svc, staging := newBuildsTestService(t)
	src := stage(t, staging, "app-1.4.2.apk-307200", []byte("сборка"))
	if w := registerBuild(svc, map[string]string{"path": src, "version": "1.4.2"}); w.Code != http.StatusOK {
		t.Fatalf("staged-имя должно распознаваться: %d %s", w.Code, w.Body.String())
	}
	builds, _ := listBuilds(t, svc)
	if len(builds) != 1 || builds[0].Platform != "android" {
		t.Fatalf("платформа не распозналась: %+v", builds)
	}
	// Исходное имя от клиента важнее догадки по staged-id.
	src2 := stage(t, staging, "какой-то-файл-99", []byte("вторая"))
	w := registerBuild(svc, map[string]string{"path": src2, "filename": "game.aab", "version": "1.5"})
	if w.Code != http.StatusOK {
		t.Fatalf("filename должен решать: %d %s", w.Code, w.Body.String())
	}
	if builds, _ := listBuilds(t, svc); !strings.HasSuffix(builds[0].File, ".aab") {
		t.Errorf("расширение взято не из filename: %+v", builds[0])
	}
}

func TestBuildRejectsForeignExtension(t *testing.T) {
	svc, staging := newBuildsTestService(t)
	src := stage(t, staging, "заметки.txt", []byte("не сборка"))
	if w := registerBuild(svc, map[string]string{"path": src, "version": "1.0"}); w.Code != http.StatusUnsupportedMediaType {
		t.Fatalf("ожидали 415, получили %d %s", w.Code, w.Body.String())
	}
}

func TestBuildDeleteRemovesFileAndRow(t *testing.T) {
	svc, staging := newBuildsTestService(t)
	src := stage(t, staging, "app.apk", []byte("сборка"))
	registerBuild(svc, map[string]string{"path": src, "version": "2.0"})
	builds, _ := listBuilds(t, svc)
	id := builds[0].ID

	req := httptest.NewRequest(http.MethodDelete, "/v1/admin/builds/"+id, nil)
	req.Header.Set("Authorization", "Bearer sekret")
	w := httptest.NewRecorder()
	svc.handleItem(w, req)
	if w.Code != http.StatusOK {
		t.Fatalf("удаление: %d %s", w.Code, w.Body.String())
	}
	if builds, latest := listBuilds(t, svc); len(builds) != 0 || latest != nil {
		t.Errorf("после удаления список должен быть пуст: %+v %+v", builds, latest)
	}
	if _, err := os.Stat(filepath.Join(svc.dir, id)); !os.IsNotExist(err) {
		t.Error("файл сборки остался на диске")
	}
}

// Каталог не должен расти без предела: старые сборки вытесняются с диска.
func TestBuildEvictsBeyondCeiling(t *testing.T) {
	svc, staging := newBuildsTestService(t)
	var first string
	for i := 0; i < maxBuilds+3; i++ {
		name := "app-" + string(rune('a'+i)) + ".apk"
		src := stage(t, staging, name, []byte(name))
		w := registerBuild(svc, map[string]string{"path": src, "version": "0." + string(rune('a'+i))})
		if w.Code != http.StatusOK {
			t.Fatalf("регистрация %s: %d %s", name, w.Code, w.Body.String())
		}
		if i == 0 {
			var m buildMeta
			_ = json.Unmarshal(w.Body.Bytes(), &m)
			first = m.ID
		}
	}
	builds, _ := listBuilds(t, svc)
	if len(builds) != maxBuilds {
		t.Fatalf("держим %d сборок вместо %d", len(builds), maxBuilds)
	}
	if _, err := os.Stat(filepath.Join(svc.dir, first)); !os.IsNotExist(err) {
		t.Error("вытесненная сборка осталась на диске")
	}
}

// Без токена и без сессии админки сборки не отдаются: APK — это продукт до
// релиза, а не публичная ссылка.
func TestBuildsNeedAuth(t *testing.T) {
	svc, staging := newBuildsTestService(t)
	src := stage(t, staging, "app.apk", []byte("сборка"))
	registerBuild(svc, map[string]string{"path": src, "version": "1.0"})

	w := httptest.NewRecorder()
	svc.handleList(w, httptest.NewRequest(http.MethodGet, "/v1/admin/builds", nil))
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("список без токена: ожидали 401, получили %d", w.Code)
	}
	w = httptest.NewRecorder()
	svc.handleItem(w, httptest.NewRequest(http.MethodGet, "/v1/admin/builds/latest", nil))
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("скачивание без токена: ожидали 401, получили %d", w.Code)
	}
}

// Русское название версии не должно молча превращаться в пустоту: раньше
// «сборка для беты» вырезалась целиком, версия подставлялась датой, и автор
// узнавал об этом, только разглядывая список.
func TestBuildVersionKeepsCyrillic(t *testing.T) {
	cases := []struct{ in, want string }{
		{"вечерняя сборка", "вечерняя сборка"},
		{"2026-08-14-вечер", "2026-08-14-вечер"},
		{"../../etc/passwd", ".etcpasswd"},
		{"с:двоеточием", "сдвоеточием"},
		{"   ", "по-умолчанию"},
	}
	for _, c := range cases {
		if got := safeLabel(c.in, "по-умолчанию", 64); got != c.want {
			t.Errorf("%q → %q, ожидалось %q", c.in, got, c.want)
		}
	}
	// Длина ограничена по РУНАМ, а не байтам: обрезка по байтам разрубила бы
	// кириллическую букву пополам и дала бы битую строку.
	long := strings.Repeat("я", 100)
	if got := safeLabel(long, "x", 10); len([]rune(got)) != 10 {
		t.Errorf("обрезка по рунам: получено %d рун", len([]rune(got)))
	}
}

// Имя ДЛЯ ЧЕЛОВЕКА и имя ФАЙЛА — разные вещи. Первое может быть на любом
// языке, второе уезжает в путь на диске и в URL. Раньше их путали, и русская
// версия упиралась в отказ, хотя автор не сделал ничего плохого.
func TestBuildIdSlugSurvivesCyrillic(t *testing.T) {
	cases := []struct{ in, want string }{
		{"текстуры и пружина", "tekstury-i-pruzhina"},
		{"1.4.2", "1.4.2"},
		{"Сборка №5 (бета)", "sborka-5-beta"},
		{"   ", "запас"},
		{"ёлка", "elka"},
	}
	for _, c := range cases {
		got := asciiSlug(c.in, "запас")
		if got != c.want {
			t.Errorf("%q → %q, ожидалось %q", c.in, got, c.want)
		}
	}
	// Главное свойство: результат обязан годиться в имя файла.
	for _, s := range []string{"текстуры и пружина", "Сборка №5", "a/b\\c:d", "..\\..\\etc"} {
		id := "android-" + asciiSlug(s, "x") + "-1.apk"
		if !buildIDRe.MatchString(id) {
			t.Errorf("из %q вышло имя, которое не годится в файл: %q", s, id)
		}
	}
}
