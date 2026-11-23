# Matigr

Matigr автоматизирует проверку уже принятых решений на математическом турнире. Жюри берёт работу из очереди и выносит вердикт, команды видят баллы и места в общей таблице и могут подать апелляцию. Один интерфейс на React, TypeScript и Vite работает с тремя сервисами на ASP.NET Core и .NET 10. У каждого сервиса своя PostgreSQL, а события передаются через RabbitMQ.

## Структура репозитория

| Где смотреть | Что находится внутри |
| --- | --- |
| [`src/backend/Matigr.slnx`](src/backend/Matigr.slnx) и [`src/backend/services/`](src/backend/services/) | Решение .NET и сервисы Checking, Results и Appeals. |
| [`src/frontend/apps/web/`](src/frontend/apps/web/) | Единое веб-приложение. [`app/AppRoutes.tsx`](src/frontend/apps/web/src/app/AppRoutes.tsx) связывает маршруты с [экранами в `features/`](src/frontend/apps/web/src/features/). В [`shared/`](src/frontend/apps/web/src/shared/) находятся HTTP-клиент, типы из OpenAPI, сессия и общие компоненты. |
| [`contracts/`](contracts/) | OpenAPI для HTTP и JSON Schema для сообщений RabbitMQ. В каталоге есть схемы отправителей, ожидания получателей и ссылки на примеры. |
| [`infra/compose/`](infra/compose/) и [`infra/fixtures/`](infra/fixtures/) | Docker Compose, параметры RabbitMQ и учебные данные для локального турнира. |
| [`tests/backend/`](tests/backend/), [`tests/integration/`](tests/integration/), [`tests/contracts/`](tests/contracts/), [`tests/e2e/`](tests/e2e/) | Тесты предметной логики и API, проверки PostgreSQL и брокера, тесты контрактов и сценарии браузерной приёмки. Frontend-тесты `*.test.ts(x)` лежат рядом с кодом. |
| [`scripts/`](scripts/) и [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | Подготовка локальной среды, команды проверок и их запуск в GitHub Actions. |
| [`docs/specs/`](docs/specs/) и [`docs/architecture/`](docs/architecture/) | Правила работы каждого сервиса, планы реализации, архитектурные решения и контракты взаимодействия. |

## Как читать backend

Сервисы устроены одинаково. В `*.Api` находятся `Program.cs`, маршруты ASP.NET Core и серверная авторизация. `*.Application` содержит команды и интерфейсы сценариев, `*.Domain` — предметные модели и правила переходов, `*.Infrastructure` — реализации сценариев, EF Core, миграции и интеграции с другими сервисами.

| Сервис | За что отвечает | С чего начать чтение кода |
| --- | --- | --- |
| **Checking** | Очередь принятых работ, выдача работы жюри, доступ к материалу, вердикт и передача старшему. Ведёт также проверки по апелляциям. | [`CheckingHttp.cs`](src/backend/services/Checking/Checking.Api/CheckingHttp.cs) → [`Check.cs`](src/backend/services/Checking/Checking.Domain/Check.cs) → [`CheckingWork.cs`](src/backend/services/Checking/Checking.Infrastructure/Storage/CheckingWork.cs) и [`CheckingTransitions.cs`](src/backend/services/Checking/Checking.Infrastructure/Storage/CheckingTransitions.cs). |
| **Results** | Применяет вердикты, рассчитывает баллы и места, публикует общую таблицу, выдаёт команде личные результаты. | [Обработка вердикта: `ApplyEvaluation.cs`](src/backend/services/Results/Results.Infrastructure/ApplyEvaluation.cs) → [чтение таблицы: `ResultReads.cs`](src/backend/services/Results/Results.Infrastructure/ResultReads.cs) → [HTTP: `ResultsHttp.cs`](src/backend/services/Results/Results.Api/ResultsHttp.cs). |
| **Appeals** | Принимает апелляцию на опубликованный результат, позволяет жюри выбрать её на перепроверку и закрывает после подтверждения от Results. | [Подача: `SubmissionEndpoints.cs`](src/backend/services/Appeals/Appeals.Api/SubmissionEndpoints.cs) → [модель `Appeal`](src/backend/services/Appeals/Appeals.Domain/Appeal.cs) → [выбор: `AppealWork.cs`](src/backend/services/Appeals/Appeals.Infrastructure/AppealWork.cs) → [завершение: `FinalProcess.cs`](src/backend/services/Appeals/Appeals.Infrastructure/FinalProcess.cs). |

## Как проходит работа

1. **Проверка.** На экране `/checking` жюри берёт работу из очереди. Карточка автоматически загружает решение и показывает «Правильно» и «Неправильно», когда его можно прочитать. Checking сохраняет назначение при перезагрузке страницы. При передаче та же работа переходит в очередь старшего жюри.
2. **Публикация.** Checking сохраняет вердикт и через RabbitMQ отправляет `CheckCompleted`. Results начисляет стоимость задачи за правильное решение или ноль за неправильное и публикует таблицу с местами. Страница `/results` доступна гостю. Results отправляет готовые снимки по WebSocket. HTTP GET поддерживает условное чтение по `ETag`.
3. **Апелляция.** До окончания турнира команда может один раз обжаловать опубликованный результат по задаче, включая первичный вердикт старшего жюри. При подаче Appeals запрашивает у Results опубликованную версию результата по HTTP. В очереди `/appeals` жюри видит названия команд и задач и нажимает «Взять на перепроверку». После подтверждения назначения появляется действие «Открыть перепроверку». Checking ведёт перепроверку, Results применяет вердикт, а Appeals закрывает обращение после `ResultEvaluationApplied`, в том числе когда балл остался прежним. Квота по задаче сохраняется после закрытия и изменения балла.

Сервисы взаимодействуют через контракты HTTP и RabbitMQ. Outbox сохраняет исходящее событие вместе с изменением в базе, inbox позволяет обработчикам распознавать повторы доставки. Версия результата защищает публикацию от устаревшей перепроверки. [Контракты взаимодействия](docs/architecture/implementation-contracts.md) · [Транспорт и восстановление доставки](src/backend/transport/README.md).

Приложение работает с подготовленными материалами турнира. Загрузка решений, QR-идентификация, управление списком турниров и production-регистрация за пределами этой реализации. Для локальной демонстрации предусмотрены тестовые роли. Права на действия проверяют API. [Контракт аутентификации](contracts/authentication.md).

## Локальный запуск

Понадобятся Docker с Compose, Node.js 22.14.0 и npm 10.9.2. Для самостоятельной сборки и тестов backend нужен .NET SDK 10.0.402. [Версии инструментов и пути к lock-файлам](docs/architecture/runtime-baseline.md).

Для первого запуска из корня репозитория выполните следующее. Если Matigr уже запускался, сначала проверьте имена проектов через `docker compose ls --all`. У разных Compose-проектов отдельные данные, но одинаковые порты на машине.

```sh
node scripts/init-local.mjs
node scripts/prepare-fixtures.mjs
docker compose -f infra/compose/compose.yaml up -d --build --wait --wait-timeout 180
npm ci --ignore-scripts --no-audit --no-fund
npm run dev:web
```

Откройте локальное приложение. При запуске через Vite таблица учебного турнира открывается автоматически. Если ввести другой ID и нажать «Показать таблицу», выбранный турнир сохранится при смене роли и переходах через меню. [Открыть Matigr](http://127.0.0.1:5173/).

На свежем стеке можно пройти короткий сценарий.

1. Выберите «Жюри 1», откройте «Проверку» и нажмите «Взять следующую». Под условием автоматически появится учебный пример решения, а под ним — кнопки вердикта.
2. Сохраните вердикт и переключитесь на команду, указанную в карточке. Откройте «Таблицу» через меню. В личных результатах дождитесь публикации, выберите «Перейти к апелляции» и подайте её.
3. Снова выберите жюри, откройте «Апелляции» и нажмите «Взять на перепроверку». Дождитесь действия «Открыть перепроверку». После нового вердикта статус обращения и баллы обновятся автоматически.

Vite перенаправляет запросы к Checking, Results и Appeals на локальные порты 5101, 5102 и 5103 соответственно. Секреты первого запуска создаются в игнорируемом каталоге `.local/secrets/`. Материалы из фикстур монтируются для Checking только для чтения. [Настройки веб-приложения](src/frontend/apps/web/README.md) · [Состав учебных данных](infra/fixtures/README.md).

При обновлении существующего стека сначала снова запустите `node scripts/init-local.mjs`, чтобы создать пароли ролей API, затем выполните ту же команду Compose с прежним именем проекта. Миграции и загрузку подготовленных данных выполняют одноразовые контейнеры `*-migrate`. API получает отдельную роль без прав изменения схемы. Демонстрационный Compose-профиль слушает loopback и включает тестовый вход явной настройкой.

Чтобы остановить стандартный Compose-проект с сохранением данных, выполните `docker compose -f infra/compose/compose.yaml down`. Если при запуске указывали имя проекта, передайте то же имя при остановке. Сохранённый проект `matigr-e2e-final` содержит уже завершённый турнир из браузерной приёмки.

## Проверки

В Windows общий набор запускается командой `./scripts/check.ps1 -Restore` из корня. Перед восстановлением зависимостей остановите Vite, если он запущен на Windows. Для Linux и macOS порядок сборки и тестов приведён в [GitHub Actions](.github/workflows/ci.yml).

| Что проверить | Команда или сценарий |
| --- | --- |
| Документацию и границы слоёв | `node scripts/check-docs.mjs`, `node scripts/check-architecture.mjs` |
| Контракты и типы клиента | `npm run test:contracts`, `npm run check:api` |
| Веб-приложение | `npm run typecheck:web`, `npm run test:web`, `npm run build:web` |
| .NET и HTTP-обработчики | `dotnet test src/backend/Matigr.slnx` после `dotnet restore src/backend/Matigr.slnx --locked-mode` |
| Запущенную инфраструктуру | `node scripts/check-infrastructure.mjs`. Для отдельных проверок хранилищ есть `check-checking-storage.mjs`, `check-results-storage.mjs` и `check-appeals-storage.mjs` в `scripts/`. Проверки, меняющие данные или останавливающие сервисы, запускайте на явно выбранном изолированном тестовом Compose-проекте. [Команды и порядок в CI](.github/workflows/ci.yml). |
| Сквозные сценарии в браузере | [V1 — проверка и публикация](tests/e2e/v1.md), [V2 — передача старшему](tests/e2e/v2.md), [V3 — апелляция без изменения балла](tests/e2e/v3.md), [V4 — коррекция баллов и мест](tests/e2e/v4.md), [V5 — старший на апелляции](tests/e2e/v5.md), [V6 — окончание турнира и восстановление](tests/e2e/v6.md). |

Первоначальная реализация завершена, приняты 82 задачи и браузерные сценарии V1–V6. Даты прогонов и результаты по каждому сервису собраны в [журнале реализации](docs/implementation/progress.md).
