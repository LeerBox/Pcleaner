# PCleaner

### Let's get rid of ads

A Windows desktop cleaner for temporary files, browser caches, and local privacy traces. Review what your PC remembers, choose what to remove, and see the results before cleaning.

PCleaner can clear selected cookies, website identifiers, and other locally stored data. It does not block advertisements or network requests, and it cannot erase activity stored in your online accounts.

## What you can clean

| Area | Features |
| --- | --- |
| Windows | Temporary files, error reports, crash dumps, thumbnail and shader caches, and Microsoft Disk Cleanup handlers. |
| Applications | Selected graphics-driver and Microsoft application caches, plus supported recent-file lists. |
| Browsers | Profiles detected for Chrome, Edge, Brave, Opera, Vivaldi, Firefox, and other browsers in the [browser catalog](src/PCleaner.Core/Browsers/BrowserCatalog.cs). Available rules depend on the browser family. |
| Privacy | Opt-in cookies, Chromium browsing history, form history, local addresses, website identifiers, and website storage. |
| Opened files | Opt-in Windows recent-file lists and supported editor sessions, including Notepad and Notepad++. |
| Review | Per-item explanations, target paths, file or database-entry previews, risk labels, and an activity log. |

The **Privacy** page includes **Reset what sites know about me** and **Forget opened files** selection presets. Review their selected items before cleaning: deleting cookies can sign you out, and clearing editor sessions can remove unsaved tabs.

## Get started

PCleaner is built for Windows 10/11. Release packages target **Windows x64**.

1. Open [Releases](https://github.com/LeerBox/Pcleaner/releases) and download a published `PCleaner-<version>-win-x64.zip` package. If no release is available yet, [build from source](docs/BUILDING.md).
2. Extract the entire ZIP and run `PCleaner.exe`. The release package includes the .NET runtime.
3. Choose **Recommended**, then **Analyze**.
4. Review the selected items and their details, then choose **Clean now**.

Use **Restart as administrator** when you need system-wide items marked **Admin**. Save your work before closing applications for cleanup.

## You choose what is removed

- **Safe** identifies regenerable caches and temporary data; these rules are selected on first run.
- **Side effects** identifies actions such as emptying the Recycle Bin or clearing saved sessions; these start unselected.
- **Privacy** identifies personal traces such as cookies and recent-file lists; these also start unselected.

Your choices are remembered. The engine uses explicit cleanup targets, a separate path-validation layer, protected browser-data lists, and checks for running applications. Temporary files younger than 24 hours are kept by default. These safeguards reduce risk; cleanup can still remove data you wanted to keep. See [safety and scope](docs/SAFETY.md) before using advanced options.

## Build from source

On Windows, install the [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) selected by [global.json](global.json), then run:

```powershell
dotnet restore PCleaner.sln
dotnet build PCleaner.sln -c Release --no-restore
dotnet test tests/PCleaner.Core.Tests/PCleaner.Core.Tests.csproj -c Release --no-build --filter "Category!=RealMachine"
dotnet run --project src/PCleaner.App/PCleaner.App.csproj -c Release --no-build -- --no-elevate
```

For portable packages and asset rebuilding, see [the build guide](docs/BUILDING.md).

## Documentation

- [Usage and troubleshooting](docs/USAGE.md)
- [Safety and cleanup scope](docs/SAFETY.md)
- [Build and release guide](docs/BUILDING.md)
- [Implementation references](docs/REFERENCES.md)
- [Contributing](CONTRIBUTING.md)

## License

PCleaner source code is available under the [MIT License](LICENSE). Bundled fonts and dependencies retain their own licenses; see [third-party notices](THIRD_PARTY_NOTICES.md).

