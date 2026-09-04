package lvn

// ПОДПИСИ ИНТЕРФЕЙСА — РЕЕСТР ДВИЖКА И ЧТО ИЗ НЕГО НЕ ПЕРЕВЕДЕНО.
//
// Экран движка не пишет слов сам: подпись идёт через `LvnWords.Of(ключ,
// английское умолчание)`, а родное слово автор кладёт в манифест (`ui.words`).
// Умолчание английское намеренно — движок открытый и служит любым играм.
//
// Отсюда обещание и его цена. Автор, не заглядывавший в исходники движка, НЕ
// ЗНАЕТ списка ключей: пропущенный ключ ничем себя не проявляет, пока игрок не
// увидит английское слово посреди своего языка. Именно так и вышло с очередью
// загрузок: ключ появился в коде, в словарь его никто не добавил, и «Downloading
// 0 of 7» уехало игроку — узналось со скриншота.
//
// Реестр ниже — ответ на это: список ключей движка с английскими умолчаниями,
// собранный ИЗ КОДА и вшитый в инструмент. Он служит двум сторонам сразу:
// автору — как шаблон словаря (что вообще надо перевести), стражу — как
// договор (реестр не смеет отстать от кода).
//
// Учтён и второй способ назвать подпись: поле секции (`ui.wardrobe.menu_label`).
// Такие ключи вливает в словарь LvnAuthoredWords, и требовать их в `ui.words`
// нельзя — иначе отчёт врёт автору, который всё сделал правильно.

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

//go:embed ui-words.json
var uiWordsRegistry []byte

// UiWord — одна подпись движка: ключ словаря, английское умолчание и (если
// подпись можно задать полем секции манифеста) путь этого поля.
type UiWord struct {
	Key     string `json:"key"`
	Default string `json:"default"`
	Field   string `json:"field,omitempty"`
}

// UiWords — реестр, вшитый в инструмент. Порядок — по ключу.
func UiWords() []UiWord {
	var out []UiWord
	if err := json.Unmarshal(uiWordsRegistry, &out); err != nil {
		return nil
	}
	return out
}

// Те же три формы вызова, что сторожит TestOneKeyOneDefault: подпись всегда
// приходит с английским умолчанием рядом.
var reWordCall = regexp.MustCompile(`(?:LvnWords\.Of|LvnWords\.Pick|Word|\bL)\("([^"]+)",\s*"([^"]*)"`)

// Множественное число живёт НЕ под голым ключом: `Plural(ключ, n, one, other)`
// спрашивает `ключ.one` и `ключ.other`, а если автор задал ещё `.few` и `.many`
// — берёт русскую тройку. Реестр обязан называть ровно те ключи, которые автор
// впишет в словарь: сказать ему «задайте chapter» значило бы отправить его
// править то, чего движок не спрашивает.
var reWordPlural = regexp.MustCompile(`Plural\("([^"]+)",\s*[^,)]+,\s*"([^"]*)",\s*"([^"]*)"`)

// Поле секции, влитое в словарь: `Put(map, "menu.store", ui.store?.menu_label)`.
var reAuthoredWord = regexp.MustCompile(`Put\(map,\s*"([^"]+)",\s*ui\.([A-Za-z_]+)\?\.([A-Za-z_]+)\)`)

// ScanUiWords собирает реестр из исходников движка. root — корень репозитория.
//
// Смотрим Runtime трёх пакетов: подписи живут там, а Editor и тесты игроку
// ничего не показывают. Первое встреченное умолчание побеждает — то, что их у
// ключа не может быть двух, сторожит отдельный тест.
func ScanUiWords(root string) ([]UiWord, error) {
	defs := map[string]string{}
	fields := map[string]string{}
	note := func(key, def string) {
		if key == "" || strings.ContainsAny(key, "{} ") {
			return // склеенный на лету ключ — реестром не описывается
		}
		if _, ok := defs[key]; !ok {
			defs[key] = def
		}
	}
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			text := string(b)
			for _, m := range reWordCall.FindAllStringSubmatch(text, -1) {
				note(m[1], m[2])
			}
			for _, m := range reWordPlural.FindAllStringSubmatch(text, -1) {
				note(m[1]+".one", m[2])
				note(m[1]+".other", m[3])
			}
			for _, m := range reAuthoredWord.FindAllStringSubmatch(text, -1) {
				fields[m[1]] = m[2] + "." + m[3]
			}
			return nil
		})
		if err != nil {
			return nil, fmt.Errorf("обход %s: %w", pkg, err)
		}
	}
	// Ключ, который вливается полем, но нигде не спрашивается через Of, — тоже
	// подпись: её показывает экран, знающий про поле напрямую.
	for key := range fields {
		if _, ok := defs[key]; !ok {
			defs[key] = ""
		}
	}
	out := make([]UiWord, 0, len(defs))
	for key, def := range defs {
		out = append(out, UiWord{Key: key, Default: def, Field: fields[key]})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Key < out[j].Key })
	return out, nil
}

// UiWordsReport — чего проекту не хватает, чтобы интерфейс заговорил на его
// языке: сначала базовый словарь, потом каждый объявленный перевод.
type UiWordsReport struct {
	Total   int
	Missing []UiWord            // нет ни в словаре, ни подписями меню, ни полем
	Locales map[string][]string // язык → ключи, которых нет в его каталоге
}

// AuditUiWords сверяет реестр с манифестом проекта.
//
// Ключ считается покрытым, если автор назвал его ЛЮБЫМ из трёх способов —
// `ui.words`, `ui.menu.labels` или полем секции: движок сливает все три в один
// словарь, и требовать конкретный было бы придиркой к форме.
func AuditUiWords(manifest []byte, words []UiWord) (UiWordsReport, error) {
	rep := UiWordsReport{Total: len(words), Locales: map[string][]string{}}
	var doc struct {
		UI struct {
			Words        map[string]string            `json:"words"`
			WordsLocales map[string]map[string]string `json:"words_locales"`
			Menu         struct {
				Labels map[string]string `json:"labels"`
			} `json:"menu"`
		} `json:"ui"`
	}
	if err := json.Unmarshal(manifest, &doc); err != nil {
		return rep, fmt.Errorf("манифест не разобран: %w", err)
	}
	// Поля секций читаем сырым деревом: их пути реестр хранит строкой
	// («wardrobe.menu_label»), и заводить под каждое поле структуру значило бы
	// повторять описание манифеста третий раз.
	var raw map[string]any
	_ = json.Unmarshal(manifest, &raw)
	fieldSet := func(path string) bool {
		node, ok := raw["ui"].(map[string]any)
		if !ok {
			return false
		}
		parts := strings.Split(path, ".")
		for i, p := range parts {
			v, ok := node[p]
			if !ok {
				return false
			}
			if i == len(parts)-1 {
				s, ok := v.(string)
				return ok && s != ""
			}
			node, ok = v.(map[string]any)
			if !ok {
				return false
			}
		}
		return false
	}
	named := func(cat map[string]string, key string) bool {
		if cat == nil {
			return false
		}
		if _, ok := cat[key]; ok {
			return true
		}
		// Русская тройка вместо пары: `.other` не нужен тому, кто задал
		// `.few` и `.many` — движок в этом случае спрашивает их.
		if base, ok := strings.CutSuffix(key, ".other"); ok {
			_, few := cat[base+".few"]
			_, many := cat[base+".many"]
			return few && many
		}
		return false
	}
	for _, w := range words {
		if named(doc.UI.Words, w.Key) || named(doc.UI.Menu.Labels, w.Key) ||
			(w.Field != "" && fieldSet(w.Field)) {
			continue
		}
		rep.Missing = append(rep.Missing, w)
	}
	for lang, cat := range doc.UI.WordsLocales {
		var miss []string
		for _, w := range words {
			if !named(cat, w.Key) {
				miss = append(miss, w.Key)
			}
		}
		sort.Strings(miss)
		rep.Locales[lang] = miss
	}
	return rep, nil
}
