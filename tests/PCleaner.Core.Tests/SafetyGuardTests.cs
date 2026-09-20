using PCleaner.Core.Engine;
using PCleaner.Core.Model;

namespace PCleaner.Core.Tests;

public sealed class SafetyGuardTests
{
    [Fact]
    public void Drive_root_is_rejected()
    {
        var reason = SafetyGuard.ValidateTargetRoot(new PathTarget { Path = @"C:\" });
        Assert.NotNull(reason);
    }

    [Fact]
    public void Windows_directory_is_rejected()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = windows }));
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = Path.Combine(windows, "System32") }));
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = Path.Combine(windows, "WinSxS") }));
    }

    [Fact]
    public void User_profile_and_known_folders_are_rejected()
    {
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }));
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) }));
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }));
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") }));
    }

    [Fact]
    public void Parent_of_protected_folder_is_rejected()
    {
        // C:\Users contains every profile.
        var users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users");
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = users }));
    }

    [Fact]
    public void Temp_folder_is_accepted()
    {
        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        Assert.Null(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = temp }));
        Assert.Null(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }));
    }

    [Fact]
    public void Network_paths_are_rejected()
    {
        Assert.NotNull(SafetyGuard.ValidateTargetRoot(new PathTarget { Path = @"\\server\share\temp" }));
    }

    [Fact]
    public void Files_outside_root_are_blocked()
    {
        Assert.NotNull(SafetyGuard.CheckDeletable(@"C:\Other\file.txt", @"C:\Root", isDirectory: false));
        Assert.NotNull(SafetyGuard.CheckDeletable(@"C:\Root\..\Other\file.txt", @"C:\Root", isDirectory: false));
        Assert.Null(SafetyGuard.CheckDeletable(@"C:\Root\sub\file.txt", @"C:\Root", isDirectory: false));
    }

    [Fact]
    public void Root_directory_is_never_deleted()
    {
        Assert.NotNull(SafetyGuard.CheckDeletable(@"C:\Root", @"C:\Root", isDirectory: true));
        Assert.Null(SafetyGuard.CheckDeletable(@"C:\Root\sub", @"C:\Root", isDirectory: true));
    }

    [Theory]
    [InlineData("Bookmarks")]
    [InlineData("Login Data")]
    [InlineData("Secure Preferences")]
    [InlineData("places.sqlite")]
    [InlineData("key4.db")]
    [InlineData("logins.json")]
    [InlineData("storage-sync-v2.sqlite")]
    [InlineData("pagefile.sys")]
    public void Protected_user_data_files_are_blocked_everywhere(string fileName)
    {
        Assert.NotNull(SafetyGuard.CheckDeletable(Path.Combine(@"C:\Root\Default", fileName), @"C:\Root", isDirectory: false));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Extensions\abc\1.0\manifest.json")]
    [InlineData(@"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Local Extension Settings\abc\000003.log")]
    [InlineData(@"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Storage\ext\abc\def\file")]
    [InlineData(@"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Extension State\000001.ldb")]
    [InlineData(@"C:\Users\x\AppData\Roaming\Mozilla\Firefox\Profiles\a.default\extensions\uBlock0@raymondhill.net.xpi")]
    [InlineData(@"C:\Users\x\AppData\Roaming\Mozilla\Firefox\Profiles\a.default\browser-extension-data\abc\storage.js")]
    public void Extension_data_below_browser_roots_is_blocked(string path)
    {
        var root = path.Contains("User Data", StringComparison.Ordinal)
            ? @"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default"
            : @"C:\Users\x\AppData\Roaming\Mozilla\Firefox\Profiles\a.default";
        Assert.NotNull(SafetyGuard.CheckDeletable(path, root, isDirectory: false));
    }

    [Fact]
    public void Cache_files_below_browser_roots_are_allowed()
    {
        Assert.Null(SafetyGuard.CheckDeletable(
            @"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_000001",
            @"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Cache",
            isDirectory: false));
    }

    [Fact]
    public void Protected_browser_directory_names_are_recognised()
    {
        Assert.True(SafetyGuard.IsProtectedBrowserDirectory(@"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Extensions"));
        Assert.False(SafetyGuard.IsProtectedBrowserDirectory(@"C:\Users\x\AppData\Local\Google\Chrome\User Data\Default\Cache"));
        // Outside browser roots the names are not special.
        Assert.False(SafetyGuard.IsProtectedBrowserDirectory(@"C:\Temp\Extensions"));
    }
}