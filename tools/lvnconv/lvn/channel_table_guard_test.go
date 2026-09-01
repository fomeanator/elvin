package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// КАНАЛ ЗВУКА — ОДНА ЗАПИСЬ, А НЕ ПЯТЬ ПАМЯТЕЙ.
//
// Про каждый канал знали пятеро врозь: именованные поля с источниками, поля
// авторской громкости, словарь «что звучит», словарь поколений, словарь живых
// затуханий. Цена была не в правке, а в её незаметности:
//
//   - соответствие «канал → источник» стояло дважды слово в слово, и в одной
//     копии забыли озвучку — голос звучал мимо своего ползунка;
//   - уборка кадра снимала печать и голос, а музыку не снимал никто: её просто
//     не было в списке того, что уносит уходящая глава, и трек главы играл в
//     меню поверх витринного («выходишь из главы, музыка дублируется», 01.09).
//
// Оба раза виноват был СПИСОК: он выглядит полным, пока не сравнишь его с
// таблицей. Обход не выглядит полным — он полон.
func TestAudioChannelIsOneRecord(t *testing.T) {
	root := repoRoot(t)
	src := string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/UI/StageAudio.cs")))
	body := stripComments(src)

	// Ни одной памяти «по каналу» мимо записи.
	for _, gone := range []string{"_playingUrl", "_channelGen", "_fadeCo", "_authMusic", "_authTyping"} {
		if strings.Contains(body, gone) {
			t.Errorf("StageAudio снова держит %s — это память про канал ВРОЗЬ "+
				"от остальных четырёх, и синхронизировать их придётся руками", gone)
		}
	}

	// Ни одного ветвления «какой это канал»: различия каналов — данные.
	branch := regexp.MustCompile(`channel\s*[=!]=\s*LvnVolumes\.`)
	if hits := branch.FindAllString(body, -1); len(hits) > 0 {
		t.Errorf("StageAudio решает про канал ветвлением (%d шт.): %v. "+
			"Различия каналов — ДАННЫЕ в таблице (ползунок, непрерывность, "+
			"слышен ли сценарию), иначе шестой канал придётся дописывать "+
			"в шести местах и одно из них забудут", len(hits), hits)
	}

	// Пересчёт громкостей и уборка главы — ОБХОДОМ. Список тут уже дважды
	// оказывался короче таблицы.
	for _, sweep := range []struct{ what, needle string }{
		{"пересчёт пользовательских громкостей", "foreach (var ch in _all)"},
		{"уборка главы", "if (ch.Authorable) Silence("},
	} {
		if !strings.Contains(body, sweep.needle) {
			t.Errorf("%s снова перечисляет каналы списком вместо обхода — "+
				"именно так из него дважды выпадала строка", sweep.what)
		}
	}
}

// НЕ ВСЯКИЙ КАНАЛ СЛЫШЕН СЦЕНАРИЮ.
//
// Пока канал искали ветвлением, «audio channel=voice» уходил в звук просто
// потому, что ветвление знало только музыку и эмбиент. Таблица знает все
// каналы поимённо — и, не будь отдельного правила, авторская команда получила
// бы право перебить озвучку реплики или стук печати.
//
// Правило должно жить в одном месте и называться вслух: расширение таблицы не
// смеет делать слышимым то, что слышимым не задумано.
func TestOnlyAuthorableChannelsHearTheScript(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/UI/StageAudio.cs"))))
	if !strings.Contains(body, "private Channel Addressed(string channel)") {
		t.Fatal("исчез дом «кому адресована авторская команда» — без него " +
			"таблица делает слышимыми озвучку, печать и интерфейс")
	}
	if !strings.Contains(body, "c.Authorable ? c : _byName[LvnVolumes.Sfx]") {
		t.Error("правило «неслышимый канал → звук» изменилось молча: " +
			"именно оно не даёт авторской команде перебить реплику")
	}
	if !strings.Contains(body, "Addressed((string)cmd[\"channel\"])") {
		t.Error("ApplyAsync берёт канал мимо Addressed — авторская команда " +
			"снова может захватить чужой канал")
	}
}
