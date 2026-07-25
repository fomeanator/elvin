package adpd

import (
	"encoding/binary"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"testing"
)

// Регрессии связности графа .adpd. Все три бага ниже пойманы на живых партнёрских
// проектах (Cold / Inaweb) и каждый из них ТИХО терял сюжет: реплики оставались в
// файле, но поток до них не доходил, а линеаризатор подклеивал их наугад. Поэтому
// фикстуры воспроизводят именно ту байтовую форму, которая ломалась.

// putTag8 пишет свойство с произвольным тегом и 4-байтовым значением.
func putTag8(b []byte, seq, pid uint16, tag byte, v uint32) []byte {
	b = binary.LittleEndian.AppendUint16(b, seq)
	b = binary.LittleEndian.AppendUint16(b, pid)
	b = append(b, tag)
	return binary.LittleEndian.AppendUint32(b, v)
}

// connHashValue — значение pConnHash из живого Cold (связь 63878). Байты важны:
// 03 fe 30 b8. Именно на них побайтовый resync попадал в ЛОЖНОЕ свойство
// (seq=9, pid=0x030a, tag=0xfe) и проглатывал следующий за ним src. Значение, у
// которого второй байт не совпадает с известным тегом, восстанавливается само —
// поэтому фикстура обязана быть именно такой, иначе тест ничего не охраняет.
const connHashValue = uint32(0xb830_fe03)

// connFull строит связь так, как её пишет articy: перед четвёркой
// [src,dst,srcPin,dstPin] стоит свойство pConnHash с тегом 0x0a.
func connFull(par, src, dst, srcPin, dstPin uint32) []byte {
	var e []byte
	e = putTag8(e, 1, pConnHash, 0x0a, connHashValue)
	e = putU32(e, 1, pConn, src)
	e = putU32(e, 2, pConn, dst)
	e = putU32(e, 3, pConn, srcPin)
	e = putU32(e, 4, pConn, dstPin)
	e = putU32(e, 1, pParent, par)
	return obj(cidConnection, e)
}

func pinObj(self, owner uint32) []byte {
	var e []byte
	e = putU32(e, 1, pSelf, self)
	e = putU32(e, 1, pParent, owner)
	return obj(cidPin, e)
}

func node(cid uint16, self, parent uint32) []byte {
	var e []byte
	e = putU32(e, 1, pSelf, self)
	e = putU32(e, 1, pParent, parent)
	return obj(cid, e)
}

// jumpNode строит Jump: выходной пин у него ЕСТЬ, но связей из него нет, а цель
// лежит ссылкой pJumpTarget на пин узла-цели.
func jumpNode(self, parent, targetPin uint32) []byte {
	var e []byte
	e = putU32(e, 1, pSelf, self)
	e = putU32(e, 1, pJumpTarget, 9999) // ModelDependency-обёртка: не пин, игнорируется
	e = putU32(e, 2, pJumpTarget, targetPin)
	e = putU32(e, 1, pParent, parent)
	return obj(cidJump, e)
}

// Свойство с тегом 0x0a перед ref'ами связи не должно съедать src. До фикса
// entries() уходил в побайтовый resync и восстанавливался ПОСЛЕ src: связь
// приходила с 3 ref'ами и молча отбрасывалась (len(r) >= 4). На Cold так рвались
// 11 связей, и Эпизод 11 обрывался на 14-й реплике из 739.
func TestConnectionSrcSurvivesLeadingHashProperty(t *testing.T) {
	d := partition(
		node(cidDialog, 10, 1),
		pinObj(100, 10), pinObj(101, 10),
		node(cidDialogFrag, 20, 10), pinObj(200, 20), pinObj(201, 20),
		frag(20, "g-20", "Готово."),
		node(cidDialogFrag, 21, 10), pinObj(210, 21), pinObj(211, 21),
		frag(21, "g-21", "Затем поднял руки."),
		connFull(10, 20, 21, 201, 210),
	)
	fl := decodeFlow(d)
	es := fl.succ[20]
	if len(es) != 1 || es[0].src != 20 || es[0].dst != 21 || es[0].srcPin != 201 {
		t.Fatalf("связь 20→21 не раскодирована: %+v", es)
	}
	if got := fl.pg.outPin[201]; len(got) != 1 || got[0].dst != 21 || got[0].dstPin != 210 {
		t.Fatalf("pin-граф не получил ребро с пина 201: %+v", got)
	}
	// И поток от 20 должен доходить до 21 — то, чего не было на проде.
	if fwd := forwardEdges(fl.pg, 20, nil); len(fwd) != 1 || fwd[0].dst != 21 {
		t.Fatalf("форвардный поток из 20 = %+v, ждём один переход в 21", fwd)
	}
}

// Jump не соединён связью: его выходной пин пуст, цель — ссылка на пин. До фикса
// поток на каждом Jump кончался (в Cold так обрывалась ветка смерти, которая по
// замыслу возвращает игрока на чекпойнт).
func TestJumpFollowsTargetPin(t *testing.T) {
	d := partition(
		node(cidDialog, 10, 1),
		pinObj(100, 10), pinObj(101, 10),
		node(cidOutcome, 15, 10), pinObj(150, 15), pinObj(151, 15), // чекпойнт-цель
		node(cidDialogFrag, 20, 10), pinObj(200, 20), pinObj(201, 20),
		frag(20, "g-20", "Вас убили! Попробуйте ещё раз."),
		jumpNode(30, 10, 150), pinObj(300, 30), pinObj(301, 30),
		connFull(10, 20, 30, 201, 300), // реплика → Jump
	)
	fl := decodeFlow(d)
	if e, ok := fl.pg.jumpTo[30]; !ok || e.dst != 15 || e.dstPin != 150 {
		t.Fatalf("цель Jump не раскодирована: %+v ok=%v", e, ok)
	}
	if tot, res := fl.pg.jumpStats(); tot != 1 || res != 1 {
		t.Fatalf("jumpStats = %d/%d, ждём 1/1", res, tot)
	}
	fwd := forwardEdges(fl.pg, 20, nil)
	if len(fwd) != 1 || fwd[0].dst != 15 {
		t.Fatalf("поток из 20 через Jump = %+v, ждём переход в 15", fwd)
	}
}

// Узел, у которого pParent не раскодировался, раньше не попадал ни в одну главу и
// РВАЛ поток на границе scope'а. Родителя достаём из объекта инцидентной связи:
// она лежит в том же контейнере, и её собственный pParent читается нормально.
func TestParentInferredFromIncidentConnection(t *testing.T) {
	orphan := func(self uint32) []byte { // узел БЕЗ pParent
		var e []byte
		e = putU32(e, 1, pSelf, self)
		return obj(cidDialogFrag, e)
	}
	d := partition(
		node(cidFlowFrag, 5, 1),
		node(cidDialog, 10, 5),
		pinObj(100, 10), pinObj(101, 10),
		node(cidDialogFrag, 20, 10), pinObj(200, 20), pinObj(201, 20),
		orphan(21), pinObj(210, 21), pinObj(211, 21),
		frag(21, "g-21", "Реплика без родителя."),
		node(cidDialogFrag, 22, 10), pinObj(220, 22), pinObj(221, 22),
		connFull(10, 20, 21, 201, 210),
		connFull(10, 21, 22, 211, 220),
	)
	fl := decodeFlow(d)
	if par, ok := fl.parentOf[21]; !ok || par != 10 {
		t.Fatalf("родитель узла 21 = %d (ok=%v), ждём 10", par, ok)
	}
	if fl.inferred != 1 {
		t.Errorf("inferred = %d, ждём 1", fl.inferred)
	}
	// Внутри поддерева главы узел теперь свой — поток не обрывается на нём.
	allowed := subtree(completeChildren(fl), 5)
	if !allowed[21] {
		t.Fatal("узел 21 всё ещё вне поддерева главы")
	}
	if fwd := forwardEdges(fl.pg, 20, allowed); len(fwd) != 1 || fwd[0].dst != 21 {
		t.Fatalf("поток 20→21 в scope главы = %+v", fwd)
	}
}

// Контейнер, потерявший pSelf, не регистрируется — и его дети выпадают из
// экспорта целиком (на Inaweb так пропали 7 реплик допроса). Ordinal контейнера
// восстанавливаем голосованием его детей по их pParent.
func TestContainerSelfRecoveredFromChildren(t *testing.T) {
	headless := func(children ...uint32) []byte { // Dialog БЕЗ pSelf
		var e []byte
		e = putU32(e, 1, pParent, 5)
		for i, c := range children {
			e = putU32(e, uint16(i+1), pChild, c)
		}
		return obj(cidDialog, e)
	}
	d := partition(
		node(cidFlowFrag, 5, 1),
		headless(20, 21),
		pinObj(100, 10), pinObj(101, 10), // пины ссылаются на ordinal 10 напрямую
		node(cidDialogFrag, 20, 10), pinObj(200, 20), pinObj(201, 20),
		frag(20, "g-20", "Говард обвиняет Линча?"),
		node(cidDialogFrag, 21, 10), pinObj(210, 21), pinObj(211, 21),
		frag(21, "g-21", "Не надо."),
		connFull(10, 20, 21, 201, 210),
	)
	fl := decodeFlow(d)
	if fl.pg.class[10] != cidDialog {
		t.Fatalf("контейнер 10 не восстановлен: class=%d", fl.pg.class[10])
	}
	if !fl.contSet[10] {
		t.Error("контейнер 10 не попал в contSet")
	}
	allowed := subtree(completeChildren(fl), 5)
	if !allowed[20] || !allowed[21] {
		t.Fatal("дети восстановленного контейнера всё ещё вне главы")
	}
}

// Ordinal 0 законен: в Cold это ПЕРВАЯ глава. Проверка «self != 0» вместо явного
// флага «ordinal известен» стоила ровно одной главы из 25 — тихо, глава просто
// не появлялась в списке. Тест держит именно этот случай: глава с ordinal 0 должна
// находиться detectChapters'ом вместе со своим содержимым.
func TestChapterWithOrdinalZeroIsDetected(t *testing.T) {
	chapter := func(self uint32, name string) []byte {
		var e []byte
		e = putU32(e, 1, pSelf, self)
		e = putU32(e, 1, pParent, 99) // общий root
		return append(obj(cidFlowFrag, e), frag(self, fmt.Sprintf("g-ch%d", self), name)...)
	}
	scene := func(self, chap, beat uint32, text string) []byte {
		out := node(cidDialog, self, chap)
		out = append(out, pinObj(self*10, self)...)
		out = append(out, pinObj(self*10+1, self)...)
		out = append(out, node(cidDialogFrag, beat, self)...)
		out = append(out, pinObj(beat*10, beat)...)
		out = append(out, pinObj(beat*10+1, beat)...)
		return append(out, frag(beat, fmt.Sprintf("g-%d", beat), text)...)
	}
	storyRoot := func() []byte { // корень проекта — сам без родителя
		var e []byte
		e = putU32(e, 1, pSelf, 99)
		return obj(cidStoryFolder, e)
	}
	d := partition(
		storyRoot(),
		chapter(0, "Эпизод 1. Первая."),
		scene(10, 0, 200, "Реплика первой главы."),
		chapter(1, "Эпизод 2. Вторая."),
		scene(11, 1, 210, "Реплика второй главы."),
	)
	fl := decodeFlow(d)
	if par, ok := fl.parentOf[0]; !ok || par != 99 {
		t.Fatalf("parentOf[0] = %d (ok=%v), ждём 99 — глава с ordinal 0 потеряла родителя", par, ok)
	}
	chs := detectChapters(fl)
	if len(chs) != 2 {
		t.Fatalf("найдено %d глав из 2: %+v", len(chs), chs)
	}
	if chs[0].root != 0 {
		t.Errorf("первая глава = %d, ждём ordinal 0", chs[0].root)
	}
	if !subtree(completeChildren(fl), 0)[200] {
		t.Error("реплика первой главы вне её поддерева")
	}
}

// ── воспроизводимое измерение связности на живых проектах ────────────────────

// TestConnectivityOnRealProjects — гейт связности на настоящих articy-проектах.
// Для каждой главы считает, до какой доли реплик доходит форвардный pin-flow
// articy САМ, без синтетической штопки, и есть ли реплики, не попавшие ни в одну
// главу (полная потеря для игрока). Это та цифра, которую надо мерить: счётчик
// Trapped на всех живых проектах равен нулю и выглядел здоровым, пока граф был
// разорван на куски.
//
//	ARTICY_PROJECTS=<dir с распакованными проектами> go test ./internal/adpd -run TestConnectivity -v
func TestConnectivityOnRealProjects(t *testing.T) {
	root := os.Getenv("ARTICY_PROJECTS")
	if root == "" {
		t.Skip("set ARTICY_PROJECTS=<dir of extracted articy projects>")
	}
	var projects []string
	_ = filepath.Walk(root, func(p string, info os.FileInfo, err error) error {
		if err == nil && info.IsDir() && info.Name() == "Partitions" {
			projects = append(projects, filepath.Dir(p))
		}
		return nil
	})
	if len(projects) == 0 {
		t.Fatalf("no projects (dirs with a Partitions/ subdir) under %s", root)
	}
	sort.Strings(projects)
	for _, proj := range projects {
		t.Run(filepath.Base(proj), func(t *testing.T) {
			fl, _, err := loadFlow(proj)
			if err != nil {
				t.Fatalf("loadFlow: %v", err)
			}
			chs := detectChapters(fl)
			if len(chs) == 0 {
				t.Skip("не главированный проект")
			}
			kids := completeChildren(fl)
			scopes := make([]scopeDef, 0, len(chs))
			for _, ch := range chs {
				scopes = append(scopes, scopeDef{name: ch.name, root: ch.root, allowed: subtree(kids, ch.root)})
			}
			rep := &LinearizeReport{}
			rep.connectivity(fl, scopes)
			total := rep.Reachable + rep.Stitched
			pct := 100 * float64(rep.Reachable) / float64(total)
			t.Log(fmt.Sprintf("%d глав, %d реплик: поток articy доходит до %.2f%% (штопка: %d), сирот: %d, jumps: %d/%d, родителей достроено: %d",
				len(chs), total, pct, rep.Stitched, rep.Orphans, rep.JumpsResolved, rep.Jumps, rep.InferredParents))
			for _, w := range rep.Warnings {
				t.Log("  " + w)
			}
			// Граф articy обязан связывать главу целиком: штопка означает, что порядок
			// и ветвление в этом месте придуманы нами.
			if pct < 99.9 {
				t.Errorf("связность %.2f%% — %d реплик дотянуты синтетическим переходом", pct, rep.Stitched)
			}
			if rep.Orphans > 0 {
				t.Errorf("%d реплик не принадлежат ни одной главе — потеря сюжета", rep.Orphans)
			}
			if rep.Jumps != rep.JumpsResolved {
				t.Errorf("%d из %d Jump не раскодированы — на них поток обрывается", rep.Jumps-rep.JumpsResolved, rep.Jumps)
			}
		})
	}
}
