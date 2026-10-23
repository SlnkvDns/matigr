# Хранение Checking — CHK-BE-02

Собственная PostgreSQL и EF Core: `CheckingDbContext`, локальные persistence records и миграция `20261023171035_InitialCheckingStorage`. Domain не зависит от EF. Records не являются контрактами API/брокера. Перевод состояния в команды/DTO, bootstrap fixtures и runtime workers выполняются следующими задачами.

Схема содержит `solution_references`, `checks`, `assignments`, `reviewer_scopes`, `check_history`, `recheck_request_receipts`, `http_idempotency`, `inbox`, `outbox`, `processing_failures`. Частичные UNIQUE гарантируют один Active на reviewer во всём Checking и один Active на Check; Initial уникален на solution, Appeal — на appealId, reviewRevision — внутри solution. Внешние team/task/result/appeal IDs не имеют межсервисных FK.

`checks.version` — EF concurrency token. При изменении отслеживаемой строки `SaveChanges` увеличивает исходную версию на один; stale update вызывает `DbUpdateConcurrencyException`. Принятый snapshot, основание Check и завершённый Check не изменяются через этот путь записи. Будущий явный SQL обязан сохранять те же условия/CAS. Транзакционная логика реальных TakeNext/Complete/Refuse ещё не подключена. Частичные UNIQUE и CHECK не заменяют проверку полномочий и допустимости перехода в Domain/Application.

Локальные FK связывают solution/tournament, check/tournament и scope назначения. Составной `fk_checks_current_assignment` не позволяет сослаться на Assignment другого Check. Он объявлен непосредственно в SQL миграции как `DEFERRABLE INITIALLY DEFERRED`, чтобы Check и его первое назначение можно было вставить в одной транзакции без циклического порядка вставок EF; этот FK проверяется PostgreSQL tests, а не только model snapshot. Completed сохраняет ссылку на своё завершённое назначение; SeniorQueued не имеет текущего назначения. У назначений нет TTL/heartbeat.

`StorageProtection:KeyPath` указывает на постоянный внешний ключ: 32 байта, записанные 64 hex-символами. В Compose используется уже подготовленный `.local/secrets/capability-protection-key`, доступный только Checking и копируемый с mode 600 для UID 1654. Отсутствующий/невалидный ключ останавливает запуск; автоматической ротации нет. `StorageProtection` создаёт случайную 256-битную capability, хранит SHA-256 и AES-256-GCM ciphertext с отдельными nonce/tag. Associated data связывает capability с assignment/reviewer, HTTP-ответ — с subject/operation/key. Полный сохранённый HTTP-ответ шифруется; capability не восстанавливают из hash. Проверка актуального владельца/Active перед выдачей или использованием остаётся обязанностью будущих handlers CHK-BE-05/06. Ключ нужно сохранять вместе с восстановлением БД; потеря/подмена обнаруживается ошибкой расшифрования.

Inbox хранит только завершённую обработку по `(consumer,messageId)`; отложенные/невалидные сообщения имеют отдельную диагностику. Outbox сохраняет стабильный messageId/envelope, попытки/следующую попытку/техническую блокировку и статус отправки. Состояния транспорта и поля retry не вводят бизнес-сроков. HTTP/recheck receipts сохраняют исходный hash/ответ; сравнение повтора и запись результата одной транзакцией будут реализованы в handlers. Автоматической очистки receipts нет.

В Local миграция применяется до открытия HTTP; `Storage:ApplyMigrations=false` отключает этот адаптер для отдельных test hosts. В остальных средах schema применяется явно при deployment. Настоящий bootstrap ссылок/материалов в этой задаче отсутствует.

Проверки из корня репозитория:

```powershell
pwsh -NoProfile -File scripts/check.ps1 -Restore
node scripts/check-checking-storage.mjs
```

Вторая команда требует Docker и подготовленный ключ (`node scripts/prepare-fixtures.mjs`). Она создаёт отдельный проект `matigr-checking-storage-tests` без host ports и без постоянного volume: PostgreSQL 18.6 в tmpfs, собственные тестовые credentials/сеть. Tests отказываются сбрасывать БД с именем, отличным от `checking_storage_test`. Проверяются up/down/up миграции, UNIQUE/CHECK/FK, реальное ожидание конкурирующей транзакции/commit/rollback, CAS, атомарный rollback связанных записей, освобождение после отказа и persistence шифрованных ответов. Затем отдельный контейнер/процесс читает прежний checkpoint с прежним ключом. `finally` удаляет только этот тестовый Compose. Существующие `matigr` volumes не используются. CI запускает ту же команду.

Это проверки границы хранения CHK-BE-02. CHK-IT-01 и HTTP/broker/E2E задачи остаются открытыми до выполнения их зависимостей и полного набора сценариев.
