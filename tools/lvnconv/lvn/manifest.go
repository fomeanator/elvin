package lvn

import (
	"encoding/json"
	"fmt"
	"regexp"
	"strings"
)

// ПРОВЕРКА МАНИФЕСТА — по именам полей, а не по схеме.
//
// Скрипты проходят через структурный гейт сервера, манифест — нет: его пишут
// на диск после разбора JSON и всё. А это весь облик приложения: темы, цвета,
// экраны, подборки, оси гардероба. Опечатка в нём молча даёт умолчание, и
// автор видит не ошибку, а «почему-то не так».
//
// Схему манифеста перенести сюда нельзя — она живёт в C#-DTO (LvnUiConfig), и
// её дублирование стало бы очередным зеркалом, которое разойдётся. Но самые
// частые описки ловятся БЕЗ схемы, по КОНВЕНЦИИ ИМЁН: поле, кончающееся на
// `_color`, обязано быть цветом; поле `theme` — известной темой. Это ловит не
// всё, зато не врёт и не требует второй копии контракта.
//
// Всё здесь — ПРЕДУПРЕЖДЕНИЯ. Манифест с непонятным полем должен доехать до
// игрока (хост вправе класть туда своё), а вот молчать об этом не должен.

var reColorField = regexp.MustCompile(`_color$|^color$`)

// ColorWords — слова, которые понимает UiColor.Named в движке. Сверяется
// сторожем: словарь цвета один на весь язык, и проверка обязана знать тот же.
var ColorWords = []string{
	// токены темы
	"bg", "surface", "surface_hi", "panel", "text", "dim", "accent", "on_accent",
	"gold", "warn", "border", "veil", "clear",
	// имена движка
	"white", "black", "red", "blue", "green", "yellow", "cyan", "magenta",
	// мнемоники настроения
	"cold", "tint_cold", "warm", "tint_warm", "sepia",
}

// ManifestWords — закрытые словари полей манифеста. Ключ — ИМЯ ПОЛЯ, потому
// что схемы у нас нет; значения совпадают с тем, что читает рантайм через
// LvnAuthorWord.
var ManifestWords = map[string][]string{
	"theme":         {"midnight", "cyber", "cyberpunk", "romance"},
	"speaker_focus": {"dim", "solo"},
	"tap_burst":     {"hearts"},
	"appear":        appearWords,
	"box_appear":    appearWords,
}

var appearWords = []string{"fade", "rise", "pop", "slide_up", "up", "slide_down", "down",
	"slide_left", "left", "slide_right", "right", "drop", "unfold"}

// ManifestWordsByPath — для имён, которые СЛИШКОМ ОБЩИЕ, чтобы судить о них по
// одному слову. «mode» бывает и у анимации (`mode=queue`), поэтому закрытый
// список привязан к полному пути, а не к имени поля.
var ManifestWordsByPath = map[string][]string{
	"ui.hud.mode": {"full", "choices"},
}

// ValidateManifest проверяет то, что можно проверить без схемы.
func ValidateManifest(data []byte) []Issue {
	var root any
	if err := json.Unmarshal(data, &root); err != nil {
		return []Issue{{Index: -1, Op: "manifest", Sev: SevError, Msg: "не разбирается как JSON: " + err.Error()}}
	}
	var out []Issue
	var walk func(node any, path string, depth int)
	walk = func(node any, path string, depth int) {
		if depth > 24 {
			return
		}
		switch n := node.(type) {
		case map[string]any:
			for k, v := range n {
				here := k
				if path != "" {
					here = path + "." + k
				}
				if s, ok := v.(string); ok && s != "" {
					checkManifestValue(&out, here, k, s)
				}
				walk(v, here, depth+1)
			}
		case []any:
			for i, v := range n {
				walk(v, fmt.Sprintf("%s[%d]", path, i), depth+1)
			}
		}
	}
	walk(root, "", 0)
	return out
}

var reHex = regexp.MustCompile(`^#?[0-9a-fA-F]{3,8}$`)

func checkManifestValue(out *[]Issue, path, field, value string) {
	v := strings.TrimSpace(strings.ToLower(value))
	// Незакрытая подстановка — не опечатка: её ещё не подставили.
	if strings.Contains(value, "{") {
		return
	}
	if reColorField.MatchString(field) {
		if inSet(ColorWords, v) || reHex.MatchString(v) {
			return
		}
		msg := fmt.Sprintf("%s=%q — не цвет: ни слово словаря, ни шестнадцатеричная запись; экран возьмёт умолчание", path, value)
		if sg := suggest(v, ColorWords); sg != "" {
			msg += fmt.Sprintf(" — может быть %q?", sg)
		}
		*out = append(*out, Issue{Index: -1, Op: "manifest", Sev: SevWarning, Msg: msg})
		return
	}
	known, ok := ManifestWordsByPath[path]
	if !ok {
		known, ok = ManifestWords[field]
	}
	if ok {
		if inSet(known, v) {
			return
		}
		msg := fmt.Sprintf("%s=%q — такого значения нет, будет умолчание (известны: %s)",
			path, value, strings.Join(known, ", "))
		if sg := suggest(v, known); sg != "" {
			msg += fmt.Sprintf(" — может быть %q?", sg)
		}
		*out = append(*out, Issue{Index: -1, Op: "manifest", Sev: SevWarning, Msg: msg})
	}
}
