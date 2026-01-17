package main

import (
	"encoding/json"
	"log"
	"net/http"
	"os"

	"github.com/gorilla/mux"
	"github.com/joho/godotenv"

	"testapplogic/config"
	"testapplogic/db"
	"testapplogic/handlers"
)

func main() {
	// Загружаем переменные окружения из .env файла
	err := godotenv.Load()
	if err != nil {
		log.Println("No .env file found, using environment variables")
	}

	// Загружаем конфигурацию
	config, err := config.LoadConfig()
	if err != nil {
		log.Fatalf("Failed to load config: %v", err)
	}

	// Подключаемся к базе данных
	db, err := db.ConnectDB(config)
	if err != nil {
		log.Fatalf("Failed to connect to database: %v", err)
	}
	defer db.Close()

	// Создаем роутер для обработки HTTP-запросов
	router := mux.NewRouter()

	// Логирование всех входящих запросов
	router.Use(func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			log.Printf("%s %s %s", r.Method, r.RequestURI, r.RemoteAddr)
			next.ServeHTTP(w, r)
		})
	})

	// Определяем общие пути для API
	api := router.PathPrefix("/api").Subrouter()

	// Публичные маршруты (не требуют авторизации)
	api.HandleFunc("/health", handlers.HealthCheck).Methods("GET")

	// Маршруты, требующие авторизации
	auth := api.PathPrefix("/").Subrouter()
	auth.Use(handlers.AuthMiddleware)

	// Маршруты для работы с дисциплинами
	auth.HandleFunc("/courses", handlers.GetCourses).Methods("GET")
	auth.HandleFunc("/courses/{id}", handlers.GetCourse).Methods("GET")
	auth.HandleFunc("/courses", handlers.CreateCourse).Methods("POST")
	auth.HandleFunc("/courses/{id}/tests", handlers.GetCourseTests).Methods("GET")

	// Маршруты для работы с тестами
	auth.HandleFunc("/tests/{id}", handlers.GetTest).Methods("GET")
	auth.HandleFunc("/tests/{id}/activate", handlers.ActivateTest).Methods("POST")
	auth.HandleFunc("/tests/{id}/deactivate", handlers.DeactivateTest).Methods("POST")

	// Маршруты для работы с вопросами
	auth.HandleFunc("/questions", handlers.CreateQuestion).Methods("POST")
	auth.HandleFunc("/questions/{id}", handlers.GetQuestion).Methods("GET")
	auth.HandleFunc("/questions/{id}", handlers.UpdateQuestion).Methods("PUT")
	auth.HandleFunc("/questions/{id}", handlers.DeleteQuestion).Methods("DELETE")

	// Маршруты для работы с попытками
	auth.HandleFunc("/attempts", handlers.CreateAttempt).Methods("POST")
	auth.HandleFunc("/attempts/{id}", handlers.GetAttempt).Methods("GET")
	auth.HandleFunc("/attempts/{id}/answers", handlers.SubmitAnswer).Methods("POST")
	auth.HandleFunc("/attempts/{id}/complete", handlers.CompleteAttempt).Methods("POST")

	// Обработка 404 ошибки
	router.NotFoundHandler = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusNotFound)
		json.NewEncoder(w).Encode(map[string]string{"error": "Not found"})
	})

	// Запускаем сервер
	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}

	log.Printf("Server started on port %s", port)
	log.Fatal(http.ListenAndServe(":"+port, router))
}

// HealthCheck - проверяет работоспособность сервера
func HealthCheck(w http.ResponseWriter, r *http.Request) {
	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]string{"status": "ok"})
}
