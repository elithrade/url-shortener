# Shortener

[![CI](https://github.com/elithrade/url-shortener/actions/workflows/ci.yml/badge.svg)](https://github.com/elithrade/url-shortener/actions/workflows/ci.yml)

A URL shortener built from scratch with ASP.NET Core Minimal APIs (.NET 10). It's a learning project for system design: collisions, atomicity, concurrency, expiry and scaling. See `PLAN.md` for the roadmap and current milestone, and `LESSONS.md` for self-test flashcards built from the bugs and experiments in this repo.

## Features (current)
- `POST /shorten` validates the URL and returns a random 7-character base62 code, generated with `RandomNumberGenerator` (unbiased and unguessable).
- URL validation (`UrlValidator`): absolute `http`/`https` only, at most 2048 characters, no embedded credentials, and no links back to the service's own host.
- Expiry: every link expires. The client may send `expiresInSeconds` (60 to 31,536,000), and the default is 30 days. An expired link behaves exactly like an unknown one (`404`).
- `GET /{code}` redirects to the stored URL with `302 Found`, or returns `404` if the code is unknown or expired.
- In-memory store (`Dictionary` guarded by a `Lock`) behind an `IUrlStore` interface. Entries are `LinkEntry(Url, ExpiresAt)`, and the expiry is checked on every read against an injected `TimeProvider`.
- Bounded retry on code collisions: up to 5 attempts, then `503`.
- `InMemoryUrlStore` also implements `IExpirySweeper.Sweep()`, which removes expired entries and is tested. `ExpirySweepService` (a `BackgroundService` with a `PeriodicTimer`) calls it every 60 seconds, so expired entries are freed shortly after they expire (they are never served in the meantime, because reads check the expiry).

## Run
```bash
dotnet run --project src/Shortener
```
The HTTP profile listens on `http://localhost:5154` (see `src/Shortener/Properties/launchSettings.json`).

## Tests
```bash
dotnet test
```
- `UrlValidatorTests`: rejected input (empty, malformed, bad schemes, credentials, own host, length limit), the canonical form of accepted URLs, and the `TryValidate` contract (exactly one of `url` or `error` is set).
- `CodeGeneratorTests`: length, alphabet, distinctness, edge lengths, and a chi-squared uniformity test that fails against the biased `byte % 62` generator.
- `ExpiryPolicyTests`: the default, the minimum and maximum, and out-of-range values.
- `InMemoryStoreTests`: concurrency (same key gives exactly one winner, and many distinct keys are all stored), expiry on read (alive 1 ms before, gone exactly at expiry, never-expiring entries), and the sweep (removes only expired entries, a second sweep removes nothing, a swept code can be added again). Time is controlled with `FakeTimeProvider`, so nothing sleeps.
- `ExpirySweepServiceTests`: the background service with a fake sweeper and a `FakeTimeProvider`: it sweeps once per interval and not before, keeps running after a sweep throws, and stops cleanly (nothing sweeps after stop).
- `ShortenEndpointTests`: the real pipeline in memory with `WebApplicationFactory`: the round trip, error responses, canonical `Location` headers, the retry limit and `503` path using a fake store (`CollidingStore`), 200 concurrent POSTs, and expiry through the endpoint with a fake clock.

Most tests were mutation-checked: the code was deliberately broken in a scratch copy (a removed lock, `>=` changed to `>`, an off-by-one retry, a biased generator, a wrong sweep interval) to confirm that a test fails.

## CI
`.github/workflows/ci.yml` builds in Release and runs `dotnet test` on every push to `main` and on pull requests. The `main` branch has a ruleset that requires the `build` check and blocks force-pushes and deletion. The repository admin can bypass it, so for the owner the gate is advisory.

## Project layout
```
Shortener.slnx
src/Shortener/            the web app: Program.cs, plus folders Api/, Expiry/, Storage/ (interfaces, InMemory/, and Postgres/ to come) and Urls/
tests/Shortener.Tests/    unit and endpoint tests, in folders that mirror src
.editorconfig             4-space indent (2 for project, yml and json files)
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
| `503 Service Unavailable` | Every code attempt collided. |

The stored URL is the canonical form (`Uri.AbsoluteUri`): the scheme and host are lowercased and default ports are dropped. Path, query and fragment keep their case.
`HTTPS://Example.COM/Path?x=1` is stored as `https://example.com/Path?x=1`, and `http://example.com:80/a` as `http://example.com/a`.

### `GET /{code}`
| Status | Meaning |
|---|---|
| `302 Found` | Redirects, with `Location` set to the original URL. |
| `404 Not Found` | Unknown or expired code. The two cases are deliberately indistinguishable. |

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
- **Reads never delete.** An expired entry keeps occupying its code until a sweep removes it, so a code isn't reissued while stale links may still circulate. After a sweep the code can be reused. That is accepted for the in-memory store. At large scale the persistent store should use native TTL plus a never-reissue policy (see `PLAN.md`).
- **Time is injected** (`TimeProvider`, registered as `TimeProvider.System`), so tests advance a `FakeTimeProvider` instead of sleeping.
- **`IExpirySweeper` is a separate interface** implemented by the in-memory store today. A Postgres store will implement it too (Postgres has no native TTL), while a store with native TTL, such as Redis, would not need it. The in-memory store is registered once and both interfaces forward to that single instance.
- **The sweep runs in a background service.** The host calls `ExecuteAsync` once, so the service owns the loop. A failed sweep is caught and logged inside the loop and the next tick still runs, and only a cancellation from shutdown ends the loop. The sweep is an O(n) scan that holds the store's lock, so concurrent reads wait for it (measured: about 11 ms per million entries when nothing expires, and about 46 ms when everything does).

## Roadmap
The detailed plan, with the current milestone and status, is in `PLAN.md`. In short:
1. ~~URL validation~~, ~~endpoint and concurrency tests~~, ~~CI~~, ~~cryptographic codes~~ (done)
2. Expiry: done (model, policy, read-time check, the measured store sweep, and the background service that runs it).
3. Persistence behind `IUrlStore` (a key-value store with native TTL is the likely fit).
4. Rate limiting, configuration through `IOptions<T>`, a health endpoint, structured logging, graceful shutdown.
5. Dockerfile, deployment and a post-deploy smoke test.
6. Async click counting with `Channel<T>`, caching, multi-instance design (shared store and ID allocation), load testing and failure injection.
7. A UI.

## Related
[collision-lab](https://github.com/elithrade/collision-lab) (a separate console project) holds the experiments that motivated these decisions: birthday-paradox collision counts by code length, the `Dictionary` race with and without a lock, and a comparison of random generators (`dotnet run -c Release -- generators`: fairness, speed and predictability of `Random`, `GetInt32`, biased bytes and `GetString`).
