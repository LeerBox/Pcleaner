# Implementation references

These are the research notes accompanying the implemented cleanup rules. Paths and formats can change between browser and Windows versions. Consult the rule source and tests before extending a target; this list is not a compatibility guarantee.

Each cleanup rule also carries a source reference shown in its details pane. See [safety and scope](SAFETY.md) for the boundaries enforced by the engine.

* Microsoft Learn: Disk Cleanup handler model (`IEmptyVolumeCache`), Windows Update reset procedure, WER settings,
  Windows Update / Setup log files, Delivery Optimization FAQ, WinSxS cleanup (DISM), `SHEmptyRecycleBin`,
  `MoveFileEx`, `wevtutil`, Teams cache guidance; `RegLoadAppKey` (application hives), `SHGetPathFromIDListEx`,
  `SHAddToRecentDocs` and the ADMX_StartMenu policy `ClearRecentDocsOnExit` (what "recently opened documents" are).
* Recent-file stores (for the "forget opened files" rules): Windows 11 Notepad state files as documented by the
  *ogmini/Notepad-State-Library* research (`TabState\*.bin`: `NP` magic, sequence number, type flag 1 = file with
  UTF-16 path / 0 = unsaved tab whose text exists only there, `.0.bin`/`.1.bin` double-buffered state; `settings.dat`
  values `RecentFiles`, `RecentFilesEnabled`) and the Windows Insider Blog announcement of Notepad's *Recent Files*
  (2025-03-13); Explorer MRU formats from *libyal/winreg-kb* (`RecentDocs`, `OpenSavePidlMRU`, `LastVisitedPidlMRU`,
  `TypedPaths`, `WordWheelQuery`) and Eric Zimmerman's *RegistryPlugins*; Notepad++ `PowerEditor/src/Parameters.cpp`
  (`session.xml`, `.inCaseOfCorruption.bak`, `<History>`, `backup\`) and its user manual (*configuration files are
  written on exit*); VLC `modules/gui/qt/recents.cpp` and `qt.cpp` (`[RecentsMRL]`, `filedialog-path`, saved on exit);
  7-Zip `FileManager/ViewSettings.cpp` and `UI/Common/ZipRegistry.cpp` (`FM\FolderHistory`, `FM\CopyHistory`,
  `Extraction\PathHistory`, `Compression\ArcHistory` – NUL-separated UTF-16 lists; `FolderShortcuts` are favourites and
  stay); Adobe *Acrobat Preference Reference* (`AVGeneral\cRecentFiles`, up to `iMaxMRUCntToBeStored` = 100 entries);
  Office `File MRU` item format `[F…][T<FILETIME>][O…]*path` (*libyal/winreg-kb* "Microsoft Office", RegRipper
  `msoffice.pl` for `Reading Locations`); MFC `CRecentFileList` ("Recent File List" sections of Paint and WordPad);
  cross-checked against the BleachBit cleaner definitions (`windows_explorer.xml`, `vlc.xml`, `winrar.xml`,
  `paint.xml`, `wordpad.xml`, `windows_media_player.xml`) and Winapp2.ini. Unverified stores (Windows 11 Paint and
  the new Media Player, `PersistedStorageItemTable`) were deliberately left out.
* Chromium source: `chrome/common/chrome_constants.cc`, `chrome_paths*.cc`, `profile_network_context_service.cc`,
  `storage_partition_impl.cc`, `gpu_disk_cache_type.cc`, `extensions/common/constants.h`, session constants,
  browsing-data remover mapping; Brave and Edge specifics.
* Chromium autofill storage (for the row-level form-history rules): `components/autofill/core/browser/webdata/`
  `autocomplete/autocomplete_table.cc` (table `autofill`), `autocomplete_table_label_sensitive.cc` (table
  `autocomplete`), `autocomplete_sync_bridge.cc` (storage keys, REMOVE vs. EXPIRE paths),
  `autofill_sync_metadata_table.cc` + `components/sync/base/data_type.cc` (`model_type` identifiers 7 = autocomplete,
  6 = addresses), `addresses/address_autofill_table.cc` + `autofill_profile.h` (`record_type` 0 = local),
  `payments/payments_autofill_table.cc` (protected tables), `sql/database.cc` (exclusive locking, `secure_delete`);
  `components/webdata_services/web_data_service_wrapper.cc`. Microsoft Edge's `autofill_edge_*` tables were mapped
  from their schema (Edge is closed source).
* Chromium Local Storage & LevelDB (for the website-identifier rule): `components/services/storage/dom_storage/`
  (`local_storage_impl.cc`, `dom_storage_database.cc` – `Local Storage\leveldb` path, `META:` / `METAACCESS:` /
  `VERSION` records, `_<origin>\0` data keys, usage-metadata protobufs; `dom_storage_rollout.cc` – the staged
  migration to a SQLite backend and its `exp-v1` folder marker, which PCleaner carries over unchanged),
  `third_party/blink/renderer/modules/storage/cached_storage_area.cc` (`StorageFormat { UTF16 = 0, Latin1 = 1 }`
  key/value prefix byte); LevelDB `doc/log_format.md`, `doc/table_format.md`, `doc/impl.md`, `db/version_edit.cc`
  (MANIFEST tags), `db/write_batch.cc`, `util/crc32c.cc` (masked CRC32C) and the Snappy format description. Compatibility tests in this repository exercise LevelDB round trips against fixture data and copies of
  available Brave and Edge profiles. These checks do not establish compatibility with every browser version. SQLite-backed Local Storage (`<profile>\LocalStorage`, tables `maps` / `map_entries`) is outside the
  current LevelDB rule. A migrated profile without that LevelDB folder is not cleaned by this rule.
* Platform identifiers (for the Privacy page, cookie *names* only): Google *Types of cookies used by Google*
  (`SID`, `HSID`, `SSID`, `LOGIN_INFO`, `DSID`) and *My Activity* / *My Ad Center* help; TikTok cookie policy
  (`sessionid`, `sid_tt`, `ttwid`) and *Watch history*; Meta, X, Reddit, Twitch and Netflix cookie / privacy
  policies; Microsoft Learn *Advertising ID* (`HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo`,
  a new ID is generated whenever the setting is turned back on; `ms-settings:privacy-general`).
* Mozilla source & docs: Profiles service documentation, `nsToolkitProfileService.cpp`, `nsXREDirProvider.cpp`,
  `nsProfileLock.cpp`, `netwerk/cache2`, `dom/quota`, WebExtension storage modules, Backup resources;
  `toolkit/components/satchel/FormHistory.sys.mjs` (schema v5, remove path, source pruning) and
  `browser/modules/Sanitizer.sys.mjs` for form history.
* Open-source cleaner rule sets (BleachBit, Winapp2/Winapp3) – used to cross-check, including their documented
  mistakes (e.g. deleting `cert9.db`, `Extension State`, `catroot2`).