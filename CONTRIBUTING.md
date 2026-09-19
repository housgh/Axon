# Contributing to Axon

## Before you start

This project is pre-1.0 (see [README.md#status](README.md#status)) and its public API is still
settling — for anything beyond a small fix, open an issue first to agree on the approach before
writing code. That avoids a PR built on a direction that turns out not to fit.

## Setup

Requires the .NET 8 SDK and Docker (for integration tests).

```bash
git clone https://github.com/housgh/Axon.git
cd Axon
dotnet build Axon.sln
dotnet test
```

`dotnet test` runs both `tests/Axon.Tests.Unit` and `tests/Axon.Tests.Integration`; the latter
needs Docker running to start a real SQL Server container via
[Testcontainers](https://dotnet.testcontainers.org/). See [README.md#testing](README.md#testing)
for what each suite covers.

## Making a change

- Match the surrounding code's style rather than introducing a new one. In particular: no
  comments explaining *what* code does (names should already make that clear) — a comment is
  only worth adding when it captures a non-obvious *why* (a hidden constraint, a workaround for a
  specific bug, a design decision that would otherwise look arbitrary). Grep any file you're
  editing for its existing comment density before adding your own.
- If you're touching a public interface (`IAxonJobStore`, `IAxonClient`, `IAxonRecurringJobStore`,
  `IAxonServerInstanceStore`, `IDeviceConnectionRegistry`, or any `AddXxx` DI extension method),
  update every implementation and call site in the same PR — grep the whole repo, not just
  `Axon.Server`/`Axon.Client`. `Axon.Store.SqlServer` and the example apps are real consumers,
  not throwaway samples.
- Add or update tests for the behavior you're changing. A new store method needs a test in both
  `InMemoryXxx` (unit) and `AxonSqlServerXxx` (integration) implementations if both exist for that
  interface. A bug fix should include a test that fails without the fix.
- Run the full build and both test suites before opening a PR:
  ```bash
  dotnet build Axon.sln
  dotnet test
  ```
- Update [CHANGELOG.md](CHANGELOG.md)'s `[Unreleased]` section for anything user-visible (a new
  feature, a fixed bug, a breaking API change) — see [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
  for the `Added`/`Changed`/`Fixed` categories it follows.
- If your change affects the public API surface, also update the relevant section of
  [README.md](README.md) (its usage examples are meant to compile against the current API, not a
  past version).

## Commit messages

Explain *why*, not just *what* — the diff already shows what changed. A commit message should
give a reviewer (or future you, in `git log`) enough context to judge whether the change was
reasonable without re-deriving it from the code. Look at recent commits (`git log --oneline -20`)
for the expected tone and level of detail before writing your own.

## Pull requests

- Keep PRs focused on one change. A refactor and a feature in the same PR make both harder to
  review and to revert independently if something goes wrong.
- Describe what you tested and how (unit tests, integration tests, manual verification against a
  running server/dashboard) — "I added tests" is less useful than "verified X still returns Y
  under Z condition."
- CI runs the same `dotnet build`/`dotnet test` you ran locally; a red CI run on Linux for
  something that passed locally on macOS/Windows is worth investigating rather than re-running —
  see `SqlExceptionTranslator`'s platform-specific error-number handling in git history for a
  real example of that exact class of bug.

## Reporting bugs / requesting features

Open an issue at <https://github.com/housgh/Axon/issues>. For a bug, include: what you expected,
what happened instead, and the smallest reproduction you can manage (a failing test is ideal, but
a code snippet plus exact error/exception text works too).
