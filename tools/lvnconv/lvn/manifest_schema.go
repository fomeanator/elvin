package lvn

import (
	_ "embed"
	"encoding/json"
	"regexp"
	"strings"
)

// СХЕМА МАНИФЕСТА — СНЯТАЯ, А НЕ ПЕРЕПИСАННАЯ.
//
// Правда о полях живёт в C#-DTO (`LvnUiConfig.cs`): их читает Newtonsoft, и
// незнакомое имя он молча пропускает — `titel_color` не даёт ни ошибки, ни
// строчки в логе, просто цвет остаётся умолчанием.
//
// Переписать схему на Go значило бы завести очередное зеркало, которое
// разойдётся. Поэтому она СНИМАЕТСЯ генератором (`cmd/lvn-genschema`) и лежит
// рядом данными; свежесть держит страж — ровно как у сгенерированной
// grammar.js.

//go:embed manifest-fields.json
var manifestFieldsJSON []byte

// ManifestSchema: имя класса → { имя поля → имя типа }.
type ManifestSchema map[string]map[string]string

var manifestSchema ManifestSchema

func init() {
	_ = json.Unmarshal(manifestFieldsJSON, &manifestSchema)
}

var (
	reClass = regexp.MustCompile(`(?m)^\s*public (?:sealed )?class (\w+)`)
	// Поле МОЖЕТ ИМЕТЬ ЗНАЧЕНИЕ ПО УМОЛЧАНИЮ (`public float duration = 1f;`).
	// Без этого хвоста снимок терял такие поля, и гейт объявлял
	// несуществующими настоящие — ровно та ложная тревога, которую он должен
	// был убрать.
	reJSONName = regexp.MustCompile(`JsonProperty\("([^"]+)"\)`)
	reField    = regexp.MustCompile(`(?m)^\s*public ([\w<>,\s\.\?\[\]]+?)\s+(\w+)\s*(?:=[^;]*)?;`)
)

// ScrapeManifestSchema снимает «класс → поля» с исходника DTO.
//
// Разбор нарочно грубый и построчный: он видит ровно то, что видит
// Newtonsoft, — публичные поля. Свойства, методы и вложенные типы ему не
// нужны, а промах ловит страж (он же и требует перегенерации).
func ScrapeManifestSchema(src string) ManifestSchema {
	out := ManifestSchema{}
	lines := strings.Split(src, "\n")
	cur, alias := "", ""
	for _, ln := range lines {
		if m := reClass.FindStringSubmatch(ln); m != nil {
			cur = m[1]
			out[cur] = map[string]string{}
			continue
		}
		if cur == "" {
			continue
		}
		// ИМЯ В JSON МОЖЕТ ОТЛИЧАТЬСЯ от имени поля: `var` — ключевое слово C#,
		// и в DTO оно объявлено как storyVar с псевдонимом. Схема обязана
		// знать то имя, которое пишет АВТОР, иначе гейт объявит несуществующим
		// поле из живого манифеста.
		if m := reJSONName.FindStringSubmatch(ln); m != nil {
			alias = m[1]
			continue
		}
		if m := reField.FindStringSubmatch(ln); m != nil {
			typ := strings.TrimSpace(m[1])
			// `public static readonly` и прочее — не поля данных.
			if strings.Contains(typ, "static") || strings.Contains(typ, "const") ||
				strings.Contains(typ, "readonly") {
				continue
			}
			name := m[2]
			if alias != "" {
				name, alias = alias, ""
			}
			out[cur][name] = simpleType(typ)
		}
	}
	return out
}

// simpleType сводит объявление к имени типа, по которому можно спуститься
// глубже: `BrowseConfig`, `List<LvnTitle>` → `LvnTitle`.
//
// СЛОВАРЬ ПОМЕЧАЕТСЯ ОСОБО (`map:LvnSpriteEntity`). У него ключи авторские —
// это ИМЕНА ГЕРОЕВ, а не поля, и проверять их по схеме нельзя: гейт объявил бы
// несуществующими всех персонажей игры. Проверять надо ЗНАЧЕНИЯ, спускаясь
// внутрь с типом значения.
func simpleType(t string) string {
	t = strings.TrimSuffix(strings.TrimSpace(t), "?")
	isMap := strings.Contains(t, "Dictionary<")
	if isMap {
		return "map:" + simpleInner(t)
	}
	return simpleInner(t)
}

func simpleInner(t string) string {
	t = strings.TrimSuffix(strings.TrimSpace(t), "?")
	if i := strings.LastIndex(t, "<"); i >= 0 {
		inner := t[i+1:]
		inner = strings.TrimSuffix(inner, ">")
		parts := strings.Split(inner, ",")
		t = strings.TrimSpace(parts[len(parts)-1])
		t = strings.TrimSuffix(t, ">")
	}
	if i := strings.LastIndex(t, "."); i >= 0 {
		t = t[i+1:]
	}
	return strings.TrimSpace(strings.TrimSuffix(t, "[]"))
}
