package handlers

import (
	"context"
	"database/sql"
	"log"
	"net/http"
	"strings"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"github.com/lib/pq"
)

var jwtSecret []byte

// InitAuth инициализирует секрет для JWT
func InitAuth(secret string) {
	jwtSecret = []byte(secret)
}

// AuthMiddleware проверяет JWT-токен и кладёт user_id и user_id_reference в контекст запроса
func AuthMiddleware(db *sql.DB) func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			authHeader := r.Header.Get("Authorization")
			if authHeader == "" {
				http.Error(w, "Authorization header is required", http.StatusUnauthorized)
				return
			}

			tokenString := strings.TrimPrefix(authHeader, "Bearer ")
			token, err := jwt.Parse(tokenString, func(token *jwt.Token) (interface{}, error) {
				if _, ok := token.Method.(*jwt.SigningMethodHMAC); !ok {
					return nil, jwt.ErrSignatureInvalid
				}
				return jwtSecret, nil
			})

			if err != nil || !token.Valid {
				http.Error(w, "Invalid token", http.StatusUnauthorized)
				return
			}

			claims, ok := token.Claims.(jwt.MapClaims)
			if !ok {
				http.Error(w, "Invalid token claims", http.StatusUnauthorized)
				return
			}

			// Получаем user_id из токена (это email, строка)
			userIDRef, ok := claims["user_id"].(string)
			if !ok {
				http.Error(w, "User ID not found in token", http.StatusUnauthorized)
				return
			}

			// Получаем username для создания пользователя
			username, _ := claims["username"].(string)
			if username == "" {
				username = userIDRef
			}

			// Получаем разрешения из токена
			permissions, ok := claims["permissions"].([]interface{})
			if !ok {
				http.Error(w, "Permissions not found in token", http.StatusUnauthorized)
				return
			}

			// Преобразуем разрешения в строковый массив
			permissionStrings := make([]string, len(permissions))
			for i, p := range permissions {
				if str, ok := p.(string); ok {
					permissionStrings[i] = str
				}
			}

			// Ищем или создаём пользователя по user_id_reference
			var userID int
			err = db.QueryRow("SELECT id FROM users WHERE user_id_reference = $1", userIDRef).Scan(&userID)
			if err == sql.ErrNoRows {
				// Создаём нового пользователя
				err = db.QueryRow(`
					INSERT INTO users (user_id_reference, full_name, roles, created_at)
					VALUES ($1, $2, $3, $4)
					RETURNING id
				`, userIDRef, username, pq.Array([]string{"Student"}), time.Now()).Scan(&userID)
				if err != nil {
					http.Error(w, "Failed to create user in database", http.StatusInternalServerError)
					return
				}
			} else if err != nil {
				http.Error(w, "Database error during user lookup", http.StatusInternalServerError)
				return
			}

			// Кладем данные в контекст запроса
			ctx := context.WithValue(r.Context(), "user_id", userID) // целое число для SQL
			ctx = context.WithValue(ctx, "permissions", permissionStrings)
			ctx = context.WithValue(ctx, "user_id_reference", userIDRef) // строка для логов

			// Логируем запрос с информацией о пользователе
			LogRequestWithUser(r.WithContext(ctx), "AuthMiddleware")

			next.ServeHTTP(w, r.WithContext(ctx))
		})
	}
}

// GetUserID извлекает user_id (целое число) из контекста запроса
func GetUserID(r *http.Request) (int, bool) {
	userID, ok := r.Context().Value("user_id").(int)
	return userID, ok
}

// GetPermissions извлекает разрешения из контекста запроса
func GetPermissions(r *http.Request) ([]string, bool) {
	permissions, ok := r.Context().Value("permissions").([]string)
	return permissions, ok
}

// CheckPermission проверяет, есть ли у пользователя нужное разрешение
func CheckPermission(r *http.Request, requiredPermission string) bool {
	permissions, ok := GetPermissions(r)
	if !ok {
		return false
	}
	for _, p := range permissions {
		if p == requiredPermission {
			return true
		}
	}
	return false
}

// CheckCourseAccess проверяет, имеет ли пользователь доступ к курсу
// (либо как студент, либо как преподаватель)
func CheckCourseAccess(db *sql.DB, r *http.Request, courseID int) bool {
	userID, ok := GetUserID(r)
	if !ok {
		return false
	}

	// Сначала проверяем базовые разрешения
	if CheckPermission(r, "course:userList") || CheckPermission(r, "course:testList") {
		var exists bool
		err := db.QueryRow(`
			SELECT EXISTS(
				SELECT 1 FROM user_courses
				WHERE user_id = $1 AND course_id = $2
			) OR EXISTS(
				SELECT 1 FROM courses
				WHERE id = $2 AND teacher_id = $1
			)
		`, userID, courseID).Scan(&exists)
		return err == nil && exists
	}

	return false
}

// CheckTestAccess проверяет доступ к тесту и его вопросам
func CheckTestAccess(db *sql.DB, r *http.Request, testID int) bool {
	if !CheckPermission(r, "course:test:read") {
		return false
	}

	var courseID int
	err := db.QueryRow("SELECT course_id FROM tests WHERE id = $1", testID).Scan(&courseID)
	if err != nil {
		return false
	}

	return CheckCourseAccess(db, r, courseID)
}

// CheckQuestionAccess проверяет доступ к вопросу
func CheckQuestionAccess(db *sql.DB, r *http.Request, questionID int) bool {
	if !CheckPermission(r, "quest:read") {
		return false
	}

	var testID int
	err := db.QueryRow("SELECT test_id FROM questions WHERE id = $1", questionID).Scan(&testID)
	if err != nil {
		return false
	}

	return CheckTestAccess(db, r, testID)
}

// CheckAdminAccess проверяет права администратора
func CheckAdminAccess(r *http.Request) bool {
	return CheckPermission(r, "user:list:read") ||
		CheckPermission(r, "course:add") ||
		CheckPermission(r, "quest:create")
}

// LogRequestWithUser логирует запрос с информацией о пользователе
func LogRequestWithUser(r *http.Request, handlerName string) {
	userID, _ := GetUserID(r)
	permissions, _ := GetPermissions(r)
	userIDRef, _ := r.Context().Value("user_id_reference").(string)

	logMessage := map[string]interface{}{
		"timestamp":   time.Now().Format(time.RFC3339),
		"handler":     handlerName,
		"user_id":     userID,
		"user_ref":    userIDRef,
		"permissions": permissions,
		"method":      r.Method,
		"path":        r.URL.Path,
	}

	log.Printf("Request: %+v", logMessage)
}
