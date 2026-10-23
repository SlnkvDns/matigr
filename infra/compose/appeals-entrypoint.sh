#!/bin/sh
set -eu
db_password="$(cat /run/secrets/appeals_db_password)"
export ConnectionStrings__Appeals="Host=appeals-db;Database=appeals_db;Username=appeals_app;Password=$db_password;Timeout=2;Command Timeout=2"
exec setpriv --reuid=1654 --regid=1654 --clear-groups --no-new-privs dotnet Appeals.Api.dll "$@"
