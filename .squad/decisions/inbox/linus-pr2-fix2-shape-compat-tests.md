# PR2 fix #2 — Resolver shape-compatibility tests for all 5 distribution shapes

**Date:** 2026-05-12T20:01:00-04:00
**Author:** Linus
**Branch:** `ankj/v3-pr2-route-primitives`
**Closes audit gap:** `linus-pr2-packaging-audit.md` — Recommended fix #2 ("~5 shape-compatibility tests").
**File:** `tests/Aspire.Cli.Tests/Acquisition/InstallPathResolverTests.cs` (+109 LOC, brings class to 16 tests / 359 LOC).

---

## What this pins down

The existing 10 `InstallPathResolverTests` exercise the resolver's *branches* in isolation
(sibling-sidecar wins over parent-sidecar, symlink resolution walks to real binary, etc.).
None of them assert that the **on-disk shape each shipping packager actually emits** maps to
the **`(Mode, Prefix)` tuple the resolver returns** — i.e., the producer↔reader contract.

If a packager moved the sidecar (e.g. into a `tools/` subdir) without updating the
resolver, every existing branch test would still pass. The producer test suite and the
reader test suite would both stay green forever while the real-world install was broken.

These 5 new tests close that loop by constructing the *exact* layout each distribution
shape produces, then calling `InstallPathResolver().Resolve(binaryPath)` and asserting
both `Mode` and `Prefix`.

## The shape contract pinned by these tests

| # | Shape | Sidecar path | Binary path | Mode | Prefix |
|---|---|---|---|---|---|
| 1 | Script-route stable archive | `<prefix>/.aspire-install.json` | `<prefix>/bin/aspire` | `PayloadInSubdirectories` | `<prefix>` |
| 2 | Script-route PR archive | `<prefix>/dogfood/pr-99999/.aspire-install.json` | `<prefix>/dogfood/pr-99999/bin/aspire` | `PayloadInSubdirectories` | `<prefix>/dogfood/pr-99999` |
| 3 | Winget zip extracted | `<prefix>/.aspire-install.json` | `<prefix>/aspire.exe` (sibling) | `PayloadColocated` | `<prefix>` |
| 4 | Brew tarball extracted | `<prefix>/.aspire-install.json` | `<prefix>/aspire` (sibling) | `PayloadColocated` | `<prefix>` |
| 5 | Dotnet-tool RID-specific | `<prefix>/tools/any/linux-x64/.aspire-install.json` | `<prefix>/tools/any/linux-x64/aspire` (sibling) | `PayloadColocated` | `<prefix>/tools/any/linux-x64` |

Key invariants the table encodes:

- **Sidecar location determines `Mode`** — sibling-to-binary → `PayloadColocated`;
  one-directory-above-binary → `PayloadInSubdirectories`. Anything else (sidecar two or
  more directories up, sidecar in a sibling subtree, no sidecar) is NOT a recognized
  shipping layout and would land in the `Unknown` fallback, which downstream consumers
  must treat as an unmanaged install.
- **`Prefix` is always the directory containing the sidecar** (NOT the install root in
  the script-PR case, NOT the package root in the dotnet-tool case). This makes the
  prefix the unambiguous handle each shape exposes to consumers — uninstall, version
  detection, channel routing all key off it.
- The script-route PR shape's sidecar lives inside the per-PR subdirectory by design,
  so each PR install is an independent prefix even though they share a common ancestor.

## Why this matters for slicing

PR2 ships the sidecar producer + the resolver but defers consumers. Without these tests,
a producer-side change (e.g. moving the sidecar location in `eng/scripts/get-aspire-cli.sh`)
would land green, a resolver-side refactor would land green, and the bug would only
surface once PR3 wires up a real consumer. These tests fail at the layer that introduces
the drift — which is the right place to fail.

## Test pattern

Each test:

1. `using var temp = new TestTempDirectory();` (shared helper at `tests/Shared/TempDirectory.cs`
   — uses `Directory.CreateTempSubdirectory()` per repo convention).
2. `Directory.CreateDirectory()` to build the per-shape subdirectory structure.
3. `File.WriteAllText(binaryPath, string.Empty)` — empty binary file (resolver only
   walks the path, doesn't read contents).
4. `File.WriteAllText(sidecarPath, "{\"route\":\"<route>\"}")` — minimal valid sidecar
   (resolver only checks existence, not content; matches the convention of every other
   test in the class).
5. `var (mode, prefix) = new InstallPathResolver().Resolve(binaryPath);`
6. `Assert.Equal` both `Mode` and `Prefix`.
7. Disposal cleans the temp dir.

No symlinks, no platform-specific skip conditions. The shape is what matters; the OS
doesn't (the resolver is OS-agnostic in path semantics — it relies on `Path.Combine` /
`Path.GetDirectoryName`, both of which work fine on every supported platform).

## Verification

- Build: `dotnet build tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj` — clean, 0 warnings.
- Targeted run (5 new tests): 5/5 passed in 836 ms.
- Full class run: 15 passed, 1 skipped (Windows-only casing test, expected on macOS).

## What this does NOT cover

- The compatibility tests do NOT verify the *producer side* — i.e., that
  `get-aspire-cli.sh` actually writes the sidecar at the path Shape #1 expects, or that
  the dotnet-tool nupkg actually contains the sidecar at the path Shape #5 expects.
  Those are producer-side responsibilities; the matching tests live in
  `StandaloneArchivePackagingTests`, `ToolNupkgPackagingTests`, the script's own
  `get-aspire-cli.*.Tests.ps1`, and (post-Basher) the extended
  `verify-cli-archive.ps1`. **The compatibility tests assume the producers are
  correct and pin only that the resolver agrees with the producers' shape.**
- The compatibility tests do NOT exercise the network layer, signature verification,
  or anything else outside the path-walk. That's by design — the resolver is a pure
  filesystem function and the tests stay at that layer.

## Followups (out of scope for this commit)

- When PR3 adds the first real consumer (route reader / version dispatcher), the
  consumer should add ONE end-to-end test per shape that pipes the resolver's output
  into the consumer's logic and asserts the consumer behaves correctly for each shape.
  That collapses the 5-test compatibility matrix to a single column of behavior per
  shape, which is the right structure once consumers exist.
