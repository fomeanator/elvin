package lvns

import (
	"strings"
	"testing"
)

// ОШИБКА АВТОРУ НАЗЫВАЕТ МЕСТО.
//
// Компилятор — интерфейс автора, и его ошибка читается как ответ на вопрос «что
// я сделал не так». Ответ «unmatched '}' (no open for/while/if block)» без
// номера строки отправляет искать скобку глазами по всему файлу — тем дороже,
// чем длиннее глава, а главы длинные.
//
// Незакрытый блок называет строку, где он ОТКРЫЛСЯ: искать надо там, а не в
// конце файла, куда добрался разбор.
func TestAuthorErrorsNameTheLine(t *testing.T) {
	cases := []struct {
		name string
		src  string
		want string // фрагмент, который обязан быть в тексте
	}{
		{
			name: "лишняя закрывающая скобка",
			src:  "label start\nsay Привет\n}\n",
			want: "line 3",
		},
		{
			name: "незакрытый while",
			src:  "label start\nwhile x < 3 {\ninc x\n",
			want: "line 2",
		},
		{
			name: "else без if",
			src:  "label start\n} else {\nsay нет\n}\n",
			want: "line 2",
		},
		{
			name: "for без переменной",
			src:  "label start\nfor  in items {\n}\n",
			want: "line 2",
		},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			_, err := Convert(c.src)
			if err == nil {
				t.Fatalf("ошибки нет, а исходник сломан:\n%s", c.src)
			}
			if !strings.Contains(err.Error(), c.want) {
				t.Fatalf("ошибка не называет место (%s): %v\n"+
					"автор ищет скобку глазами по всему файлу — место обязано быть в тексте",
					c.want, err)
			}
		})
	}
}
