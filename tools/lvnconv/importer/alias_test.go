package importer

import (
	"strings"
	"testing"
)

// ОПЕЧАТКА В ИМЕНИ ПЕРЕМЕННОЙ ЧИНИТСЯ НА ОБОИХ ПУТЯХ.
//
// `var_aliases` — спасение от опечатки в исходном контенте: автор писал
// `Relationship.Anna`, канон проекта — `Relationships.Anna`, и без починки
// показатель пишется в одно имя, а читается из другого. Ворота по статам молча
// не срабатывают: ошибки нет нигде, просто выбор всегда закрыт.
//
// Одиночный импорт чинил это давно. ПАКЕТНЫЙ — тот, которым едет живой
// контент, — не чинил вовсе, хотя комментарий рядом обещал «то же
// именование». Дыра была спящей ровно потому, что ни один шаблон псевдонимов
// не объявлял; проснулась бы она в день первой починки.
func TestBundlePathFixesVariableTypos(t *testing.T) {
	tpl := &Template{VarAliases: map[string]string{"Relationship.": "Relationships."}}
	script := `{"scene":"ch1","script":[` +
		`{"op":"inc","key":"Relationship.Anna","by":1},` +
		`{"op":"if","expr":"Relationship.Anna > 2"},` +
		`{"op":"say","text":"Relationship.Anna остаётся текстом"}` +
		`]}`
	res := &Result{Scripts: []ScriptFile{{Rel: "scripts/ch1.lvn", Data: []byte(script)}}}

	applyVarAliasesToBundle(res, tpl)

	got := string(res.Scripts[0].Data)
	if strings.Contains(got, `"key":"Relationship.Anna"`) {
		t.Error("имя переменной осталось с опечаткой — пакетный путь снова не чинит")
	}
	if !strings.Contains(got, `"key":"Relationships.Anna"`) {
		t.Errorf("канонического имени нет: %s", got)
	}
	// Знак сравнения в JSON уезжает экранированным (\u003e) — сверяем имя, а
	// не всю строку выражения.
	if !strings.Contains(got, `"expr":"Relationships.Anna`) {
		t.Errorf("выражение не починено: %s", got)
	}
	// Реплика — НЕ имя переменной: текст трогать нельзя, иначе починка чинит
	// то, что автор написал нарочно.
	if !strings.Contains(got, "Relationship.Anna остаётся текстом") {
		t.Errorf("починка залезла в текст реплики: %s", got)
	}
}

// Без объявленных псевдонимов файл не переписывается вовсе: перезапись без
// перемен меняет отпечаток в манифесте на ровном месте.
func TestBundlePathLeavesFilesAloneWithoutAliases(t *testing.T) {
	script := `{"scene":"ch1","script":[{"op":"inc","key":"Relationship.Anna","by":1}]}`
	res := &Result{Scripts: []ScriptFile{{Rel: "scripts/ch1.lvn", Data: []byte(script)}}}
	applyVarAliasesToBundle(res, &Template{})
	if string(res.Scripts[0].Data) != script {
		t.Error("файл переписан без единой перемены — отпечаток в манифесте поедет зря")
	}
}
