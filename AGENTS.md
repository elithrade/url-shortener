# AGENTS.md

Instructions for AI coding agents working in this repository.

## Project
A URL shortener built from scratch with ASP.NET Core Minimal APIs (.NET 10). It is a **learning project** for system design, and the owner wants to write the core logic personally. See `README.md` for the API, `PLAN.md` for the roadmap and current milestone, and `LESSONS.md` for the owner's self-test flashcards. When a session produces a new lesson (a bug found, a trap hit), offer to add a Q&A card to `LESSONS.md` in the same format.

`LESSONS.md` is published to the owner's homepage: on a push to `main`, `.github/workflows/lessons.yml` exports it and opens a pull request there. The exporter rejects anything outside its format, with the line number:
- Sections are `## <n>. <title>`. Optional intro paragraphs go above a section's first card; nothing may follow its last card.
- A card is `<details><summary>question</summary>` on one line (`<code>` allowed in the question), the answer paragraphs, then `</details>`.
- Answers use paragraphs, `code` and **bold** only: no lists, quotes, tables, code blocks or links.
- A card's id comes from its question, so two questions must not reduce to the same words.

## Layout
```
Shortener.slnx
src/Shortener/            the web app (Program.cs and the folders below)
tests/Shortener.Tests/    unit and endpoint tests (xUnit, WebApplicationFactory), in folders that mirror src
Dockerfile, compose.yaml  image for the app and a local stack (Postgres, Redis as a cache, a one-shot migrate step, the app)
infra/                    Azure stage 1: main.bicep, main.bicepparam (reads secrets and personal values from environment variables) and README.md with the deploy steps
loadtest/                 k6 load test of `GET /{code}`; steps in README.md, "Load tests"
dotnet-tools.json         local tool manifest (dotnet-ef)
```

### `src/Shortener/`
All code stays in the single namespace `Shortener`.
- `Api/`: request and response records, `ShortenerOptions` and its validator.
- `Expiry/`.
- `RateLimiting/`: `IRateLimiter`, `RateLimitResult`, `RateLimitOptions`, `TokenBucketOptions`, `TokenBucket`, `TokenBucketLimiter`, `RateLimitKey`, and `RateLimitFilter` on `POST /shorten`.
- `Storage/`: `IUrlStore`, `IExpirySweeper`, `LinkEntry`, `IClickStore` with `DailyClicks`, and `InMemory/`.
- `Storage/Postgres/`: `LinkRecord`, `ClickStatRecord`, `ShortenerDbContext`, `PostgresUrlStore`, `PostgresClickStore`, `PostgresExceptionExtensions` (turns transient errors into `StorageUnavailableException`), `PostgresHealthCheck`, `SchemaMigrator`, `DesignTimeDbContextFactory` and `Migrations/`.
- `Proxy/`: `TrustedProxyOptions` and its validator, which configure `UseForwardedHeaders`.
- `Urls/`: `UrlValidator`, `CodeGenerator`.
- `Clicks/`:
  - `Click`.
  - `ClickRecorder`: a bounded channel. `Record` writes to it without waiting and counts rejected clicks in `Dropped`.
  - `ClickOptions`.
  - `ClickFlushService`: the consumer. It sums clicks by (code, day), flushes on a tick or at `MaxBatchSize`, adds failed batches to `Dropped`, and drains on stop.
- `WarmUp/`: `WarmUpOptions` (`WarmUp` section: `Calls`, 0 turns it off; `Parallelism`; `DeadlineSeconds`) and `WarmUpService`. The service looks up `WarmUpService.Code` (a code no link can have) `Calls` times, retries a failed lookup after a pause, and completes `Ready` when the calls finish or the deadline passes. It is registered as a singleton, and the host runs that same instance, so `WarmUpHealthCheck` (tagged `ready`) reads its `Ready`. `/health/ready` is 503 until warm-up ends.
- `Caching/`:
  - `CacheOptions`: `TtlSeconds`, `TtlJitterPercent` (each key's TTL is cut by a random 0 to that share, so keys filled together expire apart), `TimeoutMilliseconds`, `KeyPrefix`.
  - `RedisConnection`: the connection settings that keep a Redis outage cheap.
  - `CachedUrlStore`: a cache-aside decorator around the real `IUrlStore`, registered only when `ConnectionStrings:Redis` is set. It counts reads as hit, miss or error. Concurrent misses for one code share one lookup (single-flight): the inner read and the Redis write run once, without any caller's token, and each caller waits with its own.

### `tests/Shortener.Tests/`
Namespace `Shortener.Tests`. The test project has no `Program.cs` of its own. `WebApplicationFactory<Program>` reaches the app through the project reference.
- Store behavior lives in abstract contract classes (`UrlStoreContractTests`, `ExpirySweeperContractTests`, `ClickStoreContractTests`), each with an in-memory and a Postgres subclass.
- The Postgres tests need Docker running (Testcontainers, one shared container via `[Collection("postgres")]`).
- The Redis tests need Docker too (`[Collection("redis")]`, `RedisFixture`). They never flush Redis; each store or app gets its own key prefix from `RedisFixture.UniquePrefix()`. A multiplexer on `localhost:1` (built with `RedisConnection.Configure`) stands in for a Redis that is down.
- `GatedUrlStore` holds every `GetAsync` until `Release` and records `MaxInFlight`; `WaitForArrivalsAsync(n)` waits until n calls are held. `Fail(ex)` fails held and later calls instead. The gate opens once, for good. Never released, it is a stalled database (held calls still end on cancellation).
- With warm-up on, a test that expects `/health/ready` to be 200 waits for `WarmUpService.Ready` first; tests that count store reads or cache counters set `WarmUp:Calls` to 0.
- `FlakyUrlStore(n)` fails the first n lookups with `StorageUnavailableException`; with `int.MaxValue` it is a database that stays down.
- `CountingUrlStore` counts reads of the inner store and records their codes (`Codes`). `TestMeters.CreateFactory()` with a `MetricCollector` reads a counter for one store.
- `ShortenClientExtensions.PostShortenAsync` posts to `/shorten`, optionally with a forged `Host`, a `peer` (the address connected to the app, set through `RemoteIpStartupFilter`) and an `X-Forwarded-For` header.
- `Concurrently.RunAsync` (or `RunSyncAsync` for synchronous work) starts N tasks behind one gate and releases them together. Lambdas over a `ValueTask` must be `async i => await ...`.
- `FakeTimeProvider.AdvanceUntilAsync` advances step by step until a service acts. In .NET 10 a `BackgroundService` starts through `Task.Run`, so a first `Advance` can come before its timer exists.
- `SpyClickStore` records each flushed batch for `NextBatchAsync`/`TryNextBatchAsync`, and failed ones after `FailNext`. `SpyClicksFactory` runs the app with a `SpyClickStore` as `IClickStore` and `Clicks:MaxBatchSize` 1, so every click flushes at once.
- Log assertions use `FakeLogger` (`AddFakeLogging`, `GetFakeLogCollector`).

### `loadtest/`
- `redirect.js`: the k6 script; settings through `-e`.
- `run-app.sh`: runs the app on the host (Release, Production, rate limit raised; `REDIS=off` for no cache).
- `compose.ports.yaml`: publishes Postgres and Redis. Always use it with `-p shortener-load`, so the dev volume is untouched.

## Commands
- Build and test everything: `dotnet test` (run from the repo root).
- Run the app: `dotnet run --project src/Shortener` (listens on `http://localhost:5154`).
- Format C# with `csharpier`.
- Apply the schema: `Storage=Postgres ConnectionStrings__Shortener=... dotnet run --project src/Shortener -- migrate` (exits 0 or 1, never starts the server). The app itself never creates the schema.
- New migration: `dotnet tool restore`, then `dotnet tool run dotnet-ef migrations add <Name> --project src/Shortener --output-dir Storage/Postgres/Migrations`, and change the generated namespace to `Shortener`.
- Local stack: `POSTGRES_PASSWORD=<any> docker compose up -d --wait` (app on port 8080). Never commit a password; `.env` is ignored.
- Run `dotnet test` before saying a change is done, and report the result.

## Commit messages
- The **subject line is all lowercase**, in imperative mood, with no trailing period and at most 72 characters.
  - Good: `add retry-then-success endpoint test`
  - Bad: `Add retry-then-success endpoint test`
- Put an optional body after a blank line.
- Trailers such as `Co-authored-by:` are exempt from the lowercase rule. Keep them as they are.
- Do not commit or push unless the user asks you to. Never use `--no-verify` or rewrite history that has been pushed.

## Working style
- This is a teaching setup. For core logic (stores, concurrency, validation, retry loops), explain the concept and the trade-offs first, and let the user write the code. Then review what they wrote.
- Hand over boilerplate and plumbing (setup, config, scaffolding) directly to save time.
- When reviewing, say what is wrong and why it matters. Don't rewrite the user's code unless they ask.
- When you claim something about behavior, verify it. Run the code or the tests, and don't rely on memory.
- The owner prefers test-first (red, then green) and mutation checks of important logic. Run mutations in a scratch copy under `/tmp` (copy the repo without `bin`, `obj` and `.git`), never in the working tree, and create the copy before editing it (not in the same parallel step). Remove scratch folders and containers afterwards.
- The owner's AI credit is limited. Keep sessions short and replies concise, avoid large tool outputs and unnecessary experiments, and don't re-verify things already recorded as verified in `PLAN.md` or `LESSONS.md`.
- Never write personal account, cloud subscription or tenant details into the repo or its docs.

## Code and test conventions
- Nullable reference types are on. Prefer fixing the type or adding `[NotNullWhen]` over `!`.
- `IUrlStore.TryAddAsync` is the **only** atomic check-and-insert. Never write check-then-set (`Get` then `TryAdd`) in callers.
- The store keeps the canonical URL (`Uri.AbsoluteUri`). Don't store the raw input.
- Error messages describe the rule the client must meet. They must not reveal implementation details or echo user input.
- Endpoint tests use `AllowAutoRedirect = false`, assert the status before reading the body, and use unique URLs, because the in-memory store is shared within a test class.
- A test should be able to fail. For logic that matters, temporarily break the code (for example `<=` instead of `<`) and confirm that a test goes red.
- Keep `Microsoft.EntityFrameworkCore.Design` and the `dotnet-ef` tool on the EF Core version that the Npgsql provider requires (now 10.0.4). A higher version raised the app's EF Core and broke the test project build (CS1705).
- Never call `Environment.Exit` in code that tests also run (it killed the test host and hid every result). Return an exit code from `Program.cs` instead.
- Use `WithWebHostBuilder(... ConfigureServices ...)` to substitute fakes such as `AlwaysCollidingStore`. It builds a separate app with its own singletons (store, clock), so seed, call and read through that same derived factory, never the class fixture.
- Supply settings that `Program.cs` reads at startup with `UseSetting`, not `ConfigureAppConfiguration` (the latter is ignored).
- A database store's tests go in the shared contract classes, so every store proves the same behavior. A passing suite doesn't prove coverage: mutate the code to find what isn't tested.

## Do not
- Add frameworks or dependencies without asking.
- Create planning or notes files other than the ones already listed above, unless asked.
