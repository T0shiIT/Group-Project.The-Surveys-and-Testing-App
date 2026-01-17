package models

import "time"

// User представляет пользователя в системе
type User struct {
	ID        int       `json:"id"`
	Email     string    `json:"email"`
	FullName  string    `json:"full_name"`
	Roles     []string  `json:"roles"`
	CreatedAt time.Time `json:"created_at"`
}

// Course представляет дисциплину
type Course struct {
	ID          int       `json:"id"`
	Name        string    `json:"name"`
	Description string    `json:"description"`
	TeacherID   int       `json:"teacher_id"`
	CreatedAt   time.Time `json:"created_at"`
}

// Test представляет тест
type Test struct {
	ID        int       `json:"id"`
	CourseID  int       `json:"course_id"`
	Name      string    `json:"name"`
	Active    bool      `json:"active"`
	CreatedAt time.Time `json:"created_at"`
	Questions []int     `json:"questions,omitempty"`
}

// Question представляет вопрос
type Question struct {
	ID            int       `json:"id"`
	TestID        int       `json:"test_id"`
	Text          string    `json:"text"`
	Options       []string  `json:"options"`
	CorrectAnswer int       `json:"correct_answer"`
	CreatedAt     time.Time `json:"created_at"`
}

// Attempt представляет попытку прохождения теста
type Attempt struct {
	ID        int       `json:"id"`
	UserID    int       `json:"user_id"`
	TestID    int       `json:"test_id"`
	Finished  bool      `json:"finished"`
	CreatedAt time.Time `json:"created_at"`
	Answers   []Answer  `json:"answers,omitempty"`
}

// Answer представляет ответ на вопрос
type Answer struct {
	ID         int       `json:"id"`
	AttemptID  int       `json:"attempt_id"`
	QuestionID int       `json:"question_id"`
	Answer     int       `json:"answer"`
	CreatedAt  time.Time `json:"created_at"`
}
