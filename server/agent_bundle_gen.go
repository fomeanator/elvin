package main

// agent_bundle_gen.go — сборка встроенного файла для ИИ из документации репозитория.
//
// Файл agent-bundle.md ГЕНЕРИРУЕТСЯ и лежит в git: на проде исходников нет,
// только бинарь, поэтому доки обязаны быть внутри него. Единственная опасность
// такой схемы — расхождение: кто-то правит howto/, а встроенная копия остаётся
// вчерашней и молча учит ИИ несуществующему синтаксису.
//
// Поэтому сборка живёт ЗДЕСЬ, в одной функции, и её же вызывает страж
// TestAgentBundleIsUpToDate. Пересобрать:
//
//	go test ./server -run TestAgentBundleIsUpToDate -update
//
// Порядок разделов — это порядок чтения: сначала одна страница, которой хватает
// на первую игру, потом полный справочник, потом рецепты. ИИ читает сверху.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// bundleParts — что и в каком порядке склеивается. Заголовок задаётся здесь, а
// не берётся из файла: у исходных документов свои H1, и шесть подряд идущих H1
// читаются как шесть разных документов, а не один справочник.
var bundleParts = []struct{ title, rel string }{
	{"Шпаргалка: весь язык на одной странице", "howto/CHEATSHEET.md"},
	{"С чего начать (модель, рабочий цикл, типичные ошибки)", "howto/AGENTS.md"},
	{"Полное описание языка", "howto/LANGUAGE.md"},
	{"Возможности движка и его пределы", "howto/CAPABILITIES.md"},
	{"Готовые приёмы", "howto/recipes.md"},
}

// BuildAgentBundle склеивает документацию в один самодостаточный файл.
// root — корень репозитория.
func BuildAgentBundle(root string) (string, error) {
	var b strings.Builder
	b.WriteString("<!-- СГЕНЕРИРОВАНО. Правь исходные файлы в howto/, затем пересобери:\n")
	b.WriteString("     go test ./server -run TestAgentBundleIsUpToDate -update -->\n\n")
	for _, p := range bundleParts {
		raw, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(p.rel)))
		if err != nil {
			return "", fmt.Errorf("agent bundle: %w", err)
		}
		b.WriteString("# " + p.title + "\n\n")
		b.WriteString("<!-- источник: " + p.rel + " -->\n\n")
		b.WriteString(demote(strings.TrimRight(string(raw), "\n")))
		b.WriteString("\n\n---\n\n")
	}
	return b.String(), nil
}

// demote опускает каждый заголовок исходного документа на уровень, чтобы
// внутри склейки они были подразделами своего раздела, а не конкурирующими
// корнями. Строки внутри ``` не трогаются: там # — это комментарий или
// решётка в тексте, а не заголовок.
func demote(md string) string {
	lines := strings.Split(md, "\n")
	inFence := false
	for i, l := range lines {
		if strings.HasPrefix(strings.TrimSpace(l), "```") {
			inFence = !inFence
			continue
		}
		if !inFence && strings.HasPrefix(l, "#") {
			lines[i] = "#" + l
		}
	}
	return strings.Join(lines, "\n")
}
