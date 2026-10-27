# Lessons learned

Self-test flashcards from building this project. Read the question, answer it out loud, then open the answer. Each card comes from a real bug, experiment or review in this repo. For the review habit, redo a section every week or so and mark the cards you missed.

## 1. Concurrency and atomicity

<details><summary>Fifty threads call <code>Dictionary.TryAdd</code> with no lock. What goes wrong, and why does <code>TryAdd</code> not save you?</summary>

Links are lost, the same code is handed to two callers, and exceptions are thrown (the experiment gave `accepted=2294 stored=1778`, around 200 to 400 duplicated codes, and up to 411 exceptions). `TryAdd` is only atomic if the container is thread-safe. A plain `Dictionary` does several steps internally (find bucket, check, write, count, maybe resize) and threads interleave inside them.
</details>

<details><summary>What is a check-then-set race, and what is the fix?</summary>

`if (!Contains(k)) Set(k, v)`: two callers both see "free" and both write, so one silently overwrites the other. The check and the write must be one atomic operation: `TryAdd` under a lock, `ConcurrentDictionary.TryAdd`, Redis `SET NX`, or a database unique index.
</details>

<details><summary>Why does a <code>lock</code> around <code>TryAdd</code> not guarantee uniqueness once you run two instances?</summary>

Each instance has its own lock and its own dictionary, so both can issue the same code. The lock is per process. Uniqueness must be enforced by the shared store.
</details>

<details><summary>Can you <code>await</code> inside a <code>lock</code>? What do you do instead?</summary>

No, it doesn't compile. Either keep the critical section synchronous (return `ValueTask.FromResult`) or use `SemaphoreSlim.WaitAsync`.
</details>

<details><summary>What does <code>Interlocked.Increment(ref x)</code> return, and what is the trap?</summary>

The new value, atomically. The trap is incrementing and then reading `x` again: another thread may have incremented in between, so the comparison uses a number that isn't yours. Use the returned value.
</details>

<details><summary>How big should a critical section be? Where did the <code>ConcurrentBag</code> go in the experiment, and why?</summary>

As small as possible: only the shared state that needs protecting. The bag is thread-safe on its own, so it sits outside the lock, and threads wait less.
</details>

<details><summary>What is <code>System.Threading.Lock</code> (.NET 9+) and what does it change?</summary>

A dedicated lock type. `lock (_lock)` compiles to `EnterScope()` instead of `Monitor`. It's slightly faster, states intent, and prevents locking on the wrong thing. Semantics are the same (reentrant, no `await` inside).
</details>

<details><summary>Redis equivalents: <code>TryAdd</code>, <code>Interlocked.Increment</code>, TTL?</summary>

`SET key val NX`, `INCR`, and `EXPIRE`. Single commands are atomic because Redis runs commands one at a time. Multi-step logic isn't, which is why Lua scripts and transactions exist.
</details>

## 2. Random codes and collisions

<details><summary>How soon does the first collision arrive among random codes? Give the formula and a number.</summary>

About √(π/2 · N), where N = 62^length. Length 2 (3,844 codes): about 78. Length 7 (3.5 trillion): about 2.35M. Each extra character multiplies the keyspace by 62 but delays the first collision only by √62 ≈ 7.9×.
</details>

<details><summary>A collision happened at 2M links. Is the system broken?</summary>

No. A collision means one insert needs a retry. The failure is not detecting it. The per-insert chance is about n/N (at 100M links with 7 characters, roughly 3 in 100,000). Length controls how often you retry, and the atomic add controls correctness.
</details>

<details><summary>Same URL submitted twice versus two different URLs drawing the same code: which can you ignore?</summary>

The duplicate URL is not a collision: you may return the existing code (idempotency) or issue a new one. A code collision can never be ignored: keeping the first gives the second user a wrong link, and overwriting hijacks the first user's link.
</details>

<details><summary>Why is <code>bytes[i] % 62</code> on random bytes biased, and what are the fixes?</summary>

256 = 4×62 + 8, so 8 characters appear 5/256 of the time instead of 4/256 (25% more likely). Reject bytes ≥ 248 and redraw (rejection sampling), or use `RandomNumberGenerator.GetString`/`GetItems`.
</details>

<details><summary><code>Random</code> or <code>RandomNumberGenerator</code> for short codes, and why?</summary>

`RandomNumberGenerator` if codes must not be guessable: `Random` is predictable. `Random.Shared` is thread-safe. Never share a `new Random()` across threads.
</details>

<details><summary>With a 1-character code (62 slots), retry limit 5: why do you get 503 before the table is full?</summary>

A 503 means five attempts in a row collided, not "full". With k of 62 filled, a request gives up with probability about (k/62)⁵: about 3% at half full, 85% at 60 filled. The last slots are hard to claim (about 12 requests for the last one). It's also a capacity signal.
</details>

<details><summary>Why does the 503 test assert <code>Attempts == 5</code> and not just the status?</summary>

Many broken loops also return 503: 6 attempts, 1 attempt, 50 attempts. Only the count separates a bounded, correct loop from the others.
</details>

## 3. HTTP and API design

<details><summary>301 versus 302 for a shortener, and why?</summary>

The difference is caching. A 301 is cached by browsers and CDNs, so you lose click counts, can't honor expiry or deletion, and can't change the target. 302 sends every visit to you. Use 302 when you need analytics, TTL or revocation.
</details>

<details><summary>Does <code>GET /</code> reach the <code>/{code}</code> handler? What does that say about where to validate?</summary>

No. A route parameter needs a non-empty segment, so routing returns 404 before the handler. Validate where data can really be bad: the JSON body can be missing or empty, the route value can't.
</details>

<details><summary>Why a <code>ShortenRequest(string Url)</code> record and not a bare <code>string</code> body?</summary>

A bare `string` parameter binds from the route or query, not the body. A record gives a stable, extensible contract and a typed object. JSON binding is case-insensitive (`url` binds to `Url`), and responses are serialized as camelCase.
</details>

<details><summary>Which status for an unknown code, an expired code, and an invalid URL?</summary>

404 for unknown, 410 Gone (if you track it) for expired or deleted, and 400 for an invalid URL.
</details>

<details><summary>Why does the <code>Location</code> header appear on both the 201 and the 302?</summary>

Same header, different meaning: on 201 it's where the new resource lives (the short URL), on 302 it's where to go next.
</details>

## 4. URL validation and security

<details><summary>Allowlist or blocklist for schemes, and what does <code>Uri.TryCreate(..., Absolute)</code> accept that surprises you?</summary>

Allowlist (`http`, `https` only). `TryCreate` accepts `javascript:alert(1)`, `mailto:`, and on Linux `/etc/passwd` parses as an absolute `file:` URI, so the scheme check is the real defence.
</details>

<details><summary>Why reject user info (<code>https://google.com@evil.com</code>)?</summary>

The part before `@` is credentials, so the real host is `evil.com`. It's a phishing trick behind a trusted short domain, and credentials shouldn't end up in your database or logs.
</details>

<details><summary><code>Host.Value</code> versus <code>Host.Host</code>: what is the bug, and why is the self-link check weak anyway?</summary>

`Host.Value` includes the port (`localhost:5154`), `Host.Host` doesn't. Comparing it with `Uri.Host` never matches, so the self-link check silently fails with a port. The `Host` header is also client-controlled, so production should compare against a configured base URL. (Open gap in this repo: the test client has no port, so a test can't catch the regression yet. A client `BaseAddress` with a port would.)
</details>

<details><summary>How does <code>Uri.TryCreate</code> treat <code>""</code> and whitespace?</summary>

With `Absolute` it returns false and never throws. With `RelativeOrAbsolute`, `""` and whitespace return **true** (a valid relative URI), so don't use that for validation.
</details>

<details><summary>Is <code>HTTPS://example.com:80/a</code> normalized to drop the port?</summary>

No. The default port depends on the scheme: 80 for http and 443 for https. `https://example.com:80/a` keeps `:80`. Only `http://...:80` loses it.
</details>

<details><summary>What does normalization lowercase, and what must it leave alone?</summary>

Only the scheme and host. Paths and queries are case-sensitive (`/Abc` and `/abc` can differ). `Uri.AbsoluteUri` already does the right thing: store it, not the raw input.
</details>

<details><summary><code>AbsolutePath</code> versus <code>PathAndQuery</code> versus <code>AbsoluteUri</code>?</summary>

For `https://example.com/Path?x=1`: `/Path`, `/Path?x=1` and the full URL. Storing `AbsolutePath` made every link a relative redirect to the shortener's own host. Unit tests of the validator passed throughout, and only the endpoint test catches it.
</details>

<details><summary>Do nullable annotations (<code>string Url</code>) protect you from a missing JSON field?</summary>

No. They are compile-time hints. The serializer builds the record by reflection and passes `null`, so anything arriving from outside the process must be validated.
</details>

<details><summary>What should an error message say, and what must it not say?</summary>

It should state the rule the client must meet ("Url must not contain credentials"). It must not leak internals (library names, blocklists, hosts) or echo user input. Ask: "would this be in the API docs?"
</details>

## 5. C# language and framework

<details><summary>Why does <code>private const</code> fail at the top of a top-level-statements <code>Program.cs</code>?</summary>

Top-level statements are the body of a generated `Main`, so a `const` there is a local variable, and locals have no access modifiers (CS0116). Use `const` without a modifier, or a static class. Rule: no class, no access modifiers.
</details>

<details><summary>What type does <code>var</code> infer for <code>var s = new CollidingStore();</code>, and why does it matter?</summary>

The exact static type of the right side, `CollidingStore`, never an interface. That's why the test can read `s.Attempts` while the app sees only `IUrlStore`. Both hold the same instance. `IUrlStore s = ...` would not compile on `s.Attempts`.
</details>

<details><summary><code>Task</code> versus <code>ValueTask</code>, and what is a <code>CancellationToken</code> really?</summary>

`ValueTask` avoids an allocation when the result is already available (an in-memory store), at the cost of rules: await once, don't block, `AsTask()` before `WhenAll`. A token is a cooperative signal: it cancels nothing by itself, only callees that check it. A `Dictionary` lookup has nothing to cancel.
</details>

<details><summary>What is a <code>Span&lt;T&gt;</code>, and why use <code>static</code> lambdas with <code>string.Create</code>?</summary>

A stack-only view over memory someone else owns (no copy, no growth), so it can't be used in async methods or class fields. `string.Create` hands you a span over the new string's buffer to avoid an extra array and copy. `static` forbids captures so the lambda can't start allocating by accident.
</details>

<details><summary>What does <code>[NotNullWhen(true)] out Uri? url</code> do?</summary>

Tells the compiler that `url` is non-null when the method returns true, so callers need no `!`, and the method's own code is checked against the promise.
</details>

<details><summary>Why did <code>AddSingleton</code> compile in <code>Program.cs</code> but not in the test project?</summary>

The Web SDK's implicit usings include `Microsoft.Extensions.DependencyInjection`, the plain SDK's don't. Add the `using`.
</details>

<details><summary>Why did <code>services.AddSingleton(fakeClock)</code> not replace the app's <code>TimeProvider</code>?</summary>

`AddSingleton(instance)` infers the service type from the compile-time type of the argument. With `var clock = new FakeTimeProvider(...)` that is `FakeTimeProvider`, so the registration answers requests for `FakeTimeProvider` only. DI matches the exact requested type and doesn't treat it as a `TimeProvider` registration. The app asks for `TimeProvider`, so it still got `TimeProvider.System`. Measured with a probe test: `AddSingleton(clock)` resolved the fake as `False`, and `AddSingleton<TimeProvider>(clock)` resolved it as `True`. Always state the service type when substituting a fake (`AddSingleton<TimeProvider>(clock)`, `AddSingleton<IUrlStore>(store)`), and keep the variable typed as the concrete fake so the test can still call `Advance`. Later registrations of the same service type override earlier ones.
</details>

<details><summary>Do you still need <code>public partial class Program;</code> for <code>WebApplicationFactory</code>?</summary>

Not in .NET 10. The SDK makes it visible, and analyzer ASP0027 flags the old workaround. (It was needed in .NET 6 to 9.)
</details>

## 6. Testing

<details><summary>What does <code>PostAsJsonAsync(url, new StringContent(...))</code> send?</summary>

JSON of the `StringContent` object itself (`{"headers":[...]}`), so the server sees no `url` and returns 400. `PostAsJsonAsync` takes a value to serialize, and `PostAsync` takes a ready `HttpContent`. It compiles either way, which is why it's hard to spot.
</details>

<details><summary>How do you test a redirect with <code>WebApplicationFactory</code>?</summary>

Create the client with `AllowAutoRedirect = false`, assert the status first, then read `Headers.Location?.OriginalString`. A 302 has no body, so don't call `ReadFromJsonAsync` on it.
</details>

<details><summary>Test class lifetime in xUnit: what is setup, what is shared?</summary>

A new class instance per test, so the constructor is the `[SetUp]` and `IDisposable` is the teardown. `IClassFixture<T>` is created once per class, so the app and its singleton store are shared by all tests in the class. Use unique inputs, or `WithWebHostBuilder` for a fresh host.
</details>

<details><summary>You build a fake-store client, then call <code>_client.GetAsync</code> for the redirect. What happens?</summary>

404: `_client` is the fixture's client, talking to the default app and a different store. Use the same client for both requests, and name them so that they can't be confused (`collidingClient`).
</details>

<details><summary>How do you know a test can fail?</summary>

Break the code on purpose (`<=` for `<`, remove a `return`, swap `AbsoluteUri` for `AbsolutePath`) and confirm the right test goes red. Do it in a scratch copy. The checks here caught real gaps: a missing port in the test host, and tests that only asserted the status.
</details>

<details><summary>Which retry-limit cases sit on the boundary, and what does each prove?</summary>

With a limit of 5: 4 collisions then success returns 201 (the last allowed attempt can succeed), and 5 collisions returns 503 (the loop stops).
</details>

<details><summary>Real store or fake for a concurrency test, and at which level (store or endpoint)?</summary>

Use the **real** store: the aim is to prove its lock and atomicity, and a fake only tests the fake. Fakes are for forcing branches that are hard to produce (collisions). Test the invariant at the **store level**, where you control the contention: many callers on the *same* key must give exactly one `true`, and many distinct keys must all be stored. An endpoint test with 7-character random codes never hits the same code twice, so it can't show the same-code race. It still detects a missing lock through dictionary corruption (see the measured results below), and it also guards the wiring (singleton lifetime, handler plus store).
</details>

<details><summary>Why are races hard to catch in tests, and what do you do about it?</summary>

An unlocked bug only shows when threads actually overlap, and a fast operation can finish before the next task even starts. Force overlap with a starting gate, use many callers, loop inside each task for more interleavings, run it repeatedly, and always do the mutation check: remove the lock and see which tests fail (expect the sharp store test to fail consistently, and the endpoint test to be flaky or green).
</details>

<details><summary>You collect results from 1,000 concurrent tasks in a <code>List&lt;bool&gt;</code>. Does pre-sizing it (<code>new List&lt;bool&gt;(1000)</code>) make it safe?</summary>

No. `List<T>.Add` reads `_size`, may resize, writes the slot, then increments `_size`. Two threads can read the same `_size` and write the same slot, so items are lost, and a resize can throw. Measured over 300 trials of 1,000 concurrent adds: the default list threw in 8 and had `Count != 1000` in 298. The pre-sized list never threw, but **all 300 lost items**. Pre-sizing hides the exception and keeps the bug. The measuring tool had the same bug as the thing under test.
</details>

<details><summary>What is the best way to collect results from concurrent tasks in a test?</summary>

Don't share a collection. Let each task return its result and use `bool[] results = await Task.WhenAll(tasks)`, which keeps results in order with no shared state. If you must share, use a concurrent collection (`ConcurrentBag`, `ConcurrentQueue`). A collection local to one task needs no thread safety, because its awaits run one after another.
</details>

<details><summary>You removed the <code>lock</code> from the store. The "same key, 1,000 callers" test passes 29 of 30 runs. Why, and what test do you add?</summary>

The same-key race needs two threads to pass "is the key missing?" within the same few nanoseconds, so it's real but rare. Keep the test, because it states the uniqueness rule, but it can't be the only guard. Add a distinct-keys test (1,000 tasks, each looping 10 times over its own keys, then read every key back): every add is a write to shared structure (buckets, entries, resize), so corruption appears almost every run. Without the lock it failed 30 of 30.
</details>

<details><summary>Why assert both "every add returned true" and "every key reads back"?</summary>

A corrupted `Dictionary` can report success and still lose the entry. The first check catches wrongly rejected keys, and the read-back catches entries that were silently lost. Run the read-back sequentially after `WhenAll`, so no concurrency is needed, and a failure names the missing key.
</details>

<details><summary>What does <code>InvalidOperationException: Operations that change non-concurrent collections must have exclusive access</code> mean?</summary>

`Dictionary` detected that its own internal structure was corrupted by concurrent writes (inconsistent bucket chains) and threw instead of looping forever. The detection is best-effort. Other runs lose entries silently, so don't treat the exception as protection.
</details>

<details><summary>What makes a concurrency test trustworthy?</summary>

It must fail when the protection is removed. Remove the lock in a scratch copy and run the test many times (30), and measure the failure rate. A test that fails 1 time in 30 is a weak detector, and one that fails 30 of 30 is a sharp one. Use a sharp test for detection and a precise test to document the invariant.
</details>

<details><summary>With the store's lock removed, how often did each test fail (30 runs), and what does that teach about predicting?</summary>

Same key at the store level: 1 of 30. Distinct keys at the store level: 30 of 30. 200 concurrent POSTs through the endpoint: 28 of 30. I had predicted that the endpoint test would be flaky or green, and I was wrong. Sharpness depends on how many *writes* overlap, not on the level the test runs at: each request does real writes to the shared dictionary, so 200 released together corrupt it. Measure, don't guess.
</details>

<details><summary>In an endpoint test you POST <code>https://example.com/k5</code> and then <code>GET /k5</code>. Why 404?</summary>

`k5` is part of the posted long URL, and the short code is generated by the server and returned in the response body. Keep the pair from each response, `(i, status, code)` returned from the task, then `GET /{code}` and compare `Location` with request `i`'s URL. This also proves each code maps to its own request, and not another's.
</details>

<details><summary>Why does <code>Console.WriteLine</code> show nothing in an xUnit test, and what do you use?</summary>

xUnit captures the console, because tests run in parallel and a shared console would interleave. Inject `ITestOutputHelper` through the constructor (`public class MyTests(ITestOutputHelper output)`) and call `output.WriteLine`. `dotnet test` shows it for failing tests, or with `--logger "console;verbosity=detailed"`. Better still: assert the value instead of printing it.
</details>

<details><summary>What is wrong with <code>var expected = 100; chi += diff * diff / expected;</code>?</summary>

`expected` is an `int`, so `diff * diff / expected` is integer division and silently truncates. It looks right on the tidy example (100 / 100) and breaks on real data (an expected count of 16,129.03). Use `double` for the expected value and the running total, and test the helper on a hand-computed example (`[90, 100, 110]` against 100 gives exactly 2.0).
</details>

<details><summary>You make a test depend on a production constant (<code>public static string Characters</code>) so it can check the alphabet. What are the two problems?</summary>

A public, non-readonly static field can be reassigned from anywhere, which quietly breaks every generated code. And a test that asks the code under test what is correct can't catch a wrong alphabet. Keep the constant private, and duplicate the expected set in the test (a regex or a copy).
</details>

<details><summary>Which test data can go in <code>[InlineData]</code>, and what do you do for a <code>DateTimeOffset</code> or <code>TimeSpan</code>?</summary>

Only compile-time constants (numbers, strings, `null`, enums, `typeof`). `TimeSpan.FromDays(30)` and `now.AddDays(30)` aren't constants. Pass a number (seconds, or milliseconds for boundary tests) and build the value inside the test body, or use `TheoryData<...>` with `[MemberData]`. `null` works for a nullable parameter, and `int.MinValue` is allowed because it's a constant.
</details>

<details><summary>When is a <code>[Theory]</code> right, and when should you write separate <code>[Fact]</code>s?</summary>

A theory fits the same logic on different data: "advance the clock by X, is the link alive?" with rows for 0, 1 ms before, exactly at, and 1 ms after expiry. Separate facts fit different setups and assertions (never-expiring link, re-adding an expired code). If you'd need an `if` on a parameter inside the test body to make rows work, split it.
</details>

<details><summary>You tested the expiry policy and the store separately, and every test passes. What can still be broken?</summary>

The wiring in between. Mutation checks on the handler (store `null` instead of the computed expiry, or ignore the policy's 400) both left all 96 tests green. A feature has logic and wiring, and both need a test that fails when they're removed. Unit tests of the parts don't protect the endpoint that connects them.
</details>

<details><summary>A test depends on a side effect of one call (an eviction on read). How does the order of calls matter?</summary>

The call that triggers the side effect must come before the call that observes it. In the re-add test, `GetAsync` after `TryAddAsync` passed even with an evict-on-read bug: the add had already failed (the entry was still there), and the eviction happened afterwards. Swapping to read first, then re-add, made the same mutation fail. Same assertions, different order, completely different power.
</details>

<details><summary>Why do assertions like <code>Assert.False(string.IsNullOrWhiteSpace(error))</code> miss bugs?</summary>

They pass for any text, so a message saying "between 0 and 0" (from using `TimeSpan.Seconds`) went through. When a message states a rule clients rely on, assert a distinctive part of its content (`Assert.Contains("31536000", error)`).
</details>

<details><summary>A sweep test asserts only the returned count. Which bugs does it miss?</summary>

A count checks a quantity, not an identity. With groups of 5 expired, 5 live and 5 never-expiring entries, a sweep that removes the *live* group instead returns 5 too, and the test passes. Use different group sizes (3, 9, 9, 5) so a swapped group gives a wrong count, and read every entry back so the test can see *which* ones are gone.
</details>

<details><summary>Why can <code>GetAsync</code> not prove that <code>Sweep</code> removed an entry?</summary>

`GetAsync` already treats an expired entry as missing, so it returns `null` whether or not the sweep removed it. A mutation that counted entries without removing them passed every read-back assertion. Observe removal with something the read-time check doesn't mask: a second `Sweep()` must return 0 (an un-removed entry would be found again), or `TryAddAsync` on the code must succeed (an un-removed entry still occupies it).
</details>

<details><summary>A test helper skips an entry when <code>TryAddAsync</code> returns false. What is the risk?</summary>

The failed add vanishes from the returned list, and any assertion looping over that list passes vacuously, checking nothing. In this case a count assertion happened to catch it, but a reused helper would not. Make the helper assert its own setup (`Assert.True(await store.TryAddAsync(...))`) so a broken setup fails at the line that caused it.
</details>

<details><summary>How do you stop assertions from knowing how a test helper builds its data?</summary>

Have the helper return what it added, as a named tuple (`IReadOnlyCollection<(string Code, string Url)>`), and assert over that list. The assertions then don't repeat the URL format (`$"https://example.com/{prefix}/{i}"`), they can't drift when the helper changes, and `entry.Code` reads better than `Item1`.
</details>

<details><summary>You re-add a swept code and then call <code>Assert.Equal(1, store.Sweep())</code>, but it returns 0. Who is wrong?</summary>

The test. `Sweep()` returns how many entries it removed, and the re-added entry (expiring at +3 h, with the clock at +2.5 h) wasn't expired, so 0 is correct. The `1` was right only for the *first* sweep. Draw the timeline (when each entry expires, where the clock is) before asserting a count. Also read the entry back (`GetAsync` is not null), because a sweep that skipped everything would also return 0.
</details>

<details><summary>A "re-add after a sweep" test passes even if the store keeps the old entry. Why?</summary>

It re-added an entry that was already expired (the old expiry time, with the clock moved on), and with the same URL. It could assert only that `TryAddAsync` returned `true`. Re-add with a future expiry and a different URL, then check `GetAsync` returns the new URL: only the new entry can produce that answer.
</details>

## 7. Tooling and workflow

<details><summary>Do you need <code>git mv</code> to keep history?</summary>

No. Git detects renames by content similarity (about 50% or more). `git mv` equals `mv` plus `git add`. Commit moves separately from edits, so similarity stays high.
</details>

<details><summary>Why move to <code>src/</code> and <code>tests/</code> with a solution file?</summary>

A nested test project needed four `<Compile Remove>` lines, and the editor loaded only one project (no "add using" suggestions). A root solution file lists both projects, so `dotnet test`, the language server and CI all find everything.
</details>

## 8. Async and the thread pool

<details><summary>Why use a starting gate in a concurrency test?</summary>

`Task.Run` starts work as soon as a thread is free. A fast operation can finish before later tasks start, so there's almost no overlap and a missing lock goes unnoticed. A gate parks every worker, then releases them together.
</details>

<details><summary>You start 1,000 tasks that each call <code>barrier.SignalAndWait()</code> on a 16-core, 32-thread machine. What happens?</summary>

It practically hangs. The pool's minimum thread count equals the logical processors (32), and beyond that it injects only about one or two threads per second. Thirty-two tasks block waiting for 968 others that are queued behind them, because the blocked threads count as busy. Rule: never block pool threads waiting for something that needs other pool threads to run. `Barrier` needs all N to arrive, so keep N at or below the number of threads that can run.
</details>

<details><summary>Same 1,000 tasks, but with a <code>ManualResetEventSlim</code> gate. Does it hang? How much overlap do you get?</summary>

No hang: the gate depends on the test thread calling `Set()`, not on the other tasks, so there is no circular wait. But only about 32 tasks (the pool minimum) wait at the gate while the rest queue, so peak overlap is about 32 threads. It's still real contention.
</details>

<details><summary>What does <code>await</code> do to the thread when the awaited task isn't complete?</summary>

The compiler-generated state machine registers "run the rest of me when this completes" on the task, and the method returns. The thread goes back to the pool. The parked task is a small object that uses no thread. That's why 1,000 tasks can wait at an async gate (<code>await gate.Task</code>) without starving the pool.
</details>

<details><summary>Why create the gate as <code>new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)</code>?</summary>

Without the flag, `SetResult()` runs the waiting continuations inline, one after another, on the calling thread, which serializes the very work you wanted to overlap. With it, each continuation is queued to the pool separately.
</details>

<details><summary>Does the async gate let 1,000 tasks run truly simultaneously?</summary>

No. Execution is still limited by the pool's threads and the cores (about 32 at a time, 16 really parallel). What it removes is the need for 1,000 threads to *wait*: all 1,000 reach the starting line and become runnable together, without starvation or tuning. For more pressure, loop inside each task rather than adding tasks.
</details>

<details><summary>When is each gate appropriate: <code>TaskCompletionSource</code>, <code>ManualResetEventSlim</code>, <code>Barrier</code>?</summary>

`TaskCompletionSource` for many tasks and async test methods (the default choice). `ManualResetEventSlim` or `Barrier` for a handful of dedicated threads, or synchronous code. Keep the number of blocked workers at or below the core count.
</details>

## 9. Randomness and statistics

<details><summary>Why is <code>Random</code> wrong for short codes, if its output is statistically fair?</summary>

It is a deterministic formula running over hidden state: the same seed gives the same sequence (`new Random(42)` printed `41 8 7 32 10 16 44 31` twice). Seeing a few outputs lets an attacker recover the state and predict later codes, so they can enumerate other users' links. Fairness was never the issue. A histogram of `Random` looks identical to a secure generator's, and only predictability separates them. Use `RandomNumberGenerator` whenever someone benefits from guessing the result (links, tokens, reset codes, keys).
</details>

<details><summary>Does <code>RandomNumberGenerator</code> take a seed from the OS and then run a formula?</summary>

No. It is the OS's secure source, continuously fed with fresh unpredictable input, so there is no simple formula running forward for an attacker to reconstruct. Two runs are unrelated.
</details>

<details><summary>Why is <code>byte % 62</code> biased, and how does rejection sampling fix it?</summary>

256 = 4 x 62 + 8, so bytes 248 to 255 wrap around and land on indexes 0 to 7 a fifth time. `a` to `h` then appear 5/256 of the time instead of 4/256 (measured ratio 1.249). Discard bytes at or above 248 and draw again, so every index gets exactly four bytes. Library methods (`GetInt32`, `GetString`) already do this.
</details>

<details><summary>What is the simplest production replacement for <code>Random.Shared.Next(62)</code>?</summary>

`RandomNumberGenerator.GetInt32(62)` is a one-line swap, and `RandomNumberGenerator.GetString(alphabet, length)` is better for a whole code. Hand-writing the byte loop is a learning exercise, not something to ship.
</details>

<details><summary>Why is <code>GetInt32</code> per character so much slower than <code>GetString</code>?</summary>

Each call goes to the OS random source, so a 7-character code makes 7 calls. Measured for 1M codes: 4,953 ms for `GetInt32` per character, versus 726 ms for `GetString` and 98 ms for `Random`. At 40 writes per second none of it matters, but `GetString` is both fast and the simplest.
</details>

<details><summary>What is a chi-squared test, and how do you write one?</summary>

It measures how far observed counts are from the expected counts: the sum over all categories of (observed - expected)^2 / expected. For 62 characters (61 degrees of freedom) a fair generator scores about 61 give or take 11. Count characters, compute `expected = total / 62` as a `double`, loop and accumulate `diff * diff / expected`. The biased version scored about 46,000.
</details>

<details><summary>How do you pick a chi-squared threshold for a CI test?</summary>

101 is the p = 0.001 line, so about 1 fair run in 1,000 would fail by chance, which is too flaky. Choose a threshold far above anything a fair generator reaches but far below the bug you want to catch: 150 is about 8 standard deviations above the mean, so a chance failure is around one in a trillion, and the biased version still fails by a factor of 300. A single high reading (we saw 91.9 once) is not proof of a bug: rerun a few times.
</details>

<details><summary>Which generator regressions can your tests not catch?</summary>

Switching back to `Random` passes every black-box test, since it is statistically fair. Unguessability can't be tested from outputs. Protect it with an analyzer (CA5394 flags insecure randomness; enable it in `.editorconfig`), a comment at the call site, and review.
</details>

<details><summary>You remove a character from the alphabet. Which test notices, and which doesn't?</summary>

The uniformity test fails (the missing character never appears, so chi-squared explodes). The "every character is in the allowed set" test still passes, because everything generated is still allowed. The two tests cover opposite failures: unexpected characters, and missing or skewed ones.
</details>

## 10. Time, expiry and TimeSpan

<details><summary>Expiry has two jobs. What are they, and which one makes it correct?</summary>

Correctness: checking the clock on every read (lazy expiry), so an expired link never redirects. Cleanup: a background sweep that frees the memory of dead entries. Only the lazy check makes it correct. Without it, a link would keep redirecting until the next sweep, and with only lazy checks, links nobody visits again would leak memory. Terminology: "lazy expiry" here means the expiry is *evaluated at read time*, and nothing is removed on read (we deliberately don't evict, see the code-reuse card below). In Redis and many caches, "lazy expiration" instead means *deleting the expired key when it is accessed*, so say which one you mean.
</details>

<details><summary>Should an expired link return 404 or 410, and what does 410 cost?</summary>

404 says "I don't know this code". 410 says "this existed and was removed", which helps crawlers drop the URL and lets a person see "expired". It costs storage: you must remember the code after expiry. A tombstone (code plus expiry, about 40 to 50 bytes, with the URL dropped) still grows without bound, so bound it with a retention window (for example 30 days, then 404). It also leaks that a code was once valid, which helps someone scanning for codes. With a database it's nearly free, since the row stays until a cleanup deletes it. v1 uses 404.
</details>

<details><summary>Why keep an expired entry occupying its code instead of evicting it on read?</summary>

If a read evicts it, the code becomes free, a new link can randomly draw it, and anyone still holding the old link lands on the new owner's page. An expired entry stays until the sweep decides, and reads never release it. The test needs the read to come before the re-add, or an evicting implementation passes it.
</details>

<details><summary>Absolute or sliding expiry, and which did we pick?</summary>

Absolute: a fixed deadline stored as `ExpiresAt`. Sliding refreshes the deadline on every click, which turns every read into a write on the hot path and conflicts with caching later. Storing an absolute time (not "created plus TTL") also lets each link have its own lifetime, and changing the default later doesn't alter old links.
</details>

<details><summary>Why <code>DateTimeOffset</code> instead of <code>DateTime</code> for an expiry?</summary>

`DateTime` has a hidden `Kind` (`Utc`, `Local`, `Unspecified`), and `==`, `<` and `>` ignore it. Measured: `DateTime` Local 20:00 and Utc 12:00 on a UTC+8 machine, the same instant, compared equal as `False`. `DateTimeOffset` names one exact instant, and compares by the underlying UTC moment: 12:00+00:00 and 20:00+08:00 gave `==: True`. Use it for instants. `DateTime` is only for a calendar date or time of day with no zone.
</details>

<details><summary>UTC or local time for the logic, and what about input from clients?</summary>

UTC everywhere in the code (`GetUtcNow()`, never `GetLocalNow()` for logic). Convert to local only when displaying. A client-supplied time with an offset is fine, because `DateTimeOffset` compares instants. A time with no offset is ambiguous (the parser has to guess), so reject it or take a duration instead.
</details>

<details><summary>Why take <code>expiresInSeconds</code> (a duration) and not a timestamp or a <code>TimeSpan</code>?</summary>

The server computes `now + duration` from its own clock, which avoids wrong client clocks and missing time zones. Integer seconds are language-neutral and the unit is in the name. A `TimeSpan` field uses .NET's format (`"30.00:00:00"`): measured, it rejected `"PT1H"`, `"1h"` and `3600`, accepted a negative value, and accepted a duration of about 29,000 years, so you'd range-check anyway. Keep primitives on the wire and `TimeSpan` inside the code.
</details>

<details><summary>What validation does a client-supplied lifetime need?</summary>

A minimum, a maximum and a default. Without a minimum, zero or a negative value creates a link that is dead on arrival while the API returns 201. Without a maximum, absurd values are accepted (and a huge one can overflow date arithmetic into a 500). The message should state the range and not echo the input. Check the range before computing.
</details>

<details><summary>What is the C# type for a duration, and what is <code>TimeSpan.Duration()</code>?</summary>

`TimeSpan` (Java and Go call it `Duration`). The method `Duration()` is unrelated to the type name: it returns the absolute value of a span. A `DateTimeOffset` is a point on the timeline and a `TimeSpan` is the distance between two points. A `TimeSpan` has no months or years (use `AddMonths` on the date), stores 100 ns ticks, can be negative, and `TimeOnly` (not `TimeSpan`) is for a time of day.
</details>

<details><summary><code>TimeSpan.Seconds</code> versus <code>TimeSpan.TotalSeconds</code>?</summary>

`Seconds` is the seconds component (0 to 59), like the last part of `01:02:03`. `TotalSeconds` is the whole length in seconds. For 1 minute, `Seconds` is 0 and `TotalSeconds` is 60. Using `Seconds` made every range check reject all non-zero values, accept 0, and print "between 0 and 0" in the error message. The same applies to `Minutes` versus `TotalMinutes` and `Days` versus `TotalDays`. Compare `TimeSpan`s directly where you can.
</details>

<details><summary>What is wrong with assigning the default first, then returning early only inside the <code>if</code>?</summary>

If the early path doesn't `return`, control falls through and the later assignment overwrites the result: the requested expiry was silently replaced by the 30-day default. Compute one `lifetime` value first, validate it, assign the output once, and return once.
</details>

<details><summary>Is <code>TimeProvider</code> registered in an ASP.NET Core app by default?</summary>

No (checked on a fresh `dotnet new web`). Register it with `builder.Services.AddSingleton(TimeProvider.System)`; otherwise the first resolve fails with "Unable to resolve service". Tests replace it the same way as any service: `services.AddSingleton<TimeProvider>(fakeClock)`.
</details>

<details><summary>Which package gives you <code>FakeTimeProvider</code>, and how do you use it?</summary>

`Microsoft.Extensions.TimeProvider.Testing`, in the test project only. The namespace differs from the package name: `Microsoft.Extensions.Time.Testing`. It is a `TimeProvider`, so pass it anywhere. Give it a fixed start time. The default start is `2000-01-01T00:00:00+00:00`. `Advance(TimeSpan)` only moves forward. Declare the variable as `FakeTimeProvider`, not `TimeProvider`, or you can't call `Advance`.
</details>

<details><summary>Can you write <code>readonly const</code> or <code>static readonly</code> in <code>Program.cs</code>?</summary>

No. `const` is already read-only (and implicitly static on a class member), so `readonly const` is error CS0106. In a top-level-statements file those lines are locals of the generated `Main`, and locals allow only `const`: `static readonly` there fails with CS0106 too. Use `static readonly` for values that aren't compile-time constants (`TimeSpan`) and for public values shared across assemblies (a changed `const` is baked into consumers until they rebuild). PascalCase is the C# style for constants (`MaxAttempts`).
</details>

## 11. Background sweeping and pruning at scale

<details><summary>What does <code>null</code> mean in the request and in the store, and where does 365 days come from?</summary>

`expiresInSeconds` missing in the request means "use the default": 30 days, applied by `ExpiryPolicy`. `LinkEntry.ExpiresAt == null` in the store means "never expires": the store has no default, `GetAsync` skips the check and `Sweep` never matches it (`now >= null` is false). 365 days is the *maximum* a client may request, not a default. Through the API a link never has a null expiry, so that case exists only for direct store calls or a future permanent-link feature.
</details>

<details><summary>Is it worth deleting expired links, when the keyspace holds 3.5 trillion codes?</summary>

The keyspace (62^7 = 3,521,614,606,208) isn't the limit. Reasons to prune are cost (a few hundred bytes per record, so 1 billion records is hundreds of GB plus indexes and replicas), privacy (target URLs can carry tokens or personal data, and a TTL is a promise), and, for the in-memory store, memory that otherwise grows until the process dies. Not pruning is viable for a long time (old data is cold), so it's mostly cost and privacy and not speed.
</details>

<details><summary>Separate the three concerns that "pruning" bundles together.</summary>

Correctness (does an expired link stop redirecting? the check on read), reclaiming storage (the sweep or the database's TTL), and code reuse (may a freed code be issued again? a separate policy). Reads in this project never delete, so only the sweep frees a code.
</details>

<details><summary>Is deleting billions of expired rows a performance problem, and how do databases avoid it?</summary>

A naive `DELETE ... WHERE expires_at < now` is heavy (long transactions, index churn, replication lag). Native TTL moves it off your code (Redis `EXPIRE`, DynamoDB TTL, MongoDB TTL index; Cassandra TTL writes tombstones and depends on compaction). Or partition by expiry time and drop whole partitions, which is a cheap metadata operation. Native TTL deletion lags, so readers must still check the expiry, which is exactly the read-time check in `GetAsync`.
</details>

<details><summary>How big is the code-reuse hazard once expired codes are deleted?</summary>

The expected number of reissued old codes is about `old x new / 3.5 trillion`: about 28 at 10 million and 10 million, about 285,000 at 1 billion and 1 billion. It isn't attacker-controlled (codes are random), but stale links redirect to an unrelated page. Remedies: never reissue (a Bloom filter of every issued code, where a false positive just costs one extra retry, is about 1.2 GB for 1 billion codes at 1%, versus about 8 GB of stored codes), counter-based codes (never repeat, guessable unless scrambled), or a tombstone retention window (bounds storage but only delays reuse). Accepted for the in-memory store.
</details>

<details><summary>Why a separate <code>IExpirySweeper</code> and not a method on <code>IUrlStore</code>?</summary>

When written, only the in-memory store implemented it, on the assumption that a database store would use native TTL. Postgres has no native TTL, so once it was chosen its store needs a sweep too (a store with native TTL, such as Redis, would not). A separate small interface keeps `IUrlStore` lean, and it lets each store decide whether it has anything to sweep. Name it for the capability (`IExpirySweeper`), because "Async" is a method suffix by convention, not part of an interface name. Make `Sweep()` synchronous: it is CPU work under a lock, and an async signature over synchronous work is a small lie. Returning the count makes it testable and loggable.
</details>

<details><summary>One class implements <code>IUrlStore</code> and <code>IExpirySweeper</code>. Why not two <code>AddSingleton&lt;I, Impl&gt;()</code> lines?</summary>

Each registration of that form creates its own singleton, so the sweeper would clean a store that no request uses, silently. Verified: `AddSingleton<IStore, Mem>()` plus `AddSingleton<ISweeper, Mem>()` gave `same instance? False`. Register the concrete class once and forward: `AddSingleton<Mem>()`, `AddSingleton<IStore>(sp => sp.GetRequiredService<Mem>())`, and the same for `ISweeper` (`True`). A test that resolves both from `factory.Services` and compares them guards the wiring.
</details>

<details><summary>Is <code>BackgroundService</code> built into .NET, and how does its lifecycle work?</summary>

Yes: an abstract class in `Microsoft.Extensions.Hosting` (from the shared ASP.NET Core framework, no NuGet package). Override `ExecuteAsync(CancellationToken stoppingToken)` and register with `AddHostedService<T>()`. The host starts it with the app and cancels `stoppingToken` on shutdown, then waits up to `HostOptions.ShutdownTimeout` (30 s by default). A hosted service is a singleton, so it can take singletons through the constructor but not scoped services. Hosted services also start inside `WebApplicationFactory` test hosts.
</details>

<details><summary>What happens if a <code>BackgroundService</code> loop throws an unhandled exception?</summary>

By default the whole host stops: `BackgroundServiceExceptionBehavior` is `StopHost` (verified: the log shows "BackgroundService failed", then "host stopping", then the process exits). One bad sweep would take down the shortener. Wrap each iteration in `try/catch`, log, and keep looping, but let `OperationCanceledException` through, because cancellation is how the loop is told to stop.
</details>

<details><summary>What happens on Ctrl+C to a <code>BackgroundService</code> loop built on <code>PeriodicTimer</code>?</summary>

The first Ctrl+C (SIGINT) or a SIGTERM is a graceful shutdown: the host logs "Application is shutting down...", cancels `stoppingToken`, `WaitForNextTickAsync(stoppingToken)` throws `OperationCanceledException`, the loop catches it and ends, and the process exits cleanly (verified). The exception is the normal way the loop ends, not an error. A second Ctrl+C or `kill -9` gives your code no chance to run.
</details>

<details><summary>What does <code>PeriodicTimer</code> give you over a callback timer?</summary>

You `await` each tick in a loop (`while (await timer.WaitForNextTickAsync(ct))`), so ticks can't overlap: a sweep longer than the interval just delays the next tick. It has a constructor taking a `TimeProvider`, and `FakeTimeProvider.Advance` does drive it (verified: 59 s gives no tick, 60 s completes it). Missed ticks are coalesced into one, only one wait may be pending at a time, and disposing the timer ends a pending wait with `false`.
</details>

<details><summary>Why cap how many entries one <code>Sweep</code> removes, and what does the cap not do?</summary>

The cap bounds the work done in one lock hold (removals and the size of the copied key list), so the loop can call `Sweep` repeatedly and let requests run between batches. A burst of expiries is realistic: every link created with the 30-day default in the same hour expires in the same hour. But with a brute-force scan the cap doesn't limit the scan itself: each batch walks the dictionary again, so many batches mean more total work, with shorter pauses. A `PriorityQueue` ordered by expiry would give both. Start without a cap, measure, then optimize.
</details>

<details><summary>What are the traps in <code>_store.Where(e =&gt; now &gt;= e.Value.ExpiresAt).ToArray()</code>?</summary>

`Where` is lazy, so materialize the keys (`ToArray`) before removing anything from the dictionary. Read the clock once, not inside the lambda. `now >= null` on a `DateTimeOffset?` is `false`, so never-expiring entries are skipped without an explicit check (add a comment, or write `ExpiresAt is not null`). Keep the scan, the copy and the removals inside one lock. The `Remove` result can't be false there, so counting it is redundant.
</details>

## 12. Measuring the sweep, and choosing the collection

<details><summary>How do you measure the cost of a sweep in a way that means something?</summary>

Two numbers, not one: how long the sweep takes, and how long a concurrent reader is blocked. Use a throwaway console project and not a unit test (timings make flaky tests), in a Release build, with a warm-up run so JIT time isn't counted, and report the median of several runs. Put a reader on a dedicated thread (not the thread pool) that times each `GetAsync` and keeps the worst, and sweep from the main thread. Run several data shapes: nothing expired (pure scan cost), 10% expired (typical), all expired (worst case). Check the result is right (the removed count) so a fast wrong answer can't win.
</details>

<details><summary>The reader and the sweeper never share a lock in the experiment. How do they interact, and how do the threads coordinate?</summary>

The data-level interaction is the store's own private `_lock`: `GetAsync` and `Sweep` both enter it, so a read that arrives during a sweep waits at `lock` until the sweep leaves. The experiment's lifecycle uses a "ready" poll (wait until the reader has done 1,000 iterations), a `Volatile` stop flag, and `Thread.Join`, which also guarantees the main thread then sees the reader's final writes. Polling and sleeps are timing assumptions; a `ManualResetEventSlim` and a `CancellationToken` are the tidier equivalents. The reader's worst latency is always slightly larger than the sweep time, because it can only finish after the lock is released.
</details>

<details><summary>Your benchmark reports "memory 199 MB -> 2 MB" for a sweep that removed nothing. What went wrong?</summary>

The store was no longer referenced after the sweep, so in a Release build the JIT treats it as dead and the GC freed it before the "after" reading. Call `GC.KeepAlive(store)` after the last measurement. A number that looks too good deserves a second look before it goes in a table.
</details>

<details><summary>How does the sweep scale with the number of entries, and is O(n) acceptable?</summary>

Linearly, measured with `Dictionary` + one lock: about 8 to 11 ms per million entries to scan, 46 to 54 ms per million when everything expires (removing and copying cost about four times the scan). 1M entries: 11 ms none expired, 17 ms at 10%, 46 ms all expired. 5M all expired: 269 ms. A full scan is O(n) in the total number of entries, and at this scale (and with a sweep every minute) that is the correct, simple solution: a realistic load removes only a few thousand entries per sweep. It is not the only option (a `PriorityQueue` ordered by expiry would be O(k log n) for k expired entries), but you add that only when the measured number says the scan is a problem.
</details>

<details><summary>How long is a reader blocked while a lock-based sweep runs?</summary>

For the whole sweep. Worst read latency equalled the sweep time (41 ms at 1M all expired, 270 ms at 5M), against about 0.05 ms when idle, because the sweep holds the lock from the scan to the last removal. Average latency hides this: one stalled request is the cost.
</details>

<details><summary>What changed with a <code>ConcurrentDictionary</code> store, and what did it cost?</summary>

Measured on 1M entries with the same interfaces: the reader was never blocked (worst 0.01 to 0.25 ms), but the sweep was 3 to 7 times slower (56 ms scan-only against 10 ms, 152 ms all expired against 41 ms) and memory was about 10% higher (218 MB against 199 MB), because each entry is its own node object. Read throughput with no sweep: 1 thread equal (about 74 million reads/s); 2 threads 133 against 51 million; 4 threads 303 against 25 million; 8 threads 484 against 15 million. With the lock, adding threads makes it slower (73, 51, 25, 15 million), which is what contention on one lock looks like (not profiled). A concurrent collection trades slower, more allocation-heavy writes and removals for reads that never wait.
</details>

<details><summary>A benchmark shows 484 million reads/s against 15 million. Does that mean you should switch collections?</summary>

Not on its own. The loop does nothing except look up a key (about 14 ns), while a real request spends microseconds in Kestrel. At 4,000 requests per second the lock is held roughly 0.02% of the time, so contention is irrelevant at this load. The sweep stall (41 ms against a 10 to 50 ms p99 target) is the number that affects users. Check what a micro-benchmark actually exercises before drawing a conclusion from its ratio.
</details>

<details><summary>When should you reach for <code>ConcurrentDictionary</code>, and when keep <code>Dictionary</code> + <code>lock</code>?</summary>

Reach for it when many threads do independent single-key reads and writes and you want reads that never wait, or when you can't state what a lock would need to protect. It doesn't make compound operations atomic: a check-then-act across entries, a sweep that must be consistent with adds, `GetOrAdd` (its value factory can run more than once), and `Count`, `ToArray`, `Keys`, `Values` and `Clear`, which take every lock. Keep a plain `Dictionary` with one lock when you need a multi-step invariant, the load is modest, and you can write down exactly what the lock protects (it is simpler to audit, and the sweep is faster). Either way, measure, and re-run the concurrency tests: the "remove the lock" mutation no longer exists, so the equivalent bug is a non-atomic add.
</details>

<details><summary>How is <code>ConcurrentDictionary</code> built, so that reads never block?</summary>

(Read from the `dotnet/runtime` source on `main`, in `System.Private.CoreLib`; the .NET 10 release may differ in small ways.) All state sits in one `volatile Tables _tables` reference. `Tables` holds the bucket array (each bucket is the head of a chain of heap `Node`s with a `volatile _next`), an array of locks (default `Environment.ProcessorCount`, each guarding a section of buckets) and a count per lock. `TryGetValue` takes no lock: it reads `_tables`, reads the bucket with an acquire barrier, and walks the chain. `TryAdd` and `TryRemove` take the one stripe lock for the key's bucket, and `TryRemove(pair)` compares the value with `EqualityComparer<TValue>.Default` before unlinking. The enumerator is lock-free and safe alongside writes, but is not a snapshot: it may show changes made after it started. Operations that need a consistent whole (`Count`, `IsEmpty`, `ToArray`, `Keys`, `Values`, `Clear`, `CopyTo`) acquire all locks. The difference from one big lock is granularity: many small locks for writers and none for readers.
</details>

<details><summary>Why is a <code>ConcurrentDictionary</code> sweep slower even though it blocks no one?</summary>

Each `TryRemove(pair)` takes a stripe lock, walks the chain, compares the value, and unlinks a node, where the old sweep took one lock for everything. The enumeration follows node pointers spread across the heap, where a `Dictionary` scans a contiguous array. That fits the timings, but the split between the causes was not profiled. A slower sweep on a background thread is a good trade if it removes the stall for readers.
</details>

<details><summary>Does <code>ToArray()</code> allocate a new block of memory, and can it run out of memory on a huge collection?</summary>

Yes, it copies into a new array. Here that is 16 bytes per *expired* entry (one `KeyValuePair<string, LinkEntry>` is two references, and the strings and entries are not copied): measured 15.3 MB at 1M all expired and 76.3 MB at 5M, and only the expired entries are copied, so a sweep of mostly live data copies little. The store itself costs about 200 bytes per entry (199 MB for 1M, 977 MB for 5M), so the temporary array adds at most about 8% in the worst case, and the store runs out of memory long before the array does. The first sweep in a process allocated up to 2.5 times that (one-time LINQ warm-up, observed and not explained). An array over about 85,000 bytes (roughly 5,300 pairs) lands on the Large Object Heap, which only a gen2 collection reclaims; no gen2 GC happened in these runs, though each measurement started after a forced collection. An array can't exceed about 2.1 billion elements anyway.
</details>

<details><summary>How do you sweep a <code>Dictionary</code> without the temporary array?</summary>

Remove while enumerating: `foreach (var e in d) if (expired) d.Remove(e.Key);`. Measured on a replica: 0 bytes allocated and about 2 times faster than `Where().ToArray()` then `Remove` (25 against 49 ms at 1M all expired; 165 against 275 ms at 5M). It did not throw on .NET 10. I recall that `Remove` was made safe during enumeration in .NET Core 3.0, but check the documentation before relying on it, since older .NET Framework versions throw `InvalidOperationException`. Keep it inside the same lock.
</details>

<details><summary>Why does a batch cap (<code>Where(...).Take(10_000).ToArray()</code>, repeated) make the sweep slower?</summary>

Every batch restarts the enumeration from the beginning of the dictionary and walks past the slots already removed and the live entries, so the total work is O(n x batches). Measured: 91 ms against 49 ms at 1M all expired, 1.35 s against 275 ms at 5M all expired, and 1.5 s against 76 ms at 5M with 10% expired. The cap does bound the temporary array and the pause per call, but without a cursor (or an ordered structure such as a `PriorityQueue`) it multiplies the scan cost. The per-batch pause was not measured.
</details>

<details><summary>Is the first measurement in a process reliable?</summary>

No. The same sweep allocated 29 MB on its first run and 15 MB on the second, and 188 MB against 76 MB at 5M, then matched the replica exactly. Warm up, repeat, and compare later runs, and when two supposedly identical measurements differ, resolve it before reporting either.
</details>

<details><summary>Can you remove entries from a <code>ConcurrentDictionary</code> while enumerating it?</summary>

Yes: the enumerator is safe against concurrent modification. Use `TryRemove(pair)` (the overload that takes a <code>KeyValuePair</code>), which removes the entry only if it still holds that value, so a concurrent replacement isn't removed by mistake. The enumeration is not a snapshot, so a sweep may or may not see entries added after it started, which doesn't matter for a sweep.
</details>

## 13. A background service: the loop, shutdown and testing it

<details><summary>How often does the host call <code>ExecuteAsync</code>, and why does the service need a timer inside it?</summary>

Once, when the app starts (verified: a hosted service with no loop printed "ExecuteAsync called (call #1)", returned after 300 ms, was never called again, and the host kept running). The host only knows how to start and stop a service, and has no idea of "periodic". `ExecuteAsync` is the `Main` of a long-running task: if it returns, the service is finished and the host does not restart it. So "every 60 seconds" is a loop you write inside that one call.
</details>

<details><summary>Why a <code>PeriodicTimer</code> loop, and not <code>Task.Delay</code> in a loop, a <code>System.Threading.Timer</code>, or a scheduler library?</summary>

`Task.Delay` in a loop drifts (the interval is 60 s plus the work), a callback `Timer` can overlap ticks when a run is slow, and Quartz or Hangfire are right for cron schedules, persistence or many jobs but far too heavy for one housekeeping loop. `PeriodicTimer` gives sequential awaited ticks with no overlap, coalesces missed ticks into one, ends on cancellation or dispose, and takes a `TimeProvider`, so it is testable.
</details>

<details><summary>Which exception do you catch when the app stops, and why not just check <code>stoppingToken.IsCancellationRequested</code> in the loop?</summary>

`OperationCanceledException` (`TaskCanceledException` derives from it). Catch it with a filter, `when (stoppingToken.IsCancellationRequested)`, so only a shutdown cancellation counts as normal. A check inside the loop can't replace it: the loop spends almost all its time parked in `await timer.WaitForNextTickAsync(stoppingToken)`, which throws the moment the token is cancelled, so the check never runs. Without the token, shutdown stalls until the next tick. A check followed by an await can also be overtaken by cancellation between the two lines, and the exception from the wait closes that gap. It is thrown once, at shutdown.
</details>

<details><summary>Where does the per-sweep <code>try/catch</code> go, and what happens if it wraps the whole loop?</summary>

Inside the loop, around the `Sweep()` call, with no `return`: log and let the loop continue (`catch (Exception ex) when (ex is not OperationCanceledException)`). If the `try` wraps the whole `while`, or the catch returns, one failed sweep ends `ExecuteAsync`, the host does not restart it, and nothing sweeps again until the process restarts, with a single error line in the log. A separate outer `catch (OperationCanceledException)` around the loop handles shutdown.
</details>

<details><summary>What does the host do if an <code>OperationCanceledException</code> escapes <code>ExecuteAsync</code> during a graceful shutdown?</summary>

It ignores it (verified: a clean exit with code 0 and no error logs). So `StopAsync` succeeds either way and can't tell you whether your catch works. `BackgroundService.ExecuteTask` can: `IsCompletedSuccessfully` means the loop ended through your catch, and a cancelled or faulted task means the exception escaped.
</details>

<details><summary>Does <code>BackgroundService.StartAsync</code> run <code>ExecuteAsync</code> up to its first <code>await</code> before returning?</summary>

Not in .NET 10 (I told you it did, and I was wrong). The source runs it as `Task.Run(() => ExecuteAsync(stoppingToken))` and returns a completed task immediately, so the loop starts a moment later on another thread. Earlier versions ran it synchronously until the first await. Consequence for tests with a fake clock: if you `Advance` straight after `StartAsync`, the timer may not exist yet, so its first tick is due a full interval after the *new* time and nothing ever fires. The symptom was a `Calls=0` failure, with the service still running and waiting.
</details>

<details><summary>How do you test a loop that starts on another thread, with a fake clock?</summary>

Don't guess a delay. Advance one interval at a time and wait briefly for the first sweep, repeating a few times: `for (attempt < 10) { clock.Advance(interval); if (await sweeper.WaitForCallAsync(500 ms)) return; }` then `throw` *after* the loop. From the first sweep on, the timer exists. (A `throw` placed inside the loop gave up after the first attempt, which is exactly the attempt the helper exists for.) This helper also works for the failure test, because the fake signals before it throws.
</details>

<details><summary>What is a <code>SemaphoreSlim</code>, and how does a test use it as a signal?</summary>

A counter you can await. `Release()` adds a permit, and `await WaitAsync(timeout)` takes one, waiting without blocking a thread if there is none, and returns `false` on timeout. Used as a lock it starts at 1 (one thread at a time). Used as a signal it starts at 0: the fake sweeper rings it on every call, and the test waits for a ring. Counting means a call that happens before the test starts waiting isn't lost. Rules: signal *before* throwing so the test can see failed calls; consume one permit per expected call (a leftover permit from call 1 would satisfy a wait for call 2 and make the test pass for the wrong reason); always give the wait a timeout so a broken service fails instead of hanging. Unlike `lock`, `WaitAsync` can be awaited.
</details>

<details><summary>How long should a test wait for something that should happen, and for something that should not?</summary>

Long (5 s) for an expected event, because it returns the moment the event happens, so it costs nothing and a slow machine can't cause a false failure. Short (about 100 ms) for an expected silence, since you can only wait a limited real time to show nothing happened. A silence check can give a false pass but never a false failure, so it can't make the suite flaky. A 100 ms wait for an expected call is a flaky test.
</details>

<details><summary>A single "advance 59 s: nothing; advance 1 s: a sweep" check passed against a 90-second interval. Why?</summary>

Coincidence of phase: after the helper's two 60 s advances the fake clock stood at 120 s, and the next 90 s tick (at 180 s) was due exactly 60 s later, so "59 s: nothing, then 1 s: a sweep" happened to fit. The next 90 s tick would be at 270 s, so the second pair (ending at 240 s) fails. Repeat the pair three times and the coincidence is impossible. Mutate to several wrong values (60 hours, 30, 59, 61, 90, 120 s) and don't trust a test that you only ran against the first mutant that came to mind.
</details>

<details><summary>Your shutdown test passes against a correct service, but hangs forever against one that ignores the stop token. How do you fix the test?</summary>

`StopAsync(CancellationToken.None)` waits as long as it takes, and with a fake clock no tick ever arrives to end the loop, so the test hangs (verified: killed by my 60 s limit) and stalls CI until the job's global timeout. Pass a token that expires, `new CancellationTokenSource(TimeSpan.FromSeconds(5))`: `StopAsync` gives up waiting, `ExecuteTask` isn't complete, and the assertion fails after about 5 s. A hang is worse than a failure, so run mutation checks under a hard time limit (`timeout 60 dotnet test`).
</details>

<details><summary>What are we testing when a service is given a fake sweeper, and why not the real store?</summary>

The service's own responsibilities: when it sweeps (every interval, not before), that it survives a failed sweep, and that it stops cleanly. The sweep logic is already covered by the store tests. A fake isolates the service (a failing test can only be the service's fault) and lets you force a failure the real store never produces. It is a spy: it counts calls, can be told to throw on a chosen call, and signals each call.
</details>

<details><summary>Is a test that passed 15 times in a row reliable?</summary>

It's evidence, not proof. For tests that involve threads and timing, run them 15 to 20 times (`for i in $(seq 1 20); do dotnet test --no-build --filter ...; done`) and also run the mutations. The three service tests passed 20 of 20 runs, and each mutation failed them.
</details>

<details><summary>A test failed after 514 ms and the message says "never swept", yet the helper waits up to 5 s. How do you read that?</summary>

Compare the elapsed time with the timeouts in the code. A failure far faster than the total wait means the code gave up early (here a `throw` sitting inside the helper's retry loop, so it threw after the first 500 ms attempt), not that the service was slow. A test that passes against the correct code and fails against each mutant tells you the test itself is right.
</details>

<details><summary>How do you test that two interfaces share one singleton, and how do you know the test isn't vacuous?</summary>

Resolve both from the app's real container and use `Assert.Same` (reference equality, with a clearer failure than `Equal`, which falls back to `Equals` and is also reference equality for a class with no override). `_factory.Services` is the root provider: touching it builds and starts the host (verified: the sweep service was already running, with no request made). Use `GetRequiredService<T>()`, which throws if nothing is registered. The first draft used `GetKeyedService<T>(key)`, which looks only at *keyed* registrations, so it returned `null` for both and `Assert.Same(null, null)` would have passed against the broken registration too. Prove such a test by mutating: with `AddSingleton<IUrlStore, InMemoryUrlStore>()` and `AddSingleton<IExpirySweeper, InMemoryUrlStore>()` it fails with "Values are not the same instance: Expected `InMemoryUrlStore { }`, Actual `InMemoryUrlStore { }`". Same type, different objects, which is the point. A test that passes both ways proves nothing.
</details>

<details><summary>What is the difference between <code>AddSingleton</code> and <code>AddHostedService</code>?</summary>

`AddSingleton` registers a service that something *asks for* (created on first resolve). `AddHostedService<T>` registers an `IHostedService` that the *host calls* at startup and shutdown, whether or not anything depends on it. In a `WebApplicationFactory` host the probe listed two `IHostedService` registrations: `ExpirySweepService` and ASP.NET's own `GenericWebHostService`. So the sweep service runs inside every endpoint test, against the real clock unless the test replaces it, which is harmless for tests that finish in milliseconds.
</details>

## 14. Analyzer warnings, logging and `.editorconfig`



<details><summary>The editor shows a warning on a line but <code>dotnet build</code> shows none. Why, and how do you reproduce it?</summary>

Many analyzer rules are off or only a hint in the default build, while the editor shows hints. Build with all of them on to see them: `dotnet build -c Release -p:AnalysisMode=All --no-incremental`, and filter the output for the file. The rule id (CA2007, CA1873 and so on) tells you what it wants.
</details>

<details><summary>What is CA2007 (consider calling <code>ConfigureAwait</code>), and should you act on it?</summary>

It is written for libraries: `ConfigureAwait(false)` stops a continuation being forced back onto a caller's synchronization context (a UI thread, old ASP.NET), which can deadlock. ASP.NET Core has no synchronization context, so in an app it changes nothing; Microsoft's guidance is to turn it off for application code. Put `dotnet_diagnostic.CA2007.severity = none` under `[*.cs]` in a repo-root `.editorconfig`.
</details>

<details><summary>What do <code>indent_style</code> and <code>indent_size</code> in <code>.editorconfig</code> do, and why add exceptions?</summary>

They set the default indentation for editors, and `csharpier` reads them too. Make 4 spaces the default (`[*]`), but keep `csproj`, `slnx`, `yml` and `json` at 2, because the existing files use 2 and editors would otherwise indent new lines inconsistently. Check first: this repo had no tab-indented files.
</details>

<details><summary>Which logging mistakes do CA2254, CA2253 and CA1873 flag?</summary>

CA2254: the message template must not vary between calls, so `LogInformation($"{n} swept")` is wrong, because every call is a different message and structured logging can't group them. Use `LogInformation("Swept {Count}", n)`. CA2253: a placeholder name shouldn't be only digits (`{1}`), which also looks like a `string.Format` index. CA1873: measured in a scratch project, it fires on *any* log call with arguments that isn't guarded by `IsEnabled` (a local `int`, a `string` parameter, a method call, all of them), and not on calls with no arguments, the exception-only overload, or an `IsEnabled`-guarded call. The reason is that arguments are put in a params array (and value types boxed) even when the level is off.
</details>

<details><summary>What is the idiomatic fix for those logging rules?</summary>

`[LoggerMessage]` source-generated methods: make the class `partial`, declare `[LoggerMessage(Level = LogLevel.Information, Message = "Swept {Count} entries")] private partial void LogSwept(int count);`, and call it. The generator validates the template at compile time and checks `IsEnabled` before formatting, which clears CA1848, CA1873, CA2253 and CA2254 together. An `Exception` parameter is passed to the logger as the exception, so the message needs no placeholder for it. It also compiled once the explicit `_logger` field was removed and only the primary-constructor parameter `logger` remained, so the generator accepts that (I had said it needed the field). For a log line that runs once a minute, silencing CA1873 in `.editorconfig` would also have been defensible.
</details>

## 15. Redis: commands, expiry, persistence, memory and eviction

Hands-on lab in a `redis:7` container. Items marked "docs" or "from memory" were not tested here.

<details><summary>What does <code>SET a 1 NX EX 30</code> do, and how does it map to <code>TryAddAsync</code>?</summary>

`NX` means "only if the key does not exist" and `EX 30` is a 30-second TTL (`PX` is the same in milliseconds). It replies `OK` when the key was created (`true`) and `(nil)` when the key existed (`false`, a collision), and the check, the write and the expiry are one atomic command, so `GET` then `SET` is never needed. A failed `NX` leaves the old value and its TTL untouched (verified: `SET d 1 EX 100`, `SET d 2 NX` gave `(nil)`, and `GET d` still returned `"1"`). `XX` is the opposite (only if it already exists).
</details>

<details><summary>What do <code>TTL</code> -1 and -2 mean?</summary>

-1: the key exists and has no expiry (a never-expiring link). -2: no such key. An expired key and a never-created key look the same to a client (`GET` gives `nil`, `TTL` gives -2), which matches "expired means missing".
</details>

<details><summary>You overwrite an expiring key with a plain <code>SET c 2</code>. What happens to the TTL, and why does it matter?</summary>

It is discarded (verified: `TTL c` returned -1). A code path that overwrites an expiring link would silently make it permanent, with no error. So the store should only write with `NX` (we have no update path), and a real overwrite needs `KEEPTTL` (from memory, not tested here).
</details>

<details><summary>What do <code>EX 0</code> and <code>PX -5</code> do, and what does that say about <code>TryAddAsync</code> with a past <code>ExpiresAt</code>?</summary>

Both are errors (`ERR invalid expire time in 'set' command`, verified), not a silent no-op. A past or zero TTL can't be stored, so the contract should say `ExpiresAt` must be in the future and throw `ArgumentOutOfRangeException` otherwise: returning `false` would look like a collision (the endpoint would retry 5 times and answer 503), and returning `true` without storing would claim a link exists when it doesn't. Edge cases: `ExpiresAt == now` counts as past, and `PX` takes whole milliseconds, so under 1 ms remaining rounds to 0. `PX 1` returns `OK` but the key is gone before you can look.
</details>

<details><summary>How does Redis remove expired keys, and does <code>GetAsync</code> need its own <code>now &gt;= ExpiresAt</code> check?</summary>

Two ways (docs): passive (a key is checked when it is accessed, and an expired one is treated as missing) and active (periodically Redis samples some keys that have a TTL and deletes the expired ones). Verified here: 1,000 keys with a 500 ms TTL that nobody read were gone within 3 s, so the active part works. The passive part is what guarantees a read never returns an expired key, so no read-time check is needed. Because the active part is sampling, an expired key can sit in memory a while, but it can't be seen.
</details>

<details><summary>Why did <code>PTTL e</code> return -2 on the first call after <code>SET e 1 PX 1500</code>?</summary>

Typing the second command in an interactive shell took longer than 1.5 s, so the key had already expired. Script short-lived experiments (`docker exec redis-lab sh -c '...; sleep 0.5; ...'`) so that typing speed isn't part of the test.
</details>

<details><summary>You killed a Redis container, restarted it, and the key was gone. Why was that test meaningless?</summary>

`docker run --rm` with no volume deletes the container and its filesystem when it exits, so the "restart" created a brand-new empty Redis. To test Redis's persistence the data directory must be outside the container: `-v redis-lab-data:/data` (the image writes `dump.rdb` and the AOF files in `/data`), and no `--rm`. In deployment, `/data` needs a volume too.
</details>

<details><summary>With Redis's default persistence, what survives <code>docker kill</code> and what survives <code>docker stop</code>?</summary>

The defaults (read from the server) were `save 3600 1 300 100 60 10000` (a snapshot after 1 change in an hour, 100 in 5 minutes, or 10,000 in a minute) and `appendonly no`. `docker kill` (SIGKILL) gives Redis no chance to run code, so everything since the last snapshot was lost (a `SET` acknowledged with `OK` a few minutes earlier was gone). `docker stop` (SIGTERM, then SIGKILL after 10 s) made Redis write a final `dump.rdb`, and the key survived. The volume only stores what Redis writes, and it doesn't make Redis write.
</details>

<details><summary>What does AOF (<code>--appendonly yes</code>) change, and what can <code>appendfsync everysec</code> still lose?</summary>

Redis logs every write command (Redis 7 keeps a directory `appendonlydir` with a base file, an incremental log and a manifest). A `SET` acknowledged before `docker kill` was still there after the restart. `appendfsync` defaults to `everysec` (verified): the log is flushed to disk about once a second, so a machine crash or power loss can lose about the last second of acknowledged writes (docs may say up to 2 seconds if an fsync is slow, check). I believe a killed Redis process loses nothing that it had already handed to the OS, which fits the result but isn't proven here. You lose the newest writes, and the log stays ordered, so it's loss, not corruption. `always` fsyncs every write (nearly nothing lost, slower), and `no` leaves it to the OS.
</details>

<details><summary>Does replication make <code>fsync</code> faster?</summary>

No. They protect against different failures. `appendfsync` bounds what one machine loses if its process or OS dies. A replica keeps a copy on another machine, which helps when the disk or host is gone, but replication is asynchronous by default, so a replica can lag and lose the last writes at failover.
</details>

<details><summary>You start Redis with <code>--appendonly yes</code> on a volume that already has a <code>dump.rdb</code>. What happens to the data in it?</summary>

It isn't loaded. Verified: after `SET old yes`, `docker stop`, then a new container with AOF on the same volume, `GET old` returned `nil`, although `dump.rdb` (103 bytes, written at the stop) was still in `/data`. With AOF enabled Redis loads the AOF (a fresh, empty one) and ignores the RDB. There is no error. So choose the persistence mode before real data exists. Enabling AOF on a running instance with `CONFIG SET appendonly yes` should build it from the current dataset (from memory, not tested here).
</details>

<details><summary>How do you measure memory per key, and what did 100,000 links cost?</summary>

(`used_memory` after minus `used_memory` before) divided by the key count, where "before" is read from a fresh Redis (an empty one already uses about 1 MB). Result for a 7-character key, a 40-character value and a 30-day TTL: (15,958,344 − 987,000) / 100,000 = **149.7 bytes per key**, about three times the 47 bytes of payload. `MEMORY USAGE k000001` said 104, so it doesn't count everything (the expiry table entry is part of the gap, my guess). The .NET store measured about 200 bytes per entry, so they are the same order. At 1 billion keys that is about 150 GB before replicas, longer URLs and fork headroom, and about 1.5 GB at 10 million.
</details>

<details><summary>What does a TTL cost per key in Redis?</summary>

About 24 bytes more per key (100,000 keys: about 2.4 MB more than the same keys without `EX`), around 16% of the per-key cost. My guess is the separate table of expiries (not verified). We keep it, because every link expires and Redis then deletes expired keys by itself.
</details>

<details><summary>Redis hits <code>maxmemory</code>. Does it write the extra data to disk, like an OS pagefile?</summary>

No. Everything lives in RAM, and the RDB and AOF files are only for recovery after a restart. At the limit, `maxmemory-policy` decides. Verified with `maxmemory 3mb` and 100,000 `SET`s: with the default `noeviction`, 13,584 succeeded and 86,416 failed with `OOM command not allowed when used memory > 'maxmemory'` (reads kept working); with `allkeys-lru`, all 100,000 returned `OK`, `DBSIZE` ended at 13,464, and the first key was gone, so about 86,500 keys had been deleted silently, after clients were told `OK`. With no limit (the default is 0), Redis grows until the OS kills it.
</details>

<details><summary>Which eviction policy fits a store, and which fits a cache?</summary>

A store wants `noeviction`: a refused write is a loud error you can map to 503, while eviction deletes links people were given. A cache wants an eviction policy such as `allkeys-lru`, because evicting is the point and a lost entry costs only a re-read from the source of truth. The same Redis behaves very differently depending on this one setting, and a managed Redis may default to eviction, so check it. Note that every one of our keys has a TTL, so the `volatile-*` policies would act like `allkeys-*` for us.
</details>

<details><summary>Why did we choose Postgres as the store and Redis as a cache in front of it?</summary>

Redis is priced in RAM (about 150 GB per billion keys) and with AOF `everysec` can lose about a second of acknowledged writes, but the links are the data. Postgres keeps them on disk, commits through a write-ahead log, enforces uniqueness with a primary key, and can later hold users with real foreign keys. Redis then sits in front as a read-through cache of hot links (a few links get most of the clicks, so the cache stays small), where eviction or loss costs a re-read, not data. It's not "Redis can't be a store": at the right scale it can. Redis has native TTL, but Postgres doesn't, so the Postgres store still needs the sweeper. The `IUrlStore` interface is the seam that keeps both choices open.
</details>

## 16. Postgres: atomic insert, concurrency, dead tuples, locks and durability

Hands-on lab in a `postgres:17` container. Items marked "from memory" or "believed" were not tested here.

<details><summary>What does <code>INSERT ... ON CONFLICT DO NOTHING</code> return on a collision, and how does it map to <code>TryAddAsync</code>?</summary>

The command tag reports rows affected: `INSERT 0 1` for a new code, `INSERT 0 0` for a collision (the first number is a legacy object id, always 0). In .NET that is the return value of `ExecuteNonQuery` or EF's `ExecuteSql`: 1 is `true`, 0 is `false`, with no exception. The plain `INSERT` of an existing key throws instead (`duplicate key value violates unique constraint "links_pkey"`, SQLSTATE `23505`), which you would catch and map to `false`. That is control flow by exception, slower and noisier, and the message contains the constraint name and the key, so it must never reach an API response. This is the design choice between EF `Add` plus `SaveChanges` (exception path) and raw SQL (row count).
</details>

<details><summary>50 connections insert the same code at once. What happens, and why does the code need no lock?</summary>

Exactly one `INSERT 0 1` and 49 `INSERT 0 0` (verified), one row stored, with the winner arbitrary (`u3`). With the plain insert: 1 success and 49 unique-violation errors. The primary key's unique index decides, inside the database. Caveat: 50 `docker exec` processes don't start at the same instant, so one clean run is evidence, not proof (our unlocked in-memory test also failed only 1 run of 30).
</details>

<details><summary>Session A has inserted a key in an open transaction. What does session B do when it inserts the same key, and why?</summary>

B hangs (verified). B can't yet know what its insert means: if A commits, B has a collision, and if A rolls back, the key never existed. So B waits for A's transaction to end. In `pg_stat_activity` B is `active` with `wait_event_type = Lock` and `wait_event = transactionid`, and A is `idle in transaction` with `wait_event = ClientRead` (waiting for the client's next command). Verified results: after A's `COMMIT`, B printed `INSERT 0 0`; after A's `ROLLBACK`, B printed `INSERT 0 1`. `pg_blocking_pids(pid)` shows who is blocking whom. A transaction holds all the keys it has inserted, and `pg_stat_activity` shows only its last statement. In an app, a request that opens a transaction and then waits on something slow makes others creating the same code hang, so keep transactions short.
</details>

<details><summary>You <code>DELETE</code> 500,000 of 1,000,000 rows. Why does the table stay the same size, and what is a dead tuple?</summary>

Verified: the delete took 199 ms on an idle server, and the table stayed 73 MB (110 MB with indexes), `n_dead_tup` was 500,000, `n_live_tup` 500,000, and `count(*)` 500,000. Postgres never overwrites a row in place. A delete ends the row version, and sessions with older snapshots may still need to see it, so the old version stays in the file as a **dead tuple** until vacuum frees its space. Queries don't see dead tuples. Compare the in-memory store, where the memory is freed at once.
</details>

<details><summary>What does a plain <code>VACUUM</code> do with that space, and what is <code>VACUUM FULL</code>?</summary>

Plain `VACUUM` makes the space reusable inside the file and does **not** shrink it (verified: still 73 MB; inserting 500,000 new rows left the table at 73 MB, with no growth, while the indexes grew, 110 to 125 MB in total). So a table's size follows its peak row count, and later inserts reuse the space as long as vacuum keeps up. Autovacuum runs in the background by default (verified: `n_dead_tup` was already 0 when I ran `VACUUM`, which took 4 ms). `VACUUM FULL` rewrites the table to give disk space back (verified: about 1.6 s here, the indexes shrank to 110 MB in total) and needs an exclusive lock on the table while it runs.
</details>

<details><summary>Session B holds an open read transaction, and <code>VACUUM FULL</code> is started. Does a plain read that starts afterwards wait?</summary>

Yes, and it is not B that blocks it (verified): a read started before `VACUUM FULL` returned in about 190 ms, since reads don't block reads. `VACUUM FULL` waited for B's read lock until B committed (about 5 s). A read started while `VACUUM FULL` was waiting queued behind it and took about 4.8 s, finishing 11 ms after `VACUUM FULL` did. `pg_blocking_pids` showed the chain: the read was blocked by `VACUUM FULL`, which was blocked by B. Postgres queues later requests behind a waiting exclusive lock, so one `idle in transaction` session plus one command that needs an exclusive table lock can stall every read. I expect schema-changing migrations to behave the same way (inferred from the lock mechanism, to verify before our first deploy).
</details>

<details><summary>Is a reader blocked while the sweeper deletes 500,000 expired rows, and should the delete be batched?</summary>

Measured with 4 clients doing primary-key lookups for 12 s on a 1M-row table (one run per scenario): baseline 374,078 reads, average 0.128 ms, worst 4.9 ms. During one `DELETE` of 500,000 rows (380 ms with readers running): 366,782 reads, average 0.131 ms, worst 6.1 ms, 3 reads over 5 ms. During 50 batches of 10,000: 381,221 reads, worst 3.9 ms, but the delete took 4,255 ms in total, about 11 times longer. So readers aren't blocked (readers see the old row versions, unlike the in-memory lock where a read waited for the whole sweep), and batching only costs time at this size. Decision: one `DELETE` per sweep, no batch cap. Why batching is slower is not investigated (a guess: each batch finds its rows again and commits separately). Not covered: scans by `expires_at`, concurrent inserts, replication and write-ahead-log volume, tables 100 times larger.
</details>

<details><summary>What does Postgres lose on <code>docker kill</code>, compared with Redis?</summary>

With the defaults (read from the server: `fsync`, `synchronous_commit`, `full_page_writes` and `autovacuum` all `on`), a committed row survived the kill and a row inside an open `BEGIN` did not (verified). A commit waits until its write-ahead-log record is on disk, so an acknowledged write is not lost, unlike Redis AOF `everysec` (about a second) or Redis snapshots (everything since the last one). At restart the log said `database system was not properly shut down; automatic recovery in progress`, then `redo starts` and `redo done` (under a millisecond here), then `ready to accept connections`. Postgres replays the log, which restores committed data not yet in the table files and discards uncommitted work. The `invalid record length ... got 0` line is, I believe, the normal end of the log, not an error (not verified). A statement with no `BEGIN` commits automatically when it finishes, so a lone `INSERT` is committed.
</details>

## 17. Contract tests for `IUrlStore`

<details><summary>What is a contract test, and when is it worth writing?</summary>

An abstract test class holds the behavior every implementation of an interface must share (`abstract IUrlStore CreateStore(TimeProvider clock)` plus the `[Fact]`s), and each implementation gets a thin subclass (`InMemoryStoreContractTests`) that supplies an instance. The same tests then run against every store. An interface only fixes signatures (`TryAddAsync` returns `ValueTask<bool>`), not the rules callers rely on: `false` means a collision, expired means missing, a past expiry is rejected, one of many concurrent adds wins. It pays off when a second implementation is coming, as with Postgres next to the in-memory store: the tests define the interface's behavior, not the first implementation's, and the second store has to prove it behaves the same. With only one implementation it is an ordinary test class plus a layer of indirection, so don't build it early. xUnit runs the `[Fact]`s of an abstract base through each concrete subclass (verified: one subclass gave one run per test, and the base added none).
</details>

<details><summary>What belongs in the contract and what doesn't?</summary>

Only behavior every store must share: round trip, collision returns `false` and keeps the first value, expiry boundary, never-expiring entries, a past `ExpiresAt` rejected, exactly one winner under concurrency. Not in it: `Sweep` (`IExpirySweeper` is separate on purpose), and store-specific behavior such as Postgres vacuum, locks and migrations, which get their own tests. The rule has to be uniform because the stores disagree otherwise: Redis rejects `EX 0` (verified), the in-memory store accepted anything, and I believe Postgres accepts any `timestamptz` (not tested here). So each store gets the same guard, and the contract test pins it. The costs: tests must be store-independent (the clock is injected through `CreateStore(clock)`), and a database-backed subclass needs isolation per test and is slower.
</details>

<details><summary>How do you test that a rejected add leaves no trace, and why isn't <code>GetAsync</code> enough?</summary>

A guard written after the insert (`TryAdd`, then `throw` for a past expiry) passes every "it throws" test but leaves a bad entry that occupies the code (an expired entry keeps its code until a sweep). `GetAsync("abc") == null` can't detect that, because reads treat an expired entry as missing, so it returns `null` either way. The test that works: add `abc` with a past expiry and expect `ArgumentOutOfRangeException`, then add `abc` with a valid expiry and assert `true`. With the bad entry stored, the second add returns `false`. Keep it a separate test with one past value (the guard is a single comparison), named for the outcome (`Code_can_be_added_after_rejected_past_expiry`), and mutate the guard (insert first, throw after) to confirm that it goes red. In Postgres the same mistake would burn the code with a row that was inserted and then rejected.
</details>

<details><summary>How do you name a test when you always struggle to?</summary>

Say the rule, not the steps: finish "When ___, the store ___" and turn the sentence into a name in the style of the neighbouring tests (`Null_expiresAt_is_accepted`). Name the behavior, not the method (`Rejected_add_does_not_occupy_the_code`, not `TryAddAsync_throws_then_adds`). If the name needs "and", the test does two things. Read the name as a CI failure line: can someone understand the problem without opening the file? Keep mechanism out of the name (`Guard_runs_before_insert` breaks when the implementation changes). If you're stuck, write the body first and name it last, because the final assertion usually gives the name.
</details>

<details><summary>How does xUnit create the fixture and the test class, and is it dependency injection?</summary>

It isn't the DI container: xUnit has its own matching. Verified with a probe (a fixture, an abstract base with two tests, a subclass): the fixture is constructed once and `InitializeAsync` runs once, before any test (for us: the Testcontainers container starts here, about 7.6 s); then for **every test** xUnit creates a **new instance of the test class**, passes the *same* fixture to its constructor (matched by parameter type, from `IClassFixture<T>` in the base list), and runs the class's own `InitializeAsync`, the test, and `DisposeAsync`. The fixture is disposed after the last test. So the container belongs in the fixture, and the per-test reset (`TRUNCATE`) belongs in the test class's `InitializeAsync` (`IAsyncLifetime`, which returns `Task` in xUnit 2.9.3). Only xUnit's own set of things can be injected this way (fixtures, `ITestOutputHelper`), so the clock arrives as a method parameter (`CreateStore(clock)`). The `ServiceCollection` inside our fixture is a separate real DI container, used so tests build the context factory the same way production will.
</details>

<details><summary>How should a database test reset its data between tests, and what does "a new table per test" cost?</summary>

Measured on a Testcontainers Postgres (20 repetitions, rough): `TRUNCATE TABLE` 3 ms, drop and recreate the table 2 ms, `EnsureDeleted` + `EnsureCreated` (a fresh database) 40 ms per test. All are small next to the container start. A unique table name per test does not work the obvious way: `CreateTablesAsync` failed with `relation "links" already exists`, which I believe is EF caching the model per `DbContext` type (not checked against the docs). `EnsureDeleted` also needs a named database, because it cannot drop the `postgres` maintenance database it connects through (`cannot drop the currently open database`). We use `TRUNCATE` in the test class's `InitializeAsync`. Tests in one class run one after another, so there is no race, but a second Postgres test class would start a second container unless the fixture is shared.
</details>

<details><summary>How good is a same-key race test at catching a missing lock?</summary>

Poor. With the `lock` removed from `InMemoryUrlStore.TryAddAsync`, over 20 runs: the 50-task same-key contract test failed 4 times, the 1,000-task same-key test 0 times, and the distinct-keys stress test **20 of 20**. (My prediction that more tasks would catch it more often was wrong, and I don't know why.) So the same-key test states the behavior every store must have (exactly one winner), and the distinct-keys stress test is the real guard for the in-memory lock, which is why the heavy stress tests stay in `InMemoryStoreTests` and only a light same-key test is in the contract. In Postgres the same-key test guards something else: our wiring (the `23505` mapping, a fresh context per attempt).
</details>

## 18. EF Core and the Postgres store

<details><summary>A <code>SaveChanges</code> fails with a duplicate key. Is the context back to its last good state, and can you reuse it?</summary>

No (verified against Postgres): the database transaction rolls back, but the context keeps the failed entity tracked as `Added`. Reusing that context to save a different new code failed again with `23505`, and the new code was not stored. A context that has seen a failure is poisoned, so every attempt (including each retry of a collision) needs a fresh context.
</details>

<details><summary>The store is a singleton and <code>DbContext</code> is not thread-safe. Does the database's locking protect you, and do you need a <code>SemaphoreSlim</code>?</summary>

The database protects the data, not the context object, which lives in your process (change tracker, connection). Verified: 20 concurrent saves on one shared context gave 1 `ok` and 19 `InvalidOperationException: A second operation was started on this context instance before a previous operation completed`, and 1 row stored. A `SemaphoreSlim` would "work" but serialize every request through one context and keep every entity it has seen in the change tracker, and a failed entity would still poison the next call. The pattern is `IDbContextFactory<T>` (`AddDbContextFactory`): the store creates one context per operation, `await using var db = await _factory.CreateDbContextAsync(ct)`. Verified with one context per call: 200 concurrent inserts of the same code gave 1 inserted and 199 `23505`, and 200 distinct codes gave 200 inserted. A scoped store was rejected because the singleton `ExpirySweepService` could not take it.
</details>

<details><summary>How do you catch a duplicate-key error, and what must not be caught?</summary>

`catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })` (verified: the constant is `"23505"`, and the property pattern compiles because it is a constant). Verified with built exceptions: a unique violation is caught; a `22001` (value too long) is not; a `TimeoutException` is not. A filter means the exception is never caught when the condition is false, so the stack trace is intact. The rest propagates, because of the next card.
</details>

<details><summary>A method is called <code>TryAddAsync</code>. Should it catch every exception and return <code>false</code>?</summary>

No. `Try...` returns `false` for the *expected* failure of that operation (like `int.TryParse`), not for any failure. Here `false` means exactly one thing: the code is taken, and the endpoint retries on it. Catching everything makes a database outage look like a collision (five slow retries, then a misleading 503), hides the cause, and can't be recovered from at this level. The store catches only `23505`, and the boundary (the endpoint or exception middleware) maps unexpected failures (a database error, a timeout) to 503 or 500 and logs once; catching, logging and rethrowing in every layer produces duplicate logs. I believe ASP.NET Core already logs unhandled exceptions from the pipeline, but I haven't checked what it writes. Document the rule on the interface.
</details>

<details><summary>Why do EF methods come in sync and async versions, and why is there no <code>await</code> on <code>db.Links.AsNoTracking()</code>?</summary>

The async ones are for calls that do network I/O: the thread goes back to the pool while the database works, which avoids thread pool starvation when 200 requests all wait on Postgres. Query methods (`db.Links`, `AsNoTracking`, `Where`, `Select`) only *build* an `IQueryable` (deferred execution: no database call), so there is nothing to await; only the executing call at the end (`FirstOrDefaultAsync`, `ToListAsync`, `SaveChangesAsync`) sends SQL, and a query with no executing call never runs. `Add` only records the entity in the change tracker, so use `Add`, not `AddAsync` (I recall `AddAsync` exists for special value generators that query the database; not checked). `SaveChangesAsync` returns the number of changes written, but after one `Add` a failure throws instead of returning 0, so `await SaveChangesAsync(ct); return true;` is clearer than `!= 0`.
</details>

<details><summary>You wrote the read as <code>FindAsync</code>, or <code>Where(r =&gt; r.ExpiresAt &gt; now &amp;&amp; ...)</code>. What goes wrong?</summary>

`FindAsync` returns the row by key and cannot apply the expiry rule, so you would load the whole row and test it in C#. Put the rule in the query: `.Where(r => r.Code == code)` and `.Where(r => r.ExpiresAt == null || r.ExpiresAt > now)` (chained `Where`s combine with `AND`, and avoid the precedence trap: `a || b && c` means `a || (b && c)`, which would match every never-expiring link regardless of the code). `r.ExpiresAt > now` alone never matches a null expiry, because in SQL `NULL > x` is unknown and `WHERE` keeps only true rows (verified: two tests failed). Read `now` into a local first, from the injected `TimeProvider` (EF sends it as a parameter, so the database clock is never used and `FakeTimeProvider` works). `string.Equals(x, StringComparison.OrdinalIgnoreCase)` cannot be translated (`The LINQ expression ... could not be translated`), and it would also be wrong: codes are case-sensitive. Use `r.Code == code`, and `Select(r => r.Url)` to fetch only that column.
</details>

<details><summary>Why does the Postgres store need <code>ToUniversalTime()</code> when the in-memory store does not?</summary>

`DateTimeOffset` comparisons are about the instant, so the in-memory store handles any offset (12:00 UTC equals 20:00 +08:00). Npgsql refuses to write a non-UTC offset to `timestamptz` (verified): `ArgumentException: Cannot write DateTimeOffset with Offset=08:00:00 to PostgreSQL type 'timestamp with time zone', only offset 0 (UTC) is supported.` It surfaces as a `DbUpdateException` with the `ArgumentException` inside (I first said it was not a `DbUpdateException`, which was wrong), and our `23505` filter correctly ignores it. After `ToUniversalTime()` both rows read back as the same instant. It is an adaptation to a Postgres limitation, not a contract rule, but the interface promises any valid `DateTimeOffset`, so two contract tests (accepted, and alive until that instant with a `+08:00` expiry) pass in memory and failed in Postgres until the conversion was added. Use `?.` because a never-expiring link has a null expiry.
</details>

<details><summary>Which mutations did the Postgres store's contract tests catch, and which one survived?</summary>

Six, each in a scratch copy. Remove the null clause: 2 failed. `>` to `>=` in the read: 2 failed (the exact-instant row, UTC and `+08:00`). Remove `SaveChangesAsync`: 6 failed (tests that only check `true` stay green, the round-trip ones catch it). Remove `ToUniversalTime()`: 3 failed. Guard `<=` to `<`: 1 failed. And `r.Code.ToLower() == code.ToLower()` (a case-insensitive lookup): **0 failed, it survived**, because no test read back two codes that differ only in case. The case-sensitivity test (add `abc` and `aBc`, read both back, each returns its own URL) fails against that mutation (`Expected .../2, Actual .../1`) and passes in both stores. A passing suite doesn't mean the behavior is covered: mutate the code to find what isn't.
</details>

<details><summary>Where does a type like <code>LinkEntry</code> belong when <code>LinkRecord</code> is under <code>Postgres/</code>?</summary>

Next to its widest user. `LinkRecord` is the database row, used only by the Postgres store, so it lives in `Storage/Postgres/`. `LinkEntry` is in the `IUrlStore` signature, used by the interface, the endpoint and every store (the Postgres store maps `LinkRecord` to and from it), so it stays in `Storage/` next to `IUrlStore`. In `InMemory/` the interface would depend on a type in a folder named after one implementation.
</details>

<details><summary>Where does the connection string live, and why must the check not run unconditionally?</summary>

In configuration, never in source: `builder.Configuration.GetConnectionString("Shortener")` (later sources win: `appsettings.json`, `appsettings.{Environment}.json`, user secrets in Development, environment variables such as `ConnectionStrings__Shortener`, then command-line arguments). Never log it or put it in an exception message. When I put a check ("connection string must be provided") at the top of `Program.cs`, **33 tests failed** at once, because every endpoint test boots the app through `WebApplicationFactory`, and `dotnet run` would fail for anyone without a database. The decision: a `Storage` setting (default `InMemory`), and only when it is `Postgres` read and check the connection string (`InvalidOperationException` naming the key, not the value), register the context factory and `PostgresUrlStore` with the `IUrlStore`/`IExpirySweeper` forwarding, and log which store was chosen, because forgetting the setting in production would otherwise silently run in memory.
</details>

<details><summary>Postgres or MySQL for this project?</summary>

Both would work: an atomic insert-if-absent (`INSERT IGNORE` or a duplicate-key error in MySQL), durable commits, no native TTL in either, so the sweeper stays. We chose Postgres because we had verified its behavior (the insert race, vacuum, locks, crash recovery) and the Npgsql EF provider, not for a feature MySQL lacks. In the Stack Overflow Developer Survey 2025 (checked), PostgreSQL was used by 55.6% of respondents against 40.5% for MySQL (58.2% against 39.6% among professional developers), but the survey is self-selected and counts users, not installations, so MySQL's installed base (WordPress, legacy PHP) is larger than it suggests. From memory, not verified: InnoDB clusters rows by primary key, which random 7-character keys could make more expensive for inserts than Postgres's heap plus index.
</details>

<details><summary>A <code>WebApplicationFactory</code> test calls an endpoint whose service is not registered and gets <code>500</code>, not an exception. Why?</summary>

Verified. Dependency injection resolves lazily: building the host and calling `CreateClient()` both succeed, and the missing `IUrlStore` is only found when the first request binds the handler parameter. That `InvalidOperationException` is thrown inside the request pipeline. `WebApplicationFactory` runs in the `Development` environment by default (a scratch test printed `ENV=Development`), so the developer exception page catches it and writes a `500` whose body starts `System.InvalidOperationException: Failure to infer one or more parameters.` The exception never reaches the test. `factory.Services.GetRequiredService<T>()` is different: it resolves directly and throws inside the test (that is how the two registration tests failed). When a status is unexpected, read the body first.
</details>

<details><summary>The round-trip test passes against the fixture's database. Why does that not prove the app creates its own schema, and what does?</summary>

The fixture already ran `EnsureCreatedAsync` on its database, so `links` exists before the app starts, and the test would pass even if `Program.cs` never created the schema. The test needs a database with no tables: `CREATE DATABASE "reg_<guid>"` in the same container, a connection string built with `NpgsqlConnectionStringBuilder { Database = name }`, and `DROP DATABASE ... WITH (FORCE)` when the test ends (pooled connections would otherwise block the drop, so clear the pools too). Declare `await using var db` before the factory, so the app shuts down before the drop, and don't forget the `await using`, or every run leaves a database behind. To prove the test can fail, fix `Program.cs` in two steps: register everything first (the registration tests go green and the round trip stays red with `relation "links" does not exist`), then add `EnsureCreatedAsync` (green).
</details>

<details><summary>Which assertion catches registering <code>PostgresUrlStore</code> as two separate singletons, and which does not?</summary>

Verified by mutation in a scratch copy. With `AddSingleton<IUrlStore, PostgresUrlStore>()` and `AddSingleton<IExpirySweeper, PostgresUrlStore>()`, only the `Assert.Same(urlStore, expirySweeper)` test failed (`Values are not the same instance`). `Assert.IsType<PostgresUrlStore>` on each passed, because each interface did resolve to the right type, so it checks the type and not the identity. The consequence in production would be a sweeper that cleans a store nobody reads. In the same way, removing `AddHostedService<ExpirySweepService>()` made `Assert.Single(services.OfType<ExpirySweepService>())` fail with an empty collection.
</details>

<details><summary>A test class needs the shared Postgres container and its own <code>WebApplicationFactory&lt;Program&gt;</code>. How are both declared?</summary>

Both arrive through the constructor, but each is declared differently. `[Collection("postgres")]` on the class (with `ICollectionFixture<PostgresFixture>` on a `[CollectionDefinition]` class) supplies `PostgresFixture`, created once for all classes in the collection, which then run one after another. The factory needs `IClassFixture<WebApplicationFactory<Program>>` on the class, created once per class, because a constructor parameter alone is not injected unless the fixture is declared. A new test class instance is created for every test, so `IAsyncLifetime` is only for async per-test setup and cleanup; it was removed here once nothing called `ResetAsync`. Each `WithWebHostBuilder` result builds its own host, so every test derives its own factory with its own settings and disposes it (`await using` in an async test, `using` in a sync one).
</details>

<details><summary>Is <code>EnsureCreatedAsync</code> at startup safe when several instances start at once?</summary>

Not guaranteed. The existence check and the `CREATE TABLE` are separate steps, so two callers can both see "missing" and the second `CREATE TABLE` fails (the same check-then-act shape as the store race). Measured in a scratch copy, 30 runs each: 8 concurrent callers on an empty database failed in 6 of 30 runs, every error `42P07 relation "links" already exists`. Two `WebApplicationFactory` hosts starting together failed in 0 of 30, probably because host startup is much slower than the schema step (a guess), so the real-world window is small but not zero. Two clients from one factory would not test it, because a factory builds one host. The fix is to take schema creation out of startup: a deploy step (migration job or init container) that runs once before the instances start. Alternatives are to tolerate `42P07` or to take a Postgres advisory lock. Keep `EnsureCreatedAsync` for tests, local development and a single instance.
</details>

<details><summary>The database is down. Which exception does <code>PostgresUrlStore</code> let escape, and why does <code>catch (NpgsqlException)</code> not work?</summary>

Measured (scratch copy, one run per case, no retry strategy). A refused port, and reads and the sweep against a stopped container, throw `InvalidOperationException` ("An exception has been raised that is likely due to a transient failure.") wrapping `NpgsqlException` (`IsTransient=True`) wrapping `SocketException`. A write to a stopped container with a pooled connection open throws `InvalidOperationException` wrapping `DbUpdateException` wrapping `PostgresException` `57P01`. EF wraps a transient failure in an `InvalidOperationException`, so `NpgsqlException` is never the outermost type, and catching `InvalidOperationException` is too broad (a missing DI registration is one too, and must stay a 500). The filter walks the `InnerException` chain for an `NpgsqlException { IsTransient: true }`. A unique violation (`23505`) is not transient, so it is not wrapped, and the `DbUpdateException` catch in `TryAddAsync` still matches. Recheck if `EnableRetryOnFailure` is ever added. My first guess (a raw `NpgsqlException`) was wrong.
</details>

<details><summary>Who should translate a database failure into a 503, the store or the endpoint? Why?</summary>

The store translates and the endpoint catches one type. Each store converts its provider's errors into `StorageUnavailableException` (message and inner exception), so the endpoint code doesn't mention Npgsql, and a store for another database adds its own translation without touching `Program.cs`. The store must not produce HTTP codes, because it knows nothing about HTTP. Keep the original exception as the inner exception for the logs, make the message a constant (no connection details), and log in the handler, otherwise an outage leaves no trace. An outage is not retried: the endpoint's loop exists for code collisions, and the `POST` test asserts `Attempts == 1`.
</details>

<details><summary>Why use a closed port (<code>Port=1</code>) in a test for an unavailable database, and what does the <code>3D000</code> test add?</summary>

A refused connection gives an unreachable database with no Docker and without stopping the shared container (0 to about 470 ms per call). That tests the translation. The `3D000` test (a database name that does not exist, on the live container) covers the opposite: the server is up and answers with a real `PostgresException` that is not transient, so it must escape untranslated and stay a 500 (a wrong connection string is a deploy bug, and as a 503 it would look like a temporary outage). Without it, widening the filter to `when (true)` fails no test. The test asserts the exception exists, is not a `StorageUnavailableException`, and contains `3D000` in its chain, so the setup proves itself, and it covers all three methods because each has its own copy of the filter. Mutation: with `when (true)` all three tests failed.
</details>

<details><summary>A 500 test checks that the body does not contain the inner exception's message, and it fails. Why, and what should the test assert?</summary>

`WebApplicationFactory` runs in Development, so an unhandled exception gets the developer exception page, which prints the exception, its inner exceptions and the stack trace (the failure showed `OperationException: secret:1234`). That is the framework's behavior, not the app's. The 500 tests assert only the status. The leak check belongs in the 503 tests, where the handler writes the response, and it is only meaningful if it can fail: returning `ex.InnerException?.Message` in the body made it fail. Put the secret in the inner exception's message (and the outer one) to cover both. In production, never run with the Development environment, or these messages reach clients.
</details>

## 19. URL validation: input length versus canonical length

<details><summary>The validator limits the input to 2,048 characters, but the store keeps <code>AbsoluteUri</code>. Is that enough for a <code>varchar(2048)</code> column?</summary>

No (verified): a URL with 2,000 `é` is 2,020 characters long, but its canonical form is **12,020** characters, because each `é` becomes `%C3%A9` (6 characters). `https://example.com/` plus 400 `é` is 420 characters of input and 20 + 6 × 400 = 2,420 canonical, so the input check passes and the column would reject it (I expect SQLSTATE `22001`, which our `23505` filter would not catch, so it would be a 500). The fix is a second check after parsing, `parsedUrl.AbsoluteUri.Length > MAX_LENGTH`, with its own message, and **the input check stays**: it is a cheap early reject that bounds the parse work, and the two are independent. The canonical form can also be shorter than the input (`http://example.com:80/a` becomes `http://example.com/a`). Tests: 420 characters in and 2,420 canonical is rejected (assert the message, so only the new check can have rejected it), exactly 2,048 canonical is accepted (assert `uri.AbsoluteUri.Length == 2048` so the setup proves itself), and 2,049 is rejected, so a loosened comparison fails one side or the other. In a bounded column, the length counts characters, and canonical output is ASCII, so characters equal bytes.
</details>


## 20. Rate limiting with a token bucket

<details><summary>Why is a <code>SemaphoreSlim(5)</code> not a rate limiter?</summary>

It limits **concurrency** (how many requests run at once), and a permit comes back when the work finishes. With 10 ms requests, one client can still send 500 per second. A rate limiter returns permits as **time** passes. A semaphore is the right tool for a bulkhead (protecting a resource such as a connection pool), which is .NET's `ConcurrencyLimiter`. It also has no notion of a client unless you keep one per key.
</details>

<details><summary>What does each token bucket store, and what does the limiter store instead?</summary>

Per key only `tokens` and `lastRefill`. Capacity and refill rate are policy, shared by every bucket, so they live in the options. Refill is lazy: on each request, `tokens = min(capacity, tokens + elapsed × rate)`, so an idle bucket costs no work, only memory (which is what idle eviction is for). A rejection is `(1 − tokens) / rate`. Over any period T a client gets at most `capacity + rate × T`.
</details>

<details><summary>A bucket allows 3, refills 0.1 tokens per second, and the clock moves 1 s between calls. Is the 4th call's <code>RetryAfter</code> exactly 7 s?</summary>

No (verified): in IEEE doubles the tokens come out as `0.30000000000000016`, the wait as `6.999999999999998` s, and `TimeSpan.FromSeconds` of that is 69,999,999 ticks, so `Assert.Equal(Rejected(7 s), result)` fails against a correct implementation. Short by a tick is the harmful direction: a client that waits exactly as told is rejected again. It gets worse (caught by a test that waits exactly `RetryAfter` after a partial token): after waiting 6.9999999 s the bucket holds `0.99999999…`, so the call is rejected again, the remaining wait is a few billionths of a second, `TimeSpan.FromSeconds` truncates it to **0 ticks**, and `Rejected(TimeSpan.Zero)` throws `ArgumentOutOfRangeException`, which would be a 500 for a client that did exactly what it was told. "The missing part of a token is always positive, so the wait can't be zero" is true in real numbers, not after truncation to ticks. Fixes: round the wait **up** to whole ticks (`Math.Ceiling` on the tick count, then `TimeSpan.FromTicks`), which gives exactly 7 s and never less than 1 tick, or integer ticks per token throughout. The `Rejected` guard did its job: it turned an invalid wait into a loud failure instead of a silent `Retry-After: 0`. Round to whole seconds only in the HTTP filter (`Retry-After`), never in the limiter, or partial-token tests can't tell right from wrong.
</details>

<details><summary>The burst test passed, yet <code>RetryAfter</code> was <code>(1 − tokens) * rate</code>. Why was nothing red, and how should the bug have been caught?</summary>

The test only asserted `IsAllowed`, so no test looked at the wait at all. The units show the bug (tokens × tokens per second is not a time; it gave 0.1 s instead of 10 s). Write the `RetryAfter` test before the formula and see it red, or afterwards put the wrong formula back and confirm the test goes red.
</details>

<details><summary>Seven <code>[Fact]</code>s pass, and six of them have empty bodies. What does the run tell you?</summary>

Nothing about those six: an empty test is reported green. Placeholders should be `[Fact(Skip = "todo")]` (shown as skipped) or not exist yet.
</details>

<details><summary>Why can a token bucket lose or gain tokens when the server's clock is corrected backwards?</summary>

`GetUtcNow()` is wall-clock time. A negative `elapsed` subtracts tokens, and `lastRefill` moves back, so the next call refills the skipped interval again. Clamp a negative `elapsed` to zero (or use the monotonic `TimeProvider.GetTimestamp()`). `FakeTimeProvider` can't go backwards, so this guard is hard to test.
</details>

<details><summary><code>_buckets.GetOrAdd(key, _ => new TokenBucket(_options, now))</code> runs on every request. What does it allocate when the key already exists?</summary>

A closure object for `now` and `this`, plus a delegate, on every call, even though the factory doesn't run. The `GetOrAdd(key, factory, arg)` overload with a `static` lambda and a value tuple `(Options, Now)` allocates nothing, and `static` makes the compiler reject any capture. Reading `_options` inside the lambda counts as capturing `this`. Not measured here, and irrelevant at this traffic.
</details>

<details><summary>Why limit <code>POST /shorten</code> per IP but not <code>GET /{code}</code>?</summary>

Writes grow the table, use up the code space and are how spam and phishing links get in, and real users send few. Reads are most of the traffic and cheap (a primary-key lookup), and many real users share one NAT IP, so a strict per-IP `GET` limit hurts them. The `GET` risk is enumeration, and the targeted defense is limiting **404s** per client. Read load belongs to the cache or a CDN.
</details>

<details><summary>Why round <code>RetryAfter</code> up to ticks in the limiter and to whole seconds only in the filter, and why is the <code>(long)</code> cast in <code>TimeSpan.FromTicks((long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond))</code> safe?</summary>

`Retry-After` allows only integer seconds, but that is an HTTP rule, so it belongs in the filter. Rounding up to whole seconds in the limiter turned a 0.5 s wait into 1 s, and tests with whole-second expectations couldn't tell (the test with 2 tokens per 1 s, which expects 500 ms, can). Round **up** so that waiting `RetryAfter` really gives a token. Any positive double rounds up to at least 1 tick, so `Rejected(TimeSpan.Zero)` can't happen. The cast is safe: `Math.Ceiling` already returns a whole number, the value is > 0, and a double holds every integer exactly up to 2^53 ticks (about 28 years). Only a `Period` near `TimeSpan.MaxValue` could go past `long.MaxValue`, and an upper bound on `Period` in the options is the right guard for that.
</details>
