package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// НЕСКОЛЬКО ПАМЯТЕЙ ПОД ОДНИМ КЛЮЧОМ — ПРИЗНАК РАСЩЕПЛЁННОЙ СУЩНОСТИ.
//
// За один день 01.09 эта болезнь нашлась ЧЕТЫРЕЖДЫ: канал звука (пять памятей),
// скелет (четыре), слой фигуры (четыре), загрузка (четыре). Каждый раз ломалось
// одно и то же — уборка знала не про все памяти, — и каждый раз это было не
// видно, потому что список Clear'ов выглядит полным.
//
// Признак виден заранее и без компилятора: два и более поля-словаря с
// ОДИНАКОВЫМ типом ключа в одном файле. Сам по себе он не приговор — ключ может
// совпадать у разных сущностей, — но это ровно то место, где стоит спросить, не
// одна ли это сущность, разложенная по полкам.
//
// Порог тут «сколько ещё не разобрано», и остаток назван поимённо ниже.
func TestSplitMemoriesUnderOneKey(t *testing.T) {
	const budget = 3 // только вниз

	// ЧТО ОСТАЛОСЬ И ПОЧЕМУ:
	//
	//   ContentLoader.cs — пять памятей по адресу, но сущностей ДВЕ: запись
	//     каталога (_versions, _aliases, _seedIndex — приезжают целиком с
	//     манифестом) и запись загрузки (_underway, _notFound). Свести стоит
	//     второе: «404 до времени T» — состояние захода, а не каталога. Мешает
	//     сброс итогов пакета: он выбрасывает записи, которым нечего сказать, а
	//     память о 404 обязана пережить сброс.
	//
	//   WorldActor.cs — _layers уже одна запись; _frames приезжает целиком
	//     снаружи (каталог кадров, не состояние слоя); _channels и _queue
	//     ключуются КАНАЛОМ, а не слоем, и у них свой дом (AnimLanes).
	//
	//   VnStage.cs — _cast и _talkAnims ключуются сущностью и просятся в одну
	//     запись; _labels ключуется именем метки — другое пространство.
	root := repoRoot(t)
	decl := regexp.MustCompile(`(?m)^\s*(?:private|internal|protected)\s+(?:readonly\s+)?` +
		`(?:System\.Collections\.Generic\.)?(?:Dictionary|HashSet)<\s*([^,>]+?)\s*[,>]`)

	var split []string
	scanned := 0
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine.services/Runtime",
	} {
		for _, f := range csFiles(t, filepath.Join(root, dir)) {
			scanned++
			body := string(mustRead(t, f))
			byKey := map[string]int{}
			for _, m := range decl.FindAllStringSubmatch(body, -1) {
				// static-поля не в счёт: это таблицы-справочники, а не
				// состояние предмета.
				byKey[strings.TrimSpace(m[1])]++
			}
			for key, n := range byKey {
				if n >= 3 {
					split = append(split, filepath.Base(f)+" <"+key+">×"+itoa(n))
				}
			}
		}
	}
	sawSources(t, scanned, 300, "файлов")
	if len(split) > budget {
		t.Errorf("памятей под одним ключом стало больше (%d при пороге %d): %v\n"+
			"Спросите, не одна ли это сущность: за 01.09 такое ломалось четырежды, "+
			"и всегда одинаково — уборка знала не про все памяти.", len(split), budget, split)
	}
}
