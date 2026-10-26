# Межсервисные контракты Matigr — CHK-INT-01, RES-INT-01 и APL-INT-01

Контракты Checking опубликованы в CHK-INT-01, Results — в RES-INT-01, Appeals — в APL-INT-01, 23 октября 2026 года по одобренным планам. Актуализация статуса 26 октября: runtime endpoints/handlers, inbox/outbox/recovery и provider-consumer gates трёх сервисов завершены; [доказательства](../docs/implementation/progress.md). Канонические JSON-файлы редактируются непосредственно, не генерируются из Domain/ORM. Форму проверяют contract tests; работоспособность процессов — отдельные integration/E2E tests.

## HTTP Results

[OpenAPI 3.1.1](http/results/openapi.json) задаёт анонимный snapshot standings и SSE invalidation, частный результат команды и единственный internal basis endpoint Results → Appeals. UserBearer и отдельный ServiceBearer имеют разные security schemes; user JWT не подходит для internal route. API-контракт не добавляет ручное начисление, изменение стоимости, загрузку решений или управление турниром. Health и бизнес-маршруты Results реализованы в RES-BE-03/04/05. Публичные standings DTO не включают private basis, а internal basis запрещает дополнительные поля и содержит только опубликованный источник.

Баллы и версии представлены точными десятичными строками. Pending-ячейка не содержит score; Published-ячейка содержит score. Team-result `appealAvailabilityHint` не разрешает подачу: решение остаётся за Appeals. GET/SSE публичны, личный экран использует server-side team scope, внутренний вызов требует отдельной service identity и точной expectedResultVersion.

## HTTP Appeals

[OpenAPI 3.1.1](http/appeals/openapi.json) публикует подачу, собственный список/деталь, очередь только Registered, ручной claim и локальный dev-end-fact adapter. Все частные маршруты требуют UserBearer и серверной проверки роли/scope/ownership; submit принимает только resultId/version, а claim выводит выбранного reviewer из удостоверения. `202 AcceptedAwaitingRecheck` не утверждает, что Checking подтвердил назначение. Dev end-fact route существует только в Local/Test; турнирного CRUD или планового закрытия нет. Все перечисленные маршруты Appeals, хранение quota/history и process manager реализованы. Private AppealView различает SeniorQueued/SeniorAssigned через reviewState; это не право на назначение.

`results-appeal-basis.v1.schema.json` — независимое ожидание Appeals для внутреннего ответа Results; отдельный fixture сверяет его с OpenAPI provider и неизменяемой базой RecheckRequested. Это contract test, не вызов Results и не разрешение на подачу.

## HTTP Checking

[OpenAPI 3.1.1](http/checking/openapi.json) содержит семь защищённых операций Checking, dev-выбор сессии и два health endpoint. В CHK-BE-04 реализованы dev-выбор/profile и [контракт проверки JWT/серверных прав](authentication.md). `/api/v1/checking/*` реализованы в CHK-BE-05/06, health — в инфраструктурных задачах. Документ публикуется в репозитории; HTTP-раздача OpenAPI ещё не добавлена.

- Все бизнес-команды требуют `Idempotency-Key`; complete/refuse дополнительно требуют единственный strong `If-Match`. Повтор успешной команды после проверки аутентификации/принадлежности возвращает исходный ответ до проверки версии/текущего состояния. Разный payload с тем же ключом — `409 IdempotencyConflict`. Конкретный Check входит в идентичность операции; изменяемые precondition-заголовки не создают новую бизнес-команду.
- Нет If-Match — `428 IfMatchRequired`, устарел — `412 CheckVersionConflict`. Конфликт занятости/состояния — `409`; пустая очередь/отсутствие своего назначения — `204` без тела. TakeNext не требует If-Match: Check выбирает сервер.
- `401/403` разделяют отсутствие удостоверения и полномочий, `404` скрывает недоступный Check, `503` — временную зависимость/материал. Ошибки — ProblemDetails с `code` и `correlationId`. Health readiness 503 имеет собственное тело с доступностью зависимостей.
- Capability возвращается только актуальному владельцу в MyAssignment через защищённое HTTP-чтение/успешное назначение; CheckView и очередь его не содержат. Материал читается отдельно только владельцем. Все частные ответы — `Cache-Control: private, no-store`.
- Тестовый material adapter использует подготовленные `text/plain` материалы fixture v1. В браузере нет пути/URL для чтения произвольного файла. Недоступный материал не превращается в Incorrect.
- Dev-выбор сессии — изолированная stateless выдача удостоверения по серверному fixture key, без бизнес-записи или ETag. Тело не принимает роли/teamId/scope. Эту техническую операцию не смешивать с бизнес-командами и их receipts. Вне Local/Test маршрут отсутствует; token lifetime задаётся issuer, контракт не вводит срок турнира/назначения.
- Пагинация очереди использует технический непрозрачный cursor. Она не резервирует работу и не задаёт бизнес-лимитов/приоритетов. Scope проверяет backend; старшая очередь дополнительно требует Senior.

Поля ответов и запросов различаются: в частности, `check.version`, ETag, `reviewRevision` и `expectedResultVersion` — разные значения. Версии JSON — десятичные строки, schemaVersion сообщения — integer 1. Внешние ID непрозрачны: потребитель не навязывает UUID-формат и не выводит полномочия из значения. Внутренние новые ID по T07 остаются UUID.

## Сообщения Checking

Все схемы используют JSON Schema Draft 2020-12. `$id` — идентификатор документа в зарезервированном `.invalid` пространстве, не сетевой адрес для загрузки. Схемы валидируются локально.

| Контракт | Артефакт | Получатель |
| --- | --- | --- |
| CheckCompleted | [Схема](events/checking/check-completed.v1.schema.json) | Results для Initial/Appeal, Appeals только Appeal |
| RecheckAssigned | [Схема](events/checking/recheck-assigned.v1.schema.json) | Appeals |
| RecheckEscalated | [Схема](events/checking/recheck-escalated.v1.schema.json) | Appeals |
| RecheckRequestRejected | [Схема](events/checking/recheck-request-rejected.v1.schema.json) | Appeals |
| RecheckRequested | [Ожидание Checking](events/checking/consumers/recheck-requested.v1.schema.json), [producer schema Appeals](events/appeals/recheck-requested.v1.schema.json), [wire fixture](../tests/contracts/fixtures/appeals/recheck-requested.json) | Checking |

[Каталог](events/checking/catalog.json) сопоставляет схемы с маршрутами из [Compose topology](../infra/compose/topology.json). Тест проверяет отдельные копии CheckCompleted в очередях Results и Appeals. Статическая маршрутизация дополнена настоящими broker delivery/ack/outbox и crash/recovery gates.

## Сообщения Results

- [CheckCompleted consumer expectation](events/results/consumers/check-completed.v1.schema.json) независимо закрепляет вход Results и contract test сверяет её семантику со схемой producer Checking. Initial и Appeal сохраняют раздельные routing keys; Results получает свою копию Appeal наряду с Initial.
- [ResultEvaluationApplied](events/results/result-evaluation-applied.v1.schema.json) подтверждает только kind=Appeal после доступной Published публикации. `basePublicationId` — исходная publicationId из immutable basis; `publicationId` — итоговая. Для changed=false обе ссылки совпадают, но отдельная квитанция всё равно обязательна. Фактические reviewer/assignment/origin хранятся отдельно от исходно выбранного жюри; SeniorEscalation несёт escalationId.
- [ResultEvaluationRejected](events/results/result-evaluation-rejected.v1.schema.json) фиксирует коррелированный постоянный бизнес-конфликт, не баллы. Временная недоступность/отсутствующая зависимость остаётся Deferred/Retry и не становится reject. Причины перечислены описательно; схема не выдумывает незакрытый enum.

[Каталог Results](events/results/catalog.json) и контрактные тесты проверяют маршруты с Compose topology и очередь `appeals.result-receipts.v1`. Форма и статическая маршрутизация дополнены runtime consumer/outbox/inbox/ack и проверками транзакций RES-INT-02/03 и RES-IT-01–05.

## Сообщения Appeals

- [RecheckRequested producer schema](events/appeals/recheck-requested.v1.schema.json) принадлежит Appeals и совпадает с consumer expectation Checking. Он появляется только после ручного claim; requestedReviewerId связан с authenticated fixture subject, а `recheckRequestId` — каноническое имя ID попытки в wire.
- [Каталог Appeals](events/appeals/catalog.json) содержит собственные consumer expectations для CheckCompleted Appeal, RecheckAssigned, RecheckEscalated, RecheckRequestRejected и двух Results receipts. CheckCompleted Initial отклоняется Appeals-consumer schema; очереди одного факта не делятся между сервисами.
- Fixtures копируют producer-owned примеры Checking/Results и проверяются отдельно у Appeals. Contract tests сравнивают общие IDs, reviewer selection/actual reviewer, escalation, исходную publicationId и итоговую publicationId/no-op. Статическая совместимость дополнена runtime handlers APL-INT-03/05/06 и provider-consumer gates APL-INT-04/08. Глобального порядка доставки нет; перестановки и ранние финальные receipts проверены отдельно.

Envelope обязателен целиком: messageId/messageType/schemaVersion/producer/occurredAt/aggregateType/aggregateId/aggregateVersion/correlationId/causationId/payload. Технический повтор сохраняет эти данные. Времена — UTC, версии — точные строки; время не определяет порядок.

- Completed/Assigned/Escalated используют Check stream (`aggregateType=Check`, aggregateId=checkId). RecheckRequested использует Appeal stream. Отрицательный ответ без созданного Check использует неизменяемый receipt stream `RecheckRequest`, aggregateId=recheckRequestId, aggregateVersion=`"1"`; это не новый бизнес-агрегат/сервис.
- CheckCompleted Initial требует expectedResultVersion=`"0"`, не содержит resultId и appeal-полей. Appeal требует полный снимок базы: appealId/recheckRequestId/selectedReviewerId/resultId/baseCheckId/publicationId/expectedResultVersion>0. publicationId здесь всегда исходная база; итоговую публикацию позднее сообщает Results.
- OrdinaryReview возможен для Initial, AppealReview — для Appeal, SeniorEscalation — для обоих kind с обязательным escalationId. Выбранный и фактический жюри различаются при передаче. Selected назначение и обычное AppealReview должны относиться лично к selectedReviewerId; SeniorEscalation допускает другого фактического исполнителя.
- RecheckEscalated освобождает исходного выбранного жюри и сохраняет checkId/appealId/recheckRequestId. Busy отличается: Check/assignment ещё нет. Никакое техническое падение БД/брокера не публикуется как ReviewerBusy.
- reasonCode отрицательного ответа v1: ReviewerBusy, MaterialNotFound (постоянно неизвестная ссылка, не временный сбой чтения), BasisMismatch, BaseNotAppealable (запрет базы SeniorEscalation), RequestConflict, ReviewerNotAuthorized (нет серверного scope выбранного). Busy возвращает тот же Appeal к ручному выбору; прочие постоянные конфликты требуют разрешения. Новое значение enum требует проверки consumers; автоматического rebasing/новой квоты нет.

Checking/Appeals сообщения не передают баллы, capability/assignmentToken, комментарий или материал; Results receipts содержат собственные score-поля. Устаревшее wire-имя `requestId` запрещено, используется `recheckRequestId`. Читатель допускает неизвестные необязательные добавления без смены смысла; тест публикации дополнительно ограничивает producer объявленными полями, чтобы случайно не отправить внутреннюю модель. Смена обязательного типа/смысла/enum требует отдельной проверки совместимости, а не приблизительного чтения.

Стандартная JSON Schema не сравнивает произвольные поля экземпляра между собой. Поэтому identity envelope/payload, равенство selected/actual при Selected/AppealReview и released/selected проверяются отдельно в [контрактном harness](../tests/contracts/support.mjs). Producer/consumer adapters реализуют schema/semantic guards в runtime. Контекстные связи с БД, права transport, inbox/CAS/уникальность и порядок доставки проверены отдельными integration-задачами; форма JSON не доказывает их.

Fixtures — независимые примеры допустимых ветвей, не последовательность реальных событий/не история импорта в БД. RecheckRequested producer и consumer examples используют одни и те же immutable IDs/basis. APL-INT-01 добавляет независимые consumer expectations Appeals для Checking/Results и проверяет статическую совместимость; runtime-обработчики и provider-consumer интеграция завершены и проверены отдельно от этих fixtures.

## Проверка

```sh
npm ci --ignore-scripts --no-audit --no-fund
npm run test:contracts
```

Валидаторы закреплены: `@apidevtools/swagger-parser` 12.1.0, Ajv 8.17.1, ajv-formats 3.0.1, транзитивные версии — root package-lock.json. Проверяются структура OpenAPI, компиляция всех HTTP/message schemas, валидные примеры и мутации полей/kind/origin/versions/identity; форматы дат включены, приведения типов нет. Это воспроизводимые проверки контрактов, не доказательство реализованных бизнес-переходов.

Технические источники: [OpenAPI 3.1.1](https://spec.openapis.org/oas/v3.1.1.html), [JSON Schema 2020-12](https://json-schema.org/draft/2020-12), [Ajv JSON Schema](https://ajv.js.org/json-schema.html), [Swagger Parser](https://github.com/APIDevTools/swagger-parser).
