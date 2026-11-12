#!/usr/bin/env bash
# Starts the app for a load test: Release build, Production, rate limit out of the way,
# Postgres and Redis from loadtest/compose.ports.yaml. Request logs go to a file, so the
# terminal doesn't slow the app down.
#
#   POSTGRES_PASSWORD=<same as compose> loadtest/run-app.sh            # with the Redis cache
#   POSTGRES_PASSWORD=<same as compose> REDIS=off loadtest/run-app.sh  # Postgres only
#
# First run against a new database: add `migrate` as the argument to apply the schema and exit.
set -euo pipefail
cd "$(dirname "$0")/.."

export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS=http://localhost:5154
export Shortener__BaseUrl=https://localhost:5154 # Production requires https; only shortUrl uses it
export Storage=Postgres
export ConnectionStrings__Shortener="Host=localhost;Database=shortener;Username=postgres;Password=${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD}"
export RateLimit__Capacity=1000000
export RateLimit__TokensPerPeriod=1000000
if [ "${REDIS:-on}" = "on" ]; then
  export ConnectionStrings__Redis=localhost:6379
fi

log=${LOG:-/tmp/shortener-load.log}
echo "app log: $log (Redis ${REDIS:-on})"
exec dotnet run -c Release --no-launch-profile --project src/Shortener -- "$@" >"$log" 2>&1
