# Shortener

A URL shortener built from scratch with ASP.NET Core Minimal APIs (.NET 10). It's a learning project for system design: collisions, atomicity, concurrency, expiry and scaling.

## Features (current)
- `POST /shorten` takes a long URL and returns a random 7-character base62 code.
- `GET /{code}` redirects to the original URL with `302 Found`, or returns `404` if the code is unknown.
- In-memory store (`Dictionary` guarded by a `Lock`) behind an `IUrlStore` interface.
- Bounded retry on code collisions: up to 5 attempts, then `503`.

## Run
```bash
dotnet run
```
The HTTP profile listens on `http://localhost:5154` (see `Properties/launchSettings.json`).

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
| `400 Bad Request` | `url` missing or empty. |
| `503 Service Unavailable` | Every code attempt collided. |

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

# 4. Missing url: expect 400
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{}'

# 5. Whitespace url: expect 400
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"   "}'

# 6. Invalid url: currently returns 201 (validation is not implemented yet)
curl -i -X POST $BASE/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"foo"}'
```

### Collision test
Set `CODE_LENGTH = 1` in `Program.cs` (62 possible codes) and restart, because the store is in memory and must start empty. Then run:

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
- The store keeps the original URL string as submitted.

## Roadmap
1. URL validation: `http` and `https` allowlist, length cap, reject self-links and user info.
2. Switch the code generator to `RandomNumberGenerator` (unbiased, unguessable).
3. Expiry: lazy check on read plus a background sweep, using `TimeProvider`.
4. Async click counting with `Channel<T>`.
5. Rate limiting, persistence and graceful shutdown.
6. Multi-instance design: shared store, ID allocation and caching.
7. A real concurrency test in xUnit.

## Related
[collision-lab](https://github.com/elithrade/collision-lab) (a separate console project) holds the experiments that motivated these decisions: birthday-paradox collision counts by code length, and the `Dictionary` race with and without a lock.
