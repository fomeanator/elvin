package main

// `lvnconv locale -audit <content-dir>` — СДЕЛАННЫЙ ПЕРЕВОД ОБЯЗАН БЫТЬ ВИДЕН.
//
// Каталоги переводов лежат рядом со скриптами (`<script>.<lang>.json`), а
// список языков игры объявляет манифест (`languages`). Пока эти два места
// расходятся, работа переводчика существует только на диске: рантайм грузит
// каталог ТОЛЬКО для объявленного языка, а строка выбора языка в настройках
// показывается ТОЛЬКО когда языки объявлены.
//
// Так и вышло у Time Romance: двадцать восемь английских каталогов, почти
// полностью переведённых, и пустой `languages` в манифесте — переключателя в
// игре не было вовсе, и со стороны это выглядело как «двуязычие не сделано».
//
// Проверка отвечает на три вопроса разом: какие языки лежат на диске, какие
// объявлены и насколько каждый каталог полон (ключ, равный своему значению, —
// непереведённая строка, так их оставляет `lvnconv locale`).

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

type localeAudit struct {
	lang     string
	files    int
	keys     int
	untrans  int
	declared bool
}

func auditLocales(contentDir string) ([]localeAudit, []string, error) {
	scripts := filepath.Join(contentDir, "scripts")
	entries, err := os.ReadDir(scripts)
	if err != nil {
		return nil, nil, fmt.Errorf("нет каталога скриптов %s: %w", scripts, err)
	}

	byLang := map[string]*localeAudit{}
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".json") {
			continue
		}
		// <script>.<lang>.json — язык это ПРЕДПОСЛЕДНЯЯ часть имени.
		parts := strings.Split(strings.TrimSuffix(name, ".json"), ".")
		if len(parts) < 2 {
			continue
		}
		lang := parts[len(parts)-1]
		if len(lang) != 2 && len(lang) != 5 { // ru, en, pt-BR
			continue
		}
		raw, err := os.ReadFile(filepath.Join(scripts, name))
		if err != nil {
			continue
		}
		var cat map[string]string
		if json.Unmarshal(raw, &cat) != nil {
			continue
		}
		a := byLang[lang]
		if a == nil {
			a = &localeAudit{lang: lang}
			byLang[lang] = a
		}
		a.files++
		for k, v := range cat {
			a.keys++
			if k == v {
				a.untrans++ // так `lvnconv locale` помечает непереведённое
			}
		}
	}

	declared := []string{}
	if raw, err := os.ReadFile(filepath.Join(contentDir, "manifest.json")); err == nil {
		var man struct {
			Languages []string `json:"languages"`
		}
		if json.Unmarshal(raw, &man) == nil {
			declared = man.Languages
		}
	}
	for _, l := range declared {
		if a := byLang[l]; a != nil {
			a.declared = true
		} else {
			byLang[l] = &localeAudit{lang: l, declared: true}
		}
	}

	out := make([]localeAudit, 0, len(byLang))
	for _, a := range byLang {
		out = append(out, *a)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].lang < out[j].lang })
	return out, declared, nil
}

func cmdLocaleAudit(dir string) {
	rows, declared, err := auditLocales(dir)
	if err != nil {
		die("locale -audit: " + err.Error())
	}
	if len(rows) == 0 {
		fmt.Println("каталогов переводов нет и языки не объявлены")
		return
	}
	fmt.Printf("объявлено в манифесте: %v\n\n", declared)
	problems := 0
	for _, r := range rows {
		state := "объявлен"
		if !r.declared {
			state = "НЕ ОБЪЯВЛЕН — переключателя языка в игре не будет"
			problems++
		}
		if r.files == 0 {
			state = "ОБЪЯВЛЕН, НО КАТАЛОГОВ НЕТ — игрок выберет язык и получит оригинал"
			problems++
		}
		done := r.keys - r.untrans
		fmt.Printf("  %-6s файлов %-3d строк %-6d переведено %-6d (%d%%)  %s\n",
			r.lang, r.files, r.keys, done, pct(done, r.keys), state)
	}
	if problems > 0 {
		fmt.Printf("\nрасхождений: %d — работа переводчика существует только на диске\n", problems)
		os.Exit(1)
	}
}

func pct(a, b int) int {
	if b == 0 {
		return 0
	}
	return a * 100 / b
}
