package main

// КОШЕЛЬКИ В БАЗЕ — чтение, запись и разовый переезд из файлов.
//
// Прикладные правила (восполнение, потолки, идемпотентность, история) живут в
// wallet.go и не изменились ни на строку: здесь только «откуда взять» и «куда
// положить». Разделение не косметическое — деньги стоит менять по одному
// вопросу за раз, и этот вопрос был «где они лежат».

import (
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"log"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// walletHistoryView — сколько последних записей журнала едет игроку в ответе.
// Ровно столько же держал файл; разница в том, что теперь это ОКНО, а не всё,
// что вообще сохранилось: остальное лежит в базе и доступно отчётам.
const walletHistoryView = 100

// walletLoad собирает запись игрока из строк базы. seed — стартовые балансы
// восполняемых валют: их получает ТОЛЬКО кошелёк, которого ещё нет.
func walletLoad(db *sql.DB, userID string, seed map[string]int64) (*walletDoc, error) {
	doc := &walletDoc{Balances: map[string]int64{}, Inventory: map[string]int64{}}

	var known int
	if err := db.QueryRow(`SELECT COUNT(*) FROM wallets WHERE user_id = ?`, userID).Scan(&known); err != nil {
		return nil, err
	}
	if known == 0 {
		// Новый кошелёк: восполняемые валюты начинают со своего запаса.
		for cur, start := range seed {
			if start > 0 {
				doc.Balances[cur] = start
			}
		}
		return doc, nil
	}
	if err := db.QueryRow(`SELECT version FROM wallets WHERE user_id = ?`, userID).Scan(&doc.Version); err != nil {
		return nil, err
	}

	rows, err := db.Query(`SELECT currency, amount, anchor FROM wallet_balances WHERE user_id = ?`, userID)
	if err != nil {
		return nil, err
	}
	for rows.Next() {
		var cur string
		var amount, anchor int64
		if err := rows.Scan(&cur, &amount, &anchor); err != nil {
			rows.Close()
			return nil, err
		}
		doc.Balances[cur] = amount
		if anchor > 0 {
			if doc.Regen == nil {
				doc.Regen = map[string]int64{}
			}
			doc.Regen[cur] = anchor
		}
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return nil, err
	}

	rows, err = db.Query(`SELECT sku, count FROM wallet_inventory WHERE user_id = ?`, userID)
	if err != nil {
		return nil, err
	}
	for rows.Next() {
		var sku string
		var n int64
		if err := rows.Scan(&sku, &n); err != nil {
			rows.Close()
			return nil, err
		}
		doc.Inventory[sku] = n
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return nil, err
	}

	// Журнал: последние walletHistoryView записей, в прямом порядке — таким
	// его всегда видел клиент.
	rows, err = db.Query(`SELECT ts, type, currency, amount, sku, reason, title, author
		FROM wallet_ledger WHERE user_id = ? ORDER BY id DESC LIMIT ?`, userID, walletHistoryView)
	if err != nil {
		return nil, err
	}
	var back []walletEntry
	for rows.Next() {
		var e walletEntry
		if err := rows.Scan(&e.TS, &e.Type, &e.Currency, &e.Amount, &e.SKU, &e.Reason, &e.Title, &e.Author); err != nil {
			rows.Close()
			return nil, err
		}
		back = append(back, e)
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return nil, err
	}
	for i := len(back) - 1; i >= 0; i-- {
		doc.History = append(doc.History, back[i])
	}
	doc.dbHistory = len(doc.History)

	// Метки идемпотентности: окно последних, как и было. Ключ в базе не даст
	// применить повтор в любом случае — это окно нужно, чтобы ОТВЕТИТЬ на
	// повтор текущим состоянием, а не отказом.
	rows, err = db.Query(`SELECT op_id FROM wallet_ops WHERE user_id = ? ORDER BY rowid DESC LIMIT 200 -- appliedOpsWindow`, userID)
	if err != nil {
		return nil, err
	}
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		doc.AppliedOps = append(doc.AppliedOps, id)
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return nil, err
	}
	doc.dbOps = len(doc.AppliedOps)

	rows, err = db.Query(`SELECT txn FROM wallet_receipts WHERE user_id = ?`, userID)
	if err != nil {
		return nil, err
	}
	for rows.Next() {
		var txn string
		if err := rows.Scan(&txn); err != nil {
			rows.Close()
			return nil, err
		}
		doc.Transactions = append(doc.Transactions, txn)
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return nil, err
	}
	doc.dbTxns = len(doc.Transactions)
	return doc, nil
}

// walletSave кладёт запись игрока ОДНОЙ сделкой: баланс, инвентарь, новые
// строки журнала и новые метки ложатся вместе или не ложатся вовсе.
//
// Именно этого не давал файл. Деньги, метка идемпотентности и запись в
// журнале — три следствия одного решения, и разъехаться они не имеют права:
// метка без списания означает потерянную покупку, списание без метки —
// списание дважды.
func walletSave(db *sql.DB, userID string, doc *walletDoc) error {
	tx, err := db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback() // после успешного Commit — пустышка

	doc.Version++
	if _, err := tx.Exec(`INSERT INTO wallets (user_id, version) VALUES (?, ?)
		ON CONFLICT(user_id) DO UPDATE SET version = excluded.version`, userID, doc.Version); err != nil {
		return err
	}

	for cur, amount := range doc.Balances {
		anchor := doc.Regen[cur]
		if _, err := tx.Exec(`INSERT INTO wallet_balances (user_id, currency, amount, anchor) VALUES (?, ?, ?, ?)
			ON CONFLICT(user_id, currency) DO UPDATE SET amount = excluded.amount, anchor = excluded.anchor`,
			userID, cur, amount, anchor); err != nil {
			return err
		}
	}
	for sku, n := range doc.Inventory {
		if _, err := tx.Exec(`INSERT INTO wallet_inventory (user_id, sku, count) VALUES (?, ?, ?)
			ON CONFLICT(user_id, sku) DO UPDATE SET count = excluded.count`, userID, sku, n); err != nil {
			return err
		}
	}

	// Журнал ДОПИСЫВАЕТСЯ. Курсор помнит, сколько строк уже в базе; всё, что
	// добавили в память после чтения, — новое.
	if doc.dbHistory <= len(doc.History) {
		for _, e := range doc.History[doc.dbHistory:] {
			if _, err := tx.Exec(`INSERT INTO wallet_ledger
				(user_id, ts, type, currency, amount, sku, reason, title, author)
				VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
				userID, e.TS, e.Type, e.Currency, e.Amount, e.SKU, e.Reason, e.Title, e.Author); err != nil {
				return err
			}
		}
	}
	if doc.dbOps <= len(doc.AppliedOps) {
		for _, id := range doc.AppliedOps[doc.dbOps:] {
			if _, err := tx.Exec(`INSERT OR IGNORE INTO wallet_ops (user_id, op_id, ts) VALUES (?, ?, ?)`,
				userID, id, time.Now().UTC().Format(time.RFC3339)); err != nil {
				return err
			}
		}
	}
	if doc.dbTxns <= len(doc.Transactions) {
		for _, txn := range doc.Transactions[doc.dbTxns:] {
			if _, err := tx.Exec(`INSERT OR IGNORE INTO wallet_receipts (user_id, txn, ts) VALUES (?, ?, ?)`,
				userID, txn, time.Now().UTC().Format(time.RFC3339)); err != nil {
				return err
			}
		}
	}
	if err := tx.Commit(); err != nil {
		return err
	}
	// Записалось — курсоры догоняют. Окно журнала в памяти подрезается до
	// того же размера, что видел файл: в базе лежит всё, игроку едет хвост.
	if len(doc.History) > walletHistoryView {
		doc.History = doc.History[len(doc.History)-walletHistoryView:]
	}
	doc.dbHistory = len(doc.History)
	if len(doc.AppliedOps) > appliedOpsWindow {
		doc.AppliedOps = doc.AppliedOps[len(doc.AppliedOps)-appliedOpsWindow:]
	}
	doc.dbOps = len(doc.AppliedOps)
	doc.dbTxns = len(doc.Transactions)
	return nil
}

// walletAllUsers — у кого вообще есть кошелёк.
func walletAllUsers(db *sql.DB) []string {
	rows, err := db.Query(`SELECT user_id FROM wallets ORDER BY user_id`)
	if err != nil {
		return nil
	}
	defer rows.Close()
	var ids []string
	for rows.Next() {
		var id string
		if rows.Scan(&id) == nil {
			ids = append(ids, id)
		}
	}
	return ids
}

// walletPurchases — все покупки за реальные деньги, одним запросом по индексу.
//
// Раньше это был обход ВСЕХ файлов игроков с разбором каждого целиком, и его
// делали четыре разных отчёта на каждое открытие вкладки. Комментарий у
// прежней версии честно обещал, что при сорока тысячах игроков так будет
// нельзя, — теперь этот день не наступит.
func walletPurchases(db *sql.DB) []walletPurchase {
	rows, err := db.Query(`SELECT user_id, ts, sku, title FROM wallet_ledger
		WHERE type = 'iap' ORDER BY id`)
	if err != nil {
		return nil
	}
	defer rows.Close()
	var out []walletPurchase
	for rows.Next() {
		var p walletPurchase
		if err := rows.Scan(&p.User, &p.TS, &p.SKU, &p.Title); err != nil {
			continue
		}
		out = append(out, p)
	}
	return out
}

// ── разовый переезд из файлов ───────────────────────────────────────────────

// importWalletFiles переносит кошельки из services/wallet/*.json в базу.
//
// Идёт ОДИН раз: как только в базе появился хоть один кошелёк, каталог с
// файлами уже отработал своё и переименовывается в wallet.migrated. Файлы
// НЕ удаляются — это единственная копия денег на момент переезда, и решение
// «когда её выбросить» принимает человек, а не миграция.
//
// Переезд неатомарен по кошелькам намеренно: каждый игрок кладётся своей
// сделкой, и сбой на девятом не отменяет первые восемь. Повторный запуск
// после сбоя доложит остальных — уже перенесённые видны по первичному ключу.
func importWalletFiles(db *sql.DB, dir string) (moved int, err error) {
	entries, rerr := os.ReadDir(dir)
	if rerr != nil {
		if errors.Is(rerr, fs.ErrNotExist) {
			return 0, nil // файлов нет — переезжать нечего
		}
		return 0, rerr
	}
	var files []string
	for _, e := range entries {
		if !e.IsDir() && strings.HasSuffix(e.Name(), ".json") {
			files = append(files, e.Name())
		}
	}
	if len(files) == 0 {
		return 0, nil
	}

	for _, name := range files {
		userID := strings.TrimSuffix(name, ".json")
		if !reUserFile.MatchString(userID) {
			log.Printf("[wallet] переезд: имя %q не похоже на игрока — файл оставлен как есть", name)
			continue
		}
		// Уже в базе — пропускаем. Журнал кошелька ДОПИСЫВАЕТСЯ, а не
		// заменяется: повторный заход после сорвавшегося на середине переезда
		// удвоил бы записи, а по ним считаются выплаты авторам.
		var already int
		if qerr := db.QueryRow(`SELECT COUNT(*) FROM wallets WHERE user_id = ?`, userID).Scan(&already); qerr != nil {
			return moved, fmt.Errorf("переезд кошелька %s: %w", userID, qerr)
		}
		if already > 0 {
			log.Printf("[wallet] переезд: %s уже в базе — файл пропущен", userID)
			continue
		}
		raw, ferr := os.ReadFile(filepath.Join(dir, name))
		if ferr != nil {
			return moved, fmt.Errorf("переезд кошелька %s: %w", userID, ferr)
		}
		var doc walletDoc
		if jerr := json.Unmarshal(raw, &doc); jerr != nil {
			return moved, fmt.Errorf("переезд кошелька %s: %w", userID, jerr)
		}
		if doc.Balances == nil {
			doc.Balances = map[string]int64{}
		}
		if doc.Inventory == nil {
			doc.Inventory = map[string]int64{}
		}
		// Курсоры нулевые: в базе этого игрока ещё нет, значит вся история,
		// все метки и все чеки из файла — новые.
		doc.dbHistory, doc.dbOps, doc.dbTxns = 0, 0, 0
		// Version уменьшаем на единицу: walletSave его увеличит, и номер
		// записи останется тем же, каким был в файле.
		doc.Version--
		if serr := walletSave(db, userID, &doc); serr != nil {
			return moved, fmt.Errorf("переезд кошелька %s: %w", userID, serr)
		}
		moved++
	}

	// Каталог отработал. Имя с пометкой, а не удаление: пока никто не сверил
	// деньги глазами, выбрасывать единственную прежнюю копию нельзя.
	done := dir + ".migrated"
	if _, serr := os.Stat(done); serr == nil {
		done = fmt.Sprintf("%s.migrated-%s", dir, time.Now().UTC().Format("20060102-150405"))
	}
	if rerr := os.Rename(dir, done); rerr != nil {
		log.Printf("[wallet] ВНИМАНИЕ: кошельки перенесены в базу, но каталог %s не переименован (%v) — "+
			"уберите его руками, иначе следующий старт попробует перенести их снова", dir, rerr)
	}
	return moved, nil
}
