package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ЛЕСТНИЦА СТУПЕНЕЙ АРТА НАЗВАНА В ЧЕТЫРЁХ МЕСТАХ.
//
// Докблок DownloadPolicy обещает «одним объявлением» и объясняет цену ошибки:
// «добавить ступень значило вспомнить все четыре, а забытый список молча
// оставлял бы новый бокс недосягаемым». Объявление действительно одно —
// константы, — но СПИСКИ, собранные из них, живут порознь:
//
//	DownloadPolicy.Variants          все суффиксы, встречающиеся в контенте
//	DownloadPolicy.QualityVariants   ступени ПОКАЗА, между которыми переключают
//	SettingsScreen.Device.cs         что предлагают игроку
//	LvnDeviceProfile.Advise          что советует устройство
//
// Разойтись они могут молча и по-разному: ступень, предложенная игроку, но
// отсутствующая среди ступеней показа, даст «арт не качается»; ступень в
// QualityVariants, которой нет в Variants, заставит чистку искать несуществующий
// бокс. Ни то ни другое не падает — просто перестаёт работать.
func TestСтупениАртаСогласованы(t *testing.T) {
	root := repoRoot(t)
	policy := readAll(t, root, "unity/Packages/com.lvn.engine/Runtime/Content/DownloadPolicy.cs")
	settings := readAll(t, root, "unity/Packages/com.lvn.engine.shell/Runtime/SettingsScreen.Device.cs")
	profile := readAll(t, root, "unity/Packages/com.lvn.engine/Runtime/LvnDeviceProfile.cs")

	// Имя константы → суффикс: Q2k = "@2k".
	consts := map[string]string{}
	for _, m := range regexp.MustCompile(`const string (Q\w+)\s*=\s*"([^"]+)"`).FindAllStringSubmatch(policy, -1) {
		consts[m[1]] = m[2]
	}
	if len(consts) == 0 {
		t.Fatal("не найдено ни одной константы ступени — страж потерял предмет охраны")
	}

	all := listOf(t, policy, "Variants", consts)
	display := listOf(t, policy, "QualityVariants", consts)

	// 1. Ступень показа обязана существовать в контенте.
	for _, d := range display {
		if !hasRung(all, d) {
			t.Errorf("ступень показа %q отсутствует в Variants — чистка будет искать бокс, которого нет", d)
		}
	}

	// 2. Что предлагают игроку — обязано быть ступенью показа.
	offered := offeredRungs(t, settings)
	for _, o := range offered {
		if !hasRung(display, "@"+o) {
			t.Errorf("настройки предлагают ступень %q, которой нет среди ступеней показа %v.\n"+
				"Игрок выберет её и получит «арт не качается» — молча.", o, display)
		}
	}

	// 3. Что советует устройство — тоже обязано быть ступенью показа: совет
	//    применяется БЕЗ участия игрока, и промах здесь не заметит никто.
	advised := advisedRungs(t, profile)
	if len(advised) == 0 {
		t.Fatal("не найдено ни одного совета устройства — страж потерял предмет охраны")
	}
	for _, a := range advised {
		if !hasRung(display, "@"+a) {
			t.Errorf("устройству советуется ступень %q, которой нет среди ступеней показа %v", a, display)
		}
	}

	// 4. Списки должны СОВПАСТЬ по составу: ступень показа, никому не
	//    предложенная, недостижима — а значит и не ступень.
	sort.Strings(offered)
	want := make([]string, 0, len(display))
	for _, d := range display {
		want = append(want, strings.TrimPrefix(d, "@"))
	}
	sort.Strings(want)
	if strings.Join(offered, ",") != strings.Join(want, ",") {
		t.Errorf("настройки предлагают %v, а ступеней показа %v — списки разошлись", offered, want)
	}
}

func readAll(t *testing.T, root, rel string) string {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(rel)))
	if err != nil {
		t.Fatalf("не читается %s: %v", rel, err)
	}
	return string(b)
}

// listOf разбирает `… Name = { Q2k, Q1440 };` в суффиксы.
func listOf(t *testing.T, src, name string, consts map[string]string) []string {
	t.Helper()
	re := regexp.MustCompile(name + `\s*=\s*\{([^}]*)\}`)
	m := re.FindStringSubmatch(src)
	if m == nil {
		t.Fatalf("не найден список %s", name)
	}
	var out []string
	for _, part := range strings.Split(m[1], ",") {
		key := strings.TrimSpace(part)
		if key == "" {
			continue
		}
		v, ok := consts[key]
		if !ok {
			t.Fatalf("%s ссылается на %q, а такой константы нет", name, key)
		}
		out = append(out, v)
	}
	return out
}

// offeredRungs достаёт первые элементы пар из `new[] { ("2k", "2K"), … }`.
func offeredRungs(t *testing.T, src string) []string {
	t.Helper()
	block := regexp.MustCompile(`new\[\]\s*\{\s*(\("[^"]+",\s*"[^"]+"\)\s*,?\s*)+\}`).FindString(src)
	if block == "" {
		t.Fatal("не найден список ступеней в настройках")
	}
	var out []string
	for _, m := range regexp.MustCompile(`\("([^"]+)",\s*"[^"]+"\)`).FindAllStringSubmatch(block, -1) {
		out = append(out, m[1])
	}
	return out
}

// advisedRungs достаёт строки, которые Advise() возвращает.
func advisedRungs(t *testing.T, src string) []string {
	t.Helper()
	i := strings.Index(src, "private static string Advise()")
	if i < 0 {
		t.Fatal("не найден Advise() — совет устройства переименован")
	}
	body := src[i:]
	if j := strings.Index(body, "\n        }"); j > 0 {
		body = body[:j]
	}
	var out []string
	for _, m := range regexp.MustCompile(`return\s+"([^"]+)"`).FindAllStringSubmatch(body, -1) {
		out = append(out, m[1])
	}
	return out
}

func hasRung(xs []string, want string) bool {
	for _, x := range xs {
		if x == want {
			return true
		}
	}
	return false
}
