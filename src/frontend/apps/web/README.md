# Matigr Web

Актуализировано 26 октября 2026 года; все frontend-задачи первоначального MVP и браузерные V1–V6 завершены. [Доказательства и ограничения](../../../../docs/implementation/progress.md).

Единственный React/TypeScript/Vite app для Checking, Results и Appeals. npm workspace `@matigr/web`; общий lockfile находится в корне репозитория.

## Команды из корня

```sh
npm ci --ignore-scripts --no-audit --no-fund
npm run dev:web
npm run check:api
npm run typecheck:web
npm run test:web
npm run build:web
```

Dev-сервер слушает только `127.0.0.1:5173`. Production build создаёт `dist/` внутри app; сервер production-раздачи должен возвращать index.html для клиентских маршрутов. Vite dev/preview не является production hosting.

Маршруты: `/results` (также переход с `/`), `/checking`, `/appeals`, отдельный not-found экран. Feature-модули подключаются в app/AppRoutes. Навигация имеет active state/aria-current, skip link, заголовок страницы и перенос фокуса. Подключены реальные API: таблица/личные результаты, рабочее место жюри/старшего и подача/рассмотрение апелляций. Форм загрузки/приёма/QR нет. Видимость ссылки не предоставляет прав; операции всегда проверяются backend.

В CHK-FE-02 подключены сессия и переключение субъектов через реальный dev-issuer CHK-BE-04. Проверка/очередь реализованы CHK-FE-03/04, таблица/личные результаты — RES-FE-01–03, апелляционные экраны — APL-FE-01–04.

На Windows перед `npm ci` / `scripts/check.ps1 -Restore` остановите запущенный Vite: native-модуль сборщика удерживается процессом и не может быть заменён. Обычные проверки без restore можно выполнять при работающем dev-сервере.

## Типы и HTTP

`src/shared/api/generated/checking.d.ts`, `results.d.ts`, `appeals.d.ts` создаются из собственных [Checking](../../../../contracts/http/checking/openapi.json), [Results](../../../../contracts/http/results/openapi.json), [Appeals OpenAPI](../../../../contracts/http/appeals/openapi.json) командой `npm run generate:api`. `npm run check:api` проверяет, что сохранённый результат генерации совпадает с контрактом; CI выполняет эту проверку до typecheck. Доменные модели backend во frontend не импортируются. Генератор и роутер закреплены в package.json, остальные версии соответствуют [базовой среде](../../../../docs/architecture/runtime-baseline.md).

HTTP-клиент работает с относительными путями выбранного API, принимает токен только на конкретный запрос, не хранит его в local/sessionStorage или cookie. Credentials cookies выключены, редиректы запрещены, cache=no-store. JSON response типизирован, но генерация TypeScript не заменяет runtime validation недоверенных данных. Материал подключён отдельным защищённым binary-response adapter CHK-FE-03.

Команда требует переданный вызывающим экраном Idempotency-Key; If-Match и ETag сохраняются строками без числового преобразования. Клиент не создаёт новые ключи, не повторяет запросы автоматически, не снимает назначение при отмене запроса и не считает потерянный ответ отказом сервера. Отмена fetch/body parsing сохраняется как AbortError. Технический stateless POST dev-issuer отделён от команды с receipt. Жизненный цикл сохранённой команды/повтора после возвращения относится к CHK-FE-04.

ProblemDetails сохраняет status/code/correlationId и известные поля, не raw body/detail/instance. Не-JSON и повреждённая ошибка не скрывают HTTP status. ApiProblemNotice выводит безопасное сообщение и корреляцию, без HTML/server detail и без автоматического повторения команды. Фронтенд не рассчитывает баллы, места или авторитетные права.

## Локальный proxy

Checking и `/dev/v1/session` направлены на существующий API `http://127.0.0.1:5101`. Для другого локального адреса задаётся `CHECKING_API_TARGET` в окружении процесса Vite (без префикса VITE_, в bundle он не попадает). Results и Appeals проксируются на 5102/5103; настройки `RESULTS_API_TARGET`/`APPEALS_API_TARGET`, как и `CHECKING_API_TARGET`, серверные и не входят в browser bundle. Фиктивных серверов нет. Private fixtures не импортируются в браузер. `server.fs` разрешает только app/node_modules и дополнительно запрещает `.local`, fixtures, env и ключи.

Компонентные/транспортные тесты используют jsdom и явно заданные ответы fetch. Это проверки shell/HTTP-поведения, не E2E бизнес-инвариантов или реальных PostgreSQL/брокера.

## Сессия и граница частных данных

Dev-выбор включён только при `import.meta.env.DEV`. Production-сборка показывает гостя без переключателя; каталог fixture keys и запрос выдачи удаляются из bundle. API отдельно ограничивает выдачу окружениями Local/Test. В браузер включены только семь публичных ключей выбора и подписи вариантов, без импорта manifest, внутренних ID, ролей или scope из fixtures. `POST /dev/v1/session/select` передаёт один testSubjectId, без прежнего bearer, browser role/team или business receipt. [JWT/profile contract](../../../../contracts/authentication.md).

Адаптер `shared/auth/identity.ts` проверяет форму DevSession/срок, копирует только поля серверного профиля и не декодирует JWT для получения полномочий. Роль/имя на панели берутся из ответа; выбор «Старший жюри» сам не выдаёт роль. `IdentityProvider` и панель dev-выбора отделены от внутреннего профиля, чтобы позже заменить вход/регистрацию без смены subjectId и бизнес-правил.

`SessionController` хранит в памяти одну сессию. Публичный snapshot содержит профиль/статус, без токена. Перезагрузка начинает гостевую сессию; localStorage, sessionStorage, cookie и URL для удостоверения не используются. Истечение технического срока токена и текущий 401 очищают вход и предлагают явный повторный выбор. 403 остаётся отказом в конкретной операции. Скрытых refresh/retry и команд освобождения назначения нет; серверное назначение остаётся за прежним субъектом.

Каждый выбор, включая повторный выбор того же субъекта, **до ожидания HTTP** отменяет прежние запросы, уничтожает прежний `PrivateWorkspace`, очищает его query cache и отзывает material object URLs. Частные маршруты Checking/Appeals обёрнуты в `PrivateSessionBoundary` с новым revision: локальные формы/черновики/действия размонтируются. Неуспешное переключение не возвращает прежнюю личность. Поздняя выдача/ошибка старого выбора игнорируется; private HTTP response старой сессии отвергается даже при fetch, проигнорировавшем abort. Запоздалый 401 не снимает новый вход.

Будущие private features получают клиент через `session.workspace().client('/api/v1/checking')` (либо Results/Appeals): bearer добавляется только из действующего workspace, signal объединяется с отменой вызывающего кода, command key/If-Match сохраняются. `getCached`/`cache` и `materialUrl`/`releaseMaterial` принадлежат тому же workspace; после dispose запись/чтение/команда недоступны. Локальное состояние feature располагается под `PrivateSessionBoundary`. Для личных результатов внутри публичной страницы нужна отдельная такая граница. Публичные GET/SSE используют самостоятельный HTTP-клиент **без bearer** и не помещаются в частный cache; public route не размонтируется при переключении.

Эти механизмы проверены на component/controller harness с частным состоянием, материалом и отложенными HTTP-ответами. Назначения и материалы Checking подключены в CHK-FE-03; апелляции подключены APL-FE-01–04. В браузере проверены жюри → команда, переходы, reload → гость, узкий экран 320 px и production preview без dev-выбора. Подписка React на controller использует [useSyncExternalStore](https://react.dev/reference/react/useSyncExternalStore) со стабильным snapshot; StrictMode покрыт тестом.

## Рабочее место жюри — CHK-FE-03

CheckingPage читает глобальное назначение, очередь с cursor и материал только через PrivateWorkspace. Бинарный вердикт/передача используют серверный ETag и capability, новый key на бизнес-действие; двойной клик блокируется, потерянный ответ предлагает явный повтор прежнего запроса. SeniorAssigned не предлагает второй отказ. После commit показывается свобода жюри и ожидание Results, без локального начисления баллов. Текстовый материал отображается как экранированный текст, без HTML/публичного URL/токена в DOM. Смена сессии очищает состояние. Component/API tests проверяют эти сценарии; полный reconnect/многовкладочное восстановление реализованы и проверены в CHK-FE-04 и E2E.

## Живая таблица и восстановление Checking

RES-FE-01/02: ResultsPage берёт публичный tournamentId из URL либо VITE_TOURNAMENT_ID. Независимый клиент без bearer читает закрытый allowlist DTO и отображает server scores/totals/ranks без собственного scoring/ranking. «—» — pending без score; опубликованный Incorrect остаётся числом «0». Большие версии сравниваются BigInt. Подписка SSE открывается до GET, open/reconnect вызывает свежий GET, invalidation во время запроса вызывает reconciliation; меньшая версия не заменяет большую. Периодическая публичная сверка страхует пропуск сигнала, degraded state оставляет последнюю таблицу видимой. Это не нагрузочный SLO. У каждого controller есть отмена/generation, cleanup работает с StrictMode.

CHK-FE-04: focus/online/pageshow/видимая вкладка и BroadcastChannel invalidation вызывают глобальный GET назначения. В канал не входят token/capability/материал/ID работы. 409/412 перечитывают state без автоматического повторного вердикта; неоднозначный ответ сохраняет прежние key/payload/ETag для явного retry. Если актуальное назначение неизвестно из-за ошибки, UI не показывает проверяющего свободным. Поздние read/material ответы не возвращают завершённую работу. Сервер остаётся владельцем прав/занятости; автоматического освобождения/heartbeat нет.

Component/controller tests дополнены пройденными браузерными [V1](../../../../tests/e2e/v1.md)–[V6](../../../../tests/e2e/v6.md), без подмены PostgreSQL/broker. Локальный URL таблицы приведён в корневом README.
## Апелляции команды

APL-FE-01 получает resultId/expectedResultVersion из ссылки Results, загружает own list/details через private workspace и отправляет только эту пару с устойчивым Idempotency-Key. Неоднозначный ответ повторяется прежней командой. Existing resultId независимо от новой версии блокирует вторую форму; окончательная квота/окно/senior origin проверяются Appeals. Registered показывается как ожидание ручного выбора, без автоматического claim. При смене субъекта/маршрута отменяются запросы и pending actions; поздние ответы не восстанавливают частное состояние. Appeals DTO генерируются из собственного OpenAPI, Vite proxy направлен на отдельный API 5103.

APL-FE-02: Registered очередь жюри и явный claim. 202 означает только pending request; ссылка на Checking появляется после сверки собственного назначения/checkId/reviewer. Подтверждённый Busy возвращает тот же Appeal к ручному выбору, uncertain retry сохраняет key/If-Match. Другой subject не получает чужую ссылку.

APL-FE-03/04: защищённые детали перечитываются вручную, при focus/online и техническим polling; BigInt version не допускает отката старым ответом. Completed ждёт Results; Closed показывает finalPublicationId, NeedsResolution не rebases основание. SeniorQueued/SeniorAssigned отображаются через private reviewState без нового Registered/старого назначения. Смена subject отменяет reads/actions/cache.

Production output, отдельно размещённый в Sites, не подключён к backend: [статус публикации](../../../../docs/implementation/sites-hosting.md). Тестовый dev-issuer не является production-входом. Последний общий frontend набор — 111 tests; результаты приёмки исторические, новые изменения проверяются заново.
