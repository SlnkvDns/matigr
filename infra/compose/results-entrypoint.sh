#!/bin/sh
set -eu
db_password="$(cat /run/secrets/results_db_password)"
export Broker__Password="$(cat /run/secrets/results_broker_password)"
export ConnectionStrings__Results="Host=results-db;Database=results_db;Username=results_app;Password=$db_password;Timeout=2;Command Timeout=2"
exec setpriv --reuid=1654 --regid=1654 --clear-groups --no-new-privs dotnet Results.Api.dll "$@"
