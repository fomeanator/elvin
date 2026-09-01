package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// СПОСОБ, КОТОРЫЙ НИГДЕ НЕ ЗОВУТ, ОБЪЯСНЯЕТ СЕБЯ.
//
// Правило про КЛАССЫ давно есть (`TestDormantClassesExplainThemselves`), а про
// способы не было — и напрасно: живой дом с мёртвой дверью выглядит здоровым.
// Живой случай: `LvnChrome.Tint` («перекрасить рамку, не трогая толщину»)
// завели, потому что копия этих строк жила приватно в двух экранах. Экраны с
// тех пор перевели на `Border`, где цвет и толщина идут вместе, — роль
// осталась без единого зовущего, и узнать об этом было неоткуда.
//
// Считать зовущих по имени МАЛО: способ передают ссылкой (`LvnForget.Register(…,
// LvnWallet.Forget)`) и зовут расширением (`title.FirstChapter()`). Первая
// версия счёта этого не знала и насчитала сто пять «мёртвых» способов вместо
// сорока шести — шестой за сутки случай, когда образец не знает написания.
//
// Порог только уменьшается. Уменьшать его можно двумя способами, и оба
// честные: подключить способ или подписать его словом НЕ ПОДКЛЮЧЁН с
// объяснением, чего он ждёт.
func TestDormantMethodsExplainThemselves(t *testing.T) {
	const budget = 4 // 01.09: 13 → 4. Две двери-дубля удалены, LvnUrl.Absolute подключён
	// (оболочка решала «уже адрес или ещё путь» подстрокой «://» — шире правды),
	// шесть подписаны словом НЕ ПОДКЛЮЧЁН с тем, чего ждут.
	// Card+Edge, и мимо собственного Round) и LvnPicture.Picture (Photo/Skin вписывают сами).
	// Счёт правился дважды: 46 → 17 (учли своих же соседей по файлу) и
	// 17 → 13 (имя класса стали брать из КОДА, а не из имени файла — файл
	// держит несколько классов, и LvnBackoff.DelaySeconds с четырьмя зовущими
	// числился спящим как «LvnNetwork.DelaySeconds»).
	// Первая версия счёта дала 46 — она исключала файл-объявитель и считала
	// мёртвыми внутренних помощников дома. Разница в двадцать девять имён:
	// порог, поставленный по кривому счёту, узаконивает несуществующий долг.

	root := repoRoot(t)
	var paths []string
	for _, rel := range storageRoots {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err == nil && !info.IsDir() && strings.HasSuffix(path, ".cs") {
				paths = append(paths, path)
			}
			return nil
		})
	}
	atLeast(t, len(paths), 60, "просмотренных файлов")

	src := map[string]string{}
	for _, p := range paths {
		src[p] = string(mustRead(t, p))
	}
	tests := ""
	_ = filepath.Walk(filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Tests"),
		func(path string, info os.FileInfo, err error) error {
			if err == nil && !info.IsDir() && strings.HasSuffix(path, ".cs") {
				tests += string(mustRead(t, path))
			}
			return nil
		})

	// ИМЯ КЛАССА — ИЗ КОДА, А НЕ ИЗ ИМЕНИ ФАЙЛА. Первая версия брала его у
	// файла, а файл держит несколько классов: `LvnNetwork.cs` — это
	// LvnNetworkStatus, LvnFetchException, LvnOfflineText и LvnBackoff. Страж
	// искал зовущих для «LvnNetwork.DelaySeconds», которого не существует, и
	// объявлял спящим живой `LvnBackoff.DelaySeconds` с четырьмя зовущими.
	classRe := regexp.MustCompile(`(?:public|internal)\s+(?:sealed\s+|static\s+|partial\s+|abstract\s+)*class\s+(\w+)`)
	sig := regexp.MustCompile(`public static ([\w<>\[\],.?]+) (\w+)\s*\(([^)]*)`)
	var dormant []string
	for p, s := range src {
		// Все объявления классов файла с их позициями: способ принадлежит
		// ближайшему объявлению ВЫШЕ него.
		type decl struct {
			at   int
			name string
		}
		var decls []decl
		for _, loc := range classRe.FindAllStringSubmatchIndex(s, -1) {
			decls = append(decls, decl{loc[0], s[loc[2]:loc[3]]})
		}
		for _, m := range sig.FindAllStringSubmatchIndex(s, -1) {
			name, args := s[m[4]:m[5]], s[m[6]:m[7]]
			cls := ""
			for _, d := range decls {
				if d.at < m[0] {
					cls = d.name
				}
			}
			if !strings.HasPrefix(cls, "Lvn") {
				continue
			}
			pats := []*regexp.Regexp{regexp.MustCompile(`\b` + cls + `\.` + name + `\b`)}
			if strings.HasPrefix(strings.TrimSpace(args), "this ") {
				pats = append(pats, regexp.MustCompile(`\.`+name+`\s*\(`))
			}
			used := false
			// СВОЙ ЖЕ ФАЙЛ СЧИТАЕТСЯ. Первая версия его исключала — и объявила
			// мёртвым `LvnScroll.DragToScroll`, который зовут соседние способы
			// того же дома. Способ, зовомый только внутри, — это вопрос «зачем
			// он публичный», а не «кто его зовёт»; путать их нельзя, иначе у
			// стража появляются ложные записи.
			if strings.Count(s, name+"(") > 1 {
				used = true
			}
			for q, t2 := range src {
				if q == p {
					continue
				}
				for _, pt := range pats {
					if pt.MatchString(t2) {
						used = true
					}
				}
			}
			for _, pt := range pats {
				if pt.MatchString(tests) {
					used = true
				}
			}
			if used {
				continue
			}
			// Подписанный спящий способ не считается долгом — так же, как класс.
			if idx := strings.Index(s, name+"("); idx > 0 {
				head := s[:idx]
				if k := strings.LastIndex(head, "/// <summary>"); k >= 0 && strings.Contains(head[k:], "НЕ ПОДКЛЮЧЁН") {
					continue
				}
			}
			dormant = append(dormant, cls+"."+name)
		}
	}
	sort.Strings(dormant)
	if len(dormant) > budget {
		t.Errorf("публичных способов без единого зовущего: %d при пороге %d\n  %s\n\n"+
			"Живой дом с мёртвой дверью выглядит здоровым. Подключите способ или подпишите его\n"+
			"словом НЕ ПОДКЛЮЧЁН с объяснением, чего он ждёт, — как это давно требуется от классов.",
			len(dormant), budget, strings.Join(dormant, ", "))
	}
}
