package main

// СОСТОЯНИЕ ИГРОКА ПОМИМО КОШЕЛЬКА — таблицы лидеров, ежедневная награда и
// счёт просмотров рекламы: чтение, запись и разовый переезд из файлов.
//
// Прикладные правила (серии, окна восстановления, ранги) остались в своих
// службах и не менялись: здесь только «откуда взять» и «куда положить».
// Переезд идёт тем же обрядом, что у кошелька: перенести, оставить прежние
// файлы рядом с пометкой, больше в каталог не ходить.

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

// ── таблицы лидеров ─────────────────────────────────────────────────────────

func lbLoad(db *sql.DB, board string) ([]lbEntry, error) {
	rows, err := db.Query(`SELECT user_id, name, score, updated FROM leaderboard WHERE board = ?`, board)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []lbEntry
	for rows.Next() {
		var e lbEntry
		if err := rows.Scan(&e.User, &e.Name, &e.Score, &e.Updated); err != nil {
			return nil, err
		}
		out = append(out, e)
	}
	return out, rows.Err()
}

func lbSave(db *sql.DB, board string, entries []lbEntry) error {
	tx, err := db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	for _, e := range entries {
		if _, err := tx.Exec(`INSERT INTO leaderboard (board, user_id, name, score, updated)
			VALUES (?, ?, ?, ?, ?)
			ON CONFLICT(board, user_id) DO UPDATE SET
				name = excluded.name, score = excluded.score, updated = excluded.updated`,
			board, e.User, e.Name, e.Score, e.Updated); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// ── ежедневная награда ──────────────────────────────────────────────────────

func dailyLoad(db *sql.DB, userID string) (*dailyDoc, error) {
	doc := &dailyDoc{}
	err := db.QueryRow(`SELECT last_claim, streak FROM daily_claims WHERE user_id = ?`, userID).
		Scan(&doc.LastClaim, &doc.Streak)
	if errors.Is(err, sql.ErrNoRows) {
		return doc, nil // ещё ни разу не забирал — это не ошибка
	}
	if err != nil {
		return nil, err
	}
	return doc, nil
}

func dailySave(db *sql.DB, userID string, doc *dailyDoc) error {
	_, err := db.Exec(`INSERT INTO daily_claims (user_id, last_claim, streak) VALUES (?, ?, ?)
		ON CONFLICT(user_id) DO UPDATE SET last_claim = excluded.last_claim, streak = excluded.streak`,
		userID, doc.LastClaim, doc.Streak)
	return err
}

// ── просмотры рекламы ───────────────────────────────────────────────────────

func adsLoad(db *sql.DB, userID string) (*adsUserDoc, error) {
	doc := &adsUserDoc{Counts: map[string]int{}, Spent: map[string]int{}, Since: map[string]int64{}}
	err := db.QueryRow(`SELECT day FROM ad_users WHERE user_id = ?`, userID).Scan(&doc.Day)
	if errors.Is(err, sql.ErrNoRows) {
		return doc, nil
	}
	if err != nil {
		return nil, err
	}
	rows, err := db.Query(`SELECT placement, count, spent, since FROM ad_placements WHERE user_id = ?`, userID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	for rows.Next() {
		var place string
		var count, spent int
		var since int64
		if err := rows.Scan(&place, &count, &spent, &since); err != nil {
			return nil, err
		}
		doc.Counts[place] = count
		doc.Spent[place] = spent
		doc.Since[place] = since
	}
	return doc, rows.Err()
}

func adsSave(db *sql.DB, userID string, doc *adsUserDoc) error {
	tx, err := db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err := tx.Exec(`INSERT INTO ad_users (user_id, day) VALUES (?, ?)
		ON CONFLICT(user_id) DO UPDATE SET day = excluded.day`, userID, doc.Day); err != nil {
		return err
	}
	// Площадки перечисляются по объединению трёх карт: счёт, истраченные
	// заряды и начало цикла живут порознь, и площадка может быть только в
	// одной из них.
	places := map[string]bool{}
	for k := range doc.Counts {
		places[k] = true
	}
	for k := range doc.Spent {
		places[k] = true
	}
	for k := range doc.Since {
		places[k] = true
	}
	for place := range places {
		if _, err := tx.Exec(`INSERT INTO ad_placements (user_id, placement, count, spent, since)
			VALUES (?, ?, ?, ?, ?)
			ON CONFLICT(user_id, placement) DO UPDATE SET
				count = excluded.count, spent = excluded.spent, since = excluded.since`,
			userID, place, doc.Counts[place], doc.Spent[place], doc.Since[place]); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// ── разовый переезд из файлов ───────────────────────────────────────────────

// importJSONDir — общий обряд переезда: пройти по *.json в каталоге, отдать
// каждый файл разбирающему, потом отставить каталог в сторону под именем с
// пометкой. Файлы НЕ удаляются: на момент переезда это единственная копия.
//
// Обряд один на три службы намеренно. Порознь он был бы написан трижды, и
// третья копия однажды забыла бы переименовать каталог — то есть переносила
// бы одно и то же при каждом старте, затирая свежее старым.
func importJSONDir(dir string, each func(name string, raw []byte) error) (moved int, err error) {
	entries, rerr := os.ReadDir(dir)
	if rerr != nil {
		if errors.Is(rerr, fs.ErrNotExist) {
			return 0, nil
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
		raw, ferr := os.ReadFile(filepath.Join(dir, name))
		if ferr != nil {
			return moved, ferr
		}
		if eerr := each(strings.TrimSuffix(name, ".json"), raw); eerr != nil {
			return moved, eerr
		}
		moved++
	}
	done := dir + ".migrated"
	if _, serr := os.Stat(done); serr == nil {
		done = fmt.Sprintf("%s.migrated-%s", dir, time.Now().UTC().Format("20060102-150405"))
	}
	if rerr := os.Rename(dir, done); rerr != nil {
		log.Printf("[services] ВНИМАНИЕ: %s перенесён в базу, но каталог не переименован (%v) — "+
			"уберите его руками, иначе следующий старт перенесёт то же самое поверх свежего", dir, rerr)
	}
	return moved, nil
}

func importLeaderboardFiles(db *sql.DB, dir string) (int, error) {
	return importJSONDir(dir, func(board string, raw []byte) error {
		if !reBoard.MatchString(board) {
			log.Printf("[leaderboard] переезд: %q не похоже на имя таблицы — файл оставлен", board)
			return nil
		}
		var entries []lbEntry
		if err := json.Unmarshal(raw, &entries); err != nil {
			return fmt.Errorf("переезд таблицы %s: %w", board, err)
		}
		return lbSave(db, board, entries)
	})
}

func importDailyFiles(db *sql.DB, dir string) (int, error) {
	return importJSONDir(dir, func(user string, raw []byte) error {
		if !reUserFile.MatchString(user) {
			return nil
		}
		var doc dailyDoc
		if err := json.Unmarshal(raw, &doc); err != nil {
			return fmt.Errorf("переезд ежедневки %s: %w", user, err)
		}
		return dailySave(db, user, &doc)
	})
}

func importAdsFiles(db *sql.DB, dir string) (int, error) {
	return importJSONDir(dir, func(user string, raw []byte) error {
		if !reUserFile.MatchString(user) {
			return nil
		}
		var doc adsUserDoc
		if err := json.Unmarshal(raw, &doc); err != nil {
			return fmt.Errorf("переезд рекламы %s: %w", user, err)
		}
		if doc.Counts == nil {
			doc.Counts = map[string]int{}
		}
		if doc.Spent == nil {
			doc.Spent = map[string]int{}
		}
		if doc.Since == nil {
			doc.Since = map[string]int64{}
		}
		return adsSave(db, user, &doc)
	})
}
