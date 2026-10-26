# Публикация интерфейса в Sites

26 октября 2026 пользователь поручил хостинг через Sites и отдельно выбрал **только интерфейс**, без backend. Статус основных задач MVP остаётся 82/82; это отдельная операция публикации.

- URL: [Matigr — интерфейс](https://matigr-interface-20261026.grassycorgi8.chatgpt.site).
- Доступ: приватный, только владельцу; аудитория не расширялась.
- Site ID: `appgprj_6adefb6ed120819182f538696610eae7`.
- Version: `appgprj_6adefb6ed120819182f538696610eae7~appgver_cdeb434be35c81919355677962347de6`.
- Deployment: `appgdep_6adefc039cdc8191a516736f6c3138bf`, native status `succeeded`.
- Исходный Matigr commit: `f52a58961091f3f1e11f861abd47ba7b658806df`.
- Отдельный Sites source commit: `53de45cb7b39626d3dda5f2488a9a3772db988ac`.

Production-сборка выполнена `npm run build:web`: TypeScript и Vite успешны. В браузере проверен production-экран `/results`, без dev-переключения пользователей. Отдельный source checkout: `sites/matigr-interface-20261026`; его `.openai/hosting.json` сохраняет Site ID. В него скопированы только dist/index.html и assets, добавлены index-копии для прямого открытия `/results`, `/checking`, `/appeals`. Полученный archive передан Sites без изменения после source commit/push. Токен доступа использован только в памяти/stdin helper, не сохранён в source/логах.

Backend Checking/Results/Appeals, PostgreSQL, RabbitMQ, fixtures, материалы решений, локальные ключи и секреты не публиковались. API-backed таблица, личные назначения и подача апелляций **не работают без отдельно размещённого backend**; имитация данных не добавлена. Production-сборка также не содержит dev-issuer UI. Прежние локальные данные/volumes сохранены.

Для обновления использовать тот же Site ID и собственный Sites source helper/credential. Не создавать второй Site. Сборка и публикация Sites выполняются в отдельном репозитории; исходный Matigr remote не изменялся и не получал push. Для подключения backend потребуется отдельная задача по HTTPS API gateway, серверной аутентификации и настройке same-origin API маршрутов; Sites Workers не запускают существующий Docker/.NET/PostgreSQL/RabbitMQ стек.
