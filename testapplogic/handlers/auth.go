package handlers

import (
	"context"
	"database/sql"
	"net/http"
	"strings"

	"github.com/golang-jwt/jwt/v5"
)

var jwtSecret []byte

// InitAuth инициализирует секрет для JWT
func InitAuth(secret string) {
	jwtSecret = []byte(secret)
}

// AuthMiddleware проверяет JWT-токен и кладёт user_id в контекст запроса
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

			email, ok := claims["email"].(string)
			if !ok {
				http.Error(w, "Email not found in token", http.StatusUnauthorized)
				return
			}

			var userID int
			err = db.QueryRow("SELECT id FROM users WHERE email = $1", email).Scan(&userID)
			if err != nil {
				if err == sql.ErrNoRows {
					http.Error(w, "User not found", http.StatusUnauthorized)
				} else {
					http.Error(w, "Database error", http.StatusInternalServerError)
				}
				return
			}

			ctx := context.WithValue(r.Context(), "user_id", userID)
			next.ServeHTTP(w, r.WithContext(ctx))
		})
	}
}

// GetUserID извлекает user_id из контекста запроса
func GetUserID(r *http.Request) (int, bool) {
	userID, ok := r.Context().Value("user_id").(int)
	return userID, ok
}

// CheckCourseAccess проверяет, имеет ли пользователь доступ к курсу
// (либо как студент, либо как преподаватель)
func CheckCourseAccess(db *sql.DB, r *http.Request, courseID int) bool {
	userID, ok := GetUserID(r)
	if !ok {
		return false
	}

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
