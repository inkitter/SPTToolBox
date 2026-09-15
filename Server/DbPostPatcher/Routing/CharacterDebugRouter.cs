using System.Globalization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace DbPostPatcher.Routing;

/// <summary>
///     Backs the F9 client panel's "Character" tab - lets the user edit their own profile's body
///     part max HP, hydration/energy/temperature max, and skill progress, written straight back into
///     the live in-memory PmcData and then persisted to user/profiles/{sessionId}.json via
///     SaveServer.SaveProfileAsync, same as GiveItemRouter is the server side of the F9 "Item" tab.
///
///     All GET, no request body - same reasoning as GiveItemRouter (see its header comment): a
///     request body under "/singleplayer/..." still gets byte-shuffled server-side and isn't worth
///     solving for here. Unlike the item catalog's fixed per-id routes, the values here are
///     open-ended numbers, not a small enumerable set, so this uses a DynamicRouter matched by
///     "starts with" against a fixed path prefix, with the actual field name/value baked into the
///     trailing path segments and parsed out of the raw request path in the handler.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public sealed class CharacterDebugRouter(
    JsonUtil jsonUtil,
    HttpResponseUtil httpResponseUtil,
    ProfileHelper profileHelper,
    SaveServer saveServer
) : DynamicRouter(jsonUtil, BuildRoutes(httpResponseUtil, profileHelper, saveServer))
{
    private const string StatePrefix = "/singleplayer/dbpostpatcher/character/state";
    private const string SetBodyPartPrefix = "/singleplayer/dbpostpatcher/character/set-bodypart/";
    private const string SetVitalPrefix = "/singleplayer/dbpostpatcher/character/set-vital/";
    private const string SetSkillPrefix = "/singleplayer/dbpostpatcher/character/set-skill/";

    private static List<RouteAction> BuildRoutes(HttpResponseUtil httpResponseUtil, ProfileHelper profileHelper, SaveServer saveServer)
    {
        return
        [
            new RouteAction<EmptyRequestData>(
                StatePrefix,
                async (url, info, sessionId, output, cancellationToken) =>
                {
                    var pmc = profileHelper.GetPmcProfile(sessionId);
                    if (pmc == null || pmc.Health == null)
                    {
                        return httpResponseUtil.GetBody(new object(),
                            SPTarkov.Server.Core.Models.Enums.BackendErrorCodes.UnknownError,
                            "No live PMC profile/Health data for this session yet");
                    }

                    return httpResponseUtil.GetBody(BuildState(pmc));
                }
            ),

            // .../set-bodypart/{part}/{maxValue} - part is one of Health.BodyParts's keys (Head,
            // Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg). Sets both Maximum and Current to
            // maxValue - a debug tool raising max HP almost always also wants to see it topped up
            // immediately, rather than a silent cap bump that still shows the old (lower) health bar.
            new RouteAction<EmptyRequestData>(
                SetBodyPartPrefix,
                async (url, info, sessionId, output, cancellationToken) =>
                {
                    var segments = url[SetBodyPartPrefix.Length..].Split('/');
                    if (segments.Length != 2
                        || !double.TryParse(segments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        return httpResponseUtil.GetBody(new object(),
                            SPTarkov.Server.Core.Models.Enums.BackendErrorCodes.UnknownError,
                            $"Malformed set-bodypart request: '{url}'");
                    }

                    var part = segments[0];
                    var pmc = profileHelper.GetPmcProfile(sessionId);
                    if (pmc?.Health?.BodyParts == null || !pmc.Health.BodyParts.TryGetValue(part, out var bodyPart) || bodyPart?.Health == null)
                    {
                        return httpResponseUtil.GetBody(new object(),
                            SPTarkov.Server.Core.Models.Enums.BackendErrorCodes.UnknownError,
                            $"Unknown body part '{part}' or no live Health data for this session");
                    }

                    bodyPart.Health.Maximum = value;
                    bodyPart.Health.Current = value;
                    await saveServer.SaveProfileAsync(sessionId, cancellationToken);

                    return httpResponseUtil.GetBody(BuildState(pmc));
                }
            ),

            // .../set-vital/{name}/{maxValue} - name is "hydration", "energy" or "temperature".
            new RouteAction<EmptyRequestData>(
                SetVitalPrefix,
                async (url, info, sessionId, output, cancellationToken) =>
                {
                    var segments = url[SetVitalPrefix.Length..].Split('/');
                    if (segments.Length != 2
                        || !double.TryParse(segments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        return httpResponseUtil.GetBody(new object(),
                            SPTarkov.Server.Core.Models.Enums.BackendErrorCodes.UnknownError,
                            $"Malformed set-vital request: '{url}'");
                    }

                    var pmc = profileHelper.GetPmcProfile(sessionId);
                    var vital = segments[0].ToLowerInvariant() switch
                    {
                        "hydration" => pmc?.Health?.Hydration,
                        "energy" => pmc?.Health?.Energy,
                        "temperature" => pmc?.Health?.Temperature,
                        _ => null,
                    };
                    if (vital == null)
                    {
                        return httpResponseUtil.GetBody(new object(),
                            SPTarkov.Server.Core.Models.Enums.BackendErrorCodes.UnknownError,
                            $"Unknown vital '{segments[0]}' or no live Health data for this session");
                    }

                    vital.Maximum = value;
                    vital.Current = value;
                    await saveServer.SaveProfileAsync(sessionId, cancellationToken);

                    // vital being non-null (checked above) implies pmc was non-null too.
                    return httpResponseUtil.GetBody(BuildState(pmc!));
                }
            ),

            // .../set-skill/{skillId}/{progress} - skillId is a SkillTypes name (e.g. "Endurance",
            // "Strength", "Charisma", ...); progress is the raw 0-5100 point value the client's skill
            // bar reads (level = progress / 100, roughly - CommonSkill.MaxSkillProgress = 5100). Adds
            // the skill entry if the profile doesn't have one yet (a fresh profile only carries
            // entries for skills already touched in-raid).
            new RouteAction<EmptyRequestData>(
                SetSkillPrefix,
                async (url, info, sessionId, output, cancellationToken) =>
                {
                    var segments = url[SetSkillPrefix.Length..].Split('/');
                    if (segments.Length != 2
                        || !Enum.TryParse<SPTarkov.Server.Core.Models.Enums.SkillTypes>(segments[0], true, out var skillType)
                        || !double.TryParse(segments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var progress))
                    {
                        return httpResponseUtil.GetBody(new object(),
                            SPTarkov.Server.Core.Models.Enums.BackendErrorCodes.UnknownError,
                            $"Malformed set-skill request: '{url}'");
                    }

                    var pmc = profileHelper.GetPmcProfile(sessionId);
                    if (pmc?.Skills?.Common == null)
                    {
                        return httpResponseUtil.GetBody(new object(),
                            SPTarkov.Server.Core.Models.Enums.BackendErrorCodes.UnknownError,
                            "No live Skills data for this session yet");
                    }

                    var skillList = pmc.Skills.Common.ToList();
                    var existing = skillList.FirstOrDefault(s => s.Id == skillType);
                    if (existing != null)
                    {
                        existing.Progress = progress;
                    }
                    else
                    {
                        skillList.Add(new CommonSkill { Id = skillType, Progress = progress });
                    }

                    pmc.Skills.Common = skillList;
                    await saveServer.SaveProfileAsync(sessionId, cancellationToken);

                    return httpResponseUtil.GetBody(BuildState(pmc));
                }
            ),
        ];
    }

    private static CharacterState BuildState(PmcData pmc)
    {
        var health = pmc.Health;
        var bodyParts = new Dictionary<string, VitalState>();
        if (health?.BodyParts != null)
        {
            foreach (var (part, value) in health.BodyParts)
            {
                if (value?.Health != null)
                {
                    bodyParts[part] = new VitalState(value.Health.Current ?? 0, value.Health.Maximum ?? 0);
                }
            }
        }

        var skills = (pmc.Skills?.Common ?? []).Select(s => new SkillState(s.Id.ToString(), s.Progress)).ToList();

        return new CharacterState(
            bodyParts,
            health?.Hydration == null ? null : new VitalState(health.Hydration.Current ?? 0, health.Hydration.Maximum ?? 0),
            health?.Energy == null ? null : new VitalState(health.Energy.Current ?? 0, health.Energy.Maximum ?? 0),
            health?.Temperature == null ? null : new VitalState(health.Temperature.Current ?? 0, health.Temperature.Maximum ?? 0),
            skills
        );
    }
}

public sealed record VitalState(double Current, double Maximum);

public sealed record SkillState(string Id, double Progress);

public sealed record CharacterState(
    Dictionary<string, VitalState> BodyParts,
    VitalState? Hydration,
    VitalState? Energy,
    VitalState? Temperature,
    List<SkillState> Skills
);
