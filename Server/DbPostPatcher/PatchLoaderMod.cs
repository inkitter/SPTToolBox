using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using DbPostPatcher.Patching;

namespace DbPostPatcher;

/// <summary>
///     Loads patches.enabled.json and applies every listed patches/*.json file to the in-memory
///     database, once per server boot. Runs at OnLoadOrder.Preload so the rest of the server sees
///     the patched data everywhere - see ../../CLAUDE.md-equivalent notes in patches/README.md and
///     NOTES.md for the design rationale.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload)]
public sealed class PatchLoaderMod(ISptLogger<PatchLoaderMod> logger, PatchTableRegistry registry) : IOnLoad
{
    private static readonly JsonSerializerOptions ConfigReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions ConfigWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new PatchException("could not determine the mod's own directory");

        var enabledPatches = LoadAndRefreshConfig(modDir);
        if (enabledPatches.Count == 0)
        {
            logger.Info("[DbPostPatcher] No patches enabled (patches.enabled.json missing or empty).");
            return Task.CompletedTask;
        }

        var applied = 0;
        var failed = 0;

        foreach (var relativePath in enabledPatches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var patchPath = Path.Combine(modDir, "patches", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (ApplyPatchFile(patchPath, relativePath))
            {
                applied++;
            }
            else
            {
                failed++;
            }
        }

        logger.Info($"[DbPostPatcher] Applied {applied} patch file(s), {failed} failed.");
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Reads patches.enabled.json (or starts from an empty one if it doesn't exist yet),
    ///     preserves its 'enabled' list untouched, and rewrites 'available' to match whatever
    ///     *.json files actually exist under patches/ right now - so the deployed config always
    ///     shows every patch that can be turned on, not just the ones already enabled, without
    ///     needing to hand-maintain that list anywhere.
    /// </summary>
    private List<string> LoadAndRefreshConfig(string modDir)
    {
        var configPath = Path.Combine(modDir, "patches.enabled.json");
        var patchesDir = Path.Combine(modDir, "patches");

        var enabled = new List<string>();
        if (File.Exists(configPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<EnabledPatchesConfig>(File.ReadAllText(configPath), ConfigReadOptions);
                enabled = existing?.Enabled ?? [];
            }
            catch (Exception e)
            {
                logger.Error($"[DbPostPatcher] Failed to read patches.enabled.json, treating as empty: {e.Message}");
            }
        }

        var available = Directory.Exists(patchesDir)
            ? Directory
                .EnumerateFiles(patchesDir, "*.json", SearchOption.AllDirectories)
                // a leading underscore marks a file as reference-only (see patches/README.md) - not a real toggle
                .Where(path => !Path.GetFileName(path).StartsWith('_'))
                .Select(path => Path.GetRelativePath(patchesDir, path).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        try
        {
            var refreshed = new EnabledPatchesConfig { Available = available, Enabled = enabled };
            File.WriteAllText(configPath, JsonSerializer.Serialize(refreshed, ConfigWriteOptions));
        }
        catch (Exception e)
        {
            // Not fatal - fall through and apply whatever we read, just without refreshing the file on disk.
            logger.Error($"[DbPostPatcher] Failed to refresh patches.enabled.json's 'available' list: {e.Message}");
        }

        return enabled;
    }

    /// <summary>
    ///     Applies every op in one patch file. A failure on any single op (bad path, missing
    ///     field, unknown id) is logged and that one op is skipped - it never crashes the server
    ///     and never blocks the rest of that file's other ops or other patch files from applying.
    /// </summary>
    private bool ApplyPatchFile(string patchPath, string relativePath)
    {
        PatchDocument doc;
        try
        {
            doc = JsonSerializer.Deserialize<PatchDocument>(File.ReadAllText(patchPath), ConfigReadOptions)
                ?? throw new PatchException("file deserialized to null");
        }
        catch (Exception e)
        {
            logger.Error($"[DbPostPatcher] {relativePath}: failed to load patch file: {e.Message}");
            return false;
        }

        PatchTarget target;
        try
        {
            target = registry.Resolve(doc.Table, doc.Id);
        }
        catch (Exception e)
        {
            logger.Error($"[DbPostPatcher] {relativePath}: {e.Message}");
            return false;
        }

        var node = target.Node;
        var opFailures = 0;
        foreach (var op in doc.Ops)
        {
            try
            {
                JsonPatchApplier.Apply(node, op);
            }
            catch (Exception e)
            {
                opFailures++;
                logger.Error($"[DbPostPatcher] {relativePath}: op '{op.Op}' at '{op.Path}' failed: {e.Message}");
            }
        }

        if (opFailures == doc.Ops.Count && doc.Ops.Count > 0)
        {
            // Every op in this file failed - don't write back a completely unpatched object as if it succeeded.
            logger.Error($"[DbPostPatcher] {relativePath}: all {opFailures} op(s) failed, skipping write-back.");
            return false;
        }

        try
        {
            target.WriteBack(node);
        }
        catch (Exception e)
        {
            logger.Error($"[DbPostPatcher] {relativePath}: failed to write patched value back: {e.Message}");
            return false;
        }

        if (opFailures > 0)
        {
            logger.Info($"[DbPostPatcher] {relativePath}: applied with {opFailures} op(s) skipped (see errors above).");
        }

        return true;
    }
}
