# Releasing ObdInsight to NuGet.org

**Date:** 2026-07-19 (roadmap B15). Publishing uses **NuGet Trusted Publishing**
(GitHub Actions OIDC → short-lived API key) — no long-lived API keys stored anywhere.

## Packages

| Package | Contents |
|---|---|
| `ObdInsight.Core` | Session, protocols, capabilities, resolver, resilience. Depends only on `ObdInsight.Annotations` + `Microsoft.Extensions.Logging.Abstractions` |
| `ObdInsight.Annotations` | `[CanFrame]`/`[CanSignal]`/`[Uds*]` attributes + `CanBits` |
| `ObdInsight.SourceGeneration` | Analyzer-only package (`analyzers/dotnet/cs`); needed only by projects that define their own frames |
| `ObdInsight.Telemetry` | `ITelemetrySession` consumer facade |
| `ObdInsight.Simulation` | Replay transport + simulated Leaf for hardware-free dev |
| `ObdInsight.Transports.Ble` | Plugin.BLE transport, `net9.0` + `net9.0-android` + `net9.0-ios` |

Shared metadata (license, repo URL) lives in `src/Directory.Build.props`. Every
package ships its own `README.md` (lives next to each csproj; rendered on nuget.org —
`Directory.Build.props` packs it automatically for packable projects).

## Versioning (MinVer)

Versions are computed from git tags by [MinVer](https://github.com/adamralph/minver)
(tag prefix `v`; packable projects only):

- On a tagged commit, the package version **is** the tag: `v0.1.0-preview.1` →
  `0.1.0-preview.1`.
- Between tags, local/CI builds get `last-tag + height` with the `preview.0`
  pre-release identifier (e.g. `0.1.1-preview.0.5` five commits after `v0.1.0`) —
  never colliding with a released version. With no tags at all: `0.0.0-preview.0.N`.
- Nothing to edit anywhere to bump a version — push a tag.
- MinVer needs full git history: both workflows check out with `fetch-depth: 0`.

## One-time setup (repo owner)

1. **nuget.org → your profile → Trusted Publishing → Add policy** (one policy covers
   the packages you own):
   - Repository owner: `kfrancis`
   - Repository: `ObdInsight`
   - Workflow file: `release.yml`
   - Environment: leave empty
2. **GitHub repo → Settings → Secrets and variables → Actions → Variables**: add
   `NUGET_USER` = your nuget.org profile name (the account the policy lives on).
3. That's it — no API keys anywhere. The workflow's `id-token: write` permission +
   `NuGet/login@v1` exchange the OIDC token for a ~1-hour key at run time.

## Cutting a release

```powershell
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

The `Release` workflow runs both test suites, packs all six packages with the tag's
version (`v` prefix stripped), and pushes with `--skip-duplicate`. Tag pattern examples:
`v0.1.0-preview.2`, `v0.1.0`, `v0.2.0`.

## Verifying locally before tagging

```powershell
dotnet pack src/ObdInsight.Core -c Release -o artifacts/packages
# artifacts/ is untracked scratch output
```

## EvTestDrive consumption

```xml
<PackageReference Include="ObdInsight.Core" Version="0.1.0-preview.1" />
<PackageReference Include="ObdInsight.Telemetry" Version="0.1.0-preview.1" />
<PackageReference Include="ObdInsight.Transports.Ble" Version="0.1.0-preview.1" />
<PackageReference Include="ObdInsight.Simulation" Version="0.1.0-preview.1" />
```

Wiring guide: `docs/MAUI_INTEGRATION.md`.

## Privacy note (audit M3.5)

The working tree is scrubbed: the personal adapter MAC and real VIN are gone from
source, fixtures, launch settings, docs, and the tracked DevTools session captures
(deleted). **Git history still contains them.** Before making the repository public,
either squash history or run `git filter-repo` over the old blobs — publishing packages
does NOT expose history, so NuGet releases are safe from a private repo today.
