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
- [x] Expiry, in progress (committed or about to be): `LinkEntry(Url, ExpiresAt)` (a `sealed record`) replaces the bare string in `IUrlStore.TryAddAsync`, while `GetAsync` still returns `string?` so the entry never leaves the store. `InMemoryUrlStore` takes a `TimeProvider` (registered as `TimeProvider.System`, which is not registered by default) and treats an entry as missing when `now >= ExpiresAt`. `ExpiryPolicy.TryResolve` turns an optional `expiresInSeconds` into an expiry: default 30 days, minimum 60 seconds, maximum 365 days, and `400` otherwise. Decisions: 404 (not 410) for expired links, absolute (not sliding) TTL, a duration in seconds (not a timestamp or `TimeSpan`) on the wire, and an expired entry keeps occupying its code until a sweep removes it, so a code can't be reused. Tests: store boundary theory (alive 1 ms before, dead exactly at expiry), a never-expiring link, the expired-code re-add (read first, then re-add), and `ExpiryPolicy` unit tests. Mutation-checked against `>=` versus `>`, a removed check, evict-on-read and policy boundary changes. Endpoint tests (fake clock registered as `AddSingleton<TimeProvider>`, since `AddSingleton(clock)` would register the `FakeTimeProvider` type only): mutation-checked with `<` to `<=` and `>=` to `>` (5 tests failed) a 29-day default (failed) and `now + lifetime - 1s` (failed)
- [x] Restructured into `src/` and `tests/` with `Shortener.slnx`, and added `AGENTS.md` and `CLAUDE.md` for agent instructions
- [x] Expiry sweep, store half (committed `e6af62b`): `IExpirySweeper { int Sweep(); }` is a separate interface implemented only by `InMemoryUrlStore`, because a database store will rely on native TTL. `Sweep` is synchronous, reads the clock once, and under one lock copies the expired entries with a brute-force `Where(...).ToArray()` and removes them, returning the count (no batch cap yet, by choice: simplest first, measure, then optimize). Registered once as a concrete singleton with `IUrlStore` and `IExpirySweeper` forwarding to it (two `AddSingleton<I, InMemoryUrlStore>` lines would create two instances, which was verified, so the sweeper would clean a store nobody uses). Tests: a mixed store with groups of different sizes (3 expired, 9 expiring exactly at the boundary, 9 live, 5 never-expiring), a second `Sweep()` returning 0, read-back of every entry, and "a swept code can be added again". Mutation-checked: `>=` versus `>`, removes everything, removes nothing, inverted, counts without removing, and the wrong clock all fail a test. The helper asserts its own setup, so assertions over its result can't pass vacuously
- [x] Sweep cost measured (throwaway experiments in collision-lab: `dotnet run -c Release -- sweep` and `-- stores`, Release build, 8 logical processors, median of 3 runs). **Current store (`Dictionary` + one lock):** the sweep scales linearly, about 8 to 11 ms per million entries to scan and 46 to 54 ms per million when everything expires (1M: 11 / 17 / 46 ms for none / 10% / all expired; 5M all expired: 269 ms), and a concurrent `GetAsync` waits for the *whole* sweep (worst latency equals the sweep time, 41 ms at 1M all expired, against about 0.05 ms idle) because the sweep holds the lock throughout. **`ConcurrentDictionary` twin** (experiment-only, same interfaces): the reader is never blocked (worst 0.01 to 0.25 ms), but the sweep is 3 to 7 times slower (1M: 56 ms scan-only, 152 ms all expired), memory is about 10% higher (218 MB against 199 MB), and 8 reader threads reach 484 million reads/s against 15 million for the lock (the lock version gets *slower* as threads are added: 73, 51, 25, 15 million at 1, 2, 4, 8 threads). That throughput gap is synthetic: a real request costs microseconds, so the lock is held about 0.02% of the time at 4,000 requests per second. Source reading (`ConcurrentDictionary.cs`): reads take no lock, writers take one stripe of a lock array (default `Environment.ProcessorCount` locks), `TryRemove` locks per entry, and `Count`, `ToArray`, `Keys`, `Values` and `Clear` take all locks. **`ToArray()` memory:** `Where().ToArray()` allocates 16 bytes per *expired* entry (a key/value reference pair; the strings and entries are not copied): 15.3 MB at 1M all expired and 76.3 MB at 5M, about 8% of the store's roughly 200 bytes per entry, so the store runs out of memory long before the temporary array does. The first sweep in a process allocated up to 2.5x that (a one-time LINQ warm-up, observed and not explained), and there were no gen2 GCs. **Alternatives, measured on a replica `Dictionary`:** removing in place while enumerating allocates 0 bytes, is about 2x faster (25 against 49 ms at 1M all expired; 165 against 275 ms at 5M) and did not throw on .NET 10, while a `Take(10,000)` batch cap that rescans from the start is much *slower* (91 ms at 1M and 1.35 s at 5M all expired; 1.5 s against 76 ms at 5M with 10% expired), because every batch walks the dictionary again. **Decision:** keep `Dictionary` + lock with the brute-force O(n) sweep, with no cap and no `PriorityQueue`: it is the simple, correct choice at this scale, the in-memory store is a stand-in for a database with native TTL, and the limits above are recorded for reference
- [x] `ExpirySweepService : BackgroundService` and its tests (committed `d625ecf`, `f9ece03`), plus a root `.editorconfig` (4 spaces, 2 for csproj, slnx, props, yml and json; CA2007 off). The service runs a `PeriodicTimer(60 s, TimeProvider)` loop with a per-sweep `try/catch` inside the loop (log and continue) and an outer catch for `OperationCanceledException` when the stop token is cancelled, and logs through `[LoggerMessage]` methods (clears CA1848, CA1873, CA2253 and CA2254). Verified first in scratch projects: the host calls `ExecuteAsync` once, `PeriodicTimer` follows a `FakeTimeProvider` (no tick at 59 s, a tick at 60 s, missed ticks coalesce), the host ignores an `OperationCanceledException` escaping `ExecuteAsync`, and an unhandled exception in a `BackgroundService` stops the host. In .NET 10 `StartAsync` runs `ExecuteAsync` through `Task.Run` and returns immediately (not synchronously to the first await), so a test that advances the clock straight away can run before the timer exists: the helper `AdvanceUntilFirstSweepAsync` advances one interval at a time until a sweep is seen. Tests use a `FakeSweeper` that signals a `SemaphoreSlim` on every call (before it throws), and cover: once per interval and not before (the 59 s + 1 s pair is repeated 3 times, because one pair passed a 90 s interval by coincidence), continuing after a sweep throws, and stopping cleanly (`ExecuteTask.IsCompletedSuccessfully`, and nothing sweeps after stop). `StopAsync` uses a 5 s expiring token so a loop that ignores the stop token fails instead of hanging. Mutation-checked: intervals of 60 h, 30, 59, 61, 90 and 120 s, `return` in the inner catch, no inner catch, no outer catch, and ignoring the stop token all fail. 20 of 20 runs passed, and and the suite has 110 tests with 0 warnings
- [x] DI-forwarding test (`IUrlStore_and_IExpirySweeper_resolve_the_same_instance` in `ShortenEndpointTests`): resolves both interfaces from `_factory.Services` with `GetRequiredService` and asserts `Assert.Same`. Mutation-checked: registering `AddSingleton<IUrlStore, InMemoryUrlStore>()` and `AddSingleton<IExpirySweeper, InMemoryUrlStore>()` (two separate instances, so the sweeper would clean a store nobody uses) fails it. The first draft used `GetKeyedService`, which returns `null` for non-keyed registrations, so `Assert.Same(null, null)` would have passed either way
- [x] Repos on GitHub: [url-shortener](https://github.com/elithrade/url-shortener), [collision-lab](https://github.com/elithrade/collision-lab)

## Line 1: usable and deployed (v1)

**Definition of done:** the service runs at a public URL, survives a restart without losing data, rejects abusive input, has green CI that deploys on merge to `main`, and the README explains how to run, test and deploy it.

| # | Milestone | Done when |
|---|---|---|
| 1 | ✅ Finish the test suite | Done: retry-path tests, store concurrency tests and the endpoint concurrency test (200 concurrent POSTs, each code resolving to its own URL) all pass, and each was mutation-checked |
| 2 | ✅ CI | Done: GitHub Actions builds and tests on every push and PR, a red run was demonstrated, and the `build` check is required by a ruleset on `main` (admin bypass left on, so it is advisory for the owner). Optional leftovers: a README badge, a test-report step, pinning the runner image or SDK |
| 3 | ✅ Better codes | Done: `RandomNumberGenerator.GetString` replaces `Random`, and a chi-squared uniformity test passes and fails against the biased version. Known limit: no test can detect a regression to `Random`, so consider enabling analyzer CA5394 |
| 4 | ✅ Expiry | Done: a link can have a TTL, and expired links return 404. Model, `ExpiryPolicy`, lazy check on read, handler wiring, store and policy tests, endpoint tests with a `FakeTimeProvider` (alive at lifetime minus 1 s and 404 at exactly the lifetime, for an explicit and the default lifetime, plus a boundary theory for 59, 60, 31536000 and 31536001, which also asserts the 400 body contains `31536000`; mutation-checked with `Seconds` instead of `TotalSeconds`, 4 tests failed), the sweep measurement (see above, with the decision to keep the brute-force sweep), `IExpirySweeper.Sweep()`, `ExpirySweepService` and its tests, and a test that the store and sweeper resolve to the same instance. Optional leftovers: in-place `Sweep` and logging assertions with `FakeLogger` |
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

## After v1: mock-interview drill
Once v1 is live, before Line 2: ask the coach to quiz you from `LESSONS.md`.
- Cold questions: you answer from memory, and the card is revealed afterwards.
- Weight the session towards cards you got wrong or found hard (`SemaphoreSlim`, shutdown handling, fake-clock tests).
- Mix formats: bug-hunting snippets (Mode 1), "what would you test here?" (Mode 2), and trade-off questions (Mode 3), not only recall.
- Include the cards where the coach was wrong (for example `StartAsync` timing), as traps.
- Record the weak spots afterwards, and add or fix cards for them.

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
Milestone 4 is done. Start milestone 5 (persistence). Before writing code, decide: SQLite or Postgres, native TTL versus the sweeper, the never-reissue policy for codes (see the design note below), and contract tests shared by both stores. Optional milestone 4 leftovers, any time:
1. **Optional: an integration test** with the real store, the service and a fake clock: add an entry, advance past its expiry, and wait for it to be gone. Hosted services also start inside `WebApplicationFactory` hosts, so an endpoint test that advances the clock by days can fire the sweeper.
2. **Optional small change to `Sweep`:** remove entries in place while enumerating, instead of `Where(...).ToArray()` then `Remove`. It allocates nothing and was about 2x faster in the measurements, and `Dictionary.Remove` during enumeration worked on .NET 10 (I recall it being made legal in .NET Core 3.0, so check the docs before relying on it). All the existing sweep tests should pass unchanged, so rerun them and the mutation checks. Skip it if you'd rather not touch a working, tested sweep.
3. **Optional: assert on the logs** (a failed sweep logs an error, a successful one logs the count) with a `FakeLogger`. Check the package and API first (probably `Microsoft.Extensions.Diagnostics.Testing`, not verified), and ask before adding the dependency.

Open design note for persistence (milestone 5): reads never delete, so the sweep is the only thing that frees a code for reuse, and a swept code can be issued again. That is accepted for the in-memory store. At large scale the expected number of reissued old codes is about `old x new / 3.5 trillion` (28 at 10M and 10M, about 285,000 at 1B and 1B), so the persistent store should use native TTL (reads still check the expiry) plus a never-reissue policy (a Bloom filter of issued codes, or counter-based codes), which is also the milestone 8 ID-allocation topic.

Optional tidy-ups: share the repeated `now` / `clock` / `store` setup in the store tests, simplify `ExpiryPolicy` to one exit with a `lifetime` variable, and give `ShortenedUrlRequest.ExpiresInSeconds` an explicit `= null`.
