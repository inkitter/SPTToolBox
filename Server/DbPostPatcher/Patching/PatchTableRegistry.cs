using System.Reflection;
using System.Text.Json.Nodes;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace DbPostPatcher.Patching;

/// <summary>
///     One patch target: the object's current state as a JsonNode, and how to write a patched
///     version back to wherever it actually lives (a dictionary entry, or a DI singleton's own
///     field).
/// </summary>
public sealed class PatchTarget
{
    public required JsonNode Node { get; init; }
    public required Action<JsonNode> WriteBack { get; init; }
}

/// <summary>
///     Maps a patch file's "table" (+ "id") to a live object reachable from the DI-injected
///     database/config singletons. Add a case here to support a new table.
/// </summary>
[Injectable]
public sealed class PatchTableRegistry(TemplateTable templateTable, GlobalTable globalTable, RepairConfig repairConfig, JsonUtil jsonUtil)
{
    public PatchTarget Resolve(string table, string? id)
    {
        return table switch
        {
            "items" => ResolveItem(RequireId(table, id)),
            "profileTemplates" => ResolveProfileTemplate(RequireId(table, id)),
            "globals" => ResolveSingleton(globalTable),
            "config:repair" => ResolveSingleton(repairConfig),
            _ => throw new PatchException(
                $"unknown table '{table}' (known: items, profileTemplates, globals, config:repair - add a case in {nameof(PatchTableRegistry)} to support more)"
            ),
        };
    }

    private static string RequireId(string table, string? id)
    {
        return id ?? throw new PatchException($"table '{table}' requires an 'id'");
    }

    private PatchTarget ResolveItem(string id)
    {
        var itemId = new MongoId(id);
        if (!templateTable.Items.TryGetValue(itemId, out var item))
        {
            throw new PatchException($"items: id '{id}' not found in TemplateTable.Items");
        }

        return new PatchTarget
        {
            Node = SerializeToNode(item),
            WriteBack = patched => templateTable.Items[itemId] = DeserializeNode<TemplateItem>(patched),
        };
    }

    /// <summary>
    ///     id is "&lt;AccountType&gt;.&lt;usec|bear&gt;", e.g. "Unheard.usec" -
    ///     TemplateTable.Profiles["Unheard"].Usec.
    /// </summary>
    private PatchTarget ResolveProfileTemplate(string id)
    {
        var parts = id.Split('.', 2);
        if (parts.Length != 2)
        {
            throw new PatchException($"profileTemplates: id '{id}' must be '<AccountType>.<usec|bear>'");
        }

        var (accountType, sideRaw) = (parts[0], parts[1]);
        var side = sideRaw.ToLowerInvariant();

        if (!templateTable.Profiles.TryGetValue(accountType, out var profileSides))
        {
            throw new PatchException($"profileTemplates: account type '{accountType}' not found in TemplateTable.Profiles");
        }

        var current = side switch
        {
            "usec" => profileSides.Usec,
            "bear" => profileSides.Bear,
            _ => throw new PatchException($"profileTemplates: id '{id}' - side must be 'usec' or 'bear'"),
        };

        if (current is null)
        {
            throw new PatchException($"profileTemplates: '{id}' has no template data to patch");
        }

        return new PatchTarget
        {
            Node = SerializeToNode(current),
            WriteBack = patched =>
            {
                var updated = DeserializeNode<TemplateSide>(patched);
                if (side == "usec")
                {
                    profileSides.Usec = updated;
                }
                else
                {
                    profileSides.Bear = updated;
                }
            },
        };
    }

    /// <summary>
    ///     For a DI singleton (GlobalTable, RepairConfig, ...): other services already hold a
    ///     direct reference to this exact instance, so we can't swap it for a new one - every
    ///     public property gets copied from the patched copy back onto the live instance via
    ///     reflection instead. This works even for `init`-only record properties: `init` is a
    ///     C#-compiler-enforced restriction on the call site, not a CLR-level one, so
    ///     PropertyInfo.SetValue can still invoke the generated setter after construction.
    /// </summary>
    private PatchTarget ResolveSingleton<T>(T instance) where T : class
    {
        var type = typeof(T);
        return new PatchTarget
        {
            Node = SerializeToNode(instance),
            WriteBack = patched =>
            {
                var updated = DeserializeNode<T>(patched)!;
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.CanRead && prop.SetMethod is not null)
                    {
                        prop.SetValue(instance, prop.GetValue(updated));
                    }
                }
            },
        };
    }

    private JsonNode SerializeToNode<T>(T value)
    {
        var json = jsonUtil.Serialize(value, typeof(T)) ?? throw new PatchException($"failed to serialize {typeof(T).Name} for patching");
        return JsonNode.Parse(json) ?? throw new PatchException($"failed to parse serialized {typeof(T).Name}");
    }

    private T DeserializeNode<T>(JsonNode node)
    {
        var result = jsonUtil.Deserialize(node.ToJsonString(), typeof(T));
        return result is T typed ? typed : throw new PatchException($"failed to deserialize patched {typeof(T).Name}");
    }
}
