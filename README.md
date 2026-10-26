# Matigr

Реализация согласованных Checking, Results и Appeals на .NET 10 / PostgreSQL / RabbitMQ и одном React/Vite приложении. Загрузка/приём решений, QR и управление турниром вне MVP.

Актуализировано 26 октября 2026 года. Первоначальный MVP завершён: **82/82 задачи**, браузерные V1–V6 приняты. Спецификации/планы одобрены 23 октября, реализация поручена отдельно в тот же день. Production-регистрация и публичное размещение backend не реализованы. [Статус задач и доказательства проверок](docs/implementation/progress.md), [матрица версий](docs/architecture/runtime-baseline.md), [контракты и зависимости](docs/architecture/implementation-contracts.md).

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

В CI те же команды. Lock-файлы отслеживаются Git. Ветка реализации — `codex/implementation-v0`; изменения фиксируются отдельными коммитами по проверенным шагам. Исторические результаты: 441 общий тест, отдельно Checking 46 + checkpoint, Results 27 и Appeals 31 PostgreSQL/broker tests; новые изменения требуют подходящих проверок. Readiness не заменяет бизнес-приёмку. Полная история — в [архиве MVP](docs/implementation/progress-mvp-2026-10-24.md).

Проверка хранения Checking на отдельной PostgreSQL: `node scripts/check-checking-storage.mjs` после подготовки fixtures/ключа. Команда создаёт и удаляет изолированный тестовый Compose, проверяет миграции/constraints/CAS и восстановление capability отдельным процессом. [Границы хранения и запуск](src/backend/services/Checking/Checking.Infrastructure/Storage/README.md).

## Контракты

OpenAPI всех трёх сервисов и независимые producer/consumer JSON Schemas описаны в [каталоге контрактов](contracts/README.md), отдельно от Domain/ORM. JWT/dev-вход и сервисная identity — в [authentication.md](contracts/authentication.md). Реализованы очереди/назначение/материал/Complete/Refuse, публикация/таблица/служебное основание, Submit/ручной Claim/process manager/manual end. Runtime schema/routing, inbox/outbox/confirm/ack, no-op/changed/senior/recovery проверены integration и V1–V6. `npm run test:contracts` отдельно проверяет форму/fixtures и не заменяет бизнес-тесты.

`node scripts/check-checking-work.mjs --restart` проверяет реальный HTTP по OpenAPI и сохранность назначения после рестарта Checking. Перед запуском явно задайте `COMPOSE_PROJECT_NAME` отдельного свежего Compose-проекта с подготовленными fixtures: скрипт создаёт назначения и не сбрасывает данные. Пример проверенного проекта — `matigr-chk-be05-smoke`; поднять его можно через `docker compose -p matigr-chk-be05-smoke -f infra/compose/compose.yaml up -d --build`. Проверка требует свободных локальных портов Compose. CI запускает её после инфраструктурной и identity-проверок на своём свежем стеке.

## Frontend

```sh
npm ci --ignore-scripts --no-audit --no-fund
npm run dev:web
```

Единое приложение: [http://127.0.0.1:5173](http://127.0.0.1:5173). Реализованы рабочее место жюри/старшего, anonymous таблица с SSE/GET-восстановлением, личные результаты команды, подача/список/ручной выбор апелляций и статусы до Closed/NeedsResolution, включая ожидание старшего. Профиль приходит с сервера, token только в памяти; смена субъекта очищает private state. Тестовый переключатель есть только в dev-сборке, полноценный production-вход требует новой задачи. Проверки: `npm run check:api`, `npm run typecheck:web`, `npm run test:web`, `npm run build:web`. [Устройство frontend](src/frontend/apps/web/README.md).

## Локальная инфраструктура

Сначала проверьте `docker compose ls --all` и выберите нужный проект: разные проекты имеют разные volumes, но используют те же host ports. Команды ниже — подготовка стандартного локального проекта, не сброс существующего. Для сохранённого `matigr-e2e-final` используйте `docker compose -p matigr-e2e-final -f infra/compose/compose.yaml up -d --wait --wait-timeout 180`. Его турнир завершён и очереди разобраны после V6; не открывайте окно и не сбрасывайте квоты ради демонстрации. Backend и Vite запускаются отдельно.

```sh
node scripts/init-local.mjs
node scripts/prepare-fixtures.mjs
docker compose -f infra/compose/compose.yaml up -d --build --wait --wait-timeout 180
node scripts/check-infrastructure.mjs
node scripts/check-identity.mjs --restart
```

Три PostgreSQL не публикуют порты хоста и используют отдельные сети, volumes и credentials. Локальная проверка проверяет все 6 запретов чужих credentials, durable bindings RabbitMQ и готовность всех трёх API. Compose запускает все три API. Results слушает `http://127.0.0.1:5102`, Appeals — `http://127.0.0.1:5103`; Results применяет свои миграции и фиксированный roster/rules seed. Checking слушает `http://127.0.0.1:5101`, RabbitMQ management — `http://127.0.0.1:15672`; пароли случайны, находятся в игнорируемом `.local/secrets`. Приложение после чтения Compose secrets запускается как непривилегированный `app`.

Остановить контейнеры с сохранением данных: `docker compose -f infra/compose/compose.yaml down`. Не удаляйте volumes/секреты для обычного перезапуска.

## Фикстуры

```sh
node scripts/prepare-fixtures.mjs
node --test tests/fixtures/fixtures.test.mjs
```

[Общий манифест](infra/fixtures/manifest.json) содержит согласованные тестовые стоимости 10/20, стабильные субъекты и шесть уже принятых ссылок на материал. Подготовка сверяет SHA-256 и создаёт устойчивые локальные ключи; материал монтируется в Checking только для чтения. Повтор не меняет ключи. [Границы bootstrap и хранения](infra/fixtures/README.md). Эта команда подготовки не создаёт проверки в БД. В CHK-BE-03 реализован отдельный внутренний bootstrap: Local startup Checking после миграций идемпотентно создаёт ссылки/Initial Check и закрепляет версию/checksum набора.

Проверки Results: `node scripts/check-results-storage.mjs` (изолированные PostgreSQL/RabbitMQ/HTTP), `node scripts/check-results-http.mjs` (живой Compose/OpenAPI). [Хранение, применение и service auth](src/backend/services/Results/Results.Infrastructure/README.md). Appeals: `node scripts/check-appeals-storage.mjs`, [его storage/process manager](src/backend/services/Appeals/Appeals.Infrastructure/README.md). Первичный и полный апелляционный маршруты, включая no-op/changed/senior, реализованы и приняты.

На свежем изолированном стеке после `check-checking-work.mjs --restart` запускайте `node scripts/check-checking-transitions.mjs` с тем же `COMPOSE_PROJECT_NAME`. Скрипт создаёт вердикты/передачу, проверяет прежний replay, освобождение и outbox по producer schema. Повтор на использованном стеке требует новых тестовых данных; рабочие назначения не сбрасываются.

Текущая таблица: [локальный турнир](http://127.0.0.1:5173/results?tournamentId=0344e06b-4ea1-582e-bd91-e832719773f2), либо задайте публичный `VITE_TOURNAMENT_ID` для своего развёртывания. Это ссылка на подготовленный турнир, без управления турнирами. Standings и SSE доступны гостю, UI показывает серверные баллы/сумму/места и pending отдельно от опубликованного нуля. Reconnect/open/пропущенный сигнал восстанавливаются GET; приватные данные не передаются SSE.

[Технический transport sample и redrive](src/backend/transport/README.md) компилируется независимо в Infrastructure трёх сервисов; общих доменных/ORM-моделей нет. Retry/poll/lease defaults настраиваются и не являются бизнес-сроками/SLO. На отдельном тестовом стеке после identity/work/transitions запустите `node scripts/check-transport.mjs`, затем `node scripts/check-live-results.mjs` с явным COMPOSE_PROJECT_NAME. Они меняют только тестовые данные/состояние брокера, проверяют recovery/confirm/mandatory return/quarantine, независимые копии CheckCompleted и живой SSE после настоящего вердикта. Не запускать fault-проверки на рабочем стеке.

## GitHub и Sites

Проект отправлен по поручению пользователя в приватный [SlnkvDns/matigr](https://github.com/SlnkvDns/matigr). Актуальный local/remote status проверяйте через Git; новые коммиты не отправляются автоматически.

В Sites отдельно [опубликован только интерфейс](docs/implementation/sites-hosting.md): без backend/данных/production-входа. Это не замена локальному полному приложению. Для интернет-развёртывания нужны отдельная серверная конфигурация, HTTPS API gateway и production-аутентификация. Текущие `.local/secrets` и volumes не включать в Git/статическую публикацию.
