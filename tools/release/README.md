# Release packaging

Run these commands from the repository root on Windows with PowerShell 7 and the
SDK specified in `global.json`:

```powershell
dotnet build PCleaner.sln -c Release
dotnet test tests/PCleaner.Core.Tests/PCleaner.Core.Tests.csproj -c Release --no-build --filter "Category!=RealMachine"
pwsh -File tools/release/Build-Release.ps1 -Version 1.0.0
```

The script publishes a self-contained Windows x64 application and creates:

- `artifacts/releases/PCleaner-1.0.0-win-x64.zip`
- `artifacts/releases/PCleaner-1.0.0-win-x64.zip.sha256`

The ZIP contains the application, .NET runtime, quick-start instructions, MIT
license, third-party notices, font licenses, dependency license texts, and the
resolved dependency inventory. It contains no local settings, logs, or browser
data. Packaging does not run the application or clean anything.

Each build uses a new staging directory under `artifacts/releases/`. Existing
ZIP/checksum files for the same version are replaced only after packaging
succeeds. Staging folders are left in place for inspection. Archive file order
and timestamps are normalized. The SDK is pinned; identical source and resolved
dependencies are required when comparing builds. Dependencies should be reviewed
again when project references or the SDK change.

## GitHub Actions

**Build and test** runs on pushes to `main`, pull requests, and manual starts.
Tests that inspect the machine's actual profile are excluded with
`Category!=RealMachine`.

**Build release** can be started manually to download the ZIP and checksum as a
workflow artifact. To create a draft GitHub release, update the project version
in `Directory.Build.props`, commit the change, and push a matching tag:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The workflow rejects tags that disagree with the project version. It builds and
tests first, then attaches the files to a draft release. Review the draft and
publish it from GitHub when ready. A repeat run does not overwrite an existing
release. The workflow uses GitHub's built-in token; no personal access token is
required.

## Vendored license sources

Some NuGet packages declare license expressions without including the full text.
The files in `licenses/` are copied unmodified from these upstream sources:

- `SQLitePCLRaw-LICENSE.txt`: <https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.11/LICENSE.TXT>
- `Microsoft.Data.Sqlite.Core-LICENSE.txt`: <https://github.com/dotnet/efcore/blob/v9.0.19/LICENSE.txt>

The package script also copies available license/notice files and the `.nuspec`
metadata from the exact restored package versions, including the .NET runtime
packs. Microsoft system fonts and Windows' `winsqlite3.dll` are not redistributed.