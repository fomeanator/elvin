// Export a ready-to-open Unity project as a zip.
//
// The sandbox/ project is the template: a clean Unity project that pulls the
// engine as a UPM package. Export copies it, swaps in a generated Boot.cs (the
// player's server URL + game name), patches the product name / bundle id, and
// streams a zip. The author opens it in Unity and hits Build — the game talks
// to the same server the IDE writes to (online mode).
//
//	POST /v1/export   body: {name, bundleId, company, serverUrl, askName}
package main

import (
	"archive/zip"
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
)

// Каталоги шаблона, которым не место в экспорте. Сравнение регистронезависимое:
// на macOS «build» и «Build» — одна и та же папка, и список, написанный с
// заглавной, молча пропускал 198 МБ собранных 3D-наборов из build/.
var exportSkipDirs = map[string]bool{
	"library": true, "temp": true, "logs": true, "obj": true, "build": true,
	"builds": true, "usersettings": true, ".git": true, ".vs": true, ".idea": true,
}

type exportConfig struct {
	// BundleEngine — положить исходники движка ВНУТРЬ архива и оставить
	// локальные ссылки вместо зеркал на GitHub.
	//
	// Зеркала существуют для чужих проектов: они дают маленький клон вместо
	// монорепозитория на триста мегабайт. Но НАШЕЙ собственной сборке крюк
	// через GitHub не нужен вовсе — исходники лежат рядом, — а стоит он
	// дорого: зеркала обновляются только по релизному тегу, и когда токен
	// публикации протух, наши APK две недели собирались на старом движке.
	// Ошибка при этом молчаливая: экспорт отвечает 200, проект открывается,
	// собирается — и едет с кодом двухнедельной давности.
	BundleEngine bool   `json:"bundleEngine"`
	Name         string `json:"name"`
	BundleID     string `json:"bundleId"`
	Company      string `json:"company"`
	ServerURL    string `json:"serverUrl"`
	// AltServers — запасные адреса ТОГО ЖЕ сервера. На старте движок гоняет их
	// вместе с основным наперегонки по /healthz и берёт первый ответивший
	// (ServerSelectScreen), поэтому упавшее или заблокированное имя больше не
	// превращает установленную сборку в кирпич: у продукта основной домен
	// однажды начали резать по SNI, и единственным путём к серверу осталось
	// второе имя — которое в собранных приложениях было не прописать.
	AltServers []altServer `json:"altServers"`
	// Icon — картинка из контент-директории (например "art/cover.png"), которая
	// станет иконкой приложения. Пусто — иконки нет вовсе: чужую из шаблона
	// экспорт не отдаёт (см. exportIconRel), потому что бренд другой игры на
	// рабочем столе хуже кубика Unity.
	Icon    string `json:"icon"`
	AskName bool   `json:"askName"`
	Offline bool   `json:"offline"` // bundle content into StreamingAssets (no server needed)
}

// altServer — запасной адрес и подпись для ручного выбора сервера.
type altServer struct {
	Name string `json:"name"`
	URL  string `json:"url"`
}

// where bundled content lives inside the exported project, and the URL prefixes
// the engine asks for (mirrored under it so file:// reads resolve).
const bundleDir = "Assets/StreamingAssets/lvn"

// resolveTemplate finds the sandbox template dir, trying a few cwd-relative
// candidates so it works whether the server runs from the repo root or server/.
func resolveTemplate(flagDir string) string {
	for _, c := range []string{flagDir, "./sandbox", "sandbox", "../sandbox"} {
		if c == "" {
			continue
		}
		if fi, err := os.Stat(filepath.Join(c, "Assets")); err == nil && fi.IsDir() {
			return c
		}
	}
	return flagDir
}

func sanitizeName(s, fallback string) string {
	s = strings.TrimSpace(s)
	re := regexp.MustCompile(`[^A-Za-z0-9 _.-]+`)
	s = re.ReplaceAllString(s, "")
	s = strings.TrimSpace(s)
	if s == "" {
		return fallback
	}
	return s
}

// safeLabel — человеческое имя, пригодное для файла и ссылки. В отличие от
// sanitizeName сохраняет ЛЮБОЙ алфавит: обрезать кириллицу значит молча
// превращать русское название в пустую строку. Запрещено ровно то, что делает
// имя опасным: разделители пути, управляющие символы и точки-переходы.
func safeLabel(s, fallback string, max int) string {
	s = strings.TrimSpace(s)
	var b strings.Builder
	for _, r := range s {
		switch {
		case r < 0x20 || r == 0x7f: // управляющие
		case r == '/' || r == '\\' || r == ':':
		default:
			b.WriteRune(r)
		}
	}
	// До стабильного результата: одна замена оставляет «....» → «..», то есть
	// ровно то, от чего избавлялись. Разделители пути уже убраны выше, так
	// что это подстраховка, но подстраховка должна быть верной.
	out := b.String()
	for strings.Contains(out, "..") {
		out = strings.ReplaceAll(out, "..", ".")
	}
	out = strings.TrimSpace(out)
	if rs := []rune(out); len(rs) > max {
		out = string(rs[:max])
	}
	if out == "" {
		return fallback
	}
	return out
}

func (s *server) handleExport(w http.ResponseWriter, r *http.Request) {
	// Export bundles the ENTIRE content directory — gate it behind the admin
	// token like every other privileged endpoint, or it leaks all content.
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var cfg exportConfig
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<20)).Decode(&cfg); err != nil && err != io.EOF {
		http.Error(w, "bad body", http.StatusBadRequest)
		return
	}
	if cfg.ServerURL == "" {
		// default to the host the request came in on, so the exported game points
		// back here unless the author overrides it.
		//
		// Через requestBase, а НЕ по r.TLS: в проде сервер стоит за обратным
		// прокси, поэтому r.TLS всегда пустой и наивная догадка вшивала в
		// приложение "http://". Цена ошибки максимальная и отложенная: Android
		// с 9-й версии по умолчанию запрещает открытый HTTP, так что собранный
		// APK не грузил бы контент ВООБЩЕ — и выяснилось бы это на телефоне, а
		// не при экспорте. requestBase читает X-Forwarded-Proto/Host.
		cfg.ServerURL = requestBase(r)
	}
	name := sanitizeName(cfg.Name, "LvnGame")
	folder := strings.ReplaceAll(name, " ", "")
	if folder == "" {
		folder = "LvnGame"
	}

	tmpl := resolveTemplate(s.templateDir)
	if _, err := os.Stat(filepath.Join(tmpl, "Assets")); err != nil {
		http.Error(w, "export template not found (sandbox project missing)", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", fmt.Sprintf(`attachment; filename="%s.zip"`, folder))
	zw := zip.NewWriter(w)
	defer zw.Close()

	// Пакеты движка кладём ДО обхода шаблона: если запись оборвётся, лучше
	// получить архив без содержимого, чем архив с содержимым и без движка —
	// второй выглядит рабочим и не собирается.
	if cfg.BundleEngine {
		if err := copyEnginePackages(zw, folder, engineDeps(tmpl)); err != nil {
			// Заголовки уже ушли, кода ошибки не отдать: обрываем архив, чтобы
			// он не выглядел целым.
			return
		}
	}

	bootRel := filepath.Join("Assets", "Sandbox", "Boot.cs")
	settingsRel := filepath.Join("ProjectSettings", "ProjectSettings.asset")
	manifestRel := filepath.Join("Packages", "manifest.json")
	lockRel := filepath.Join("Packages", "packages-lock.json")

	_ = filepath.Walk(tmpl, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		rel, rerr := filepath.Rel(tmpl, path)
		if rerr != nil {
			return nil
		}
		if rel == "." {
			return nil
		}
		// skip excluded top-level dirs (and everything under them)
		top := strings.ToLower(strings.SplitN(filepath.ToSlash(rel), "/", 2)[0])
		if info.IsDir() {
			if exportSkipDirs[top] {
				return filepath.SkipDir
			}
			return nil
		}
		if exportSkipDirs[top] {
			return nil
		}

		// the lock file pins the engine's local file: path — drop it so Unity
		// re-resolves against the git URL we write into the manifest.
		if rel == lockRel {
			return nil
		}
		// local junk that must not ship in every export: dev screenshots under
		// Assets and loose images at the template root.
		relSlash := filepath.ToSlash(rel)
		if !exportAssetAllowed(relSlash) {
			return nil
		}
		// Мусор в корне шаблона: случайные картинки и отчёты об аварии Unity.
		if !strings.Contains(relSlash, "/") &&
			(strings.HasSuffix(relSlash, ".png") || strings.HasSuffix(relSlash, ".jpg") ||
				strings.HasPrefix(relSlash, "mono_crash.")) {
			return nil
		}

		var data []byte
		switch rel {
		case bootRel:
			data = []byte(bootSource(cfg))
		case settingsRel:
			raw, _ := os.ReadFile(path)
			data = patchProjectSettings(raw, cfg)
		case manifestRel:
			raw, _ := os.ReadFile(path)
			if cfg.BundleEngine {
				// Пакеты уезжают в архив рядом с манифестом, поэтому путь
				// становится соседним каталогом.
				data = localizeManifest(raw)
			} else {
				data = patchManifest(raw, engineReleaseTag(tmpl))
			}
		default:
			raw, derr := os.ReadFile(path)
			if derr != nil {
				return nil
			}
			data = raw
		}

		zf, cerr := zw.Create(folder + "/" + filepath.ToSlash(rel))
		if cerr != nil {
			return nil
		}
		_, _ = zf.Write(data)
		return nil
	})

	// Иконка приложения: берём картинку из контента и кладём туда, где её ждёт
	// AppIcon (пакет движка зовёт его сам при сборке). Без этого собранное
	// приложение выходило с кубиком Unity — или, что хуже, с иконкой той игры,
	// чей проект послужил шаблоном.
	if data, ok := s.exportIcon(cfg.Icon); ok {
		if zf, err := zw.Create(folder + "/" + exportIconRel); err == nil {
			_, _ = zf.Write(data)
		}
	}

	// Offline build: bake the novel's content into StreamingAssets, mirroring the
	// server's URL paths so the engine reads it via file:// with no network.
	if cfg.Offline {
		s.bundleContent(zw, folder)
	} else {
		// Онлайн-сборка везёт СИД: критичные файлы вводной (первая сцена) и её
		// скрипты внутри APK — первый запуск одевает сцену без сети вообще.
		// Сид перетирается живым контентом по версии; каждая сборка кладёт
		// свежий сид сама — отдельного шага в конвейере не нужно.
		s.bundleIntroSeed(zw, folder)
	}

	// a short README so the author knows what to do with the zip.
	if zf, err := zw.Create(folder + "/HOW_TO_BUILD.md"); err == nil {
		zf.Write([]byte(buildReadme(cfg, name)))
	}
}

// exportAssetKeep — единственные каталоги под Assets/, которые едут в экспорт.
//
// Список белый, а не чёрный, и это принципиально. Шаблон — рабочая песочница
// движка: в ней копятся покупные 3D-киты, наборы сцен, редакторные скрипты
// (сборка наборов, покраска террейна, снимки для README). Всё это уезжало в
// каждый экспорт, и последствий было два: архив на 561 МБ вместо десятка, и
// главное — экспортированный проект НЕ КОМПИЛИРОВАЛСЯ. Редакторные скрипты
// песочницы обращаются к типам, которых в закреплённом релизе движка ещё нет,
// так что сборка APK падала на чужой кухне, к игре отношения не имеющей.
// Чёрный список чинил бы это ровно до следующего кита в песочнице.
var exportAssetKeep = []string{
	"Assets/Sandbox",      // Boot.cs — точка входа, её мы и генерируем
	"Assets/Resources/UI", // тема загрузочного экрана: без неё текст на вуали без шрифта
	// Assets/Icon сюда НЕ входит: иконки шаблона — иконки его собственной игры.
	// Свою кладём отдельно, после обхода (см. exportIconRel).
}

// exportAssetAllowed решает, едет ли файл шаблона в экспорт. Всё вне Assets/
// (ProjectSettings, Packages) едет как есть — это конфигурация проекта.
func exportAssetAllowed(relSlash string) bool {
	if !strings.HasPrefix(relSlash, "Assets/") {
		return true
	}
	for _, keep := range exportAssetKeep {
		if relSlash == keep || strings.HasPrefix(relSlash, keep+"/") || relSlash == keep+".meta" {
			return true
		}
	}
	// .meta промежуточных каталогов: без Assets/Resources.meta Unity
	// переимпортирует папку и меняет ссылки.
	if strings.HasSuffix(relSlash, ".meta") {
		dir := strings.TrimSuffix(relSlash, ".meta")
		for _, keep := range exportAssetKeep {
			if strings.HasPrefix(keep, dir+"/") {
				return true
			}
		}
	}
	return false
}

// serverLabel — подпись адреса в экране выбора сервера. Через sanitizeName её
// пропускать нельзя: тот чистит строку под ИМЯ ФАЙЛА и вырезает всё, кроме
// латиницы, — «Запасной адрес» превращался в пустоту и подменялся заглушкой.
// Здесь строка едет в C#-литерал через json.Marshal, который экранирует
// кавычки и управляющие символы сам, так что резать нужно только длину и
// переводы строк.
func serverLabel(name string) string {
	name = strings.TrimSpace(strings.Map(func(r rune) rune {
		if r == '\n' || r == '\r' || r == '\t' {
			return ' '
		}
		if r < 0x20 || r == 0x7f {
			return -1
		}
		return r
	}, name))
	if runes := []rune(name); len(runes) > 40 {
		name = string(runes[:40])
	}
	if name == "" {
		return "Запасной"
	}
	return name
}

// exportIconRel — куда в проекте ложится иконка. Тот же путь читает
// Lvn.EditorTools.AppIcon, и менять его надо в двух местах сразу.
const exportIconRel = "Assets/Icon/app-icon.png"

// exportIcon читает картинку автора из контент-директории. Путь приходит из
// запроса, поэтому склеивается через Clean("/"+rel): выйти за пределы контента
// («../../etc/passwd») он не должен даже теоретически — иначе экспорт станет
// способом вынести с сервера любой файл.
func (s *server) exportIcon(rel string) ([]byte, bool) {
	rel = strings.TrimSpace(rel)
	if rel == "" {
		return nil, false
	}
	rel = strings.TrimPrefix(strings.TrimPrefix(rel, "/content/"), "/")
	clean := filepath.Clean("/" + filepath.ToSlash(rel))[1:]
	if clean == "" {
		return nil, false
	}
	switch strings.ToLower(filepath.Ext(clean)) {
	case ".png", ".jpg", ".jpeg":
	default:
		log.Printf("[export] иконка %q не картинка — пропускаю", rel)
		return nil, false
	}
	data, err := os.ReadFile(filepath.Join(s.content, clean))
	if err != nil {
		log.Printf("[export] иконка %q не читается: %v", rel, err)
		return nil, false
	}
	return data, true
}

// bundleContent copies the content dir into StreamingAssets and writes the
// manifest + version index at the exact paths the engine requests, so an
// offline build resolves everything locally.
func (s *server) bundleContent(zw *zip.Writer, folder string) {
	base := folder + "/" + bundleDir

	// every served file → StreamingAssets/lvn/content/<rel>
	_ = filepath.Walk(s.content, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		rel, rerr := filepath.Rel(s.content, path)
		if rerr != nil {
			return nil
		}
		raw, derr := os.ReadFile(path)
		if derr != nil {
			return nil
		}
		if zf, cerr := zw.Create(base + "/content/" + filepath.ToSlash(rel)); cerr == nil {
			zf.Write(raw)
		}
		return nil
	})

	// the manifest at the engine's API path: GET /v1/content/manifest
	if raw, err := os.ReadFile(filepath.Join(s.content, "manifest.json")); err == nil {
		if zf, cerr := zw.Create(base + "/v1/content/manifest"); cerr == nil {
			zf.Write(raw)
		}
	}

	// the version index: GET /content/asset-versions.json (optional, but matches online behaviour)
	if data, err := json.Marshal(s.computeVersions(false)); err == nil {
		if zf, cerr := zw.Create(base + "/content/asset-versions.json"); cerr == nil {
			zf.Write(data)
		}
	}
}

// bundleIntroSeed кладёт в StreamingAssets/lvn-seed критичные файлы вводной
// новеллы (type:"intro"): script_url её глав + все ассеты с critical:true из
// планов глав. Плюс index.json со списком rel-путей — клиент по нему решает
// «есть в сиде» без слепых запросов внутрь APK. Файлы кладутся ОРИГИНАЛАМИ
// (клиент нормализует @2k-запрос к базе и ужимает при декоде сам).
func (s *server) bundleIntroSeed(zw *zip.Writer, folder string) {
	raw, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		return
	}
	var m map[string]any
	if json.Unmarshal(raw, &m) != nil {
		return
	}
	titles, _ := m["titles"].([]any)
	urls := map[string]bool{}
	for _, ti := range titles {
		t, _ := ti.(map[string]any)
		if t == nil || !strings.EqualFold(str(t["type"]), "intro") {
			continue
		}
		for _, si := range asList(t["seasons"]) {
			se, _ := si.(map[string]any)
			for _, ci := range asList(se["chapters"]) {
				ch, _ := ci.(map[string]any)
				if ch == nil {
					continue
				}
				if u := str(ch["script_url"]); u != "" {
					urls[u] = true
				}
				assets, _ := ch["assets"].(map[string]any)
				for u, mi := range assets {
					meta, _ := mi.(map[string]any)
					if crit, _ := meta["critical"].(bool); crit {
						urls[u] = true
					}
				}
			}
		}
	}
	if len(urls) == 0 {
		return
	}
	base := folder + "/Assets/StreamingAssets/lvn-seed"
	var index []string
	for u := range urls {
		rel := strings.TrimPrefix(u, "/content/")
		if rel == u { // не контент-URL — не наш файл
			continue
		}
		data, rerr := os.ReadFile(filepath.Join(s.content, filepath.FromSlash(rel)))
		if rerr != nil {
			continue
		}
		entry := "content/" + rel
		if zf, cerr := zw.Create(base + "/" + entry); cerr == nil {
			_, _ = zf.Write(data)
			index = append(index, entry)
		}
	}
	sort.Strings(index)
	if data, jerr := json.Marshal(index); jerr == nil {
		if zf, cerr := zw.Create(base + "/index.json"); cerr == nil {
			_, _ = zf.Write(data)
		}
	}
}

func str(v any) string { s, _ := v.(string); return s }

func asList(v any) []any { l, _ := v.([]any); return l }

// bootSource renders the standalone Boot.cs with the author's settings.
func bootSource(cfg exportConfig) string {
	ask := "false"
	if cfg.AskName {
		ask = "true"
	}
	offline := "false"
	if cfg.Offline {
		offline = "true"
	}
	// Emit the URL as a JSON string literal: json.Marshal escapes ", \ and
	// control chars, which are all valid C# string escapes too — so a crafted
	// serverUrl can't break out of the literal and inject code into Boot.cs.
	urlLit, _ := json.Marshal(cfg.ServerURL)
	// Запасные адреса — тем же способом: каждая строка через json.Marshal,
	// пустые и битые отбрасываем здесь, чтобы в Boot.cs не уехал кортеж с
	// пустым URL (движок его молча пропустит, но читать такой файл неприятно).
	alts := ""
	for _, a := range cfg.AltServers {
		u := strings.TrimSpace(a.URL)
		if u == "" || u == strings.TrimSpace(cfg.ServerURL) {
			continue
		}
		nameLit, _ := json.Marshal(serverLabel(a.Name))
		altLit, _ := json.Marshal(u)
		if alts != "" {
			alts += ", "
		}
		alts += "(" + string(nameLit) + ", " + string(altLit) + ")"
	}
	knownServers := ""
	if alts != "" {
		knownServers = "\n            // Запасные адреса того же сервера: на старте они гоняются\n" +
			"            // с основным наперегонки по /healthz, побеждает первый живой.\n" +
			"            app.KnownServers = new (string, string)[] { " + alts + " };"
	}
	return `using UnityEngine;
using UnityEngine.EventSystems;
using Lvn.UI.Screens;

namespace Game
{
    // Generated by the ELVIN IDE export. Boots the novel against the
    // configured server. Build from Unity (File ▸ Build Settings ▸ Build).
    public static class Boot
    {
        public const string ServerUrl = ` + string(urlLit) + `;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Run()
        {
            if (Object.FindFirstObjectByType<NovelApp>() != null) return;

            if (Object.FindFirstObjectByType<Camera>() == null)
            {
                var camGo = new GameObject("Main Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                camGo.tag = "MainCamera";
                Object.DontDestroyOnLoad(camGo);
            }

            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                Object.DontDestroyOnLoad(es);
            }

            var go = new GameObject("NovelApp");
            var app = go.AddComponent<NovelApp>();
            app.ServerUrl = ServerUrl;` + knownServers + `
            app.OfflineBundled = ` + offline + `;
            app.AskName = ` + ask + `;
            app.SyncInterval = 5f;
            app.ThemeResourcePath = "UI/AppLoading/UnityDefaultRuntimeTheme";
            Object.DontDestroyOnLoad(go);
        }
    }
}
`
}

// patchProjectSettings rewrites product/company name and bundle id in the
// ProjectSettings.asset YAML.
func patchProjectSettings(raw []byte, cfg exportConfig) []byte {
	out := string(raw)
	name := sanitizeName(cfg.Name, "LvnGame")
	company := sanitizeName(cfg.Company, "LvnStudio")
	out = regexp.MustCompile(`(?m)^  productName:.*$`).ReplaceAllString(out, "  productName: "+yamlScalar(name))
	out = regexp.MustCompile(`(?m)^  companyName:.*$`).ReplaceAllString(out, "  companyName: "+yamlScalar(company))
	if cfg.BundleID != "" {
		id := sanitizeName(cfg.BundleID, "")
		if id != "" {
			// Заменяется ВЕСЬ блок вместе с его отступными детьми, а не одна
			// строка-заголовок. Прежняя версия дописывала свой `Standalone:`
			// перед старыми строками и оставляла их на месте — YAML получал
			// ДУБЛИ ключей, Unity брал последний, и bundleId не работал вовсе
			// ни для одной платформы. Молча: экспорт отвечал 200, а APK
			// собирался с идентификатором песочницы и мог встать поверх другого
			// приложения на телефоне.
			//
			// Android перечислен явно: для APK значим именно он, а Standalone
			// к телефону отношения не имеет.
			repl := "  applicationIdentifier:\n    Android: " + yamlScalar(id) +
				"\n    Standalone: " + yamlScalar(id) + "\n"
			out = regexp.MustCompile(`(?m)^  applicationIdentifier:\n(?:    [^\n]*\n)*`).
				ReplaceAllString(out, repl)
		}
	}
	return []byte(out)
}

// localizeManifest оставляет пакеты движка локальными, но переписывает путь:
// в песочнице они лежат через два каталога вверх (file:../../unity/Packages/…),
// а в архиве — прямо в Packages/ рядом с манифестом.
func localizeManifest(raw []byte) []byte {
	re := regexp.MustCompile(`"(com\.lvn\.engine(?:\.[a-z0-9-]+)?)"\s*:\s*"file:[^"]*"`)
	return []byte(re.ReplaceAllString(string(raw), `"$1": "file:$1"`))
}

// copyEnginePackages кладёт исходники пакетов движка в архив под Packages/.
//
// Путь берётся ИЗ САМОГО МАНИФЕСТА (file:…), а не вычисляется от каталога
// шаблона: манифест и есть то место, где записано, где лежат пакеты, и
// вычисленный путь разошёлся бы с ним при первой же перестановке каталогов —
// молча, потому что пустой архив тоже архив.
func copyEnginePackages(zw *zip.Writer, folder string, deps map[string]string) error {
	for name, src := range deps {
		if _, err := os.Stat(src); err != nil {
			continue // пакета нет — не наш случай, не повод рушить экспорт
		}
		err := filepath.Walk(src, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() {
				return nil
			}
			rel, rerr := filepath.Rel(src, path)
			if rerr != nil {
				return nil
			}
			data, rerr := os.ReadFile(path)
			if rerr != nil {
				return nil
			}
			// ВНУТРЬ папки проекта, а не рядом с ней: обход шаблона кладёт всё
			// под <Имя игры>/, и пакеты, оказавшиеся снаружи, Unity просто не
			// увидит — проект откроется без движка.
			f, werr := zw.Create(folder + "/" + filepath.ToSlash(filepath.Join("Packages", name, rel)))
			if werr != nil {
				return werr
			}
			_, werr = f.Write(data)
			return werr
		})
		if err != nil {
			return err
		}
	}
	return nil
}

// engineDeps — пакеты движка из манифеста песочницы: имя → каталог на диске.
// Пути в манифесте относительны папке Packages, как их понимает Unity.
func engineDeps(tmpl string) map[string]string {
	base := filepath.Join(tmpl, "Packages")
	raw, err := os.ReadFile(filepath.Join(base, "manifest.json"))
	if err != nil {
		return nil
	}
	re := regexp.MustCompile(`"(com\.lvn\.engine(?:\.[a-z0-9-]+)?)"\s*:\s*"file:([^"]+)"`)
	out := map[string]string{}
	for _, m := range re.FindAllSubmatch(raw, -1) {
		out[string(m[1])] = filepath.Join(base, filepath.FromSlash(string(m[2])))
	}
	return out
}

// mirrorRepoURL is the public read-only mirror repo for one engine package:
// per-package repos hold just that package's history (a few-MB clone), where
// the monorepo drags ~300 MB of demo content through every UPM resolve. The
// mirrors are produced by the mirror-packages workflow on every release tag.
// "com.lvn.engine" → …/lvn-engine.git, "com.lvn.engine.shell" →
// …/lvn-engine-shell.git, and so on.
func mirrorRepoURL(name string) string {
	suffix := strings.TrimPrefix(name, "com.lvn.engine")
	return "https://github.com/fomeanator/lvn-engine" + strings.ReplaceAll(suffix, ".", "-") + ".git"
}

// patchManifest swaps EVERY engine-family local file: dependency
// (com.lvn.engine, .shell, .services, .spine, .addressables, …) for its
// public mirror git URL so a downloaded project resolves on open, and drops
// repo-only dev tooling (the MCP editor bridge) that players' builds must
// not depend on. A non-empty tag pins every URL to that release (#vX.Y.Z) —
// the packages are versioned together, and mixing commits would duplicate
// classes a pre-split engine still carries. An exported project keeps
// building identically until its OWNER bumps the tags.
func patchManifest(raw []byte, tag string) []byte {
	re := regexp.MustCompile(`"(com\.lvn\.engine(?:\.[a-z0-9-]+)?)"\s*:\s*"file:[^"]*"`)
	out := re.ReplaceAllStringFunc(string(raw), func(dep string) string {
		name := re.FindStringSubmatch(dep)[1]
		url := mirrorRepoURL(name)
		if tag != "" {
			url += "#" + tag
		}
		return `"` + name + `": "` + url + `"`
	})
	dev := regexp.MustCompile(`\s*"com\.coplaydev\.unity-mcp"\s*:\s*"[^"]*",?`)
	out = dev.ReplaceAllString(out, "")
	return []byte(out)
}

// engineReleaseTag derives the release tag to pin exports to: the version of
// the engine package the template's manifest points at (vX.Y.Z — the release
// process tags every published version). Empty when it can't be determined —
// the export then tracks the default branch, which is only right for dev.
func engineReleaseTag(tmpl string) string {
	raw, err := os.ReadFile(filepath.Join(tmpl, "Packages", "manifest.json"))
	if err != nil {
		return ""
	}
	m := regexp.MustCompile(`"com\.lvn\.engine"\s*:\s*"file:([^"]+)"`).FindSubmatch(raw)
	if m == nil {
		return ""
	}
	// file: paths resolve relative to the Packages folder.
	pkg, err := os.ReadFile(filepath.Join(tmpl, "Packages", filepath.FromSlash(string(m[1])), "package.json"))
	if err != nil {
		return ""
	}
	v := regexp.MustCompile(`"version"\s*:\s*"([^"]+)"`).FindSubmatch(pkg)
	if v == nil {
		return ""
	}
	return "v" + string(v[1])
}

func yamlScalar(s string) string {
	if strings.ContainsAny(s, ":#") {
		return `"` + strings.ReplaceAll(s, `"`, `\"`) + `"`
	}
	return s
}

func buildReadme(cfg exportConfig, name string) string {
	head := "# " + name + "\n\n" +
		"Exported from ELVIN IDE. This is a complete Unity project.\n\n" +
		"## Build\n" +
		"1. Open this folder in Unity (the engine package is pulled automatically).\n" +
		"2. File ▸ Build Settings ▸ pick a platform ▸ Build.\n\n" +
		"## Updating the engine\n" +
		"The engine dependency in `Packages/manifest.json` is pinned to the\n" +
		"release it was exported with (`…com.lvn.engine#vX.Y.Z`) — engine updates\n" +
		"never change your project until you opt in. To update: change the tag to\n" +
		"the new release (see the engine CHANGELOG), reopen the project, and Unity\n" +
		"re-resolves the package. Releases keep saves, scripts (.lvn/.lvns) and\n" +
		"manifests compatible within a major version; player saves are\n" +
		"schema-versioned and migrate automatically.\n\n"
	if cfg.Offline {
		return head + "## Content\n" +
			"The novel is bundled inside the game (StreamingAssets). It runs fully\n" +
			"offline — no server needed. Re-export to update the content.\n"
	}
	return head + "## Content\n" +
		"The game loads its novel from your server at:\n\n    " + cfg.ServerURL + "\n\n" +
		"Keep that server running (and reachable) for players. Edit chapters in the\n" +
		"authoring panel and they update live.\n"
}

// translit — кириллица в латиницу для тех мест, где имя становится путём.
// Не транслитерируем ВЕЗДЕ намеренно: человек должен видеть своё название как
// написал, а обезличенное имя нужно только файлу.
var translit = map[rune]string{
	'а': "a", 'б': "b", 'в': "v", 'г': "g", 'д': "d", 'е': "e", 'ё': "e",
	'ж': "zh", 'з': "z", 'и': "i", 'й': "y", 'к': "k", 'л': "l", 'м': "m",
	'н': "n", 'о': "o", 'п': "p", 'р': "r", 'с': "s", 'т': "t", 'у': "u",
	'ф': "f", 'х': "h", 'ц': "c", 'ч': "ch", 'ш': "sh", 'щ': "sch",
	'ъ': "", 'ы': "y", 'ь': "", 'э': "e", 'ю': "yu", 'я': "ya",
}

// asciiSlug — имя, пригодное для файла и адреса. Пустой результат заменяется
// запасным: безымянный файл хуже неточного.
func asciiSlug(s, fallback string) string {
	var b strings.Builder
	prevDash := false
	for _, r := range strings.ToLower(strings.TrimSpace(s)) {
		var out string
		switch {
		case r >= 'a' && r <= 'z', r >= '0' && r <= '9', r == '.', r == '_':
			out = string(r)
		case translit[r] != "":
			out = translit[r]
		case r == 'ъ' || r == 'ь':
			out = ""
		default:
			out = "-"
		}
		if out == "-" {
			if prevDash {
				continue
			}
			prevDash = true
		} else if out != "" {
			prevDash = false
		}
		b.WriteString(out)
	}
	out := strings.Trim(b.String(), "-")
	if len([]rune(out)) > 60 {
		out = string([]rune(out)[:60])
	}
	if out == "" {
		return fallback
	}
	return out
}
