package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ПРАВИЛО, ВЫУЧЕННОЕ ОДИН РАЗ, ДОЛЖНО ПРИМЕНЯТЬСЯ ВЕЗДЕ.
//
// Поставщик ассетов делает две вещи разом: помнит готовое и не даёт двум
// одновременным запросам одного адреса сделать работу дважды. Вторая половина
// не украшение — проигравший гонку перезаписывал запись кэша и НАВСЕГДА терял
// чужую текстуру: она оставалась в видеопамяти без единой ссылки.
//
// Сетевой поставщик эту гонку закрыл. Каталожный — нет, хотя грузит те же
// файлы тем же способом: у него стояла перепроверка кэша после ожидания, и она
// СУЖАЕТ окно, но не закрывает — оба захода проходят обе проверки, оба строят
// текстуру, один результат теряется.
//
// Одна работа, два поставщика, разные правила — и правило, применённое лишь
// однажды, читается как применённое всюду.
func TestEveryProviderRemembersOnce(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI")
	for _, name := range []string{"NetworkAssets.cs", "DirectoryAssets.cs"} {
		body := stripComments(string(mustRead(t, filepath.Join(dir, name))))
		if !strings.Contains(body, "LvnOnce<Sprite>") || !strings.Contains(body, "LvnOnce<AudioClip>") {
			t.Errorf("%s держит свой кэш мимо дома LvnOnce — вторая половина "+
				"правила (не делать работу дважды) снова окажется написана "+
				"только у одного из поставщиков", name)
		}
		// Свои словари «в полёте» больше не заводят: это и была та половина,
		// которую забыли скопировать.
		for _, gone := range []string{"_spriteInFlight", "_audioInFlight", "_spriteCache", "_audioCache"} {
			if strings.Contains(body, gone) {
				t.Errorf("%s снова держит %s врозь от дома", name, gone)
			}
		}
	}
}

// НЕУДАЧУ НЕ ЗАПОМИНАЕМ.
//
// Пустой ответ означает «сейчас не вышло», а не «этого нет». Запомнив его, мы
// отняли бы у файла все будущие попытки — именно так выглядит арт, который
// однажды не догрузился и больше не появился.
func TestOnceDoesNotRememberFailure(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/LvnOnce.cs"))))
	if !strings.Contains(body, "if (made != null) Done[key] = made;") {
		t.Error("дом «один раз на адрес» перестал отличать неудачу от результата: " +
			"запомненный пустой ответ отнимает у файла все будущие попытки")
	}
	if !strings.Contains(body, "finally { _flying.Remove(key); }") {
		t.Error("сорвавшаяся работа оставляет адрес «вечно в полёте» — " +
			"следующий просящий встанет в очередь за задачей, которой нет")
	}
}

// «КОДА НЕТ» И «СЕТИ НЕТ» — РАЗНЫЕ БЕДЫ.
//
// Показать вторую как первую значит послать разбираться не туда: человек пойдёт
// проверять кодировщик и очередь сервера, хотя до сервера просто не
// достучались. Поймано прогоном 01.09: два теста, идущих БЕЗ сервера,
// покраснели на строке про basisu.
func TestMissingCodeIsNotConfusedWithMissingNetwork(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Sprites.cs"))))
	if !strings.Contains(body, "if (Lvn.LvnNetworkStatus.IsOffline)") {
		t.Error("отказ по отсутствию кода больше не отличает обрыв связи — " +
			"разбираться пойдут в кодировщик вместо сети")
	}
}
