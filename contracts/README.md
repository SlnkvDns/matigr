# Контракты Matigr — CHK-INT-01

Опубликованы 23 октября 2026 года по одобренным планам. Канонические JSON-файлы редактируются непосредственно; они не генерируются из Domain/ORM. Наличие контракта не означает реализацию соответствующего endpoint или consumer. Этот шаг не закрывает CHK-INT-04, RES-INT-01/03 или APL-INT-01/04/08.

## HTTP Checking

[OpenAPI 3.1.1](http/checking/openapi.json) содержит семь защищённых операций Checking, dev-выбор сессии и два реализованных health endpoint. Доступные обработчики пока только `/health/live` и `/health/ready`; `/api/v1/checking/*` и dev-issuer реализуются в CHK-BE-04–06. Документ публикуется в репозитории; HTTP-раздача OpenAPI ещё не добавлена.

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
| RecheckRequested | [Ожидание Checking](events/checking/consumers/recheck-requested.v1.schema.json), [входной пример](../tests/contracts/fixtures/appeals/recheck-requested.json) | Checking; producer schema владельца Appeals ещё относится к APL-INT-01 |

[Каталог](events/checking/catalog.json) сопоставляет схемы с маршрутами из [Compose topology](../infra/compose/topology.json). Тест проверяет отдельные копии CheckCompleted в очередях Results и Appeals. Это статическая проверка маршрутизации; runtime delivery/ack/outbox пока не реализованы.

Envelope обязателен целиком: messageId/messageType/schemaVersion/producer/occurredAt/aggregateType/aggregateId/aggregateVersion/correlationId/causationId/payload. Технический повтор сохраняет эти данные. Времена — UTC, версии — точные строки; время не определяет порядок.

- Completed/Assigned/Escalated используют Check stream (`aggregateType=Check`, aggregateId=checkId). RecheckRequested использует Appeal stream. Отрицательный ответ без созданного Check использует неизменяемый receipt stream `RecheckRequest`, aggregateId=recheckRequestId, aggregateVersion=`"1"`; это не новый бизнес-агрегат/сервис.
- CheckCompleted Initial требует expectedResultVersion=`"0"`, не содержит resultId и appeal-полей. Appeal требует полный снимок базы: appealId/recheckRequestId/selectedReviewerId/resultId/baseCheckId/publicationId/expectedResultVersion>0. publicationId здесь всегда исходная база; итоговую публикацию позднее сообщает Results.
- OrdinaryReview возможен для Initial, AppealReview — для Appeal, SeniorEscalation — для обоих kind с обязательным escalationId. Выбранный и фактический жюри различаются при передаче. Selected назначение и обычное AppealReview должны относиться лично к selectedReviewerId; SeniorEscalation допускает другого фактического исполнителя.
- RecheckEscalated освобождает исходного выбранного жюри и сохраняет checkId/appealId/recheckRequestId. Busy отличается: Check/assignment ещё нет. Никакое техническое падение БД/брокера не публикуется как ReviewerBusy.
- reasonCode отрицательного ответа v1: ReviewerBusy, MaterialNotFound (постоянно неизвестная ссылка, не временный сбой чтения), BasisMismatch, BaseNotAppealable (запрет базы SeniorEscalation), RequestConflict, ReviewerNotAuthorized (нет серверного scope выбранного). Busy возвращает тот же Appeal к ручному выбору; прочие постоянные конфликты требуют разрешения. Новое значение enum требует проверки consumers; автоматического rebasing/новой квоты нет.

Схемы запрещают поля capability/assignmentToken/баллов/комментария/материала и устаревшее wire-имя `requestId`. Читатель допускает неизвестные необязательные добавления без смены смысла; тест публикации дополнительно ограничивает producer объявленными полями, чтобы случайно не отправить внутреннюю модель. Смена обязательного типа/смысла/enum требует отдельной проверки совместимости, а не приблизительного чтения.

Стандартная JSON Schema не сравнивает произвольные поля экземпляра между собой. Поэтому identity envelope/payload, равенство selected/actual при Selected/AppealReview и released/selected проверяются отдельно в [контрактном harness](../tests/contracts/support.mjs). Эти проверки — явные требования будущих producer/consumer adapters. Они ещё не подключены к runtime. Контекстные связи с БД, права transport, inbox/CAS/уникальность и порядок доставки проверяются поздними integration-задачами; форма JSON не доказывает их.

Fixtures — независимые примеры допустимых ветвей, не последовательность реальных событий/не история импорта в БД. Входная RecheckRequested fixture и исходящие примеры используют одни и те же immutable IDs/basis для проверки стыка. Полная совместимость независимого producer Appeals и consumers Results/Appeals проверяется после появления их контрактов; их реализация не объявляется завершённой сейчас.

## Проверка

```sh
npm ci --ignore-scripts --no-audit --no-fund
npm run test:contracts
```

Валидаторы закреплены: `@apidevtools/swagger-parser` 12.1.0, Ajv 8.17.1, ajv-formats 3.0.1, транзитивные версии — root package-lock.json. Проверяются структура OpenAPI, компиляция всех HTTP/message schemas, валидные примеры и мутации полей/kind/origin/versions/identity; форматы дат включены, приведения типов нет. Это воспроизводимые проверки контрактов, не доказательство реализованных бизнес-переходов.

Технические источники: [OpenAPI 3.1.1](https://spec.openapis.org/oas/v3.1.1.html), [JSON Schema 2020-12](https://json-schema.org/draft/2020-12), [Ajv JSON Schema](https://ajv.js.org/json-schema.html), [Swagger Parser](https://github.com/APIDevTools/swagger-parser).
