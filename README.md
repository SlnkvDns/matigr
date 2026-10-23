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
