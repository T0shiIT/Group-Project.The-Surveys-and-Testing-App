package db

import (
	"database/sql"
	"fmt"
	"log"

	"testapplogic/config"

	_ "github.com/lib/pq" // Драйвер PostgreSQL
)

// ConnectDB подключается к базе данных PostgreSQL
func ConnectDB(config *config.Config) (*sql.DB, error) {
	// Формируем строку подключения
	connStr := fmt.Sprintf("host=%s port=%d user=%s password=%s dbname=%s sslmode=disable",
		config.DBHost,
		config.DBPort,
		config.DBUser,
		config.DBPassword,
		config.DBName,
	)

	// Создаем подключение к БД
	db, err := sql.Open("postgres", connStr)
	if err != nil {
		return nil, err
	}

	// Проверяем подключение
	err = db.Ping()
	if err != nil {
		return nil, err
	}

	log.Println("Connected to PostgreSQL database")
	return db, nil
}
