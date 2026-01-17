package handlers

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"strconv"
	"testapplogic/models"
	"time"

	"github.com/gorilla/mux"
	"github.com/lib/pq"
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

// GetCourses возвращает список дисциплин, доступных пользователю
func (h *DBHandler) GetCourses(w http.ResponseWriter, r *http.Request) {
	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	rows, err := h.DB.Query(`
		SELECT c.id, c.name, c.description, c.teacher_id, c.created_at
		FROM courses c
		WHERE c.teacher_id = $1
		   OR EXISTS(SELECT 1 FROM user_courses uc WHERE uc.user_id = $1 AND uc.course_id = c.id)
	`, userID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}
	defer rows.Close()

	var courses []models.Course
	for rows.Next() {
		var c models.Course
		err := rows.Scan(&c.ID, &c.Name, &c.Description, &c.TeacherID, &c.CreatedAt)
		if err != nil {
			http.Error(w, "Scan error", http.StatusInternalServerError)
			return
		}
		courses = append(courses, c)
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

	var c models.Course
	err = h.DB.QueryRow(`
		SELECT id, name, description, teacher_id, created_at
		FROM courses
		WHERE id = $1
	`, courseID).Scan(&c.ID, &c.Name, &c.Description, &c.TeacherID, &c.CreatedAt)

	if err == sql.ErrNoRows {
		http.NotFound(w, r)
		return
	} else if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(c)
}

// CreateCourse создаёт новую дисциплину
func (h *DBHandler) CreateCourse(w http.ResponseWriter, r *http.Request) {
	var input struct {
		Name        string `json:"name"`
		Description string `json:"description"`
	}
	if err := json.NewDecoder(r.Body).Decode(&input); err != nil {
		http.Error(w, "Invalid JSON", http.StatusBadRequest)
		return
	}

	if input.Name == "" || input.Description == "" {
		http.Error(w, "Name and description are required", http.StatusBadRequest)
		return
	}

	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	now := time.Now()
	tx, err := h.DB.Begin()
	if err != nil {
		http.Error(w, "Transaction error", http.StatusInternalServerError)
		return
	}
	defer tx.Rollback()

	var courseID int
	err = tx.QueryRow(`
		INSERT INTO courses (name, description, teacher_id, created_at)
		VALUES ($1, $2, $3, $4) RETURNING id
	`, input.Name, input.Description, userID, now).Scan(&courseID)
	if err != nil {
		http.Error(w, "Failed to create course", http.StatusInternalServerError)
		return
	}

	_, err = tx.Exec(`
		INSERT INTO user_courses (user_id, course_id, role, created_at)
		VALUES ($1, $2, 'teacher', $3)
	`, userID, courseID, now)
	if err != nil {
		http.Error(w, "Failed to assign teacher role", http.StatusInternalServerError)
		return
	}

	if err = tx.Commit(); err != nil {
		http.Error(w, "Transaction commit failed", http.StatusInternalServerError)
		return
	}

	course := models.Course{
		ID:          courseID,
		Name:        input.Name,
		Description: input.Description,
		TeacherID:   userID,
		CreatedAt:   now,
	}

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

	rows, err := h.DB.Query(`
		SELECT id, name, course_id, active, created_at
		FROM tests
		WHERE course_id = $1
	`, courseID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}
	defer rows.Close()

	var tests []models.Test
	for rows.Next() {
		var t models.Test
		err := rows.Scan(&t.ID, &t.Name, &t.CourseID, &t.Active, &t.CreatedAt)
		if err != nil {
			http.Error(w, "Scan error", http.StatusInternalServerError)
			return
		}

		// Загружаем ID вопросов
		qRows, err := h.DB.Query("SELECT id FROM questions WHERE test_id = $1", t.ID)
		if err != nil {
			continue
		}
		for qRows.Next() {
			var qid int
			qRows.Scan(&qid)
			t.Questions = append(t.Questions, qid)
		}
		qRows.Close()

		tests = append(tests, t)
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(tests)
}

// GetTest возвращает информацию о тесте
func (h *DBHandler) GetTest(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	testID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid test ID", http.StatusBadRequest)
		return
	}

	var t models.Test
	err = h.DB.QueryRow(`
		SELECT id, name, course_id, active, created_at
		FROM tests
		WHERE id = $1
	`, testID).Scan(&t.ID, &t.Name, &t.CourseID, &t.Active, &t.CreatedAt)

	if err == sql.ErrNoRows {
		http.NotFound(w, r)
		return
	} else if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	// Загружаем вопросы
	qRows, err := h.DB.Query("SELECT id FROM questions WHERE test_id = $1", testID)
	if err == nil {
		for qRows.Next() {
			var qid int
			qRows.Scan(&qid)
			t.Questions = append(t.Questions, qid)
		}
		qRows.Close()
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(t)
}

// ActivateTest активирует тест
func (h *DBHandler) ActivateTest(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	testID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid test ID", http.StatusBadRequest)
		return
	}

	var courseID int
	err = h.DB.QueryRow("SELECT course_id FROM tests WHERE id = $1", testID).Scan(&courseID)
	if err != nil {
		http.Error(w, "Test not found", http.StatusNotFound)
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	_, err = h.DB.Exec("UPDATE tests SET active = true WHERE id = $1", testID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"message": "Test activated",
		"id":      testID,
	})
}

// DeactivateTest деактивирует тест
func (h *DBHandler) DeactivateTest(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	testID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid test ID", http.StatusBadRequest)
		return
	}

	var courseID int
	err = h.DB.QueryRow("SELECT course_id FROM tests WHERE id = $1", testID).Scan(&courseID)
	if err != nil {
		http.Error(w, "Test not found", http.StatusNotFound)
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	_, err = h.DB.Exec("UPDATE tests SET active = false WHERE id = $1", testID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]interface{}{
		"message": "Test deactivated",
		"id":      testID,
	})
}

// CreateQuestion создаёт новый вопрос
func (h *DBHandler) CreateQuestion(w http.ResponseWriter, r *http.Request) {
	var input struct {
		TestID        int      `json:"test_id"`
		Text          string   `json:"text"`
		Options       []string `json:"options"`
		CorrectAnswer int      `json:"correct_answer"`
	}
	if err := json.NewDecoder(r.Body).Decode(&input); err != nil {
		http.Error(w, "Invalid JSON", http.StatusBadRequest)
		return
	}

	if input.Text == "" || len(input.Options) < 2 {
		http.Error(w, "Text and at least 2 options are required", http.StatusBadRequest)
		return
	}
	if input.CorrectAnswer < 0 || input.CorrectAnswer >= len(input.Options) {
		http.Error(w, "Correct answer index out of range", http.StatusBadRequest)
		return
	}

	// Проверяем, что пользователь имеет доступ к курсу этого теста
	var courseID int
	err := h.DB.QueryRow("SELECT course_id FROM tests WHERE id = $1", input.TestID).Scan(&courseID)
	if err != nil {
		http.Error(w, "Test not found", http.StatusNotFound)
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	now := time.Now()
	var questionID int
	err = h.DB.QueryRow(`
		INSERT INTO questions (test_id, text, options, correct_answer, created_at)
		VALUES ($1, $2, $3, $4, $5) RETURNING id
	`, input.TestID, input.Text, input.Options, input.CorrectAnswer, now).Scan(&questionID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	question := models.Question{
		ID:            questionID,
		TestID:        input.TestID,
		Text:          input.Text,
		Options:       input.Options,
		CorrectAnswer: input.CorrectAnswer,
		CreatedAt:     now,
	}

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

	var q models.Question
	err = h.DB.QueryRow(`
		SELECT id, test_id, text, options, correct_answer, created_at
		FROM questions
		WHERE id = $1
	`, questionID).Scan(&q.ID, &q.TestID, &q.Text, pq.Array(&q.Options), &q.CorrectAnswer, &q.CreatedAt)

	if err == sql.ErrNoRows {
		http.NotFound(w, r)
		return
	} else if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(q)
}

// UpdateQuestion обновляет вопрос
func (h *DBHandler) UpdateQuestion(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	questionID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid question ID", http.StatusBadRequest)
		return
	}

	var input struct {
		Text          string   `json:"text"`
		Options       []string `json:"options"`
		CorrectAnswer int      `json:"correct_answer"`
	}
	if err := json.NewDecoder(r.Body).Decode(&input); err != nil {
		http.Error(w, "Invalid JSON", http.StatusBadRequest)
		return
	}

	if input.Text == "" || len(input.Options) < 2 {
		http.Error(w, "Text and at least 2 options are required", http.StatusBadRequest)
		return
	}
	if input.CorrectAnswer < 0 || input.CorrectAnswer >= len(input.Options) {
		http.Error(w, "Correct answer index out of range", http.StatusBadRequest)
		return
	}

	// Проверяем доступ через курс
	var courseID int
	err = h.DB.QueryRow(`
		SELECT c.id
		FROM questions q
		JOIN tests t ON q.test_id = t.id
		JOIN courses c ON t.course_id = c.id
		WHERE q.id = $1
	`, questionID).Scan(&courseID)
	if err != nil {
		http.Error(w, "Question not found", http.StatusNotFound)
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	_, err = h.DB.Exec(`
		UPDATE questions
		SET text = $1, options = $2, correct_answer = $3, created_at = CURRENT_TIMESTAMP
		WHERE id = $4
	`, input.Text, input.Options, input.CorrectAnswer, questionID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	// Возвращаем обновлённый вопрос
	var q models.Question
	h.DB.QueryRow(`
		SELECT id, test_id, text, options, correct_answer, created_at
		FROM questions WHERE id = $1
	`, questionID).Scan(&q.ID, &q.TestID, &q.Text, pq.Array(&q.Options), &q.CorrectAnswer, &q.CreatedAt)

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(q)
}

// DeleteQuestion удаляет вопрос (на самом деле — физически, так как нет is_deleted)
func (h *DBHandler) DeleteQuestion(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	questionID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid question ID", http.StatusBadRequest)
		return
	}

	// Проверяем доступ
	var courseID int
	err = h.DB.QueryRow(`
		SELECT c.id
		FROM questions q
		JOIN tests t ON q.test_id = t.id
		JOIN courses c ON t.course_id = c.id
		WHERE q.id = $1
	`, questionID).Scan(&courseID)
	if err != nil {
		http.Error(w, "Question not found", http.StatusNotFound)
		return
	}

	if !CheckCourseAccess(h.DB, r, courseID) {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	_, err = h.DB.Exec("DELETE FROM questions WHERE id = $1", questionID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]string{"message": "Question deleted"})
}

// CreateAttempt создаёт новую попытку прохождения теста
func (h *DBHandler) CreateAttempt(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	testID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid test ID", http.StatusBadRequest)
		return
	}

	// Проверяем, что тест активен
	var active bool
	err = h.DB.QueryRow("SELECT active FROM tests WHERE id = $1", testID).Scan(&active)
	if err != nil {
		http.Error(w, "Test not found", http.StatusNotFound)
		return
	}
	if !active {
		http.Error(w, "Test is not active", http.StatusBadRequest)
		return
	}

	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	// Проверяем, нет ли уже активной попытки
	var exists bool
	h.DB.QueryRow("SELECT EXISTS(SELECT 1 FROM attempts WHERE user_id = $1 AND test_id = $2 AND finished = false)", userID, testID).Scan(&exists)
	if exists {
		http.Error(w, "You already have an active attempt", http.StatusBadRequest)
		return
	}

	now := time.Now()
	var attemptID int
	err = h.DB.QueryRow(`
		INSERT INTO attempts (user_id, test_id, finished, created_at)
		VALUES ($1, $2, false, $3) RETURNING id
	`, userID, testID, now).Scan(&attemptID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	attempt := models.Attempt{
		ID:        attemptID,
		UserID:    userID,
		TestID:    testID,
		Finished:  false,
		CreatedAt: now,
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

	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	// Проверяем, принадлежит ли попытка пользователю или он преподаватель курса
	var ownerID, testID, courseID int
	err = h.DB.QueryRow(`
		SELECT a.user_id, a.test_id, t.course_id
		FROM attempts a
		JOIN tests t ON a.test_id = t.id
		WHERE a.id = $1
	`, attemptID).Scan(&ownerID, &testID, &courseID)
	if err != nil {
		http.Error(w, "Attempt not found", http.StatusNotFound)
		return
	}

	isOwner := (ownerID == userID)
	isTeacher := false
	if !isOwner {
		isTeacher = CheckCourseAccess(h.DB, r, courseID)
	}
	if !isOwner && !isTeacher {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}

	var a models.Attempt
	err = h.DB.QueryRow(`
		SELECT id, user_id, test_id, finished, created_at
		FROM attempts WHERE id = $1
	`, attemptID).Scan(&a.ID, &a.UserID, &a.TestID, &a.Finished, &a.CreatedAt)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	// Загружаем ответы
	rows, err := h.DB.Query("SELECT id, question_id, answer, attempt_id, created_at FROM answers WHERE attempt_id = $1", attemptID)
	if err == nil {
		defer rows.Close()
		for rows.Next() {
			var ans models.Answer
			rows.Scan(&ans.ID, &ans.QuestionID, &ans.Answer, &ans.AttemptID, &ans.CreatedAt)
			a.Answers = append(a.Answers, ans)
		}
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(a)
}

// SubmitAnswer отправляет ответ на вопрос
func (h *DBHandler) SubmitAnswer(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	attemptID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid attempt ID", http.StatusBadRequest)
		return
	}

	var input struct {
		QuestionID int `json:"question_id"`
		Answer     int `json:"answer"`
	}
	if err := json.NewDecoder(r.Body).Decode(&input); err != nil {
		http.Error(w, "Invalid JSON", http.StatusBadRequest)
		return
	}

	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	// Проверяем, что попытка принадлежит пользователю и не завершена
	var dbUserID int
	var finished bool
	err = h.DB.QueryRow("SELECT user_id, finished FROM attempts WHERE id = $1", attemptID).Scan(&dbUserID, &finished)
	if err != nil {
		http.Error(w, "Attempt not found", http.StatusNotFound)
		return
	}
	if dbUserID != userID {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}
	if finished {
		http.Error(w, "Attempt is already finished", http.StatusBadRequest)
		return
	}

	// Проверяем, что вопрос существует в этом тесте
	var testID int
	h.DB.QueryRow("SELECT test_id FROM attempts WHERE id = $1", attemptID).Scan(&testID)
	var exists bool
	h.DB.QueryRow("SELECT EXISTS(SELECT 1 FROM questions WHERE id = $1 AND test_id = $2)", input.QuestionID, testID).Scan(&exists)
	if !exists {
		http.Error(w, "Question not found in this test", http.StatusBadRequest)
		return
	}

	// Сохраняем ответ
	now := time.Now()
	var answerID int
	err = h.DB.QueryRow(`
		INSERT INTO answers (attempt_id, question_id, answer, created_at)
		VALUES ($1, $2, $3, $4) RETURNING id
	`, attemptID, input.QuestionID, input.Answer, now).Scan(&answerID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	ans := models.Answer{
		ID:         answerID,
		AttemptID:  attemptID,
		QuestionID: input.QuestionID,
		Answer:     input.Answer,
		CreatedAt:  now,
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(ans)
}

// CompleteAttempt завершает попытку
func (h *DBHandler) CompleteAttempt(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	attemptID, err := strconv.Atoi(vars["id"])
	if err != nil {
		http.Error(w, "Invalid attempt ID", http.StatusBadRequest)
		return
	}

	userID, ok := GetUserID(r)
	if !ok {
		http.Error(w, "User not authenticated", http.StatusUnauthorized)
		return
	}

	var dbUserID int
	var finished bool
	err = h.DB.QueryRow("SELECT user_id, finished FROM attempts WHERE id = $1", attemptID).Scan(&dbUserID, &finished)
	if err != nil {
		http.Error(w, "Attempt not found", http.StatusNotFound)
		return
	}
	if dbUserID != userID {
		http.Error(w, "Forbidden", http.StatusForbidden)
		return
	}
	if finished {
		http.Error(w, "Attempt is already finished", http.StatusBadRequest)
		return
	}

	// Проверяем, ответил ли на все вопросы
	var testID int
	h.DB.QueryRow("SELECT test_id FROM attempts WHERE id = $1", attemptID).Scan(&testID)
	var total, answered int
	h.DB.QueryRow("SELECT COUNT(*), (SELECT COUNT(*) FROM answers WHERE attempt_id = $1) FROM questions WHERE test_id = $2", attemptID, testID).Scan(&total, &answered)
	if answered < total {
		http.Error(w, "Not all questions answered", http.StatusBadRequest)
		return
	}

	_, err = h.DB.Exec("UPDATE attempts SET finished = true WHERE id = $1", attemptID)
	if err != nil {
		http.Error(w, "Database error", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]string{"message": "Attempt completed"})
}
