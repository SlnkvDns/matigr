#!/bin/sh
set -eu
db_password="$(cat /run/secrets/checking_db_password)"
export ConnectionStrings__Checking="Host=checking-db;Database=checking_db;Username=checking_app;Password=$db_password;Timeout=2;Command Timeout=2"
export Broker__Password="$(cat /run/secrets/checking_broker_password)"
# Compose file secrets can be root-only on Linux. Copy into the container runtime
# directory with app ownership; never loosen permissions on the host key files.
install -d -m 700 -o "$APP_UID" -g "$APP_UID" /run/matigr-identity
install -m 600 -o "$APP_UID" -g "$APP_UID" /run/secrets/dev_issuer_public /run/matigr-identity/public.pem
install -m 600 -o "$APP_UID" -g "$APP_UID" /run/secrets/dev_issuer_private /run/matigr-identity/private.pem
exec setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups --no-new-privs dotnet Checking.Api.dll
