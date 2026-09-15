using System;
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

        // null = not checked yet, true/false = last ping result. Re-checked once per panel open
        // (see QuestDebugPanel), not on every frame - a ping is still a real network round trip.
        public static bool? BackendAvailable { get; private set; }
        public static string LastStatus { get; private set; } = "";

        // Fetched from /dbpostpatcher/catalog rather than duplicated client-side, so this list
        // (Server/DbPostPatcher/Routing/GiveItemCatalog.cs) has exactly one place it's maintained.
        public static CatalogEntry[] Catalog { get; private set; } = Array.Empty<CatalogEntry>();

        public sealed class CatalogEntry
        {
            [JsonPropertyName("itemTemplateId")]
            public string ItemTemplateId { get; set; }

            [JsonPropertyName("label")]
            public string Label { get; set; }
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
                LastStatus = "Couldn't find -token/-config BackendUrl in the game's launch command line";
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
                    BackendAvailable = false;
                    return;
                }

                using var response = await Http.GetAsync($"{session.BaseUrl}/singleplayer/dbpostpatcher/ping").ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    BackendAvailable = false;
                    LastStatus = $"Backend responded {(int)response.StatusCode} to /singleplayer/dbpostpatcher/ping - DbPostPatcher likely not deployed on this backend";
                    return;
                }

                var body = StripBom(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var ping = JsonSerializer.Deserialize<PingBody>(body, JsonOptions);
                BackendAvailable = ping?.Data?.Mod == "DbPostPatcher";
                if (BackendAvailable != true)
                {
                    LastStatus = "Backend responded but not with DbPostPatcher's ping shape - probably a different/older mod version";
                    return;
                }

                LastStatus = $"DbPostPatcher v{ping.Data.Version} detected on backend";

                using var catalogResponse = await Http.GetAsync($"{session.BaseUrl}/singleplayer/dbpostpatcher/catalog").ConfigureAwait(false);
                if (catalogResponse.IsSuccessStatusCode)
                {
                    var catalogBody = StripBom(await catalogResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var catalogEnvelope = JsonSerializer.Deserialize<CatalogBody>(catalogBody, JsonOptions);
                    Catalog = catalogEnvelope?.Data ?? Array.Empty<CatalogEntry>();
                }
            }
            catch (Exception e)
            {
                BackendAvailable = false;
                LastStatus = $"Ping failed: {DescribeException(e)} - is the DbPostPatcher mod deployed on this backend?";
            }
        }

        // The server writes its JSON responses with a leading UTF-8 BOM (0xEF 0xBB 0xBF) - the
        // string-based JsonSerializer.Deserialize overload doesn't skip a leading BOM CHARACTER
        // (U+FEFF) the way the stream/UTF8JsonReader-based overloads do, so it fails to parse with
        // "'0xEF' is an invalid start of a value" (that's literally the BOM's first raw byte
        // showing through, not a real JSON token). Confirmed via the server's own request log
        // (the request/response round trip completes fine - this is purely a parsing-side issue).
        private static string StripBom(string s)
        {
            return s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
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
                var body = StripBom(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

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
