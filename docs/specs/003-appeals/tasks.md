# Задачи Appeals

Актуализация 26 октября 2026 года: первоначальная реализация завершена, все задачи этого документа выполнены. Одобрение от 23 октября 2026 года сохраняется. Проверенные результаты и оставшиеся ограничения — в [текущей сводке](../../implementation/progress.md), подробные шаги — в [архиве MVP](../../implementation/progress-mvp-2026-10-24.md).

Статус: **одобрены пользователем 23 октября 2026 года** вместе с планами и задачами двух других сервисов. Все задачи первоначальной реализации завершены. Основания — [plan.md](plan.md), [одобренная спецификация](spec.md) и [общие контракты/V0–V6](../../architecture/implementation-contracts.md). Веха `MVP-REVIEW` пройдена.

Глобальные зависимости: [Checking](../001-checking/tasks.md), [Results](../002-results/tasks.md). Каждая строка — отдельный проверяемый результат. T05 принят вместе с планами: Submit/ручной конец сериализуются на окне Appeals; будущий upstream Q02 остаётся открытым. Не выбирать другую границу молча. Q07/Q15 не превращаются в новые команды коррекции/отзыва. Все сквозные операции используют уже принятый материал, без upload.

## Инфраструктура

| ID | Сценарий | Зависимости | Работа и проверяемый результат | Статус |
| --- | --- | --- | --- | --- |
| APL-INF-01 | V0 | MVP-REVIEW, CHK-INF-01, CHK-INF-02, CHK-INF-03, CHK-INF-04 | **Выполнена 23 октября 2026 года**, см. [журнал](../../implementation/progress.md). Создать четыре проекта Appeals/test projects, собственные DB credentials/volume/migrations/readiness и подключение к общему Compose/CI. Проверка: build/architecture references, нет cross-DB доступа; окно fixture Open создаётся без повторного открытия Closed | Выполнена |

## Backend

| ID | Сценарий | Зависимости | Работа и проверяемый результат | Статус |
| --- | --- | --- | --- | --- |
| APL-BE-01 | V3 | APL-INF-01 | Реализовать Appeal/PublishedBasis/SelectionAttempt/SubmissionWindow и переходы домена. Проверка: постоянная квота, immutable основание, manual selection, no-op требует receipt, original checker допустим; нет комментария жюри | Выполнена |
| APL-BE-02 | V3 | APL-BE-01, APL-INT-01 | Создать миграции постоянной tuple UNIQUE/attempt/истории/window/end facts/idempotency/inbox/outbox. Проверка: новые resultVersion/Closed не разрешают новую строку Appeal, исходная/итоговая публикации раздельны | Выполнена |
| APL-BE-03 | V6 | APL-BE-02, CHK-BE-04 | Реализовать dev-only ручной end-fact adapter с organizer scope, повторяемостью и блокировкой окна по принятому T05/Q02. Проверка: факт идемпотентен, чужой/production запрос запрещён; нет турнирного CRUD, автоматического конца по часам или повторного Open | Выполнена |
| APL-BE-04 | V3/V6 | APL-BE-02, APL-INT-02, APL-UT-01 | Реализовать Submit с trusted Results basis вне транзакции, авторизацией команды, lock окна/UNIQUE/idempotency/history. Проверка: Registered без RecheckRequested, успешный replay проверяется до состояния окна, Results down не расходует квоту. Для V3 достаточно fixture Open; dev end adapter принимается в V6 | Выполнена |
| APL-BE-05 | V3 | APL-BE-04, CHK-BE-04 | Реализовать очереди Registered/свои апелляции/детали и ManuallyClaim с If-Match, attempt/requestId и outbox. Проверка: выбранный берётся из subject; два жюри не сохраняют две попытки, 202 ещё не assignment, окно конца не блокирует старую Registered | Выполнена |

## Интеграция

| ID | Сценарий | Зависимости | Работа и проверяемый результат | Статус |
| --- | --- | --- | --- | --- |
| APL-INT-01 | V0 | APL-INF-01, CHK-INT-01, RES-INT-01 | **Выполнена 23 октября 2026 года**, см. [журнал](../../implementation/progress.md). Опубликованы Appeals OpenAPI, producer schema RecheckRequested, отдельные consumer schemas всех фактов Checking/квитанций Results и ожидание internal HTTP basis. Fixtures проверяют requestedReviewerId из fixture subject, `recheckRequestId`, неизменяемое исходное основание и отдельные base/final publicationId | Выполнена |
| APL-INT-02 | V3 | APL-INT-01, RES-BE-04, CHK-BE-04 | Реализовать Results HTTP adapter с service auth, timeout/cancellation и различением 404/409/503; не держать SQL-транзакцию при запросе. Проверка: недоверенный snapshot/кэш/browser fields не разрешают Submit | Выполнена |
| APL-INT-03 | V3 | APL-BE-05, CHK-INT-02, CHK-INT-03 | Подключить outbox RecheckRequested/inbox и начальный process manager RecheckAssigned/RecheckRequestRejected. Проверка: current attempt/version/hash, busy→Registered только по подтверждению, stale reply не снимает новое назначение; таймаут не создаёт новый request | Выполнена |
| APL-INT-04 | V3 | APL-INT-03, APL-INT-05, APL-INT-07, CHK-INT-04, RES-INT-03 | Выполнить совместимость HTTP/schemas/routing обычного апелляционного маршрута и негативных payload. Проверка: обе стороны понимают выбранного жюри, immutable basis и семантику двух publicationId; ошибки/версии согласованы. Runtime senior path принимается отдельной задачей V5 | Выполнена |
| APL-INT-05 | V3/V4 | APL-INT-03, RES-INT-02, RES-BE-06 | Добавить CheckCompleted/Applied/Rejected: ожидание результата и закрытие только по Published receipt, no-op/changed, корреляция основания. Проверка: финальный receipt достаточен до Assigned/Completed, старые факты не открывают Closed | Выполнена |
| APL-INT-06 | V5 | APL-INT-03, APL-INT-05 | Добавить RecheckEscalated/назначение старшего и полную selected/actual lineage в progress/receipt. Проверка: тот же Appeal/Check/requestId, не Registered; Applied старшего до промежуточных фактов принимается при валидных связях | Выполнена |
| APL-INT-07 | V3/V6 | APL-INT-03, APL-INT-05 | Подключить deferred/quarantine/redrive, readiness и метрики ожидания/конфликтов/correlation к обработчикам Appeals. Проверка: poison не теряется/не вызывает горячий requeue, секреты не логируются, redrive не создаёт новую квоту | Выполнена |
| APL-INT-08 | V5 | APL-INT-04, APL-INT-06, CHK-INT-04, RES-INT-03 | Расширить runtime contract checks senior-маршрутом. Проверка: assignmentKind/decisionOrigin, обязательные escalationId/selectedReviewerId, фактический старший и достаточный ранний Applied одинаково понимаются тремя сервисами | Выполнена |

## Frontend

| ID | Сценарий | Зависимости | Работа и проверяемый результат | Статус |
| --- | --- | --- | --- | --- |
| APL-FE-01 | V2/V3 | CHK-FE-02, RES-FE-03, APL-BE-04 | Добавить подачу по resultId/version, свой список и обработку AlreadyAppealed/WindowClosed/BaseNotAppealable/stale/503. Проверка component/API tests: двойной клик/повтор использует прежний ключ, новый resultVersion не открывает вторую форму подачи; окончательное право серверное | Выполнена |
| APL-FE-02 | V3 | APL-FE-01, CHK-FE-03, APL-BE-05, APL-INT-03 | Добавить jury очередь Registered и ручной claim/pending/ReviewerBusy retry тем же Appeal. Проверка: Selected жюри открывает только своё назначение Checking, 202 не показывает ложное назначение; повтор выбора ручной, без auto-claim | Выполнена |
| APL-FE-03 | V3/V4 | APL-FE-02, APL-INT-05 | Добавить детали ожидания применения/Closed/NeedsResolution и обновление статуса через защищённое чтение; вопросы push/private уведомлений не расширять. Проверка: старый ответ не откатывает версию, после конца старые апелляции доступны, смена subject очищает private cache | Выполнена |
| APL-FE-04 | V5 | APL-FE-03, APL-INT-06 | Добавить отображение ожидания/назначения старшего после отказа. Проверка component tests: не предлагать новый ручной выбор Registered и не показывать старое назначение как текущее | Выполнена |

## Unit-тестирование

| ID | Сценарий | Зависимости | Проверяемый результат | Статус |
| --- | --- | --- | --- | --- |
| APL-UT-01 | V3 | APL-BE-01 | Доменные тесты допуска/квоты/immutable basis/переходов, один Appeal навсегда, запрет senior-origin, ручной выбор самого себя/original reviewer, no-op vs changed. AP-A01/03/06/08–10/16–18/20/21/26/36 | Выполнена |
| APL-UT-02 | V3 | APL-INT-05 | Табличные перестановки facts/receipt: Applied раньше assigned/completed, старый busy/assignment, wrong links, терминальный Closed. AP-A11–13/23–25 | Выполнена |
| APL-UT-03 | V5 | APL-INT-06, APL-UT-02 | Перестановки escalation/senior assignment/Applied, Selected vs Senior actual lineage, освобождённый прежний жюри и immutable исходный выбор. AP-A32–35 | Выполнена |

## Integration-тестирование

| ID | Сценарий | Зависимости | Проверяемый результат | Статус |
| --- | --- | --- | --- | --- |
| APL-IT-01 | V3 | APL-BE-04, APL-UT-01 | Реальные PostgreSQL + HTTP Results: два Submit/разные ключи, hash conflict/replay, unpublished/wrong team/senior base/stale/basis unavailable. AP-A01/03–06/15/21; гонка с ручным концом вынесена в APL-IT-05 | Выполнена |
| APL-IT-02 | V3 | APL-BE-05, APL-IT-01 | Concurrent claim двух жюри, stale ETag/повтор ключа, Registered видна до claim, attempt/outbox atomic rollback. AP-A17–19/23; на этом тесте 202 не считается назначением | Выполнена |
| APL-IT-03 | V3/V4 | APL-INT-03, APL-UT-02, APL-IT-02, CHK-IT-03, RES-IT-05 | Реальный broker: гонка с обычной работой, busy→новый ручной request в том же Appeal, stale reply, ранний Applied, no-op и changed, Rejected. AP-A08–13/22–26/31; CAS Results не обходится | Выполнена |
| APL-IT-04 | V6 | APL-INT-07, APL-IT-03 | Fault tests до/после commit/ack/confirm, Checking/Results/broker down, restart ожидания, redrive и poison payload. AP-A07/14/15; таймаут не создаёт новую попытку/Check, нет ложного Closed или новой квоты | Выполнена |
| APL-IT-05 | V6 | APL-BE-03, APL-BE-04, APL-IT-01 | Гонка Submit/end с барьерами по принятому Q02/T05, ручной конец только organizer, replay успешного Submit/end после Closed, повтор bootstrap не открывает окно. AP-A02/04/27/30; ReceivedAt не обходит выбранную транзакционную границу | Выполнена |
| APL-IT-06 | V5 | APL-IT-03, APL-UT-03, APL-INT-08 | Реальные три сервиса/broker: отказ назначенного, очередь и назначение старшему, его Applied до RecheckEscalated/Assigned, позднее старое назначение и restart. AP-A32–35; нет второго Check/Appeal/квоты и регресса Closed | Выполнена |

## End-to-end и порядок приёмки

| ID | Сценарий | Зависимости | Проверяемый результат | Статус |
| --- | --- | --- | --- | --- |
| APL-E2E-01 | V2 | CHK-E2E-02, APL-FE-01, APL-IT-01, RES-IT-03 | После первичного отказа/вердикта старшего команда видит опубликованный итог, но новая апелляция отвергается сервером; роль Senior сама по себе не подменяет decisionOrigin | Выполнена |
| APL-E2E-02 | V3 | APL-FE-03, APL-IT-03, APL-INT-04, CHK-E2E-01, RES-FE-03 | Команда Submit → Registered видна жюри → ручной выбор (включая первоначального) → личный Check → тот же outcome → Applied(changed=false) → Closed. Квота не возвращается, публикация не меняет дату/автора | Выполнена |
| APL-E2E-03 | V4 | APL-E2E-02, RES-E2E-02 | Полный changed сценарий до Closed и обновлённой анонимной таблицы; новую апелляцию после изменения не подать. Проверить, что Closed относится к итоговой publicationId, а исходная база сохранена | Выполнена |
| APL-E2E-04 | V5 | APL-E2E-02, APL-FE-04, APL-IT-06 | Выбранный отказывается → свободен → тот же Check старшему → его финальный вердикт → Results → Closed. Поменять порядок доставки Applied/эскалации/назначения, не потерять selected/actual lineage | Выполнена |
| APL-E2E-05 | V6 | APL-E2E-03, APL-E2E-04, APL-BE-03, APL-IT-05, APL-IT-04, CHK-E2E-03 | Organizer manual end: новые Submit заблокированы; прежний successful replay, Registered claim, busy retry, отказ/старший и завершение/публикация после конца разрешены. Два браузера/сбои не создают дубль Appeal/Check; AP-A02/04/27–35 | Выполнена |

Общий порядок демонстраций: V1 Checking/Results → V2 APL-E2E-01 → V3 APL-E2E-02 → V4 APL-E2E-03 → V5 APL-E2E-04 → V6 APL-E2E-05. В процессе готовятся только зависимости следующего проверяемого вертикального сценария, не все backend-функции трёх сервисов до начала UI. Проверки используют ID/version-based ожидание с техническим deadline, не fixed sleep.

Готовность Appeals: все строки приняты, AP-A01–AP-A36 прослеживаются в тестах; permanent quota/window/assignment/receipt semantics подтверждены; Q02 решение и Q15 ограничения явно записаны. Готовность всего MVP дополнительно требует E2E и contract gates соседних сервисов; Приёмка первоначального MVP завершена; доказательства приведены в журнале и V1–V6.
