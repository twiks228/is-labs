# Этап 1: сборка
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем csproj и восстанавливаем зависимости
COPY ["IsLabApp.csproj", "."]
RUN dotnet restore "./IsLabApp.csproj"

# Копируем весь исходный код
COPY . .
WORKDIR "/src/."

# Публикуем в Release
RUN dotnet publish "IsLabApp.csproj" -c Release -o /app/publish

# Этап 2: runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Копируем опубликованное приложение
COPY --from=build /app/publish .

# Открываем порт
EXPOSE 8080

# Запускаем приложение
ENTRYPOINT ["dotnet", "IsLabApp.dll"]
