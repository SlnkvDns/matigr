# Matigr

Реализация согласованных Checking, Results и Appeals на .NET 10 / PostgreSQL / RabbitMQ и одном React/Vite приложении. Загрузка/приём решений, QR и управление турниром вне MVP.

Планы одобрены 23 октября 2026 года; реализация поручена отдельно в тот же день. [Статус задач и доказательства проверок](docs/implementation/progress.md), [матрица версий](docs/architecture/runtime-baseline.md), [контракты и зависимости](docs/architecture/implementation-contracts.md).

## Проверка проекта

Нужны SDK из global.json, Node из .node-version, npm 10.9.2. Локальный SDK можно разместить в игнорируемом `.tools/dotnet`; системный SDK менять не требуется.

```powershell
./scripts/check.ps1 -Restore
```

Linux/macOS:

```sh
node scripts/check-architecture.mjs
npm ci --ignore-scripts --no-audit --no-fund
npm run test:contracts
npm run check:api
npm run typecheck:web
npm run test:web
npm run build:web
dotnet restore src/backend/Matigr.slnx --locked-mode
dotnet build src/backend/Matigr.slnx --no-restore -c Release
dotnet test src/backend/Matigr.slnx --no-build --no-restore -c Release
```

В CI те же команды. Lock-файлы отслеживаются Git. Ветка реализации — `codex/implementation-v0`; изменения фиксируются отдельными коммитами по проверенным шагам. Текущие health endpoints не означают готовность бизнес-сценариев V1–V6.

Проверка хранения Checking на отдельной PostgreSQL: `node scripts/check-checking-storage.mjs` после подготовки fixtures/ключа. Команда создаёт и удаляет изолированный тестовый Compose, проверяет миграции/constraints/CAS и восстановление capability отдельным процессом. [Границы хранения и запуск](src/backend/services/Checking/Checking.Infrastructure/Storage/README.md).

## Контракты

[OpenAPI Checking](contracts/http/checking/openapi.json) и [JSON Schema сообщений](contracts/README.md) описывают согласованный интерфейс отдельно от доменных моделей. Проверка: `npm run test:contracts` после `npm ci`. Она включена в общую команду PowerShell и CI. В CHK-BE-04 реализованы [dev-выбор/profile, JWT и серверные права](contracts/authentication.md); CHK-BE-05 добавляет очереди, TakeNext, текущее назначение, Check и материал актуального владельца. CHK-BE-06 добавляет Complete/Refuse с capability/If-Match и атомарным освобождением/history/outbox. Проверки реальных отправителей/потребителей и брокера — отдельные задачи CHK-INT-02–04.

`node scripts/check-checking-work.mjs --restart` проверяет реальный HTTP по OpenAPI и сохранность назначения после рестарта Checking. Перед запуском явно задайте `COMPOSE_PROJECT_NAME` отдельного свежего Compose-проекта с подготовленными fixtures: скрипт создаёт назначения и не сбрасывает данные. Пример проверенного проекта — `matigr-chk-be05-smoke`; поднять его можно через `docker compose -p matigr-chk-be05-smoke -f infra/compose/compose.yaml up -d --build`. Проверка требует свободных локальных портов Compose. CI запускает её после инфраструктурной и identity-проверок на своём свежем стеке.

## Frontend

```sh
npm ci --ignore-scripts --no-audit --no-fund
npm run dev:web
```

Единое приложение: [http://127.0.0.1:5173](http://127.0.0.1:5173). В CHK-FE-01/02 созданы навигация, HTTP/ProblemDetails client, типы из OpenAPI и выбор тестового пользователя через dev-issuer. Профиль приходит с сервера; токен хранится в памяти, смена пользователя очищает частное состояние. Переключатель виден только в dev-сборке. CHK-FE-03 подключает рабочее место жюри: очередь, назначение, материал, бинарный вердикт, отказ и senior queue. Экраны результатов/апелляций остаются следующими задачами. Проверки: `npm run check:api`, `npm run typecheck:web`, `npm run test:web`, `npm run build:web`; они также входят в `scripts/check.ps1 -Restore` и CI. [Устройство frontend и границы текущего шага](src/frontend/apps/web/README.md).

## Локальная инфраструктура

```sh
node scripts/init-local.mjs
node scripts/prepare-fixtures.mjs
docker compose -f infra/compose/compose.yaml up -d --build --wait --wait-timeout 180
node scripts/check-infrastructure.mjs
node scripts/check-identity.mjs --restart
```

Три PostgreSQL не публикуют порты хоста и используют отдельные сети, volumes и credentials. Локальная проверка проверяет все 6 запретов чужих credentials, durable bindings RabbitMQ и готовность Checking. Compose запускает все три API. Results слушает `http://127.0.0.1:5102`, Appeals — `http://127.0.0.1:5103`; Results применяет свои миграции и фиксированный roster/rules seed. Checking слушает `http://127.0.0.1:5101`, RabbitMQ management — `http://127.0.0.1:15672`; пароли случайны, находятся в игнорируемом `.local/secrets`. Приложение после чтения Compose secrets запускается как непривилегированный `app`.

Остановить контейнеры с сохранением данных: `docker compose -f infra/compose/compose.yaml down`. Не удаляйте volumes/секреты для обычного перезапуска.

## Фикстуры

```sh
node scripts/prepare-fixtures.mjs
node --test tests/fixtures/fixtures.test.mjs
```

[Общий манифест](infra/fixtures/manifest.json) содержит согласованные тестовые стоимости 10/20, стабильные субъекты и шесть уже принятых ссылок на материал. Подготовка сверяет SHA-256 и создаёт устойчивые локальные ключи; материал монтируется в Checking только для чтения. Повтор не меняет ключи. [Границы bootstrap и хранения](infra/fixtures/README.md). Эта команда подготовки не создаёт проверки в БД. В CHK-BE-03 реализован отдельный внутренний bootstrap: Local startup Checking после миграций идемпотентно создаёт ссылки/Initial Check и закрепляет версию/checksum набора.

Проверка Results: `node scripts/check-results-storage.mjs` — изолированная PostgreSQL/HTTP и SQL-ранжирование; `node scripts/check-results-http.mjs` — живой Compose/OpenAPI с реальным issuer. [Хранение, применение, чтение и service auth](src/backend/services/Results/Results.Infrastructure/README.md). CHK-INT-02 и RES-INT-02 подключают durable доставку первичного вердикта Checking → Results; RES-BE-05 и RES-FE-01/02 добавляют анонимную таблицу/SSE и восстановление версий. Апелляционные обработчики остаются следующими задачами.

На свежем изолированном стеке после `check-checking-work.mjs --restart` запускайте `node scripts/check-checking-transitions.mjs` с тем же `COMPOSE_PROJECT_NAME`. Скрипт создаёт вердикты/передачу, проверяет прежний replay, освобождение и outbox по producer schema. Повтор на использованном стеке требует новых тестовых данных; рабочие назначения не сбрасываются.

Текущая таблица: [локальный турнир](http://127.0.0.1:5173/results?tournamentId=0344e06b-4ea1-582e-bd91-e832719773f2), либо задайте публичный `VITE_TOURNAMENT_ID` для своего развёртывания. Это ссылка на подготовленный турнир, без управления турнирами. Standings и SSE доступны гостю, UI показывает серверные баллы/сумму/места и pending отдельно от опубликованного нуля. Reconnect/open/пропущенный сигнал восстанавливаются GET; приватные данные не передаются SSE.

[Технический transport sample и redrive](src/backend/transport/README.md) компилируется независимо в Infrastructure двух сервисов; общих доменных/ORM-моделей нет. Retry/poll/lease defaults настраиваются и не являются бизнес-сроками/SLO. На отдельном тестовом стеке после identity/work/transitions запустите `node scripts/check-transport.mjs`, затем `node scripts/check-live-results.mjs` с явным COMPOSE_PROJECT_NAME. Они меняют только тестовые данные/состояние брокера, проверяют recovery/confirm/mandatory return/quarantine, независимые копии CheckCompleted и живой SSE после настоящего вердикта. Не запускать fault-проверки на рабочем стеке.
