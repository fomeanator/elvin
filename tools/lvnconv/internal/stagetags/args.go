// Package stagetags разбирает аргументы постановочных тегов `# cmd: k=v k=v`.
//
// СИНТАКСИС ОДИН НА ВСЕ ВХОДЫ. Автор, переходящий из Inky в articy, пишет
// постановку одинаково — это заявлено прямо в коде обоих импортёров. Но сам
// разбор жил двумя копиями, слово в слово: articy держал его в tags.go, ink в
// convert.go.
//
// Копия тут особенно коварна. Расхождение не падает и не логируется: тег
// разберётся, просто «300» приедет строкой вместо числа, а `text="две слова"`
// потеряет кавычки. Ошибку увидит не импортёр, а плеер — и не сразу, а на той
// новелле, которую внесли через ДРУГОЙ вход. Один и тот же тег, два входа,
// разное поведение — самый дорогой вид расхождения, потому что искать его
// начинают в плеере.
package stagetags

import (
	"regexp"
	"strconv"
	"strings"
)

var spaces = regexp.MustCompile(`\s+`)

// SplitArgs режет строку аргументов по пробелам. Пусто — nil, а не срез из
// одной пустой строки: вызывающие проверяют длину.
func SplitArgs(s string) []string {
	s = strings.TrimSpace(s)
	if s == "" {
		return nil
	}
	return spaces.Split(s, -1)
}

// ApplyKV раскладывает поля `ключ=значение` в команду, приводя значения к их
// естественному типу. Поле без `=` пропускается молча: это позиционный
// аргумент, и разбирает его сам тег.
func ApplyKV(c map[string]any, fields []string) {
	for _, f := range fields {
		eq := strings.Index(f, "=")
		if eq <= 0 {
			continue
		}
		c[f[:eq]] = Coerce(f[eq+1:])
	}
}

// Coerce приводит строку к тому, чем она выглядит: true/false/null, целое,
// дробное, строка в кавычках — иначе как есть.
func Coerce(s string) any {
	switch s {
	case "true":
		return true
	case "false":
		return false
	case "null":
		return nil
	}
	if n, err := strconv.ParseInt(s, 10, 64); err == nil {
		return n
	}
	if f, err := strconv.ParseFloat(s, 64); err == nil {
		return f
	}
	if len(s) >= 2 && (s[0] == '"' && s[len(s)-1] == '"' || s[0] == '\'' && s[len(s)-1] == '\'') {
		return s[1 : len(s)-1]
	}
	return s
}
