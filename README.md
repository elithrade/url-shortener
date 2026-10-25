# Shortener

[![CI](https://github.com/elithrade/url-shortener/actions/workflows/ci.yml/badge.svg)](https://github.com/elithrade/url-shortener/actions/workflows/ci.yml)

A URL shortener built from scratch with ASP.NET Core Minimal APIs (.NET 10). It's a learning project for system design: collisions, atomicity, concurrency, expiry and scaling. See `PLAN.md` for the roadmap and current milestone, and `LESSONS.md` for self-test flashcards built from the bugs and experiments in this repo.

## Features (current)
- `POST /shorten` validates the URL and returns a random 7-character base62 code, generated with `RandomNumberGenerator` (unbiased and unguessable).
- URL validation (`UrlValidator`): absolute `http`/`https` only, at most 2048 characters, no embedded credentials, and no links back to the service's own host.
- Expiry: every link expires. The client may send `expiresInSeconds` (60 to 31,536,000), and the default is 30 days. An expired link behaves exactly like an unknown one (`404`).
- `GET /{code}` redirects to the stored URL with `302 Found`, or returns `404` if the code is unknown or expired.
- Two stores behind the `IUrlStore` interface, chosen by the `Storage` setting: `InMemory` (the default; a `Dictionary` guarded by a `Lock`) and `Postgres` (EF Core with Npgsql, where uniqueness comes from the primary key on the code and links are kept in the database). Entries are `LinkEntry(Url, ExpiresAt)`, and the expiry is checked on every read against an injected `TimeProvider`.
- Bounded retry on code collisions: up to 5 attempts, then `503`.
- A storage outage returns `503` (not retried, and with no details in the response). Other unexpected errors stay `500`.
- Both stores implement `IExpirySweeper.SweepAsync()`, which removes expired entries (the Postgres store with one `DELETE`) and is tested. `ExpirySweepService` (a `BackgroundService` with a `PeriodicTimer`) calls it every 60 seconds, so expired entries are freed shortly after they expire (they are never served in the meantime, because reads check the expiry).

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
`migrate` applies the EF Core migrations (`src/Shortener/Storage/Postgres/Migrations`) and exits without starting the web server. It exits `0` on success and `1` on failure (an unreachable database, or `Storage` not set to `Postgres`), and running it again is harmless. It does not create the database itself. Only one process should run it, which is why it is a separate step and not part of app startup (two `CREATE TABLE`s at once can race; measured in `PLAN.md`). The container above has no volume, so its data is lost when it stops.

### With Docker Compose
`compose.yaml` runs Postgres (with a `pgdata` volume), the one-shot `migrate` service, and the app, in that order. Set `POSTGRES_PASSWORD` first (in the shell, or in an untracked `.env` file next to `compose.yaml`; any value without `;` or `=` works locally):
```bash
echo POSTGRES_PASSWORD=dev-only-pw-123 > .env
docker compose up -d --wait
curl -i -X POST http://localhost:8080/shorten -H "Content-Type: application/json" -d '{"url":"https://example.com"}'
docker compose down        # keeps the volume, so links survive; add -v to delete the data
```
The password is only read when Postgres first creates its data directory, so changing it later needs `docker compose down -v`. The app listens on `8080` in the container.

To add a migration after changing the model: `dotnet tool restore`, then `dotnet tool run dotnet-ef migrations add <Name> --project src/Shortener --output-dir Storage/Postgres/Migrations`, and change the generated namespace to `Shortener`.

## Tests
```bash
dotnet test
```
- `UrlValidatorTests`: rejected input (empty, malformed, bad schemes, credentials, own host, length limit), the canonical form of accepted URLs, and the `TryValidate` contract (exactly one of `url` or `error` is set).
- `CodeGeneratorTests`: length, alphabet, distinctness, edge lengths, and a chi-squared uniformity test that fails against the biased `byte % 62` generator.
- `ExpiryPolicyTests`: the default, the minimum and maximum, and out-of-range values.
- `InMemoryStoreTests`: concurrency against the real in-memory store (same key gives exactly one winner, and many distinct keys are all stored).
- `UrlStoreContractTests` and `ExpirySweeperContractTests`: abstract classes that hold the shared store behavior (atomic add, expiry on read, alive 1 ms before and gone exactly at expiry, a past `ExpiresAt` is rejected, case-sensitive codes, and the sweep). Each has an in-memory and a Postgres subclass, so both stores prove the same behavior. Time is controlled with `FakeTimeProvider`, so nothing sleeps.
- The Postgres tests use Testcontainers, so **Docker must be running**, and one container is shared by all of them. Besides the contract subclasses: `PostgresRegistrationTests` (both interfaces resolve to one store, the hosted sweeper is registered, and a round trip works on a database that the test migrated first, since the app does not create the schema), `PostgresMigrationTests` (`SchemaMigrator` on an empty database creates the table and records `InitialCreate` in the migration history, running it twice keeps the data, and an unreachable database returns `1` without throwing), `PostgresUnavailableTests` (an unreachable database becomes `StorageUnavailableException`, with no container needed) and `PostgresMisconfiguredTests` (a database that does not exist is not an outage, so it is not translated).
- `ExpirySweepServiceTests`: the background service with a fake sweeper and a `FakeTimeProvider`: it sweeps once per interval and not before, keeps running after a sweep throws, and stops cleanly (nothing sweeps after stop).
- `ShortenEndpointTests`: the real pipeline in memory with `WebApplicationFactory`: the round trip, error responses, canonical `Location` headers, the retry limit and `503` path using a fake store (`CollidingStore`), 200 concurrent POSTs, expiry through the endpoint with a fake clock, and a storage outage (`503`, not retried, nothing leaked) versus other store exceptions (`500`).

Most tests were mutation-checked: the code was deliberately broken in a scratch copy (a removed lock, `>=` changed to `>`, an off-by-one retry, a biased generator, a wrong sweep interval) to confirm that a test fails.

## CI
`.github/workflows/ci.yml` builds in Release and runs `dotnet test` on every push to `main` and on pull requests. The `main` branch has a ruleset that requires the `build` check and blocks force-pushes and deletion. The repository admin can bypass it, so for the owner the gate is advisory.

## Project layout
```
Shortener.slnx
src/Shortener/            the web app: Program.cs, plus folders Api/, Expiry/, Storage/ (interfaces, `StorageUnavailableException`, InMemory/ and Postgres/, which holds `SchemaMigrator` and the EF migrations) and Urls/
tests/Shortener.Tests/    unit and endpoint tests, in folders that mirror src
.editorconfig             4-space indent (2 for project, yml and json files)
Dockerfile, compose.yaml  image for the app, and a local stack (Postgres, migrate step, app)
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
| `503 Service Unavailable` | Every code attempt collided, or the storage is unavailable. |

The stored URL is the canonical form (`Uri.AbsoluteUri`): the scheme and host are lowercased and default ports are dropped. Path, query and fragment keep their case.
`HTTPS://Example.COM/Path?x=1` is stored as `https://example.com/Path?x=1`, and `http://example.com:80/a` as `http://example.com/a`.

### `GET /{code}`
| Status | Meaning |
|---|---|
| `302 Found` | Redirects, with `Location` set to the original URL. |
| `404 Not Found` | Unknown or expired code. The two cases are deliberately indistinguishable. |
| `503 Service Unavailable` | The storage is unavailable. |

## Manual testing with curl
`-i` shows the status line and headers. Leave `-L` off, so you see the 302 itself and not the destination page.

```bash
BASE=http://localhost:5154

# 1. Create a link: expect 201, with code and shortUrl in the body
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com"}'

# 2. Follow the code from step 1: expect 302 and Location: https://example.com
curl -i $BASE/<code>

# 3. Unknown code: expect 404
curl -i $BASE/nope

# 4. Missing url: expect 400 "Url is required"
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{}'

# 5. Whitespace url: expect 400 "Url is required"
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"   "}'

# 6. Invalid urls: expect 400 with a message for each rule
for u in 'foo' 'javascript:alert(1)' 'file:///etc/passwd' '/etc/passwd' 'http://' \
         'https://google.com@evil.com' 'http://localhost:5154/abc'; do
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
Expected: 62 × `201` and 38 × `503`, with the number of successes never above 62. A `503` means five attempts in a row collided, not necessarily that the keyspace is full. Failures become frequent as the table fills.

Set `CodeLength` back to `7` afterwards.

## Design decisions
- **Random codes** from `RandomNumberGenerator.GetString`, 7 characters of base62. The keyspace is 62⁷ ≈ 3.5 trillion. `Random` is statistically fair but predictable (a seeded `Random` repeats exactly, and its state can be recovered from a few outputs), so it must not be used for codes. Collisions are rare, but the store still has to detect them.
- **Store arbitrates, caller retries.** `IUrlStore.TryAddAsync` is the only atomic check and insert. The endpoint generates the code and retries on `false`.
- **302, not 301.** Browsers don't cache a 302, so expiry, deletion and click counting see every request.
- **Not idempotent.** Submitting the same URL twice gives two codes.
- **Validation is an allowlist.** Only absolute `http`/`https` URLs are accepted. A blocklist of bad schemes always misses one, and `Uri.TryCreate(..., UriKind.Absolute)` alone accepts `javascript:` and, on Linux, `/etc/passwd` as a `file:` URI.
- **Credentials in URLs are rejected.** `https://google.com@evil.com` actually goes to `evil.com`, which is a phishing trick, and credentials shouldn't end up in the database or logs.
- **Self-links are rejected** by comparing hosts, to avoid redirect chains and loops. The comparison uses the request's `Host` header, which the client controls, so a deployment should compare against a configured base URL instead.
- **Error messages describe the rule** the client must meet, and say nothing about the implementation.
- The store keeps the canonical URL (`AbsoluteUri`) in a `LinkEntry`, not the raw input, and `GetAsync` returns only the URL, so the expiry decision stays inside the store.
- **Expiry is an absolute time, computed on the server from a duration.** The client sends seconds, not a timestamp, so a wrong client clock or a missing time zone can't matter. Times are `DateTimeOffset` in UTC. The default is 30 days, and the range is 60 seconds to 365 days, so every link has a bounded lifetime.
- **Expired means missing.** The check runs on every read (`now >= ExpiresAt`), so a link never redirects after its expiry, even before any cleanup. It returns `404`, the same as an unknown code, so a caller can't tell whether a code ever existed.
- **Reads never delete.** An expired entry keeps occupying its code until a sweep removes it, so a code isn't reissued while stale links may still circulate. After a sweep the code can be reused. That is accepted for now. At large scale the persistent store needs a never-reissue policy (Postgres has no native TTL, so the sweeper's `DELETE` is what frees a code; see `PLAN.md`).
- **A storage outage is a 503, translated by the store.** Each store turns its own provider errors into `StorageUnavailableException` (for Postgres, a transient `NpgsqlException` found in the inner chain, because EF wraps it in an `InvalidOperationException`), and the endpoints catch only that type, so the endpoint code does not depend on a database. An outage is not retried (the retry loop is for collisions), the response has no body, and other exceptions stay `500`.
- **Time is injected** (`TimeProvider`, registered as `TimeProvider.System`), so tests advance a `FakeTimeProvider` instead of sleeping.
- **`IExpirySweeper` is a separate interface** implemented by both stores (Postgres has no native TTL, so its sweeper deletes the expired rows), while a store with native TTL, such as Redis, would not need it. Each store is registered once and both interfaces forward to that single instance.
- **The sweep runs in a background service.** The host calls `ExecuteAsync` once, so the service owns the loop. A failed sweep is caught and logged inside the loop and the next tick still runs, and only a cancellation from shutdown ends the loop. The sweep is an O(n) scan that holds the store's lock, so concurrent reads wait for it (measured: about 11 ms per million entries when nothing expires, and about 46 ms when everything does).

## Roadmap
The detailed plan, with the current milestone and status, is in `PLAN.md`. In short:
1. ~~URL validation~~, ~~endpoint and concurrency tests~~, ~~CI~~, ~~cryptographic codes~~ (done)
2. Expiry: done (model, policy, read-time check, the measured store sweep, and the background service that runs it).
3. Persistence behind `IUrlStore`: done for Postgres (the store, shared contract tests, registration, `503` on an outage, EF migrations with a one-shot `migrate` step, and Docker Compose with a data volume, checked by restarting the stack). Redis is planned later as a cache in front of it.
4. Rate limiting, configuration through `IOptions<T>`, a health endpoint, structured logging, graceful shutdown.
5. Deployment (the Dockerfile exists; CI image build and push, a host, and a post-deploy smoke test are left).
6. Async click counting with `Channel<T>`, caching, multi-instance design (shared store and ID allocation), load testing and failure injection.
7. A UI.

## Related
[collision-lab](https://github.com/elithrade/collision-lab) (a separate console project) holds the experiments that motivated these decisions: birthday-paradox collision counts by code length, the `Dictionary` race with and without a lock, and a comparison of random generators (`dotnet run -c Release -- generators`: fairness, speed and predictability of `Random`, `GetInt32`, biased bytes and `GetString`).
