package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// «СКОЛЬКО СРАЗУ» — ОДИН ДОМ.
//
// Правило стояло в трёх местах и в каждом по своей верной причине: двенадцать
// мест в сети (потоки HTTP/2), три у распаковки (чтобы выгрузка в видеопамять
// не приезжала залпом), двенадцать/шесть/два у расписания главы (чтобы пара
// крупных файлов не заняла соединение). Три правильных числа — и ни одно не
// знало главного: ждёт ли этот файл поверхность ПРЯМО СЕЙЧАС.
//
// Из-за этого живая картинка стояла за фоновым прогревом не по невезению, а
// ПО УСТРОЙСТВУ: мест ровно столько, кто первым попросил — того и место.
//
// Страж держит границу: считать места умеет только LvnLane. Новый
// SemaphoreSlim в тракте загрузки — это четвёртое правило, которое опять не
// будет знать про ступень.
func TestSlotCountingLivesInOneHome(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime")
	home := filepath.Join(dir, "Content", "LvnLanes.cs")

	rogue := regexp.MustCompile(`new\s+(System\.Threading\.)?SemaphoreSlim\s*\(\s*(\d+)`)
	allowed := map[string]string{
		// Не полоса, а замок: «одна пачка за раз». Ширины у него нет.
		"Content/ContentLoader.Batch.cs": "_batchGate",
	}
	for _, f := range csFiles(t, dir) {
		if f == home {
			continue
		}
		body := stripComments(string(mustRead(t, f)))
		hits := rogue.FindAllStringSubmatch(body, -1)
		if len(hits) == 0 {
			continue
		}
		rel := filepath.ToSlash(strings.TrimPrefix(f, dir+"/"))
		if why, ok := allowed[rel]; ok && len(hits) == 1 && hits[0][2] == "1" {
			_ = why // замок на одно место — не полоса
			continue
		}
		t.Errorf("%s считает места сам (%d шт.) — это работа LvnLane. "+
			"Своя ширина в чужом доме не знает про ступень, и живое снова "+
			"встанет в очередь за фоновым прогревом", rel, len(hits))
	}
}

// ФОН ОБЪЯВЛЯЕТСЯ, ЖИВОЕ МОЛЧИТ.
//
// Умолчание «живое» выбрано намеренно: живых мест в движке — весь код, фоновых
// пять и они наперечёт. Но раз объявляться обязан фон, объявления должны БЫТЬ,
// иначе бронь охраняет пустоту: если ступень не назвал никто, все ступени
// живые и полоса ведёт себя ровно как обычный семафор.
func TestBackgroundWorkDeclaresItsRung(t *testing.T) {
	root := repoRoot(t)
	want := map[string]string{
		"unity/Packages/com.lvn.engine/Runtime/Content/AssetScheduler.cs": "LvnRung.CurrentChapter",
		"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Preload.cs":     "LvnRung.CurrentChapter",
		"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Spine.cs":       "LvnRung.FirstFrame",
		"unity/Packages/com.lvn.engine.shell/Runtime/NovelApp.Chapter.cs": "LvnRung.Spare",
	}
	for file, rung := range want {
		body := stripComments(string(mustRead(t, filepath.Join(root, file))))
		if !strings.Contains(body, rung) {
			t.Errorf("%s больше не объявляет свою ступень (%s) — "+
				"фоновая работа снова считается живой и занимает бронь", file, rung)
		}
	}
}

// БРОНЬ НЕ БЫВАЕТ ВО ВСЮ ШИРИНУ.
//
// Полоса, у которой бронь равна ширине, останавливает фон навсегда: мест для
// него нет ни одного. Ошибка тихая — очередь просто никогда не двигается, — и
// поэтому её ловят на объявлении.
func TestEveryLaneLeavesRoomForBackground(t *testing.T) {
	root := repoRoot(t)
	home := string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/LvnLanes.cs")))
	if !strings.Contains(home, "keptForLive >= width") {
		t.Error("LvnLane больше не отвергает бронь во всю ширину — такая полоса " +
			"молча останавливает фоновую очередь навсегда")
	}
	if !strings.Contains(home, "_current.Value ?? LvnRung.Live") {
		t.Error("умолчание ступени перестало быть «живое»: молчащего надо считать " +
			"видимым — забыть объявление должно стоить брони, а не показа")
	}
}

// УСТУПКА — НЕ ОТМЕНА.
//
// Полоса вправе попросить фоновый заход вернуть место живому. Для вызывающего
// при этом НИЧЕГО не происходит: он не видит ни ошибки, ни отмены — только
// более долгую загрузку. Отмена самого вызывающего остаётся отменой и уходит
// наверх.
//
// Слить их в одно означало бы либо «игрок вышел из главы, а она продолжает
// качать себя», либо «фоновый файл, которого попросили подвинуться, больше не
// приедет никогда». Оба признака есть, и различать их обязан тот, кто ловит.
func TestYieldingIsNotCancelling(t *testing.T) {
	root := repoRoot(t)
	fetch := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Fetch.cs"))))

	if !strings.Contains(fetch, "catch (OperationCanceledException) when (pass.Yielded && !ct.IsCancellationRequested)") {
		t.Error("заход за байтами больше не отличает уступку от отмены: " +
			"либо ушедшая глава продолжит качать себя, либо подвинувшийся " +
			"файл не приедет никогда")
	}
	if !strings.Contains(fetch, "if (pass.Yielded && !ct.IsCancellationRequested) continue;") {
		t.Error("оборванный по уступке запрос снова считается сбоем сети — " +
			"движок объявит офлайн на ровном месте")
	}

	lanes := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/LvnLanes.cs"))))
	if !strings.Contains(lanes, "if (_all.CurrentCount == 0) AskOneToYield();") {
		t.Error("живое больше не просит фон уступить — брони хватает одному " +
			"живому делу, а у актёра слоёв пять-восемь, и все живые")
	}
	// Просят ОДНОГО: обрушить фоновую очередь ради одного кадра — не размен.
	if !strings.Contains(lanes, "if (!s.Asked) { victim = s; break; }") {
		t.Error("уступить просят не одного и не самого давнего")
	}
}
