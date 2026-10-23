# План Appeals

Актуализация 23 октября 2026 года: пользователь явно поручил реализацию по утверждённым документам, начиная с V0 / CHK-INF-01, с соблюдением зависимостей. Одобрение планов сохраняется; прежние фразы о непорученной реализации ниже описывают состояние при согласовании. Текущее выполнение и проверки — в [журнале реализации](../../implementation/progress.md).

Статус: **одобрен пользователем 23 октября 2026 года** вместе с задачами и планами двух других сервисов. Поручение реализации действует; APL-INF-01 завершена, остальные задачи выполняются по зависимостям. Основание — [одобренная спецификация](spec.md), [архитектура](../../architecture/architecture.md), [сообщения](../../architecture/event-contracts.md). Декомпозиция — [tasks.md](tasks.md); общие HTTP/REST-стык/матрица событий/T01–T08/V0–V6 — [согласование контрактов](../../architecture/implementation-contracts.md).

## 1. Внутренняя архитектура

Appeals владеет A9: допуском подачи, постоянной квотой на задачу и состоянием процесса до подтверждения опубликованного результата (P9, E23/E24/E28). Не владеет назначением Checking и не применяет баллы Results. Локальный process manager находится в этом же сервисе, не дополнительная saga-служба.

Проекты в `src/backend/services/Appeals/`: `Appeals.Api` (ASP.NET Core .NET 10, HTTP/права и host workers), `Appeals.Application` (Submit, ManuallyClaim, HandleCheckingFact, HandleResultReceipt, RecordManualEndFact), `Appeals.Domain` (Appeal, переходы/квота), `Appeals.Infrastructure` (PostgreSQL/EF Core/SQL, Results HTTP adapter, broker/inbox/outbox, dev-only end adapter). HTTP adapter переводит опубликованный снимок в локальную модель основания, не импортирует Results.Domain.

Frontend feature `appeals` в общем React/TypeScript/Vite app: команда подаёт по опубликованному resultId/version, видит состояние; жюри видит **Registered** до начала рассмотрения и вручную выбирает. После 202 UI показывает ожидание Checking и переходит к feature checking только после подтверждённого собственного назначения/GET. Вердикт и отказ выполняются в Checking, баллы читаются из Results. Нет автоматического claim при подаче, комментария жюри, формы загрузки решения, отзыва или отдельного отклонения до проверки.

## 2. Сущности и состояния

| Сущность | Данные/инвариант |
| --- | --- |
| Appeal | appealId, UNIQUE(tournamentId,teamId,taskId) навсегда, version/state, submittedAt, принятие по окну, immutable basis, текущая attempt/check correlation, receipt |
| PublishedBasis | resultId/resultVersion, tuple, solution/material, baseCheckId, исходная publicationId/publishedAt, outcome/decisionOrigin/scoreRuleVersion. Сохраняется при регистрации, не ребазируется |
| SelectionAttempt | requestId, appealId, requestedReviewerId, acceptedAt, hash, Pending/Assigned/RejectedBusy/RejectedConflict; история попыток, исходный selectedReviewerId успешного выбора |
| ReviewProgress | checkId, last Check aggregateVersion, currentAssignment/currentReviewer, escalationId, выбранный и фактический исполнитель; это проекция процесса, не источник занятости |
| SubmissionWindow | Одна строка на tournamentId: Open/Closed, version, факт ручного конца/actor/time. Локальная граница допуска, не агрегат полного управления турниром |

| Состояние | Переход и условие |
| --- | --- |
| Registered | ManuallyClaim свободным на момент запроса по UI жюри → AcceptedAwaitingRecheck; окончательно занятость проверяет Checking |
| AcceptedAwaitingRecheck | RecheckAssigned текущей попытки → RecheckPending; подтверждённый ReviewerBusy без Check → тот же Registered; постоянная ошибка → NeedsResolution |
| RecheckPending | CheckCompleted → AwaitingResultApplication; RecheckEscalated → AwaitingSeniorReview |
| AwaitingSeniorReview | RecheckAssigned SeniorEscalation обновляет прогресс без возврата к ручному выбору; CheckCompleted → AwaitingResultApplication |
| AwaitingResultApplication | ResultEvaluationApplied правильной оценки/публикации → Closed; постоянный Rejected → NeedsResolution |
| Closed | Терминальное успешное состояние; поздние промежуточные факты его не откатывают |
| NeedsResolution | Наблюдаемая аномалия, без автоматической новой базы/квоты/ручной корректировки (Q15) |

Доверенный Applied с полными связями может прийти раньше RecheckAssigned/CheckCompleted/RecheckEscalated и сразу закрыть Appeal текущей попытки. Не требовать доставку всех промежуточных фактов. Противоречащий финальный receipt/связи фиксируется как аномалия и не меняет уже корректное Closed. Check version упорядочивает факты только одного Check; версии разных агрегатов не сравниваются.

Одна апелляция за весь tournament/team/task, включая Closed/NeedsResolution и changed resultVersion. Первоначальный жюри может выбрать её. На старший первичный вердикт SeniorEscalation новая апелляция запрещена; роль Senior без такого происхождения сама по себе не запрет. При отказе после успешного назначения сохраняется тот же Appeal/Check/requestId, а не возврат Registered. Право дорассмотрения после конца касается и Registered, и повторного выбора после ReviewerBusy.

## 3. Схема хранения

Отдельная `appeals_db`, свои credentials/volume; FK только внутри неё.

| Таблица | Ключи, поля/ограничения |
| --- | --- |
| appeals | PK appeal_id; постоянный UNIQUE(tournament_id,team_id,task_id); state/version/submitted_at, window_version/admission_fact, immutable basis columns, current_request_id, check_id?, selected_reviewer_id?, last_check_version?, progress/receipt IDs |
| selection_attempts | PK request_id; FK appeal_id; requested reviewer, accepted_at, state, payload hash, причина/receipt; частичный UNIQUE(appeal_id) для незавершённой попытки |
| appeal_history | PK history_id; FK appeal_id; state/version/actor/time, request/check/assignment/escalation IDs и result receipt; без комментария жюри |
| submission_windows | PK tournament_id; state/version, end_fact_id?, ended_by/ended_at; fixture создаёт Open, повтор bootstrap не открывает Closed |
| manual_end_facts | PK fact_id; tournament_id, trusted actor, payload hash, server receipt/закрытая версия; повтор и конфликт различаются |
| http_idempotency | UNIQUE(subject_id,operation,key); нормализованный hash, response/status/ETag; успешный submit сохраняется для replay после конца |
| inbox / outbox / processing_failures | UNIQUE(consumer,message_id); hash/envelope, delivery state/attempt/nextAttempt, deferred/карантин/аномалия с корреляцией |

resultId/checkId/reviewerId — стабильные внешние ID без межсервисных FK. Индексы очередей по tournament/state/submittedAt/appealId, team/tournament, request/check. Исходная и итоговая publicationId хранятся **раздельно**, чтобы receipt не подменил основание подачи. Не удалять строку Appeal для восстановления квоты; retention технических сообщений не удаляет бизнес-уникальность.

## 4. REST API

| Метод и путь | Вход и права | Ответ |
| --- | --- | --- |
| POST `/api/v1/appeals` | Team subject; Idempotency-Key; resultId/expectedResultVersion. teamId берётся из удостоверения/проверяется по базе Results | 201 Registered + Location/ETag; повтор успешного ключа возвращает сохранённый ответ даже после конца; 409 AlreadyAppealed/WindowClosed/BaseNotAppealable/ResultVersionConflict; 503 если Results недоступен |
| GET `/api/v1/appeals/me?tournamentId=…` | Команда | Только свои апелляции и состояния; без служебного payload/token |
| GET `/api/v1/appeals/tournaments/{tournamentId}?state=Registered` | Жюри в scope | Очередь поданных апелляций до принятия, минимальная база/статус/ETag. Допустимые фильтры явно перечислить в OpenAPI |
| GET `/api/v1/appeals/{appealId}` | Своя команда либо уполномоченный жюри | Снимок процесса + ETag; публичного доступа нет |
| POST `/api/v1/appeals/{appealId}/claim` | Жюри; If-Match/Idempotency-Key; без requestedReviewerId из браузера | 202 AcceptedAwaitingRecheck + Location/ETag. Два жюри не сохраняют два успешных выбора одной версии |
| POST `/dev/v1/tournaments/{tournamentId}/end-facts` | Только local/test organizer; factId, Idempotency-Key | 200 закрытая версия окна; повтор идемпотентен. Production route выключен, нет турнирного CRUD |

Существующий Appeal при другом Idempotency-Key не считается второй успешной подачей: `409 AlreadyAppealed` со ссылкой на доступный своей команде объект. После stale 412 UI перечитывает, не подменяет версию автоматически. Текст команды/отзыв/предварительное отклонение не заданы Q07: не вводить обязательные поля и новые endpoint. HTTP error conventions и внутренний GET Results — общие, не локальные альтернативы.

## 5. Входящие и исходящие сообщения

| Направление | Сообщение | Правило |
| --- | --- | --- |
| Выход → Checking | RecheckRequested | Только после ручного claim; requestId, requestedReviewerId из principal, неизменяемые result/material/base/publication и submittedAt/acceptedAt |
| Вход Checking | RecheckAssigned | Корреляция текущей попытки/основания. Selected: actual=requested. SeniorEscalation: тот же Check, новый assignment, escalationId; исходный selected сохраняется |
| Вход Checking | RecheckRequestRejected | ReviewerBusy текущей попытки → Registered, квота не освобождается; новый ручной claim может создать новый requestId. Остальные постоянные ошибки → NeedsResolution |
| Вход Checking | RecheckEscalated | Подтверждённый отказ назначенной перепроверки → AwaitingSeniorReview, старый assignment уже закрыт Checking; это не ReviewerBusy |
| Вход Checking | CheckCompleted, kind=Appeal | Ожидать применения Results; ничего не начислять и не закрывать |
| Вход Results | ResultEvaluationApplied, kind=Appeal | Проверить evaluationId=checkId, resultId/expectedResultVersion/baseCheckId, tuple/material, request/selected, допустимое actual lineage и Published итог → Closed, в том числе changed=false |
| Вход Results | ResultEvaluationRejected, kind=Appeal | Только соответствующий процесс/попытка; постоянный конфликт → NeedsResolution, не менять основание |

Старый отказ busy не снимает новое назначение, старый Assigned не откатывает передачу/Closed. Applied при SeniorEscalation содержит достаточно assignment/escalation lineage для закрытия до прихода промежуточных фактов; не требовать actual reviewer=selected при этом маршруте. Во входном основании publicationId — исходная, в Applied — итоговая. Публикация не отдельный этап/событие: успешный Applied уже означает доступный результат.

Факт ручного конца в MVP вводится dev-адаптером непосредственно в Application Appeals; нового интеграционного TournamentEnded producer/сервиса нет. Будущий E9 требует авторитетного согласованного admission protocol: простой async флаг или синхронный GET не устраняет гонку. Не планировать будущий upstream как реализуемую часть MVP.

## 6. Транзакционные границы

1. Submit: аутентификация → lookup успешного HTTP replay → запрос доверенного актуального PublishedBasis к Results **вне SQL-транзакции** → проверки team/версия/origin → короткая транзакция с lock SubmissionWindow, повторной проверкой quota/idempotency → insert Registered/basis/admission/history/HTTP receipt. Не создавать RecheckRequested на подаче. UNIQUE защищает два параллельных запроса с разными ключами.
2. Manual end: organizer dev-adapter → lock той же window row → Closed/version + fact/receipt. По принятому вместе с планом T05 выигравшая транзакция допуска определяет порядок с закрытием; receivedAt — аудит, не разрешение регистрации. Для локального MVP эта часть Q02 решена; будущий upstream остаётся открытым. Нет доверия клиентскому endedAt для заднего числа.
3. Claim: lock/CAS Registered + If-Match → attempt с новым requestId и выбранным subject → AcceptedAwaitingRecheck/history/idempotency/outbox атомарно. Checking может ответить busy из-за гонки с другой обычной/апелляционной работой; локальная проекция Appeals не гарантирует свободу.
4. Входной факт/receipt: inbox/hash → lock Appeal → корреляция/версия/current attempt → переход/history/receipt/inbox одним commit. Late stale fact отмечается обработанным без регресса. Невалидный/чужой final receipt — диагностировать, не закрывать по одному appealId. Payload Applied достаточен для восстановления пропущенных промежуточных связей из доверенного producer.
5. Busy: тот же Appeal Registered, попытка завершена RejectedBusy; новый requestId только новым ручным claim. Никакой автоматической compensation-команды снятия назначения Checking: при busy оно не было создано, при успешном назначении снять его по таймауту нельзя.

Сетевых ожиданий в транзакциях нет, распределённой транзакции нет. Если после HTTP чтения база неожиданно изменится, последующий CAS Results не даст перезаписать её; процесс станет наблюдаемым конфликтом Q15. После конца окно проверяется только для **нового Submit**: claim, busy retry, отказ/старший и receipts продолжаются.

## 7. Сбои и восстановление

- Results недоступен при Submit: 503 без новой строки/расходования квоты. Потерянный успешный ответ: replay receipt до проверки окна, та же Appeal/submittedAt.
- Broker/Checking недоступен после claim: outbox повторяет ту же попытку; UI честно показывает ожидание. Таймаут не создаёт новый requestId и не возвращает Registered без подтверждённого ReviewerBusy.
- Падение после создания Check, но до доставки Assigned: повтор запроса возвращает прежний логический исход; полный Applied также может закончить процесс. Не создавать дубль/не отменять старое назначение.
- Вердикт пришёл, Results недоступен: AwaitingResultApplication, не Closed по таймеру. No-op тоже требует receipt.
- Неизвестная схема/неправильная связь: карантин/NeedsResolution с сохранённым envelope, без автоматического rebasing/повторной квоты. Redrive не создаёт новую апелляцию; ручное исправление как продуктовая операция — Q15.
- Метрики: возраст Registered/AcceptedAwaitingRecheck/AwaitingResultApplication, outbox/inbox/deferred, busy retries, stale facts, conflicts; трассы по appeal/request/check/evaluation/message/correlation IDs. Уведомления и статус доступны только разрешённым субъектам, токены/материал не логируются.

## 8. Стратегия тестирования

| Уровень | Проверка |
| --- | --- |
| Unit | Переходы, постоянная квота, запрет SeniorEscalation base, original reviewer разрешён, manual claim/self, busy vs refusal, no-op/changed receipt, late facts/Closed, allowed lineage и отсутствие комментария; AP-A06/08–13/16–18/20/21/24–26/32–36 |
| PostgreSQL + HTTP integration | UNIQUE для нескольких submit, ETag claim race, replay после закрытия/другой payload, Submit vs window lock по решению Q02, trusted basis errors/права, rollback; AP-A01–05/15/19/27/30 |
| Broker/host integration | Duplicate/redelivery/crash before-after commit/ack, Applied до Checking facts, старые attempt/assignment/version, restart waiting, Rejected/deferred/карантин; AP-A07/11–14/22–25/33–35 |
| Frontend/contract | Registered видна до выбора, 202 не притворяется assignment, busy retry ручной, private state/ошибки; одинаковые схемы с Checking/Results, internal GET error codes |
| E2E | V2 запрет апелляции на первичный старший; V3 no-op, V4 changed/rank, V5 senior fallback, V6 manual end/new submit blocked/дорассмотрение и busy retry после конца; AP-A17–36 |

Два браузерных контекста/субъекта воспроизводят гонки выбора; отдельный test harness управляет задержкой/перестановкой broker delivery. Реальные PostgreSQL/брокер обязательны для интеграции; unit mocks не доказывают уникальность/commit. Сквозные проверки ждут терминального состояния и опубликованной версии, а не фиксированного сна. Tests не реализуются на этом этапе.

## 9. Решения до реализации

T05 и dev-end adapter приняты вместе с планом для локального MVP. Остаются будущий upstream Q02, scope/несколько старших Q05, точные версии и эксплуатационные параметры Q13. Dev-end adapter ограничен тестовым вводом факта, не расширяет MVP до управления турниром. Q07/Q15/Q16 остаются открыты для будущих действий/видимости; базовые планы их не заполняют произвольными решениями. Матрица взаимодействий и пройденная веха согласования — [общий документ](../../architecture/implementation-contracts.md).
