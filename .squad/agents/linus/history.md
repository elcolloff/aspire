# Linus Agent History

## Learnings

### Comments Must Stand On Their Own Without Design-Doc References

**Rule:** All comments in code, tests, and scripts must be self-contained and understandable without referencing internal design specifications.

**Forbidden patterns:**
- Design-doc filenames: `agreed-design-v3.md`
- Internal labels: `PR2-S<N>`, `PR2-spec`, `PR2 G<N>`
- Design-doc section references: `§N.N`, `§G<N>`, `Spec §N.N`
- Design terminology specific to internal specs: `Mode A`, `Mode B`, `sidecar primitive` (use actual locations instead: "parent-directory sidecar", "sibling sidecar")
- Spec references: `Acquisition v3`, `the v3 spec`, `PR2 design contract`

**Replacement approach:** Restate the actual invariant or behavior in plain English as if writing for a future maintainer with no access to design docs.

**Examples:**
- ❌ `// Per agreed-design-v3.md §2.4, return the resolved binary's directory...`
- ✅ `// Return the resolved binary's directory as the prefix so downstream consumers can still locate the binary even when the sidecar is absent.`

- ❌ `// Spec §2.4: when neither the Mode B sibling nor Mode A parent sidecar is present...`
- ✅ `// When neither the sibling sidecar (next to the binary) nor the parent-directory sidecar (one level up) is present...`

**Where this applies:** Production code, test code, inline comments, XML doc comments, and installer scripts.

**How to identify:** Use pattern matching on git diff:
```bash
git diff origin/main..HEAD -- src/ tests/ eng/ | grep -nE '^\+.*\b(PR2-S[0-9]|PR2-spec|PR2 G[0-9]|Spec §|§[0-9]\.[0-9]|§G[0-9]|Acquisition v3|agreed-design-v3|per spec §|the v3 spec|PR2 design contract|sidecar primitive|Mode A sidecar|Mode B sidecar)'
```

**Date:** 2026-05-06


---


## 2026-05-12T19:42:00-04:00 — PR2 packaging-verification audit (read-only)

PR2 (#16817, branch `ankj/v3-pr2-route-primitives`) audit under new minimal scope (sidecar producer + `IInstallPathResolver` + packaging fixes only; consumers deferred). Verdict: NEEDS-FIXES, small — one required fix, one recommended. Report at `.squad/decisions/inbox/linus-pr2-packaging-audit.md`.

**Key findings:**
- All 5 distribution shapes produce a sidecar correctly. The 2 script-route shapes (stable + PR) have *filesystem-level* tests (dry-run + assert file on disk). The dotnet-tool nupkgs (RID + pointer) have *three layers* of verification including release-time `verify-cli-tool-nupkg.ps1` that positively asserts sidecar absence in the pointer. Winget zip + brew tarball have **only XML-shape tests** of `Common.projitems` — no archive-extraction verification anywhere. That's the one real gap.
- `verify-cli-archive.ps1` exists in the pipeline but does not look at `.aspire-install.json`. Mirroring the `verify-cli-tool-nupkg.ps1` pattern (~30 LOC) closes the gap.
- `IInstallPathResolver` ships with **zero production consumers** on this branch — only the test assembly references it. Acceptable under the slicing as long as the PR body says so; otherwise reviewers will flag dead code.
- Resolver test coverage is strong on individual branches (10 tests, 250 LOC) but missing a single positive integration test that "the shape script/packager puts on disk is the shape the resolver classifies." Recommended: ~5 shape-compatibility tests in the resolver test class.

## Learnings — packaging verification patterns

1. **Three-layer pattern for shipped artifacts** (source JSON + wiring XML + release-time extraction) is the gold standard. The dotnet-tool nupkg achieves all three; the standalone archive achieves only the first two. When auditing packaging completeness, ask: *(a) is the source content valid? (b) is the build wiring correct? (c) does the final shipped artifact actually contain what we think it does?* Wiring tests catch typos in conditions; extraction tests catch path-resolution bugs in MSBuild properties like `$(OutputPath)` ending up somewhere unexpected.
2. **Negative assertions matter as much as positive ones.** Best example here: `--local-dir`/unmanaged install tests assert sidecar is *not* written at any depth under the install root (recursive scan). Similarly the pointer-nupkg test asserts the file is *absent*. A packaging PR is not "verified" if a regression that adds the file in the wrong place — or removes it from the right place — wouldn't fail a test.
3. **XML-shape tests are a load-bearing shortcut, not a substitute for end-to-end.** `StandaloneArchivePackagingTests` and `ToolNupkgPackagingTests` both verify `eng/clipack/Common.projitems` / `Aspire.Cli.csproj` declares the right MSBuild structure. Cheap. Fast. *Cannot* catch a bug where the file lands one directory below where the archiver picks it up. Always pair XML-shape tests with at least one extraction-and-inspect integration test, even if outerloop. If running pack is too heavy in unit tests, extend an existing release-time `verify-*.ps1` script — that's already invoked from CI for a different purpose, so the marginal cost is negligible.
4. **Heuristic for self-contained PRs that defer consumers:** producer + reader must have a *compatibility* test, not just two separate tests. If `script.sh` writes a sidecar at path P and `Resolver` reads sidecars at paths {Q, R}, you need a test asserting P ∈ {Q, R}. Two separate green test suites can pass forever while the contract drifts. The compatibility test is what makes the slicing safe.
5. **Dead-code surface in foundation PRs is acceptable iff the PR body says so.** `internal interface` + `internal sealed class` + zero production consumers reads as accidental to reviewers who don't have the slicing context. Surfacing the slicing in the PR description avoids burning review cycles on "why does this exist?"

## 2026-05-12T20:01:00-04:00 — Shape-compatibility test pattern (PR2 fix #2)

Added 5 producer↔reader compatibility tests to `InstallPathResolverTests` (+109 LOC,
class now at 16 tests). Decision note: `.squad/decisions/inbox/linus-pr2-fix2-shape-compat-tests.md`.

## Learnings — shape-compatibility test pattern

1. **Branch tests ≠ shape tests.** When a resolver/reader has N classification branches
   (sibling-sidecar, parent-sidecar, no-sidecar fallback, …), unit-testing each branch
   in isolation does NOT prove the *real-world shapes producers emit* land in the right
   branch. You can have 100% branch coverage and still ship a bug where the script
   writes the sidecar one directory off from where the resolver walks. The defense is a
   small "shape" test suite: construct the EXACT layout each producer emits, then
   call the reader and assert both classification AND output. ~1 test per shipping
   shape; tiny; high leverage; fails at exactly the layer that introduces the drift.

2. **The sidecar-location → Mode contract for Aspire CLI installs (pinned as of PR2):**
   - **Sidecar SIBLING to binary** (flat layout): `PayloadColocated` mode, prefix =
     binary directory. Used by winget, brew, dotnet-tool RID nupkg.
   - **Sidecar ONE DIRECTORY ABOVE binary** (`bin/` subdir): `PayloadInSubdirectories`
     mode, prefix = parent of binary directory. Used by script-route stable archive
     and script-route PR archive (the PR variant nests the prefix inside a
     `dogfood/pr-NNNNN/` segment but the relationship of sidecar to binary is the same).
   - **No sidecar reachable**: `Unknown` mode, prefix = binary directory (so consumers
     can still locate the binary in the unmanaged-install fallback).
   - **Anything else** (sidecar two+ levels up, sidecar in a sibling subtree): NOT a
     recognized shape; lands in `Unknown`. If a future packager needs a deeper layout,
     it must extend the resolver AND add a shape-compat test.

3. **Prefix is always the directory containing the sidecar.** Not the install root,
   not the package root, not the OS-managed parent. This is the unambiguous handle
   each shape exposes — uninstall, version detection, channel routing all key off it.
   Especially important for the dotnet-tool RID layout where the meaningful prefix is
   `<package>/tools/any/<rid>/`, NOT `<package>/`.

4. **Shape-compat tests assume producer-side correctness; they pin ONLY the reader's
   agreement with producers.** They are NOT a substitute for producer-side tests
   (extraction, XML wiring, etc.) — those still need to live in their respective test
   suites. The compatibility tests are the small "are these two halves still talking"
   integration layer above them.

5. **Minimal sidecar content for tests:** `"{\"route\":\"<route>\"}"`. The resolver
   only checks file existence, not content. Don't over-spec the JSON — that's what
   the producer-side packaging tests are for.

