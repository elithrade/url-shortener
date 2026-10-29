# Project plan

Two finish lines, then the UI. Dates assume about 3 hours a day, 5 days a week (about 15 hours a week). Weeks start on Monday. Estimates are rough, so adjust at the checkpoints below.

Legend: ✅ done, 🟡 in progress, ⬜ to do.

## Already done

### ✅ Basics
- ✅ Minimal API: `POST /shorten`, `GET /{code}` (302 or 404)
- ✅ `IUrlStore` with a lock-based `InMemoryUrlStore`, bounded retry on collisions
- ✅ `UrlValidator`: absolute http/https only, length cap, no credentials, no self-links, canonical form
- ✅ Restructured into `src/` and `tests/` with `Shortener.slnx`; added `AGENTS.md` and `CLAUDE.md`
- ✅ Repos on GitHub: [url-shortener](https://github.com/elithrade/url-shortener), [collision-lab](https://github.com/elithrade/collision-lab)

### ✅ Milestone 1: test suite
- ✅ Validator unit tests, and endpoint tests with `WebApplicationFactory`
- ✅ **Retry path** with a `CollidingStore` fake
  - 503 when every code collides, success after 1 to 3 collisions
  - Limit boundary: 4 collisions succeed, 5 give 503
  - Mutation-checked against off-by-one loop bugs
- ✅ **Store concurrency** against the real `InMemoryUrlStore`, released together from a `TaskCompletionSource` gate
  - Same key: exactly one winner. 1,000 tasks × 10 distinct keys: all stored and readable
  - Lock removed: the distinct-keys test failed 30 of 30 runs, the same-key test only 1 of 30
- ✅ **Endpoint concurrency**: 200 concurrent `POST /shorten` each return 201, and every code redirects to its own URL
  - Lock removed: failed 28 of 30 runs. A scoped store registration fails it with 404s

### ✅ Milestone 2: CI
- ✅ GitHub Actions (`.github/workflows/ci.yml`): checkout, `setup-dotnet` 10.0.x, Release build, `dotnet test --no-build`
- ✅ A deliberately broken test on a PR turned the `build` check red at the test step
- ✅ Ruleset `protect-main`: requires a PR and the `build` check, blocks force-push and deletion
  - The admin role may bypass, so direct pushes still work for the owner (the gate is advisory for them)

### ✅ Milestone 3: cryptographic codes
- ✅ `CodeGenerator` uses `RandomNumberGenerator.GetString` instead of `Random`
- **Comparison experiment** (collision-lab, `dotnet run -c Release -- generators`)
  - `Random` and `GetInt32` are both fair (chi-squared about 61)
  - The biased `% 62` version is not (about 46,000; `a` to `h` 25% too frequent)
  - `GetInt32` per character is about 7x slower than `GetString` (4.9 s vs 0.7 s per million codes)
  - A seeded `Random` repeats exactly
- **Tests:** length, alphabet, distinctness, edge lengths, uniformity (chi-squared below 150), mutation-checked against the biased generator

### ✅ Milestone 4: expiry

#### ✅ Model, policy and read-time check
- `LinkEntry(Url, ExpiresAt)` (a `sealed record`) replaces the bare string in `TryAddAsync`; `GetAsync` still returns `string?`, so the entry never leaves the store
- `InMemoryUrlStore` takes a `TimeProvider` (`TimeProvider.System` must be registered, it isn't by default) and treats an entry as missing when `now >= ExpiresAt`
- `ExpiryPolicy.TryResolve`: default 30 days, minimum 60 s, maximum 365 days, otherwise `400`
- **Decisions**
  - 404 (not 410) for expired links
  - Absolute TTL, not sliding
  - A duration in seconds on the wire, not a timestamp or `TimeSpan`
  - An expired entry keeps its code until a sweep removes it
- **Tests:** store boundary (alive 1 ms before, gone exactly at expiry), a never-expiring link, the expired-code re-add, `ExpiryPolicy` unit tests
  - Mutation-checked: `>=` vs `>`, a removed check, evict-on-read, policy boundaries
- **Endpoint tests** with a fake clock, registered as `AddSingleton<TimeProvider>` (`AddSingleton(clock)` registers only the `FakeTimeProvider` type)
  - Mutation-checked: `<` to `<=` and `>=` to `>` (5 tests failed), a 29-day default, `now + lifetime - 1s`

#### ✅ Sweep, store half
- `IExpirySweeper` is a separate interface. It was first meant for the in-memory store only (assuming a database has native TTL); Postgres has none, so it implements it too. A store with native TTL (Redis) would not
- The sweep reads the clock once, and under one lock copies the expired entries (`Where(...).ToArray()`) and removes them, returning the count. No batch cap, by choice: simplest first, measure, then optimize
- **Registration:** one concrete singleton, with `IUrlStore` and `IExpirySweeper` forwarding to it
  - Two `AddSingleton<I, InMemoryUrlStore>` lines would create two instances (verified), so the sweeper would clean a store nobody uses
- **Tests:** a mixed store (3 expired, 9 exactly at the boundary, 9 live, 5 never-expiring), a second sweep returns 0, every entry read back, a swept code can be added again
  - Mutation-checked: `>=` vs `>`, removes everything, removes nothing, inverted, counts without removing, wrong clock
  - The helper asserts its own setup, so assertions over its result can't pass vacuously
- **DI-forwarding test** (`IUrlStore_and_IExpirySweeper_resolve_the_same_instance`): `Assert.Same` on both resolved services
  - Fails with two separate registrations
  - The first draft used `GetKeyedService`, which returns `null` for non-keyed registrations, so `Assert.Same(null, null)` would always pass

#### ✅ Sweep cost measured
Throwaway experiments in collision-lab (`-- sweep` and `-- stores`), Release build, 8 logical processors, median of 3 runs.
- **Current store (`Dictionary` + one lock)**
  - Linear: about 8 to 11 ms per million entries to scan, 46 to 54 ms per million when all expire
  - 1M entries: 11 / 17 / 46 ms for none / 10% / all expired; 5M all expired: 269 ms
  - A concurrent `GetAsync` waits for the whole sweep: worst latency 41 ms at 1M all expired, vs about 0.05 ms idle
- **`ConcurrentDictionary` twin** (experiment only)
  - Readers are never blocked (worst 0.01 to 0.25 ms)
  - The sweep is 3 to 7x slower (1M: 56 ms scan only, 152 ms all expired); memory about 10% higher (218 vs 199 MB)
  - 8 reader threads: 484 million reads/s vs 15 million for the lock, which gets slower with more threads (73, 51, 25, 15 million at 1, 2, 4, 8)
  - That gap is synthetic: at 4,000 requests/s the lock is held about 0.02% of the time
  - From the source: reads take no lock, writers lock one stripe (default `ProcessorCount` locks), `TryRemove` locks per entry, and `Count`, `ToArray`, `Keys`, `Values`, `Clear` take all locks
- **`ToArray()` memory**
  - 16 bytes per expired entry (a reference pair): 15.3 MB at 1M, 76.3 MB at 5M, about 8% of the store's ~200 bytes per entry
  - The first sweep in a process allocated up to 2.5x that (a one-time LINQ warm-up, not explained); no gen2 GCs
- **Alternatives** (on a replica `Dictionary`)
  - Removing in place while enumerating: 0 bytes, about 2x faster (25 vs 49 ms at 1M; 165 vs 275 ms at 5M), didn't throw on .NET 10
  - A `Take(10,000)` batch cap that rescans: much slower (91 ms at 1M, 1.35 s at 5M all expired), because every batch walks the dictionary again
- **Decision:** keep `Dictionary` + lock with the brute-force O(n) sweep, no cap, no `PriorityQueue`. Simple and correct at this scale, and the in-memory store is a stand-in for a database store

#### ✅ `ExpirySweepService`
- A `BackgroundService` with a `PeriodicTimer(60 s, TimeProvider)` loop
  - A `try/catch` per sweep inside the loop (log and continue), an outer catch for `OperationCanceledException` on stop
  - Logs through `[LoggerMessage]` methods (clears CA1848, CA1873, CA2253, CA2254)
- Root `.editorconfig`: 4 spaces (2 for csproj, slnx, props, yml, json), CA2007 off
- **Verified first in scratch projects**
  - The host calls `ExecuteAsync` once
  - `PeriodicTimer` follows a `FakeTimeProvider`: no tick at 59 s, a tick at 60 s, missed ticks coalesce
  - The host ignores an `OperationCanceledException` escaping `ExecuteAsync`; an unhandled exception stops the host
  - In .NET 10, `StartAsync` runs `ExecuteAsync` through `Task.Run`, so a test that advances the clock straight away can run before the timer exists. `AdvanceUntilFirstSweepAsync` advances one interval at a time until a sweep is seen
- **Tests** with a `FakeSweeper` that signals a `SemaphoreSlim` on every call
  - Once per interval and not before (the 59 s + 1 s pair repeated 3 times, because one pair passed a 90 s interval by coincidence)
  - Keeps running after a sweep throws; stops cleanly, and nothing sweeps after stop
  - `StopAsync` uses a 5 s expiring token, so a loop that ignores the stop token fails instead of hanging
- **Mutation-checked:** intervals of 60 h, 30, 59, 61, 90, 120 s; `return` in the inner catch; no inner catch; no outer catch; ignoring the stop token

### ✅ Milestone 5: persistence

#### ✅ Redis lab
Hands-on in `redis-cli` in a `redis:7` container; no code in this repo.
- **Decision (revised the same day):** Postgres is the source of truth (milestone 5), Redis a read-through cache in front of it (Line 2, item B)
  - Why: Redis is priced in RAM (about 150 GB per billion keys), and with AOF `everysec` it can lose about a second of acknowledged writes
  - Polyglot persistence was the other side: a second system to run and back up, and no transaction across two stores
- **Verified by running it**
  - `SET k v NX EX 30` is an atomic insert-if-absent with expiry (`OK`, then `(nil)` on a collision); a failed `NX` leaves the old value and TTL alone. This is exactly `TryAddAsync`
  - `TTL` returns -1 for "no expiry" and -2 for "no such key"; an expired key reads as missing
  - A plain `SET` on an existing key discards its TTL, so only ever write with `NX`
  - `EX 0` and `PX -5` are errors, so a past `ExpiresAt` can't be written
  - Redis removed 1,000 expired, unread keys within 3 s
- **From the docs, not tested separately:** expiry is passive (on access) plus active (sampling), so a read never returns an expired key
- **Persistence**
  - Defaults (`save 3600 1 300 100 60 10000`, `appendonly no`): a SIGKILL loses everything since the last snapshot; a SIGTERM (`docker stop`) writes a final `dump.rdb`
  - With `--appendonly yes` a write survived `docker kill`; `appendfsync` defaults to `everysec` (the docs may say up to 2 s lost, check)
  - Turning on AOF on a volume that only has `dump.rdb` silently ignored the snapshot, so choose the mode before real data exists
  - A container needs a volume on `/data`; `--rm` without a volume made the first restart test meaningless
- **Memory** (100,000 keys, 7-char key, 40-char value, 30-day TTL)
  - 149.7 bytes per key; `MEMORY USAGE` says 104 for one key; the TTL adds about 24 bytes (guess: the expiry table)
  - 1 billion keys: about 150 GB before replicas; 10M links: about 1.5 GB
- **Eviction** (`maxmemory 3mb`)
  - `noeviction`: 13,584 of 100,000 `SET`s succeeded, the rest failed with `OOM command not allowed`
  - `allkeys-lru`: all returned `OK`, but only 13,464 keys remained, so live links were deleted silently
  - As a store this would need `noeviction`, an alert on `used_memory`, and OOM mapped to 503. A managed Redis may default to eviction
  - As a cache the choice flips: eviction is the point, and a lost entry only costs a read from Postgres

#### ✅ Postgres lab
Hands-on in a `postgres:17` container with a named volume; table `links (code text PRIMARY KEY, url text NOT NULL, expires_at timestamptz)`.
- **Atomic insert**
  - `INSERT ... ON CONFLICT DO NOTHING` reports `INSERT 0 1` or `INSERT 0 0`, which maps straight to `true` / `false`
  - A plain `INSERT` of an existing key throws `23505` (`links_pkey`); the message contains the key, so it must never reach a response
  - `DO NOTHING` also returns 0 for an expired, unswept row, so the code stays unusable until the sweep (same rule as in memory)
- **Race:** 50 parallel inserts of one code gave exactly 1 success and one row, with no app lock (evidence, not proof: 50 `docker exec` processes aren't simultaneous)
- **Why no app lock is needed:** a second session inserting a key that an open transaction inserted waits (`Lock` / `transactionid`), then gets `INSERT 0 0` after commit or `INSERT 0 1` after rollback
- **Delete and dead tuples** (1M rows, half expired)
  - `DELETE` of 500,000 rows: 199 ms idle (the in-memory sweep is about 8x faster)
  - The table stayed 73 MB (110 MB with indexes) with 500,000 dead tuples: a delete only ends a row version, vacuum frees the space
  - Plain vacuum makes the space reusable but doesn't shrink the file; `VACUUM FULL` rewrote it in about 1.6 s
- **Lock queue**
  - With an open read transaction, `VACUUM FULL` waited, and a later read queued behind it for about 4.8 s
  - So an idle-in-transaction session plus a command needing an exclusive lock can stall every read. Schema migrations probably behave the same (inferred; verify before the first deploy)
- **Sweep vs readers** (4 clients, primary-key lookups, 12 s)
  - Baseline: 374,078 reads, average 0.128 ms, worst 4.9 ms
  - During one `DELETE` of 500,000 rows (380 ms): 366,782 reads, average 0.131 ms, worst 6.1 ms
  - In 50 batches of 10,000: worst 3.9 ms, but the delete took 4,255 ms (about 11x longer)
  - **Decision:** one `DELETE` per sweep, no batch cap. Readers aren't blocked, and batching only costs time at this size
  - Not covered: scans by `expires_at`, concurrent inserts, replication and WAL volume, 100x larger tables, repeated runs
- **Durability**
  - `fsync`, `synchronous_commit`, `full_page_writes` and `autovacuum` are all `on` by default
  - After `docker kill`, a committed row survived and an uncommitted one didn't (automatic recovery in the log)
  - A commit waits for the WAL to reach disk, unlike Redis `everysec`

#### ✅ Contract rule for a past expiry
- `TryAddAsync` rejects an `ExpiresAt` that isn't in the future with `ArgumentOutOfRangeException`, before the lock and the insert
  - `ExpiresAt == now` counts as past (matches `now >= ExpiresAt` on reads); `null` (never expires) is accepted
  - A store rule because nothing else would stop a store from storing a pre-expired entry (Redis rejects `EX 0` itself)
- **Tests** in the abstract `UrlStoreContractTests`
  - `-1 day`, `-1 ms`, `0` throw; `+1 ms`, `+1 day` and `null` are accepted
  - An existing code with a past expiry throws instead of returning `false` (precondition wins)
  - A rejected add leaves the code free
- **Mutation-checked:** `<` instead of `<=`, throwing after the insert, `ExpiresAt!.Value <= now`
- CA2208 caught a real bug: `new ArgumentOutOfRangeException("text")` treats the string as the parameter name

#### ✅ Postgres store and registration
- `AddDbContextFactory<ShortenerDbContext>` (`UseNpgsql`), `PostgresUrlStore` once with both interfaces forwarding, `ExpirySweepService` for both stores
- A startup log line names the storage kind, never the connection string
- `PostgresRegistrationTests`: same instance for both interfaces, `TimeProvider` and exactly one `ExpirySweepService` registered, and a round trip on a brand new empty database (`CREATE DATABASE` per test, `DROP ... WITH (FORCE)` on dispose)
  - Mutation-checked: two separate registrations, no `ExpirySweepService`
- *Superseded:* schema creation with `EnsureCreatedAsync` at startup, replaced by migrations and the `migrate` mode (below)
- **`EnsureCreatedAsync` race experiment** (30 runs each, gated callers)
  - 8 concurrent callers on an empty database failed in 6 of 30 runs (`42P07 relation "links" already exists`)
  - Two hosts starting together failed in 0 of 30 (probably because startup is slow compared with the schema step; a guess)
  - Led to making schema creation a deploy step

#### ✅ Storage outage maps to 503
Committed `30420c3` (its subject says 502 by mistake; the code and tests use 503).
- **Experiment first** (Postgres 17, no retry strategy)
  - Refused port, or a stopped container on reads and sweeps: `InvalidOperationException` → `NpgsqlException` (`IsTransient`) → `SocketException`
  - A write to a stopped container: `InvalidOperationException` → `DbUpdateException` → `PostgresException 57P01`
  - So the outer type is never `NpgsqlException`, and catching `InvalidOperationException` alone would also catch DI failures
- **Design (the owner's choice)**
  - Each store translates its own provider errors into `StorageUnavailableException`; the endpoints catch only that
  - `PostgresUrlStore.IsUnavailable` walks the inner chain for `NpgsqlException { IsTransient: true }`
  - In `TryAddAsync` the `23505` catch comes first (a collision still returns `false`)
  - Endpoints return `503` with an empty body, log it, and **don't retry** (the retry loop is for collisions). Other exceptions stay `500`
- **Tests:** unreachable port (all three methods), a missing database (`3D000`, not transient, must escape untranslated), and endpoint tests with an `ExceptionStore` fake (no secret text in the body)
  - Mutation-checked: catching `Exception`, a `GET` catch that never matches, `continue` instead of `return`, leaking the inner message, `when (true)`
- **Not covered:** the `57P01` path, cancellation passing through untranslated. `IsTransient` is broader than "down" (timeouts also become 503, intended). Rerun the experiment if `EnableRetryOnFailure` is ever added

#### ✅ Migrations and the `migrate` mode
Committed `177635c`, `1ee118d`.
- **Packages:** `Microsoft.EntityFrameworkCore.Design` 10.0.4 and the `dotnet-ef` 10.0.4 local tool (10.0.13 broke the test build with CS1705)
- `DesignTimeDbContextFactory` exists because `Program.cs` registers the context only for Postgres; `InitialCreate` lives in `Storage/Postgres/Migrations`
- **`SchemaMigrator.ExecuteAsync`** runs `MigrateAsync` in a `try` and returns `0`, or `1` with the error logged
  - No `CancellationToken` yet: a cancel would count as a failure
- **`Program.cs`:** `migrate` as the first argument runs the migrator and returns its exit code without starting the server (`1` when storage isn't Postgres)
- **Tests** (`PostgresMigrationTests`): table absent before and present after, `InitialCreate` applied, read-back through a fresh context, a second run keeps a row, an unreachable port returns `1`
  - Mutation-checked: a no-op, `EnsureCreatedAsync` instead (fails only the `InitialCreate` assertion), `catch` returning `0`, no `try`/`catch`
- **Traps hit**
  - A stub that returns the expected value leaves its test green without running any code
  - `Environment.Exit` in the startup path killed the test host and hid every result
  - The fixture's `EnsureCreatedAsync` database (no history table) clashed with migrations at startup
- **Run by hand** against a throwaway `postgres:17`: first run `0`, second `0`, wrong port `1`, in-memory `1`; EF logs an expected `fail:` for the history-table probe
- **Decisions:** schema creation is a one-shot `migrate` step before the app; `migrate` doesn't create the database (that needs a more privileged role)

#### ✅ Dockerfile and Compose
Committed `68afdff`.
- Multi-stage `Dockerfile` (`sdk:10.0` to `aspnet:10.0`, cached restore, non-root user, port 8080) and `.dockerignore`
- `compose.yaml`: `postgres:17` (named volume, `pg_isready` healthcheck), a one-shot `migrate` service, and `app` after it (`service_completed_successfully`)
- `POSTGRES_PASSWORD` is required from the shell or an untracked `.env`, never in the file. Postgres only reads it when it creates the data directory, so changing it needs `down -v`
- **Restart check:** a link created, `down` (volume kept), `up` again, and the same code still redirected
- The `migrate` log shows `libgssapi_krb5.so.2: cannot open shared object file` (probably Npgsql probing for Kerberos; harmless, not confirmed)

### 🟡 Milestone 6: abuse protection

#### ✅ Rate limiting on `POST /shorten`
- A token bucket per client IP, through `RateLimitFilter` (an `IEndpointFilter`), with settings from `RateLimit:*` (default burst 5, 1 per second)
- The key: IPv6 reduced to its /64, IPv4-mapped normalized, a shared `Unknown` key (with a warning) when there is no address
- `429` with `Retry-After` in whole seconds. The limiter itself is exact to the tick, and a clock stepped backwards counts as no time passed
- Limiter, key and endpoint tests were each mutation-checked
- Endpoint tests use `StrictRateLimitFactory` and a test-only `X-Test-Client-Ip` filter, because `RemoteIpAddress` is `null` under `TestServer`
- Details in `README.md` and `LESSONS.md` sections 6 and 20

#### ⬜ Configured `BaseUrl` for the self-link check

## Line 1: usable and deployed (v1)

**Definition of done:** the service runs at a public URL, survives a restart without losing data, rejects abusive input, has green CI that deploys on merge to `main`, and the README explains how to run, test and deploy it.

| # | Milestone | Done when |
|---|---|---|
| 1 | ✅ Finish the test suite | Done: retry-path tests, store concurrency tests and the endpoint concurrency test (200 concurrent POSTs, each code resolving to its own URL) all pass, and each was mutation-checked |
| 2 | ✅ CI | Done: GitHub Actions builds and tests on every push and PR, a red run was demonstrated, and the `build` check is required by a ruleset on `main` (admin bypass left on, so it is advisory for the owner). Optional leftovers: a README badge, a test-report step, pinning the runner image or SDK |
| 3 | ✅ Better codes | Done: `RandomNumberGenerator.GetString` replaces `Random`, and a chi-squared uniformity test passes and fails against the biased version. Known limit: no test can detect a regression to `Random`, so consider enabling analyzer CA5394 |
| 4 | ✅ Expiry | Done: a link can have a TTL, and expired links return 404. Model, `ExpiryPolicy`, lazy check on read, handler wiring, store and policy tests, endpoint tests with a `FakeTimeProvider` (alive at lifetime minus 1 s and 404 at exactly the lifetime, for an explicit and the default lifetime, plus a boundary theory for 59, 60, 31536000 and 31536001, which also asserts the 400 body contains `31536000`; mutation-checked with `Seconds` instead of `TotalSeconds`, 4 tests failed), the sweep measurement (see above, with the decision to keep the brute-force sweep), `IExpirySweeper.Sweep()`, `ExpirySweepService` and its tests, and a test that the store and sweeper resolve to the same instance. Optional leftovers: in-place `Sweep` and logging assertions with `FakeLogger` |
| 5 | ✅ Persistence | A Postgres `IUrlStore` (EF Core with the Npgsql provider) replaces the in-memory one. Links survive a restart, acknowledged writes are durable, and the same contract tests run against both implementations. Uniqueness comes from a primary key on the code, and expiry from an `expires_at` column that reads check against the injected `TimeProvider`, with the existing sweeper deleting expired rows. Users and auth can later use the same database. Done: the store, the contract tests, registration, `503` on an outage, EF migrations with a one-shot `migrate` step, and a Compose stack with a data volume whose restart check kept a link (see "Already done"). The Redis lab's findings feed the Line 2 cache. Optional leftovers: see "Next up" |
| 6 | Abuse protection | Rate limiting on `POST /shorten` (done), and a configured `BaseUrl` for the self-link check |
| 7 | Config and ops | Settings in `IOptions<T>` and env vars, a `/health` endpoint, structured logging, graceful shutdown |
| 8 | Ship it | Dockerfile, image built and pushed by CI, deployed to a host, post-deploy smoke test. **Decide first:** where to host and which Postgres to use (a managed service, which gives backups, or a container with a `/data` volume and scheduled `pg_dump` backups plus a tested restore). The connection string comes from configuration, and the deploy must run the `migrate` step once before the app starts (settled: EF migrations, one-shot, see "Already done"), with a database role that can alter the schema for that step only |

### Schedule
| Week | Dates | Focus |
|---|---|---|
| 0 | Oct 16 | Milestone 1: finish tests |
| 1 | Oct 19 to 23 | Milestones 2, 3, 4: CI, better codes, expiry |
| 2 | Oct 26 to 30 | Milestone 5: persistence and contract tests |
| 3 | Nov 2 to 6 | Milestones 6, 7: rate limiting, config, health, logging, Dockerfile |
| 4 | Nov 9 to 13 | Milestone 8: deploy, smoke test, README. **v1 live** |

## Deployment shape (notes for milestone 8 and Line 2)
Decided in discussion, not yet built:
- **The API and the database are never one container.** Separate containers, one main process each. Reasons: (1) scaling: with a database inside each API container, instances would hold different links and could issue the same code twice, because uniqueness comes from one shared primary key (Line 2, item C); (2) the API is stateless and replaceable, while the database needs a volume, backups and careful upgrades, so redeploying one must not restart the other; (3) different resources and failure modes; (4) health checks, logs and limits stay meaningful per container.
- **v1 layout:** the same shape as `compose.yaml` on one VM (Postgres with a volume, the `migrate` step, the app), or a **managed Postgres** with only the API deployed. The managed option gives backups, failover and patching, and is the one to lean towards if the data matters. This is still the open "decide first" item in milestone 8.
- **Several API instances:** N copies behind a load balancer, all on the same database. The `migrate` step runs once per deploy, not once per instance (the `EnsureCreatedAsync` race). During a rolling deploy the old version runs against the new schema for a while, so schema changes should be additive first (expand), with drops and renames in a later release (contract).
- **Redis** (Line 2, item B) is its own service, never inside the API container: a read-through cache in front of Postgres, a cache TTL that never outlives `ExpiresAt`, eviction allowed (a lost entry costs one read), and redirects must still work when Redis is down.
- Not covered yet: the load balancer and forwarded headers (the rate limiter's client key and the self-link check both depend on them), secrets in the host's store, and backup and restore tests.

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
| B | Caching | Redis as a read-through cache in front of the Postgres store | Hit rate measured; invalidation on expiry or delete is handled; Redis runs with a `maxmemory` limit and an eviction policy chosen for a cache (the lab showed `noeviction` fails writes and `allkeys-lru` evicts, so decide which and why); the cache TTL never outlives the link's `ExpiresAt`; redirects still work when Redis is down |
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
1. **Configured `BaseUrl` for the self-link check** (milestone 6, part 2). The check compares against the request's `Host` header today, which the client controls.
2. **Milestone 7:** settings in `IOptions<T>`, a `/health` endpoint, structured logging, graceful shutdown.
3. **Milestone 8:** decide the host and the Postgres option first (see the milestone table and "Deployment shape").

## Open items (not scheduled)
**Rate limiting**
- Rate-limit the missing-address warning (log once, or once a minute).
- A concurrency test for `TokenBucketLimiter`, and eviction of idle buckets.
- An upper bound on `TokenBucketOptions.Period` (a huge period could overflow the tick cast, see `LESSONS.md` section 20).
- Check whether malformed bodies bypass the limit (endpoint filters run after model binding).
- Limit 404s per client on `GET` against code enumeration.
- Behind a proxy: `ForwardedHeaders` with trusted proxies (milestone 8).

**Persistence**
- Decide what a normal start does when the schema is missing: fail fast (for example via `GetPendingMigrationsAsync`) or keep the `500` on the first request.
- Build the shared test fixture's database with `SchemaMigrator` instead of `EnsureCreatedAsync`, so tests use the production schema path.
- The `57P01` test from the storage-outage entry.
- A `CancellationToken` for `SchemaMigrator`.
- CI could run the Compose stack as a smoke test.

**Expiry (optional, from milestone 4)**
- An integration test with the real store, the sweeper service and a fake clock (hosted services also run inside `WebApplicationFactory`).
- Remove entries in place in the in-memory `Sweep` instead of `Where(...).ToArray()` (about 2x faster when measured; check the docs on `Dictionary.Remove` during enumeration first).
- Assert on the sweep logs with a `FakeLogger` (check the package first and ask before adding it).

**Tidy-ups (optional)**
- Share the repeated `now` / `clock` / `store` setup in the store tests.
- Simplify `ExpiryPolicy` to one exit with a `lifetime` variable.
- Give `ShortenedUrlRequest.ExpiresInSeconds` an explicit `= null`.

## Design notes
- **Never-reissue policy for codes.** Reads never delete, so the sweep is what frees a code, and a swept code can be issued again. Accepted for the in-memory store. At scale the expected number of reissued old codes is about `old × new / 3.5 trillion` (28 at 10M and 10M, about 285,000 at 1B and 1B), so the persistent store needs a policy. Options: keep expired rows and only hide them (the table grows, nothing is reused), delete the row but record the code in a tombstone table, or counter-based codes (a Bloom filter of issued codes is the in-memory alternative). This is also the milestone 8 ID-allocation topic.
- **Migrations and locks.** A migration that needs an exclusive table lock can stall reads (see the lock queue in the Postgres lab), so review each new migration for that.
