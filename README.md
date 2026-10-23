# Matigr

Реализация согласованных Checking, Results и Appeals на .NET 10 / PostgreSQL / RabbitMQ и одном React/Vite приложении. Загрузка/приём решений, QR и управление турниром вне MVP.

Планы одобрены 23 октября 2026 года; реализация поручена отдельно в тот же день. [Статус задач и доказательства проверок](docs/implementation/progress.md), [матрица версий](docs/architecture/runtime-baseline.md), [контракты и зависимости](docs/architecture/implementation-contracts.md).

## Проверка backend

Нужны SDK из global.json, Node из .node-version, npm 10.9.2. Локальный SDK можно разместить в игнорируемом `.tools/dotnet`; системный SDK менять не требуется.

```powershell
./scripts/check.ps1 -Restore
```

Linux/macOS:

```sh
node scripts/check-architecture.mjs
dotnet restore src/backend/Matigr.slnx --locked-mode
dotnet build src/backend/Matigr.slnx --no-restore -c Release
dotnet test src/backend/Matigr.slnx --no-build --no-restore -c Release
```

В CI те же команды. Lock-файлы отслеживаются Git. Ветка реализации — `codex/implementation-v0`; изменения фиксируются отдельными коммитами по проверенным шагам. Текущие health endpoints не означают готовность бизнес-сценариев V1–V6.

## Локальная инфраструктура

```sh
node scripts/init-local.mjs
docker compose -f infra/compose/compose.yaml up -d --build --wait --wait-timeout 180
node scripts/check-infrastructure.mjs
```

Три PostgreSQL не публикуют порты хоста и используют отдельные сети, volumes и credentials. Локальная проверка проверяет все 6 запретов чужих credentials, durable bindings RabbitMQ и готовность Checking. Отдельных API Results/Appeals пока нет. Checking слушает `http://127.0.0.1:5101`, RabbitMQ management — `http://127.0.0.1:15672`; пароли случайны, находятся в игнорируемом `.local/secrets`. Приложение после чтения Compose secrets запускается как непривилегированный `app`.

Остановить контейнеры с сохранением данных: `docker compose -f infra/compose/compose.yaml down`. Не удаляйте volumes/секреты для обычного перезапуска.

## Фикстуры

```sh
node scripts/prepare-fixtures.mjs
node --test tests/fixtures/fixtures.test.mjs
```

[Общий манифест](infra/fixtures/manifest.json) содержит согласованные тестовые стоимости 10/20, стабильные субъекты и шесть уже принятых ссылок на материал. Подготовка сверяет SHA-256 и создаёт устойчивые локальные ключи; материал монтируется в Checking только для чтения. Повтор не меняет ключи. [Границы bootstrap и хранения](infra/fixtures/README.md). Эта команда не создаёт проверки в БД: внутренний bootstrap относится к CHK-BE-03.
