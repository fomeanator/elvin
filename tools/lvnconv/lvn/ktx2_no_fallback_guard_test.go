package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// «ЕЩЁ НЕ ЗАКОДИРОВАН» — НЕ «НЕТ НИКОГДА».
//
// Сервер кодирует .ktx2 лениво и на первый запрос честно отвечает «пока нет».
// Клиент дважды читал этот ответ как приговор: сперва сессионной защёлкой
// («первый промах гасит весь тракт»), потом счётчиком восьми промахов подряд.
// Второй костыль выглядел мягче первого и был ровно таким же: на холодном
// старте холодных файлов ВСЕГДА больше восьми, значит защёлка срабатывала
// каждый раз.
//
// Цена — вся разница между 110 мс распаковки через ktx2 и 1,2–3,7 с через
// PNG, на каждый слой героини (живой лог 01.09). Быстрый формат при этом
// числился сделанным: 62 файла @2k.ktx2 в каталоге и почти ни одного показа
// через них.
//
// Костыль был удобнее беды: он делал поломку незаметной.
func TestKtx2HasNoGiveUpLatch(t *testing.T) {
	root := repoRoot(t)
	f := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Ktx2.cs")
	body := stripComments(string(mustRead(t, f)))

	for _, latch := range []string{"GiveUpAfterMisses", "_ktx2MissStreak", "_ktx2Unavailable"} {
		if strings.Contains(body, latch) {
			t.Errorf("ContentLoader.Ktx2.cs: вернулась защёлка «сдаёмся на растр» (%s). "+
				"Холодный код — это ПОВТОРИТЬ ПОЗЖЕ, а не переключиться на медленный путь навсегда",
				latch)
		}
	}
	// Единственное честное «нельзя» — видеокарта, которая ktx2 не рисует.
	if !strings.Contains(body, "_gpuWithoutKtx2") {
		t.Error("ContentLoader.Ktx2.cs: пропала единственная законная причина отказа — " +
			"видеокарта без поддержки формата")
	}
	// Память о холоде обязана таять: пришёл хоть один код — очередь движется.
	if !strings.Contains(body, "_ktx2Cold.Clear()") {
		t.Error("ContentLoader.Ktx2.cs: холодные адреса больше не забываются после первого " +
			"попадания — один промах снова запирает адрес до перезапуска")
	}
}

// ХОЛОДНЫЙ КОД ЖДУТ, А НЕ ХОРОНЯТ.
//
// Растровой подстраховки у арта истории больше нет, и это верно. Но пока она
// была, «кода ещё нет» стоило одного медленного показа; без неё тот же ответ
// с первого промаха означает «картинки не будет НИКОГДА».
//
// Смоук-тест поймал это 01.09: обложка куклы не показалась ни разу за прогон,
// хотя сервер собрал её код через пару секунд. Сервер кодирует и на прогреве,
// и по первому запросу — холодный файл состояние временное и обычное, а на
// свежем контенте холодны ВСЕ.
func TestColdKtx2IsWaitedForNotBuried(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Sprites.cs"))))
	if !strings.Contains(body, "for (int wait = 0; wait < Ktx2Waits; wait++)") {
		t.Fatal("холодный код больше не ждут — отказ с первого промаха теперь " +
			"означает «картинки не будет никогда»: растра под ним нет")
	}
	if !strings.Contains(body, "ForgetKtx2Cold(url)") {
		t.Error("повтор не снимает отметку «холодный» — он уйдёт в тот же " +
			"пропуск, и ждать будет незачем")
	}
	i := strings.Index(body, "ForgetKtx2Cold(url)")
	j := strings.Index(body, "TryDecodeKtx2Async(url, ct)")
	for j >= 0 && j < i {
		next := strings.Index(body[j+1:], "TryDecodeKtx2Async(url, ct)")
		if next < 0 {
			break
		}
		j = j + 1 + next
	}
	if i < 0 || j < i {
		t.Error("забывчивость стоит ПОСЛЕ повторной попытки — попытка уйдёт " +
			"в пропуск, а снятие отметки достанется следующему кругу")
	}
}
