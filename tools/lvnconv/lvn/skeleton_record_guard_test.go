package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// СКЕЛЕТ — ОДНА ЗАПИСЬ, А НЕ ЧЕТЫРЕ ПАМЯТИ.
//
// Про каждый скелет знали врозь: построенный объект, отметка «строится»,
// отложенный проигрыш и место в порядке давности. Уборка по вытеснению знала
// про три из четырёх, а уборка сцены — про три ДРУГИХ из четырёх: место в
// порядке давности не чистил никто, и мёртвые имена занимали ёмкость живых.
//
// Тот же урок уже записан про мизансцену (WorldStage.Slot): пять памятей по
// одному ключу и уборка, делящая их на группы вручную. Список ломается не
// сегодня — он ломается на следующей памяти, которую в него забудут внести.
func TestSkeletonIsOneRecord(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime")
	for _, gone := range []string{"_spineActors", "_spineLoading", "_spinePendingPlay"} {
		for _, f := range csFiles(t, dir) {
			if strings.Contains(stripComments(string(mustRead(t, f))), gone) {
				t.Errorf("%s снова держит %s — это память про скелет ВРОЗЬ от "+
					"остальных, и очередная уборка узнает не про все",
					filepath.Base(f), gone)
			}
		}
	}

	spine := stripComments(string(mustRead(t, filepath.Join(dir, "UI/VnStage.Spine.cs"))))
	if !strings.Contains(spine, "private void ForgetSkeleton(string id)") {
		t.Fatal("исчезла одна дверь «забыть скелет целиком» — уборка снова " +
			"будет тремя строками, из которых одну забудут")
	}
	// СМОТРЕТЬ НАДО В ТЕЛО, А НЕ В ФАЙЛ. Первая версия этого стража искала
	// строки по всему файлу и не заметила подделки: «_spineMru.Remove(id)»
	// есть и в TouchSpine, поэтому убрать её из ForgetSkeleton можно было
	// незаметно для проверки. Страж, ищущий подстроку в файле, отвечает на
	// вопрос «встречается ли она», а спрашивали «делает ли это ВОТ ЭТОТ дом».
	body := spine[strings.Index(spine, "private void ForgetSkeleton(string id)"):]
	if end := strings.Index(body, "\n        }"); end > 0 {
		body = body[:end]
	}
	for _, part := range []string{"_skeletons.Remove(id)", "_spineMru.Remove(id)", "UnpinSpinePages(id)"} {
		if !strings.Contains(body, part) {
			t.Errorf("ForgetSkeleton больше не убирает %s — забытая половина "+
				"скелета занимает ёмкость живых", part)
		}
	}
}

// УБОРКА СЦЕНЫ ЗАБИРАЕТ И ПОРЯДОК ДАВНОСТИ.
//
// ResetStage чистил объекты, отметки и отложенные проигрыши — три Clear
// подряд, — а список давности не трогал. Имена умерших скелетов оставались в
// нём и занимали ёмкость (четыре живых), из-за чего вытеснение принималось за
// работу раньше, чем нужно: скелет уходил из памяти, пока его ещё показывали
// в следующей главе.
func TestChapterResetForgetsSkeletonOrderToo(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Playback.cs"))))
	i := strings.Index(body, "_skeletons.Clear()")
	if i < 0 {
		t.Fatal("уборка сцены больше не чистит записи скелетов")
	}
	if !strings.Contains(body, "_spineMru.Clear()") {
		t.Error("уборка сцены не чистит порядок давности: мёртвые имена займут " +
			"ёмкость живых, и вытеснение сработает раньше времени")
	}
}
