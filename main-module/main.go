package main

import (
	"log"
	"net/http"
	"os"
	"time"

	"testapp/auth"
	"testapp/config"
	"testapp/db"
	"testapp/handlers"

	"github.com/gorilla/mux"
)

func main() {
	// Загружаем конфигурацию
	cfg := config.LoadConfig()
	os.Setenv("DB_HOST", cfg.DBHost)
	os.Setenv("DB_PORT", cfg.DBPort)
	os.Setenv("DB_USER", cfg.DBUser)
	os.Setenv("DB_PASSWORD", cfg.DBPassword)
	os.Setenv("DB_NAME", cfg.DBName)
	os.Setenv("JWT_SECRET", cfg.JWTSecret)

	// Инициализируем БД
	dbConn := db.InitDB()
	defer db.CloseDB(dbConn)

	// Создаем роутер
	r := mux.NewRouter()

	// Добавляем обработчики
	r.HandleFunc("/users", handlers.GetUserList(dbConn)).Methods("GET")
	r.HandleFunc("/users/{id}", handlers.GetUser(dbConn)).Methods("GET")
	r.HandleFunc("/courses", handlers.GetCourseList(dbConn)).Methods("GET")
	r.HandleFunc("/courses/{id}", handlers.GetCourse(dbConn)).Methods("GET")

	// Проверка токена для всех запросов
	r.Use(func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			// Пропускаем некоторые эндпоинты (например, /health)
			if r.URL.Path == "/health" {
				next.ServeHTTP(w, r)
				return
			}

			// Проверяем токен
			tokenString := r.Header.Get("Authorization")
			if tokenString == "" {
				http.Error(w, "Требуется аутентификация", http.StatusUnauthorized)
				return
			}

			_, err := auth.VerifyToken(tokenString)
			if err != nil {
				http.Error(w, "Неверный токен", http.StatusUnauthorized)
				return
			}

			next.ServeHTTP(w, r)
		})
	})

	// Эндпоинт для проверки работоспособности
	r.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("OK"))
	}).Methods("GET")

	// Запускаем сервер
	server := &http.Server{
		Addr:         ":8080",
		Handler:      r,
		ReadTimeout:  15 * time.Second,
		WriteTimeout: 15 * time.Second,
	}

	log.Printf("Сервер запущен на http://localhost:8080")
	log.Fatal(server.ListenAndServe())
}
