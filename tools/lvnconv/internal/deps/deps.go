// Package deps — пакетная система .lvns: манифест, lock-файл, vendor.
//
// Решения, принятые явно (см. .agents/chat.md, запись [claude] 2026-07-31):
//
//   - СКАЧИВАНИЕ — ЭТО ТУЛИНГ, НЕ ЯЗЫК. Сеть трогает только `lvnconv deps …`;
//     `lvnconv convert` никогда не ходит в сеть и собирается оффлайн из
//     vendor-каталога lvns_packages/. Include в компиляторе лишь умеет путь
//     "@scope/pkg/file.lvns" (см. include.go) — ни кэшей, ни резолвером
//     версий там нет, поэтому цена фичи для рантаймов — ноль.
//
//   - ВОСПРОИЗВОДИМОСТЬ. lvns.lock хранит SHA-256 тарболла, коммит (из
//     pax-заголовка codeload) и SHA-256 каждого файла. Одинаковый lock →
//     байт-в-байт одинаковый vendor. Расхождение хэша — жёсткая ошибка,
//     а не предупреждение: молчаливое «обновилось само» и есть supply chain.
//
//   - НИКАКИХ MUTABLE-ССЫЛОК. github:owner/repo@main запрещён на входе:
//     ветка движется, значит сборка недетерминирована. Тег или полный SHA.
//     (Тег технически можно передвинуть — это ловит хэш тарболла в lock.)
//
//   - file:-ссылки — РЕЖИМ РАЗРАБОТКИ. Пакет по соседству, без сети и без
//     пиннинга (hash пишется, но обновляется каждым sync). В прод-lock их
//     пускать не стоит; `deps list` помечает их как mutable.
//
//   - path traversal: имена из тарболла с "..", абсолютные и не-обычные
//     файлы (симлинки и т.п.) — ошибка целиком, а не «пропустим подозрительное».
//
//   - Циклы зависимостей — ошибка с цепочкой имён, как у include.
package deps

import (
	"archive/tar"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

const (
	ManifestName = "lvns.package.json"
	LockName     = "lvns.lock"
	VendorDir    = "lvns_packages"

	maxTarball = 64 << 20 // жёсткий предел на пакет: контент, не дистрибутив игры
	maxFiles   = 4096
)

// CodeloadBase переопределяется тестами: боевое значение — GitHub codeload.
var CodeloadBase = "https://codeload.github.com"

// Manifest — lvns.package.json. Один формат на оба употребления:
// у ПАКЕТА заполнены name/version/exports, у ИГРЫ-потребителя — dependencies
// (и content_dir, куда deps sync копирует ассеты пакетов).
type Manifest struct {
	Name         string            `json:"name,omitempty"`
	Version      string            `json:"version,omitempty"`
	License      string            `json:"license,omitempty"`
	MinEngine    string            `json:"min_engine,omitempty"`
	Exports      []string          `json:"exports,omitempty"`
	ContentDir   string            `json:"content_dir,omitempty"`
	Dependencies map[string]string `json:"dependencies,omitempty"`
}

// LockEntry — зафиксированное состояние одного пакета.
type LockEntry struct {
	Ref    string            `json:"ref"`
	Commit string            `json:"commit,omitempty"`
	SHA256 string            `json:"sha256,omitempty"` // тарболл; для file: пусто
	Files  map[string]string `json:"files"`            // rel-путь → sha256 содержимого
}

// Lock — lvns.lock целиком.
type Lock struct {
	Packages map[string]LockEntry `json:"packages"`
}

// reName — имя пакета: @scope/pkg. Скоуп обязателен: без него имя пакета
// неотличимо от обычного файла в include, и "@" в начале — и есть признак.
var reName = regexp.MustCompile(`^@[a-z0-9][a-z0-9._-]*/[a-z0-9][a-z0-9._-]*$`)

// reGithub — github:owner/repo@ref[#subdir].
var reGithub = regexp.MustCompile(`^github:([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)@([^#]+)(?:#(.+))?$`)

var reHexSHA = regexp.MustCompile(`^[0-9a-f]{40}$`)

// mutableRefs — то, что двигается. Запрещено по спеке: «никогда не разрешать
// mutable main без lock-файла» — мы строже: не разрешаем вовсе.
var mutableRefs = map[string]bool{"main": true, "master": true, "HEAD": true, "trunk": true, "develop": true}

func ReadManifest(dir string) (*Manifest, error) {
	raw, err := os.ReadFile(filepath.Join(dir, ManifestName))
	if err != nil {
		return nil, err
	}
	var m Manifest
	if err := json.Unmarshal(raw, &m); err != nil {
		return nil, fmt.Errorf("%s: %w", ManifestName, err)
	}
	return &m, nil
}

func readLock(root string) (*Lock, error) {
	raw, err := os.ReadFile(filepath.Join(root, LockName))
	if os.IsNotExist(err) {
		return &Lock{Packages: map[string]LockEntry{}}, nil
	}
	if err != nil {
		return nil, err
	}
	var l Lock
	if err := json.Unmarshal(raw, &l); err != nil {
		return nil, fmt.Errorf("%s: %w", LockName, err)
	}
	if l.Packages == nil {
		l.Packages = map[string]LockEntry{}
	}
	return &l, nil
}

// writeLock пишет lock детерминированно: MarshalIndent сортирует ключи карт,
// перевод строки в конце — чтобы дифф не трогал последнюю строку вечно.
func writeLock(root string, l *Lock) error {
	raw, err := json.MarshalIndent(l, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(root, LockName), append(raw, '\n'), 0o644)
}

// Sync приводит lvns_packages/ в соответствие манифесту и lock-файлу.
// update=false: lock — истина, сеть только если vendor не совпал (и хэш
// обязан сойтись). update=true: манифест — истина, lock переписывается.
func Sync(root string, update bool) error {
	m, err := ReadManifest(root)
	if err != nil {
		return fmt.Errorf("проект без %s: %w", ManifestName, err)
	}
	lock, err := readLock(root)
	if err != nil {
		return err
	}
	seen := map[string]bool{}
	names := sortedKeys(m.Dependencies)
	for _, name := range names {
		if err := ensure(root, m, lock, name, m.Dependencies[name], update, nil, seen); err != nil {
			return err
		}
	}
	// Пакеты, пропавшие из манифеста (и не пришедшие транзитивно), уходят и из lock.
	for name := range lock.Packages {
		if !seen[name] {
			delete(lock.Packages, name)
			os.RemoveAll(filepath.Join(root, VendorDir, filepath.FromSlash(name)))
		}
	}
	return writeLock(root, lock)
}

// ensure — один пакет: скачать/проверить/развендорить + транзитивные зависимости.
// chain — цепочка имён для сообщения о цикле.
func ensure(root string, project *Manifest, lock *Lock, name, ref string, update bool, chain []string, seen map[string]bool) error {
	for _, c := range chain {
		if c == name {
			return fmt.Errorf("цикл зависимостей: %s", strings.Join(append(chain, name), " -> "))
		}
	}
	if seen[name] {
		return nil // ромб: два пакета зависят от одного — норма
	}
	if !reName.MatchString(name) {
		return fmt.Errorf("%s: имя пакета должно быть вида @scope/pkg", name)
	}
	seen[name] = true

	var entry LockEntry
	var err error
	switch {
	case strings.HasPrefix(ref, "file:"):
		entry, err = ensureLocal(root, name, ref)
	case strings.HasPrefix(ref, "github:"):
		entry, err = ensureGithub(root, lock, name, ref, update)
	default:
		return fmt.Errorf("%s: непонятная ссылка %q (жду github:owner/repo@tag или file:path)", name, ref)
	}
	if err != nil {
		return fmt.Errorf("%s: %w", name, err)
	}
	lock.Packages[name] = entry

	// Транзитивные зависимости — из манифеста самого пакета.
	pm, err := ReadManifest(filepath.Join(root, VendorDir, filepath.FromSlash(name)))
	if err != nil {
		return fmt.Errorf("%s: пакет без %s: %w", name, ManifestName, err)
	}
	if pm.Name != name {
		return fmt.Errorf("%s: пакет называет себя %q — имена обязаны совпадать", name, pm.Name)
	}
	for _, dep := range sortedKeys(pm.Dependencies) {
		if err := ensure(root, project, lock, dep, pm.Dependencies[dep], update, append(chain, name), seen); err != nil {
			return err
		}
	}
	// Ассеты пакета — в контент игры (если игра сказала, где он).
	return copyAssets(root, project, name)
}

// ensureLocal — file:-ссылка: пакет лежит рядом, хэшируем и копируем как есть.
func ensureLocal(root, name, ref string) (LockEntry, error) {
	src := filepath.Join(root, filepath.FromSlash(strings.TrimPrefix(ref, "file:")))
	files, err := dirFiles(src)
	if err != nil {
		return LockEntry{}, err
	}
	if err := vendor(root, name, src, files); err != nil {
		return LockEntry{}, err
	}
	hashes := map[string]string{}
	for _, f := range files {
		h, err := fileSHA(filepath.Join(src, filepath.FromSlash(f)))
		if err != nil {
			return LockEntry{}, err
		}
		hashes[f] = h
	}
	return LockEntry{Ref: ref, Files: hashes}, nil
}

// ensureGithub — github:-ссылка. Оффлайн-путь: lock совпал и vendor совпал по
// хэшам — сети нет вообще. Иначе тарболл, проверка, vendor.
func ensureGithub(root string, lock *Lock, name, ref string, update bool) (LockEntry, error) {
	m := reGithub.FindStringSubmatch(ref)
	if m == nil {
		return LockEntry{}, fmt.Errorf("ссылка %q не разобралась (github:owner/repo@ref[#subdir])", ref)
	}
	owner, repo, gref, subdir := m[1], m[2], m[3], m[4]
	if mutableRefs[gref] {
		return LockEntry{}, fmt.Errorf("ссылка на ветку %q запрещена: ветка двигается, сборка перестаёт быть воспроизводимой. Прикалывай тег или полный SHA коммита", gref)
	}

	prev, hasPrev := lock.Packages[name]
	if !update && hasPrev && prev.Ref == ref {
		if vendorMatches(root, name, prev.Files) {
			return prev, nil // оффлайн: всё уже на месте и совпадает
		}
	}

	url := fmt.Sprintf("%s/%s/%s/tar.gz/%s", CodeloadBase, owner, repo, gref)
	if reHexSHA.MatchString(gref) {
		// полный SHA качается тем же путём
	} else {
		url = fmt.Sprintf("%s/%s/%s/tar.gz/refs/tags/%s", CodeloadBase, owner, repo, gref)
	}
	resp, err := http.Get(url)
	if err != nil {
		return LockEntry{}, fmt.Errorf("скачивание %s: %w", url, err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return LockEntry{}, fmt.Errorf("скачивание %s: HTTP %d", url, resp.StatusCode)
	}
	raw, err := io.ReadAll(io.LimitReader(resp.Body, maxTarball+1))
	if err != nil {
		return LockEntry{}, err
	}
	if len(raw) > maxTarball {
		return LockEntry{}, fmt.Errorf("тарболл больше %d МБ — это не пакет скриптов", maxTarball>>20)
	}
	sum := sha256.Sum256(raw)
	tarSHA := hex.EncodeToString(sum[:])
	// Замок: lock уже знает хэш этого ref — новый обязан совпасть.
	if !update && hasPrev && prev.Ref == ref && prev.SHA256 != "" && prev.SHA256 != tarSHA {
		return LockEntry{}, fmt.Errorf("SHA-256 тарболла не совпал с lvns.lock (%s ≠ %s): содержимое %q изменилось под тем же тегом", tarSHA, prev.SHA256, gref)
	}

	files, commit, err := untarPackage(raw, subdir)
	if err != nil {
		return LockEntry{}, err
	}
	if err := vendorBytes(root, name, files); err != nil {
		return LockEntry{}, err
	}
	hashes := map[string]string{}
	for rel, data := range files {
		h := sha256.Sum256(data)
		hashes[rel] = hex.EncodeToString(h[:])
	}
	return LockEntry{Ref: ref, Commit: commit, SHA256: tarSHA, Files: hashes}, nil
}

// untarPackage распаковывает codeload-тарболл в память: первый сегмент пути
// (repo-ref/) срезается, subdir выделяется, всё подозрительное — ошибка.
func untarPackage(raw []byte, subdir string) (map[string][]byte, string, error) {
	gz, err := gzip.NewReader(strings.NewReader(string(raw)))
	if err != nil {
		return nil, "", err
	}
	tr := tar.NewReader(gz)
	files := map[string][]byte{}
	commit := ""
	for {
		hdr, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, "", err
		}
		if hdr.Typeflag == tar.TypeXGlobalHeader {
			if c, ok := hdr.PAXRecords["comment"]; ok {
				commit = c // codeload кладёт сюда SHA коммита
			}
			continue
		}
		name := path.Clean(hdr.Name)
		if strings.HasPrefix(name, "..") || path.IsAbs(name) || strings.Contains(name, "/../") {
			return nil, "", fmt.Errorf("подозрительный путь в тарболле: %q", hdr.Name)
		}
		// срезать корневой каталог repo-ref/
		i := strings.IndexByte(name, '/')
		if i < 0 {
			continue
		}
		rel := name[i+1:]
		if subdir != "" {
			sd := strings.TrimSuffix(subdir, "/") + "/"
			if !strings.HasPrefix(rel, sd) {
				continue
			}
			rel = strings.TrimPrefix(rel, sd)
		}
		if rel == "" {
			continue
		}
		switch hdr.Typeflag {
		case tar.TypeDir:
			continue
		case tar.TypeReg:
		default:
			return nil, "", fmt.Errorf("%q: в пакете допустимы только обычные файлы (тип %q)", hdr.Name, hdr.Typeflag)
		}
		if len(files) >= maxFiles {
			return nil, "", fmt.Errorf("в пакете больше %d файлов", maxFiles)
		}
		data, err := io.ReadAll(io.LimitReader(tr, maxTarball))
		if err != nil {
			return nil, "", err
		}
		files[rel] = data
	}
	if len(files) == 0 {
		return nil, "", fmt.Errorf("в тарболле не нашлось файлов пакета%s", map[bool]string{true: " (проверь #subdir)", false: ""}[subdir != ""])
	}
	return files, commit, nil
}

// vendor / vendorBytes — записать пакет в lvns_packages/<name>/ начисто.
func vendor(root, name, src string, files []string) error {
	byteFiles := map[string][]byte{}
	for _, f := range files {
		data, err := os.ReadFile(filepath.Join(src, filepath.FromSlash(f)))
		if err != nil {
			return err
		}
		byteFiles[f] = data
	}
	return vendorBytes(root, name, byteFiles)
}

func vendorBytes(root, name string, files map[string][]byte) error {
	dst := filepath.Join(root, VendorDir, filepath.FromSlash(name))
	if err := os.RemoveAll(dst); err != nil {
		return err
	}
	for _, rel := range sortedKeys(files) {
		p := filepath.Join(dst, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			return err
		}
		if err := os.WriteFile(p, files[rel], 0o644); err != nil {
			return err
		}
	}
	return nil
}

// vendorMatches — оффлайн-проверка: vendor побайтно совпадает с lock.
func vendorMatches(root, name string, hashes map[string]string) bool {
	dir := filepath.Join(root, VendorDir, filepath.FromSlash(name))
	for rel, want := range hashes {
		got, err := fileSHA(filepath.Join(dir, filepath.FromSlash(rel)))
		if err != nil || got != want {
			return false
		}
	}
	// и ничего лишнего
	files, err := dirFiles(dir)
	if err != nil || len(files) != len(hashes) {
		return false
	}
	return true
}

// copyAssets кладёт assets/ пакета в контент игры:
// <content_dir>/packages/<pkg>/… — рантайм никогда не ходит на GitHub сам.
func copyAssets(root string, project *Manifest, name string) error {
	if project.ContentDir == "" {
		return nil
	}
	src := filepath.Join(root, VendorDir, filepath.FromSlash(name), "assets")
	if _, err := os.Stat(src); os.IsNotExist(err) {
		return nil
	}
	short := name[strings.LastIndexByte(name, '/')+1:]
	dst := filepath.Join(root, filepath.FromSlash(project.ContentDir), "packages", short)
	if err := os.RemoveAll(dst); err != nil {
		return err
	}
	return filepath.Walk(src, func(p string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return err
		}
		rel, _ := filepath.Rel(src, p)
		data, err := os.ReadFile(p)
		if err != nil {
			return err
		}
		out := filepath.Join(dst, rel)
		if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
			return err
		}
		return os.WriteFile(out, data, 0o644)
	})
}

// Add вписывает зависимость в манифест проекта и сразу синхронизирует.
func Add(root, name, ref string) error {
	if !reName.MatchString(name) {
		return fmt.Errorf("%s: имя пакета должно быть вида @scope/pkg", name)
	}
	p := filepath.Join(root, ManifestName)
	m := &Manifest{}
	if raw, err := os.ReadFile(p); err == nil {
		if err := json.Unmarshal(raw, m); err != nil {
			return fmt.Errorf("%s: %w", ManifestName, err)
		}
	}
	if m.Dependencies == nil {
		m.Dependencies = map[string]string{}
	}
	m.Dependencies[name] = ref
	raw, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	if err := os.WriteFile(p, append(raw, '\n'), 0o644); err != nil {
		return err
	}
	return Sync(root, false)
}

// List — что зафиксировано.
func List(root string, w io.Writer) error {
	lock, err := readLock(root)
	if err != nil {
		return err
	}
	if len(lock.Packages) == 0 {
		fmt.Fprintln(w, "lock пуст — зависимостей нет (или ещё не было deps sync)")
		return nil
	}
	for _, name := range sortedKeys(lock.Packages) {
		e := lock.Packages[name]
		mark := ""
		if strings.HasPrefix(e.Ref, "file:") {
			mark = "   ⚠ mutable (file:, режим разработки)"
		}
		commit := e.Commit
		if len(commit) > 12 {
			commit = commit[:12]
		}
		fmt.Fprintf(w, "%s  %s  %s  files:%d%s\n", name, e.Ref, commit, len(e.Files), mark)
	}
	return nil
}

// ── мелочи ──────────────────────────────────────────────────────────────

func dirFiles(dir string) ([]string, error) {
	var out []string
	err := filepath.Walk(dir, func(p string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if info.Mode()&os.ModeSymlink != 0 {
			return fmt.Errorf("%s: симлинки в пакете запрещены", p)
		}
		if info.IsDir() {
			if info.Name() == ".git" {
				return filepath.SkipDir
			}
			return nil
		}
		rel, _ := filepath.Rel(dir, p)
		out = append(out, filepath.ToSlash(rel))
		return nil
	})
	sort.Strings(out)
	return out, err
}

func fileSHA(p string) (string, error) {
	data, err := os.ReadFile(p)
	if err != nil {
		return "", err
	}
	h := sha256.Sum256(data)
	return hex.EncodeToString(h[:]), nil
}

func sortedKeys[V any](m map[string]V) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
