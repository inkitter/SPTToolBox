using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SPTMap.Utils
{
    // Talks to DbPostPatcher's server-side routes (Server/DbPostPatcher/Routing/GiveItemRouter.cs)
    // over plain HTTP - deliberately not touching any client-side inventory/item-placement IL2CPP
    // internals (that path was investigated and abandoned, see Server/DbPostPatcher/NOTES.md): the
    // server mails the item, and the game's own already-existing live mail-notification push does
    // the rest, so this class only ever needs a session id and a base URL, both plain strings.
    //
    // Session id and backend URL come straight off the game's own launch command line, which the
    // SPT launcher always passes as -token=<sessionId> and -config={"BackendUrl":"...",...} (seen
    // logged verbatim in BepInEx/LogOutput.log at startup: "key:token value:..." / "key:config
    // value:{'BackendUrl':'https://127.0.0.1:6969',...}"). Plain Environment.GetCommandLineArgs()
    // - no IL2CPP object access needed for this at all, unlike an earlier attempt that tried
    // (wrongly - misread a decompiled coroutine's local variable as a session field) to pull a
    // base URL off ClientBackendSession.
    public static class DbPostPatcherClient
    {
        // BackendUrl is https://127.0.0.1:... with a self-signed cert (SPT's own C# server) - the
        // game client itself already trusts this same local connection implicitly, so bypass cert
        // validation here too rather than trying to install/trust the cert for this one HttpClient.
        //
        // Two callbacks, not one: HttpClientHandler.ServerCertificateCustomValidationCallback is
        // the modern (SocketsHttpHandler) opt-out, but this plugin runs inside Unity's Mono
        // runtime (not full .NET), whose HttpClient can fall back to a legacy TLS path
        // (Mono.Btls) that only honors the older, process-wide ServicePointManager callback - a
        // cert failure on that path surfaces as an unhelpful bare native error code (e.g. an
        // exception whose Message is just "0xEF") rather than a normal .NET exception message.
        // Setting both covers whichever path is actually taken.
        static DbPostPatcherClient()
        {
            System.Net.ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
        }

        private static readonly HttpClient Http = new HttpClient(
            new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }
        )
        { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        // BackendAvailable/LastStatus/Catalog used to be three independent static properties, each
        // written separately by CheckBackendCoreAsync with a network await in between (ping, then a
        // second round trip for the catalog). QuestDebugPanel's OnGUI runs multiple passes per frame
        // (Layout, then Repaint) and reads these on the Unity main thread while the async continuation
        // could land on a different thread between the two writes - so BackendAvailable could already
        // read true (catalog section starts rendering) while Catalog was still the old/empty array on
        // one pass and populated on the next, changing how many GUILayout.BeginHorizontal/EndHorizontal
        // calls DrawGiveItemSection made between passes of the *same* frame. Unity flags that mismatch
        // as "GUILayout: Mismatched LayoutGroup.repaint" and aborts OnGUI. Bundling the three into one
        // record swapped via a single reference assignment (atomic in .NET) means every read within one
        // OnGUI call - and across the Layout/Repaint passes of one frame - sees one consistent snapshot.
        private static State _state = State.Empty;

        private sealed record State(bool? BackendAvailable, string LastStatus, CatalogEntry[] Catalog)
        {
            public static readonly State Empty = new(null, "", Array.Empty<CatalogEntry>());
        }

        // null = not checked yet, true/false = last ping result. Re-checked once per panel open
        // (see QuestDebugPanel), not on every frame - a ping is still a real network round trip.
        public static bool? BackendAvailable => _state.BackendAvailable;
        public static string LastStatus => _state.LastStatus;

        // Fetched from /dbpostpatcher/catalog rather than duplicated client-side, so this list
        // (Server/DbPostPatcher/Routing/GiveItemCatalog.cs) has exactly one place it's maintained.
        public static CatalogEntry[] Catalog => _state.Catalog;

        public sealed class CatalogEntry
        {
            [JsonPropertyName("itemTemplateId")]
            public string ItemTemplateId { get; set; }

            [JsonPropertyName("label")]
            public string Label { get; set; }
        }

        // Mirrors Server/DbPostPatcher/Routing/CharacterDebugRouter.cs's VitalState/SkillState/
        // CharacterState records - not shared code between the two projects (client/server don't
        // reference each other), just kept in sync by hand like CatalogEntry already is.
        private static CharacterState _characterState;

        public static CharacterState LatestCharacterState => _characterState;

        public sealed class VitalState
        {
            [JsonPropertyName("current")]
            public double Current { get; set; }

            [JsonPropertyName("maximum")]
            public double Maximum { get; set; }
        }

        public sealed class SkillState
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }

            [JsonPropertyName("progress")]
            public double Progress { get; set; }
        }

        public sealed class CharacterState
        {
            [JsonPropertyName("bodyParts")]
            public Dictionary<string, VitalState> BodyParts { get; set; }

            [JsonPropertyName("hydration")]
            public VitalState Hydration { get; set; }

            [JsonPropertyName("energy")]
            public VitalState Energy { get; set; }

            [JsonPropertyName("temperature")]
            public VitalState Temperature { get; set; }

            [JsonPropertyName("skills")]
            public List<SkillState> Skills { get; set; }
        }

        private readonly struct SessionInfo
        {
            public readonly string BaseUrl;
            public readonly string SessionId;

            public SessionInfo(string baseUrl, string sessionId)
            {
                BaseUrl = baseUrl;
                SessionId = sessionId;
            }
        }

        // Tolerant of both strict JSON (double-quoted) and the single-quoted form seen in
        // LogOutput.log - Unity's own arg logging reformats it with single quotes, so the raw
        // command line's actual quoting isn't confirmed; a regex sidesteps needing to know which.
        private static readonly Regex BackendUrlPattern =
            new(@"BackendUrl[""']?\s*:\s*[""']([^""']+)[""']", RegexOptions.Compiled);

        private static bool TryGetSession(out SessionInfo session)
        {
            session = default;

            string token = null;
            string backendUrl = null;

            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("-token=", StringComparison.Ordinal))
                {
                    token = arg.Substring("-token=".Length);
                }
                else if (arg.StartsWith("-config=", StringComparison.Ordinal))
                {
                    var match = BackendUrlPattern.Match(arg);
                    if (match.Success)
                    {
                        backendUrl = match.Groups[1].Value;
                    }
                }
            }

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(backendUrl))
            {
                _state = new State(false, "Couldn't find -token/-config BackendUrl in the game's launch command line", Array.Empty<CatalogEntry>());
                return false;
            }

            session = new SessionInfo(backendUrl.TrimEnd('/'), token);
            return true;
        }

        // Fire-and-forget from the F9 panel - updates BackendAvailable/LastStatus for the next
        // OnGUI frame to read. Call once per panel open (see QuestDebugPanel), not every frame.
        public static void CheckBackendAsync()
        {
            _ = CheckBackendCoreAsync();
        }

        private static async Task CheckBackendCoreAsync()
        {
            try
            {
                if (!TryGetSession(out var session))
                {
                    _state = new State(false, "", Array.Empty<CatalogEntry>());
                    return;
                }

                using var response = await Http.GetAsync($"{session.BaseUrl}/singleplayer/dbpostpatcher/ping").ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _state = new State(
                        false,
                        $"Backend responded {(int)response.StatusCode} to /singleplayer/dbpostpatcher/ping - DbPostPatcher likely not deployed on this backend",
                        Array.Empty<CatalogEntry>());
                    return;
                }

                var body = await ReadBodyAsync(response.Content).ConfigureAwait(false);
                var ping = JsonSerializer.Deserialize<PingBody>(body, JsonOptions);
                var available = ping?.Data?.Mod == "DbPostPatcher";
                if (!available)
                {
                    _state = new State(
                        false,
                        "Backend responded but not with DbPostPatcher's ping shape - probably a different/older mod version",
                        Array.Empty<CatalogEntry>());
                    return;
                }

                var lastStatus = $"DbPostPatcher v{ping.Data.Version} detected on backend";
                var catalog = Array.Empty<CatalogEntry>();

                using var catalogResponse = await Http.GetAsync($"{session.BaseUrl}/singleplayer/dbpostpatcher/catalog").ConfigureAwait(false);
                if (catalogResponse.IsSuccessStatusCode)
                {
                    var catalogBody = await ReadBodyAsync(catalogResponse.Content).ConfigureAwait(false);
                    var catalogEnvelope = JsonSerializer.Deserialize<CatalogBody>(catalogBody, JsonOptions);
                    catalog = catalogEnvelope?.Data ?? Array.Empty<CatalogEntry>();
                }

                // Single reference-assignment publishes availability + catalog together - see the
                // State comment above for why the two used to disagree mid-frame.
                _state = new State(true, lastStatus, catalog);
            }
            catch (Exception e)
            {
                _state = new State(
                    false,
                    $"Ping failed: {DescribeException(e)} - is the DbPostPatcher mod deployed on this backend?",
                    Array.Empty<CatalogEntry>());
            }
        }

        // Every SPT server response body is zlib-deflate compressed, always - not something
        // "/singleplayer/" or any other path exempts (that prefix only skips the extra byte-
        // shuffle permutation layer on top, see SptHttpListener.WriteFrameAsync/ShouldShuffleResponse
        // in server-csharp: WriteFrameAsync always deflates into a frame first, then conditionally
        // shuffles that frame). A raw read of the bytes looks like noise ("'0xEF'/'0x00' is an
        // invalid start of a value" when treated as text) because it *is* compressed binary, not
        // malformed JSON - confirmed by manually zlib-inflating a captured response with Python
        // (wbits=15, standard zlib header 0x78 0xDA) and getting back the exact expected JSON.
        private static async Task<string> ReadBodyAsync(HttpContent content)
        {
            using var compressed = new MemoryStream(await content.ReadAsByteArrayAsync().ConfigureAwait(false));
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.UTF8);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);

            // Belt-and-suspenders: strip a leading UTF-8 BOM character if the decompressed text
            // still has one - JsonSerializer's string overload doesn't skip it the way the
            // stream-based overloads do.
            return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
        }

        // e.Message alone can be an unhelpful bare code (see the ServicePointManager comment
        // above) - walk the full InnerException chain and include each exception's type name so a
        // report of this string is actually actionable instead of just "0xEF".
        private static string DescribeException(Exception e)
        {
            var sb = new StringBuilder();
            for (var current = e; current != null; current = current.InnerException)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" <- ");
                }
                sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
            }
            return sb.ToString();
        }

        // Fire-and-forget from the F9 panel button. onResult is invoked (off the main thread) with
        // a human-readable outcome string - QuestDebugPanel stores it to show next frame.
        public static void GiveItemAsync(string itemTemplateId, string label, Action<string> onResult)
        {
            _ = GiveItemCoreAsync(itemTemplateId, label, onResult);
        }

        private static async Task GiveItemCoreAsync(string itemTemplateId, string label, Action<string> onResult)
        {
            try
            {
                if (!TryGetSession(out var session))
                {
                    onResult($"Give '{label}' failed: {LastStatus}");
                    return;
                }

                // GET with no body, item id baked into the path - see GiveItemRouter.cs's header
                // comment for why (a POST body here would get mangled by the server's
                // request-shuffling, which "/singleplayer/" only exempts responses from).
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{session.BaseUrl}/singleplayer/dbpostpatcher/give-item/{itemTemplateId}");
                request.Headers.Add("Cookie", $"PHPSESSID={session.SessionId}");

                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                var body = await ReadBodyAsync(response.Content).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    onResult($"Give '{label}' failed: HTTP {(int)response.StatusCode} - {body}");
                    return;
                }

                var parsed = JsonSerializer.Deserialize<ErrorEnvelope>(body, JsonOptions);
                if (parsed?.Err is not (null or 0))
                {
                    onResult($"Give '{label}' failed: {parsed.ErrMsg ?? "server rejected the request"}");
                    return;
                }

                onResult($"'{label}' mailed - check your in-game mail.");
            }
            catch (Exception e)
            {
                onResult($"Give '{label}' failed: {DescribeException(e)}");
            }
        }

        // Fire-and-forget - fetches the live server-side Health/Skills snapshot to populate the F9
        // panel's Character tab. Call once per panel open/tab switch, not every frame - same
        // reasoning as CheckBackendAsync. onResult (off the main thread, like GiveItemAsync's) gets
        // either the fetched CharacterState or null on failure, plus a human-readable status string.
        public static void FetchCharacterStateAsync(Action<CharacterState, string> onResult)
        {
            _ = RequestCharacterStateAsync("state", onResult);
        }

        public static void SetBodyPartAsync(string part, double maxValue, Action<CharacterState, string> onResult)
        {
            _ = RequestCharacterStateAsync($"set-bodypart/{part}/{maxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}", onResult);
        }

        public static void SetVitalAsync(string vitalName, double maxValue, Action<CharacterState, string> onResult)
        {
            _ = RequestCharacterStateAsync($"set-vital/{vitalName}/{maxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}", onResult);
        }

        public static void SetSkillAsync(string skillId, double progress, Action<CharacterState, string> onResult)
        {
            _ = RequestCharacterStateAsync($"set-skill/{skillId}/{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}", onResult);
        }

        // Every character route (state/set-bodypart/set-vital/set-skill) is a GET with no body -
        // same reasoning as give-item - and returns the same CharacterStateBody envelope shape
        // (the mutating ones return the post-mutation state so the panel can refresh in one round
        // trip instead of a set + a separate re-fetch).
        private static async Task RequestCharacterStateAsync(string routeSuffix, Action<CharacterState, string> onResult)
        {
            try
            {
                if (!TryGetSession(out var session))
                {
                    onResult(null, $"Request failed: {LastStatus}");
                    return;
                }

                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{session.BaseUrl}/singleplayer/dbpostpatcher/character/{routeSuffix}");
                request.Headers.Add("Cookie", $"PHPSESSID={session.SessionId}");

                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                var body = await ReadBodyAsync(response.Content).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    onResult(null, $"Request failed: HTTP {(int)response.StatusCode} - {body}");
                    return;
                }

                var parsed = JsonSerializer.Deserialize<CharacterStateBody>(body, JsonOptions);
                if (parsed?.Err is not (null or 0))
                {
                    onResult(null, $"Request failed: {parsed.ErrMsg ?? "server rejected the request"}");
                    return;
                }

                _characterState = parsed.Data;
                onResult(parsed.Data, "OK");
            }
            catch (Exception e)
            {
                onResult(null, $"Request failed: {DescribeException(e)}");
            }
        }

        private sealed class CharacterStateBody
        {
            [JsonPropertyName("err")]
            public int? Err { get; set; }

            [JsonPropertyName("errmsg")]
            public string ErrMsg { get; set; }

            [JsonPropertyName("data")]
            public CharacterState Data { get; set; }
        }

        private sealed class ErrorEnvelope
        {
            [JsonPropertyName("err")]
            public int? Err { get; set; }

            [JsonPropertyName("errmsg")]
            public string ErrMsg { get; set; }
        }

        private sealed class PingBody
        {
            [JsonPropertyName("data")]
            public PingData Data { get; set; }
        }

        private sealed class PingData
        {
            [JsonPropertyName("mod")]
            public string Mod { get; set; }

            [JsonPropertyName("version")]
            public string Version { get; set; }
        }

        private sealed class CatalogBody
        {
            [JsonPropertyName("data")]
            public CatalogEntry[] Data { get; set; }
        }
    }
}
