package handlers

import (
	"context"
	"database/sql"
	"net/http"
	"strings"

	"github.com/dgrijalva/jwt-go"
)

// AuthMiddleware — проверяет JWT и кладёт user_id в контекст
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
				return []byte(getConfig().JWTSecret), nil
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

			// Ищем пользователя в БД по email
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

func GetUserID(r *http.Request) (int, bool) {
	userID, ok := r.Context().Value("user_id").(int)
	return userID, ok
}

func GetUserInfo(db *sql.DB, r *http.Request) (*models.User, bool) {
	userID, ok := GetUserID(r)
	if !ok {
		return nil, false
	}

	var user models.User
	err := db.QueryRow(`
		SELECT id, email, full_name, created_at, updated_at
		FROM users
		WHERE id = $1`,
		userID,
	).Scan(&user.ID, &user.Email, &user.FullName, &user.CreatedAt, &user.UpdatedAt)

	if err != nil {
		return nil, false
	}

	rows, err := db.Query(`SELECT role FROM user_roles WHERE user_id = $1`, userID)
	if err != nil {

		user.Roles = []string{}
	} else {
		defer rows.Close()
		for rows.Next() {
			var role string
			if err := rows.Scan(&role); err == nil {
				user.Roles = append(user.Roles, role)
			}
		}
	}

	return &user, true
}

func CheckUserAccess(r *http.Request, targetUserID int) bool {
	userID, ok := GetUserID(r)
	return ok && userID == targetUserID
}

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
		)`, userID, courseID).Scan(&exists)

	return err == nil && exists
}

func CheckPermission(r *http.Request, requiredPermission string) bool {
	authHeader := r.Header.Get("Authorization")
	tokenString := strings.TrimPrefix(authHeader, "Bearer ")
	token, err := jwt.Parse(tokenString, func(token *jwt.Token) (interface{}, error) {
		if _, ok := token.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, jwt.ErrSignatureInvalid
		}
		return []byte(getConfig().JWTSecret), nil
	})

	if err != nil || !token.Valid {
		return false
	}

	claims, ok := token.Claims.(jwt.MapClaims)
	if !ok {
		return false
	}

	permissions, ok := claims["permissions"].([]interface{})
	if !ok {
		return false
	}

	for _, p := range permissions {
		if pStr, ok := p.(string); ok && pStr == requiredPermission {
			return true
		}
	}
	return false
}
