using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace DbPostPatcher;

public sealed class ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "dev.inkitter.dbpostpatcher";
    public string Name { get; init; } = "DbPostPatcher";
    public string Author { get; init; } = "inkitter";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("0.1.0");
    public Range SptVersion { get; init; } = new("~5.0.0");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/inkitter/SPTToolBox";
    public string License { get; init; } = "MIT";
}
