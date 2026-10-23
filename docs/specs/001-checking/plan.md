# План Checking

Актуализация 23 октября 2026 года: пользователь явно поручил реализацию по утверждённым документам, начиная с V0 / CHK-INF-01, с соблюдением зависимостей. Одобрение планов сохраняется; прежние фразы о непорученной реализации ниже описывают состояние при согласовании. Текущее выполнение и проверки — в [журнале реализации](../../implementation/progress.md).

Статус: **одобрен пользователем 23 октября 2026 года** вместе с задачами и планами двух других сервисов. Основание — [одобренная спецификация](spec.md), [архитектура](../../architecture/architecture.md), [контракты сообщений](../../architecture/event-contracts.md). Реализация не начата и этим сообщением не поручена. Работы описаны в [tasks.md](tasks.md).

Общие технические решения T01–T08, HTTP-правила, матрица совместимости и порядок V0–V6 — в [согласовании контрактов](../../architecture/implementation-contracts.md). Они приняты вместе с планами в описанном объёме; отсутствующие значения и явно открытые вопросы не считаются решёнными.

## 1. Внутренняя архитектура

Checking владеет проверкой A7: назначением, занятостью, бинарным вердиктом и передачей старшему (P5/P6, E16–E20). Перепроверка использует те же команды и инварианты, а не отдельный сервис/движок. Баллы принадлежат Results, допустимость/квота апелляции — Appeals.

Предлагаемые проекты в `src/backend/services/Checking/`: `Checking.Api` (ASP.NET Core .NET 10, HTTP, authentication/authorization, composition root), `Checking.Application` (use cases, порты хранилища/материала/публикации), `Checking.Domain` (Check, Assignment, переходы), `Checking.Infrastructure` (собственная PostgreSQL, миграции, outbox/inbox, RabbitMQ, read-only material adapter, dev-issuer). Domain не зависит от web/ORM/брокера. Фоновые workers запускаются в host Api и используют те же Application use cases; при нескольких экземплярах координируются БД, не памятью процесса.

Главные операции: BootstrapAcceptedReference, TakeNext, GetMyAssignment, Complete, RefuseToSenior, HandleRecheckRequested. Fixtures загружают только уже принятые метаданные и подготовленный материал. Нет upload/intake API, QR, формы выбора задачи командой или live SolutionAccepted consumer. Метаданные не изменяют материал, не принимают файл из браузера.

Frontend: feature `checking` в единственном `src/frontend/apps/web`. Экран текущего назначения, защищённое чтение материала, Correct/Incorrect и отказ; обычная/старшая очереди видны по правам. При старте и возвращении GET текущего назначения; успешный отказ/завершение освобождает UI без ожидания Results. На неоднозначный сетевой ответ — повтор команды с прежним ключом и сверка GET. Поля комментария нет.

CHK-FE-01/02: [единый frontend](../../../src/frontend/apps/web/README.md) создан с маршрутами трёх feature-модулей, HTTP DTO Checking, общим JSON/ProblemDetails transport и переключением тестовых субъектов/profile через реальный issuer. Токен только в памяти; смена субъекта очищает частное состояние. Реальные очереди/вердикты, standings/SSE и апелляции реализуются отдельными задачами ниже; текущий UI их не имитирует.

CHK-BE-04: [dev-выдача/profile и JWT verification contract](../../../contracts/authentication.md) реализованы отдельно от бизнес-модели. Серверные policies проверяют reviewer/senior и турнирный scope; CHK-BE-05 проверяет актуального владельца/назначение при чтении, Complete/Refuse реализованы в CHK-BE-06. Frontend-сессия подключена в CHK-FE-02.

## 2. Сущности и состояния

| Сущность | Содержимое и инвариант |
| --- | --- |
| SolutionReference | Неизменяемые solutionId, tournamentId/teamId/taskId, materialVersionId/ref, acceptedAt/sourceVersion. Одна принятая ссылка по ключу; конфликт новой ссылки не является повторной подачей |
| Check | checkId, solutionId, kind Initial/Appeal, reviewRevision, state, version, outcome/decisionOrigin; для Appeal — appealId/requestId, исходный selectedReviewerId и основание результата |
| Assignment | assignmentId, checkId, reviewerId, role/scope evidence, capability hash, startedAt/endedAt, Active/Completed/Refused. Один Active на Check и один Active на reviewerId во всём Checking |
| Escalation/history | escalationId, снятое назначение, actor/time, новые состояния и версия Check; исходный выбор на апелляции сохраняется |
| RecheckRequestReceipt | Стабильный исход попытки requestId: Assigned с исходным Check/событием либо Rejected с причиной. Hash обнаруживает иной payload |

| Check до | Команда | Check после | Атомарный эффект |
| --- | --- | --- | --- |
| Отсутствует | Bootstrap | Queued, kind=Initial | Создать ссылку/первичный Check один раз |
| Queued | TakeNext обычной очереди | Assigned | Назначить свободного жюри |
| Отсутствует | RecheckRequested при свободном выбранном | Assigned, kind=Appeal | Создать один Check/назначение именно requestedReviewerId |
| Assigned | Complete | Completed | Записать Correct/Incorrect, origin OrdinaryReview или AppealReview; освободить жюри |
| Assigned | RefuseToSenior | SeniorQueued | Закрыть Assignment, отменить capability, освободить жюри, записать escalationId |
| SeniorQueued | TakeNext старшей очереди | SeniorAssigned | Назначить свободного уполномоченного старшего |
| SeniorAssigned | Complete | Completed | Финальный SeniorEscalation с assignmentId/escalationId, освободить старшего |

Completed неизменяем; новая оценка — отдельный Appeal Check. Старший в SeniorAssigned завершает финальным вердиктом: дополнительного маршрута отказа/автовозврата не вводить. ReviewerBusy не создаёт Check. Нет TTL назначения или освобождения по отключению клиента (D05); технический lease outbox-worker не связан с занятостью жюри. Занятость глобальна по reviewerId, а не по турниру/очереди/вкладке. Конец турнира не закрывает проверки уже принятого материала.

## 3. Схема хранения

Отдельная `checking_db`, отдельные credentials/volume. Имена таблиц ниже — проект схемы, не миграции. UUID/иной формат ID уточняется T07; время — UTC/timestamptz, версии — монотонный bigint с безопасным JSON-представлением.

| Таблица | Ключи, поля и ограничения |
| --- | --- |
| solution_references | PK solution_id; UNIQUE(tournament_id,team_id,task_id); material_version_id/ref, source_version, accepted_at; сравнение полного неизменяемого снимка при повторе |
| checks | PK check_id; локальный FK solution_id; UNIQUE(solution_id,review_revision); частичный UNIQUE(solution_id) WHERE kind=Initial; частичный UNIQUE(appeal_id) WHERE kind=Appeal; state/version, outcome, origin, current_assignment_id, baseline columns; условие согласованности kind и полей Appeal |
| assignments | PK assignment_id; локальный FK check_id; reviewer_id, capability_hash и защищённый capability_ciphertext для повторного чтения владельцем, state/times; частичные UNIQUE(reviewer_id) и UNIQUE(check_id) WHERE state=Active; действующий capability изменяется при новом назначении |
| check_history | PK history_id; FK check_id; UNIQUE(check_id,version,transition_type); actor, assignment/escalation IDs, from/to state, outcome/time; без комментария |
| recheck_request_receipts | PK request_id; appeal_id, request_hash, исход Assigned/Rejected, check_id?, response_envelope; разные попытки busy сохраняются отдельно |
| reviewer_scopes | Локальная fixture-проекция стабильных субъектов/ролей/доступных турниров; не аккаунты/регистрация. Занятость определяется assignments, а не второй несогласованной копией |
| http_idempotency | UNIQUE(subject_id,operation,key); hash, HTTP status/response/ETag; чувствительный сохранённый ответ защищён как соответствующий API |
| inbox / outbox / processing_failures | Inbox UNIQUE(consumer,message_id), payload hash; outbox message_id/envelope/status/attempt/next_attempt; диагностика и карантин с корреляцией |

Локальные FK возможны только внутри checking_db; appealId/resultId/teamId — внешние ID без FK к другим БД. Индексы очереди по state/tournament/createdAt/checkId. Секрет защиты capability стабилен при restart и хранится вне БД/репозитория; GET владельца восстанавливает capability из защищённого значения, не пытается восстановить его из hash. Материал не хранится как байты в БД/сообщениях; adapter разрешает только подготовленные materialRef из allowlist, не произвольный filesystem path/URL клиента. Недоступный материал — ошибка чтения, а не Incorrect.

CHK-BE-02: [схема хранения, миграция и границы записи](../../../src/backend/services/Checking/Checking.Infrastructure/Storage/README.md) реализованы. Partial UNIQUE проверены на настоящей PostgreSQL, версия Check участвует в CAS, capability и чувствительный HTTP replay хранятся зашифрованно. Bootstrap реализован в CHK-BE-03, TakeNext/read handlers — в CHK-BE-05; Complete/Refuse реализованы в CHK-BE-06; transport остаётся следующими задачами; schema tests не объявляются завершением CHK-IT-01.

## 4. REST API

CHK-BE-03: внутренний bootstrap принятых ссылок/Initial Check и read-only fixture material adapter реализованы. Local startup закрепляет неизменный набор в `fixture_sets`, повтор не создаёт новую работу и не сбрасывает назначение/вердикт. Материал выдаётся только по полному allowlisted snapshot после проверки SHA-256, без записи/загрузки. HTTP route и актуальное право владельца реализованы в CHK-BE-05.

Все маршруты с `/api/v1/checking`, кроме dev-issuer. Форма HTTP DTO опубликована отдельно от доменных классов в [Checking OpenAPI](../../../contracts/http/checking/openapi.json), условные поля сообщений — в [JSON Schema](../../../contracts/README.md) (CHK-INT-01). TakeNext, очереди, текущее назначение/Check, материал владельца и отдельные .NET DTO/serialization adapters реализованы в CHK-BE-05; Complete/Refuse реализованы в CHK-BE-06; transport остаётся следующими задачами.

| Метод и путь | Права и вход | Результат |
| --- | --- | --- |
| GET `/queues?tournamentId=…&queue={queue}` | Жюри в scope; queue=general либо senior; senior требует Senior. Пагинация — техническая, не резервирует Check | 200: доступные метаданные/счётчики без чужих capability |
| POST `/me/assignments/next` | Idempotency-Key; body tournamentId/queue, reviewer из subject | 200 Assignment + Check/ETag, 204 если очередь пуста, 409 ReviewerBusy; фильтр очереди не позволяет взять чужой текущий Check |
| GET `/me/assignment` | Актуальный subject | 200 текущее назначение и capability/ETag; 204 если свободен; фильтр турнира не прячет занятую работу другого турнира |
| GET `/checks/{checkId}` | Владелец/разрешённый scope чтения; минимум служебных данных | 200 состояние/версия; token только актуальному владельцу |
| GET `/checks/{checkId}/material` | Актуальный владелец назначения | Read-only готовый материал; 503 при временной недоступности; private/no-store, без анонимного materialRef |
| POST `/checks/{checkId}/complete` | Владелец, If-Match, Idempotency-Key; assignmentId/capability, outcome Correct/Incorrect | 200 Completed + версия; Results может ещё ожидать событие |
| POST `/checks/{checkId}/refuse` | Владелец Assigned, те же preconditions; assignmentId/capability | 200 SeniorQueued + версия; прежний жюри уже свободен |
| POST `/dev/v1/session/select` | Только local/test; testSubjectId — ключ серверной fixture | 200 подписанное dev-удостоверение и разрешённый профиль; произвольные роли/teamId не принимаются |

После 412/409 UI перечитывает состояние; не генерирует новую команду автоматически. Состояния завершения/отказа и идемпотентные повторы не опираются на локальный кэш. Ограничения на дополнительное чтение истории остаются Q16; отдельного публичного endpoint истории нет. Входной межсервисный RecheckRequested идёт только через брокер, не дублируется REST-командой.

## 5. Интеграционные события

| Направление | Контракт | Локальный эффект/основание отправки |
| --- | --- | --- |
| Вход Appeals → Checking | RecheckRequested | Проверить requestId/hash, связи с тем же материалом и Completed baseCheck, версию/публикацию как переданное основание, scope выбранного; не читать чужую БД и не пересчитывать допустимость Appeals |
| Выход → Results; → Appeals для kind=Appeal | CheckCompleted | Только commit Completed; binary outcome, фактический reviewerId/assignmentId, decisionOrigin, expectedResultVersion; без баллов |
| Выход → Appeals | RecheckAssigned | Первое персональное назначение Selected либо новое SeniorEscalation на том же Check; capability отсутствует |
| Выход → Appeals | RecheckEscalated | Commit отказа на существующем Appeal Check; прежние appealId/requestId/selectedReviewerId сохраняются |
| Выход → Appeals | RecheckRequestRejected | Зафиксированный исход попытки: ReviewerBusy либо постоянный конфликт; инфраструктурная недоступность не является бизнес-отказом |

Initial: expectedResultVersion=0 и без appeal-полей. Appeal: resultId, expectedResultVersion, baseCheckId, исходный publicationId, appealId/requestId и selectedReviewerId обязательны. SeniorEscalation в обоих kind требует escalationId. reviewRevision — отдельный номер рассмотрения, его пропуски не доказывают потерю событий. Полные payload/envelope — [канонический контракт](../../architecture/event-contracts.md).

Повтор requestId возвращает прежний исход даже после смены занятости/передачи. Новый requestId после ReviewerBusy допустим внутри той же апелляции. Если успешный Check уже существует, согласованный повтор основания с новой попыткой возвращает существующий Check без смены исполнителя; конфликт основания не создаёт второй Check и не обходит запрет SeniorEscalation.

## 6. Транзакционные границы и конкуренция

1. Bootstrap: принять внутренний prepared snapshot → сравнить immutable fields/уникальный ключ → создать Initial Check в одной транзакции. Повтор после сбоя идемпотентен; изменение fixture не заменяет существующий материал.
2. TakeNext: в короткой транзакции выбрать/заблокировать подходящий Check с пропуском уже заблокированных строк, проверить scope и свободное назначение, вставить Active Assignment, обновить Check CAS, записать history/HTTP receipt. Частичные UNIQUE защищают и двух жюри на один Check, и одного жюри на два Check. При unique conflict/serialization rollback перечитать и вернуть корректный исход, не частично назначать. Возврат capability — после commit.
3. Complete/Refuse: lock Check + сверка version/assignment/capability/subject → переход, закрытие Assignment, history, HTTP receipt и outbox одной транзакцией. Конкурирующие complete/refuse имеют только одного победителя. Брокер и Results не участвуют в транзакции; освобождение действует сразу после commit.
4. RecheckRequested: inbox/receipt lookup → проверка baseCheck и ключей → попытка атомарного персонального назначения + Check/history/receipt/outbox/inbox. Гонки unique обрабатываются rollback и новой короткой транзакцией чтения/фиксации исхода. ReviewerBusy сохраняет только receipt/inbox/ответный outbox, без Check. Ревизия выделяется под блокировкой SolutionReference.
5. Senior claim использует тот же механизм занятости. Для Appeal в той же транзакции — RecheckAssigned с новым assignmentId/escalationId. Scope/queue decision проверяется на сервере независимо от списка UI.

Не держать SQL-транзакцию при чтении файла или ожидании брокера. HTTP replay ищется до проверки устаревшего ETag, но после аутентификации. Deadlock/serialization retry ограничивается технической политикой с тем же идентификатором команды. Длительное ожидание команды не доказывает свободу жюри.

## 7. Сбои и наблюдаемость

- Потерян HTTP-ответ: исход находится в idempotency receipt; GET текущего назначения/Check восстанавливает UI после restart/reconnect. Доменное назначение не имеет heartbeat-таймера.
- Commit перед падением publisher: outbox позже отправит тот же messageId. Падение после broker confirm до пометки sent допускает дубликат. Непривязанный маршрут — ошибка доставки, а не sent.
- Consumer падает до commit — rollback и повтор; после commit до ack — inbox/receipt возвращает прежний эффект. Poison payload/несовместимая схема — карантин с raw envelope и безопасной диагностикой, без бесконечного горячего requeue.
- Нет базы/материала/связей: различать временную зависимость и постоянное нарушение; не записывать Incorrect или ReviewerBusy вместо технической ошибки. Ручная коррекция/освобождение — отдельный Q06/Q15, не скрытая команда в MVP.
- Метрики: возраст/размер outbox, повторы/inbox conflicts, очереди/активные назначения, конфликты CAS/unique, карантин. Logs/traces связывают correlationId, checkId, appealId/requestId и messageId без токенов/фото. Readiness различает недоступную БД и деградацию доставки брокера.

## 8. Стратегия тестирования

| Уровень | Что доказывает |
| --- | --- |
| Unit | Таблица переходов Initial/Appeal, бинарные исходы, immutable материал/Completed, origin/escalation lineage, scope и глобальная занятость как policy; CH-A05/06/18–20/28/29/31/33–36 |
| Integration, настоящая PostgreSQL | Реальные partial UNIQUE/locks/CAS: два жюри — один Check, один жюри — две очереди/апелляция, complete/refuse, replay hash, bootstrap/revision, assignment после рестарта; CH-A01–04/07/13–16/21–27/30/37 |
| Integration, broker/host/material | Outbox/inbox crash windows, duplicates с тем же/новым messageId, потеря ответов, invalid schema/lineage, busy request replay, безопасное чтение fixtures; CH-A08–12/17/24/25/32/34 |
| Contract | OpenAPI права/errors/ETag; схемы всех исходящих сообщений проверяются consumers Results/Appeals, RecheckRequested — producer Appeals; обязательные поля по kind/origin, никакого capability/score/comment |
| E2E, три сервиса + браузер | V1 обычный цикл; V2 отказ/старший; V3–V5 личная перепроверка/передача совместно с Appeals; V6 reconnect/restart/позднее рассмотрение. Проверять UI и итоговые инварианты, а не только HTTP 200 |

Гонки синхронизируются барьерами/конкурентными клиентами на реальной БД; не доказываются mock-репозиторием. Сквозные тесты ждут наблюдаемого состояния по ID/версии с техническим deadline, без fixed sleep. Нагрузка/допустимый лаг ещё Q10; тестовые timeout не объявляются SLO.

## 9. Решения до реализации

Решения и оставшиеся вопросы — [итоги ревью](../../architecture/implementation-contracts.md#7-решения-и-оставшиеся-вопросы). Для Checking приняты T08 (алгоритм очереди) и T02/T03 (технологический выбор); остаются Q05 (scope/несколько старших) и детали Q13 (версии/эксплуатационные параметры). Q06 не разрешает вводить TTL/ручное снятие: базовый MVP сохраняет назначения. Контракты будущего приёма не превращать в работы текущего плана. Спецификация и план одобрены; задачи реализации ещё не выполнены.

Текущая реализация 24 октября: CHK-BE-06, CHK-UT-01, CHK-IT-01 и CHK-FE-03 завершены; фактические проверки/ограничения — в журнале. Экран жюри подключён к реальному API. Публикация outbox и runtime RecheckRequested остаются CHK-INT-02/03.
