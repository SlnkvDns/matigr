# Задачи Results

Актуализация 23 октября 2026 года: пользователь явно поручил реализацию по утверждённым документам, начиная с V0 / CHK-INF-01, с соблюдением зависимостей. Одобрение планов сохраняется; прежние фразы о непорученной реализации ниже описывают состояние при согласовании. Текущее выполнение и проверки — в [журнале реализации](../../implementation/progress.md).

Статус: **одобрены пользователем 23 октября 2026 года** вместе с планами и задачами двух других сервисов. RES-INF-01 и RES-INT-01 выполнены в рамках поручения реализации; остальные задачи реализации открыты. Основания — [plan.md](plan.md), [одобренная спецификация](spec.md), [общие контракты/V0–V6](../../architecture/implementation-contracts.md).

Зависимости — глобальные IDs: [Checking](../001-checking/tasks.md), [Appeals](../003-appeals/tasks.md). Веха MVP-REVIEW пройдена; повторное одобрение планов не требуется. T02–T04 и отдельный pending status/пустая сумма=0 приняты вместе с планами; конкретные значения Q09 позже явно согласованы пользователем при поручении реализации: 10/20 целых баллов, строки JSON ([профиль](../../architecture/runtime-baseline.md)). Эксплуатационные параметры остаются открытыми. CI не фиксирует произвольные значения как бизнес-правило. Shell/Compose/auth fixtures — единственные общие из Checking.

## Инфраструктура

| ID | Сценарий | Зависимости | Работа и проверяемый результат |
| --- | --- | --- | --- |
| RES-INF-01 | V0 | MVP-REVIEW, CHK-INF-01, CHK-INF-02, CHK-INF-03, CHK-INF-04 | **Выполнена 23 октября 2026 года**, см. [журнал](../../implementation/progress.md). Создать четыре проекта Results и test projects, подключить только results_db к Compose, migrations/readiness и локальные команды/CI. Проверка: build/architecture references, отдельные credentials/volume, согласованный fixture manifest, нет доступа к чужим БД |

## Backend

| ID | Сценарий | Зависимости | Работа и проверяемый результат |
| --- | --- | --- | --- |
| RES-BE-01 | V1/V4 | RES-INF-01 | Реализовать TeamTaskResult/Publication/EvaluationReceipt и чистые scoring/CAS/no-op policies. Проверка: binary→фиксированные баллы, отсутствие результата отдельно, неизменный cost и Published source при no-op |
| RES-BE-02 | V1 | RES-BE-01, CHK-INF-04 | Создать миграции unique result/evaluation/publication, snapshots/inbox/outbox/diagnostics и идемпотентный seed roster/rules. Проверка: checksum/version mismatch не меняет стоимость; тип баллов и JSON точны по принятому Q09/T07 |
| RES-BE-03 | V1 | RES-BE-02, RES-INT-01 | Реализовать первичный ApplyCheckCompleted: receipt до CAS expected=0, lock порядка снимка/результата, публикация/acknowledgement одним commit, конфликт второго Initial. Проверка: rollback не оставляет частичного результата, один checkId не применяется повторно; маршрут Appeal подключается отдельно |
| RES-BE-04 | V1/V3 | RES-BE-03, RES-INT-01 | Реализовать anonymous standings, private team results и internal appeal-basis. Snapshot query читает одну согласованную версию и вычисляет sum/dense rank; internal GET проверяет expected version/доверенный service. Проверка: все маршруты/DTO/errors совпадают с контрактом |
| RES-BE-05 | V1/V6 | RES-BE-04 | Реализовать SSE invalidate/handshake после регистрации подписки и наблюдение snapshotVersion для каждой API replica. Проверка: только public поля; reconnect/пропуск сигнала восстанавливается GET; технические интервалы настраиваются, не объявлены SLO |
| RES-BE-06 | V3/V4 | RES-BE-03 | Добавить применение Appeal: проверку immutable basis/версии, замену вклада, changed/no-op receipts и постоянный reject без rebasing. Проверка: no-op сохраняет источник/публикацию, SeniorEscalation передаёт actual lineage; V1 не ждёт этого маршрута |

## Интеграция

| ID | Сценарий | Зависимости | Работа и проверяемый результат |
| --- | --- | --- | --- |
| RES-INT-01 | V0 | RES-INF-01, CHK-INT-01 | **Выполнена 23 октября 2026 года**, см. [журнал](../../implementation/progress.md). Опубликованы Results OpenAPI, consumer schema CheckCompleted и producer schemas Applied/Rejected с fixtures и проверками. В контракте один internal basis endpoint, исходная/итоговая publicationId различаются, kind/origin/selected/actual/escalation проверяются; Applied receipt обязателен и при no-op |
| RES-INT-02 | V1/V4 | RES-BE-03, RES-INT-01, CHK-INT-02 | Подключить собственные broker consumer/outbox/inbox/receipts, defer отсутствующих зависимостей и карантин. Проверка: durable маршрут своей копии CheckCompleted, ack только после commit; Initial conflicts наблюдаемы без внешнего consumer; метрики/trace IDs |
| RES-INT-03 | V3/V5 | RES-INT-02, RES-BE-06, APL-INT-01, CHK-INT-01 | Provider/consumer tests с Checking/Appeals для сообщений и HTTP основания, negative fixtures и схемы версии. Проверка: Applied достаточен для закрытия до Checking facts; Changed=false не меняет publication source; raw receipt не выходит в public DTO |

## Frontend

| ID | Сценарий | Зависимости | Работа и проверяемый результат |
| --- | --- | --- | --- |
| RES-FE-01 | V1 | CHK-FE-01, RES-BE-04 | Добавить results feature: анонимная таблица всех команд/задач, score/status/total/dense rank по DTO. Проверка component tests: равные места, pending не равен Incorrect, браузер не определяет итоговые баллы/места |
| RES-FE-02 | V1/V6 | RES-FE-01, RES-BE-05 | Подключить SSE, version reconciliation, GET/reconnect и видимость деградации. Проверка frontend tests: событие во время GET/старый ответ не откатывает снимок; новый anonymous browser видит обновление |
| RES-FE-03 | V3 | RES-FE-01, CHK-FE-02, RES-BE-04 | Добавить личные результаты и передачу resultId/expectedResultVersion в appeals route, без локальной выдачи права. Проверка: смена subject очищает private кэш; отображается ожидание результата, недоступные поля не попадают в публичную таблицу |

## Unit-тестирование

| ID | Сценарий | Зависимости | Проверяемый результат |
| --- | --- | --- | --- |
| RES-UT-01 | V1/V4 | RES-BE-01 | Scoring Correct/Incorrect/no result, замена вместо инкремента, no-op/новый outcome при равном score, immutable cost. RS-A01–03/06/07/22/31 |
| RES-UT-02 | V1/V4 | RES-BE-01 | Ранжирование отдельных сумм/равных групп 1,1,2, слияние/разделение групп и изменение мест нескольких команд; отсутствие tie-break. RS-A18/20/24/25. Если авторитетный rank вычисляется SQL, те же case-наборы проверяются на SQL в RES-IT-03, не создаётся второй production алгоритм ради unit |

## Integration-тестирование

| ID | Сценарий | Зависимости | Проверяемый результат |
| --- | --- | --- | --- |
| RES-IT-01 | V1 | RES-BE-03, RES-UT-01, RES-UT-02 | Настоящая PostgreSQL: duplicate check/new message, два Initial, чужой solution, две задачи одной команды одновременно, history/snapshot commit/rollback. RS-A04/05/09/13–15/17/23; no-op/stale Appeal проверяются отдельно |
| RES-IT-02 | V1/V6 | RES-INT-02, RES-IT-01 | Broker/host failures первичного результата до/после commit/ack/confirm, unavailable scoring dependency→defer→redrive, poison schema, idempotent receipts и Published snapshot после Applied. RS-A09/10/16/17/26/29/30; нет фиктивного 0/успеха |
| RES-IT-03 | V1/V3/V4 | RES-BE-04, RES-INT-02, CHK-BE-04, RES-IT-01 | HTTP auth/public-field allowlist/internal basis errors и consistent snapshot под конкурирующими commit; реальные SQL dense ranks и team/table equality. RS-A13/14/18–21/23–26/31; чужая команда не получает private данные |
| RES-IT-04 | V1/V6 | RES-BE-05, RES-IT-03 | SSE/GET gap: commit между subscribe/handshake/GET, disconnect/reconnect, пропуск invalidation, две API replicas, version monotonic UI. RS-A11/12/19/21; метрики lag без обещания неустановленного Q10 |
| RES-IT-05 | V3/V4/V5 | RES-BE-06, RES-IT-01, RES-INT-02 | Реальные PostgreSQL/broker: stale basis, no-op CAS/replay, смена outcome при равном score, чужой материал, senior appeal lineage, late completion и crash окна receipt. RS-A06–09/22/27/28; никакого двойного вклада/нового publishedAt при no-op |

## End-to-end и порядок приёмки

| ID | Сценарий | Зависимости | Проверяемый результат |
| --- | --- | --- | --- |
| RES-E2E-01 | V1 | CHK-E2E-01, RES-FE-02, RES-IT-03 | Расширить общий V1 тестами второго анонимного браузера и командного экрана: автоматически опубликованные scores/total/rank совпадают; public HTTP/SSE без входа и без private полей. Не дублировать setup базового V1 |
| RES-E2E-02 | V4 | RES-E2E-01, APL-FE-03, APL-INT-03, APL-IT-03, RES-INT-03 | Через реальную апелляцию изменить вердикт/баллы, проверить замену вклада и места нескольких команд/1,1,2. Проверить no-op без нового publishedAt и сохранённую историю. Это gate таблицы V4; полное закрытие процесса проверяет APL-E2E-03 |
| RES-E2E-03 | V6 | RES-E2E-01, RES-IT-02, RES-IT-04 | Браузер отключён при обновлении, API/broker restarted, уведомление пропущено: после reconnect GET восстанавливает итоговую версию. Повтор событий не меняет total/version/publication повторно. Измерить snapshot/transaction smoke и записать ограничение Q10 без выдуманного нагрузочного порога |

Приёмка вертикально: V0 foundations → V1 CHK-E2E-01 + RES-E2E-01 → V3 no-op с Appeals → V4 RES-E2E-02 + APL-E2E-03 → V5 senior lineage → V6 RES-E2E-03. Исправление API/схемы в одной задаче обязательно повторяет соответствующие consumer checks.

Готовность Results: все строки приняты, RS-A01–RS-A31 покрыты, таблица анонимна и согласована с опубликованными результатами, нет stale overwrite/двойного начисления, Q09/Q10/Q13 решения и ограничения отражены в документах. Это критерий будущей реализации.
