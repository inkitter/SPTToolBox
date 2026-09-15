using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbPostPatcher.Routing;

/// <summary>
///     The set of items the F9 "Give Item" panel offers, read from give-item-catalog.json next to
///     the deployed mod DLL - same ids as patches/profileTemplates/unheard-usec-hideout-containers.json's
///     static injection, just delivered live via mail instead of baked into the profile template at
///     character creation. Editable by the user without a rebuild; re-read once per server boot
///     (GiveItemRouter's routes are built once at DI construction time, same as patches applying once
///     at boot), so a catalog edit needs a server restart to take effect.
/// </summary>
public static class GiveItemCatalog
{
    public sealed record Entry(
        [property: JsonPropertyName("itemTemplateId")] string ItemTemplateId,
        [property: JsonPropertyName("label")] string Label);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static readonly List<Entry> Items = Load();

    private static List<Entry> Load()
    {
        var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var catalogPath = modDir == null ? null : Path.Combine(modDir, "give-item-catalog.json");

        if (catalogPath == null || !File.Exists(catalogPath))
        {
            return [];
        }

        try
        {
            var doc = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(catalogPath), ReadOptions);
            return doc?.Items ?? [];
        }
        catch
        {
            // GiveItemRouter's routes are built once from this list at DI construction, before any
            // ISptLogger is available here - a malformed catalog file just yields an empty catalog
            // (the F9 panel shows "catalog is empty") rather than crashing server boot.
            return [];
        }
    }

    private sealed class CatalogFile
    {
        public List<Entry> Items { get; set; } = [];
    }
}
