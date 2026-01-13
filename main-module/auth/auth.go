package auth //проверка прав и тд

import (
	"errors"
	"net/http"
	"strings"

	"github.com/dgrijalva/jwt-go"
)

var jwtSecret = []byte("super_secret_key_123")

// Claims структура для хранения данных в JWT
type Claims struct {
	Email       string   `json:"email"`
	Permissions []string `json:"permissions"`
	jwt.StandardClaims
}

// VerifyToken проверяет JWT-токен и возвращает права пользователя
func VerifyToken(tokenString string) (*Claims, error) {
	// Удаляем префикс "Bearer "
	tokenString = strings.TrimPrefix(tokenString, "Bearer ")

	token, err := jwt.ParseWithClaims(tokenString, &Claims{}, func(token *jwt.Token) (interface{}, error) {
		return jwtSecret, nil
	})

	if err != nil {
		return nil, err
	}

	if claims, ok := token.Claims.(*Claims); ok && token.Valid {
		return claims, nil
	}

	return nil, errors.New("некорректный токен")
}

// CheckPermission проверяет, есть ли у пользователя необходимое разрешение
func CheckPermission(r *http.Request, requiredPermission string) bool {
	authHeader := r.Header.Get("Authorization")
	if authHeader == "" {
		return false
	}

	tokenString := strings.TrimPrefix(authHeader, "Bearer ")
	claims, err := VerifyToken(tokenString)
	if err != nil {
		return false
	}

	// Проверяем, есть ли у пользователя необходимое разрешение
	for _, perm := range claims.Permissions {
		if perm == requiredPermission {
			return true
		}
	}

	return false
}

// GetEmailFromToken возвращает email из токена
func GetEmailFromToken(r *http.Request) string {
	authHeader := r.Header.Get("Authorization")
	if authHeader == "" {
		return ""
	}

	tokenString := strings.TrimPrefix(authHeader, "Bearer ")
	claims, err := VerifyToken(tokenString)
	if err != nil {
		return ""
	}

	return claims.Email
}
