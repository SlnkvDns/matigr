#!/bin/sh
set -eu
db_password="$(cat /run/secrets/appeals_db_password)"
export Broker__Password="$(cat /run/secrets/appeals_broker_password)"
export ConnectionStrings__Appeals="Host=appeals-db;Database=appeals_db;Username=appeals_app;Password=$db_password;Timeout=2;Command Timeout=2"
install -d -m 700 -o 1654 -g 1654 /run/matigr-service
install -m 600 -o 1654 -g 1654 /run/secrets/appeals_results_private /run/matigr-service/private.pem
install -m 600 -o 1654 -g 1654 /run/secrets/dev_issuer_public /run/matigr-service/user-public.pem
exec setpriv --reuid=1654 --regid=1654 --clear-groups --no-new-privs dotnet Appeals.Api.dll "$@"
