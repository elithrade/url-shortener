# Shortener

[![CI](https://github.com/elithrade/url-shortener/actions/workflows/ci.yml/badge.svg)](https://github.com/elithrade/url-shortener/actions/workflows/ci.yml)

A URL shortener built from scratch with ASP.NET Core Minimal APIs (.NET 10). It's a learning project for system design: collisions, atomicity, concurrency, expiry and scaling. See `PLAN.md` for the roadmap and current milestone, and `LESSONS.md` for self-test flashcards built from the bugs and experiments in this repo.

## Features
**Endpoints**
- `POST /shorten` validates a URL and returns a random 7-character base62 code.
- `GET /{code}` redirects with `302 Found`, or returns `404` for an unknown or expired code.
- `GET /{code}/stats` returns a link's click count over a date range.
- `GET /health/live` and `GET /health/ready` for probes (see Operations below).

**Validation and abuse protection**
- `UrlValidator` accepts absolute `http`/`https` URLs of at most 2048 characters, with no embedded credentials and no links back to this service.
- The service's own address comes from the `Shortener:BaseUrl` setting, never from the request's `Host` header, which the client controls. It is used for the self-link check and to build `shortUrl`. The app refuses to start unless the value is absolute, `https` (`http` is allowed in Development only) and has no path, query, fragment or user info. Development uses `http://localhost:5154` from `appsettings.Development.json`.
- `POST /shorten` is rate limited per client IP with a token bucket: a burst of 5, then 1 request per second. Over the limit it returns `429` with `Retry-After`. `GET /{code}` has no limit.

**Expiry**
- Every link expires. The client may send `expiresInSeconds` (60 to 31,536,000); the default is 30 days.
- An expired link returns `404`, the same as an unknown code.
- A background service deletes expired entries every 60 seconds. Reads check the expiry themselves, so an expired link is never served while it waits for the sweep.

**Storage**
- The `Storage` setting picks the store behind `IUrlStore`: `InMemory` (the default, a `Dictionary` behind a `Lock`) or `Postgres` (EF Core with Npgsql; the primary key on the code enforces uniqueness).
- When two codes collide, the endpoint retries up to 5 times, then returns `503`.
- An optional Redis cache (`CachedUrlStore`) sits in front of the store when `ConnectionStrings:Redis` is set. A redirect reads Redis first and fills it on a miss. A cached link lives for `Cache:TtlSeconds` (1 hour) or its remaining lifetime, whichever is shorter. If Redis is down or slower than `Cache:TimeoutMilliseconds` (200 ms), the read counts as a miss and goes to the store. Watch hits, misses and errors with `dotnet-counters monitor -n Shortener --counters Shortener.Cache`.

**Click counting**
- Redirects put clicks on a bounded in-memory queue, and a background service writes them in batches. The count can lag by one flush interval (5 s by default).
- When the queue is full, new clicks are dropped so the redirect never waits.

**Operations**
- A storage outage returns `503` with an empty body and is not retried. Other unexpected errors return `500`.
- Timeouts shrink from the outside in, so each layer gives up before its caller: database connect 3 s and command 3 s, host shutdown 12 s, container grace period 15 s. A request against a hung database gets `503`. Migrations get a 5-minute command timeout.
- On SIGTERM the app stops accepting connections, finishes requests in progress and exits `0`.
- Logs are one JSON object per line outside Development, and readable text under `dotnet run`. Each entry carries the request's `TraceId`. Every request gets one line with method, path, status and duration. Query strings, headers and bodies are never logged, because short URLs can carry tokens. Health probes are not logged. EF Core command logs are `Information` in Development and `Warning` otherwise.
- `GET /health/live` runs no checks. `GET /health/ready` checks that Postgres accepts a connection within 2 seconds (it is always healthy with the in-memory store). Both return `200` or `503` and are not rate limited.

## Run
```bash
dotnet run --project src/Shortener
```
The HTTP profile listens on `http://localhost:5154` (see `src/Shortener/Properties/launchSettings.json`).

To use Postgres instead of the in-memory store, set `Storage` to `Postgres` and provide a connection string (never commit one). For example, with a throwaway container:
```bash
docker run -d --rm --name pg -e POSTGRES_PASSWORD=dev -p 5432:5432 postgres:17
Storage=Postgres ConnectionStrings__Shortener="Host=localhost;Username=postgres;Password=dev;Database=postgres" \
  dotnet run --project src/Shortener
```
The app does not create its own schema. Run the one-shot migration step first, against an existing (empty) database:
```bash
Storage=Postgres ConnectionStrings__Shortener="Host=localhost;Username=postgres;Password=dev;Database=postgres" \
  dotnet run --project src/Shortener -- migrate
```
`migrate` applies the EF Core migrations in `src/Shortener/Storage/Postgres/Migrations` and exits without starting the web server:
- It exits `0` on success and `1` on failure (an unreachable database, or `Storage` not set to `Postgres`).
- Running it twice is safe.
- It does not create the database.

It is a separate step, not part of app startup, because only one process should run it: two `CREATE TABLE`s at once can race (measured in `PLAN.md`).

The container above has no volume, so its data is lost when it stops.

### With Docker Compose
`compose.yaml` runs Postgres (with a `pgdata` volume), Redis as a cache (100 MB, `allkeys-lru`, no persistence), the one-shot `migrate` service, and the app, in that order. Set `POSTGRES_PASSWORD` first (in the shell, or in an untracked `.env` file next to `compose.yaml`; any value without `;` or `=` works locally):
```bash
echo POSTGRES_PASSWORD=dev-only-pw-123 > .env
docker compose up -d --wait
curl -i -X POST http://localhost:8080/shorten -H "Content-Type: application/json" -d '{"url":"https://example.com"}'
docker compose down        # keeps the volume, so links survive; add -v to delete the data
```
Things to know:
- Postgres reads the password only when it first creates its data directory. To change it later, run `docker compose down -v`, which deletes the data.
- The app listens on port `8080`. It runs as Development with `Shortener__BaseUrl=http://localhost:8080`, because only Development accepts an `http` base URL.
- `appsettings*.json` is baked into the image, so after changing it run `docker compose up -d --build`. A change to `environment:` in `compose.yaml` only needs `docker compose up -d`.

To add a migration after changing the model: `dotnet tool restore`, then `dotnet tool run dotnet-ef migrations add <Name> --project src/Shortener --output-dir Storage/Postgres/Migrations`, and change the generated namespace to `Shortener`.

## Tests
```bash
dotnet test
```
- `UrlValidatorTests`: rejected input (empty, malformed, bad schemes, credentials, own host, length limit), the canonical form of accepted URLs, and the `TryValidate` contract (exactly one of `url` or `error` is set).
- `CodeGeneratorTests`: length, alphabet, distinctness, edge lengths, and a chi-squared uniformity test that fails against the biased `byte % 62` generator.
- `ExpiryPolicyTests`: the default, the minimum and maximum, and out-of-range values.
- `TokenBucketLimiterTests`: the token bucket limiter with a fake clock: a burst of capacity then a rejection, the exact `RetryAfter` after a partial refill (to the tick, so a 0.5 s wait is not rounded to 1 s), a clock stepped backwards does not drain the bucket, waiting exactly `RetryAfter` allows exactly one more request, refill capped at capacity, independent keys, and the options guards.
- `RateLimitKeyTests`: the client key from an IP address: no address gives the shared fallback key, an IPv4-mapped IPv6 address matches its IPv4 form, IPv6 addresses in the same /64 share a key and different /64s don't (including a pair that differs only in the last byte of the prefix).
- `InMemoryStoreTests`: concurrency against the real in-memory store (same key gives exactly one winner, and many distinct keys are all stored).
- `UrlStoreContractTests` and `ExpirySweeperContractTests`: abstract classes that hold the shared store behavior (atomic add, expiry on read, alive 1 ms before and gone exactly at expiry, a past `ExpiresAt` is rejected, case-sensitive codes, and the sweep). Each has an in-memory and a Postgres subclass, so both stores prove the same behavior. Time is controlled with `FakeTimeProvider`, so nothing sleeps.
- The Postgres tests use Testcontainers, so **Docker must be running**, and one container is shared by all of them. Besides the contract subclasses: `PostgresRegistrationTests` (both interfaces resolve to one store, the hosted sweeper is registered, and a round trip works on a database that the test migrated first, since the app does not create the schema), `PostgresMigrationTests` (`SchemaMigrator` on an empty database creates the table and records `InitialCreate` in the migration history, running it twice keeps the data, and an unreachable database returns `1` without throwing), `PostgresUnavailableTests` (an unreachable database becomes `StorageUnavailableException`, with no container needed) and `PostgresMisconfiguredTests` (a database that does not exist is not an outage, so it is not translated).
- The Redis tests also use Testcontainers (one shared `redis:7` container, a key prefix per test): the store contract run against the cache decorator, `CachedUrlStoreTests` (a miss fills the cache, a hit skips the store, the key's TTL, expiry on a hit by the app's clock, unusable cached values, Redis down, and the read counter) and `CacheEndpointTests` (the app with the cache switched on).
- `ExpirySweepServiceTests`: the background service with a fake sweeper and a `FakeTimeProvider`: it sweeps once per interval and not before, keeps running after a sweep throws, and stops cleanly (nothing sweeps after stop).
- `ShortenEndpointTests`: the real pipeline in memory with `WebApplicationFactory`: the round trip, error responses, canonical `Location` headers, the retry limit and `503` path using a fake store (`CollidingStore`), 200 concurrent POSTs, expiry through the endpoint with a fake clock, and a storage outage (`503`, not retried, nothing leaked) versus other store exceptions (`500`).
- `RateLimitEndpointTests`: rate limiting through the real pipeline, with a small limit (`StrictRateLimitFactory`). `TestServer` leaves the client address `null`, so a test-only `IStartupFilter` (`RemoteIpStartupFilter`) sets it from an `X-Test-Client-Ip` header. Requests over the limit get `429` with `Retry-After: 3600`, one client over the limit doesn't limit another, clients without an address share one limit, and redirects are not limited. `ShortenEndpointTests` uses a generous limit (`GenerousRateLimitFactory`).
- Click counting: `ClickRecorderTests` (the bounded queue and its drop count), `ClickFlushServiceTests` (batching, flush on tick or at the batch cap, drain on stop), `ClickStoreContractTests` (in-memory and Postgres), `ClicksEndpointTests` and `StatsEndpointTests`.
- Startup and operations: `ShortenerOptionsValidatorTests` and `BaseUrlStartupTests` (a bad `BaseUrl` stops startup), `HealthEndpointTests` and `PostgresHealthEndpointTests`, `ShortenerLoggingTests` (no query strings or bodies in logs, no health lines) and `ForwardedHeadersTests`.

Most tests were mutation-checked: in a scratch copy, the code was broken on purpose (a removed lock, `>=` changed to `>`, an off-by-one retry, a biased generator, a wrong sweep interval) to confirm that a test fails.

## Load tests
[k6](https://github.com/grafana/k6/releases) sends redirects at a fixed rate and reports latency percentiles, and `dotnet-counters` shows the app's counters while it runs. The app runs on the host so `dotnet-counters` can attach to it. Postgres and Redis run in Compose under a separate project name, so the dev stack's database is never touched. Compare numbers between runs only: k6, the app and Docker share one machine.

Install once: the k6 binary on your `PATH`, and `dotnet tool install --global dotnet-counters`.

```bash
# 1. Postgres and Redis on localhost (any password; it is fixed when the volume is first created)
export POSTGRES_PASSWORD=loadtest
docker compose -p shortener-load -f compose.yaml -f loadtest/compose.ports.yaml up -d --wait postgres redis
loadtest/run-app.sh migrate          # first run only: applies the schema and exits

# 2. The app: Release, Production, rate limit raised, logs in /tmp/shortener-load.log
loadtest/run-app.sh                  # with the Redis cache
REDIS=off loadtest/run-app.sh        # Postgres only, as a baseline

# 3. In a second terminal: live counters (q quits)
dotnet-counters monitor -n Shortener --counters Shortener.Cache,System.Runtime,Microsoft.AspNetCore.Hosting

# 4. In a third terminal: the load
k6 run loadtest/redirect.js
k6 run -e RATE=500 -e DURATION=60s -e HOT_SHARE=0.8 loadtest/redirect.js

# 5. Afterwards: stop and keep the data, or remove everything including the test database
docker compose -p shortener-load -f compose.yaml -f loadtest/compose.ports.yaml stop
docker compose -p shortener-load -f compose.yaml -f loadtest/compose.ports.yaml down -v
```

`loadtest/redirect.js` creates `LINKS` links in `setup()`, then calls `GET /{code}` `RATE` times a second for `DURATION` (k6's `constant-arrival-rate`, so a slow app shows up as higher latency, not fewer requests). `HOT_SHARE` sends that share of requests to the hottest 1% of codes; the default 0 picks codes uniformly. The settings and their defaults are listed at the top of the script.

Reading the k6 summary:
- Use the `{ scenario:redirects }` lines. The unfiltered ones also include the setup's POSTs.
- `p(99)` is the time 99% of redirects beat. A flat `p(50)` with a climbing `p(99)` means a queue is forming, so the app is near its limit.
- The thresholds (`http_req_failed` under 1%, `p(99)` under `P99_MS`, default 200 ms) mark the run ✓ or ✗, and k6 exits with 99 when one fails.
- The cache hit rate is `hit / (hit + miss)` from `shortener.cache.reads`. With uniform picks it mostly reflects how many requests each code got, so compare runs with the same `LINKS`, `RATE`, `DURATION` and `HOT_SHARE`.
- Lost clicks are not exposed as a counter: send a known number of redirects to one code, wait past `Clicks:FlushIntervalSeconds` (5 s), and compare with `GET /{code}/stats`.

## CI
`.github/workflows/ci.yml` builds in Release and runs `dotnet test` on every push to `main` and on pull requests. A ruleset on `main` requires the `build` check and blocks force-pushes and deletion. The repository admin can bypass it, so the owner can still push directly.

## Project layout
```
Shortener.slnx
src/Shortener/            the web app: Program.cs, plus folders Api/, Caching/ (the Redis cache decorator and its connection settings), Clicks/ (the click recorder, its channel and the flush service), Expiry/, Proxy/ (trusted proxy options for forwarded headers), RateLimiting/ (the token bucket limiter on `POST /shorten`), Storage/ (interfaces, `StorageUnavailableException`, InMemory/ and Postgres/, which holds `SchemaMigrator` and the EF migrations) and Urls/
tests/Shortener.Tests/    unit and endpoint tests, in folders that mirror src
.editorconfig             4-space indent (2 for project, yml and json files)
Dockerfile, compose.yaml  image for the app, and a local stack (Postgres, Redis, migrate step, app)
infra/                    Azure stage 1: Bicep templates and the deploy steps
loadtest/                 the k6 redirect test, a script that starts the app for it, and a Compose override that publishes Postgres and Redis
.github/workflows/        CI, the deploy, the nightly Postgres stop and a manual Postgres delete
dotnet-tools.json         the local `dotnet-ef` tool
PLAN.md                   roadmap and current milestone
LESSONS.md                self-test flashcards
AGENTS.md                 instructions for AI coding agents (CLAUDE.md points to it)
```

## API

### `POST /shorten`
Request body:
```json
{ "url": "https://example.com", "expiresInSeconds": 3600 }
```
`expiresInSeconds` is optional. When omitted, the link expires in 30 days.

Responses:
| Status | Meaning |
|---|---|
| `201 Created` | Link created. Body is `{ "code": "...", "shortUrl": "..." }`, and the `Location` header holds the short URL. |
| `400 Bad Request` | The URL failed validation, or `expiresInSeconds` is outside 60 to 31,536,000. The body is a JSON string that says which rule failed. |
| `429 Too Many Requests` | This client sent too many requests. The `Retry-After` header says how many seconds to wait. |
| `503 Service Unavailable` | Every code attempt collided, or the storage is unavailable. |

The stored URL is the canonical form (`Uri.AbsoluteUri`): the scheme and host are lowercased and default ports are dropped. Path, query and fragment keep their case.
`HTTPS://Example.COM/Path?x=1` is stored as `https://example.com/Path?x=1`, and `http://example.com:80/a` as `http://example.com/a`.

### `GET /{code}`
| Status | Meaning |
|---|---|
| `302 Found` | Redirects, with `Location` set to the original URL. |
| `404 Not Found` | Unknown or expired code. The two cases are deliberately indistinguishable. |
| `503 Service Unavailable` | The storage is unavailable. |

### `GET /{code}/stats?start=&end=`
Dates are `yyyy-MM-dd` in UTC, both ends inclusive. `end` defaults to today and `start` to 29 days before `end`. The range is at most one year.
```json
{ "code": "Cy8Zv0I", "start": "2026-10-01", "end": "2026-10-04", "clicks": 5 }
```
| Status | Meaning |
|---|---|
| `200 OK` | The count for the range (`0` for an unknown code). |
| `400 Bad Request` | A date is not `yyyy-MM-dd`, `start` is after `end`, or the range is longer than one year. |
| `503 Service Unavailable` | The storage is unavailable. |

## Manual testing with curl
`-i` shows the status line and headers. Leave `-L` off, so you see the 302 itself and not the destination page.

```bash
BASE=http://localhost:5154    # dotnet run (launchSettings.json)
# BASE=http://localhost:8080  # docker compose: the app listens on 8080 in its container, published as 8080:8080

# 1. Create a link: expect 201, with code and shortUrl in the body
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com"}'

# 2. Follow the code from step 1: expect 302 and Location: https://example.com
curl -i $BASE/<code>

# 3. Unknown code: expect 404
curl -i $BASE/nope

# 4. Missing URL: expect 400 "URL is required"
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{}'

# 5. Whitespace URL: expect 400 "URL is required"
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"   "}'

# 6. Invalid URLs: expect 400 with a message for each rule
for u in 'foo' 'javascript:alert(1)' 'file:///etc/passwd' '/etc/passwd' 'http://' \
         'https://google.com@evil.com' "$BASE/abc"; do
  curl -s -w " -> %{http_code}\n" -X POST $BASE/shorten \
    -H "Content-Type: application/json" \
    -d "{\"url\":\"$u\"}"
done

# 7. Canonical form: create, then GET the code and check Location
#    HTTPS://Example.COM/Path?x=1  ->  Location: https://example.com/Path?x=1
#    http://example.com:80/a       ->  Location: http://example.com/a
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"HTTPS://Example.COM/Path?x=1"}'

# 8. Expiry: a 60 second link, then GET it, wait over a minute, and GET it again (404)
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com","expiresInSeconds":60}'

# 9. Out-of-range expiry: expect 400 with the allowed range in the message
for s in -5 0 59 31536001; do
  curl -s -w " -> %{http_code}\n" -X POST $BASE/shorten \
    -H "Content-Type: application/json" \
    -d "{\"url\":\"https://example.com\",\"expiresInSeconds\":$s}"
done

# 10. Forged Host header: shortUrl still starts with $BASE (from Shortener:BaseUrl),
#     and a link to this service is still rejected with 400
curl -i -X POST $BASE/shorten -H "Host: evil.example" \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/forged"}'
curl -s -w " -> %{http_code}\n" -X POST $BASE/shorten -H "Host: evil.example" \
  -H "Content-Type: application/json" \
  -d "{\"url\":\"$BASE/abc\"}"

# 11. Click stats: create a link, follow it three times, then read the count
CODE=$(curl -s -X POST $BASE/shorten -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/stats-demo"}' | sed -E 's/.*"code":"([^"]+)".*/\1/')
for i in 1 2 3; do curl -s -o /dev/null $BASE/$CODE; done
curl -s $BASE/$CODE/stats   # straight away: clicks may still be 0 (not flushed yet)
sleep 6                     # wait one flush interval (Clicks:FlushIntervalSeconds, 5 s)
curl -s $BASE/$CODE/stats   # last 30 days: {"code":"...","start":"...","end":"<today>","clicks":3}
TODAY=$(date -u +%F)
curl -s "$BASE/$CODE/stats?start=$TODAY&end=$TODAY"            # today only (UTC): clicks 3
curl -s -w " -> %{http_code}\n" "$BASE/$CODE/stats?start=2026-10-05&end=2026-10-01"
#   "Start must not be after end" -> 400
curl -s -w " -> %{http_code}\n" "$BASE/$CODE/stats?start=2025-01-01&end=2026-10-01"
#   "The range must be at most one year" -> 400
curl -s -w " -> %{http_code}\n" "$BASE/$CODE/stats?start=yesterday"
#   "start and end must be dates in the format yyyy-MM-dd" -> 400
```

### Collision test
Set `CodeLength` to `1` in `src/Shortener/Program.cs` (62 possible codes) and restart, because the store is in memory and must start empty. Then run:

```bash
# Sequential: prints one status per request, so you can see when 503s start
for i in $(seq 1 100); do
  curl -s -o /dev/null -w "%{http_code} " -X POST $BASE/shorten \
    -H "Content-Type: application/json" \
    -d "{\"url\":\"https://example.com/$i\"}"
done; echo

# Parallel: the 201 count must never exceed 62
for i in $(seq 1 100); do
  curl -s -o /dev/null -w "%{http_code}\n" -X POST $BASE/shorten \
    -H "Content-Type: application/json" \
    -d "{\"url\":\"https://example.com/$i\"}" &
done | sort | uniq -c; wait
```
Expected: 62 × `201` and 38 × `503`, with never more than 62 successes. A `503` means five attempts in a row collided, which can happen before the keyspace is full. Failures get more frequent as the table fills.

Set `CodeLength` back to `7` afterwards.

## Design decisions
- **Random codes** come from `RandomNumberGenerator.GetString`: 7 base62 characters, so 62⁷ ≈ 3.5 trillion possible codes. `Random` is statistically fair but predictable (a seeded `Random` repeats exactly, and its state can be recovered from a few outputs), so codes never use it. Collisions are rare, but the store still has to detect them.
- **The store arbitrates and the caller retries.** `IUrlStore.TryAddAsync` is the only atomic check-and-insert. The endpoint generates a code and retries when it returns `false`.
- **302 instead of 301.** Browsers don't cache a 302, so expiry, deletion and click counting see every request.
- **Shortening is not idempotent.** Submitting the same URL twice gives two codes.
- **Validation is an allowlist.** Only absolute `http`/`https` URLs pass. A blocklist of bad schemes always misses one, and `Uri.TryCreate(..., UriKind.Absolute)` on its own accepts `javascript:` and, on Linux, `/etc/passwd` as a `file:` URI.
- **Credentials in URLs are rejected.** `https://google.com@evil.com` goes to `evil.com`, a phishing trick, and credentials shouldn't end up in the database or logs.
- **Self-links are rejected** by comparing the URL's host with the host of `Shortener:BaseUrl`, to avoid redirect chains and loops. The request's `Host` header is not used, because the client controls it.
- **Error messages describe the rule** the client must meet, and say nothing about the implementation.
- The store keeps the canonical URL (`AbsoluteUri`) in a `LinkEntry`, not the raw input, and `GetAsync` returns only the URL, so the expiry decision stays inside the store.
- **Expiry is an absolute time, computed on the server from a duration.** The client sends seconds, not a timestamp, so a wrong client clock or a missing time zone can't matter. Times are `DateTimeOffset` in UTC. The default is 30 days, and the range is 60 seconds to 365 days, so every link has a bounded lifetime.
- **Expired means missing.** The check runs on every read (`now >= ExpiresAt`), so a link never redirects after its expiry, even before any cleanup. It returns `404`, the same as an unknown code, so a caller can't tell whether a code ever existed.
- **Reads never delete.** An expired entry keeps occupying its code until a sweep removes it, so a code isn't reissued while stale links may still circulate. After a sweep the code can be reused. That is accepted for now. At large scale the persistent store needs a never-reissue policy (Postgres has no native TTL, so the sweeper's `DELETE` is what frees a code; see `PLAN.md`).
- **A storage outage is a 503, translated by the store.** Each store turns its own provider errors into `StorageUnavailableException` (for Postgres, a transient `NpgsqlException` found in the inner chain, because EF wraps it in an `InvalidOperationException`), and the endpoints catch only that type, so the endpoint code does not depend on a database. An outage is not retried (the retry loop is for collisions), the response has no body, and other exceptions stay `500`.
- **Rate limiting is a token bucket per client, on writes only.**
  - A bucket holds up to `RateLimit:Capacity` tokens (default 5) and refills `RateLimit:TokensPerPeriod` tokens every `RateLimit:PeriodSeconds` seconds (default 1 per second). The defaults are in `appsettings.json`; override them with environment variables (`RateLimit__Capacity=10`). A missing setting stops startup.
  - Only `POST /shorten` is limited, through an `IEndpointFilter`. Abuse comes in through writes, and on reads many real users can share one IP.
  - The key is the client IP. IPv6 addresses are reduced to their /64, because one user usually controls a whole /64, and IPv4-mapped addresses are normalized to IPv4.
  - The limiter knows nothing about HTTP and reports the wait to the tick. The filter rounds it up to whole seconds for `Retry-After`. If the clock is corrected backwards, the limiter counts it as no time passed.
- **Behind a proxy, every request comes from the proxy's IP**, so all clients would share one limit. The `ForwardedHeaders` settings (`TrustedProxyOptions`, off by default) read the client IP from `X-Forwarded-For`, but only from a trusted proxy. Trusting that header from anyone would let a client pick its own rate-limit key. A request with no address at all (for example over a Unix socket) uses one shared fallback limit and logs a warning.
- **Time is injected** (`TimeProvider`, registered as `TimeProvider.System`), so tests advance a `FakeTimeProvider` instead of sleeping.
- **`IExpirySweeper` is a separate interface** implemented by both stores (Postgres has no native TTL, so its sweeper deletes the expired rows), while a store with native TTL, such as Redis, would not need it. Each store is registered once and both interfaces forward to that single instance.
- **The sweep runs in a background service.** The host calls `ExecuteAsync` once, so the service owns the loop. A failed sweep is caught and logged inside the loop, and the next tick still runs. Only shutdown ends the loop. In memory, the sweep is an O(n) scan that holds the store's lock, so concurrent reads wait for it: measured at about 11 ms per million entries when nothing expires, and about 46 ms when everything does.

## Roadmap
The detailed plan, with the current milestone and status, is in `PLAN.md`. In short:
1. ~~URL validation~~, ~~endpoint and concurrency tests~~, ~~CI~~, ~~cryptographic codes~~ (done)
2. Expiry: done (model, policy, read-time check, the measured store sweep, and the background service that runs it).
3. Persistence behind `IUrlStore`: done for Postgres (the store, shared contract tests, registration, `503` on an outage, EF migrations with a one-shot `migrate` step, and Docker Compose with a data volume, checked by restarting the stack). A Redis cache sits in front of it (item 6).
4. Abuse protection: done (rate limiting on `POST /shorten`, and a configured `BaseUrl` validated at startup). Config and ops: done (settings through `IOptions<T>`, health endpoints, structured JSON logging, graceful shutdown with a deadline budget).
5. Deployment, stage 1: done (Azure Container Apps and Postgres from Bicep, a GitHub Actions deploy with OIDC that migrates, deploys and smoke-tests `/health/ready`).
6. System design practice: click analytics done (`Channel<T>` off the redirect path, batched writes, `GET /{code}/stats`). A Redis read cache is built (cache-aside, safe when Redis is down). The k6 load test is built (`loadtest/`), but the cache hit rate and redirect latency with click recording are not measured yet. After that: multi-instance design, failure injection and a design write-up.
7. A UI.

## Related
[collision-lab](https://github.com/elithrade/collision-lab) (a separate console project) holds the experiments that motivated these decisions: birthday-paradox collision counts by code length, the `Dictionary` race with and without a lock, and a comparison of random generators (`dotnet run -c Release -- generators`: fairness, speed and predictability of `Random`, `GetInt32`, biased bytes and `GetString`).
