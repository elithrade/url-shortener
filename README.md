# Shortener

A URL shortener built from scratch with ASP.NET Core Minimal APIs (.NET 10). It's a learning project for system design: collisions, atomicity, concurrency, expiry and scaling.

## Features (current)
- `POST /shorten` validates the URL and returns a random 7-character base62 code.
- URL validation (`UrlValidator`): absolute `http`/`https` only, at most 2048 characters, no embedded credentials, and no links back to the service's own host.
- `GET /{code}` redirects to the stored URL with `302 Found`, or returns `404` if the code is unknown.
- In-memory store (`Dictionary` guarded by a `Lock`) behind an `IUrlStore` interface.
- Bounded retry on code collisions: up to 5 attempts, then `503`.

## Run
```bash
dotnet run --project src/Shortener
```
The HTTP profile listens on `http://localhost:5154` (see `src/Shortener/Properties/launchSettings.json`).

## Tests
```bash
dotnet test
```
`UrlValidatorTests` covers rejected input (empty, malformed, bad schemes, credentials, own host, length limit), the canonical form of accepted URLs, and the `TryValidate` contract (exactly one of `url` or `error` is set). `ShortenEndpointTests` runs the real pipeline in memory with `WebApplicationFactory`: the round trip, error responses, canonical `Location` headers, and the `503` path using a fake store that always collides.

## Project layout
```
Shortener.slnx
src/Shortener/            the web app
tests/Shortener.Tests/    unit and endpoint tests
```

## API

### `POST /shorten`
Request body:
```json
{ "url": "https://example.com" }
```
Responses:
| Status | Meaning |
|---|---|
| `201 Created` | Link created. Body is `{ "code": "...", "shortUrl": "..." }`, and the `Location` header holds the short URL. |
| `400 Bad Request` | The URL failed validation. The body is a JSON string that says which rule failed. |
| `503 Service Unavailable` | Every code attempt collided. |

The stored URL is the canonical form (`Uri.AbsoluteUri`): the scheme and host are lowercased and default ports are dropped. Path, query and fragment keep their case.
`HTTPS://Example.COM/Path?x=1` is stored as `https://example.com/Path?x=1`, and `http://example.com:80/a` as `http://example.com/a`.

### `GET /{code}`
| Status | Meaning |
|---|---|
| `302 Found` | Redirects, with `Location` set to the original URL. |
| `404 Not Found` | Unknown code. |

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
```

### Collision test
Set `CODE_LENGTH = 1` in `src/Shortener/Program.cs` (62 possible codes) and restart, because the store is in memory and must start empty. Then run:

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

Set `CODE_LENGTH` back to `7` afterwards.

## Design decisions
- **Random codes** from `Random.Shared`, 7 characters of base62. The keyspace is 62⁷ ≈ 3.5 trillion. Collisions are rare, but the store still has to detect them.
- **Store arbitrates, caller retries.** `IUrlStore.TryAddAsync` is the only atomic check and insert. The endpoint generates the code and retries on `false`.
- **302, not 301.** Browsers don't cache a 302, so expiry, deletion and click counting will see every request.
- **Not idempotent.** Submitting the same URL twice gives two codes.
- **Validation is an allowlist.** Only absolute `http`/`https` URLs are accepted. A blocklist of bad schemes always misses one, and `Uri.TryCreate(..., UriKind.Absolute)` alone accepts `javascript:` and, on Linux, `/etc/passwd` as a `file:` URI.
- **Credentials in URLs are rejected.** `https://google.com@evil.com` actually goes to `evil.com`, which is a phishing trick, and credentials shouldn't end up in the database or logs.
- **Self-links are rejected** by comparing hosts, to avoid redirect chains and loops. The comparison uses the request's `Host` header, which the client controls, so a deployment should compare against a configured base URL instead.
- **Error messages describe the rule** the client must meet, and say nothing about the implementation.
- The store keeps the canonical URL (`AbsoluteUri`), not the raw input.

## Roadmap
1. ~~URL validation~~ (done)
2. Endpoint tests with `WebApplicationFactory`, and a GitHub Actions workflow that runs `dotnet test`.
3. Switch the code generator to `RandomNumberGenerator` (unbiased, unguessable).
4. Expiry: lazy check on read plus a background sweep, using `TimeProvider`.
5. Async click counting with `Channel<T>`.
6. Rate limiting, persistence and graceful shutdown.
7. Dockerfile, configuration through `IOptions<T>` and a health endpoint, then deployment.
8. Multi-instance design: shared store, ID allocation and caching.
9. A real concurrency test in xUnit against `InMemoryUrlStore`.

## Related
[collision-lab](https://github.com/elithrade/collision-lab) (a separate console project) holds the experiments that motivated these decisions: birthday-paradox collision counts by code length, and the `Dictionary` race with and without a lock.
