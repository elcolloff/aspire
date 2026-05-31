---
name: code-review
description: "Review a GitHub pull request for problems. Use when asked to review a PR, do a code review, check a PR for issues, or review pull request changes. Focuses only on identifying problems — not style nits or praise."
---

# PR Code Review

You are a specialized code review agent for the microsoft/aspire repository. Your goal is to review a pull request and identify **problems only** — bugs, security issues, correctness errors, performance regressions, missing error handling at system boundaries, and violations of repository conventions. Do not comment on style preferences, do not add praise, and do not suggest improvements that aren't fixing a problem.

## CRITICAL: Step Ordering

**You MUST complete Step 1 (local checkout) BEFORE fetching PR diffs or file lists.** Branch-discovery calls (e.g., `gh pr view` to get the branch name) are allowed, but do not call `mcp_github_pull_request_read` with `get_diff` or `get_files` until Step 1 is resolved. Skipping or reordering this step degrades review quality and violates the skill workflow.

## Understanding User Requests

Parse user requests to extract:
1. **PR identifier** — a PR number (e.g., `7890`) or full URL (e.g., `https://github.com/microsoft/aspire/pull/7890`)
2. **Repository** — defaults to `microsoft/aspire` unless specified otherwise

If no PR number is given, check if the current branch has an open PR:

```bash
gh pr view --json number,title,headRefName 2>/dev/null
```

## Step 1: Ensure the PR Branch Is Available Locally (BLOCKING — must complete before any other step)

Check whether the PR branch is already checked out locally:

```bash
# Get PR branch name
gh pr view <number> --repo microsoft/aspire --json headRefName --jq '.headRefName'
```

```bash
# Check if we're already on that branch
git branch --show-current
```

If the current branch **matches** the PR branch, proceed to Step 2.

If the current branch **does not match**, ask the user how they'd like to proceed:

- **Option 1 (recommended)**: Check out the branch (stash uncommitted changes if needed) — stash any uncommitted work, fetch, and check out the PR branch. This gives the best review quality because surrounding code is available for context.
- **Option 2**: Review from GitHub diff only — proceed using only the GitHub API diff without touching the working tree. Review quality may be lower because the agent cannot read surrounding code for context.

### Option: Check out the branch

```bash
# Check for uncommitted changes
git status --porcelain
```

If there are uncommitted changes, warn the user and stash them:

```bash
git stash push -m "auto-stash before PR review of #<number>"
```

Then check out the PR branch (this handles both same-repo and fork PRs):

```bash
gh pr checkout <number> --repo microsoft/aspire
```

### Option: GitHub diff only

No local action needed. Proceed to Step 2. Note that review quality may be reduced since surrounding code context is unavailable.

## Step 2: Gather PR Context

Fetch the PR metadata, diff, and file list. This skill uses the `mcp_github_*` tools (MCP GitHub integration). These are available when the GitHub MCP server is configured in the agent environment. If they are unavailable, fall back to the `gh` CLI for equivalent operations.

1. **PR details** — use `mcp_github_pull_request_read` with method `get` to get the title, description, base branch, and author.
2. **Changed files** — use `mcp_github_pull_request_read` with method `get_files` to get the list of changed files. Paginate if there are many files.
3. **Diff** — use `mcp_github_pull_request_read` with method `get_diff` to get the full diff.
4. **Existing reviews** — use `mcp_github_pull_request_read` with method `get_review_comments` to see what's already been flagged. Don't duplicate existing review comments.

## Step 3: Categorize the Changes

Group files by area to guide how deeply to review each:

| Area | Paths | Review focus |
|------|-------|--------------|
| Hosting | `src/Aspire.Hosting*/**` | Resource lifecycle, connection strings, health checks, parameter validation |
| Dashboard | `src/Aspire.Dashboard/**` | Blazor component logic, data binding, accessibility |
| Integrations/Components | `src/Components/**` | Client configuration, DI registration, connection handling |
| CLI | `src/Aspire.Cli/**` | Command parsing, error handling, exit codes |
| Tests | `tests/**` | Flaky test patterns (see below), test isolation, assertions |
| Build/Infra | `eng/**`, `*.props`, `*.targets` | Unintended side effects, breaking conditional logic, channel/quality/versioning impact (see [Channel, quality, and versioning impact](#channel-quality-and-versioning-impact)) |
| CLI packaging | `src/Aspire.Cli/Packaging/**`, `src/Aspire.Cli/Acquisition/**`, `src/Aspire.Cli/Configuration/Aspire*Configuration*.cs` | Channel resolution, identity channel, quality (stable/prerelease/both), staging synthesis gates, per-commit darc feed derivation, install-route sidecar / `WingetFirstRunProbe` / `PeerInstallProbe`, diagnostic overrides — see [Channel, quality, and versioning impact](#channel-quality-and-versioning-impact) |
| Release/install infra | `eng/pipelines/**`, `eng/scripts/get-aspire-cli*`, `eng/scripts/debug-aspire-channel.*`, `eng/scripts/*npm*`, `eng/scripts/stage-native-cli-tool-packages.ps1`, `eng/winget/**`, `eng/homebrew/**`, `.github/workflows/release-*.yml`, `.github/workflows/extension-release.yml`, `.github/workflows/backmerge-release.yml` | Channel promotion (darc), per-channel manifests, NuGet + npm + dotnet-tool + WinGet + Homebrew + install-script publishing, install-route sidecars, daily vs. staging vs. stable behavior, Skip-flag idempotency — see [Channel, quality, and versioning impact](#channel-quality-and-versioning-impact) |
| API files | `src/*/api/*.cs` | Should never be manually edited — flag if modified |
| Extension | `extension/**` | Localization, TypeScript usage |
| Docs/Config | `docs/**`, `*.md`, `*.json` | Accuracy only |

## Step 4: Review the Code

Read the diff carefully. For each changed file, also read surrounding context to understand the impact of the change.

- **If the branch is checked out directly**: read files from the current workspace.
- **If reviewing from GitHub diff only**: use `mcp_github_get_file_contents` to fetch specific files from the PR branch when additional context is needed.

### What to Flag

Only flag **actual problems**. Every comment must identify a concrete issue. Categories:

1. **Bugs** — logic errors, off-by-one, null dereferences, missing awaits, race conditions, incorrect resource disposal.
2. **Security** — injection risks, credential exposure, insecure defaults, OWASP Top 10 violations.
3. **Correctness** — wrong behavior relative to the PR description or existing contracts, breaking changes to public API without justification.
4. **Behavioral contract changes** — when a type/class is replaced, removed, or refactored, check whether any behavioral contracts were silently changed. Examples: a property that previously threw on invalid access now returns a default value; an override that enforced an invariant is gone; a method that validated input no longer does.
5. **Weakened invariants** — check whether validation was relaxed during refactoring. Examples: `SingleOrDefault` (throws on duplicates) replaced by `FirstOrDefault` (silently picks first); `Debug.Assert` guarding a release-relevant invariant that should be an `if` + `throw`; precondition checks that were removed.
6. **Missing error handling at system boundaries** — unvalidated external input, missing null checks at public API entry points. Do NOT flag missing null checks for parameters the type system already guarantees non-null.
7. **Performance regressions** — unnecessary allocations in hot paths, N+1 queries, blocking async calls (`Task.Result`, `.Wait()`).
8. **Concurrency issues** — thread-unsafe collections in concurrent code, missing synchronization, deadlock risks.
9. **Temporal coupling and initialization safety** — fields initialized to `null!` with a separate `Initialize()` method that must be called before use; DI registrations that depend on call ordering; any pattern where forgetting a call causes a runtime NRE with no compile-time safety.
10. **Resource leaks** — `IDisposable` objects (e.g., `CancellationTokenSource`, `SemaphoreSlim`) that are created but never disposed, even if the pattern was moved from elsewhere.
11. **Dead code and stale comments** — comments describing behavior the code no longer implements; unused variables; `ToList()` calls with comments like "materialize to check count" where the count is never checked.
12. **Repository convention violations** — per the AGENTS.md rules:
    - Manual edits to `api/*.cs` files
    - Manual edits to `*.xlf` files
    - Changes to `NuGet.config` adding unapproved feeds
    - Changes to `global.json`
    - Using `== null` instead of `is null`
13. **Code comment guidance** — apply the `AGENTS.md` Code comments guidance when reviewing changed code. Flag only concrete problems, such as comments that contradict the code, workaround comments without a tracking link, parser/protocol/log parsing that omits the raw shape needed to understand edge cases, or comments around privacy/security-sensitive behavior that fail to explain the opt-in, scope, or WHY. Do not flag subjective missing comments or ask for comments on obvious code.
14. **Test problems** — flaky patterns per the test review guidelines: thread-unsafe test fakes, log-based readiness checks instead of `WaitForHealthyAsync()`, shared timeout budgets, hardcoded ports, `Directory.SetCurrentDirectory` usage, commented-out tests.
15. **Channel, quality, and versioning impact** — changes that look correct in a local `dotnet run` but silently break the daily, staging, or stable build pipelines, or vice versa. See [Channel, quality, and versioning impact](#channel-quality-and-versioning-impact) below for the dedicated checklist.

### What NOT to Flag

- Style preferences already handled by `.editorconfig` or formatters
- Missing XML doc comments (unless a public API is completely undocumented)
- Suggestions for refactoring unrelated code
- Missing API file regeneration (this is expected during development)

### Channel, quality, and versioning impact

Aspire ships through multiple channels with different "qualities" of build, and a change that works in one channel can silently break another. For every PR — even when the diff looks local in scope — explicitly reason about what happens in each build flavor before signing off.

**Channels and qualities to consider**

- **Local / dev loop**: `./build.sh` plus `dotnet run` from a contributor checkout. `AspireCliChannel=local`; identity channel is `local`. No darc, no signing, no NuGet/npm publish. Staging synthesis is never reached unless the diagnostic overrides below are set.
- **PR build**: full-bundle `~/.aspire` install produced by a PR run and fetched with `eng/scripts/get-aspire-cli-pr.{sh,ps1} <PR>`. Identity is `pr-<N>`. Used as the recommended carrier for the `debug-aspire-channel.{sh,ps1}` validation scripts because it is a real install but does not synthesize staging on its own.
- **Daily build**: produced by `microsoft-aspire` AzDO pipeline runs on `main` / topic branches; promoted to the daily darc channel; consumed via `aspire.dev/install.{sh,ps1} -q dev`, the `daily` channel in `aspire new` / `aspire update`, and dogfooding feeds. Versions are prerelease-shaped (e.g., `13.0.0-preview.*` or `*-ci.*`). Identity channel is `daily`. Resolves through the shared `dnceng/.../dotnet9` daily feed.
- **Staging build**: release-branch builds whose identity is baked as `staging` (`AspireCliChannel=staging`). Each commit has a SHA-specific `darc-pub-microsoft-aspire-<sha8>` feed. The CLI synthesizes a `staging` package channel when identity is `staging`, the project pins `channel: staging` in `aspire.config.json`, the `StagingChannelEnabled` feature flag is on, or `overrideStagingFeed` is set. **Feed provenance (identity) is decoupled from version filtering (quality)**: `PackagingService.ShouldUseSharedStagingFeed` routes a staging-identity CLI to its own darc feed regardless of version shape, so a prerelease-shaped staging build (`13.4.0-preview.*`) still resolves to its darc feed and not the shared daily feed. Quality is typically `Both`; stable-shaped staging is `Stable`. See [`docs/cli-staging-validation.md`](../../docs/cli-staging-validation.md) for the routing matrix.
- **Stable / GA build**: signed builds promoted to the GA channel by `release-publish-nuget` (`GaChannelName`, e.g., `Aspire 9.x GA`); pushed to NuGet.org and npm (`@microsoft/aspire-cli` pointer + per-RID packages via ESRP/MicroBuild); GitHub release assets uploaded; WinGet manifest submitted; Homebrew autobump validated. Surfaced as the `stable` channel and as default NuGet feed resolution. Identity channel is `stable`. Quality is `Stable` only.

**Distribution surfaces (per `docs/release-process.md`'s Installer channels table)**

A single CLI version is acquired through many routes, and each carries its own routing assumption. When reviewing acquisition, sidecar, or `aspire update --self` code, walk every route:

- **NuGet.org** — libraries, AppHost SDK, `Aspire.Cli.*` per-RID tool packages.
- **npm** — `@microsoft/aspire-cli` pointer package and seven per-RID packages, published through ESRP/MicroBuild. Pointer is published only after the RID packages plus a propagation delay (`NpmRegistryPropagationDelayMinutes`).
- **`dotnet tool install -g Aspire.Cli`** — per-RID NuGet packages.
- **WinGet** (`winget install Microsoft.Aspire`) — manifest PRs into `microsoft/winget-pkgs`.
- **Homebrew cask** (`brew install --cask aspire`) — upstream autobump; release pipeline only validates the cask against the live GitHub release.
- **Install script** (`get-aspire-cli.{sh,ps1}`) — pulls from GitHub release assets directly.
- **VS Code extension Marketplace** — separate publish step, gated by `Package VS Code Extension as Pre-Release=true` for prereleases.

The CLI identifies its acquisition route via a per-install sidecar so `aspire update --self` can route back through the same channel. See [`docs/specs/install-routes.md`](../../docs/specs/install-routes.md).

**Diagnostic overrides (PackagingService only)**

Two config keys exist purely for staging-feed validation and are scoped to `PackagingService` routing only — they do **not** change the global identity used for hive / packages directory lookups. Treat any change that touches their semantics or scope with extra care:

| Key | Effect |
| --- | --- |
| `overrideCliIdentityChannel` | Forces the identity used for staging-feed routing decisions. Must be a valid channel (`stable`, `staging`, `daily`, `local`, `pr-<N>`); invalid values are ignored and the real identity is used. |
| `overrideCliInformationalVersion` | Forces the informational version read by both the SHA-derivation provider and the version-shape (quality) predicate. The `+<sha>` suffix (truncated to 8 chars) builds the darc URL. |
| `overrideStagingFeed` | Forces staging-channel availability regardless of identity / feature flag; treats staging as available. |

A CLI run with any override set emits a one-time warning so overridden routing can never silently take effect on a normal invocation.

**What to look for**

Flag changes that fall into any of these categories:

- **Channel resolution / synthesis** — edits to `PackagingService`, `PackageChannel*`, `PackageSources`, `PackageMapping`, `PackageSourceOverrideMappings`, `NuGetConfigMerger`, `NuGetConfigPrompter`, `IdentityChannelReader`, or the `aspire.config.json` schema. Verify the change preserves the matrix: identity `local` / `pr-<N>` / `daily` / `staging` / `stable` × requested channel `default` / `stable` / `staging` / `daily` × project pin present/absent × version shape prerelease/stable. Check that staging synthesis stays gated (identity, project pin, feature flag, or `overrideStagingFeed`) so daily/local/PR CLIs don't silently fabricate staging feeds and downgrade resolution.
- **Identity vs. quality decoupling** — `ShouldUseSharedStagingFeed` and the surrounding logic establish that a staging-identity CLI always uses its own darc feed regardless of version shape. A change that re-couples feed provenance to version shape will reintroduce the polyglot regression from #17743 where prerelease-shaped staging builds wrongly resolve through the shared daily feed.
- **C# vs. polyglot apphost feed resolution** — C# apphosts have the darc feed baked into their `nuget.config`, which masks channel-routing bugs. Polyglot (TypeScript / Python) apphosts resolve solely through the synthesized channel's feed. Any channel-routing change must be reasoned about against polyglot apphosts, not just C#.
- **Quality mapping** — anything that touches `PackageChannelQuality` (`Stable`, `Prerelease`, `Both`) or decides whether prerelease versions are eligible. Flipping a channel to `Prerelease`/`Both` will surface daily bits in stable installs; flipping to `Stable` will hide daily packages from the daily channel.
- **Acquisition / sidecar / self-update** — changes to `InstallationDiscovery`, `InstallationCandidateSources`, `InstallSidecarReader`, `InstallSource`, `PeerInstallProbe`, `WingetFirstRunProbe`, or `aspire update --self`. A bug here can permanently strand a user on the wrong channel or cross-route an update (e.g., a WinGet install self-updating from npm). Verify every install route surface listed above still routes correctly.
- **Version selection / floating ranges** — changes to `Directory.Packages.props`, `Versions.props`, `eng/Version.Details.xml`, package floating version ranges (`*-*`, `*-preview*`), or `PackageValidationBaselineVersion`. Stable consumers must not float into prerelease; daily/staging consumers must not get pinned to the last stable.
- **Feeds & NuGet config** — changes to `NuGet.config`, generated NuGet.config from `aspire new` / `aspire update`, or feed selection logic. Adding a feed that only resolves on internal Azure DevOps will work in CI but break public daily/stable acquisition (and vice versa). Per `AGENTS.md`, public feeds outside the approved domains are prohibited.
- **Diagnostic override scope** — changes touching `overrideCliIdentityChannel`, `overrideCliInformationalVersion`, or `overrideStagingFeed`. These must remain scoped to `PackagingService` only, must keep their one-time warning, and must not start influencing hive / package directory paths.
- **Release pipeline & manifests** — changes to `eng/pipelines/release-publish-nuget.yml`, `release-github-tasks.yml`, `extension-release.yml`, `backmerge-release.yml`, `eng/winget/**`, `eng/homebrew/**`, npm packaging scripts (`pack-cli-npm-package.ps1`, `verify-cli-npm-package.ps1`, `stage-native-cli-tool-packages.ps1`), or darc channel promotion. Confirm the change is safe under `DryRun: true` and under the documented re-run idempotency: `SkipNuGetPublish`, `SkipNpmPublish`, `SkipNpmRidPublish`, `SkipNpmPointerPublish`, `SkipChannelPromotion`, `SkipWinGetPublish`, `SkipHomebrewValidation`, `SkipReleaseAssets`, `SkipGitHubTasks`, `SkipVSCodeExtensionPublish` (see `docs/release-process.md`). Watch for assumptions that only hold for stable (`IsPrerelease=false`) or only for prerelease — npm publishing is currently blocked for prereleases until non-`latest` dist-tag support lands. Be wary of `AllowNpmLatestDistTagMove` being used outside its documented servicing scenario.
- **Versioning math** — version string parsing/formatting, SemVer comparisons, prerelease label handling, `aspire update` "newer than" comparisons. Daily versions sort below stable under standard SemVer; getting this backwards causes the CLI to either refuse to update or to "update" stable users down to daily.
- **Feature flags tied to channel** — flags like `StagingChannelEnabled` and config like `overrideStagingFeed`. Verify defaults are appropriate per channel and that flipping a default doesn't change behavior for already-installed CLIs at a different quality.

**Reasoning checklist — apply per change**

For each non-trivial change, explicitly answer:

1. **Local run**: does the change work in `./build.sh` + `dotnet run` from a fresh contributor checkout (`AspireCliChannel=local`, no signing, no published feeds)?
2. **PR build**: does the change still behave correctly when carried by a PR-build install (`get-aspire-cli-pr.{sh,ps1}`, identity `pr-<N>`), including the diagnostic-override-driven validation flows in `debug-aspire-channel.{sh,ps1}`?
3. **Daily build**: what happens when the same code ships in a daily build consumed via `-q dev` / `daily` channel? Are prerelease versions still resolved? Does the install-route sidecar still point at daily? Does `aspire update` keep daily users on daily?
4. **Staging build**: does staging-channel synthesis still gate correctly (identity, project pin, feature flag, override)? Does `ShouldUseSharedStagingFeed` still route a staging-identity CLI to its per-commit darc feed regardless of version shape? Does the change accidentally enable staging on daily/local/PR builds, or accidentally disable it on staging builds? Does it work for **polyglot** apphosts (which have no baked nuget.config), not just C#?
5. **Stable / GA build**: does the change survive promotion to the GA darc channel and publication to NuGet.org and npm? Does it respect `IsPrerelease=false` semantics in `release-publish-nuget`? Are stable users protected from accidentally pulling prerelease packages, daily feeds, or staging-only manifests?
6. **Cross-channel transitions**: what happens when a user installed via WinGet / Homebrew / install script / `dotnet tool install` / npm / VS Code extension runs `aspire update --self`? Does the install-route sidecar route the update back through the original channel? Does discovery (`InstallationDiscovery`, `PeerInstallProbe`, `WingetFirstRunProbe`) still surface the right install?
7. **Idempotency / re-runs**: if this change is in the release pipeline or GitHub workflows, does it stay idempotent under the documented `Skip*` re-run flags, including the npm-specific ones (`SkipNpmPublish`, `SkipNpmRidPublish`, `SkipNpmPointerPublish`) and the npm propagation delay? (See `docs/release-process.md` → "Handling Failures".)
8. **Validation tooling**: if the change touches staging-feed routing, run or recommend running `eng/scripts/debug-aspire-channel.{sh,ps1}` (or its `debug-staging` / `debug-stable` wrappers) against a PR build to confirm the resolved feed and quality match the validation matrix in `docs/cli-staging-validation.md`.

If any of these questions cannot be answered confidently from the diff alone, flag it as a review comment asking the author to confirm.

### Reviewing refactored / moved code

When code is moved from one file to another (e.g., extracting a class), treat the moved code as if it were newly written. Specifically:

- **Flag pre-existing issues in moved code.** If buggy or unsafe code is copy-pasted into a new file, flag it. The refactoring is an opportunity to fix it. Mark these as "Pre-existing issue, good opportunity to fix during this refactoring."
- **Diff old vs. new behavior.** When a type/class is deleted and replaced, explicitly compare the old and new implementations. Look for: removed overrides, changed exception behavior, relaxed validation, lost invariant checks.
- **Check callers of removed types.** If `OldClass` is removed and replaced by `NewClass<T>`, verify that all call sites that depended on `OldClass`-specific behavior still work correctly.

## Step 5: Present Findings to the User

**Do not post a review automatically.** Instead, present all findings as a numbered list for the user to triage. Order by potential impact.

Then ask the user what to do next. The user may respond with:

- **"Add 1, 3, 5 as comments"** — post only those numbered items as review comments.
- **"Add all"** — post every item.
- **"Add none"** — skip posting entirely.
- Any other selection or modification instructions.

## Step 6: Post Selected Comments as a Review

Once the user has selected which findings to include:

### Auto-merge safety check

Before submitting a review with `event: "APPROVE"`, check whether the PR has auto-merge enabled:

```bash
gh pr view <number> --repo microsoft/aspire --json autoMergeRequest --jq '.autoMergeRequest'
```

If the result is **non-null** (auto-merge is enabled) **and** the review includes comments, warn the user:

> **Warning:** This PR has auto-merge enabled. Approving it will likely trigger an automatic merge before the author has a chance to address your review comments. Would you like to:
>
> 1. **Approve anyway** — submit as APPROVE (auto-merge may proceed immediately).
> 2. **Downgrade to comment** — submit as COMMENT instead so the author can address feedback first.

Wait for the user's response before proceeding. If they choose option 2, use `event: "COMMENT"` instead of `"APPROVE"`.

### Posting the review

1. **Create a pending review**:
   Use `mcp_github_pull_request_review_write` with method `create` (no `event` parameter) to start a pending review.

2. **Add inline comments for each selected finding**:
   Use `mcp_github_add_comment_to_pending_review` for each selected item. Place comments on the specific lines in the diff:
   - `subjectType`: `LINE` for line-specific comments, `FILE` for file-level comments
   - `side`: `RIGHT` for comments on new code
   - `path`: relative file path
   - `line`: the line number in the diff
   - `body`: concise description of the problem and how to fix it

3. **Submit the review**:
   Use `mcp_github_pull_request_review_write` with method `submit_pending`:
   - If any comments were posted and the user explicitly asked to approve: use `event: "APPROVE"` only if auto-merge is not enabled on the PR, or the user confirmed they want to approve after seeing the auto-merge warning.
   - If any comments were posted and the user did not ask to approve: use `event: "COMMENT"`.
   - In either case, include a summary body listing the number of issues found by category. Do not use `"REQUEST_CHANGES"` unless the user explicitly asks for it.
   - If the user chose to add none: do not create or submit a review. Confirm to the user that no review was posted.

## Review Quality Rules

- **Flag only concrete, high-confidence problems.** Report definite issues such as bugs, security problems, correctness errors, performance regressions, missing error handling at system boundaries, or repository-convention violations. Do not raise speculative concerns, design feedback, or issues you cannot support with specific evidence in the diff.
- **One problem per comment.** Don't bundle multiple issues into a single comment.
- **Be specific.** Reference the exact line(s), variable(s), or condition(s) that are problematic.
- **Provide fix direction.** If the fix isn't obvious, include a brief suggestion or code snippet.
- **Don't repeat existing review comments.** Check existing review threads before posting.
