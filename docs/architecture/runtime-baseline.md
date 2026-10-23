# Базовая среда реализации — CHK-INF-01

Зафиксирована 23 октября 2026 года после поручения реализации. Спецификации и планы сохраняют одобрение от этой даты. Исходная документация сохранена коммитом `5ef5e1b`, реализация ведётся в `codex/implementation-v0`.

## Матрица версий

| Компонент | Точная версия | Проверка совместимости / источник |
| --- | --- | --- |
| .NET SDK | 10.0.402 | [Официальная загрузка .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), `global.json`, rollForward=disable |
| ASP.NET Core / EF Core / EF Relational / dotnet-ef | 10.0.13 | [EF 10 требует .NET 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/), центральные PackageVersion; provider-зависимость Relational закреплена явно на 10.0.13 |
| JwtBearer / JsonWebTokens | 10.0.13 / 8.19.2 | CHK-BE-04: nuspec JwtBearer требует IdentityModel 8.19.2; прямые версии и lock-файлы, RS256/public-key verification по [контракту](../../contracts/authentication.md) |
| Npgsql EF provider / Npgsql | 10.0.3 | [Provider 10](https://www.npgsql.org/efcore/release-notes/10.0.html); nuspec 10.0.3 требует EF [10.0.4,11.0.0), Npgsql 10.0.3 |
| PostgreSQL | 18.6, образ `postgres:18.6-bookworm` | [Release notes](https://www.postgresql.org/docs/release/18.6/); точные баллы — numeric, версии — bigint |
| RabbitMQ | 4.3.6, образ `rabbitmq:4.3.6-management` | [Поддерживаемая серия 4.3](https://www.rabbitmq.com/release-information) |
| RabbitMQ.Client | 7.2.2 | [.NET client 7](https://www.rabbitmq.com/client-libraries/dotnet-api-guide); AMQP 0-9-1, async API |
| Node / npm | 22.14.0 / 10.9.2 | [Официальный выпуск](https://nodejs.org/en/blog/release/v22.14.0); [Vite требует Node >=22.12](https://vite.dev/guide/) |
| React / React DOM | 19.3.1 | Точные версии npm registry, lockfile при создании frontend |
| TypeScript / Vite / plugin-react | 5.9.3 / 8.3.5 / 6.1.2 | Проверены engines; Node 22.14.0 удовлетворяет Vite/plugin |
| React Router / OpenAPI TypeScript | 7.9.5 / 7.13.0 | CHK-FE-01: declarative маршруты единственного app, генерация DTO из OpenAPI и проверка актуальности; Router требует Node >=20 / React >=18, генератор совместим с TypeScript 5.x |
| Testing Library user-event / types React / types React DOM / types Node | 14.6.1 / 19.2.2 / 19.2.2 / 22.14.0 | CHK-FE-01: точные дополнительные dev-версии в workspace package.json и общем lockfile |
| Vitest / jsdom | 5.0.5 / 26.1.0 | jsdom 26 допускает Node >=18; jsdom 30 требует более новый Node и здесь не используется |
| Testing Library React / jest-dom | 16.3.3 / 7.0.1 | Node >=18 / >=22 |
| Playwright | 1.64.1 | Будущий E2E; Node >=20, браузеры устанавливаются вместе с этой версией |
| xUnit / VS adapter / Test SDK | 2.9.3 / 3.1.5 / 18.11.0 | Точный совместимый VSTest-профиль, без тестов-заглушек |
| OpenAPI / JSON Schema validators | Swagger Parser 12.1.0 / Ajv 8.17.1 / ajv-formats 3.0.1 | CHK-INT-01: OpenAPI 3.1.1 / Draft 2020-12, strict schema compilation, проверка format без приведения типов. Точные версии и root package-lock.json |

Версии NuGet проверены по официальному flat-container API, npm — по registry.npmjs.org. Прямые пакеты закрепляются без диапазонов, транзитивные — в packages.lock.json/package-lock.json. Образы закреплены patch-тегами и digest в Compose/Dockerfile; их доступность и запуск проверены в CHK-INF-03. CI actions закреплены commit SHA официальных репозиториев. Обновление версий — отдельное изменение с повторной сборкой и проверкой совместимости.

## T03: транспорт

Один durable topic exchange `matigr.v1`, durable очереди из implementation-contracts.md, persistent сообщения, publisher confirms + mandatory routing, manual ack только после локального commit. Checking/Results/Appeals имеют отдельные transport credentials. Маршрут включает kind для CheckCompleted и receipts: Results получает Initial и Appeal, Appeals — только Appeal и свои Checking facts. Строка producer в JSON не заменяет transport authorization. Автоматическая очистка receipts не вводится. Численные параметры повторов/наблюдения являются настраиваемой локальной техникой, не регламентом или SLO Q10/Q13.

## T07, Q05 и Q09

- Внешние ID — непрозрачные строки; внутренние новые ID — UUID, в PostgreSQL uuid. Фикстуры имеют стабильные UUID, ID не кодируют полномочия. Получатель не выводит бизнес-смысл из формата.
- Все версии агрегата/результата/снимка/материала и reviewRevision передаются десятичными строками. schemaVersion — малое целое `1`; оно не является версией агрегата. TypeScript сравнивает версии через BigInt, не Number.
- Пользователь явно согласовал тестовые задачи **10 и 20 целых баллов**, JSON строки, 23 октября 2026 года. Баллы/суммы — десятичные строки целых чисел, .NET decimal / PostgreSQL numeric(28,0), без float/double. Это выбранный профиль MVP, не универсальный будущий регламент. Отрицательные стоимости не допускаются; частичные баллы не добавляются.
- Pending — отдельный статус без score; пустая сумма — строка `"0"`. Отрицательный вердикт — опубликованный Incorrect со score `"0"`.
- Q05: один тестовый турнир, два обычных жюри и один старший с явными scope этого турнира, три команды и организатор. Число старших не ограничивается доменной моделью. Scope нескольких турниров/выбор между старшими остаются открытыми.

Полный MVP и V0 пока не приняты. Выполнение и доказательства проверок фиксируются в [журнале реализации](../implementation/progress.md).
