# Build and release guide

## Requirements

- Windows 10/11 x64 for the application and portable package.
- [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) matching `global.json`.
- PowerShell 7 or later for the release script.

The solution treats warnings as errors and targets `net9.0-windows` with WPF.

## Build and test

From the repository root:

```powershell
dotnet restore PCleaner.sln
dotnet build PCleaner.sln -c Release --no-restore
dotnet test tests/PCleaner.Core.Tests/PCleaner.Core.Tests.csproj -c Release --no-build --filter "Category!=RealMachine"
dotnet run --project src/PCleaner.App/PCleaner.App.csproj -c Release --no-build -- --no-elevate
```

The `RealMachine` tests are excluded from normal CI because they inspect the host’s installed browsers and Windows state. Run them only in a disposable Windows environment where that inspection is acceptable.

## Create a portable package

The package is self-contained and targets `win-x64`:

```powershell
pwsh -File tools/release/Build-Release.ps1 -Version 1.0.0
```

The script publishes the application, includes license and dependency notices, creates `artifacts/releases/PCleaner-1.0.0-win-x64.zip`, and writes a matching `.sha256` file. ZIP entries use stable ordering and timestamps so repeated builds are reproducible when their published inputs are identical.

## Release workflow

1. Update the version in `Directory.Build.props`.
2. Run the build, isolated tests, and package script locally.
3. Confirm the tag exactly matches the project version, for example `v1.0.0` for version `1.0.0`.
4. Push the tag. The `Build release` workflow builds on `windows-latest`, runs the isolated tests, uploads the package artifact, and creates a draft GitHub release with the ZIP and checksum.
5. Verify the checksum and release notes, then publish the draft release.

The workflow is defined in `.github/workflows/release.yml`. Main-branch pushes and pull requests use `.github/workflows/ci.yml` to restore, build, test, and upload TRX results.

## Assets and source changes

The application icon is `src/PCleaner.App/Assets/PCleaner.ico`. Bundled font files and their notices live under `src/PCleaner.App/Fonts`. If these assets change, run the full build and the asset tests before packaging.

Keep release packages portable: the complete extracted directory is required at runtime. Update `tools/release/RELEASE_NOTES.md` when user-visible behavior or supported cleanup rules change.
