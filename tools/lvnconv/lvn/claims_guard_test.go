package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ОГОВОРКА В ДОКУМЕНТАЦИИ — ЭТО УТВЕРЖДЕНИЕ О КОДЕ.
//
// Живой случай 01.09: у LvnPicture.Frame в документации стояло «способ
// написан, но не позван НИ РАЗУ… рамки до сих пор показываются простым
// растяжением». Обе половины были неправдой — зовут из двух мест, и рамки не
// растягиваются. Я поверил и удалил живой код; поймала компиляция, но поймать
// могло и вкладкой гардероба без рамки на чужом устройстве.
//
// Комментарий, бывший правдой вчера, врёт увереннее отсутствующего: у него
// есть авторитет места. Поэтому утверждение «этот способ никто не зовёт»
// проверяется так же, как код.
//
// Прошедшее время НЕ считается утверждением о сегодня: «были написаны и не
// позваны ни разу» — рассказ о починенном баге, и он остаётся правдой. Правило
// простое: заявляешь в настоящем — отвечай за настоящее; рассказываешь
// историю — скажи, что это история.
func TestNotCalledClaimsAreTrue(t *testing.T) {
	root := repoRoot(t)
	claim := regexp.MustCompile(`(не позван|не позвана|не позвано|никто не зовёт|нет ни одного вызова)`)
	// Прошедшее время ищем В САМОЙ СТРОКЕ и в следующей — там, где обычно
	// стоит «…и это была неправда». Окно шире (абзац) отфильтровало ВСЁ:
	// у любого дома рядом есть рассказ о том, как было раньше.
	past := regexp.MustCompile(`(был|была|были|было|раньше|прежде|тогда|оставал|выходило|значилось|стояло)`)
	ident := regexp.MustCompile(`<c>([\w.]+)</c>|<see cref="([\w.]+)"|` + "`" + `([\w.]+)` + "`")

	type claimed struct {
		file, name string
		line       int
	}
	var claims []claimed
	var files []string
	for _, rel := range storageRoots {
		err := filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			if strings.Contains(filepath.ToSlash(path), "/Tests/") {
				return nil
			}
			files = append(files, path)
			relPath, _ := filepath.Rel(root, path)
			lines := strings.Split(string(mustRead(t, path)), "\n")
			for i, l := range lines {
				trimmed := strings.TrimSpace(l)
				if !strings.HasPrefix(trimmed, "//") || !claim.MatchString(trimmed) {
					continue
				}
				here := trimmed
				if i+1 < len(lines) {
					here += " " + strings.TrimSpace(lines[i+1])
				}
				if past.MatchString(here) {
					continue // рассказ о прошлом — не утверждение о сегодня
				}
				// Имя способа часто стоит строкой выше или ниже — ищем в абзаце.
				lo, hi := i-2, i+2
				if lo < 0 {
					lo = 0
				}
				if hi >= len(lines) {
					hi = len(lines) - 1
				}
				para := strings.Join(lines[lo:hi+1], "\n")
				m := ident.FindStringSubmatch(para)
				if m == nil {
					continue // утверждение без имени способа проверить нечем
				}
				name := m[1] + m[2] + m[3]
				if dot := strings.LastIndex(name, "."); dot >= 0 {
					name = name[dot+1:]
				}
				claims = append(claims, claimed{filepath.ToSlash(relPath), name, i + 1})
			}
			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}
	atLeast(t, len(files), 60, "просмотренных файлов")

	// СЧИТАТЬ ВЫЗОВЫ ПО ИМЕНИ НЕЛЬЗЯ: `Show` объявлен у десятка экранов, и
	// первая версия стража насчитала GameHud.Show девять вызовов — чужих.
	// Проверяем только СТАТИЧЕСКИЕ способы домов: у них вызов всегда назван
	// типом (LvnPicture.Frame(...)), и счёт получается честным. Утверждение о
	// способе объекта проверить так нельзя — оно остаётся на совести автора,
	// и страж об этом молчит, а не делает вид, что проверил.
	var stale []string
	verified := 0
	for _, c := range claims {
		typeName := strings.SplitN(filepath.Base(c.file), ".", 2)[0]
		declRe := regexp.MustCompile(`static[^;{()]*\b` + regexp.QuoteMeta(c.name) + `\s*\(`)
		declared := false
		for _, path := range files {
			if filepath.ToSlash(path) != "" && strings.HasSuffix(filepath.ToSlash(path), c.file) {
				if declRe.MatchString(stripComments(string(mustRead(t, path)))) {
					declared = true
				}
			}
		}
		if !declared {
			continue // не статический способ этого дома — проверить нечем
		}
		verified++
		call := regexp.MustCompile(`\b` + regexp.QuoteMeta(typeName) + `\.` + regexp.QuoteMeta(c.name) + `\s*\(`)
		callers := 0
		for _, path := range files {
			for _, l := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
				if call.MatchString(l) {
					callers++
				}
			}
		}
		if callers > 0 {
			stale = append(stale, fmt.Sprintf("%s:%d — сказано, что «%s.%s» никто не зовёт, а вызовов %d",
				c.file, c.line, typeName, c.name, callers))
		}
	}
	if len(stale) > 0 {
		t.Errorf("устаревшие утверждения в документации (%d):\n  %s\n\n"+
			"Оговорка — это утверждение о коде. Либо приведите её в соответствие, либо "+
			"скажите, что это ИСТОРИЯ (прошедшее время), — тогда она снова правда.",
			len(stale), strings.Join(stale, "\n  "))
	}
	atLeast(t, len(claims), 1, "утверждений о невызванности")
}
