package main

import (
	"encoding/json"
	"log"
	"net/http"
	"os"

	"testapplogic/config"
	"testapplogic/db"
	"testapplogic/handlers"

	"github.com/gorilla/mux"
	"github.com/joho/godotenv"
)

func main() {
	// Загружаем переменные окружения из .env файла
	err := godotenv.Load()
	if err != nil {
		log.Println("No .env file found, using environment variables")
	}

	// Загружаем конфигурацию
	cfg, err := config.LoadConfig()
	if err != nil {
		log.Fatalf("Failed to load config: %v", err)
	}

	// Подключаемся к базе данных
	database, err := db.ConnectDB(cfg)
	if err != nil {
		log.Fatalf("Failed to connect to database: %v", err)
	}
	defer database.Close()

	// Инициализируем JWT-секрет
	handlers.InitAuth(cfg.JWTSecret)

	// Создаём роутер
	router := mux.NewRouter()

	// Логирование всех входящих запросов
	router.Use(func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			log.Printf("%s %s %s", r.Method, r.RequestURI, r.RemoteAddr)
			next.ServeHTTP(w, r)
		})
	})

	// Общие маршруты API
	api := router.PathPrefix("/api").Subrouter()

	// Публичный маршрут для проверки работоспособности
	api.HandleFunc("/health", (&handlers.DBHandler{DB: database}).HealthCheck).Methods("GET")

	// Маршруты, требующие авторизации
	auth := api.PathPrefix("").Subrouter()
	auth.Use(handlers.AuthMiddleware(database))

	// Курсы
	auth.HandleFunc("/courses", (&handlers.DBHandler{DB: database}).GetCourses).Methods("GET")
	auth.HandleFunc("/courses/{id}", (&handlers.DBHandler{DB: database}).GetCourse).Methods("GET")
	auth.HandleFunc("/courses", (&handlers.DBHandler{DB: database}).CreateCourse).Methods("POST")

	// Тесты
	auth.HandleFunc("/courses/{id}/tests", (&handlers.DBHandler{DB: database}).GetCourseTests).Methods("GET")
	auth.HandleFunc("/tests/{id}", (&handlers.DBHandler{DB: database}).GetTest).Methods("GET")
	auth.HandleFunc("/tests/{id}/activate", (&handlers.DBHandler{DB: database}).ActivateTest).Methods("POST")
	auth.HandleFunc("/tests/{id}/deactivate", (&handlers.DBHandler{DB: database}).DeactivateTest).Methods("POST")

	// Вопросы
	auth.HandleFunc("/questions", (&handlers.DBHandler{DB: database}).CreateQuestion).Methods("POST")
	auth.HandleFunc("/questions/{id}", (&handlers.DBHandler{DB: database}).GetQuestion).Methods("GET")
	auth.HandleFunc("/questions/{id}", (&handlers.DBHandler{DB: database}).UpdateQuestion).Methods("PUT")
	auth.HandleFunc("/questions/{id}", (&handlers.DBHandler{DB: database}).DeleteQuestion).Methods("DELETE")

	// Попытки
	auth.HandleFunc("/tests/{id}/attempts", (&handlers.DBHandler{DB: database}).CreateAttempt).Methods("POST")
	auth.HandleFunc("/attempts/{id}", (&handlers.DBHandler{DB: database}).GetAttempt).Methods("GET")
	auth.HandleFunc("/attempts/{id}/answers", (&handlers.DBHandler{DB: database}).SubmitAnswer).Methods("POST")
	auth.HandleFunc("/attempts/{id}/complete", (&handlers.DBHandler{DB: database}).CompleteAttempt).Methods("POST")

	// Обработка 404 ошибки
	router.NotFoundHandler = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusNotFound)
		json.NewEncoder(w).Encode(map[string]string{"error": "Not found"})
	})

	// Запуск сервера
	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}
	log.Printf("Server started on port %s", port)
	log.Fatal(http.ListenAndServe(":"+port, router))
}
