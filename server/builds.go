package main

// СБОРКИ — готовый APK, который команда забирает сама.
//
// До этого билд ездил руками: собрал, положил в мессенджер, объяснил, какой из
// трёх файлов свежий. Теперь свежая сборка лежит у сервера, а админка знает
// ровно одну ссылку — «последняя версия», — которая всегда отдаёт её.
//
// Файлы намеренно НЕ в контент-директории: `/content/` раздаётся статикой всем
// подряд, и билд, положенный туда, оказался бы публичной ссылкой. Каталог
// лежит рядом с `uploads` — попасть в него можно только через эти маршруты, а
// значит через ворота админки.
//
// Байты приезжают тем же кусочным заливом, что и бандлы импорта
// (/v1/admin/staged-upload/<id>): APK — это сотня мегабайт, и обрыв связи на
// девяностом мегабайте не должен отправлять всё сначала. Регистрация здесь
// принимает УЖЕ залитый путь и переносит его к себе.

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

// maxBuilds — сколько сборок держим на диске. Дальше самые старые вытесняются:
// на продуктовом боксе места мало, а история версий за полгода никому не нужна
// — нужна последняя и пара предыдущих, чтобы было куда откатиться.
const maxBuilds = 20

// Расширения, которые вообще имеют смысл как «версия игры». Список закрытый
// осознанно: каталог сборок не должен превращаться в файлопомойку, куда через
// админку заливают что угодно. Слайс, а не карта: порядок перебора должен быть
// одинаковым от запуска к запуску.
var buildPlatforms = []struct{ ext, platform string }{
	{".apk", "android"},
	{".aab", "android"},
	{".ipa", "ios"},
	{".zip", "other"}, // веб-сборка или архив десктопа
}

// buildKind распознаёт вид сборки по имени файла.
//
// Имя приходит НЕ чистым: кусочный залив хранит файл под своим id, и на диске
// он называется «app-1.4.2.apk-307200» — расширение оказывается в середине.
// Поэтому сначала честный Ext (когда клиент передал исходное имя), а если он
// ничего не сказал — ищем известное расширение внутри имени.
func buildKind(name string) (ext, platform string, ok bool) {
	low := strings.ToLower(name)
	if e := filepath.Ext(low); e != "" {
		for _, p := range buildPlatforms {
			if p.ext == e {
				return p.ext, p.platform, true
			}
		}
	}
	for _, p := range buildPlatforms {
		if strings.Contains(low, p.ext) {
			return p.ext, p.platform, true
		}
	}
	return "", "", false
}

var buildIDRe = regexp.MustCompile(`^[A-Za-z0-9_.-]{1,120}$`)

// buildMeta — карточка сборки в списке админки.
type buildMeta struct {
	ID       string `json:"id"`       // и имя файла на диске
	Version  string `json:"version"`  // как её называет команда: 1.4.2, 2026-08-11
	Platform string `json:"platform"` // android | ios | other
	File     string `json:"file"`     // имя, под которым файл скачается
	Size     int64  `json:"size"`
	SHA256   string `json:"sha256"`
	Uploaded string `json:"uploaded"` // RFC3339, UTC — для показа
	// MS — то же время в миллисекундах, и порядок «свежие первыми» решает
	// именно оно: у RFC3339 секундная точность, и две сборки одной секунды
	// встали бы в списке как попало.
	MS int64 `json:"ms"`
	Notes    string `json:"notes"`    // «что нового» — попадает в список
	By       string `json:"by"`       // кто залил: логин или «ключ сборки»
}

type BuildsService struct {
	dir     string
	staging string // каталог кусочного залива — единственный источник байтов
	token   string
	mu      sync.Mutex // сериализует чтение-правку-запись index.json
}

// NewBuildsService кладёт сборки рядом с контентом, но НЕ внутрь него —
// см. шапку файла. Каталог залива считается тем же способом, что и в
// (*server).stagingDir: они обязаны совпадать, иначе регистрация не найдёт
// ни одного залитого файла.
func NewBuildsService(content, token string) *BuildsService {
	base := filepath.Dir(strings.TrimRight(content, string(filepath.Separator)))
	return &BuildsService{
		dir:     filepath.Join(base, "builds"),
		staging: filepath.Join(base, "uploads"),
		token:   token,
	}
}

func (s *BuildsService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/admin/builds", s.handleList)
	mux.HandleFunc("/v1/admin/builds/", s.handleItem)
}

func (s *BuildsService) indexPath() string { return filepath.Join(s.dir, "index.json") }

// load читает опись. Отсутствующий файл — не ошибка: это первый запуск.
func (s *BuildsService) load() []buildMeta {
	raw, err := os.ReadFile(s.indexPath())
	if err != nil {
		return nil
	}
	var doc struct {
		Builds []buildMeta `json:"builds"`
	}
	if json.Unmarshal(raw, &doc) != nil {
		log.Printf("[builds] опись не читается — начинаем с пустой")
		return nil
	}
	// Свежие первыми: именно в этом порядке список показывается, и именно
	// первый элемент отдаёт «последняя версия».
	sort.SliceStable(doc.Builds, func(i, j int) bool { return doc.Builds[i].MS > doc.Builds[j].MS })
	return doc.Builds
}

func (s *BuildsService) save(list []buildMeta) error {
	raw, err := json.MarshalIndent(map[string]any{"builds": list}, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(s.dir, 0o755); err != nil {
		return err
	}
	return atomicWrite(s.indexPath(), raw, 0o644)
}

// who — имя для колонки «кто залил». Сессия человека важнее токена: если у
// действия есть имя, в журнале должно остаться имя.
func who(r *http.Request) string {
	if adminPeople != nil {
		if sess := adminPeople.Session(r); sess != nil && sess.Login != "" {
			return sess.Login
		}
	}
	return "ключ сборки"
}

// ── GET /v1/admin/builds, POST /v1/admin/builds ─────────────────────────────

func (s *BuildsService) handleList(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.token) {
		return
	}
	switch r.Method {
	case http.MethodGet:
		list := s.load()
		var latest *buildMeta
		if len(list) > 0 {
			latest = &list[0]
		}
		writeJSON(w, http.StatusOK, map[string]any{"builds": list, "latest": latest})
	case http.MethodPost:
		s.register(w, r)
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// register переносит уже залитый файл в каталог сборок и заводит карточку.
func (s *BuildsService) register(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Path     string `json:"path"`     // что вернул staged-upload
		Filename string `json:"filename"` // исходное имя: на диске файл лежит под id залива
		Version  string `json:"version"`  //
		Platform string `json:"platform"` // необязательно: обычно ясно по расширению
		Notes    string `json:"notes"`
	}
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&body); err != nil {
		http.Error(w, "bad json: "+err.Error(), http.StatusBadRequest)
		return
	}

	// Путь принимаем ТОЛЬКО из каталога залива. Иначе «зарегистрируй сборку»
	// становится способом утащить в скачиваемое место любой файл сервера.
	src := filepath.Clean(body.Path)
	if src == "" || !strings.HasPrefix(src, s.staging+string(os.PathSeparator)) {
		http.Error(w, "файл должен быть залит через staged-upload", http.StatusForbidden)
		return
	}
	info, err := os.Stat(src)
	if err != nil || info.IsDir() {
		http.Error(w, "залитый файл не найден", http.StatusNotFound)
		return
	}

	name := body.Filename
	if name == "" {
		name = filepath.Base(src)
	}
	ext, platform, known := buildKind(name)
	if !known {
		http.Error(w, "сборкой считаются .apk, .aab, .ipa и .zip", http.StatusUnsupportedMediaType)
		return
	}
	if body.Platform != "" {
		platform = sanitizeName(body.Platform, platform)
	}
	version := sanitizeName(body.Version, time.Now().UTC().Format("2006-01-02"))
	notes := strings.TrimSpace(body.Notes)
	if len(notes) > 500 {
		notes = notes[:500]
	}

	now := time.Now()
	id := fmt.Sprintf("%s-%s-%d%s", platform, version, now.UnixMilli(), ext)
	if !buildIDRe.MatchString(id) {
		http.Error(w, "имя версии не годится в имя файла", http.StatusBadRequest)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	if err := os.MkdirAll(s.dir, 0o755); err != nil {
		http.Error(w, "каталог сборок: "+err.Error(), http.StatusInternalServerError)
		return
	}
	dst := filepath.Join(s.dir, id)
	if err := moveFile(src, dst); err != nil {
		http.Error(w, "перенос: "+err.Error(), http.StatusInternalServerError)
		return
	}
	sum, err := fileSHA256(dst)
	if err != nil {
		http.Error(w, "контрольная сумма: "+err.Error(), http.StatusInternalServerError)
		return
	}

	meta := buildMeta{
		ID:       id,
		Version:  version,
		Platform: platform,
		File:     fmt.Sprintf("%s-%s%s", platform, version, ext),
		Size:     info.Size(),
		SHA256:   sum,
		Uploaded: now.UTC().Format(time.RFC3339),
		MS:       now.UnixMilli(),
		Notes:    notes,
		By:       who(r),
	}
	list := append([]buildMeta{meta}, s.load()...)
	list = s.evict(list)
	if err := s.save(list); err != nil {
		http.Error(w, "опись: "+err.Error(), http.StatusInternalServerError)
		return
	}
	log.Printf("[builds] залита %s (%s, %.1f МБ) — %s", meta.Version, meta.Platform, float64(meta.Size)/(1<<20), meta.By)
	writeJSON(w, http.StatusOK, meta)
}

// evict удаляет с диска всё, что не влезло в maxBuilds, и возвращает
// подрезанный список.
func (s *BuildsService) evict(list []buildMeta) []buildMeta {
	if len(list) <= maxBuilds {
		return list
	}
	for _, old := range list[maxBuilds:] {
		if err := os.Remove(filepath.Join(s.dir, old.ID)); err == nil {
			log.Printf("[builds] вытеснена старая сборка %s", old.Version)
		}
	}
	return list[:maxBuilds]
}

// ── GET|DELETE /v1/admin/builds/<id>, GET /v1/admin/builds/latest ───────────

func (s *BuildsService) handleItem(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.token) {
		return
	}
	id := strings.TrimPrefix(r.URL.Path, "/v1/admin/builds/")
	if id == "" || !buildIDRe.MatchString(id) || strings.Contains(id, "..") {
		http.Error(w, "bad id", http.StatusBadRequest)
		return
	}

	list := s.load()
	// «Последняя версия» — стабильная ссылка, которую можно дать команде один
	// раз и больше не возвращаться к вопросу «а где взять свежий билд».
	if id == "latest" {
		want := r.URL.Query().Get("platform")
		for i := range list {
			if want == "" || list[i].Platform == want {
				s.serve(w, r, list[i])
				return
			}
		}
		http.Error(w, "сборок ещё нет", http.StatusNotFound)
		return
	}

	idx := -1
	for i := range list {
		if list[i].ID == id {
			idx = i
			break
		}
	}
	if idx < 0 {
		http.Error(w, "нет такой сборки", http.StatusNotFound)
		return
	}

	switch r.Method {
	case http.MethodGet, http.MethodHead:
		s.serve(w, r, list[idx])
	case http.MethodDelete:
		s.mu.Lock()
		defer s.mu.Unlock()
		if err := os.Remove(filepath.Join(s.dir, id)); err != nil && !os.IsNotExist(err) {
			http.Error(w, "удаление: "+err.Error(), http.StatusInternalServerError)
			return
		}
		if err := s.save(append(list[:idx:idx], list[idx+1:]...)); err != nil {
			http.Error(w, "опись: "+err.Error(), http.StatusInternalServerError)
			return
		}
		log.Printf("[builds] удалена сборка %s — %s", list[idx].Version, who(r))
		writeJSON(w, http.StatusOK, map[string]any{"ok": true})
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// serve отдаёт файл вложением. ServeFile, а не io.Copy: он умеет Range, и
// сорвавшаяся на телефоне закачка стомегабайтного APK продолжится с места
// обрыва, а не начнётся заново.
func (s *BuildsService) serve(w http.ResponseWriter, r *http.Request, m buildMeta) {
	path := filepath.Join(s.dir, m.ID)
	f, err := os.Open(path)
	if err != nil {
		http.Error(w, "файл сборки пропал с диска", http.StatusNotFound)
		return
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		http.Error(w, "файл сборки не читается", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Disposition", "attachment; filename=\""+m.File+"\"")
	w.Header().Set("X-Build-Version", m.Version)
	w.Header().Set("X-Build-SHA256", m.SHA256)
	http.ServeContent(w, r, m.File, info.ModTime(), f)
}

// ── мелочи ──────────────────────────────────────────────────────────────────

// moveFile переносит файл, переживая переезд между файловыми системами:
// каталог залива и каталог сборок могут оказаться на разных томах, и тогда
// Rename честно откажется.
func moveFile(src, dst string) error {
	if err := os.Rename(src, dst); err == nil {
		return nil
	}
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(dst, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		_ = os.Remove(dst)
		return err
	}
	if err := out.Close(); err != nil {
		_ = os.Remove(dst)
		return err
	}
	_ = os.Remove(src)
	return nil
}

func fileSHA256(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}
