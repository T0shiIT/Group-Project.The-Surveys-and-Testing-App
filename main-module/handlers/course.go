package handlers // тоже ток для дисциплин

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"strconv"

	"testapp/auth"
)

// GetCourseList возвращает список дисциплин
func GetCourseList(db *sql.DB) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		// Проверяем, что пользователь аутентифицирован
		if auth.GetEmailFromToken(r) == "" {
			http.Error(w, "Требуется аутентификация", http.StatusUnauthorized)
			return
		}

		// Получаем данные из БД
		rows, err := db.Query("SELECT id, name, description, teacher_id FROM courses")
		if err != nil {
			http.Error(w, "Ошибка при получении дисциплин", http.StatusInternalServerError)
			return
		}
		defer rows.Close()

		var courses []db.Course
		for rows.Next() {
			var course db.Course
			err := rows.Scan(&course.ID, &course.Name, &course.Description, &course.TeacherID)
			if err != nil {
				http.Error(w, "Ошибка при обработке данных", http.StatusInternalServerError)
				return
			}
			courses = append(courses, course)
		}

		// Возвращаем JSON
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(courses)
	}
}

// GetCourse возвращает информацию о дисциплине
func GetCourse(db *sql.DB) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		// Проверяем, что пользователь аутентифицирован
		if auth.GetEmailFromToken(r) == "" {
			http.Error(w, "Требуется аутентификация", http.StatusUnauthorized)
			return
		}

		// Получаем ID дисциплины из URL
		courseIDStr := r.URL.Path[len("/courses/"):]

		courseID, err := strconv.Atoi(courseIDStr)
		if err != nil {
			http.Error(w, "Неверный ID дисциплины", http.StatusBadRequest)
			return
		}

		// Проверяем, что дисциплина существует
		var course db.Course
		err = db.QueryRow("SELECT id, name, description, teacher_id FROM courses WHERE id = $1", courseID).
			Scan(&course.ID, &course.Name, &course.Description, &course.TeacherID)
		if err != nil {
			http.Error(w, "Дисциплина не найдена", http.StatusNotFound)
			return
		}

		// Возвращаем JSON
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(course)
	}
}
