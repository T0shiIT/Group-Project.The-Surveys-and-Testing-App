package config

import (
	"fmt"
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
	portStr := os.Getenv("DB_PORT")
	port := 5432
	if portStr != "" {
		if p, err := strconv.Atoi(portStr); err == nil {
			port = p
		}
	}

	config := &Config{
		DBHost:     getEnv("DB_HOST", "localhost"),
		DBPort:     port,
		DBUser:     getEnv("DB_USER", ""),
		DBPassword: getEnv("DB_PASSWORD", ""),
		DBName:     getEnv("DB_NAME", ""),
		JWTSecret:  getEnv("JWT_SECRET", ""),
	}

	if config.DBUser == "" {
		return nil, fmt.Errorf("DB_USER is required")
	}
	if config.DBPassword == "" {
		return nil, fmt.Errorf("DB_PASSWORD is required")
	}
	if config.DBName == "" {
		return nil, fmt.Errorf("DB_NAME is required")
	}
	if config.JWTSecret == "" {
		return nil, fmt.Errorf("JWT_SECRET is required")
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
