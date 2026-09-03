package main

// База: аккаунты и кошельки.
//
// ПОЧЕМУ ВООБЩЕ. Аккаунты жили в одном users.json, который читался в память и
// переписывался ЦЕЛИКОМ на каждую запись — вход, смена имени, метка канала.
// На сорока пяти игроках это одиннадцать килобайт и незаметно; на десяти
// тысячах это мегабайты, переписываемые при каждом чихе, и первая же
// параллельная запись стоит потерянной. Кошельки лежали по файлу на игрока, и
// идемпотентность денег была собрана прикладными хитростями поверх файловой
// системы вместо транзакции.
//
// ПОЧЕМУ SQLITE, А НЕ POSTGRES. Один процесс на одной машине. В этом случае
// SQLite строго лучше: файл вместо демона, ноль памяти на коробке, где её
// полтора гигабайта, настоящие транзакции и настоящие запросы. Postgres
// понадобится, когда процессов станет больше одного.
//
// ПОЧЕМУ modernc.org/sqlite. Сборка идёт с CGO_ENABLED=0 (кросс-компиляция с
// мака на линукс). Популярный mattn/go-sqlite3 требует cgo и сломал бы деплой
// молча — на маке собралось бы, на прод не уехало.
//
// ПОЧЕМУ ОБЫЧНЫЙ SQL, А НЕ ORM. Мы выбираем не базу навсегда, а слой доступа:
// database/sql плюс SQL без экзотики переезжает на Postgres сменой драйвера и
// строки подключения. ORM привязал бы к себе сильнее, чем сама база.

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"

	_ "modernc.org/sqlite"
)

// openStore открывает (и при необходимости создаёт) базу рядом с остальными
// служебными данными.
func openStore(dir string) (*sql.DB, error) {
	// Каталог создаётся ЗДЕСЬ, а не ожидается от вызывающего: базу открывают
	// первой из служб, до всех, кто заводит services/ сам, — и на пустом
	// каталоге контента сервер падал на старте («unable to open database
	// file»). Демо-контент возит services/ в git, поэтому CI этого не видел;
	// видел любой, кто поднимал сервер на своём каталоге (аудит 03.09.2026).
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, fmt.Errorf("каталог базы %s: %w", dir, err)
	}
	path := filepath.Join(dir, "lvn.db")
	// _journal=WAL — читатели не блокируют писателя; для сервера, который
	// одновременно отдаёт отчёты и принимает покупки, это не тюнинг, а
	// условие работы.
	// _busy_timeout — вместо мгновенной ошибки «database is locked» ждём;
	// пять секунд больше любой нашей транзакции.
	// _fk=1 — внешние ключи в SQLite по умолчанию ВЫКЛЮЧЕНЫ, и молча.
	dsn := "file:" + path + "?_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=foreign_keys(1)"
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, err
	}
	// Один писатель. SQLite всё равно сериализует записи, а пул на десять
	// соединений лишь превращает ожидание блокировки в ошибку под нагрузкой.
	db.SetMaxOpenConns(1)
	if err := db.Ping(); err != nil {
		return nil, fmt.Errorf("открыть базу %s: %w", path, err)
	}
	if err := migrate(db); err != nil {
		return nil, fmt.Errorf("миграции: %w", err)
	}
	return db, nil
}

// migrations — по одной на изменение схемы, только вперёд, никогда не правим
// уже уехавшую. Номер хранится в user_version — родном счётчике SQLite, чтобы
// не заводить таблицу ради одного числа.
var migrations = []string{
	// 1. Аккаунты. Идентичности вынесены в свою таблицу: у игрока их может
	// быть несколько (Google и Apple), и хранить их картой в колонке значит
	// потерять возможность искать по ним.
	`
	CREATE TABLE IF NOT EXISTS users (
		id           TEXT PRIMARY KEY,
		device_hash  TEXT NOT NULL DEFAULT '',
		token_hash   TEXT NOT NULL DEFAULT '',
		created      TEXT NOT NULL DEFAULT '',
		name         TEXT NOT NULL DEFAULT '',
		attr_json    TEXT NOT NULL DEFAULT ''
	);
	CREATE INDEX IF NOT EXISTS users_device ON users(device_hash);
	CREATE TABLE IF NOT EXISTS user_providers (
		user_id   TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
		provider  TEXT NOT NULL,
		subject   TEXT NOT NULL,
		PRIMARY KEY (provider, subject)
	);
	CREATE INDEX IF NOT EXISTS user_providers_user ON user_providers(user_id);
	`,
	// 2. Отзывы из игры. Живут в базе, а не в файлах, ровно по одной причине:
	// их читают запросом — новые сверху, разбивка по сборкам, скоро фильтр по
	// главе. По файлам это обход всего каталога на каждое открытие вкладки.
	`
	CREATE TABLE IF NOT EXISTS feedback (
		id       INTEGER PRIMARY KEY AUTOINCREMENT,
		ts       TEXT NOT NULL,
		user_id  TEXT NOT NULL DEFAULT '',
		kind     TEXT NOT NULL DEFAULT '',
		text     TEXT NOT NULL,
		build    TEXT NOT NULL DEFAULT '',
		title    TEXT NOT NULL DEFAULT '',
		chapter  TEXT NOT NULL DEFAULT '',
		at       INTEGER NOT NULL DEFAULT 0,
		label    TEXT NOT NULL DEFAULT '',
		device   TEXT NOT NULL DEFAULT '',
		log      TEXT NOT NULL DEFAULT '',
		line     TEXT NOT NULL DEFAULT '',
		bg       TEXT NOT NULL DEFAULT ''
	);
	CREATE INDEX IF NOT EXISTS feedback_ts ON feedback(ts DESC);
	CREATE INDEX IF NOT EXISTS feedback_build ON feedback(build);
	`,
}

func migrate(db *sql.DB) error {
	var have int
	if err := db.QueryRow("PRAGMA user_version").Scan(&have); err != nil {
		return err
	}
	for i := have; i < len(migrations); i++ {
		tx, err := db.Begin()
		if err != nil {
			return err
		}
		if _, err := tx.Exec(migrations[i]); err != nil {
			tx.Rollback()
			return fmt.Errorf("миграция %d: %w", i+1, err)
		}
		// PRAGMA не принимает подстановку параметров — номер подставляется в
		// строку. Он приходит из длины константного среза, не извне.
		if _, err := tx.Exec(fmt.Sprintf("PRAGMA user_version = %d", i+1)); err != nil {
			tx.Rollback()
			return err
		}
		if err := tx.Commit(); err != nil {
			return err
		}
	}
	return nil
}
