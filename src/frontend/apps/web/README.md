# Matigr Web — CHK-FE-01

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

Маршруты: `/results` (также переход с `/`), `/checking`, `/appeals`, отдельный not-found экран. Feature-модули подключаются в app/AppRoutes. Навигация имеет active state/aria-current, skip link, заголовок страницы и перенос фокуса. Разделы пока показывают явное состояние подготовки: здесь нет искусственных таблиц, результатов, назначений, форм загрузки или неработающих действий. Видимость ссылки не предоставляет прав; будущие операции всегда проверяются backend.

В CHK-FE-02 подключаются сессия/переключение субъектов после CHK-BE-04; проверка/очередь — CHK-FE-03, таблица — RES-FE-01, подача/рассмотрение — задачи Appeals. Эти функции не объявляются готовыми в CHK-FE-01.

На Windows перед `npm ci` / `scripts/check.ps1 -Restore` остановите запущенный Vite: native-модуль сборщика удерживается процессом и не может быть заменён. Обычные проверки без restore можно выполнять при работающем dev-сервере.

## Типы и HTTP

`src/shared/api/generated/checking.d.ts` создаётся из [Checking OpenAPI](../../../../contracts/http/checking/openapi.json) командой `npm run generate:api`. `npm run check:api` проверяет, что сохранённый результат генерации совпадает с контрактом; CI выполняет эту проверку до typecheck. Доменные модели backend во frontend не импортируются. Генератор и роутер закреплены в package.json, остальные версии соответствуют [базовой среде](../../../../docs/architecture/runtime-baseline.md).

HTTP-клиент работает с относительными путями выбранного API, принимает токен только на конкретный запрос, не хранит его в local/sessionStorage или cookie. Credentials cookies выключены, редиректы запрещены, cache=no-store. JSON response типизирован, но генерация TypeScript не заменяет runtime validation недоверенных данных. Материал как binary response будет подключён отдельным адаптером в CHK-FE-03.

Команда требует переданный вызывающим экраном Idempotency-Key; If-Match и ETag сохраняются строками без числового преобразования. Клиент не создаёт новые ключи, не повторяет запросы автоматически, не снимает назначение при отмене запроса и не считает потерянный ответ отказом сервера. Отмена fetch/body parsing сохраняется как AbortError. Технический stateless POST dev-issuer отделён от команды с receipt. Жизненный цикл сохранённой команды/повтора после возвращения относится к CHK-FE-04.

ProblemDetails сохраняет status/code/correlationId и известные поля, не raw body/detail/instance. Не-JSON и повреждённая ошибка не скрывают HTTP status. ApiProblemNotice выводит безопасное сообщение и корреляцию, без HTML/server detail и без автоматического повторения команды. Фронтенд не рассчитывает баллы, места или авторитетные права.

## Локальный proxy

Checking и `/dev/v1/session` направлены на существующий API `http://127.0.0.1:5101`. Для другого локального адреса задаётся `CHECKING_API_TARGET` в окружении процесса Vite (без префикса VITE_, в bundle он не попадает). Results/Appeals подключаются после появления их API; фиктивных серверов и proxy на придуманные endpoints нет. Private fixtures не импортируются в браузер. `server.fs` разрешает только app/node_modules и дополнительно запрещает `.local`, fixtures, env и ключи.

Компонентные/транспортные тесты используют jsdom и явно заданные ответы fetch. Это проверки shell/HTTP-поведения, не E2E бизнес-инвариантов или реальных PostgreSQL/брокера.
