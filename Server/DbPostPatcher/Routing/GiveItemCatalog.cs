namespace DbPostPatcher.Routing;

/// <summary>
///     The fixed set of items the F9 "Give Item" panel offers - same ids as
///     patches/profileTemplates/unheard-usec-hideout-containers.json's static injection, just
///     delivered live via mail instead of baked into the profile template at character creation.
/// </summary>
public static class GiveItemCatalog
{
    public sealed record Entry(string ItemTemplateId, string Label);

    public static readonly List<Entry> Items =
    [
        new("5c0a840b86f7742ffa4f2482", "THICC item case"),
        new("5b6d9ce188a4501afc1b2b25", "THICC weapon case"),
        new("5d235bb686f77443f4331278", "SICC pouch"),
        new("619cbf7d23893217ec30b689", "Injector case"),
        new("5c093db286f7740a1b2617e3", "Mr. Holodilnick's thermal bag"),
        new("5c093e3486f77430cb02e593", "Dogtag case"),
        new("5aafbcd986f7745e590fff23", "Medicine case"),
        new("62a09d3bcf4a99369e262447", "Gingy keytool"),
        new("591094e086f7747caa7bb2ef", "Armor repair kit"),
        new("5910968f86f77425cf569c32", "Weapon repair kit"),
    ];
}
