# Сборка приложения
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

# Запуск приложения
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Установка CURL:
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
EXPOSE 5000
ENTRYPOINT ["dotnet", "TelegramBot.dll"]