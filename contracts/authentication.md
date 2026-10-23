# Проверка пользовательского удостоверения — CHK-BE-04

Реализовано 23 октября 2026 года в Checking по D23/T06. Это общий wire-контракт доверия для будущих адаптеров Results/Appeals, без общей доменной библиотеки или отдельного Identity-сервиса. Их runtime-проверки появятся в собственных задачах; текущие тесты проверяют независимый verification host с одним публичным ключом.

## Выдача и профиль

`POST /dev/v1/session/select` принимает строго `{ "testSubjectId": "jury-a" }` по [Checking OpenAPI](http/checking/openapi.json). Только окружения `Local` и `Test` регистрируют маршрут, directory и issuer. В `Development`, `Staging`, `Production` маршрут отсутствует (404), приватный ключ/manifest для выдачи не читаются. Неизвестный fixture key — `404 DevSubjectNotFound`, неверная форма, повтор ключа JSON или дополнительные поля — `400 InvalidRequest`.

Сервер читает subjectId, displayName, roles, tournamentIds и необязательный teamId из проверенного по SHA-256 [manifest v1](../infra/fixtures/manifest.json). Клиент не задаёт роль, команду или scope. Ответ DevSession содержит подписанный accessToken, tokenType=Bearer, expiresAt и разрешённый профиль. teamId присутствует только у Team. Пароли/регистрация/обновление аккаунтов не реализуются. При повторном выборе/restart subjectId и ключ остаются прежними; jti и срок нового удостоверения меняются.

## Доверенный JWT

| Поле | Значение / проверка |
| --- | --- |
| Header alg / typ | Только `RS256` / `at+jwt`, RSA не слабее 3072 бит |
| iss | Точное настроенное `Identity:Issuer`; локально `matigr-local` |
| aud | Настроенное `Identity:Audience`; локально `matigr-user-api`, без эквивалентности завершающего `/` |
| exp / nbf / iat | UTC NumericDate; exp обязателен, срок/nbf проверяются с ClockSkew=0. Время не управляет назначением или концом турнира |
| sub | Единственный непустой стабильный внутренний subjectId, не fixture key и не имя пользователя |
| name | Отображаемое имя; не ключ и не полномочие |
| role | Массив уникальных серверных ролей Jury, Senior, Team, Organizer |
| tournament_id | Массив уникальных разрешённых ID турниров; пустой scope не даёт доступа к турниру |
| team_id | Единственная непустая привязка для Team, отсутствует у остальных |
| token_use | Ровно `user`; сервисное удостоверение не принимается пользовательской схемой |
| jti | Новый технический ID удостоверения; не ID бизнес-команды/назначения |

Проверяющий использует доверенный публичный ключ из конфигурации, не ключ/URL из заголовка JWT. Закрытый ключ в `Identity:PublicKeyPath` отвергается. Проверяются подпись, issuer, audience, lifetime, тип и форма пользовательских claims; неверный токен → `401 Unauthorized` с `WWW-Authenticate: Bearer`. Подписанное удостоверение не заменяет проверку доступа к объекту.

В Checking `AuthenticatedSubject` изолирует внутренние ID/права от JWT/dev-выбора. `MapCheckingApi()` защищает будущие handlers политикой CheckingReviewer: Jury либо Senior. Senior — уполномоченный член жюри; старшая очередь дополнительно требует Senior. `CheckingReviewScope` проверяет роль и конкретный турнир через ресурс ReviewScope. Команды получают actor из `HttpContext.CurrentSubject()`. Заголовки браузера role/team/user и поля body не подменяют этот контекст.

Материал, завершение/отказ и capability потребуют дополнительной проверки актуального владельца/назначения из собственной БД в CHK-BE-05/06. Одна роль и даже валидный capability без принадлежности не дают права. Занятость не хранится в JWT. Эти handlers ещё отсутствуют; тестовые authorization probes существуют только в тестовом host, в API не опубликованы.

Будущие Results/Appeals проверяют тот же пользовательский контракт своими адаптерами и открытым ключом; их объектные права остаются собственными. Публичная таблица будет явно анонимной. Сервисный REST basis использует отдельное удостоверение/аудиторию и не авторизуется пользовательским JWT. Подключение IdP позже заменяет dev-выдачу и адаптер нормализации: внешний `(issuer, sub)` сопоставляется прежнему внутреннему subjectId без переписывания истории.

## Настройка и локальная проверка

Все host требуют `Identity:Issuer`, `Identity:Audience`, `Identity:PublicKeyPath`. В Local/Test дополнительно обязательны `Fixtures:Directory`, `DevIdentity:PrivateKeyPath`, положительный `DevIdentity:LifetimeSeconds`. Настройки передаются обычной ASP.NET configuration (`__` в environment). Без обязательных файлов/настроек запуск завершается ошибкой; пара ключей не генерируется во время выдачи. Manifest/key pair сверяются при запуске.

Compose задаёт техническое время токена **3600 секунд** как изменяемую настройку локальной среды; это не бизнес-срок и не TTL назначения. Ключи создаёт `scripts/prepare-fixtures.mjs` в игнорируемой `.local/secrets`. Только Checking получает private key; копии внутри контейнера принадлежат UID 1654 с mode 600, host permissions не ослабляются. Остальные сервисы позже получат только public key. Ключи не публикуются HTTP и не входят в образ.

```sh
node scripts/init-local.mjs
node scripts/prepare-fixtures.mjs
docker compose -f infra/compose/compose.yaml up -d --build --wait --wait-timeout 180
node scripts/check-infrastructure.mjs
node scripts/check-identity.mjs --restart
```

Последняя команда проверяет семь живых профилей по OpenAPI, подпись через Node crypto и один public key, отклонение browser authority, затем перезапускает только Checking и проверяет прежний ключ/subject. Токены остаются в памяти скрипта и не выводятся. Backend integration tests проверяют также 401/403, scope, все не-local окружения и ключи. `Cache-Control: private, no-store` установлен для частных ответов и ошибок; ProblemDetails содержит code/correlationId без лишнего traceId. В CHK-FE-02 реализованы [переключатель, профиль и хранение токена только в памяти](../src/frontend/apps/web/README.md#сессия-и-граница-частных-данных), очистка частного состояния и защита от поздних ответов.

Пакеты: JwtBearer 10.0.13, JsonWebTokens 8.19.2 (совместимость проверена по nuspec JwtBearer и locked restore). [Официальная настройка JWT bearer в ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0).
