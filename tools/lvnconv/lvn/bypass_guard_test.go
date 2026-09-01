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
//     штуки и потраченная вещь остаётся ключом с нулём;
//   - AddComponent<UIDocument>() без общих настроек панели — слой живёт в своём
//     масштабе, и тот же интерфейс на том же экране выходит другого размера.
func TestHomesAreNotBypassed(t *testing.T) {
	scanned := 0
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
		// Слой без ОБЩИХ настроек панели живёт в своём масштабе: тот же
		// интерфейс на том же экране получается другого размера, и заметно это
		// только глазами, на устройстве. Оговорка — упоминание LvnPanel.Shared
		// рядом (дом этажей ставит его сам).
		// Следы домов, заведённых 01.09. Каждый — не гипотеза: ровно так это и
		// было написано по месту, пока дома не появилось.
		// «@» встречается и в ключе кэша (url@version) — это другое понятие,
		// поэтому след ищет склейку именно со СТУПЕНЬЮ качества.
		{regexp.MustCompile(`"@" \+ \w*[Qq]uality`), "ступень арта склеена руками",
			"DownloadPolicy.SuffixFor(качество) — иначе разделитель нельзя переименовать: клиент попросит одно, сервер сделает другое",
			regexp.MustCompile(`DownloadPolicy\.cs`)},
		{regexp.MustCompile(`Contains\("(left|right|top|bottom)"\)\s*\?`), "якорь разобран на месте",
			"LvnAnchor.Percent(слово, умолчание) — правило одно, отличаться вправе только умолчание",
			regexp.MustCompile(`LvnAnchor\.cs`)},
		{regexp.MustCompile(`Mathf\.Min\(0?\.\d+f?,\s*\w+ - _?\w*[Ll]ast`), "шаг кадра посчитан на месте",
			"LvnClock.Step(ref отметка, потолок) — он же двигает отметку, а забыть вторую половину легко",
			regexp.MustCompile(`LvnClock\.cs`)},
		{regexp.MustCompile(`"/content"`), "приставка контента вписана строкой",
			"LvnAssetPath.ContentPrefix — это соглашение ЯЗЫКА, и сменить его наполовину нельзя",
			regexp.MustCompile(`LvnAssetPath\.cs`)},
		// Цена входа и область кросс-новелльных статов — деньги и прогресс.
		// Оба правила уже расходились по местам: ценник на карточке показывал
		// выдуманную «1», пока списывал гейт экономики совсем другое; ключ
		// области — единственная связь между новеллами, и вписанный строкой он
		// разошёлся бы молча.
		{regexp.MustCompile(`economy\.chapter_cost|economy\.chapter_currency|free_chapters`),
			"цена входа считается мимо дома",
			"LvnEntryPrice.OfChapter/OfTitle — показывающий и списывающий обязаны спрашивать одно место",
			regexp.MustCompile(`LvnEntryPrice\.cs|LvnManifest\.cs`)},
		{regexp.MustCompile(`"__global"`), "область общих статов вписана строкой",
			"LvnGlobalStats.ScopeId — это единственная связь между новеллами",
			regexp.MustCompile(`LvnGlobalStats\.cs`)},
		// Рост в метрах: доля экрана считается делением на высоту сцены, и это
		// правило уже спорило с долями, которые называет ставящий. Пока метры
		// читают у дома, спор решён один раз; прочитанные по месту, они снова
		// станут третьим мнением.
		{regexp.MustCompile(`\["meters"\]|\["height_m"\]`), "рост в метрах прочитан на месте",
			"LvnScale.MetersIn(команда) — и LvnScale.Fraction для доли кадра",
			regexp.MustCompile(`LvnScale\.cs`)},
		// Слово «глава» авторское: игра вправе звать её эпизодом или делом.
		// Литерал в подписи отменяет этот выбор молча.
		{regexp.MustCompile(`"(Глава|Эпизод|Дело) `), "слово «глава» вписано в подпись",
			"LvnCaptions.Chapter(глава) — слово выбирает игра полем ui.chapter_word",
			regexp.MustCompile(`LvnCaptions\.cs|LvnWords`)},
		{regexp.MustCompile(`AddComponent<UIDocument>\(\)`), "свой слой мимо общей панели",
			"LvnFloor.Open(имя, этаж) — он ставит документ, общие настройки и этаж разом",
			regexp.MustCompile(`LvnPanel\.Shared|LvnFloor\.cs`)},
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
			scanned++
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
					// Исключение считается подписанным, если оговорка стоит в
					// самой строке или РЯДОМ — в четырёх строках выше или ниже.
					// Ниже стало нужно, когда след «свой слой мимо общей
					// панели» поймал два законных места: там документ создают
					// строкой выше, а общие настройки ставят следующей.
					ctx := line
					for j := i - 4; j <= i+4; j++ {
						if j >= 0 && j < len(lines) && j != i {
							ctx += "\n" + lines[j]
						}
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
	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(found) > 0 {
		t.Fatalf("дом обошли стороной:\n  %s", strings.Join(found, "\n  "))
	}
}
