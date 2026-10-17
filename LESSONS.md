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

