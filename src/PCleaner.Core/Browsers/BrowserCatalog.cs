namespace PCleaner.Core.Browsers;

/// <summary>Known browsers and where they keep their data on Windows.</summary>
public static class BrowserCatalog
{
    public static IReadOnlyList<BrowserDefinition> GetDefinitions(string localAppData, string roamingAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppData);

        string L(string relative) => Path.Combine(localAppData, relative);
        string R(string relative) => Path.Combine(roamingAppData, relative);

        var list = new List<BrowserDefinition>
        {
            // ----------------------------------------------------------------- Chromium family
            Chromium("chrome", "Google Chrome", ["chrome"], [L(@"Google\Chrome\User Data")], [@"Google\Chrome", @"Clients\StartMenuInternet\Google Chrome"], ["chrome.exe"]),
            Chromium("chrome-beta", "Google Chrome Beta", ["chrome"], [L(@"Google\Chrome Beta\User Data")], [@"Google\Chrome Beta"], []),
            Chromium("chrome-dev", "Google Chrome Dev", ["chrome"], [L(@"Google\Chrome Dev\User Data")], [@"Google\Chrome Dev"], []),
            Chromium("chrome-canary", "Google Chrome Canary", ["chrome"], [L(@"Google\Chrome SxS\User Data")], [@"Google\Chrome SxS"], []),
            Chromium("edge", "Microsoft Edge", ["msedge"], [L(@"Microsoft\Edge\User Data")], [@"Microsoft\Edge", @"Clients\StartMenuInternet\Microsoft Edge"], ["msedge.exe"]),
            Chromium("edge-beta", "Microsoft Edge Beta", ["msedge"], [L(@"Microsoft\Edge Beta\User Data")], [@"Microsoft\Edge Beta"], []),
            Chromium("edge-dev", "Microsoft Edge Dev", ["msedge"], [L(@"Microsoft\Edge Dev\User Data")], [@"Microsoft\Edge Dev"], []),
            Chromium("edge-canary", "Microsoft Edge Canary", ["msedge"], [L(@"Microsoft\Edge SxS\User Data")], [@"Microsoft\Edge SxS"], []),
            Chromium("brave", "Brave", ["brave"], [L(@"BraveSoftware\Brave-Browser\User Data")], [@"BraveSoftware\Brave-Browser", @"Clients\StartMenuInternet\Brave"], ["brave.exe"]),
            Chromium("brave-beta", "Brave Beta", ["brave"], [L(@"BraveSoftware\Brave-Browser-Beta\User Data")], [@"BraveSoftware\Brave-Browser-Beta"], []),
            Chromium("brave-nightly", "Brave Nightly", ["brave"], [L(@"BraveSoftware\Brave-Browser-Nightly\User Data")], [@"BraveSoftware\Brave-Browser-Nightly"], []),
            Chromium("vivaldi", "Vivaldi", ["vivaldi"], [L(@"Vivaldi\User Data")], [@"Vivaldi", @"Clients\StartMenuInternet\Vivaldi"], ["vivaldi.exe"]),
            Chromium("chromium", "Chromium", ["chrome", "chromium"], [L(@"Chromium\User Data")], [@"Chromium"], ["chromium.exe"]),
            Chromium("yandex", "Yandex Browser", ["browser"], [L(@"Yandex\YandexBrowser\User Data")], [@"Yandex\YandexBrowser", @"Clients\StartMenuInternet\YANDEX.EXE"], ["browser.exe"]),
            Chromium("arc", "Arc", ["Arc"], [L(@"Packages\TheBrowserCompany.Arc_ttt1ap7aakyb4\LocalCache\Local\Arc\User Data")], [], []),
            Chromium("comodo-dragon", "Comodo Dragon", ["dragon"], [L(@"Comodo\Dragon\User Data")], [@"Comodo\Dragon"], ["dragon.exe"]),
            Chromium("epic", "Epic Privacy Browser", ["epic"], [L(@"Epic Privacy Browser\User Data")], [@"Epic Privacy Browser"], ["epic.exe"]),
            Chromium("coccoc", "Cốc Cốc", ["browser"], [L(@"CocCoc\Browser\User Data")], [@"CocCoc\Browser"], []),
            Chromium("cent", "Cent Browser", ["chrome"], [L(@"CentBrowser\User Data")], [@"CentBrowser"], []),
            Chromium("whale", "Naver Whale", ["whale"], [L(@"Naver\Naver Whale\User Data")], [@"Naver\Naver Whale"], ["whale.exe"]),
            Chromium("slimjet", "Slimjet", ["slimjet"], [L(@"Slimjet\User Data")], [@"Slimjet"], ["slimjet.exe"]),
            Chromium("supermium", "Supermium", ["chrome"], [L(@"Supermium\User Data")], [@"Supermium"], []),
            Chromium("thorium", "Thorium", ["thorium"], [L(@"Thorium\User Data")], [@"Thorium"], ["thorium.exe"]),
            Chromium("sidekick", "Sidekick", ["sidekick"], [L(@"Sidekick\User Data")], [@"Sidekick"], ["sidekick.exe"]),
            Chromium("maxthon", "Maxthon", ["Maxthon"], [L(@"Maxthon\Application\User Data")], [@"Maxthon"], ["Maxthon.exe"]),

            // Opera keeps the profile in Roaming AppData and the cache in Local AppData; the data root IS the profile.
            Opera("opera", "Opera", "Opera Stable"),
            Opera("opera-gx", "Opera GX", "Opera GX Stable"),
            Opera("opera-air", "Opera Air", "Opera Air Stable"),
            Opera("opera-beta", "Opera Beta", "Opera Next"),
            Opera("opera-dev", "Opera Developer", "Opera Developer"),
            Opera("opera-neon", "Opera Neon", "Opera Neon"),

            // ----------------------------------------------------------------- Gecko family
            Gecko("firefox", "Mozilla Firefox", ["firefox"], R(@"Mozilla\Firefox"), L(@"Mozilla\Firefox\Profiles"), [@"Mozilla\Mozilla Firefox", @"Clients\StartMenuInternet\FIREFOX.EXE"], ["firefox.exe"]),
            Gecko("firefox-store", "Mozilla Firefox (Microsoft Store)", ["firefox"], L(@"Packages\Mozilla.Firefox_n80bbvh6b1yt2\LocalCache\Roaming\Mozilla\Firefox"), L(@"Packages\Mozilla.Firefox_n80bbvh6b1yt2\LocalCache\Local\Mozilla\Firefox\Profiles"), [], []),
            Gecko("waterfox", "Waterfox", ["waterfox"], R("Waterfox"), L(@"Waterfox\Profiles"), [@"Waterfox"], ["waterfox.exe"]),
            Gecko("librewolf", "LibreWolf", ["librewolf"], R("librewolf"), L(@"librewolf\Profiles"), [@"LibreWolf"], ["librewolf.exe"]),
            Gecko("floorp", "Floorp", ["floorp"], R("Floorp"), L(@"Floorp\Profiles"), [@"Floorp"], ["floorp.exe"]),
            Gecko("zen", "Zen Browser", ["zen"], R("zen"), L(@"zen\Profiles"), [@"Zen"], ["zen.exe"]),
            Gecko("palemoon", "Pale Moon", ["palemoon"], R(@"Moonchild Productions\Pale Moon"), L(@"Moonchild Productions\Pale Moon\Profiles"), [@"Moonchild Productions\Pale Moon"], ["palemoon.exe"]),
            Gecko("basilisk", "Basilisk", ["basilisk"], R(@"Moonchild Productions\Basilisk"), L(@"Moonchild Productions\Basilisk\Profiles"), [@"Moonchild Productions\Basilisk"], ["basilisk.exe"]),
            Gecko("seamonkey", "SeaMonkey", ["seamonkey"], R(@"Mozilla\SeaMonkey"), L(@"Mozilla\SeaMonkey\Profiles"), [@"Mozilla\SeaMonkey"], ["seamonkey.exe"]),
        };

        return list;

        BrowserDefinition Opera(string id, string name, string folder) => new()
        {
            Id = id,
            Name = name,
            Family = BrowserFamily.Chromium,
            ProcessNames = ["opera", "launcher"],
            DataRoots = [R(Path.Combine("Opera Software", folder))],
            SeparateCacheRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [R(Path.Combine("Opera Software", folder))] = L(Path.Combine("Opera Software", folder)),
            },
            RegistryMarkers = [@"Opera Software", @"Clients\StartMenuInternet\" + name + "Stable"],
            ExecutableNames = ["opera.exe"],
        };
    }

    private static BrowserDefinition Chromium(string id, string name, string[] processes, string[] roots, string[] registryMarkers, string[] executables) => new()
    {
        Id = id,
        Name = name,
        Family = BrowserFamily.Chromium,
        ProcessNames = processes,
        DataRoots = roots,
        RegistryMarkers = registryMarkers,
        ExecutableNames = executables,
    };

    private static BrowserDefinition Gecko(string id, string name, string[] processes, string root, string localRoot, string[] registryMarkers, string[] executables) => new()
    {
        Id = id,
        Name = name,
        Family = BrowserFamily.Gecko,
        ProcessNames = processes,
        DataRoots = [root],
        LocalProfilesRoot = localRoot,
        RegistryMarkers = registryMarkers,
        ExecutableNames = executables,
    };
}