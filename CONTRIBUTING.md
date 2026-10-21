# Contributing to McpEngramMemory

Thanks for your interest in improving McpEngramMemory. This document covers
how to set up a local dev environment, the coding conventions we follow, and
how changes flow from a fork to a release.

## Ground rules

- **Be kind.** Assume good intent, ask clarifying questions, and keep review
  feedback about the code, not the author.
- **Open an issue first for non-trivial changes.** A quick discussion avoids
  you spending a weekend on a PR we'd ask you to restructure. Typo fixes,
  doc clarifications, and small bug fixes are welcome without a prior issue.
- **One logical change per PR.** Bundled PRs are hard to review and hard to
  revert. If in doubt, split.

## Development setup

Prerequisites:

- .NET SDK 10.0 (or 8.0+ if you only need to target `net8.0`)
- PowerShell 7+ (for the helper scripts) or any shell for `dotnet` directly
- Git

```bash
git clone https://github.com/wyckit/mcp-engram-memory.git
cd mcp-engram-memory
dotnet restore
dotnet build
dotnet test --filter "Category!=MSA&Category!=RecallScale&Category!=IndependentRecall" --blame-hang-timeout 5m
```

CI runs `RecallScale` and `IndependentRecall` separately on every PR, including their strict report
validators. MSA runs on a nightly schedule or manual dispatch. Its historical live-agent artifact
checks are not fresh LLM executions. See [installation validation](docs/installation-and-upgrades.md)
for the additional source/package lanes.

## Project layout

```
src/
  McpEngramMemory.Core/   Library — memory engine, search, graph, clustering
  McpEngramMemory/        MCP server (dotnet global tool) built on top of Core
tests/
  McpEngramMemory.Tests/  xUnit tests (1,100+ across net8/9/10)
docs/                     User-facing documentation
benchmarks/               Benchmark datasets, result artifacts, baselines
examples/                 MCP config snippets for Claude Code, VS Code, etc.
```

Start with `docs/core-10.md` and `docs/first-5-minutes.md` to get oriented.

## Making a change

1. Fork, branch off `main` with a descriptive name
   (`feat/<topic>`, `fix/<topic>`, `docs/<topic>`, `chore/<topic>`).
2. Keep commits focused. Conventional-commit-style subjects are nice but not
   required — a clear imperative subject is what matters.
3. Add or update tests. A bug fix should include a regression test that would
   have failed before the fix.
4. Run the test suite locally before pushing.
5. Open a PR against `main`. Fill in what changed and why; link any related
   issue. The CI workflow (`ci.yml`) will build and test across net8/9/10.
6. Reviewers may ask for changes. Push follow-up commits to the same branch —
   don't force-push to rewrite review history until the PR is approved.

## Coding conventions

- **C# style** follows the rules in `.editorconfig`. Run
  `dotnet format` before pushing if your editor doesn't do it on save.
- **No comments explaining *what* code does** — well-named identifiers cover
  that. Comment only when the *why* is non-obvious (a hidden constraint, a
  workaround for a specific bug, surprising behaviour).
- **Boundaries get validation; internals get trust.** Validate user input at
  the MCP tool surface; assume internal callers pass sane arguments.
- **Tests should be deterministic.** Avoid timing-based assertions; prefer
  structural or outcome-based checks.

## Releasing (maintainer notes)

1. Complete both strict recall gates, the fresh MSA gate, installed-package/source validation and the
   release candidate's planned soak. Investigate remaining failures and state unqualified platforms/backends.
2. Bump `<Version>` together in all three package projects: Core, Synthesis.Onnx and the MCP tool.
3. Add a `CHANGELOG.md` entry under a new version header, matching the style
   of prior entries.
4. Merge the release PR.
5. Tag `vX.Y.Z` on `main` and push the tag.
6. `gh release create vX.Y.Z --notes-file <excerpt from CHANGELOG>`.
7. Run `./publish-nuget.ps1 -WhatIf` and retain its unique package staging directory and validation evidence.
   After release approval, publish all three packages in dependency order (Core, Synthesis.Onnx, tool),
   using the publisher's configured feeds. Never update a 2.0.0 tool with default data until completing
   the [pre-update migration](docs/installation-and-upgrades.md).

## Reporting security issues

Please see [SECURITY.md](SECURITY.md). Do not open a public issue for
security vulnerabilities.

## License

By contributing you agree that your contribution will be licensed under the
repository's MIT license ([LICENSE](LICENSE)).
