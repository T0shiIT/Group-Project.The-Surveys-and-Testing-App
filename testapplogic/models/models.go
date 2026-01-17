package models

// User представляет пользователя в системе
type User struct {
	ID       int
	Email    string
	FullName string
	Roles    []string
}

// Course представляет дисциплину
type Course struct {
	ID          int
	Name        string
	Description string
	TeacherID   int
}

// Test представляет тест
type Test struct {
	ID        int
	CourseID  int
	Name      string
	Active    bool
	Questions []int
}

// Question представляет вопрос
type Question struct {
	ID      int
	TestID  int
	Text    string
	Options []string
	Answer  int
}

// Attempt представляет попытку прохождения теста
type Attempt struct {
	ID       int
	UserID   int
	TestID   int
	Answers  []int
	Finished bool
}

// Answer представляет ответ на вопрос
type Answer struct {
	ID         int
	AttemptID  int
	QuestionID int
	Answer     int
}
