#---[ЭТАП СБОРКИ]---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# Установка необходимых пакетов для работы с SSL и сертификатами
RUN apt-get update && \
    apt-get install -y curl ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Очистка кэша NuGet и настройка переменных окружения
RUN dotnet nuget locals all --clear
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENV NUGET_XMLDOC_MODE=skip
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# Создание директории для NuGet конфигурации
RUN mkdir -p /root/.nuget/NuGet

# Настройка NuGet без проверки подписей (исправлено)
RUN echo '<configuration><config><add key="signatureValidationMode" value="disable" /></config><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" /></packageSources></configuration>' > /root/.nuget/NuGet/NuGet.Config

# Установка рабочей директории
WORKDIR /src

# Копирование файлов проекта
COPY *.csproj .

# Восстановление пакетов с увеличенным таймаутом
RUN dotnet restore --verbosity minimal --disable-parallel --ignore-failed-sources

# Копирование всего кода
COPY . .

# Публикация приложения
RUN dotnet publish -c Release -o /app

#---[ЭТАП ЗАПУСКА]---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Установка зависимостей для работы с curl и сертификатами
RUN apt-get update && \
    apt-get install -y curl ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Установка рабочей директории
WORKDIR /app

# Копирование опубликованного приложения
COPY --from=build /app .

# Экспорт порта
EXPOSE 5000

# Запуск приложения
CMD ["dotnet", "TelegramBot.dll"]