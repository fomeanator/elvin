package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// Дома не обходят «мимо», даже когда обход выглядит безобидно.
//
// Роль считается выделенной, когда её позвали ВСЕ, а не когда она появилась.
// Проверять это удобнее не по имени дома (оно упомянуто везде и ни о чём не
// говорит), а по СЛЕДАМ ОБХОДА — коротким признакам, каждый из которых уже
// оказывался живым дефектом:
//
//   - `:N0` / ToString("N0") — число мимо Ценника: разделитель разрядов брался
//     из настроек телефона, и в одном окне цена не совпадала с балансом;
//   - `_cfg.*_text ?? "литерал"` — надпись, которую можно перевести ТОЛЬКО
//     конфигом экрана: ui.words её не видит;
//   - Inventory.ContainsKey — «есть вещь» по ключу, тогда как инвентарь считает
//     штуки и потраченная вещь остаётся ключом с нулём.
func TestHomesAreNotBypassed(t *testing.T) {
	root := repoRoot(t)
	type probe struct {
		re   *regexp.Regexp
		what string
		fix  string
		skip *regexp.Regexp
	}
	probes := []probe{
		{regexp.MustCompile(`ToString\("N0"\)|:N0\}`), "число мимо Ценника",
			"LvnPriceTag.Amount(...) — разделитель разрядов берётся из языка новеллы",
			regexp.MustCompile(`LvnNumberFormat|LvnPriceTag\.cs`)},
		{regexp.MustCompile(`_cfg\.[a-z_]+_(text|label|prompt|title) \?\? "[A-Za-zА-Яа-я]`),
			"надпись только из конфига",
			"добавьте LvnWords.Of(ключ, умолчание) — иначе ui.words эту надпись не видит",
			regexp.MustCompile(`LvnWords`)},
		{regexp.MustCompile(`Inventory\.ContainsKey`), "владение по наличию ключа",
			"LvnWallet.Has(sku) — «штук больше нуля»; ContainsKey значит «встречал когда-либо»",
			regexp.MustCompile(`НАРОЧНО`)},
	}

	var found []string
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
			rel, _ := filepath.Rel(root, path)
			lines := strings.Split(string(b), "\n")
			for i, line := range lines {
				code := line
				if c := strings.Index(code, "//"); c >= 0 {
					code = code[:c]
				}
				for _, p := range probes {
					if !p.re.MatchString(code) {
						continue
					}
					// Исключение считается подписанным, если оговорка стоит
					// в самой строке или в четырёх строках над ней: объяснение
					// «почему тут иначе» редко влезает в одну строку.
					ctx := line
					for j := i - 4; j < i && j >= 0; j++ {
						ctx += "\n" + lines[j]
					}
					if p.skip != nil && (p.skip.MatchString(ctx) || p.skip.MatchString(rel)) {
						continue
					}
					found = append(found, fmt.Sprintf("%s:%d [%s] %s\n      → %s",
						filepath.ToSlash(rel), i+1, p.what, strings.TrimSpace(line), p.fix))
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}
	if len(found) > 0 {
		t.Fatalf("дом обошли стороной:\n  %s", strings.Join(found, "\n  "))
	}
}
