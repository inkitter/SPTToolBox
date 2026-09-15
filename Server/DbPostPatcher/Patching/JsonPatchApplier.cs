using System.Text.Json.Nodes;

namespace DbPostPatcher.Patching;

/// <summary>
///     Applies a subset of RFC 6902 JSON Patch ("add" / "replace" / "remove") to a JsonNode tree in place.
///     "move" / "copy" / "test" are intentionally not implemented - see patches/README.md.
/// </summary>
public static class JsonPatchApplier
{
    public static void Apply(JsonNode root, PatchOp op)
    {
        var pointer = JsonPointer.Parse(op.Path);
        if (pointer.Count == 0)
        {
            throw new PatchException($"op '{op.Op}': empty path is not supported (path must target a field, not the whole object)");
        }

        var parent = Navigate(root, pointer.SkipLast(1).ToList(), op);
        var last = pointer[^1];

        switch (op.Op)
        {
            case "add":
                Add(parent, last, op);
                break;
            case "replace":
                Replace(parent, last, op);
                break;
            case "remove":
                Remove(parent, last, op);
                break;
            default:
                throw new PatchException(
                    $"op '{op.Op}' at path '{op.Path}': unsupported - only add/replace/remove are implemented"
                );
        }
    }

    /// <summary>
    ///     Walks all but the last pointer segment. Every intermediate segment must already exist -
    ///     this engine never auto-creates intermediate containers, so a typo'd path fails loudly
    ///     instead of silently patching nothing.
    /// </summary>
    private static JsonNode Navigate(JsonNode root, List<string> segments, PatchOp op)
    {
        var current = root;
        foreach (var segment in segments)
        {
            current = current switch
            {
                JsonObject obj => obj.TryGetPropertyValue(segment, out var child) && child is not null
                    ? child
                    : throw new PatchException($"op '{op.Op}' at path '{op.Path}': no field '{segment}' on the object at that point"),
                JsonArray arr => TryGetArrayIndex(arr, segment, out var child)
                    ? child!
                    : throw new PatchException($"op '{op.Op}' at path '{op.Path}': index '{segment}' out of range (array has {arr.Count} elements)"),
                _ => throw new PatchException($"op '{op.Op}' at path '{op.Path}': can't descend into a scalar value at '{segment}'"),
            };
        }

        return current;
    }

    private static void Add(JsonNode parent, string key, PatchOp op)
    {
        switch (parent)
        {
            case JsonObject obj:
                // RFC 6902: 'add' on an object member creates it if missing, overwrites if present.
                obj[key] = op.Value?.DeepClone();
                break;
            case JsonArray arr:
                if (key == "-")
                {
                    arr.Add(op.Value?.DeepClone());
                }
                else
                {
                    var index = ParseArrayIndex(arr, key, op, allowEnd: true);
                    arr.Insert(index, op.Value?.DeepClone());
                }
                break;
            default:
                throw new PatchException($"op 'add' at path '{op.Path}': parent is not an object or array");
        }
    }

    private static void Replace(JsonNode parent, string key, PatchOp op)
    {
        switch (parent)
        {
            case JsonObject obj:
                if (!obj.ContainsKey(key))
                {
                    throw new PatchException($"op 'replace' at path '{op.Path}': field '{key}' does not exist - use 'add' if it's meant to be created");
                }
                obj[key] = op.Value?.DeepClone();
                break;
            case JsonArray arr:
                var index = ParseArrayIndex(arr, key, op, allowEnd: false);
                arr[index] = op.Value?.DeepClone();
                break;
            default:
                throw new PatchException($"op 'replace' at path '{op.Path}': parent is not an object or array");
        }
    }

    private static void Remove(JsonNode parent, string key, PatchOp op)
    {
        switch (parent)
        {
            case JsonObject obj:
                if (!obj.Remove(key))
                {
                    throw new PatchException($"op 'remove' at path '{op.Path}': field '{key}' does not exist");
                }
                break;
            case JsonArray arr:
                var index = ParseArrayIndex(arr, key, op, allowEnd: false);
                arr.RemoveAt(index);
                break;
            default:
                throw new PatchException($"op 'remove' at path '{op.Path}': parent is not an object or array");
        }
    }

    private static bool TryGetArrayIndex(JsonArray arr, string segment, out JsonNode? value)
    {
        value = null;
        if (!int.TryParse(segment, out var index) || index < 0 || index >= arr.Count)
        {
            return false;
        }

        value = arr[index];
        return true;
    }

    private static int ParseArrayIndex(JsonArray arr, string segment, PatchOp op, bool allowEnd)
    {
        if (allowEnd && segment == "-")
        {
            return arr.Count;
        }

        if (!int.TryParse(segment, out var index) || index < 0 || index > arr.Count || (!allowEnd && index >= arr.Count))
        {
            throw new PatchException($"op '{op.Op}' at path '{op.Path}': index '{segment}' out of range (array has {arr.Count} elements)");
        }

        return index;
    }
}
