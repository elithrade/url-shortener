# Project plan

Two finish lines, then the UI. Dates assume about 3 hours a day, 5 days a week (about 15 hours a week). Weeks start on Monday. Estimates are rough, so adjust at the checkpoints below.

Legend: `[x]` done, `[ ]` to do.

## Already done
- [x] Minimal API: `POST /shorten`, `GET /{code}` (302 or 404)
- [x] `IUrlStore` with a lock-based `InMemoryUrlStore`, bounded retry on collisions
- [x] `UrlValidator`: absolute http/https only, length cap, no credentials, no self-links, canonical form
- [x] Unit tests for the validator and endpoint tests with `WebApplicationFactory`
- [x] Retry-path tests with a `CollidingStore` fake: 503 when every code collides, success after 1 to 3 collisions, and the limit boundary (4 collisions succeed, 5 give 503). Mutation-checked against off-by-one loop bugs
- [x] Store concurrency tests against the real `InMemoryUrlStore`, released together from a `TaskCompletionSource` gate: same key gives exactly one winner, and 1,000 tasks x 10 distinct keys are all stored and readable. Mutation-checked: with the lock removed, the distinct-keys test failed 30 of 30 runs, and the same-key test only 1 of 30
- [x] Endpoint concurrency test through `WebApplicationFactory`: 200 concurrent `POST /shorten` each return 201, and every returned code redirects to its own URL. Mutation-checked: with the lock removed it failed 28 of 30 runs, and a scoped store registration makes it fail with 404s
- [x] CI with GitHub Actions (`.github/workflows/ci.yml`): checkout, `setup-dotnet` 10.0.x, Release build, `dotnet test --no-build`. A deliberately broken test on a PR turned the `build` check red at the test step. Ruleset `protect-main` requires a PR and the `build` check, and blocks force-push and deletion, with the repository admin role allowed to bypass (so direct pushes to `main` still work for the owner, and the gate is advisory for them)
- [x] Cryptographic codes: `CodeGenerator` uses `RandomNumberGenerator.GetString` instead of `Random`. A comparison experiment in collision-lab (`dotnet run -c Release -- generators`) showed that `Random` and `GetInt32` are both fair (chi-squared about 61), the biased `% 62` version is not (about 46,000, with `a` to `h` 25% too frequent), `GetInt32` per character is about 7x slower than `GetString` (4.9 s versus 0.7 s per million codes), and a seeded `Random` repeats exactly. Tests cover length, alphabet, distinctness, edge lengths and uniformity (chi-squared below 150), and the uniformity test was mutation-checked against the biased generator
- [x] Expiry, in progress (committed or about to be): `LinkEntry(Url, ExpiresAt)` (a `sealed record`) replaces the bare string in `IUrlStore.TryAddAsync`, while `GetAsync` still returns `string?` so the entry never leaves the store. `InMemoryUrlStore` takes a `TimeProvider` (registered as `TimeProvider.System`, which is not registered by default) and treats an entry as missing when `now >= ExpiresAt`. `ExpiryPolicy.TryResolve` turns an optional `expiresInSeconds` into an expiry: default 30 days, minimum 60 seconds, maximum 365 days, and `400` otherwise. Decisions: 404 (not 410) for expired links, absolute (not sliding) TTL, a duration in seconds (not a timestamp or `TimeSpan`) on the wire, and an expired entry keeps occupying its code until a sweep removes it, so a code can't be reused. Tests: store boundary theory (alive 1 ms before, dead exactly at expiry), a never-expiring link, the expired-code re-add (read first, then re-add), and `ExpiryPolicy` unit tests. Mutation-checked against `>=` versus `>`, a removed check, evict-on-read and policy boundary changes
- [x] Restructured into `src/` and `tests/` with `Shortener.slnx`, and added `AGENTS.md` and `CLAUDE.md` for agent instructions
- [x] Repos on GitHub: [url-shortener](https://github.com/elithrade/url-shortener), [collision-lab](https://github.com/elithrade/collision-lab)

## Line 1: usable and deployed (v1)

**Definition of done:** the service runs at a public URL, survives a restart without losing data, rejects abusive input, has green CI that deploys on merge to `main`, and the README explains how to run, test and deploy it.

| # | Milestone | Done when |
|---|---|---|
| 1 | ✅ Finish the test suite | Done: retry-path tests, store concurrency tests and the endpoint concurrency test (200 concurrent POSTs, each code resolving to its own URL) all pass, and each was mutation-checked |
| 2 | ✅ CI | Done: GitHub Actions builds and tests on every push and PR, a red run was demonstrated, and the `build` check is required by a ruleset on `main` (admin bypass left on, so it is advisory for the owner). Optional leftovers: a README badge, a test-report step, pinning the runner image or SDK |
| 3 | ✅ Better codes | Done: `RandomNumberGenerator.GetString` replaces `Random`, and a chi-squared uniformity test passes and fails against the biased version. Known limit: no test can detect a regression to `Random`, so consider enabling analyzer CA5394 |
| 4 | Expiry (in progress) | A link can have a TTL, and expired links return 404. Done: model, `ExpiryPolicy`, lazy check on read, handler wiring, store and policy tests. Left: endpoint tests with a fake clock (the handler wiring is unprotected: dropping the expiry or ignoring the 400 passes all tests), an assertion on the policy error message, and the background sweep (`BackgroundService` with `PeriodicTimer`) |
| 5 | Persistence | A SQLite (or Postgres) `IUrlStore` replaces the in-memory one. Links survive a restart. The same contract tests run against both implementations |
| 6 | Abuse protection | Rate limiting on `POST /shorten`, and a configured `BaseUrl` for the self-link check |
| 7 | Config and ops | Settings in `IOptions<T>` and env vars, a `/health` endpoint, structured logging, graceful shutdown |
| 8 | Ship it | Dockerfile, image built and pushed by CI, deployed to a host, post-deploy smoke test |

### Schedule
| Week | Dates | Focus |
|---|---|---|
| 0 | Oct 16 | Milestone 1: finish tests |
| 1 | Oct 19 to 23 | Milestones 2, 3, 4: CI, better codes, expiry |
| 2 | Oct 26 to 30 | Milestone 5: persistence and contract tests |
| 3 | Nov 2 to 6 | Milestones 6, 7: rate limiting, config, health, logging, Dockerfile |
| 4 | Nov 9 to 13 | Milestone 8: deploy, smoke test, README. **v1 live** |

## Line 2: system design practice
Optional for v1, and the main place for learning trade-offs. Start after v1 is live. Each item is a small project with its own write-up.

| # | Topic | Exercise | Done when |
|---|---|---|---|
| A | Click analytics | Count clicks off the hot path with `Channel<T>` and a background consumer | Redirect latency is unaffected, and behavior when the queue is full is decided and tested (drop, block or spill) |
| B | Caching | LRU cache in front of the store | Hit rate measured, and invalidation on expiry or delete is handled |
| C | Multi-instance | Two instances behind a proxy with a shared store (Redis or Postgres) | Uniqueness enforced by the shared store (`SET NX` or a unique index), no duplicates under parallel load |
| D | Load test | k6 or bombardier against the redirect path | p99 latency measured, the bottleneck found and one fix applied |
| E | Failure injection | Kill the database mid-request, add timeouts, retries and a circuit breaker | Users get a clean error, and the system recovers on its own |
| F | Design write-up | A short design doc covering decisions and trade-offs | Doc committed to the repo |

### Schedule
| Week | Dates | Focus |
|---|---|---|
| 5 | Nov 16 to 20 | A: click analytics |
| 6 | Nov 23 to 27 | B and D: caching and load test |
| 7 | Nov 30 to Dec 4 | C: multi-instance |
| 8 | Dec 7 to 11 | E and F: failure injection and write-up |

## Then: the UI
Weeks 9 and 10 (Dec 14 to 25). Decide the stack early (React or Next.js, Blazor, or something else), because the API will need CORS and an OpenAPI document first.

## Out of scope for now
User accounts, link editing, custom aliases, QR codes. Any of these is a reasonable v2.

## Working agreement
- You write the core logic, and I give the concept first and review your code.
- Plumbing (setup, config, boilerplate) can be handed over to save time.
- Time-box each milestone. If one runs over budget, cut scope instead of moving dates, for example SQLite only, or skip failure injection.
- Check actual hours against this plan at the end of weeks 4 and 8, and adjust.
- Start each day by re-reading yesterday's diff, and end it with a committed state plus a note on what comes next.

## Next up
Milestone 4, remaining work, in this order:
1. **Commit the current expiry work** (lowercase subject, for example `add expiry policy and expiry check on read`).
2. **Endpoint tests with a fake clock.** In `WithWebHostBuilder`, register a `FakeTimeProvider` as `TimeProvider`, as was done for `CollidingStore`, with a helper like `NewClient(FakeTimeProvider clock)`. Cases: `expiresInSeconds: 3600` redirects and then returns 404 after `Advance(1 hour)`; no field is alive after 29 days and gone after 30; `-5` and `31536001` return 400 with the range in the body. These close the two handler mutations that currently pass (no expiry stored, 400 ignored).
3. **Assert the policy error message content** (`Assert.Contains("31536000", error)`), so a `Seconds` versus `TotalSeconds` regression is caught.
4. **Background sweep:** a `BackgroundService` with a `PeriodicTimer` that removes expired entries. Decide how it reaches the store (a method on the store, or on a separate interface) and what an O(n) scan under the lock costs at large sizes (for example sweeping in batches). It only reclaims memory, because lazy expiry already makes reads correct. Test it with `FakeTimeProvider`, and think about what happens on shutdown.
5. **Tick milestone 4**, then milestone 5 (persistence).

Optional tidy-ups: share the repeated `now` / `clock` / `store` setup in the store tests, simplify `ExpiryPolicy` to one exit with a `lifetime` variable, and give `ShortenedUrlRequest.ExpiresInSeconds` an explicit `= null`.
