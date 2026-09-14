package lvn

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Имя товара «фон меню» трижды за один день собирали по-разному в разных
// углах кода: покупка писала его на персонажа, а проверки спрашивали у
// общего владельца. Каждый развод стоил игроку денег — товар не
// засчитывался, и кнопка снова предлагала «Купить», списывая кристаллы за
// уже оплаченное (живой лог 08.09: 60→40→20→0 за один и тот же фон).
// Написание имени обязано жить в одной точке, и страж держит её одной.
func TestBackdropOwnershipHasSingleSource(t *testing.T) {
	shell := filepath.Join("..", "..", "..",
		"unity", "Packages", "com.lvn.engine.shell", "Runtime")
	read := func(name string) string {
		t.Helper()
		b, err := os.ReadFile(filepath.Join(shell, name))
		if err != nil {
			t.Fatalf("не прочитать %s: %v", name, err)
		}
		return string(b)
	}

	buy := read("WardrobeSheet.Buy.cs")
	if strings.Contains(buy, "Sku(_entity, axis") {
		t.Error("покупка собирает имя товара на персонажа (Sku(_entity, axis)), " +
			"а проверка владения — на общего владельца: куплено под одним " +
			"именем, спрошено под другим, деньги спишутся снова")
	}
	if !strings.Contains(buy, "internal static bool OwnsBackdrop(") {
		t.Error("пропало единственное правило владения фоном (OwnsBackdrop)")
	}

	menu := read("NovelApp.Menu.cs")
	if strings.Contains(menu, "LvnWallet.Has(") {
		t.Error("решатель адреса полотна спрашивает кошелёк сам, мимо " +
			"OwnsBackdrop: так он не увидит покупок, записанных на " +
			"персонажа, и молча вернёт авторский фон")
	}
	if !strings.Contains(menu, "WardrobeSheet.OwnsBackdrop(") {
		t.Error("решатель адреса полотна перестал спрашивать OwnsBackdrop")
	}
}
