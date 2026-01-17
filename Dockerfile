FROM golang:1.25-alpine

# Устанавливаем зависимости
RUN apk add --no-cache git

# Создаем рабочую директорию
WORKDIR /app

# Копируем go.mod и go.sum
COPY go.mod go.sum ./

# Загружаем зависимости
RUN go mod download

# Копируем остальные файлы
COPY . .

# Собираем приложение
RUN CGO_ENABLED=0 GOOS=linux go build -o main .

# Финальный образ
FROM alpine:3.19

# Устанавливаем необходимые зависимости
RUN apk --no-cache add ca-certificates

# Копируем бинарный файл из образа сборки
COPY --from=build /app/main /app/main

# Устанавливаем рабочую директорию
WORKDIR /app

# Запускаем приложение
CMD ["./main"]