# Project plan

Two finish lines, then the UI. Dates assume about 3 hours a day, 5 days a week (about 15 hours a week). Weeks start on Monday. Estimates are rough, so adjust at the checkpoints below.

Legend: `[x]` done, `[ ]` to do.

## Already done
- [x] Minimal API: `POST /shorten`, `GET /{code}` (302 or 404)
- [x] `IUrlStore` with a lock-based `InMemoryUrlStore`, bounded retry on collisions
- [x] `UrlValidator`: absolute http/https only, length cap, no credentials, no self-links, canonical form
- [x] Unit tests for the validator and endpoint tests with `WebApplicationFactory`
- [x] Retry-path tests with a `CollidingStore` fake: 503 when every code collides, success after 1 to 3 collisions, and the limit boundary (4 collisions succeed, 5 give 503). Mutation-checked against off-by-one loop bugs
- [x] Restructured into `src/` and `tests/` with `Shortener.slnx`, and added `AGENTS.md` and `CLAUDE.md` for agent instructions
- [x] Repos on GitHub: [url-shortener](https://github.com/elithrade/url-shortener), [collision-lab](https://github.com/elithrade/collision-lab)

## Line 1: usable and deployed (v1)

**Definition of done:** the service runs at a public URL, survives a restart without losing data, rejects abusive input, has green CI that deploys on merge to `main`, and the README explains how to run, test and deploy it.

| # | Milestone | Done when |
|---|---|---|
| 1 | Finish the test suite | Retry-path tests are done. The parallel POST test is left: 200 concurrent requests give 200 distinct codes, and each code resolves to its own URL |
| 2 | CI | GitHub Actions runs build and test on every push and PR, and a failing test blocks the merge |
| 3 | Better codes | `RandomNumberGenerator` replaces `Random`, with no modulo bias, and a distribution test passes |
| 4 | Expiry | A link can have a TTL, and expired links return 410 or 404. Lazy check on read plus a background sweep, using `TimeProvider`, tested without `Sleep` |
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
Milestone 1, last item: the parallel POST test. Concept first (what concurrent requests can and cannot prove), then write it with `Task.WhenAll`, then break the `lock` in `InMemoryUrlStore` on purpose to see whether the test fails.

After that, milestone 2: a GitHub Actions workflow that runs `dotnet test` on every push and PR.
