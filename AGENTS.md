# AGENTS.md

Instructions for AI coding agents working in this repository.

## Project
A URL shortener built from scratch with ASP.NET Core Minimal APIs (.NET 10). It is a **learning project** for system design, and the owner wants to write the core logic personally. See `README.md` for the API, `PLAN.md` for the roadmap and current milestone, and `LESSONS.md` for the owner's self-test flashcards. When a session produces a new lesson (a bug found, a trap hit), offer to add a Q&A card to `LESSONS.md` in the same format.

## Layout
```
Shortener.slnx
src/Shortener/            web app (Program.cs, IUrlStore, InMemoryUrlStore, UrlValidator, CodeGenerator)
tests/Shortener.Tests/    unit tests and endpoint tests (xUnit, WebApplicationFactory)
```
The test project has no `Program.cs` of its own. `WebApplicationFactory<Program>` reaches the app through the project reference.

## Commands
- Build and test everything: `dotnet test` (run from the repo root).
- Run the app: `dotnet run --project src/Shortener` (listens on `http://localhost:5154`).
- Format C# with `csharpier`.
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

## Code and test conventions
- Nullable reference types are on. Prefer fixing the type or adding `[NotNullWhen]` over `!`.
- `IUrlStore.TryAddAsync` is the **only** atomic check-and-insert. Never write check-then-set (`Get` then `TryAdd`) in callers.
- The store keeps the canonical URL (`Uri.AbsoluteUri`). Don't store the raw input.
- Error messages describe the rule the client must meet. They must not reveal implementation details or echo user input.
- Endpoint tests use `AllowAutoRedirect = false`, assert the status before reading the body, and use unique URLs, because the in-memory store is shared within a test class.
- A test should be able to fail. For logic that matters, temporarily break the code (for example `<=` instead of `<`) and confirm that a test goes red.
- Use `WithWebHostBuilder(... ConfigureServices ...)` to substitute fakes such as `AlwaysCollidingStore`.

## Do not
- Add frameworks or dependencies without asking.
- Create planning or notes files other than the ones already listed above, unless asked.
