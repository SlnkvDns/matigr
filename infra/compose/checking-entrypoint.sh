#!/bin/sh
set -eu
db_password="$(cat /run/secrets/checking_db_password)"
export ConnectionStrings__Checking="Host=checking-db;Database=checking_db;Username=checking_app;Password=$db_password;Timeout=2;Command Timeout=2"
export Broker__Password="$(cat /run/secrets/checking_broker_password)"
exec setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups --no-new-privs dotnet Checking.Api.dll
