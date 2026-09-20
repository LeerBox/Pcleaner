# Third-party notices

The [MIT License](LICENSE) for PCleaner does not replace the licenses of its dependencies and bundled assets. Release packages include third-party license and notice files alongside the application. Package versions and resolved dependencies are recorded by NuGet during restore.

## Ubuntu derivative PCleaner fonts

The embedded **Ubuntu derivative PCleaner** family is derived from Ubuntu, designed by Dalton Maag Ltd.

Copyright 2010, 2011 Canonical Ltd.

The font software is distributed under the **Ubuntu Font Licence 1.0**. The license text is in [LICENCE-Ubuntu-UFL.txt](src/PCleaner.App/Fonts/LICENCE-Ubuntu-UFL.txt); attribution accompanies the fonts in [COPYRIGHT-Ubuntu.txt](src/PCleaner.App/Fonts/COPYRIGHT-Ubuntu.txt). These texts are also embedded as application resources.

PCleaner's build script subsets the fonts, removes standard ligatures, makes selected tabular symbol variants the defaults, and renames the family. It retains glyph outlines and hinting. The fonts retain their own license, separately from PCleaner's source code.

Source: [Ubuntu in Google Fonts](https://github.com/google/fonts/tree/main/ufl/ubuntu). License: [Ubuntu Font Licence](https://ubuntu.com/legal/font-licence). This project is not endorsed by Canonical or Dalton Maag.

## Application dependencies

The table lists the package families used by the application. Exact resolved versions may include transitive dependencies in addition to the versions declared in the project files.

| Component | Purpose | License / attribution |
| --- | --- | --- |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM helpers and generated commands/properties. | MIT; .NET Foundation and Contributors. |
| [Microsoft.Data.Sqlite.Core](https://github.com/dotnet/efcore) | Managed SQLite API. | MIT; Microsoft Corporation. |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (`core`, `bundle_winsqlite3`, `provider.winsqlite3`) | SQLite bindings and Windows SQLite provider. | Apache-2.0; SourceGear, LLC. |
| [System.ServiceProcess.ServiceController](https://github.com/dotnet/runtime) | Windows service integration. | MIT; .NET Foundation and Contributors / Microsoft Corporation. |
| [System.Diagnostics.EventLog](https://github.com/dotnet/runtime) | Windows event-log integration. | MIT; .NET Foundation and Contributors / Microsoft Corporation. |
| [System.Memory](https://github.com/dotnet/runtime) | Transitive managed memory APIs. | MIT; .NET Foundation and Contributors / Microsoft Corporation. |
| [.NET / Windows Desktop Runtime](https://github.com/dotnet/runtime) | Runtime included in self-contained releases. | MIT and accompanying third-party notices; .NET Foundation and Contributors. |

The SQLite provider uses `winsqlite3.dll` supplied by Windows. PCleaner does not ship a separate SQLite native engine through this provider. Microsoft Segoe Fluent Icons and Segoe MDL2 Assets are referenced as installed Windows system fonts; their font files are not included in the repository.

The custom LevelDB reader/writer implements the storage format inside `src/PCleaner.Core/Storage/LevelDb`; PCleaner does not link a native LevelDB library. Research references are recorded in [implementation references](docs/REFERENCES.md).

## Development dependencies

Tests use xUnit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, and coverlet.collector. These are development dependencies rather than application features. Optional font rebuilding uses fontTools. Their licenses remain with their upstream packages; review those notices when distributing development tools or modified copies.