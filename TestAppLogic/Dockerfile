# Stage 1: Сборка
FROM golang:1.21-alpine AS builder

WORKDIR /app

# Копируем go.mod и go.sum
COPY go.mod go.sum ./

# Загружаем зависимости
RUN go mod download

# Копируем всё содержимое проекта (включая main.go, db/, handlers/, models/)
COPY . .

# Собираем бинарник
RUN CGO_ENABLED=0 GOOS=linux go build -o main .

# Stage 2: Минимальный образ для запуска
FROM alpine:3.19

WORKDIR /root/

# Устанавливаем сертификаты (если нужны HTTPS-запросы)
RUN apk --no-cache add ca-certificates

# Копируем бинарник из stage 'builder'
COPY --from=builder /app/main .

# Открываем порт (если приложение слушает)
EXPOSE 8080

# Запускаем
CMD ["./main"]