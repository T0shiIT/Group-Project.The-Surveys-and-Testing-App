package config

import (
	"log"
	"os"
	"strconv"
)

// Config содержит настройки приложения
type Config struct {
	DBHost     string
	DBPort     int
	DBUser     string
	DBPassword string
	DBName     string
	JWTSecret  string
}

// LoadConfig загружает конфигурацию из переменных окружения
func LoadConfig() (*Config, error) {
	// Пытаемся получить порт БД, если не указан - используем 5432
	port, err := strconv.Atoi(os.Getenv("DB_PORT"))
	if err != nil {
		port = 5432
	}

	config := &Config{
		DBHost:     getEnv("DB_HOST", "localhost"),
		DBPort:     port,
		DBUser:     os.Getenv("DB_USER"),
		DBPassword: os.Getenv("DB_PASSWORD"),
		DBName:     os.Getenv("DB_NAME"),
		JWTSecret:  os.Getenv("JWT_SECRET"),
	}

	// Проверяем обязательные переменные
	if config.DBUser == "" {
		return nil, log.New(os.Stdout, "DB_USER is required", 0)
	}
	if config.DBPassword == "" {
		return nil, log.New(os.Stdout, "DB_PASSWORD is required", 0)
	}
	if config.DBName == "" {
		return nil, log.New(os.Stdout, "DB_NAME is required", 0)
	}

	return config, nil
}

// getEnv возвращает значение переменной окружения или значение по умолчанию
func getEnv(key, defaultValue string) string {
	if value, exists := os.LookupEnv(key); exists {
		return value
	}
	return defaultValue
}
