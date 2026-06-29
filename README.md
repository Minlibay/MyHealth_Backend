# MyHealth.Api

Бэкенд для приложения MyHealth (ASP.NET Core 10 + EF Core + PostgreSQL).

## Возможности (MVP)
- Регистрация / вход (JWT, пароли — BCrypt).
- Хранение медицинских показателей пользователя.
- Пакетная загрузка измерений (идемпотентно по `clientId`).
- Выборка истории по показателю и периоду, последние значения.

## Запуск (dev)

```bash
# 1. Поднять dev-БД (PostgreSQL в Docker, порт 5434)
docker compose up -d

# 2. Запустить API (миграции применятся автоматически в Development)
dotnet run --project src/MyHealth.Api
```

Swagger UI: `https://localhost:<port>/swagger`
Примеры запросов: `src/MyHealth.Api/MyHealth.Api.http`

## Миграции

```bash
dotnet ef migrations add <Name> --project src/MyHealth.Api
dotnet ef database update --project src/MyHealth.Api
```

## Конфигурация
- `appsettings.Development.json` — строка подключения и dev-ключ JWT.
- В проде задавать `ConnectionStrings:Postgres` и `Jwt:Key` через переменные
  окружения / секрет-хранилище (НЕ коммитить реальные секреты).

## Безопасность и GDPR (важно перед продом)
Медицинские данные — особая категория (GDPR ст. 9). Реализовано: TLS,
хеш паролей, JWT. **Ещё нужно** до прод-запуска: шифрование БД в покое,
аудит-логи доступа, явное согласие на передачу данных, выбор страны
хостинга (ЕС/РФ), DPA с провайдером, экспорт/удаление данных пользователя.
