#!/bin/sh
set -eu
db_password="$(cat /run/secrets/results_db_password)"
export Broker__Password="$(cat /run/secrets/results_broker_password)"
export ConnectionStrings__Results="Host=results-db;Database=results_db;Username=results_app;Password=$db_password;Timeout=2;Command Timeout=2"
# Compose file secrets may be root-only on Linux; keep host permissions intact.
install -d -m 700 -o 1654 -g 1654 /run/matigr-identity
install -m 600 -o 1654 -g 1654 /run/secrets/dev_issuer_public /run/matigr-identity/public.pem
install -m 600 -o 1654 -g 1654 /run/secrets/appeals_results_public /run/matigr-identity/appeals-results-public.pem
exec setpriv --reuid=1654 --regid=1654 --clear-groups --no-new-privs dotnet Results.Api.dll "$@"
