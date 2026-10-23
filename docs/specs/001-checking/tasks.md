# Задачи Checking

Актуализация 23 октября 2026 года: пользователь явно поручил реализацию по утверждённым документам, начиная с V0 / CHK-INF-01, с соблюдением зависимостей. Одобрение планов сохраняется; прежние фразы о непорученной реализации ниже описывают состояние при согласовании. Текущее выполнение и проверки — в [журнале реализации](../../implementation/progress.md).

Статус: **одобрены пользователем 23 октября 2026 года** вместе с планами и задачами двух других сервисов; ни одна задача реализации не начата. Основание — [plan.md](plan.md) и [одобренная спецификация](spec.md). Одобрение списка не означает выполнение задач или поручение писать приложение.

Каждая строка — отдельная проверяемая работа; все задачи открыты. Зависимости — обязательные предшественники, не только порядок строк. Префиксы CHK/RES/APL глобальны; задачи соседей: [Results](../002-results/tasks.md), [Appeals](../003-appeals/tasks.md). Веха `MVP-REVIEW` из [общего порядка V0–V6](../../architecture/implementation-contracts.md#6-общий-порядок-вертикальных-сценариев) пройдена: планы одобрены. Реализация начинается по отдельному поручению без повторного согласования тех же планов.

Применять принятые технические решения плана; перед затрагиваемым сценарием уточнять только действительно не заданные детали из реестра вопросов. Не добавлять функции из Q06/Q07/Q15 вместо их согласования. Общий каркас, Compose, fixture manifest и frontend shell создаются здесь один раз; соседние сервисы подключают свои части.

## Инфраструктура

| ID | Сценарий | Зависимости | Работа и проверяемый результат |
| --- | --- | --- | --- |
| CHK-INF-01 | V0 | MVP-REVIEW | Закрепить совместимые .NET 10 SDK, EF/provider, PostgreSQL, RabbitMQ/client, Node/package manager и тестовые инструменты по официальным источникам; зафиксировать версии/решения T02/T03/T07, без floating/latest. Проверка: воспроизводимая матрица версий и согласованные Q05/Q09 fixtures decisions |
| CHK-INF-02 | V0 | CHK-INF-01 | Создать monorepo layout, solution и четыре проекта только Checking, тестовые проекты/общие команды build и CI. Проверка: сборка и запрет Domain → Api/Infrastructure и cross-service Domain references; не создавать пустые доменные библиотеки «на будущее» |
| CHK-INF-03 | V0 | CHK-INF-02 | Поднять Compose infrastructure: три независимые PostgreSQL с разными credentials/volumes, RabbitMQ topology/health, сеть и параметры API. Проверка: инфраструктура стартует с чистого volume, чужие credentials не читают БД; API подключаются после появления проектов, без fake-сервисов |
| CHK-INF-04 | V0 | CHK-INF-03 | Подготовить общий manifest fixtures со стабильными subject/team/task/solution IDs и фиксированными баллами после решения Q09, read-only материал и локальные secrets/key persistence вне Git. Проверка: checksum/version, безопасный повтор bootstrap; нет загрузки/QR/приёма |

## Backend

| ID | Сценарий | Зависимости | Работа и проверяемый результат |
| --- | --- | --- | --- |
| CHK-BE-01 | V1/V2 | CHK-INF-02 | Реализовать SolutionReference, Check, Assignment и чистые переходы Initial/Appeal/Senior. Проверка: Completed и материал неизменяемы, исход бинарный, без комментария; scope и единая занятость явно представлены |
| CHK-BE-02 | V1 | CHK-BE-01, CHK-INF-03 | Создать миграции собственных таблиц/partial UNIQUE/CAS/inbox/outbox/idempotency. Проверка: миграции с чистой БД, constraints реально запрещают два Active на жюри/Check; capability восстанавливается после restart с сохранённым ключом |
| CHK-BE-03 | V1 | CHK-BE-02, CHK-INF-04 | Добавить идемпотентный внутренний bootstrap принятых ссылок/Initial Check и read-only adapter материала. Проверка: повтор не создаёт новую работу, конфликт материала наблюдаем; произвольный path/URL недоступен, отсутствие файла не создаёт Incorrect |
| CHK-BE-04 | V0/V1 | CHK-INF-04, CHK-INT-01 | **Выполнена 23 октября 2026 года**, [verification contract](../../../contracts/authentication.md), [журнал](../../implementation/progress.md). Изолированный dev-issuer/profile, JWT и серверные role/scope policies. Проверка: подмена browser role/team запрещена, ключ/subjectId устойчивы, выдача только Local/Test; объектные права реальных бизнес-handlers остаются CHK-BE-05/06 |
| CHK-BE-05 | V1/V2 | CHK-BE-02, CHK-BE-04, CHK-INT-01 | Реализовать TakeNext, queues, GetMyAssignment/Check и доступ владельца к материалу. Проверка: атомарное назначение и 204/409/ETag по контракту, один жюри не получает две работы из разных турниров/очередей |
| CHK-BE-06 | V1/V2/V5 | CHK-BE-05 | Реализовать Complete/Refuse с capability/If-Match/idempotency и атомарным освобождением/history/outbox. Проверка: следующий Check доступен до применения Results; отказ переводит тот же Check в SeniorQueued и старый capability недействителен |

## Интеграция

| ID | Сценарий | Зависимости | Работа и проверяемый результат |
| --- | --- | --- | --- |
| CHK-INT-01 | V0 | CHK-INF-02 | **Выполнена 23 октября 2026 года**, см. [артефакты](../../../contracts/README.md) и [журнал](../../implementation/progress.md). Checking OpenAPI и producer schemas CheckCompleted/RecheckAssigned/RecheckEscalated/RecheckRequestRejected; consumer expectation/fixture RecheckRequested. Проверка: HTTP semantics/обязательность kind/origin и имена полей совпадают с общим документом, capability/баллы не попадают в события. Runtime adapters и полная проверка независимых consumers остаются CHK-INT-02–04 |
| CHK-INT-02 | V1 | CHK-INF-03, CHK-BE-02, CHK-INT-01 | Реализовать outbox publisher, inbox consumer adapter, confirms/manual ack, bounded backoff/deferred/quarantine и correlation/health/метрики. Проверка: commit без брокера сохраняет событие, unroutable не помечается sent; оформить применимый образец для других сервисов без общей доменной библиотеки |
| CHK-INT-03 | V3/V5 | CHK-INT-02, CHK-BE-06, APL-INT-01 | Реализовать HandleRecheckRequested, request receipt/hash, назначение выбранному, ReviewerBusy без Check и ответные события; senior assignment/escalation на том же Appeal Check. Проверка: technical retry возвращает прежний исход, новая попытка после busy не расходует второй Check |
| CHK-INT-04 | V1/V3/V5 | CHK-INT-01, CHK-INT-02, RES-INT-01, APL-INT-01 | Выполнить schema/fixture producer-consumer tests с обеих сторон и проверить отдельные маршруты копий CheckCompleted. Проверка: negative fixtures отвергают пропущенный escalationId/неверный Selected reviewer/schemaVersion; wire использует recheckRequestId. Полный runtime маршрута перепроверки проверяется позже в CHK-IT-03, поэтому V1 не ждёт реализации апелляций |

## Frontend

| ID | Сценарий | Зависимости | Работа и проверяемый результат |
| --- | --- | --- | --- |
| CHK-FE-01 | V0 | CHK-INF-02, CHK-INT-01 | **Выполнена 23 октября 2026 года**, [frontend](../../../src/frontend/apps/web/README.md), [журнал](../../implementation/progress.md). Один React/TypeScript/Vite app, маршруты feature-модулей, HTTP/ProblemDetails client и команды typecheck/component tests. Проверка: нет второго frontend у соседних сервисов, API DTO генерируются из OpenAPI отдельно от domain; бизнес-экраны остаются последующими задачами |
| CHK-FE-02 | V0/V1 | CHK-FE-01, CHK-BE-04 | Добавить переключение dev-субъектов/профиль. Проверка: токен только в памяти, смена пользователя очищает private query/cache/материал/действия; анонимная таблица остаётся доступной |
| CHK-FE-03 | V1/V2 | CHK-FE-02, CHK-BE-03, CHK-BE-05, CHK-BE-06 | Добавить очередь/текущее назначение/материал/бинарный вердикт/отказ и senior queue. Проверка component/API tests: правильные действия по state, без комментария, после передачи можно взять следующую |
| CHK-FE-04 | V1/V6 | CHK-FE-03, CHK-IT-02 | Добавить восстановление текущего назначения при возврате/reconnect, обработку 412/409/потерянного ответа с тем же ключом. Проверка frontend tests: старый ответ/другая вкладка не возвращает завершённое назначение; нет таймера освобождения |

## Unit-тестирование

| ID | Сценарий | Зависимости | Проверяемый результат |
| --- | --- | --- | --- |
| CHK-UT-01 | V1/V2 | CHK-BE-01 | Табличные тесты разрешённых/запрещённых переходов, scope, одной работы, origins, фиксации материала и окончательности Senior. Критерии CH-A05/06/18–20/28/29/31/35/36 |
| CHK-UT-02 | V3/V5 | CHK-INT-03 | Тесты request hash/повтора busy и успешного исхода, immutable selectedReviewerId и смены actual assignment при эскалации. Критерии CH-A09/10/26/30/33/34/37 |

## Integration-тестирование

| ID | Сценарий | Зависимости | Проверяемый результат |
| --- | --- | --- | --- |
| CHK-IT-01 | V1 | CHK-BE-02, CHK-UT-01 | На настоящей PostgreSQL проверить миграции и unique constraints с параллельными транзакциями/rollback. Не использовать in-memory ORM для доказательства занятости |
| CHK-IT-02 | V1/V2/V6 | CHK-BE-03, CHK-BE-06, CHK-INT-02, CHK-IT-01 | Гонки HTTP: два жюри/одна работа, один жюри/две очереди, complete/refuse, lost reply/replay с устаревшим ETag, restart host/current assignment, права на material. CH-A01–04/07/11–16/21–25 |
| CHK-IT-03 | V3/V5 | CHK-INT-03, CHK-INT-04, CHK-UT-02, CHK-IT-02 | Конкурентная обычная работа и appeal одного жюри, busy→новый ручной request, исходный жюри, senior fallback без второго Check, чужой payload/старый token; fault/retry после создания перепроверки. CH-A09/10/26–34/37 |
| CHK-IT-04 | V1/V6 | CHK-INT-02, CHK-BE-06 | Fault tests первичного вердикта/outbox с реальным брокером: crash до/после commit/confirm, broker down/restart, повтор доставки, unroutable/poison/schema и redrive технического adapter. CH-A08/17/24/25; эффект и messageId сохраняются. Consumer crash/ack для перепроверки — CHK-IT-03 |

## End-to-end и порядок приёмки

| ID | Сценарий | Зависимости | Проверяемый результат |
| --- | --- | --- | --- |
| CHK-E2E-01 | V1 | CHK-FE-04, CHK-IT-02, CHK-IT-04, CHK-INT-04, RES-FE-02, RES-INT-02, RES-IT-02 | Владелец первого общего E2E: fixture solution → jury UI → Correct/Incorrect → Results → анонимная таблица. Дождаться result/snapshot version, не fixed sleep; жюри может взять следующую до доставки Results |
| CHK-E2E-02 | V2 | CHK-E2E-01, CHK-IT-02, CHK-UT-01 | Браузерный отказ, сохранение очереди занятому старшему, свобода обычного, финальный senior verdict, возврат после закрытия вкладки. Проверка фактического assignment/origin в Results; реализация апелляционного consumer ещё не требуется |
| CHK-E2E-03 | V6 | CHK-E2E-02, CHK-IT-04, APL-BE-03 | При завершённом вручную турнире взять/передать/закончить уже принятое решение; рестарт APIs/брокера и reconnect не теряют назначение/публикацию. Приёмка CH-A22–25/32/35 без авторелиза |

Порядок демонстраций: V0 → CHK-E2E-01 (V1) → CHK-E2E-02 (V2); затем сценарии Appeals V3–V5; в V6 CHK-E2E-03 дополняет проверки соседей. Инфраструктурные и backend-задачи не образуют отдельный многонедельный этап без UI: каждый E2E требует работающий экран, API и интеграцию своего сценария.

Готовность Checking к MVP: все строки приняты, CH-A01–CH-A37 прослеживаются в тестах, контракты согласованы у consumers, failures/ограничения Q06/Q15 документированы. Это будущий критерий, не отчёт о выполнении.
