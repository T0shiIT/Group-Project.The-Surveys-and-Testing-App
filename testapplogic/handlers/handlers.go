package handlers

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"time"

	"testapplogic/models"

	"github.com/gorilla/mux"
)

// DBHandler структура для передачи подключения к БД в обработчики
type DBHandler struct {
	DB *sql.DB
}

// HealthCheck проверяет работоспособность сервера и подключение к БД
func (h *DBHandler) HealthCheck(w http.ResponseWriter, r *http.Request) {
	var status string
	err := h.DB.QueryRow("SELECT 'ok'").Scan(&status)
	if err != nil {
		http.Error(w, "Database connection failed", http.StatusInternalServerError)
		return
	}

	w.WriteHeader(http.StatusOK)
	json.NewEncoder(w).Encode(map[string]string{"status": status})
}

// GetCourses возвращает список дисциплин
func (h *DBHandler) GetCourses(w http.ResponseWriter, r *http.Request) {
	if !CheckPermission(r, "course:list:read") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	// Запрашиваем курсы, доступные пользователю
	query := `
	SELECT c.id, c.name, c.description, c.teacher_id, c.is_active, c.created_at, c.updated_at
	FROM courses c
	JOIN user_courses uc ON c.id = uc.course_id
	WHERE uc.user_id = $1 AND c.is_deleted = false
	`

	rows, err := h.DB.Query(query, userID)
	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer rows.Close()

	var courses []models.Course
	for rows.Next() {
		var course models.Course
		err := rows.Scan(
			&course.ID,
			&course.Name,
			&course.Description,
			&course.TeacherID,
			&course.IsActive,
			&course.CreatedAt,
			&course.UpdatedAt,
		)
		if err != nil {
			http.Error(w, "Data parsing error: "+err.Error(), http.StatusInternalServerError)
			return
		}
		courses = append(courses, course)
	}

	if err = rows.Err(); err != nil {
		http.Error(w, "Row iteration error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(courses)
}

// GetCourse возвращает информацию о дисциплине
func (h *DBHandler) GetCourse(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	courseID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid course ID", http.StatusBadRequest)
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	if !CheckPermission(r, "course:info:read") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	var course models.Course
	err = h.DB.QueryRow(`
		SELECT id, name, description, teacher_id, is_active, created_at, updated_at
		FROM courses
		WHERE id = $1 AND is_deleted = false
	`, courseID).Scan(
		&course.ID,
		&course.Name,
		&course.Description,
		&course.TeacherID,
		&course.IsActive,
		&course.CreatedAt,
		&course.UpdatedAt,
	)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Course not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(course)
}

// CreateCourse создает новую дисциплину
func (h *DBHandler) CreateCourse(w http.ResponseWriter, r *http.Request) {
	if !CheckPermission(r, "course:add") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	var course models.Course
	if err := json.NewDecoder(r.Body).Decode(&course); err != nil {
		http.Error(w, "Invalid request body: "+err.Error(), http.StatusBadRequest)
		return
	}

	// Проверяем обязательные поля
	if course.Name == "" || course.Description == "" {
		http.Error(w, "Name and description are required", http.StatusBadRequest)
		return
	}

	// Получаем ID пользователя из контекста
	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	// Создаем курс в БД
	var newCourseID int
	now := time.Now()
	err := h.DB.QueryRow(`
		INSERT INTO courses (name, description, teacher_id, is_active, created_at, updated_at)
		VALUES ($1, $2, $3, true, $4, $4)
		RETURNING id
	`, course.Name, course.Description, userID, now).Scan(&newCourseID)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Создаем связь пользователя с курсом (преподаватель)
	_, err = h.DB.Exec(`
		INSERT INTO user_courses (user_id, course_id, role, created_at)
		VALUES ($1, $2, 'teacher', $3)
	`, userID, newCourseID, now)

	if err != nil {
		http.Error(w, "Failed to assign course to teacher: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Обновляем данные для ответа
	course.ID = newCourseID
	course.TeacherID = userID
	course.IsActive = true
	course.CreatedAt = now
	course.UpdatedAt = now

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	json.NewEncoder(w).Encode(course)
}

// GetCourseTests возвращает список тестов для дисциплины
func (h *DBHandler) GetCourseTests(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	courseID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid course ID", http.StatusBadRequest)
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	if !CheckPermission(r, "course:testList") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	rows, err := h.DB.Query(`
		SELECT id, name, course_id, is_active, is_deleted, created_at, updated_at
		FROM tests
		WHERE course_id = $1 AND is_deleted = false
	`, courseID)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer rows.Close()

	var tests []models.Test
	for rows.Next() {
		var test models.Test
		err := rows.Scan(
			&test.ID,
			&test.Name,
			&test.CourseID,
			&test.IsActive,
			&test.IsDeleted,
			&test.CreatedAt,
			&test.UpdatedAt,
		)
		if err != nil {
			http.Error(w, "Data parsing error: "+err.Error(), http.StatusInternalServerError)
			return
		}

		// Загружаем вопросы для теста
		test.Questions, err = h.getTestQuestions(test.ID)
		if err != nil {
			http.Error(w, "Failed to load test questions: "+err.Error(), http.StatusInternalServerError)
			return
		}

		tests = append(tests, test)
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(tests)
}

// Вспомогательная функция для загрузки вопросов теста
func (h *DBHandler) getTestQuestions(testID int) ([]int, error) {
	rows, err := h.DB.Query(`
		SELECT question_id
		FROM test_questions
		WHERE test_id = $1
		ORDER BY position
	`, testID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var questionIDs []int
	for rows.Next() {
		var id int
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		questionIDs = append(questionIDs, id)
	}
	return questionIDs, rows.Err()
}

// GetTest возвращает информацию о тесте
func (h *DBHandler) GetTest(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	testID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid test ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "course:test:read") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	var test models.Test
	err = h.DB.QueryRow(`
		SELECT id, name, course_id, is_active, is_deleted, created_at, updated_at
		FROM tests
		WHERE id = $1 AND is_deleted = false
	`, testID).Scan(
		&test.ID,
		&test.Name,
		&test.CourseID,
		&test.IsActive,
		&test.IsDeleted,
		&test.CreatedAt,
		&test.UpdatedAt,
	)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Test not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	// Загружаем вопросы для теста
	test.Questions, err = h.getTestQuestions(testID)
	if err != nil {
		http.Error(w, "Failed to load test questions: "+err.Error(), http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(test)
}

// ActivateTest активирует тест
func (h *DBHandler) ActivateTest(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	testID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid test ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "course:test:write") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	// Проверяем, что пользователь является преподавателем курса
	var courseID int
	err = h.DB.QueryRow(`
		SELECT course_id FROM tests WHERE id = $1
	`, testID).Scan(&courseID)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Test not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden: You are not a teacher of this course", http.StatusForbidden)
		return
	}

	now := time.Now()
	_, err = h.DB.Exec(`
		UPDATE tests
		SET is_active = true, updated_at = $1
		WHERE id = $2 AND is_deleted = false
	`, now, testID)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	response := map[string]interface{}{
		"id":         testID,
		"activated":  true,
		"message":    "Test activated successfully",
		"updated_at": now.Format(time.RFC3339),
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(response)
}

// DeactivateTest деактивирует тест
func (h *DBHandler) DeactivateTest(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	testID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid test ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "course:test:write") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	// Проверяем, что пользователь является преподавателем курса
	var courseID int
	err = h.DB.QueryRow(`
		SELECT course_id FROM tests WHERE id = $1
	`, testID).Scan(&courseID)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Test not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden: You are not a teacher of this course", http.StatusForbidden)
		return
	}

	now := time.Now()
	_, err = h.DB.Exec(`
		UPDATE tests
		SET is_active = false, updated_at = $1
		WHERE id = $2 AND is_deleted = false
	`, now, testID)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	response := map[string]interface{}{
		"id":          testID,
		"deactivated": true,
		"message":     "Test deactivated successfully",
		"updated_at":  now.Format(time.RFC3339),
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(response)
}

// CreateQuestion создает новый вопрос
func (h *DBHandler) CreateQuestion(w http.ResponseWriter, r *http.Request) {
	if !CheckPermission(r, "quest:create") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	var question models.Question
	if err := json.NewDecoder(r.Body).Decode(&question); err != nil {
		http.Error(w, "Invalid request body: "+err.Error(), http.StatusBadRequest)
		return
	}

	// Валидация вопроса
	if question.Text == "" || len(question.Options) < 2 || question.Correct < 0 || question.Correct >= len(question.Options) {
		http.Error(w, "Invalid question data", http.StatusBadRequest)
		return
	}

	// Проверяем, что пользователь имеет доступ к курсу
	if !CheckCourseAccess(h.DB, r, question.CourseID) {
		http.Error(w, "Forbidden: No access to this course", http.StatusForbidden)
		return
	}

	now := time.Now()
	tx, err := h.DB.Begin()
	if err != nil {
		http.Error(w, "Transaction error: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer tx.Rollback()

	// Вставляем вопрос
	var questionID int
	err = tx.QueryRow(`
		INSERT INTO questions (text, correct, course_id, version, is_deleted, created_at, updated_at)
		VALUES ($1, $2, $3, 1, false, $4, $4)
		RETURNING id
	`, question.Text, question.Correct, question.CourseID, now).Scan(&questionID)

	if err != nil {
		http.Error(w, "Failed to create question: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Вставляем варианты ответов
	for i, option := range question.Options {
		_, err = tx.Exec(`
			INSERT INTO question_options (question_id, option_index, text, created_at)
			VALUES ($1, $2, $3, $4)
		`, questionID, i, option, now)
		if err != nil {
			http.Error(w, "Failed to save options: "+err.Error(), http.StatusInternalServerError)
			return
		}
	}

	// Фиксируем транзакцию
	if err = tx.Commit(); err != nil {
		http.Error(w, "Transaction commit failed: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Обновляем данные для ответа
	question.ID = questionID
	question.Version = 1
	question.CreatedAt = now
	question.UpdatedAt = now

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	json.NewEncoder(w).Encode(question)
}

// GetQuestion возвращает информацию о вопросе
func (h *DBHandler) GetQuestion(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	questionID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid question ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "quest:read") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	// Загружаем основные данные вопроса
	var question models.Question
	err = h.DB.QueryRow(`
		SELECT id, text, correct, course_id, version, is_deleted, created_at, updated_at
		FROM questions
		WHERE id = $1 AND is_deleted = false
	`, questionID).Scan(
		&question.ID,
		&question.Text,
		&question.Correct,
		&question.CourseID,
		&question.Version,
		&question.IsDeleted,
		&question.CreatedAt,
		&question.UpdatedAt,
	)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Question not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	// Загружаем варианты ответов
	rows, err := h.DB.Query(`
		SELECT option_index, text
		FROM question_options
		WHERE question_id = $1
		ORDER BY option_index
	`, questionID)

	if err != nil {
		http.Error(w, "Failed to load options: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer rows.Close()

	question.Options = []string{}
	optionsMap := make(map[int]string)
	for rows.Next() {
		var idx int
		var text string
		if err := rows.Scan(&idx, &text); err != nil {
			http.Error(w, "Data parsing error: "+err.Error(), http.StatusInternalServerError)
			return
		}
		optionsMap[idx] = text
	}

	// Восстанавливаем порядок вариантов
	for i := 0; i < len(optionsMap); i++ {
		if text, exists := optionsMap[i]; exists {
			question.Options = append(question.Options, text)
		}
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(question)
}

// UpdateQuestion обновляет вопрос
func (h *DBHandler) UpdateQuestion(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	questionID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid question ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "quest:update") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	var question models.Question
	if err := json.NewDecoder(r.Body).Decode(&question); err != nil {
		http.Error(w, "Invalid request body: "+err.Error(), http.StatusBadRequest)
		return
	}

	// Валидация вопроса
	if question.Text == "" || len(question.Options) < 2 || question.Correct < 0 || question.Correct >= len(question.Options) {
		http.Error(w, "Invalid question data", http.StatusBadRequest)
		return
	}

	// Проверяем, что пользователь имеет доступ к курсу, к которому относится вопрос
	var courseID int
	err = h.DB.QueryRow(`
		SELECT course_id FROM questions WHERE id = $1 AND is_deleted = false
	`, questionID).Scan(&courseID)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Question not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden: No access to this course", http.StatusForbidden)
		return
	}

	now := time.Now()
	tx, err := h.DB.Begin()
	if err != nil {
		http.Error(w, "Transaction error: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer tx.Rollback()

	// Обновляем вопрос и увеличиваем версию
	_, err = tx.Exec(`
		UPDATE questions
		SET text = $1, correct = $2, version = version + 1, updated_at = $3
		WHERE id = $4 AND is_deleted = false
	`, question.Text, question.Correct, now, questionID)

	if err != nil {
		http.Error(w, "Failed to update question: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Удаляем старые варианты ответов
	_, err = tx.Exec(`
		DELETE FROM question_options
		WHERE question_id = $1
	`, questionID)

	if err != nil {
		http.Error(w, "Failed to delete old options: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Вставляем новые варианты ответов
	for i, option := range question.Options {
		_, err = tx.Exec(`
			INSERT INTO question_options (question_id, option_index, text, created_at)
			VALUES ($1, $2, $3, $4)
		`, questionID, i, option, now)
		if err != nil {
			http.Error(w, "Failed to save new options: "+err.Error(), http.StatusInternalServerError)
			return
		}
	}

	// Фиксируем транзакцию
	if err = tx.Commit(); err != nil {
		http.Error(w, "Transaction commit failed: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Обновляем данные для ответа (загружаем новую версию)
	err = h.DB.QueryRow(`
		SELECT version FROM questions WHERE id = $1
	`, questionID).Scan(&question.Version)

	if err != nil {
		http.Error(w, "Failed to get updated version: "+err.Error(), http.StatusInternalServerError)
		return
	}

	question.ID = questionID
	question.CourseID = courseID
	question.UpdatedAt = now

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(question)
}

// DeleteQuestion удаляет вопрос
func (h *DBHandler) DeleteQuestion(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	questionID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid question ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "quest:del") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	// Проверяем, что пользователь имеет доступ к курсу вопроса
	var courseID int
	err = h.DB.QueryRow(`
		SELECT course_id FROM questions WHERE id = $1 AND is_deleted = false
	`, questionID).Scan(&courseID)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Question not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden: No access to this course", http.StatusForbidden)
		return
	}

	now := time.Now()
	_, err = h.DB.Exec(`
		UPDATE questions
		SET is_deleted = true, updated_at = $1
		WHERE id = $2 AND is_deleted = false
	`, now, questionID)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	response := map[string]interface{}{
		"id":      questionID,
		"deleted": true,
		"message": "Question marked as deleted",
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(response)
}

// CreateAttempt создает новую попытку прохождения теста
func (h *DBHandler) CreateAttempt(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	testID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid test ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "test:answer:read") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	// Проверяем, что тест активен
	var testActive bool
	err = h.DB.QueryRow(`
		SELECT is_active FROM tests WHERE id = $1 AND is_deleted = false
	`, testID).Scan(&testActive)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Test not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	if !testActive {
		http.Error(w, "Test is not active", http.StatusBadRequest)
		return
	}

	// Проверяем, что у пользователя нет активной попытки
	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	var activeAttemptExists bool
	err = h.DB.QueryRow(`
		SELECT EXISTS(
			SELECT 1 FROM attempts
			WHERE user_id = $1 AND test_id = $2 AND is_complete = false
		)
	`, userID, testID).Scan(&activeAttemptExists)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	if activeAttemptExists {
		http.Error(w, "You already have an active attempt for this test", http.StatusBadRequest)
		return
	}

	// Создаем новую попытку
	now := time.Now()
	var attemptID int
	err = h.DB.QueryRow(`
		INSERT INTO attempts (user_id, test_id, is_complete, created_at, updated_at)
		VALUES ($1, $2, false, $3, $3)
		RETURNING id
	`, userID, testID, now).Scan(&attemptID)

	if err != nil {
		http.Error(w, "Failed to create attempt: "+err.Error(), http.StatusInternalServerError)
		return
	}

	attempt := models.Attempt{
		ID:         attemptID,
		UserID:     userID,
		TestID:     testID,
		IsComplete: false,
		CreatedAt:  now,
		UpdatedAt:  now,
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	json.NewEncoder(w).Encode(attempt)
}

// GetAttempt возвращает информацию о попытке
func (h *DBHandler) GetAttempt(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	attemptID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid attempt ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "test:answer:read") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	// Проверяем, что попытка принадлежит пользователю или пользователь является преподавателем курса
	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	var attempt models.Attempt
	var testID int
	err = h.DB.QueryRow(`
		SELECT a.id, a.user_id, a.test_id, a.is_complete, a.created_at, a.updated_at, t.course_id
		FROM attempts a
		JOIN tests t ON a.test_id = t.id
		WHERE a.id = $1
	`, attemptID).Scan(
		&attempt.ID,
		&attempt.UserID,
		&testID,
		&attempt.IsComplete,
		&attempt.CreatedAt,
		&attempt.UpdatedAt,
		&courseID,
	)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Attempt not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	// Проверяем доступ: либо это их попытка, либо они преподаватель курса
	isOwner := attempt.UserID == userID
	isTeacher := false
	if !isOwner {
		isTeacher = CheckCourseAccess(h.DB, r, courseID)
	}

	if !isOwner && !isTeacher {
		http.Error(w, "Forbidden: No access to this attempt", http.StatusForbidden)
		return
	}

	// Загружаем ответы для попытки
	rows, err := h.DB.Query(`
		SELECT id, question_id, option, attempt_id, created_at
		FROM answers
		WHERE attempt_id = $1
		ORDER BY created_at
	`, attemptID)

	if err != nil {
		http.Error(w, "Failed to load answers: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer rows.Close()

	attempt.Answers = []models.Answer{}
	for rows.Next() {
		var answer models.Answer
		err := rows.Scan(
			&answer.ID,
			&answer.QuestionID,
			&answer.Option,
			&answer.AttemptID,
			&answer.CreatedAt,
		)
		if err != nil {
			http.Error(w, "Data parsing error: "+err.Error(), http.StatusInternalServerError)
			return
		}
		attempt.Answers = append(attempt.Answers, answer)
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(attempt)
}

// SubmitAnswer отправляет ответ на вопрос
func (h *DBHandler) SubmitAnswer(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	attemptID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid attempt ID", http.StatusBadRequest)
		return
	}

	var answer models.Answer
	if err := json.NewDecoder(r.Body).Decode(&answer); err != nil {
		http.Error(w, "Invalid request body: "+err.Error(), http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "answer:update") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	// Проверяем, что попытка принадлежит пользователю
	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	var attemptUserID int
	var isComplete bool
	err = h.DB.QueryRow(`
		SELECT user_id, is_complete FROM attempts WHERE id = $1
	`, attemptID).Scan(&attemptUserID, &isComplete)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Attempt not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	if attemptUserID != userID {
		http.Error(w, "Forbidden: This attempt does not belong to you", http.StatusForbidden)
		return
	}

	if isComplete {
		http.Error(w, "Attempt is already completed", http.StatusBadRequest)
		return
	}

	// Проверяем, что вопрос существует и относится к тесту этой попытки
	var testID int
	err = h.DB.QueryRow(`
		SELECT test_id FROM attempts WHERE id = $1
	`, attemptID).Scan(&testID)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	var questionExists bool
	err = h.DB.QueryRow(`
		SELECT EXISTS(
			SELECT 1
			FROM test_questions tq
			JOIN questions q ON tq.question_id = q.id
			WHERE tq.test_id = $1 AND q.id = $2 AND q.is_deleted = false
		)
	`, testID, answer.QuestionID).Scan(&questionExists)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	if !questionExists {
		http.Error(w, "Question does not exist in this test", http.StatusBadRequest)
		return
	}

	// Сохраняем ответ
	now := time.Now()
	var answerID int
	err = h.DB.QueryRow(`
		INSERT INTO answers (question_id, option, attempt_id, created_at)
		VALUES ($1, $2, $3, $4)
		RETURNING id
	`, answer.QuestionID, answer.Option, attemptID, now).Scan(&answerID)

	if err != nil {
		http.Error(w, "Failed to save answer: "+err.Error(), http.StatusInternalServerError)
		return
	}

	answer.ID = answerID
	answer.AttemptID = attemptID
	answer.CreatedAt = now

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(answer)
}

// CompleteAttempt завершает попытку
func (h *DBHandler) CompleteAttempt(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	attemptID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid attempt ID", http.StatusBadRequest)
		return
	}

	if !CheckPermission(r, "test:answer:read") {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	// Проверяем, что попытка принадлежит пользователю
	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	var attemptUserID int
	var isComplete bool
	err = h.DB.QueryRow(`
		SELECT user_id, is_complete FROM attempts WHERE id = $1
	`, attemptID).Scan(&attemptUserID, &isComplete)

	if err != nil {
		if err == sql.ErrNoRows {
			http.Error(w, "Attempt not found", http.StatusNotFound)
		} else {
			http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		}
		return
	}

	if attemptUserID != userID {
		http.Error(w, "Forbidden: This attempt does not belong to you", http.StatusForbidden)
		return
	}

	if isComplete {
		http.Error(w, "Attempt is already completed", http.StatusBadRequest)
		return
	}

	// Проверяем, что пользователь ответил на все вопросы теста
	var testID int
	err = h.DB.QueryRow(`
		SELECT test_id FROM attempts WHERE id = $1
	`, attemptID).Scan(&testID)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	var totalQuestions, answeredQuestions int
	err = h.DB.QueryRow(`
		SELECT 
			(SELECT COUNT(*) FROM test_questions WHERE test_id = $1) as total,
			(SELECT COUNT(DISTINCT question_id) FROM answers WHERE attempt_id = $2) as answered
	`, testID, attemptID).Scan(&totalQuestions, &answeredQuestions)

	if err != nil {
		http.Error(w, "Database error: "+err.Error(), http.StatusInternalServerError)
		return
	}

	if answeredQuestions < totalQuestions {
		http.Error(w, fmt.Sprintf("Not all questions answered. Answered: %d of %d", answeredQuestions, totalQuestions), http.StatusBadRequest)
		return
	}

	// Завершаем попытку
	now := time.Now()
	_, err = h.DB.Exec(`
		UPDATE attempts
		SET is_complete = true, updated_at = $1
		WHERE id = $2
	`, now, attemptID)

	if err != nil {
		http.Error(w, "Failed to complete attempt: "+err.Error(), http.StatusInternalServerError)
		return
	}

	response := map[string]interface{}{
		"id":           attemptID,
		"completed":    true,
		"message":      "Attempt completed successfully",
		"completed_at": now.Format(time.RFC3339),
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(response)
}
