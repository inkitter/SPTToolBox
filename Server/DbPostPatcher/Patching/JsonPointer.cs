namespace DbPostPatcher.Patching;

/// <summary>
///     RFC 6901 JSON Pointer parsing ("/a/b/0" -> ["a", "b", "0"], with ~1 -> / and ~0 -> ~ unescaped).
/// </summary>
public static class JsonPointer
{
    public static List<string> Parse(string pointer)
    {
        if (pointer.Length == 0)
        {
            return [];
        }

        if (pointer[0] != '/')
        {
            throw new PatchException($"JSON Pointer must start with '/': '{pointer}'");
        }

        var segments = pointer.Split('/');
        var result = new List<string>(segments.Length - 1);

        // segments[0] is always "" because the pointer starts with '/'
        for (var i = 1; i < segments.Length; i++)
        {
            result.Add(segments[i].Replace("~1", "/").Replace("~0", "~"));
        }

        return result;
    }
}
