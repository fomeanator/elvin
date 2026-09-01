package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ЗАМОК, НАЗВАННЫЙ ИМЕНЕМ ОДНОЙ ИЗ ОХРАНЯЕМЫХ ПАМЯТЕЙ, — ЭТО ПРИЗНАК.
//
// Про одну загрузку знали четверо: сколько байт ждём, сколько получили, какая
// попытка и сама задача. Первые три свели в запись ещё в прошлый заход, а
// четвёртая — задача — осталась в своём словаре, и замок носил ЕГО имя:
// `lock (_inflight)` стоял вокруг полей, которые к нему не относятся.
//
// Разъезд уже случался и стоил вранья на экране: очистка была написана в двух
// местах и очищала РАЗНОЕ, отчего индикатор показывал «131 из 135» при пустой
// очереди. Тогда починили добавлением ещё одной очистки — залатали МЕСТО, а не
// форму.
func TestOneDownloadIsOneRecord(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content")
	for _, f := range csFiles(t, dir) {
		body := stripComments(string(mustRead(t, f)))
		if strings.Contains(body, "_inflight") {
			t.Errorf("%s снова держит _inflight — задача про загрузку живёт "+
				"ВРОЗЬ от её же байтов и попыток, и очистка опять будет "+
				"написана в двух местах по-разному", filepath.Base(f))
		}
		if strings.Contains(body, "lock (_fetch)") {
			t.Errorf("%s караулит записи именем, которого у них нет", filepath.Base(f))
		}
	}
	loader := stripComments(string(mustRead(t, filepath.Join(dir, "ContentLoader.cs"))))
	if !strings.Contains(loader, "private readonly Dictionary<string, Underway> _underway") {
		t.Fatal("исчезла единая память о загрузках — вернулись словари врозь")
	}
	if !strings.Contains(loader, "lock (_underway)") {
		t.Error("замок больше не носит имя того, что караулит: " +
			"имя соседа снова спрячет, сколько памятей на самом деле под ним")
	}
}

// ПАКЕТ — НЕ ФАЙЛ, И ЭТО СВОЙСТВО ЗАПИСИ, А НЕ ФОРМА КЛЮЧА.
//
// Индикатор исключал пакет главы по точному равенству ключа со строкой
// «__preload_batch__». Настоящий ключ несёт ещё и отпечаток списка, поэтому
// условие не срабатывало НИ РАЗУ: пакет считался файлом в полёте и мог стать
// именем на карточке — игрок видел служебный ключ вместо названия.
//
// Сравнение с началом строки починило бы симптом. Признак у записи чинит
// причину: спрашивать надо у предмета, а не у формы его имени.
func TestBundleIsMarkedNotGuessedFromTheKey(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content")
	loader := stripComments(string(mustRead(t, filepath.Join(dir, "ContentLoader.cs"))))
	if !strings.Contains(loader, "public bool Bundle;") {
		t.Fatal("у записи о загрузке нет признака «это весь пакет» — " +
			"его снова будут угадывать по имени ключа")
	}
	if !strings.Contains(loader, "if (f.Bundle || f.Work == null) continue;") {
		t.Error("снимок сетевой активности перестал спрашивать признак у записи")
	}
	if strings.Contains(loader, `== "__preload_batch__"`) {
		t.Error(`снимок снова сравнивает ключ со строкой "__preload_batch__" — ` +
			"настоящий ключ длиннее на отпечаток списка, и условие не сработает ни разу")
	}
	batch := stripComments(string(mustRead(t, filepath.Join(dir, "ContentLoader.Batch.cs"))))
	if !strings.Contains(batch, "rec.Bundle = true;") {
		t.Error("пакет главы больше не помечает себя пакетом")
	}
}
