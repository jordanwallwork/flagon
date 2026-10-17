# Releasing

Packages are published to nuget.org by `.github/workflows/release.yml`, which runs only when a version tag is pushed.

## Versioning

[MinVer](https://github.com/adamralph/minver) derives every package's version from the latest `v`-prefixed git tag
(`v1.2.0` -> `1.2.0`). Untagged commits build as `<next patch>-alpha.0.<height>` pre-releases, so CI packages never
collide with released versions. All packages in the repo share one version.

## One-time setup

1. **Reserve the `Flagon.*` package ID prefix on nuget.org before the first publish.** Email `account@nuget.org` from
   the owning account asking for the `Flagon` prefix to be reserved (see
   [ID prefix reservation](https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation)). Reservation marks the
   packages as verified and stops anyone else publishing `Flagon.*` IDs. No packages named or prefixed `Flagon` existed
   on nuget.org as of 2026-10-16.
2. Create a nuget.org API key scoped to **Push new packages and package versions** with the glob pattern `Flagon*`.
3. Add it as a repository secret named `NUGET_API_KEY` (Settings -> Secrets and variables -> Actions).

## Cutting a release

```sh
git checkout main && git pull
git tag v1.0.0          # or a pre-release, e.g. v1.0.0-beta.1
git push origin v1.0.0
```

The workflow restores, builds, runs the full test suite (both target frameworks, Testcontainers stores), packs every
project under `src/` with symbols (`.snupkg`), checks each package carries the tag's version, pushes them to nuget.org
(`--skip-duplicate`, so a re-run is safe) and creates a GitHub release with the packages attached. Tags containing a
`-` are marked as pre-releases.

## What every package carries

Set once in `Directory.Build.props` for all projects under `src/`: MIT licence expression, project and repository URLs,
the repository `README.md` (rendered on nuget.org), `assets/icon.png`, SourceLink, deterministic CI builds and symbol
packages. Each project's `.csproj` supplies its own `PackageId` and `Description`.

To check locally: `dotnet pack -c Release -o artifacts`, then open a `.nupkg` (it is a zip) and inspect the `.nuspec`.
