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
  - Alive at lifetime minus 1 s and `404` at exactly the lifetime, for an explicit and the default lifetime
  - A boundary theory for 59, 60, 31536000 and 31536001 that also asserts the `400` body contains `31536000`
  - Mutation-checked: `<` to `<=` and `>=` to `>` (5 tests failed), a 29-day default, `now + lifetime - 1s`, `Seconds` instead of `TotalSeconds` (4 tests failed)

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

### ✅ Milestone 6: abuse protection

#### ✅ Rate limiting on `POST /shorten`
- A token bucket per client IP, through `RateLimitFilter` (an `IEndpointFilter`), with settings from `RateLimit:*` (default burst 5, 1 per second)
- The key: IPv6 reduced to its /64, IPv4-mapped normalized, a shared `Unknown` key (with a warning) when there is no address
- `429` with `Retry-After` in whole seconds. The limiter itself is exact to the tick, and a clock stepped backwards counts as no time passed
- Limiter, key and endpoint tests were each mutation-checked
- Endpoint tests use `StrictRateLimitFactory` and a test-only `X-Test-Client-Ip` filter, because `RemoteIpAddress` is `null` under `TestServer`
- Details in `README.md` and `LESSONS.md` sections 6 and 20

#### ✅ Configured `BaseUrl` for the self-link check
- ✅ `Shortener:BaseUrl` is bound to `ShortenerOptions` and checked at startup by `ShortenerOptionsValidator` (`ValidateOnStart`): required, absolute, http or https, https outside Development, no user info, path, query or fragment. Dev value `http://localhost:5154` in `appsettings.Development.json`
- ✅ Theory tests for every rule (one rule broken per row), and `BaseUrlStartupTests` proving a bad value stops startup. An empty-host check was found unreachable and removed (`LESSONS.md` section 21)
- ✅ `RateLimit:*` moved to `RateLimitOptions` (DataAnnotations, `ValidateOnStart`), defaults in `appsettings.json`, mapped to the unchanged `TokenBucketOptions`
- ✅ `HandlePost` takes `IOptions<ShortenerOptions>` (parsed per request) and no longer reads the request: the self-link check uses `baseUrl.Host` and `shortUrl` is `new Uri(baseUrl, code)`. Red-then-green endpoint tests with a forged `Host: evil.example`
- ✅ Compose: the `app` service runs as Development with `Shortener__BaseUrl=http://localhost:8080` (Development allows http). Checked: `shortUrl` was `http://localhost:8080/...`. Milestone 8 replaces this with an https name
- ✅ Error messages say "URL" in prose (identifiers stay `Url`). The dead empty-host check was removed from `UrlValidator` too

### ✅ Milestone 7: config and ops

#### ✅ Settings through `IOptions<T>`
- `ShortenerOptions` and `RateLimitOptions` (see milestone 6). `Storage` stays a pre-build read because it decides the registrations

#### ✅ Health endpoints
- `/health/live` runs no checks; `/health/ready` runs the checks tagged `ready`: `PostgresHealthCheck` (`CanConnectAsync`, registered only with Postgres, `timeout:` 2 s). Not rate-limited
- Endpoint tests: live and ready `200` with the in-memory store, live `200` and ready `503` with an unreachable Postgres (port 1), ready `200` against the Testcontainers Postgres
- Checked by hand with Compose: `docker compose stop postgres` gave live `200` and ready `503` (6 ms); `docker compose pause postgres` took 15 s until `.WaitAsync(ct)` enforced the timeout (then 2.1 s). See `LESSONS.md` section 22
- Decided: `CanConnectAsync` only, no table query per probe. A missing schema is a deploy-time problem (see the open item on failing fast)

#### ✅ Structured logging
- Console output as one JSON object per line (`appsettings.json`: `FormatterName: json`, scopes, UTC timestamps), so a stack trace stays one entry and properties stay searchable. `appsettings.Development.json` keeps the readable `simple` format for `dotnet run`; the Compose app sets JSON with `Logging__Console__FormatterName`
- One request line per request (`AddHttpLogging`, `CombineLogs`): method, path, status, duration, with the `TraceId` in scopes. No query string, headers or bodies, because shortened URLs can carry tokens. `/health/*` is skipped (`UseWhen`). EF Core command logs at `Warning` outside Development
- Tests with `FakeLogger` (`Microsoft.Extensions.Diagnostics.Testing` 10.11.0): one request line with path and status, no secret from the body or the query string in any log entry, no `/health` lines. Mutation-checked: adding `RequestQuery`, removing the `UseWhen` and removing `CombineLogs` each turned exactly one test red
- Later (milestone 8): App Insights through the Azure Monitor OpenTelemetry package; the `ILogger` code stays as it is

#### ✅ Graceful shutdown
- Deadline budget, shrinking inward (constants in `Program.cs`): Npgsql connect `Timeout` 3 s and `CommandTimeout` 3 s (set in code, whatever the connection string says), `HostOptions.ShutdownTimeout` 12 s, Compose `stop_grace_period` 15 s. `SchemaMigrator` overrides the command timeout to 5 minutes, because migrations can run longer than a request (not covered by a test)
- Measured with `docker compose pause postgres` and a POST in progress, then `docker compose stop app`: before, 10.56 s and exit `137` (SIGKILL, the client got a dropped connection). After: exit `0`, and the POST got `503`, on a new connection after 3.04 s and on a pooled connection hung mid-command after 9.18 s. A hung command fails after its timeout + about 6 s (5 s gave 11.17 s), source inside Npgsql not found. See `LESSONS.md` section 23
- The Dockerfile uses the exec-form `ENTRYPOINT`, so `dotnet` is PID 1 and gets SIGTERM. `ExpirySweepService` logs `Cancel signalled, exiting...` on stop

### ✅ Milestone 8: ship it (Azure stage 1)
Deploy steps are in `infra/README.md`; the stage 1 must-dos are under "Azure plan, in stages".

#### ✅ Forwarded headers
- `ForwardedHeaders` section: off by default, `X-Forwarded-For` only, `TrustAnyProxy` or `KnownNetworks`, `ForwardLimit` 1
- Five tests, each mutation-checked

#### ✅ Bicep and the first manual deploy
- Bicep in `infra/`, deployed by hand to `australiaeast`: the migrate job ran, both health checks return `200`, `POST /shorten` returns `201`
- A subscription budget with email alerts

#### ✅ Deploy from GitHub Actions
- `.github/workflows/deploy.yml` runs after `ci.yml` passes on a push to `main`, in this order:
  1. Build and push the image with `GITHUB_TOKEN`
  2. Sign in to Azure with OIDC (a managed identity, Contributor on the resource group only, federated credential for this repo's `main`)
  3. Start Postgres if it is stopped, or recreate it from the Bicep if it was deleted
  4. Run the migrate job
  5. Deploy the Bicep
  6. Smoke-test `/health/ready`
- First run verified end to end. A stopped Postgres made the migrate step fail and nothing was deployed, as designed

#### ✅ Postgres cost
- No free offer on this account. Cost analysis: B1MS about AU$0.037 an hour, storage about AU$6.30 a month even while stopped
- `stop-postgres.yml` stops the server nightly and `deploy.yml` starts it before migrating: about AU$7 to 10 a month instead of about AU$33
- `delete-postgres.yml` (run by hand) deletes the server for a long break, since storage can't shrink below 32 GB. The next deploy recreates it from the Bicep, then migrates. Verified: a delete, then a deploy that recreated it and returned ready `200`

#### ✅ Follow-ups
- The grace period (see the must-dos)
- `SSL Mode=VerifyFull` instead of `Require`. The container trusts Azure's certificate chain, and `/health/ready` returned `200` on the new revision
- The `org.opencontainers.image.source` label in the Dockerfile
- A flaky startup test: `ObjectDisposedException` about one run in eight, from a race inside `WebApplicationFactory` when `ValidateOnStart` failed inside `app.Run()`. Fixed by running `IStartupValidator.Validate()` before `Run`

### 🟡 Line 2, item A: click analytics
Done. Item D measured redirects with click recording on: p95 under 1 ms at 10,000 a second with the cache. Not measured: a run with recording off for a before-and-after (needs a switch in the code).

#### ✅ Click store
- `IClickStore`
  - `AddAsync(batch of DailyClicks)` adds (never sets) and sums duplicates of (code, day)
  - `GetAsync(code, start, end)` returns a `long` total, both ends inclusive
  - `start > end` or a count of zero or less throws, rejecting the whole batch before any write. An empty batch does nothing
- `InMemoryClickStore`: `AddOrUpdate` per key
- `PostgresClickStore`
  - The `AddClickStats` migration: `click_stats` with `"Code"`, `"Day"` (date) and `"Count"` (bigint), primary key `(Code, Day)`, FK to `links` with `ON DELETE CASCADE` (a swept code can be reused)
  - `AddAsync` is one raw-SQL upsert: `unnest` of three arrays, `JOIN links` (clicks for a vanished link are skipped), `GROUP BY`, `ON CONFLICT ... + EXCLUDED."Count"`. It is sent as a `FormattableString`, so the arrays are parameters
  - `GetAsync` is LINQ `SumAsync`
  - `PostgresFixture.ResetAsync` truncates both tables
  - The transient-error helpers moved to `PostgresExceptionExtensions` for both stores
- Tests: both stores pass the same 15 contract tests, and a Postgres-only test proves clicks for a code without a link are skipped and the rest counted
- Mutation-checked on the in-memory store. Read-then-write lost about 60% of 100,000 concurrent adds; set-instead-of-add and an off-by-one at either end of the range each turn a test red
- Decided: the in-memory click store is not pruned when the sweep deletes a link (Postgres cascades), so in development a reused code inherits old clicks. Accepted, since the in-memory store is for development only

#### ✅ `ClickRecorder`
- A bounded channel of `Click(Code, At)`. `Record` stamps the time with `TimeProvider`, so a click near midnight counts on its own day
- `FullMode = Wait` with `TryWrite` only. A full channel drops the new click without delaying the redirect, and each `false` increments `Dropped` with `Interlocked`. An exact count beats `DropWrite`, whose `TryWrite` returns `true` and drops silently
- Five unit tests: record, drop the new click when full, accept again after a read, 10,000 concurrent drops all counted, capacity below 1 throws
- Mutation: `_dropped++` instead of `Interlocked.Increment` turned the concurrency test red 3 of 3 times

#### ✅ `ClickFlushService`
- A `BackgroundService` that reads a `ChannelReader<Click>` (not the recorder, so tests use a plain channel)
- One loop on one thread, so a plain `Dictionary<(Code, Day), int>` needs no lock
  1. `await Task.WhenAny(tick, read)`
  2. Take what is queued: at most the `Count` snapshot, stopping at `MaxBatchSize` distinct entries
  3. Flush if the `PeriodicTimer` ticked or the batch is full
- A completed `tick` or `read` task is replaced after every wake-up. Otherwise `WhenAny` returns at once and the loop spins (an empty tick that was not re-armed pinned a CPU and hung the run)
- A failed flush (`StorageUnavailableException`) is dropped, not retried. At-most-once, because a retry after a commit whose reply was lost would double count. Its clicks are added to `Dropped`, and one warning is logged
- On stop the loop ends through the `while` check or, if a flush was cancelled, through `catch (OperationCanceledException) when`. A drain then takes and flushes what is left with its own 5 s budget
- `ClickOptions` (`Clicks` section): `Capacity`, `MaxBatchSize`, `FlushIntervalSeconds`
- Six tests with `SpyClickStore` and `FakeTimeProvider.AdvanceUntilAsync`
- .NET 10 trap: if `StopAsync` cancels before the `Task.Run` that starts `ExecuteAsync` has run, `ExecuteAsync` never runs (probe: `ExecuteTask` was `Canceled` in the full run; the drain test passed alone and failed in every full run). So the drain test first flushes one click by tick to prove the loop is live
- Mutations that turn a test red: `<=` at the cap, size-only flush, tick-only flush, no clear after a failure, counting entries instead of clicks, no empty check, no drain
- Survives: removing the `Count` snapshot. A hot key under constant writes never reaches the cap, so the take never ends. Not covered

#### ✅ Wiring
- `RegisterClicks` binds `ClickOptions` with `ValidateDataAnnotations` and `ValidateOnStart` (defaults in `appsettings.json`: 10000, 1000, 5 s)
- `ClickRecorder` is a singleton (a second instance would be a channel nobody reads), its `Reader` is registered as `ChannelReader<Click>`, and `ClickFlushService` as a hosted service
- `RegisterStorage` adds `IClickStore` (in-memory or Postgres)
- `HandleGet` calls `Record` only on a redirect, never on a 404 or 503
- Two endpoint tests in `ClicksEndpointTests` with `SpyClicksFactory`: a redirect flushes one click for its code, and a miss flushes nothing (red when `Record` moved above the null check)

#### ✅ Stats endpoint
- `GET /{code}/stats?start=&end=` returns `{ code, start, end, clicks }` (`StatsResponse`), dates as UTC `DateOnly`, both ends inclusive
- Each date defaults on its own: `end` to today (from `TimeProvider`), `start` to `end` minus 29 days (30 days inclusive). The response echoes the range, so a client sees the defaults applied
- Rules
  - `start > end` is a 400, which also covers a `start` after a defaulted today
  - At most one calendar year: `start >= end.AddYears(-1)`, not a day count, because a range across Feb 29 has 366 or 367 days. The cap is defensive. Daily rows behind the `(code, day)` key keep even a huge range cheap, but the cap bounds input and leaves room for raw events later
- The dates arrive as strings and are parsed with `TryParseExact("yyyy-MM-dd")`; an empty value counts as missing. Bound as `DateOnly?`, a bad date got a framework 400 (the developer page in Development, naming `Nullable<DateOnly>`) and `10/01/2026` was accepted as October 1
- An unknown code returns `clicks: 0` without a link lookup. A store outage returns `503`, as for the redirect
- Tests (`StatsEndpointTests`): the count over an explicit range, a missing code, two theories with a fake clock through `WithWebHostBuilder` (four ranges that resolve, four rejected with the rule in the message), and a `503` through `ExceptionClickStore`
- Mutations that turn a test red: `>=` at the cap, no cap, no order check, `-30` instead of `-29`, both dates defaulted together, `start` defaulted from today, no `503` catch, lenient `TryParse` instead of `TryParseExact`

### 🟡 Line 2, item B: Redis cache
Built and measured in item D: at 10,000 redirects a second the cache cut latency about 3x and the app's CPU by about one core. The hit rate was close to 100%, which reflects the test's uniform picks over 1,000 links, not real traffic.

#### ✅ `CachedUrlStore`
- A cache-aside decorator around the real `IUrlStore`. A read checks Redis, and a miss reads the inner store and fills Redis. `TryAddAsync` and the sweeper go straight to the store
- On only when `ConnectionStrings:Redis` is set (`RegisterCache`). The decorator asks for the inner store by its concrete type, because asking for `IUrlStore` would resolve to itself
- The key is `Cache:KeyPrefix` + code, and the value is the `LinkEntry` as JSON, in one `SET` with its TTL (a hash would need a second `EXPIRE`). So `IUrlStore.GetAsync` now returns `LinkEntry?`, not the URL
- The key's TTL is the smaller of `Cache:TtlSeconds` (3600) and the link's remaining lifetime. Every hit checks `ExpiresAt` against the app's `TimeProvider` at the stores' boundary (`now >= ExpiresAt`). The app's clock decides validity; Redis's TTL only frees memory
- "Not found" is not cached, and `POST /shorten` does not write to the cache. Links never change, so only expiry invalidates, and only clicked links take memory
- Reads use `JsonSerializerOptions.Strict` (.NET 10), so `{}` or a missing `Url` throws. A cached `null`, bad JSON and a failed Redis call are treated as a miss and fall back to the store (the counter tags them `error`)
- A `remaining <= 0` guard before the write was dead code (a mutation removing it survived) and is gone. `now` is read once, before the inner store, and the inner store only returns an entry whose `ExpiresAt` is after its own later clock reading, so `remaining` is always positive. Reading the clock again after the inner call would need the guard back, because Redis rejects a zero or negative TTL

#### ✅ Redis down
- `RedisConnection.Configure` sets `AbortOnConnectFail = false`, all three timeouts to `Cache:TimeoutMilliseconds` (200) and `BacklogPolicy.FailFast`
- Measured against a refused port: with only `asyncTimeout=200`, a read waited 5 s in the client's backlog (6 s for the call); with fail-fast it failed in 28 ms
- Catch both `RedisException` and `RedisTimeoutException` (the second derives from `TimeoutException`)

#### ✅ Counter
- `shortener.cache.reads` on the meter `Shortener.Cache`, tagged `hit`, `miss` or `error`, exactly one per `GetAsync`
- `ReadCacheAsync` returns one result per path. An expired entry is a hit, an unusable value or failed read an error. Failed writes are not counted
- Read it with `dotnet-counters monitor -n Shortener --counters Shortener.Cache`

#### ✅ Compose and tests
- Compose runs `redis:7` with `maxmemory 100mb`, `allkeys-lru` and no persistence
- Tests
  - `CachedStoreContractTests`: the whole store contract against the decorator with a real Redis
  - `CachedUrlStoreTests`, and the decorator around the Postgres store
  - `CacheEndpointTests`: the app with the setting (one miss then one hit), redirects with Redis down, no cache without the setting
  - `RedisConnectionTests`: the settings directly (in a timing test, fail-fast and the short timeouts hide each other)
- Mutations
  - Store, 11 of 12 red: never reading Redis, no expiry check on a hit, `>` at the boundary, no TTL cap, no TTL, either Redis catch rethrown, the JSON error rethrown, default JSON options, a null entry returned, connection errors not caught
  - Counter, 6 of 6. Registration, 5 of 5 after `CacheEndpointTests` and `RedisConnectionTests` were added
  - Survives: dropping `RedisTimeoutException` from the catches. The tests only produce a refused connection; covering it needs a stalled Redis (item E)

#### Azure option
- Azure Cache for Redis retires on 2028-09-30. Its replacement, Azure Managed Redis, starts near US$12 a month (third-party listing, unverified) and cannot be stopped. The plan is Redis as its own container in Container Apps

### ✅ Line 2, item D: load test
p99 measured, the bottleneck found, and the fix built and measured: a startup warm-up of 15,000 lookups brings a fresh app's p99 at 10,000/s (Redis off) from 115 ms to about 10 ms.

#### ✅ Tooling
- k6 v2.3.0 and dotnet-counters, with `loadtest/` (steps in `README.md`, "Load tests"). The app runs on the host (Release, Production) against Postgres and Redis from Compose under `-p shortener-load`
- One machine (8 cores, WSL) runs k6, the app and Docker, so the numbers compare runs with each other; they are not production capacity figures
- `dropped_iterations` came from stalls: during a 250 ms pause about 500 requests are due, the 50 pre-allocated VUs are busy, and k6 drops iterations while it creates more. More pre-allocated VUs would avoid it

#### ✅ Method
- All runs: 1,000 links created in `setup()`, codes picked uniformly, 0 failed requests in every run. Compare the `{ scenario:redirects }` lines only
- **Rising rates** (2,000, 5,000, 10,000/s for 60 s) on one app that stayed up between runs, so each run started warmer than the one before
- **Cold runs:** a freshly started app, then 10,000/s for 30 s at once. Used for the minimum-pool experiment (`PG_OPTIONS="Minimum Pool Size=N;Maximum Pool Size=90"`, N = 0, 50, 80), with the number of open connections read from `pg_stat_activity` at "ready" and 5 s later
- **Warm runs:** a freshly started app, 15 s at 1,000/s to warm it, then 10,000/s for 30 s. Used for the warm-up test and for the Redis comparison
- **Measurements:** k6 for latency; dotnet-counters `collect` to CSV for the `Npgsql` meter (pool use, requests waiting, connection and query time) and `Shortener.Cache`; btop for the CPU split while running; `ps -o cputimes` per process before and after the 10,000/s phase for CPU seconds (whole seconds per process, so the roughly 90 Postgres connections, each under a second, rounded to 0)

#### ✅ Results: rising rates on one app (2026-11-13)
| Rate | Redis | p50 | p95 | p99 | Notes |
|---|---|---|---|---|---|
| 2,000/s | off | 0.79 ms | 1.05 ms | 2.08 ms | About 2 requests in flight (rate × latency) |
| 5,000/s | off | 0.80 ms | 1.54 ms | 2.26 ms | Only the tail moves |
| 10,000/s | off | 1.45 ms | 4.15 ms | 8.35 ms | Machine 80% busy, app about 3 cores, Postgres about 0.3 |
| 10,000/s | on | 0.51 ms | 0.93 ms | 2.96 ms | Machine 53% busy, app about 2 cores; a 332 ms stall at the start as 1,000 fresh codes missed together |

- The knee is between 5,000 and 10,000/s without the cache: p50 doubled while the machine was 80% busy. Waiting grows steeply before 100% (about ρ / (1 − ρ) times the service time)
- The prediction before the Redis run was "no difference, Postgres is idle". Wrong: the cost of a database read sits in the app, not on the Postgres server

#### ✅ Results: cold start (Redis off, 10,000/s for 30 s)
| Minimum pool | Open at "ready" / 5 s later | p50 | p95 | p99 | Waiting (peak) | In use (peak) |
|---|---|---|---|---|---|---|
| 0 | 1 / 1 | 1.49 ms | 97 ms | 115 ms | 792 | 89 |
| 50 | 1 / 1 | 1.65 ms | 89 ms | 113 ms | 683 | 76 |
| 80 | 1 / 1 | 1.68 ms | 102 ms | 116 ms | 784 | 88 |
| 0, after a 15 s warm-up at 1,000/s | 90 after the warm-up | 1.22 ms | 2.93 ms | 4.86 ms | 0 | 17 |

- `Minimum Pool Size` does not open connections at startup: with 80, one connection was open (from the readiness check). It only stops the pool shrinking below N once N exist. Ruled out as the cold-start fix
- A cold app at full load builds a queue that feeds itself: slow first requests pile up, the pile keeps every connection busy, each query waits longer (p99 34 ms as the app saw it, against 8 ms warm), and the backlog does not clear in 30 s. k6 hit its 1,000-VU cap and dropped about 8,000 iterations
- A 15 s warm-up at 1,000/s removed almost all of it, paying the cold cost (its own p99 11 ms, max 205 ms) at a rate where it did no harm
- Connections were probably not the main cold cost: `setup()` sends bursts of 100 parallel POSTs, so the pool grows early in the cold runs too, and they stayed slow. The likely cost is the code warming up (.NET optimizing hot methods, first-use costs in EF Core and Npgsql). The warm-up run warmed both, so it does not separate their shares

#### ✅ Results: warmed apps, Redis off against on (10,000/s for 30 s)
| | Redis off | Redis on |
|---|---|---|
| p50 / p95 / p99 | 1.24 / 3.12 / 5.28 ms | 0.50 / 0.73 / 1.48 ms |
| max | 20.5 ms | 11.2 ms |
| App CPU | 87 s (about 2.9 cores) | 59 s (about 2.0 cores) |
| `docker-proxy` CPU | 28 s (Postgres port) | 14 s (Redis port) |
| `redis-server` CPU | n/a | 6 s (about 0.2 cores) |
| Measured total (Postgres not measured) | 115 s | 79 s |

- Warm, the cache still makes redirects about 3x faster and saves the app about 0.9 cores for 0.2 cores of Redis. A Redis `GET` on one shared connection costs the app far less than an EF Core query through the pool
- `docker-proxy` exists only because the test publishes ports; production has a network hop instead, so absolute numbers will differ
- Without warm-up, the cache was also hiding the cold-start problem; with warm-up its gain is the per-request saving alone
- What Redis costs besides CPU: a service to run (in Azure another container, or Azure Managed Redis from about US$12 a month, unverified), up to 100 MB of memory, and its failure modes (miss stampede on a cold cache; Redis down falls back to the pool-bound path). Worth it at this load, not for the current Azure traffic of one user

#### ✅ Bottleneck: the connection pool, made worse by a cold start (Redis off)
- Npgsql counters at 10,000/s from a cold start: 96 of 100 connections used, 818 requests waiting for one (`pending_requests`), a new connection takes 89 ms (p50) to 161 ms (p99) to open, a query takes 9 ms (p50) as the app sees it, and `prepared_ratio` is 0
- Postgres `max_connections` is 100, so the pool took every slot. `psql` got `too many clients already` during the run, and idle pooled connections kept the slots afterwards
- Little's law: 10,000/s × 9 ms ≈ 90 busy connections. Warmed, the same load needed 17. The fix is cheaper and warmer queries, not more connections
- The cache works because most redirects never need a connection. In a Redis outage the app is back to this behavior

#### ✅ The fix: warm-up before readiness
- Chosen over `Max Auto Prepare` (steady state, still a candidate later) because the cold start was the largest measured effect. `Minimum Pool Size` was tried and ruled out (see above)
- The idea: at startup the app runs its real lookup path a few hundred times, partly in parallel, before `/health/ready` reports 200. Bicep already gives Container Apps a readiness probe on `/health/ready`, so a replica gets no traffic until it is warm. It matters on every deploy, scale-out and scale-up from zero
- Decided:
  - The app listens at once, and `/health/ready` returns 503 until warm-up finishes
  - A separate hosted service calls `IUrlStore.GetAsync` with a code that does not exist (never cached, no click recorded). No new method on `IUrlStore`; with Redis on, DI resolves `CachedUrlStore`, so it warms both
  - The call count and the parallelism are configurable; start with about 20 in parallel (a warm app needed 17 connections at 10,000/s) and a few hundred calls, and let the cold-start run decide
  - On failure, report ready after a 15 s deadline and log a warning. One failing call can take up to 3 s (connect and command timeouts). Shutdown during warm-up ends it through the stopping token
- Tests first: ready 503 before warm-up and 200 after; the store is called the configured number of times (`CountingUrlStore`); with a failing store, ready after the deadline and one warning (`FakeTimeProvider`, `FakeLogger`); shutdown mid-warm-up ends cleanly. Check existing tests that expect ready 200, since they may now race the warm-up
- Prediction: a fresh app at 10,000/s with Redis off gets p99 near 5 ms instead of 115 ms. Partly wrong: with the first settings (300 calls) nothing changed; it took about 15,000 calls (see Result)
- `Max Pool Size` below `max_connections` (room for admin access and the migrate job) belongs to item C
- Progress:
  - ✅ `WarmUpService` written: `Parallel.ForEachAsync` with `Parallelism` as the cap; looks up `WarmUpService.Code` (`~warmup`, outside `CodeGenerator`'s alphabet); only lookups that succeed count, so a failed one is retried after a 50 ms pause on the fake-able clock; the deadline and shutdown cancel through one linked token, handled once outside the loop; `Ready` completes in `finally`. Settings: 300 calls, 20 in parallel, 15 s deadline
  - ✅ 13 service tests, with `GatedUrlStore` (held lookups, most in flight) and `FlakyUrlStore` (first N lookups fail). 12 of 13 mutations caught; the survivor (no filter on the retry catch) behaves the same, since the pause throws on the cancelled token
  - ✅ Registered: a singleton the host runs (`AddHostedService(sp => sp.GetRequiredService<WarmUpService>())`), so `WarmUpHealthCheck` (tagged `ready`, any store) reads the same instance; registering it twice gave two instances and a readiness that never turned 200. 5 endpoint tests: ready 503 while warming and 200 after, live 200, ready with `Calls` 0, redirects served while warming. Existing tests adjusted: the cache endpoint tests turn warm-up off (its misses inflated their counters), the Postgres readiness test waits for warm-up. The deploy smoke test retries `/health/ready` for about 60 s, which covers the 15 s deadline
  - ✅ Cold-start rerun (below); `Calls` raised to 15,000. The in-memory readiness test now waits for warm-up too
- Result. Each run: a fresh database and app, Redis off, minimum pool 0, maximum 90, 10,000/s for 30 s; warm-up time is from `/health/live` to `/health/ready`:

  | Calls | In parallel | Warm-up | p99 | max | Most waiting for a connection |
  |---|---|---|---|---|---|
  | none (earlier baseline) | | | 115 ms | | |
  | 300 | 20 | 0.2 s | 116 ms | 221 ms | 791 |
  | 1,000 | 20 | 0.2 s | 115 ms | 165 ms | 700 |
  | 3,000 | 20 | 0.4 s | 113 ms | 216 ms | 689 |
  | 7,000 | 20 | 0.7 s | 72 ms | 121 ms | 336 |
  | 10,000 (two runs) | 20 | 1.1 s | 13.8 ms, 29.3 ms | 66 ms, 110 ms | 0, 1 |
  | 15,000 (two runs) | 20 | 1.6 to 1.7 s | 9.4 ms, 11.9 ms | 79 ms, 113 ms | 0, 0 |
  | 15,000 | 90 | 1.5 s | 7.6 ms | 20 ms | 0 |

  - The number of calls is what fixes it, not the connections: 20 in parallel opens about 20, and a warm app used at most 23 at this rate; 90 only helped a little
  - The cliff lies between 7,000 and 10,000; 10,000 sits on its edge (the repeat was 29 ms), so the setting is 15,000 for margin. Expect a longer warm-up on Azure (network hops), still inside the 15 s deadline
  - Likely cause of the count (not measured): .NET recompiles a method optimized only after many calls and after startup settles, and a 0.2 s warm-up ends before that. Neither warm-up touched the HTTP pipeline, so the cold cost sits in the store path (EF Core and Npgsql)
  - Each probe during warm-up logs a "Health check warm-up ... Unhealthy" line; a couple per start

#### Not measured
- Latency with click recording off, for item A's before-and-after
- A realistic hit rate: more `LINKS` with `HOT_SHARE` (most links get few clicks, and each first click misses)
- Postgres CPU per run (the `ps` method rounds each connection to 0)

## Line 1: usable and deployed (v1)

**Definition of done:** the service runs at a public URL, survives a restart without losing data, rejects abusive input, has green CI that deploys on merge to `main`, and the README explains how to run, test and deploy it.

| # | Milestone | Done when |
|---|---|---|
| 1 | ✅ Finish the test suite | Done: retry path, store concurrency and endpoint concurrency, each mutation-checked (see "Already done") |
| 2 | ✅ CI | Done: build and test on every push and PR, a red run demonstrated, `build` required on `main`. Optional leftovers: a test-report step, pinning the runner image or SDK |
| 3 | ✅ Better codes | Done: `RandomNumberGenerator.GetString` replaces `Random`, with a chi-squared uniformity test. No test can detect a regression to `Random`, so consider enabling analyzer CA5394 |
| 4 | ✅ Expiry | Done: links have a TTL and expired links return 404; the read-time check, the measured sweep and `ExpirySweepService` (see "Already done"). Optional leftover: remove entries in place in `Sweep` |
| 5 | ✅ Persistence | Done: a Postgres `IUrlStore` (EF Core, Npgsql) behind the same contract tests as the in-memory store, `503` on an outage, EF migrations with a one-shot `migrate` step, and a Compose stack whose restart check kept a link. Optional leftovers: see "Open items", Persistence |
| 6 | ✅ Abuse protection | Done: rate limiting on `POST /shorten`, and a `BaseUrl` validated at startup for the self-link check and `shortUrl`. Optional leftover: set `AllowedHosts` to the real host |
| 7 | ✅ Config and ops | Done: settings through `IOptions<T>` (`Storage` stays a pre-build read because it decides registrations), health endpoints, graceful shutdown with a deadline budget, JSON logging with one line per request |
| 8 | ✅ Ship it | Done: stage 1 of the Azure plan below (Container Apps and Flexible Server from Bicep, deployed from GitHub Actions with OIDC, `migrate` as a Container Apps Job before each new revision, a post-deploy smoke test) |

### Schedule
| Week | Dates | Focus |
|---|---|---|
| 0 | Oct 16 | Milestone 1: finish tests |
| 1 | Oct 19 to 23 | Milestones 2, 3, 4: CI, better codes, expiry |
| 2 | Oct 26 to 30 | Milestone 5: persistence and contract tests |
| 3 | Nov 2 to 6 | Milestones 6, 7: rate limiting, config, health, logging, Dockerfile |
| 4 | Nov 9 to 13 | Milestone 8: deploy, smoke test, README. **v1 live** |

## Deployment shape (notes for milestone 8 and Line 2)
Decided before milestone 8. Stage 1 and the Redis cache are built; several API instances are not:
- **The API and the database are never one container.** Each runs in its own container with one main process, because:
  1. With a database inside each API container, instances would hold different links and could issue the same code twice. Uniqueness comes from one shared primary key (Line 2, item C).
  2. The API is stateless and replaceable, while the database needs a volume, backups and careful upgrades. Redeploying one must not restart the other.
  3. They use different resources and fail in different ways.
  4. Health checks, logs and limits stay meaningful per container.
- **v1 layout (decided):** Azure, with the API on **Container Apps** and **managed Postgres** (Flexible Server). Goal: learn deploying to Azure with a path to scale, not production-grade resilience; the only user is the owner. Rejected: Postgres as a Container App (container storage is ephemeral, and Azure Files over SMB is not POSIX: no `chmod` and no hard links, which Postgres needs; NFS needs the Premium tier and a VNet) and Postgres in Docker on a VM (cheaper, but all the operations work: patching, backups, tested restores, network security).
- **Several API instances:** N copies behind a load balancer, all on the same database. The `migrate` step runs once per deploy, not once per instance (the `EnsureCreatedAsync` race). During a rolling deploy the old version runs against the new schema for a while, so schema changes should be additive first (expand), with drops and renames in a later release (contract).
- **Redis** (Line 2, item B) is its own service, never inside the API container: a read-through cache in front of Postgres, a cache TTL that never outlives `ExpiresAt`, eviction allowed (a lost entry costs one read), and redirects must still work when Redis is down.
- Not covered yet: backup and restore tests. Forwarded headers and secrets were handled in stage 1 (see the must-dos below).

### Azure plan, in stages
Each stage adds one layer of a typical enterprise setup (edge, gateway, private network, compute), so each can be learned on its own, and the work can stop after any stage. Infrastructure is defined in **Bicep** from stage 1, so recreating or scaling is a small diff and nothing depends on portal clicks.

| Stage | What | Learns |
|---|---|---|
| 1 (milestone 8, v1) | Container Apps with public ingress, scale 0 to N; Flexible Server Burstable B1ms, public access with a firewall rule; images in GitHub Container Registry; GitHub Actions deploys with OIDC (federated credentials, no stored Azure secrets); the `migrate` step as a Container Apps Job; Log Analytics with a daily cap; a budget alert | the deploy pipeline, Container Apps revisions and jobs, Bicep |
| 2 | Container Apps environment in a VNet, Postgres with private access (no public endpoint) | private networking |
| 3 | API Management (Consumption tier) in front, subscription key required for `POST /shorten`, `GET /{code}` public; App Insights through OpenTelemetry (the `ILogger` code stays as it is) | gateway, products and keys, telemetry |
| 4 (optional) | Front Door with a WAF, app ingress only from Front Door | edge. The expensive tier: check prices, try it, then tear it down |

**Cost (approximate, East US pay-as-you-go, third-party figures; check the Azure pricing calculator):** Container Apps scaled to zero stays inside the monthly free grant (180,000 vCPU-seconds, 360,000 GiB-seconds, 2 million requests per subscription). Flexible Server B1ms is about $12 to $15 compute plus about $0.115 per GB-month of storage (32 GiB minimum, so about $16 to $19 in total); new free accounts can get it free for 12 months, and a stopped server pays only for storage (Azure restarts it after 7 days). Log Analytics: the first 5 GB per month are free, and the request logs are about 600 bytes each. GitHub Container Registry is free here (Azure Container Registry Basic would be about $0.167 per day). Expected: about $0 in the free year, otherwise mostly the database.

**Stage 1 must-dos, and how each was handled:**
- ✅ Container Apps ingress is a proxy, so `RemoteIpAddress` is the proxy's address. `ForwardedHeaders` with `TrustAnyProxy` (the app is reachable only through the ingress). Checked in production: 7 posts, each with a different forged `X-Forwarded-For`, all landed in one bucket (`429` after 5). Not yet shown: that the bucket is keyed on the client and not on the ingress (needs a second client IP; possible once stage 3 opens the ingress).
- ✅ `Shortener__BaseUrl` is built in Bicep from the environment's default domain, and the environment is Production; `shortUrl` comes back as the https address.
- ✅ The app's request budget assumes the host's grace period is above `ShutdownTimeout` (12 s). Bicep sets `terminationGracePeriodSeconds: 15` (the same as Compose); the deployed app reports 15.
- ✅ The connection string is a Container Apps secret, built in Bicep from a `@secure()` parameter that is read from an environment variable at deploy time.
- ✅ `POST /shorten` is open to the internet until stage 3, so the ingress allows only the owner's IP.

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
Milestone 8 is done; items A, B and D are built and measured (see "Already done").

1. **Line 2, item C (multi-instance).** Set Npgsql's Max Pool Size so that replicas × pool stays under the server's limit. The pool defaults to 100 connections per connection string; Postgres allows 100 in all and Azure B1ms about 50. Item D showed one instance with the default pool taking all 100. (A test that filled the pool locked a later test out; fixed with `ClearAllPools`.)
2. **Line 2, item E (failure injection).**
   - A stalled Redis, so a test covers the `RedisTimeoutException` catch that survives mutation today
   - Redis killed mid-run: the app falls back to the pool-bound path measured in item D
   - The cold-cache miss stampede, first (started Nov 15). Plan: single-flight per instance, so concurrent misses for one code share one database lookup (with N instances, up to N lookups per key; a Redis lock was judged not worth its complexity here)
     - Decided (Nov 15): inside `CachedUrlStore`. The shared unit is `inner.GetAsync` plus the Redis `SET`, so a cold key costs one query and one write, and Postgres is also shielded while Redis is down. It reads its own `now` for the TTL and passes `CancellationToken.None` to the inner store
     - The owner writes it. Traps: `GetOrAdd` can run its factory twice (store a `Lazy<Task>`); the shared lookup must not use one caller's token (each caller waits with `WaitAsync(ct)`); a failed lookup fails all waiters but is removed, so the next call retries; remove with `TryRemove(KeyValuePair)` so a newer lookup is not deleted
     - Progress (Nov 15): the five tests are stubbed in `CachedUrlStoreTests`. The first two (shared lookup, failure and retry) are written and red on `Gets`. Next: the cancellation test (cancel the caller that starts the lookup; `Gets == 1` is the red assert, `InFlight == 1` guards the token), then the last two, then the implementation
     - Tests first: 100 concurrent reads of one code with a held inner store reach it once; a failure reaches every waiter and the next call queries again; one cancelled caller does not cancel the others; different codes do not wait for each other; nothing is left in flight afterwards
   - Jittered TTLs: keys filled in the same second expire in the same second an hour later
3. **Redis in Azure**, as its own Container App (see "Azure option" under item B).

## Open items (not scheduled)
**Rate limiting**
- Rate-limit the missing-address warning (log once, or once a minute).
- A concurrency test for `TokenBucketLimiter`, and eviction of idle buckets.
- An upper bound on `TokenBucketOptions.Period` (a huge period could overflow the tick cast, see `LESSONS.md` section 20).
- Check whether malformed bodies bypass the limit (endpoint filters run after model binding).
- Limit 404s per client on `GET` against code enumeration.

**Persistence**
- Decide what a normal start does when the schema is missing: fail fast (for example via `GetPendingMigrationsAsync`) or keep the `500` on the first request. Readiness does not catch it: `CanConnectAsync` only proves the server accepts a connection.
- Build the shared test fixture's database with `SchemaMigrator` instead of `EnsureCreatedAsync`, so tests use the production schema path.
- The `57P01` test from the storage-outage entry.
- A `CancellationToken` for `SchemaMigrator`.
- CI could run the Compose stack as a smoke test.

**Expiry (optional, from milestone 4)**
- An integration test with the real store, the sweeper service and a fake clock (hosted services also run inside `WebApplicationFactory`).
- Remove entries in place in the in-memory `Sweep` instead of `Where(...).ToArray()` (about 2x faster when measured; check the docs on `Dictionary.Remove` during enumeration first).
- Assert on the sweep logs with a `FakeLogger` (the package is already in the test project for the logging tests).

**Validation (a future PR)**
- Request validation is hand-written in each handler (`UrlValidator`, `ExpiryPolicy`, the stats range). Consider a declarative approach, like zod in TypeScript: first check .NET 10's built-in Minimal API validation (`AddValidation()`, data annotations on parameters and request types, no new package; verify what it covers), then FluentValidation (a new dependency, ask first). Validation runs after binding, so neither fixes binding leniency on its own: `DateOnly?` accepted `10/01/2026`, which is why the stats dates are parsed with `TryParseExact`.

**Tidy-ups (optional)**
- Share the repeated `now` / `clock` / `store` setup in the store tests.
- Simplify `ExpiryPolicy` to one exit with a `lifetime` variable.
- Give `ShortenedUrlRequest.ExpiresInSeconds` an explicit `= null`.

## Design notes
- **Never-reissue policy for codes.** Reads never delete, so the sweep is what frees a code, and a swept code can be issued again. Accepted for the in-memory store. At scale the expected number of reissued old codes is about `old × new / 3.5 trillion` (28 at 10M and 10M, about 285,000 at 1B and 1B), so the persistent store needs a policy. Options: keep expired rows and only hide them (the table grows, nothing is reused), delete the row but record the code in a tombstone table, or counter-based codes (a Bloom filter of issued codes is the in-memory alternative). This is also the milestone 8 ID-allocation topic.
- **Migrations and locks.** A migration that needs an exclusive table lock can stall reads (see the lock queue in the Postgres lab), so review each new migration for that.
