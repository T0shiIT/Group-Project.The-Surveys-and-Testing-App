package db //подключение к бдшке

import (
	"database/sql"
	"fmt"
	"log"
	"os"

	_ "github.com/lib/pq"
)

// InitDB инициализирует подключение к PostgreSQL
func InitDB() *sql.DB {
	config := os.Getenv("DB_CONNECTION_STRING")
	if config == "" {
		config = fmt.Sprintf("host=%s port=%s user=%s password=%s dbname=%s sslmode=disable",
			os.Getenv("DB_HOST"),
			os.Getenv("DB_PORT"),
			os.Getenv("DB_USER"),
			os.Getenv("DB_PASSWORD"),
			os.Getenv("DB_NAME"),
		)
	}

	db, err := sql.Open("postgres", config)
	if err != nil {
		log.Fatalf("Не удалось подключиться к БД: %v", err)
	}

	// Проверка подключения
	err = db.Ping()
	if err != nil {
		log.Fatalf("Не удалось проверить подключение: %v", err)
	}

	log.Println("Подключено к PostgreSQL")
	return db
}

// CloseDB закрывает соединение с БД
func CloseDB(db *sql.DB) {
	db.Close()
}
