# План Results

Актуализация 26 октября 2026 года: первоначальная реализация завершена, все задачи этого документа выполнены. Одобрение от 23 октября 2026 года сохраняется. Проверенные результаты и оставшиеся ограничения — в [текущей сводке](../../implementation/progress.md), подробные шаги — в [архиве MVP](../../implementation/progress-mvp-2026-10-24.md).

Статус: **одобрен пользователем 23 октября 2026 года** вместе с задачами и планами двух других сервисов. Все задачи первоначальной реализации завершены. Основание — [одобренная спецификация](spec.md), [архитектура](../../architecture/architecture.md), [сообщения](../../architecture/event-contracts.md). Декомпозиция — [tasks.md](tasks.md); общие HTTP/версии/сообщения и V0–V6 — [согласование контрактов](../../architecture/implementation-contracts.md).

## 1. Внутренняя архитектура

Results владеет A8/P7: переводом доверенного вердикта в баллы, текущим результатом, историей публикаций и таблицей. Не назначает жюри и не решает квоту/допуск апелляции. Это владелец бизнес-данных, а не восстанавливаемый из браузерного кэша экран.

Проекты в `src/backend/services/Results/`: `Results.Api` (ASP.NET Core .NET 10, публичные/private/internal HTTP и SSE, host workers), `Results.Application` (ApplyCheckCompleted, GetStandings, GetTeamResults, GetAppealBasis), `Results.Domain` (TeamTaskResult, правила применения/баллов), `Results.Infrastructure` (своя PostgreSQL, EF Core/SQL, broker, inbox/outbox). Domain не знает HTTP/Checking.Domain. Доставка CheckCompleted переводится в локальную команду Application через adapter.

Frontend feature `results` в общем React/TypeScript/Vite app: анонимная таблица всех команд, баллы по задачам/сумма/место, состояние соединения; личный экран результата с resultId/version для подачи апелляции. Клиент не рассчитывает авторитетные места/баллы, не выбирает новый expectedResultVersion для обхода конфликта. Кнопка апелляции вызывает Appeals, а не Results.

Предложение хранения read side: не поддерживать независимые асинхронные копии сумм/мест в MVP. Standings и team results читаются из опубликованных TeamTaskResult одной БД; сумма и dense rank вычисляются запросом. Это устраняет потерянные инкременты и устаревшие места. При неизвестном Q10 это проверяемая базовая модель; оптимизация только по измерениям с сохранением контракта снимка.

## 2. Сущности и состояния

| Сущность | Смысл |
| --- | --- |
| TeamTaskResult | Один результат на tournament/team/task, закреплённый solution/material; outcome, score/ruleVersion, resultVersion, sourceCheck/decisionOrigin, publicationId/publishedAt |
| ResultPublication | Неизменяемый опубликованный снимок конкретной resultVersion и её основание/авторство. Нет ручного состояния ожидания публикации |
| EvaluationReceipt | Один исход обработки evaluationId=checkId; Applied(changed true/false) либо постоянный Rejected, semantic hash и точное подтверждение. Не смешивать с техническим Deferred |
| ScoringRule / Roster fixture | Фиксированная до турнира стоимость задачи с версией, все команды/задачи для строк/колонок; источник MVP — согласованные fixtures |
| TournamentSnapshot | Монотонная snapshotVersion, изменяемая в commit видимого результата; версия всего согласованного чтения турнира, не сумма resultVersion |

| До | Вход | После |
| --- | --- | --- |
| Нет результата | Initial, expectedResultVersion=0, связи/правило валидны | Published v1, history/publication/Applied receipt |
| Published vN | Appeal с совпадающей базой, outcome меняется | Published vN+1 и новая публикация, заменён вклад задачи |
| Published vN | Appeal с той же базой, авторитетные поля без изменения | Тот же Published vN/publication/sourceCheck/publishedAt; отдельный Applied(changed=false) receipt |
| Любое | Повтор ранее обработанного checkId с тем же смыслом | Прежний receipt, без повторного эффекта/новой публикации |
| Published vN | Другая/устаревшая база или чужой материал | Результат без изменения, Rejected и наблюдаемый конфликт |
| Любое | Временно нет зависимости/инфраструктуры | Обработка Deferred/Retrying вне успешного receipt; результата «0 по умолчанию» нет |

Correct → полная фиксированная стоимость; Incorrect → 0. Нет результата — отдельная ячейка, а не Incorrect. Итог — сумма опубликованных вкладов, равенство сумм даёт одно место, нумерация 1,1,2. Вместе с планом принят вариант: пустая сумма=0 и отдельный pending status, не равный Incorrect. Q09 локального профиля уточнён пользователем при реализации: 10/20 целых баллов, строки JSON; [технический профиль T07](../../architecture/runtime-baseline.md). Если outcome меняется при одинаковом score, это всё равно новая бизнес-версия.

## 3. Схема хранения

Отдельная `results_db` с собственными credentials/volume. FK только локальные; чужие check/appeal IDs — корреляция, не FK.

| Таблица | Поля и ограничения |
| --- | --- |
| team_task_results | PK result_id; UNIQUE(tournament_id,team_id,task_id); solution/material IDs, outcome, точный score, rule_version, result_version, source_check_id, origin, assignment/reviewer/escalation lineage, current_publication_id/published_at |
| result_publications | PK publication_id; локальный FK result_id; UNIQUE(result_id,result_version); полный неизменяемый снимок, предыдущая версия, check/appeal IDs, время |
| evaluation_receipts | PK evaluation_id=checkId; hash семантики; Applied/Rejected, expected/actual/result versions, исход/баллы/changed, корреляция, точный outgoing receipt |
| scoring_rules | UNIQUE(tournament_id,task_id,rule_version); fixed points и checksum fixture; runtime API изменения отсутствует, несовпадение fixture выявляется |
| tournament_teams / tournament_tasks | Локальные справочные fixtures с PK и ключами турнира; только необходимые ID/публичные имена и порядок колонок |
| tournament_snapshots | PK tournament_id; snapshot_version, updated_at; строка блокировки порядка видимых commit турнира |
| inbox / outbox / processing_failures | Общее назначение из контракта; inbox UNIQUE(consumer,message_id); deferred/карантин с raw envelope и причиной |

Точные баллы — точный тип PostgreSQL, без float/double; для согласованного целочисленного профиля выбран numeric(28,0), баллы/версии передаются десятичными строками по [T07](../../architecture/runtime-baseline.md). Версии — bigint с безопасным сериализатором. Индексы: results(tournament_id,team_id), receipts по appealId/requestId, outbox(status,next_attempt). Вычисляемые totals/ranks не хранятся как обновляемые фронтендом строки.

## 4. REST API и онлайн-обновление

| Метод и путь | Доступ | Ответ |
| --- | --- | --- |
| GET `/api/v1/results/tournaments/{tournamentId}/standings` | Анонимный | 200: snapshotVersion, команды/задачи, ячейки со статусом и score?, total/rank; ETag снимка. Нет материала, reviewer, назначений, истории апелляции |
| GET `/api/v1/results/tournaments/{tournamentId}/standings/events` | Анонимный SSE | Только tournamentId/snapshotVersion и технический статус соединения; без частных данных |
| GET `/api/v1/results/me/tournaments/{tournamentId}/results` | Team subject в scope | 200: свои опубликованные результаты, resultId/resultVersion/publicationId, исход/баллы и минимальная подсказка доступности; окончательное право проверяет Appeals |
| GET `/internal/v1/results/{resultId}/appeal-basis?expectedResultVersion=…` | Только доверенный Appeals service | Канонические 200/404/409/503 и поля — [REST-стык](../../architecture/implementation-contracts.md#3-синхронный-стык-results--appeals) |

Нет POST начисления из браузера, ручной публикации, изменения стоимости, пересчёта по новому регламенту или управления турниром. Нет дополнительного публичного endpoint истории/авторства без решения Q16.

T04: сначала подписка SSE, затем GET; сервер после регистрации подписки читает/посылает текущую snapshotVersion. Клиент делает GET и сверяет, что его версия не меньше последней полученной; событие во время GET вызывает повтор, ответы с меньшей версией не заменяют новые. На reconnect всегда новый handshake/GET; сохранение полного event replay/Last-Event-ID не требуется. Дополнительная периодическая сверка страхует потерю live-сигнала и restart publisher. SSE — подсказка инвалидировать, не единственный носитель истины.

Snapshot GET читает roster/results/snapshotVersion в одной согласованной read-only транзакции (предложение: Repeatable Read), а не отдельными запросами из разных commit. При нескольких API replicas каждая активная SSE replica должна узнавать изменения: для MVP допускается наблюдение snapshotVersion своей БД с настраиваемой частотой, вместо общей competing consumer очереди уведомлений. Первый handshake и периодическая сверка закрывают окно пропуска. Измеряемый лаг/SLO/частота — Q10; нельзя объявить незаданный интервал гарантией.

## 5. Интеграционные события

| Направление | Контракт | Обработка |
| --- | --- | --- |
| Вход Checking → Results | CheckCompleted | Доверенный producer/schema; kind/outcome/origin, ключ/материал, lineage, база. Проверка баллов не приходит в payload |
| Выход Results → Appeals, kind=Appeal | ResultEvaluationApplied | Receipt после commit доступного Published результата; включает selected и actual reviewer/assignment, evaluationDecisionOrigin, escalationId при SeniorEscalation |
| Выход Results → Appeals, kind=Appeal | ResultEvaluationRejected | Постоянный коррелированный конфликт; не скрывает аномалию под успешной публикацией |

Для Initial receipts также сохраняются и наблюдаемы локально, обязательного другого consumer нет. E21/E22 — локальные события/обновление снимка, без дополнительного интеграционного события, дублирующего Applied. Входы RecheckRequested и факт конца турнира Results не нужны: позднее завершение разрешено и должно публиковаться.

`publicationId` во входном Appeal CheckCompleted обозначает **исходную** публикацию; в Applied — **итоговую**. При no-op итог совпадает с прежней публикацией; фактический автор оценки из receipt не меняет авторство опубликованного источника. Appeals закрывает по receipt, поэтому lineage, baseCheckId и selectedReviewerId нельзя терять даже при no-op и SeniorEscalation.

## 6. Транзакционные границы

1. Consumer проверяет envelope/schema/producer. В транзакции проверяет inbox и затем evaluation receipt **до CAS**. Повтор семантически того же checkId с новым messageId добавляет inbox-след и воспроизводит исход, не конфликтует с уже изменённой версией. Тот же checkId с другим смыслом — нарушение, не успех.
2. Для новой оценки — блокировка tournament_snapshots и целевого результата в одинаковом порядке. Для Initial отсутствие результата защищает UNIQUE ключ; для Appeal — CAS resultId/version плюс совпадение solution/material/baseCheck/publication. Не менять expectedResultVersion на текущую.
3. Рассчитать score по известной фиксированной rule. Commit изменённого TeamTaskResult, publication/history, snapshotVersion, receipt, inbox и outbox Applied — одна локальная транзакция. Сумма/места при GET вычисляются из этих же данных; отдельного ожидающего проектора нет.
4. No-op всё равно проверяет базу под блокировкой. В commit — receipt/inbox/outbox, без новой resultVersion/publication/time/sourceCheck/snapshotVersion. Конкретное опубликованное основание остаётся прежним. Потерянный ack не создаёт новую историю.
5. Постоянный конфликт — receipt Rejected + inbox + outbox/диагностика в короткой транзакции без изменения результата. Временно отсутствующий первичный результат/rule — отложенное сообщение, не окончательный receipt и не бесконечный горячий requeue; повтор после восстановления зависимости.

Сериализация изменений одного турнира через snapshot row — осознанное предложение для простого согласованного MVP. Она даёт точную версию снимка и исключает lost update, но ограничивает throughput; не переносится как обещание производительности. При deadlock/serialization failure весь переход повторяется с прежним evaluationId. Транзакций с HTTP, брокером или чужими БД нет.

## 7. Сбои и восстановление

- Broker/API restart, падение до/после commit/confirm/ack: outbox/inbox и receipts гарантируют повторяемый эффект при повторной доставке; exactly-once transport не предполагается.
- Нет rule/базы: сохранять наблюдаемую зависимость/Deferred; не начислять 0 и не подтверждать применение. Постоянное несоответствие — Rejected, Appeals → NeedsResolution; операционный redrive не меняет базовую версию.
- Подтверждение Applied отправляется после commit. Следующий GET видит опубликованную версию или новее при согласованном чтении primary; read replica с неопределённым lag не вводится в MVP. No-op ack не обещает новый publishedAt.
- SSE оборвался/уведомление пропущено: повторное подключение и GET/сверка snapshotVersion восстанавливают состояние. Ошибка приватного результата не открывает private DTO через публичный fallback.
- Метрики: input/outbox lag, Deferred/Rejected/quarantine, CAS conflicts, время snapshot query/transaction, reconnect/SSE lag. Логи связывают evaluationId/resultId/appealId/requestId/messageId; токен/материал не логировать. Backup/restore проверять согласованным набором собственных таблиц, не заменой fixtures поверх истории.

## 8. Стратегия тестирования

| Уровень | Проверка |
| --- | --- |
| Unit | Correct/Incorrect/no result, замена вклада, no-op, изменённый outcome при равном score, immutable cost, dense rank 1,1,2 и группы равных; RS-A01–03/06/07/18/20/24/25/31 |
| PostgreSQL integration | UNIQUE/CAS, receipt перед CAS, два Initial/две задачи одной команды конкурентно, snapshot read consistency, исходная история/no-op/source, неподходящий solution, rollback; RS-A04/05/08/09/13/14/15/17/22/23 |
| Broker/HTTP integration | Crash windows/дубли, неизвестное правило/зависимость и redrive, kind/origin/schema, internal basis auth/errors, ранний Applied для Appeals, публикация после конца; RS-A10/16/26–30 |
| Frontend integration/contract | SSE handshake/GET gap/reconnect/старая версия, никакой утечки через public DTO/SSE, стабильный контракт внутреннего основания и семи сообщений; RS-A11/12/19/21 |
| E2E | V1 анонимная таблица после вердикта, V4 изменение места нескольких команд и no-op закрытие, V6 потеря соединения/рестарт/late completion; все три сервиса и браузер |

Integration выполняются на PostgreSQL и настоящем broker из изолированного окружения, не in-memory ORM. Проверки отслеживают версии/IDs и eventual convergence, не fixed sleep. Query/performance smoke измеряет выбранную сериализацию, но нагрузочный бюджет требует Q10. Fixture v1 использует согласованные 10/20 целых баллов, pending без score и пустую сумму "0"; это локальный тестовый профиль.

## 9. Принятые решения и ограничения

Приняты SSE T04, общая snapshot lock/query read side и отображение pending/пустой суммы. Q09 для локальных fixtures уточнён явно (10/20 целых, строки JSON); версии T02/T03 закреплены в CHK-INF-01. Остаются Q10 — объём/лаг; Q13 — retention/эксплуатационные параметры. Публичные поля ограничиваются уже согласованной таблицей; Q16 не разрешает публиковать историю/авторов. Все проверки совместимости включены в [общую матрицу](../../architecture/implementation-contracts.md#5-проверка-согласованности).

Текущая реализация 24 октября: RES-BE-01–04 и RES-UT-01/02 завершены, см. журнал и [границы adapters](../../../src/backend/services/Results/Results.Infrastructure/README.md). Standings/private/internal HTTP реализованы; SQL rank case-наборы выполняются на настоящей PostgreSQL. Runtime consumer/outbox и SSE выполнены в RES-INT-02/RES-BE-05, Appeal application реализована в RES-BE-06.

24 октября: RES-INT-02, RES-BE-05, RES-FE-01/02 и RES-IT-01/03 завершены. Первичный вердикт доставляется в Results, публичные GET/SSE и UI восстановление версий работают. Fault gates RES-IT-02/04/05 и браузерные V1/V4/V6 завершены; статусы/доказательства — в журнале.
