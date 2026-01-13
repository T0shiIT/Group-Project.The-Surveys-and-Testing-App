package handlers //обработчики для пользоватей

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"strconv"

	"testapp/auth"
)

// GetUserList возвращает список пользователей (требуется разрешение user:list:read)
func GetUserList(db *sql.DB) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		// Проверяем разрешение
		if !auth.CheckPermission(r, "user:list:read") {
			http.Error(w, "Доступ запрещен", http.StatusForbidden)
			return
		}

		// Получаем данные из БД
		rows, err := db.Query("SELECT id, email, full_name FROM users")
		if err != nil {
			http.Error(w, "Ошибка при получении пользователей", http.StatusInternalServerError)
			return
		}
		defer rows.Close()

		var users []db.User
		for rows.Next() {
			var user db.User
			err := rows.Scan(&user.ID, &user.Email, &user.FullName)
			if err != nil {
				http.Error(w, "Ошибка при обработке данных", http.StatusInternalServerError)
				return
			}
			users = append(users, user)
		}

		// Возвращаем JSON
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(users)
	}
}

// GetUser возвращает информацию о пользователе
func GetUser(db *sql.DB) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		// Проверяем, что пользователь запрашивает свои данные
		userIDStr := r.URL.Path[len("/users/"):]

		userID, err := strconv.Atoi(userIDStr)
		if err != nil {
			http.Error(w, "Неверный ID пользователя", http.StatusBadRequest)
			return
		}

		// Получаем email из токена
		email := auth.GetEmailFromToken(r)
		if email == "" {
			http.Error(w, "Требуется аутентификация", http.StatusUnauthorized)
			return
		}

		// Проверяем, что пользователь запрашивает свои данные
		var ownerID int
		err = db.QueryRow("SELECT id FROM users WHERE email = $1", email).Scan(&ownerID)
		if err != nil {
			http.Error(w, "Пользователь не найден", http.StatusNotFound)
			return
		}

		if userID != ownerID {
			http.Error(w, "Доступ разрешен только к своим данным", http.StatusForbidden)
			return
		}

		// Получаем данные пользователя
		var user db.User
		err = db.QueryRow("SELECT id, email, full_name FROM users WHERE id = $1", userID).
			Scan(&user.ID, &user.Email, &user.FullName)
		if err != nil {
			http.Error(w, "Пользователь не найден", http.StatusNotFound)
			return
		}

		// Возвращаем JSON
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(user)
	}
}
