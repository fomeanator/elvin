package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// МОЛЧАНИЕ ОБЯЗАНО БЫТЬ ПОДПИСАННЫМ.
//
// Пустой `catch { }` — самая дешёвая в написании и самая дорогая в чтении
// конструкция: по ней не отличить решение от забывчивости. А разница огромная:
// «удалить недокачанный файл не вышло — и ладно» это решение, а проглоченная
// ошибка загрузки главы — потерянная причина, которую потом ищут по симптомам.
//
// Ревизия 21.08 нашла 33 таких места. Восемь заменены на LvnQuiet.Try (вызов
// сам говорит «здесь молчим намеренно»), остальным дописано, ПОЧЕМУ молчим.
// Страж держит счёт на нуле: новое молчание должно быть объяснено там же, где
// написано.
var silentCatch = regexp.MustCompile(`catch\s*(\([^)]*\))?\s*\{\s*\}`)

func TestEveryEmptyCatchExplainsItself(t *testing.T) {
	root := capsRepoRoot()
	var offenders []string

	for _, rel := range dupRoots {
		dir := filepath.Join(root, rel)
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		_ = filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			data, rerr := os.ReadFile(path)
			if rerr != nil {
				return nil
			}
			lines := strings.Split(string(data), "\n")
			for i, l := range lines {
				if !silentCatch.MatchString(l) {
					continue
				}
				// Пояснением считается комментарий на этой же строке или
				// на строке выше: и то и другое читается вместе с catch.
				if strings.Contains(l, "//") {
					continue
				}
				if i > 0 && strings.Contains(lines[i-1], "//") {
					continue
				}
				offenders = append(offenders,
					filepath.Base(path)+":"+itoa(i+1)+"  "+strings.TrimSpace(l))
			}
			return nil
		})
	}
	sort.Strings(offenders)

	for _, o := range offenders {
		t.Errorf("молчаливый catch без объяснения — %s\n"+
			"    Напишите одной строкой, ПОЧЕМУ ошибка здесь не важна, либо\n"+
			"    используйте Lvn.LvnQuiet.Try — сам вызов и есть подпись под\n"+
			"    намеренным молчанием. Иначе не отличить решение от забывчивости.", o)
	}
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	var b []byte
	for n > 0 {
		b = append([]byte{byte('0' + n%10)}, b...)
		n /= 10
	}
	return string(b)
}
