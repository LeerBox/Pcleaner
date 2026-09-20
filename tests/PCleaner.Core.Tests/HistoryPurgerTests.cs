using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32;
using PCleaner.Core.Engine;
using PCleaner.Core.Model;

namespace PCleaner.Core.Tests;

public sealed class HistoryPurgerTests
{
    private const string NotepadStateSuffix = @"Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\LocalState";
    private const string NotepadSettingsSuffix = @"Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\Settings\settings.dat";
    private const string ScratchKey = @"Software\PCleanerTests";

    // ------------------------------------------------------------------ Notepad tabs

    private static byte[] SavedTab(string path)
    {
        var pathBytes = Encoding.Unicode.GetBytes(path);
        return [(byte)'N', (byte)'P', 0, 1, (byte)path.Length, .. pathBytes, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0];
    }

    private static byte[] UnsavedTab(string text)
    {
        var textBytes = Encoding.Unicode.GetBytes(text);
        var length = (byte)text.Length;
        return [(byte)'N', (byte)'P', 0, 0, 1, 0, 0, 1, 0, 0, 3, 1, 1, 1, length, .. textBytes, 1, 0, 0, 0, 0];
    }

    [Fact]
    public void Notepad_tabs_are_listed_and_deleted_but_folders_kept()
    {
        using var tree = new TempTree();
        var localState = tree.Dir(NotepadStateSuffix);
        var tabState = tree.Dir(NotepadStateSuffix + @"\TabState");
        var windowState = tree.Dir(NotepadStateSuffix + @"\WindowState");
        File.WriteAllBytes(Path.Combine(tabState, "11111111-1111-1111-1111-111111111111.bin"), SavedTab(@"C:\Users\me\notes.txt"));
        File.WriteAllBytes(Path.Combine(tabState, "11111111-1111-1111-1111-111111111111.0.bin"), [(byte)'N', (byte)'P', 0, 10, 0, 0]);
        File.WriteAllBytes(Path.Combine(tabState, "22222222-2222-2222-2222-222222222222.bin"), UnsavedTab("Shopping list\r\nmilk"));
        File.WriteAllBytes(Path.Combine(windowState, "33333333-3333-3333-3333-333333333333.0.bin"), [(byte)'N', (byte)'P', 0, 0, 0]);

        var target = new HistoryTarget { Store = HistoryStore.NotepadTabs, Location = localState, Label = "Tab" };
        Assert.Null(SafetyGuard.ValidateHistoryTarget(target));

        var inspection = HistoryPurger.Inspect(target);
        Assert.True(inspection.Exists);
        Assert.Equal(2, inspection.EntryCount);
        Assert.Contains(inspection.Entries, e => e.Field == "Open file" && e.Value == @"C:\Users\me\notes.txt");
        Assert.Contains(inspection.Entries, e => e.Field == "Unsaved tab" && e.Value == "Shopping list");
        Assert.True(inspection.Bytes > 0);

        var dry = HistoryPurger.Purge(target, dryRun: true);
        Assert.Equal(2, dry.EntriesRemoved);
        Assert.Equal(4, Directory.GetFiles(localState, "*.bin", SearchOption.AllDirectories).Length);

        var result = HistoryPurger.Purge(target, dryRun: false);
        Assert.Null(result.Error);
        Assert.Equal(2, result.EntriesRemoved);
        Assert.Empty(Directory.GetFiles(localState, "*.bin", SearchOption.AllDirectories));
        Assert.True(Directory.Exists(tabState));
        Assert.True(Directory.Exists(windowState));
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Real_notepad_tabs_on_this_machine_are_readable()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), NotepadStateSuffix, "TabState");
        if (!Directory.Exists(real))
        {
            return; // Store Notepad not installed here
        }

        using var tree = new TempTree();
        var copy = tree.Dir(NotepadStateSuffix + @"\TabState");
        foreach (var file in Directory.GetFiles(real, "*.bin"))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }

        var inspection = HistoryPurger.Inspect(new HistoryTarget { Store = HistoryStore.NotepadTabs, Location = tree.Dir(NotepadStateSuffix), Label = "Tab" });
        Assert.Null(inspection.Error);
        Assert.Equal(inspection.EntryCount, inspection.Entries.Count);
        Assert.All(inspection.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Value)));
        Assert.All(inspection.Entries.Where(e => e.Field == "Open file"), e => Assert.True(Path.IsPathFullyQualified(e.Value), e.Value));
    }

    [Fact]
    public void Varint_and_first_line_parsing()
    {
        var offset = 0;
        Assert.True(HistoryPurger.TryReadVarint([0xAC, 0x01], ref offset, out var value));
        Assert.Equal(172UL, value);
        Assert.Equal(2, offset);

        offset = 0;
        Assert.False(HistoryPurger.TryReadVarint([0x80], ref offset, out _));

        var tab = HistoryPurger.ReadNotepadTab(WriteTemp(UnsavedTab("first line\nsecond")));
        Assert.NotNull(tab);
        Assert.False(tab.Value.IsSaved);
        Assert.Equal("first line", tab.Value.Title);

        var saved = HistoryPurger.ReadNotepadTab(WriteTemp(SavedTab(@"D:\x.txt")));
        Assert.Equal((true, @"D:\x.txt"), saved);

        Assert.Null(HistoryPurger.ReadNotepadTab(WriteTemp([1, 2, 3])));
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "PCleanerTests", Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ------------------------------------------------------------------ Notepad recent files (settings.dat hive)

    [Fact]
    public void Notepad_recent_files_are_reset_to_an_empty_list_and_other_settings_kept()
    {
        using var tree = new TempTree();
        var settings = Path.Combine(tree.Dir(Path.GetDirectoryName(NotepadSettingsSuffix)!), "settings.dat");
        var json = "[\"C:\\\\Users\\\\me\\\\a.txt\",\"D:\\\\b.log\"]";
        var text = Encoding.Unicode.GetBytes(json + "\0");
        var raw = new byte[text.Length + 8];
        text.CopyTo(raw, 0);
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(text.Length), DateTime.UtcNow.ToFileTimeUtc());

        using (var hive = HistoryPurger.AppHive.Open(settings, writable: true, out _))
        {
            Assert.NotNull(hive);
            using var localState = hive.CreateSubKey("LocalState", writable: true);
            HistoryPurger.AppHive.WriteRaw(localState, "RecentFiles", 0x5f5e10c, raw);
            HistoryPurger.AppHive.WriteRaw(localState, "WindowPositionLeft", 0x5f5e104, [0x2A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            localState.Flush();
        }

        var target = new HistoryTarget { Store = HistoryStore.NotepadRecentFiles, Location = settings, Label = "Recent files" };
        Assert.Null(SafetyGuard.ValidateHistoryTarget(target));

        var inspection = HistoryPurger.Inspect(target);
        Assert.Null(inspection.Error);
        Assert.Equal(2, inspection.EntryCount);
        Assert.Equal(@"C:\Users\me\a.txt", inspection.Entries[0].Value);

        var result = HistoryPurger.Purge(target, dryRun: false);
        Assert.Null(result.Error);
        Assert.Equal(2, result.EntriesRemoved);

        Assert.Equal(0, HistoryPurger.Inspect(target).EntryCount);
        using (var hive = HistoryPurger.AppHive.Open(settings, writable: false, out _))
        {
            using var localState = hive!.OpenSubKey("LocalState")!;
            var recent = HistoryPurger.AppHive.ReadRaw(localState, "RecentFiles", out var type);
            Assert.Equal(0x5f5e10cu, type);
            Assert.Equal("[]", Encoding.Unicode.GetString(recent!, 0, recent!.Length - 8).TrimEnd('\0'));
            Assert.NotNull(HistoryPurger.AppHive.ReadRaw(localState, "WindowPositionLeft", out _));
        }
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Real_notepad_settings_copy_is_readable_and_clearable()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), NotepadSettingsSuffix);
        if (!File.Exists(real))
        {
            return;
        }

        using var tree = new TempTree();
        var copy = Path.Combine(tree.Dir(Path.GetDirectoryName(NotepadSettingsSuffix)!), "settings.dat");
        try
        {
            File.Copy(real, copy);
        }
        catch (IOException)
        {
            return; // Notepad is running and holds the hive
        }

        var target = new HistoryTarget { Store = HistoryStore.NotepadRecentFiles, Location = copy, Label = "Recent files" };
        var before = HistoryPurger.Inspect(target);
        Assert.Null(before.Error);
        Assert.All(before.Entries, e => Assert.True(Path.IsPathFullyQualified(e.Value), e.Value));

        var result = HistoryPurger.Purge(target, dryRun: false);
        Assert.Null(result.Error);
        Assert.Equal(0, HistoryPurger.Inspect(target).EntryCount);
    }

    // ------------------------------------------------------------------ registry MRUs

    private static RegistryKey Scratch(string relative) => Registry.CurrentUser.CreateSubKey($@"{ScratchKey}\{relative}", writable: true);

    private static void RemoveScratch() => Registry.CurrentUser.DeleteSubKeyTree(ScratchKey, throwOnMissingSubKey: false);

    private static byte[] RecentDocsValue(string name) => [.. Encoding.Unicode.GetBytes(name + "\0"), 0x14, 0, 0x1F, 0x50, 0xE0, 0x4F, 0, 0];

    [Fact]
    public void Registry_key_contents_are_listed_once_and_cleared_without_deleting_the_key()
    {
        RemoveScratch();
        try
        {
            using (var recent = Scratch("Explorer\\RecentDocs"))
            {
                recent.SetValue("0", RecentDocsValue("report.docx"), RegistryValueKind.Binary);
                recent.SetValue("1", RecentDocsValue("photo.jpg"), RegistryValueKind.Binary);
                recent.SetValue("MRUListEx", new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }, RegistryValueKind.Binary);
                using var ext = recent.CreateSubKey(".docx");
                ext.SetValue("0", RecentDocsValue("report.docx"), RegistryValueKind.Binary);
                ext.SetValue("MRUListEx", new byte[] { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }, RegistryValueKind.Binary);
            }

            var target = new HistoryTarget { Store = HistoryStore.RegistryKeyContents, Location = $@"{ScratchKey}\Explorer\RecentDocs", Label = "Recent documents" };
            var inspection = HistoryPurger.Inspect(target);
            Assert.True(inspection.Exists);
            Assert.Equal(2, inspection.EntryCount); // the per-extension copy is not counted twice
            Assert.Equal(["report.docx", "photo.jpg"], inspection.Entries.Select(e => e.Value).Order().Reverse().ToArray());

            var result = HistoryPurger.Purge(target, dryRun: false);
            Assert.Null(result.Error);
            Assert.Equal(2, result.EntriesRemoved);

            using var after = Registry.CurrentUser.OpenSubKey($@"{ScratchKey}\Explorer\RecentDocs");
            Assert.NotNull(after);
            Assert.Empty(after.GetValueNames());
            Assert.Empty(after.GetSubKeyNames());
            Assert.Equal(0, HistoryPurger.Inspect(target).EntryCount);
        }
        finally
        {
            RemoveScratch();
        }
    }

    [Fact]
    public void Office_items_with_wildcard_accounts_decode_path_and_time()
    {
        RemoveScratch();
        try
        {
            var stamp = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
            using (var mru = Scratch(@"Office\Word\User MRU\LiveId_1\File MRU"))
            {
                mru.SetValue("Item 1", $"[F00000000][T{stamp:X16}][O00000000]*C:\\Users\\me\\Documents\\thesis.docx");
            }

            using (var mru = Scratch(@"Office\Word\User MRU\ADAL_2\File MRU"))
            {
                mru.SetValue("Item 1", "[F00000000][T0000000000000000][O00000000]*D:\\work\\budget.docx");
            }

            var target = new HistoryTarget { Store = HistoryStore.RegistryKeyContents, Location = $@"{ScratchKey}\Office\Word\User MRU\*\File MRU", Label = "Word" };
            var inspection = HistoryPurger.Inspect(target);
            Assert.Equal(2, inspection.EntryCount);
            var thesis = Assert.Single(inspection.Entries, e => e.Value == @"C:\Users\me\Documents\thesis.docx");
            Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), thesis.LastUsedUtc);
            Assert.Null(Assert.Single(inspection.Entries, e => e.Value == @"D:\work\budget.docx").LastUsedUtc);

            Assert.Equal(2, HistoryPurger.Purge(target, dryRun: false).EntriesRemoved);
            Assert.Equal(0, HistoryPurger.Inspect(target).EntryCount);
            Assert.NotNull(Registry.CurrentUser.OpenSubKey($@"{ScratchKey}\Office\Word\User MRU\LiveId_1\File MRU"));
        }
        finally
        {
            RemoveScratch();
        }
    }

    [Fact]
    public void Named_values_only_are_removed_and_string_lists_decoded()
    {
        RemoveScratch();
        try
        {
            using (var fm = Scratch(@"7-Zip\FM"))
            {
                fm.SetValue("FolderHistory", Encoding.Unicode.GetBytes("C:\\a\0D:\\b\0"), RegistryValueKind.Binary);
                fm.SetValue("FolderShortcuts", Encoding.Unicode.GetBytes("C:\\fav\0"), RegistryValueKind.Binary);
                fm.SetValue("PanelPath0", @"C:\a");
            }

            var target = new HistoryTarget { Store = HistoryStore.RegistryValues, Location = $@"{ScratchKey}\7-Zip\FM", Label = "File Manager", ValueNames = ["FolderHistory", "PanelPath0", "PathHistory"] };
            var inspection = HistoryPurger.Inspect(target);
            Assert.Equal(3, inspection.EntryCount); // two folders in the history list + the panel path; the missing value is ignored
            Assert.Contains(inspection.Entries, e => e.Value == @"D:\b");

            Assert.Equal(3, HistoryPurger.Purge(target, dryRun: false).EntriesRemoved);
            using var after = Registry.CurrentUser.OpenSubKey($@"{ScratchKey}\7-Zip\FM")!;
            Assert.Equal(["FolderShortcuts"], after.GetValueNames());
        }
        finally
        {
            RemoveScratch();
        }
    }

    [Fact]
    public void Entry_subkeys_and_run_box_values_are_decoded()
    {
        RemoveScratch();
        try
        {
            using (var run = Scratch(@"Explorer\RunMRU"))
            {
                run.SetValue("a", "cmd\\1");
                run.SetValue("b", "notepad C:\\x.txt\\1");
                run.SetValue("MRUList", "ba");
            }

            using (var pdf = Scratch(@"Adobe\cRecentFiles\c1"))
            {
                pdf.SetValue("tDIText", @"C:\docs\invoice.pdf");
                pdf.SetValue("sDate", "D:20260901120000+02'00'");
                pdf.SetValue("uPageCount", 3, RegistryValueKind.DWord);
            }

            var runBox = HistoryPurger.Inspect(new HistoryTarget { Store = HistoryStore.RegistryKeyContents, Location = $@"{ScratchKey}\Explorer\RunMRU", Label = "Run box" });
            Assert.Equal(["cmd", "notepad C:\\x.txt"], runBox.Entries.Select(e => e.Value).Order().ToArray());

            var adobe = HistoryPurger.Inspect(new HistoryTarget { Store = HistoryStore.RegistryKeyContents, Location = $@"{ScratchKey}\Adobe\cRecentFiles", Label = "Acrobat" });
            var entry = Assert.Single(adobe.Entries);
            Assert.Equal(@"C:\docs\invoice.pdf", entry.Value);
            Assert.NotNull(entry.LastUsedUtc);
        }
        finally
        {
            RemoveScratch();
        }
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Real_open_save_dialog_history_resolves_to_paths()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU");
        if (key is null || key.SubKeyCount == 0)
        {
            return;
        }

        var inspection = HistoryPurger.Inspect(new HistoryTarget { Store = HistoryStore.RegistryKeyContents, Location = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU", Label = "Open/Save dialogs" });
        Assert.Null(inspection.Error);
        Assert.True(inspection.EntryCount > 0);
        Assert.Contains(inspection.Entries, e => e.Value.Contains('\\', StringComparison.Ordinal));
    }

    [Fact]
    public void Item_list_validation_rejects_garbage()
    {
        Assert.False(HistoryPurger.IsWellFormedItemList([0x14, 0, 1, 2]));            // item runs past the end
        Assert.False(HistoryPurger.IsWellFormedItemList([0, 0]));                      // no items
        Assert.True(HistoryPurger.IsWellFormedItemList([4, 0, 0xAA, 0xBB, 0, 0]));     // one item + terminator
        Assert.Null(HistoryPurger.ShellItemListPath([4, 0, 0xAA, 0xBB, 0, 0]));       // well formed but meaningless
        Assert.Equal("abc", HistoryPurger.LeadingUtf16(Encoding.Unicode.GetBytes("abc\0zzz"), out var consumed));
        Assert.Equal(8, consumed);
    }

    // ------------------------------------------------------------------ Notepad++

    private const string NppConfig = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\r\n<NotepadPlus>\r\n    <GUIConfigs>\r\n        <GUIConfig name=\"RememberLastSession\">yes</GUIConfig>\r\n    </GUIConfigs>\r\n    <History nbMaxFile=\"10\" inSubMenu=\"no\" customLength=\"-1\">\r\n        <File filename=\"C:\\a.txt\" />\r\n        <File filename=\"C:\\b.txt\" />\r\n    </History>\r\n</NotepadPlus>\r\n";

    private const string NppSession = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\r\n<NotepadPlus>\r\n    <Session activeView=\"0\">\r\n        <mainView activeIndex=\"1\">\r\n            <File firstVisibleLine=\"0\" filename=\"C:\\a.txt\" backupFilePath=\"\" />\r\n            <File firstVisibleLine=\"0\" filename=\"new 1\" backupFilePath=\"C:\\Users\\me\\AppData\\Roaming\\Notepad++\\backup\\new 1@2026-09-08_001322\" />\r\n        </mainView>\r\n        <subView activeIndex=\"0\" />\r\n    </Session>\r\n</NotepadPlus>\r\n";

    [Fact]
    public void Notepad_plus_plus_session_and_history_are_forgotten_settings_kept()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("Notepad++");
        var config = tree.File(@"Notepad++\config.xml", NppConfig);
        tree.File(@"Notepad++\session.xml", NppSession);
        tree.File(@"Notepad++\session.xml.inCaseOfCorruption.bak", NppSession);
        tree.File(@"Notepad++\backup\new 1@2026-09-08_001322", "unsaved text");
        tree.File(@"Notepad++\shortcuts.xml", "<NotepadPlus/>");

        var session = new HistoryTarget { Store = HistoryStore.NotepadPlusPlusSession, Location = folder, Label = "Tab" };
        var history = new HistoryTarget { Store = HistoryStore.NotepadPlusPlusRecentFiles, Location = folder, Label = "Recent files" };
        Assert.Null(SafetyGuard.ValidateHistoryTarget(session));
        Assert.Null(SafetyGuard.ValidateHistoryTarget(history));

        var tabs = HistoryPurger.Inspect(session);
        Assert.Equal(2, tabs.EntryCount);
        Assert.Contains(tabs.Entries, e => e.Field == "Unsaved tab" && e.Value == "new 1");
        Assert.Contains(tabs.Entries, e => e.Field == "Open tab" && e.Value == @"C:\a.txt");
        Assert.Equal(2, HistoryPurger.Inspect(history).EntryCount);

        Assert.Null(HistoryPurger.Purge(session, dryRun: false).Error);
        Assert.False(File.Exists(Path.Combine(folder, "session.xml")));
        Assert.False(File.Exists(Path.Combine(folder, "session.xml.inCaseOfCorruption.bak")));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "backup")));
        Assert.True(File.Exists(Path.Combine(folder, "shortcuts.xml")));

        Assert.Equal(2, HistoryPurger.Purge(history, dryRun: false).EntriesRemoved);
        var after = File.ReadAllText(config);
        Assert.DoesNotContain("<File ", after, StringComparison.Ordinal);
        Assert.Contains("<History nbMaxFile=\"10\" inSubMenu=\"no\" customLength=\"-1\"", after, StringComparison.Ordinal);
        Assert.Contains("RememberLastSession", after, StringComparison.Ordinal);
        Assert.Contains("<?xml version=\"1.0\" encoding=\"utf-8\"", after, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, HistoryPurger.Inspect(history).EntryCount);
        Assert.Equal(0, HistoryPurger.Inspect(session).EntryCount);
    }

    // ------------------------------------------------------------------ VLC

    [Fact]
    public void Vlc_recent_media_lines_are_removed_and_everything_else_kept()
    {
        using var tree = new TempTree();
        var ini = tree.File(@"vlc\vlc-qt-interface.ini", "[General]\r\nfiledialog-path=C:/Users/me/Videos\r\ngeometry=@ByteArray(abc)\r\n\r\n[RecentsMRL]\r\nlist=file:///C:/Users/me/Videos/a.mp4, file:///D:/clip%20two.mkv, \"file:///E:/with,comma.avi\"\r\ntimes=0, 1234, -1\r\n\r\n[OpenDialog]\r\nnetMRL=https://example.test/stream\r\nvolume=50\r\n");
        var target = new HistoryTarget { Store = HistoryStore.VlcRecentMedia, Location = ini, Label = "Recent media" };
        Assert.Null(SafetyGuard.ValidateHistoryTarget(target));

        var inspection = HistoryPurger.Inspect(target);
        Assert.Equal(5, inspection.EntryCount); // 3 media + last folder + network stream
        Assert.Contains(inspection.Entries, e => e.Value == @"D:\clip two.mkv");
        Assert.Contains(inspection.Entries, e => e.Value == @"E:\with,comma.avi");

        Assert.Equal(5, HistoryPurger.Purge(target, dryRun: false).EntriesRemoved);
        var after = File.ReadAllText(ini);
        Assert.Equal("[General]\r\ngeometry=@ByteArray(abc)\r\n\r\n[RecentsMRL]\r\n\r\n[OpenDialog]\r\nvolume=50\r\n", after);
        Assert.Equal(0, HistoryPurger.Inspect(target).EntryCount);
    }

    // ------------------------------------------------------------------ guard + engine

    [Theory]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", true)]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU", true)]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\Microsoft\Office\16.0\Word\User MRU\*\File MRU", true)]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\Adobe\Acrobat Reader\*\AVGeneral\cRecentFiles", true)]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\Microsoft\Windows\CurrentVersion\Explorer", false)]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\Microsoft\Windows\CurrentVersion\Run", false)]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", false)]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\7-Zip\FM", false)]
    [InlineData(HistoryStore.RegistryKeyContents, @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", false)]
    [InlineData(HistoryStore.RegistryKeyContents, @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs\..\..", false)]
    public void Guard_allow_list_for_registry_history(HistoryStore store, string location, bool allowed)
    {
        var reason = SafetyGuard.ValidateHistoryTarget(new HistoryTarget { Store = store, Location = location, Label = "x" });
        Assert.Equal(allowed, reason is null);
    }

    [Fact]
    public void Guard_checks_value_names_and_file_locations()
    {
        Assert.Null(SafetyGuard.ValidateHistoryTarget(new HistoryTarget { Store = HistoryStore.RegistryValues, Location = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", Label = "x", ValueNames = ["LastKey"] }));
        Assert.NotNull(SafetyGuard.ValidateHistoryTarget(new HistoryTarget { Store = HistoryStore.RegistryValues, Location = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", Label = "x", ValueNames = ["Favorites"] }));
        Assert.NotNull(SafetyGuard.ValidateHistoryTarget(new HistoryTarget { Store = HistoryStore.RegistryValues, Location = @"Software\7-Zip\FM", Label = "x", ValueNames = ["FolderShortcuts"] }));
        Assert.NotNull(SafetyGuard.ValidateHistoryTarget(new HistoryTarget { Store = HistoryStore.RegistryValues, Location = @"Software\7-Zip\FM", Label = "x" }));
        Assert.NotNull(SafetyGuard.ValidateHistoryTarget(new HistoryTarget { Store = HistoryStore.NotepadTabs, Location = @"C:\Users\me\AppData\Local\Packages\SomeOtherApp\LocalState", Label = "x" }));
        Assert.NotNull(SafetyGuard.ValidateHistoryTarget(new HistoryTarget { Store = HistoryStore.NotepadPlusPlusSession, Location = @"C:\Users\me\AppData\Roaming", Label = "x" }));
        Assert.NotNull(SafetyGuard.ValidateHistoryTarget(new HistoryTarget { Store = HistoryStore.VlcRecentMedia, Location = @"C:\Users\me\AppData\Roaming\vlc\vlcrc", Label = "x" }));
    }

    [Fact]
    public async Task Engine_forgets_notepad_tabs_end_to_end()
    {
        using var tree = new TempTree();
        var localState = tree.Dir(NotepadStateSuffix);
        var tabState = tree.Dir(NotepadStateSuffix + @"\TabState");
        File.WriteAllBytes(Path.Combine(tabState, "11111111-1111-1111-1111-111111111111.bin"), SavedTab(@"C:\Users\me\notes.txt"));
        File.WriteAllBytes(Path.Combine(tabState, "22222222-2222-2222-2222-222222222222.bin"), UnsavedTab("draft"));

        var rule = new CleanupRule
        {
            Id = "test.recent.notepad",
            Name = "Notepad tabs",
            Description = "test",
            Category = RuleCategory.WindowsUser,
            Group = "Test",
            Risk = RiskLevel.Moderate,
            Action = RuleAction.ForgetHistory,
            RelaunchClosedApplications = false,
            History = [new HistoryTarget { Store = HistoryStore.NotepadTabs, Location = localState, Label = "Tab" }],
        };

        var scans = await new Scanner().ScanAsync([rule], null, CancellationToken.None);
        var scan = Assert.Single(scans);
        Assert.Equal(SkipReason.None, scan.Skip);
        Assert.Equal(2, scan.EntryCount);
        Assert.Contains(scan.Entries, e => e.Field == "Unsaved tab" && e.Value == "draft");

        var cleans = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);
        var clean = Assert.Single(cleans);
        Assert.Equal(SkipReason.None, clean.Skip);
        Assert.Equal(2, clean.DeletedEntries);
        Assert.Contains("forgotten", clean.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(tabState));

        var again = await new Scanner().ScanAsync([rule], null, CancellationToken.None);
        Assert.Equal(0, again[0].EntryCount);
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public async Task Engine_reports_missing_stores_as_not_present()
    {
        var rule = new CleanupRule
        {
            Id = "test.recent.missing",
            Name = "Missing",
            Description = "test",
            Category = RuleCategory.Applications,
            Group = "Test",
            Action = RuleAction.ForgetHistory,
            History = [new HistoryTarget { Store = HistoryStore.RegistryKeyContents, Location = @"Software\Microsoft\Windows\CurrentVersion\Applets\Wordpad\Recent File List", Label = "WordPad" }],
        };

        using var existing = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Wordpad\Recent File List");
        var scans = await new Scanner().ScanAsync([rule], null, CancellationToken.None);
        Assert.Equal(existing is null ? SkipReason.LocationNotFound : SkipReason.None, scans[0].Skip);
    }
}