package main

// agent.go — «Коннект»: один файл, который делает ИИ работоспособным
// сотрудником этой студии за одну вставку в чат.
//
// Задача с натуры: ребёнок сидит в веб-IDE, жмёт одну кнопку, получает файл и
// кидает его ИИ. Дальше он говорит «сделай игру про драконов», а ИИ пишет и
// публикует её В ТУ ЖЕ студию, которую ребёнок видит на экране. Всё, что для
// этого нужно, лежит в одном файле: как устроен язык, куда стучаться и каким
// ключом.
//
// Отсюда два эндпойнта:
//
//	GET  /v1/admin/agent-bundle   — сам файл: живая шапка с адресом и ключом
//	                                ЭТОГО сервера + встроенная документация.
//	POST /v1/admin/agent/publish  — публикация .lvns одним вызовом: компиляция,
//	                                структурная проверка, запись, регистрация в
//	                                манифесте, ссылка на игру в ответе.
//
// Почему публикация именно ОДНИМ вызовом. Без неё файл-инструкция вынужден
// начинаться со слов «поставь Go, собери lvnconv, скомпилируй» — то есть с
// барьера, ради снятия которого он и существует. Ребёнок не поставит тулчейн, и
// чужой ИИ в браузерном чате тоже. Одна HTTP-ручка, принимающая ИСХОДНИК, —
// разница между «работает» и «инструкция к тому, чего нет».
//
// Про ключ в файле — прямо и без иллюзий: файл содержит админ-токен, потому что
// иначе он бесполезен, и это делает его СЕКРЕТОМ. Он открывает запись во весь
// контент студии. Отдавать его чужому чат-боту — осознанное решение владельца
// сервера; в шапке файла об этом сказано первым абзацем, а не мелким шрифтом.

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

// agentBundleDocs — документация, собранная в один файл на этапе сборки
// (agent_bundle_gen.go делает сборку, TestAgentBundleIsUpToDate стережёт от
// расхождения). Встроена в бинарь: на проде из репозитория ничего нет, там
// только исполняемый файл и каталог контента.
//
//go:embed agent-bundle.md
var agentBundleDocs string

// handleAgentBundle отдаёт файл целиком: живая шапка + встроенные доки.
//
// Шапка генерируется НА ЗАПРОС, а не встроена: у каждого сервера свой адрес и
// свой ключ (у племянника будет отдельный инстанс на VPS), и зашитая шапка
// увела бы его ИИ на чужую студию.
func (s *server) handleAgentBundle(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if !bearerOK(r, s.adminToken) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	base := requestBase(r)
	w.Header().Set("Content-Type", "text/markdown; charset=utf-8")
	w.Header().Set("Content-Disposition", `attachment; filename="elvin-agent.md"`)
	w.Header().Set("Cache-Control", "no-store") // содержит ключ
	_, _ = io_WriteString(w, agentHeader(base, s.adminToken)+agentBundleDocs)
}

// io_WriteString вынесен, чтобы не тащить io ради одной строки.
func io_WriteString(w http.ResponseWriter, s string) (int, error) { return w.Write([]byte(s)) }

// requestBase восстанавливает внешний адрес сервера так, как его видит браузер,
// включая обратный прокси (на проде сервер стоит за ним, и без X-Forwarded-Proto
// в файл уехал бы http://, по которому ИИ получил бы редирект вместо ответа).
func requestBase(r *http.Request) string {
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	if p := r.Header.Get("X-Forwarded-Proto"); p != "" {
		scheme = strings.Split(p, ",")[0]
	}
	host := r.Host
	if h := r.Header.Get("X-Forwarded-Host"); h != "" {
		host = strings.Split(h, ",")[0]
	}
	return scheme + "://" + strings.TrimSpace(host)
}

func agentHeader(base, token string) string {
	return `# Elvin — подключение и полный справочник по языку

Это ОДИН самодостаточный файл. В нём всё, что нужно, чтобы писать и публиковать
игры на движке Elvin: доступ к конкретной студии, формат публикации и полное
описание языка. Ничего доустанавливать не нужно — ни Go, ни Unity, ни SDK.

> **Внимание, это секрет.** Ниже настоящий ключ записи от студии. Он позволяет
> менять и удалять любой её контент. Файл нельзя выкладывать в открытый доступ
> и класть в публичный репозиторий. Если ключ утёк — владелец меняет его на
> сервере (флаг ` + "`-admin-token`" + `) и скачивает файл заново.

## Доступ

    Адрес студии:  ` + base + `
    Ключ записи:   ` + token + `

Все запросы к ` + "`/v1/admin/…`" + ` требуют заголовок:

    Authorization: Bearer ` + token + `

## Как опубликовать игру — один запрос

Пишешь исходник на ` + "`.lvns`" + ` (язык описан ниже) и отправляешь ЕГО. Сервер сам
скомпилирует, проверит структуру, сохранит и зарегистрирует главу в студии.

    POST ` + base + `/v1/admin/agent/publish
    Authorization: Bearer ` + token + `
    Content-Type: application/json

    {
      "id":      "dragons",
      "name":    "Драконы",
      "chapter": 1,
      "lvns":    "scene dragons\n\nТы просыпаешься в пещере.\n- Встать -> up\n\n:up\nПора.\n-> __end\n"
    }

Пример целиком:

    curl -X POST ` + base + `/v1/admin/agent/publish \
      -H "Authorization: Bearer ` + token + `" \
      -H "Content-Type: application/json" \
      -d @game.json

Ответ при успехе:

    {"ok":true,"id":"dragons","commands":6,"warnings":[],"play_url":"` + base + `/","script_url":"/content/scripts/dragons-ch01.lvn"}

Что означает ответ:

* ` + "`warnings`" + ` — ПУСТОЙ список это цель. Каждое предупреждение это реальный
  дефект: висячий переход, глава кончается молча, неизвестная команда. Если
  список не пуст — почини исходник и опубликуй снова, тем же ` + "`id`" + `.
* Ошибка компиляции возвращается кодом 400 с точным номером строки. Структурная
  ошибка — кодом 422, и в этом случае НИЧЕГО не записано: прежняя версия игры
  цела.
* Повторная публикация с тем же ` + "`id`" + ` и ` + "`chapter`" + ` заменяет главу; прошлая версия
  уходит в историю студии, её можно откатить из панели.

Полезное рядом:

    GET ` + base + `/v1/content/manifest      — что уже опубликовано
    GET ` + base + `/content/scripts/<имя>.lvn — забрать скомпилированную главу

## Порядок работы

1. Прочитай раздел «Шпаргалка» ниже — это одна страница, её достаточно для
   первой игры.
2. Напиши ` + "`.lvns`" + ` и опубликуй. Смотри на ` + "`warnings`" + `.
3. Добивайся пустого списка предупреждений. Это и есть критерий готовности:
   он проверяет связность, а не орфографию.
4. Нужна деталь — ищи её в «Полном описании языка» и «Возможностях движка»
   ниже. Не выдумывай синтаксис: чего нет в этом файле, того нет в языке.

Дальше идёт документация движка целиком.

---

`
}

// publishReq — то, что присылает ИИ. Намеренно минимально: заставлять его
// собирать манифест руками означало бы вернуть тот же барьер с другой стороны.
type publishReq struct {
	ID      string `json:"id"`
	Name    string `json:"name"`
	Chapter int    `json:"chapter"`
	Lvns    string `json:"lvns"`
	BgURL   string `json:"bg_url"`
}

func (s *server) handleAgentPublish(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if !bearerOK(r, s.adminToken) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	var req publishReq
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 8<<20)).Decode(&req); err != nil {
		http.Error(w, "invalid JSON: "+err.Error(), http.StatusBadRequest)
		return
	}
	req.ID = strings.TrimSpace(req.ID)
	if !validID(req.ID) {
		http.Error(w, "id must match [A-Za-z0-9_-]+", http.StatusBadRequest)
		return
	}
	if strings.TrimSpace(req.Lvns) == "" {
		http.Error(w, "lvns is empty — send the .lvns source, not a file path", http.StatusBadRequest)
		return
	}
	if req.Chapter <= 0 {
		req.Chapter = 1
	}
	if req.Name == "" {
		req.Name = req.ID
	}

	// 1. Компиляция. Ошибка здесь — ошибка автора, с номером строки; на диск
	// ничего не идёт.
	compiled, err := importer.CompileLvns(req.Lvns)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{
			"ok": false, "stage": "compile", "error": err.Error(),
		})
		return
	}

	rel := fmt.Sprintf("scripts/%s-ch%02d.lvn", req.ID, req.Chapter)

	// 2. Тот же структурный гейт, через который проходит любая запись скрипта
	// (lvnguard.go). Отказ — до единой записи на диск, поэтому неудачная
	// публикация оставляет прошлую версию игры нетронутой.
	findings := s.checkLvn(rel, compiled)
	if findings.blocked() {
		writeJSON(w, http.StatusUnprocessableEntity, map[string]any{
			"ok": false, "stage": "check", "errors": orEmpty(findings.Errors),
			"warnings": orEmpty(findings.Warnings),
		})
		return
	}

	// 3. Запись. Исходник кладём рядом с результатом: он и есть то, что автор
	// (или ИИ) правит в следующий раз, и то, что открывает IDE.
	srcRel := strings.TrimSuffix(rel, ".lvn") + ".lvns"
	lk := s.writeLock()
	lk.Lock()
	werr := s.writeContentFile(srcRel, []byte(req.Lvns))
	if werr == nil {
		werr = s.writeContentFile(rel, compiled)
	}
	var mErr error
	if werr == nil {
		mErr = s.registerChapter(req, rel)
	}
	lk.Unlock()
	if werr != nil {
		http.Error(w, "write failed: "+werr.Error(), http.StatusInternalServerError)
		return
	}
	if mErr != nil {
		// Скрипт уже лежит и играбелен по прямой ссылке — врать про полный
		// успех нельзя, но и терять сделанное незачем.
		writeJSON(w, http.StatusOK, map[string]any{
			"ok": false, "stage": "manifest", "error": mErr.Error(),
			"script_url": "/content/" + rel,
			"warnings":   orEmpty(findings.Warnings),
		})
		return
	}

	var doc struct {
		Script []any `json:"script"`
	}
	_ = json.Unmarshal(compiled, &doc)
	writeJSON(w, http.StatusOK, map[string]any{
		"ok": true, "id": req.ID, "chapter": req.Chapter,
		"commands": len(doc.Script), "warnings": orEmpty(findings.Warnings),
		"script_url": "/content/" + rel,
		"play_url":   requestBase(r) + "/",
	})
}

// writeContentFile пишет файл под контент-корнем со снапшотом в историю.
// Вызывается ПОД замком записи (см. (*server).writeLock).
func (s *server) writeContentFile(rel string, data []byte) error {
	dst := filepath.Join(s.content, filepath.FromSlash(rel))
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	snapshotHistory(s.content, rel)
	return atomicWrite(dst, data, 0o644)
}

// registerChapter вписывает главу в манифест, создавая титул при первой
// публикации и ЗАМЕНЯЯ запись при повторной. Вызывается под замком записи.
//
// Манифест правится как обычный JSON-объект, а не через типизированную
// структуру: у титулов есть поля, о которых этот код не знает (обложки,
// экономика, разблокировки), и разбор в структуру потерял бы их при первой же
// публикации ребёнка.
func (s *server) registerChapter(req publishReq, scriptRel string) error {
	path := filepath.Join(s.content, "manifest.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		return fmt.Errorf("manifest.json is not valid JSON: %w", err)
	}
	titles, _ := m["titles"].([]any)

	var title map[string]any
	for _, t := range titles {
		if tm, ok := t.(map[string]any); ok && tm["id"] == req.ID {
			title = tm
			break
		}
	}
	if title == nil {
		title = map[string]any{"id": req.ID, "name": req.Name, "subtitle": ""}
		titles = append(titles, title)
	} else if req.Name != req.ID {
		title["name"] = req.Name // переименование при повторной публикации
	}

	seasons, _ := title["seasons"].([]any)
	if len(seasons) == 0 {
		seasons = []any{map[string]any{"chapters": []any{}}}
	}
	season, _ := seasons[0].(map[string]any)
	if season == nil {
		season = map[string]any{"chapters": []any{}}
		seasons[0] = season
	}
	chapters, _ := season["chapters"].([]any)

	entry := map[string]any{
		"id":         fmt.Sprintf("%s-ch%02d", req.ID, req.Chapter),
		"name":       fmt.Sprintf("%02d", req.Chapter),
		"number":     req.Chapter,
		"script_url": "/content/" + scriptRel,
	}
	if req.BgURL != "" {
		entry["bg_url"] = req.BgURL
	}
	replaced := false
	for i, c := range chapters {
		if cm, ok := c.(map[string]any); ok && cm["id"] == entry["id"] {
			// Сохраняем поля, которые проставили в панели руками (обложка,
			// имя главы) — публикация меняет скрипт, а не оформление.
			for k, v := range cm {
				if _, taken := entry[k]; !taken {
					entry[k] = v
				}
			}
			if nm, ok := cm["name"].(string); ok && nm != "" {
				entry["name"] = nm
			}
			chapters[i] = entry
			replaced = true
			break
		}
	}
	if !replaced {
		chapters = append(chapters, entry)
	}
	season["chapters"] = chapters
	seasons[0] = season
	title["seasons"] = seasons
	m["titles"] = titles

	out, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	snapshotHistory(s.content, "manifest.json")
	return atomicWrite(path, append(out, '\n'), 0o644)
}
