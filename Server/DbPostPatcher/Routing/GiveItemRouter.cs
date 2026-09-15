using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;

namespace DbPostPatcher.Routing;

/// <summary>
///     Backs the F9 client panel's "Give Item" buttons. Delivers the item as a mailed attachment
///     instead of writing directly into the live profile's inventory - mail delivery already goes
///     through the game's normal live-notification push, so the player sees it immediately without
///     needing to relog or re-enter a raid. See NOTES.md for why this path was chosen over trying
///     to splice an item straight into the in-memory inventory.
///
///     Every route is a GET with no request body and lives under "/singleplayer/..." - see
///     SptHttpListener.ShouldShuffleRequest/ShouldShuffleResponse in server-csharp: the server
///     byte-shuffles (a reproducible permutation, not real crypto) both request and response
///     bodies to match live Tarkov's wire format, except responses under "/singleplayer/" and
///     anything shorter than 4 bytes. A GET with no body sidesteps request-shuffling entirely (a
///     POST body here would get deshuffled as if it were shuffled-on-the-wire and come out as
///     garbage, or throw outright once decoded as an "impossible length" frame) - that's also why
///     the item id is baked into a fixed per-item route below instead of taken as a request body:
///     the catalog is small and static, so one exact-match route per entry is simpler than solving
///     "how do I send an unshuffled body" for a single string.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public sealed class GiveItemRouter(JsonUtil jsonUtil, HttpResponseUtil httpResponseUtil, MailSendService mailSendService, TemplateTable templateTable)
    : StaticRouter(jsonUtil, BuildRoutes(httpResponseUtil, mailSendService, templateTable))
{
    private static List<RouteAction> BuildRoutes(HttpResponseUtil httpResponseUtil, MailSendService mailSendService, TemplateTable templateTable)
    {
        var routes = new List<RouteAction>
        {
            new RouteAction<EmptyRequestData>(
                "/singleplayer/dbpostpatcher/ping",
                async (url, info, sessionId, output, cancellationToken) => httpResponseUtil.GetBody(new PingResponse())
            ),
            new RouteAction<EmptyRequestData>(
                "/singleplayer/dbpostpatcher/catalog",
                async (url, info, sessionId, output, cancellationToken) => httpResponseUtil.GetBody(GiveItemCatalog.Items)
            ),
        };

        foreach (var entry in GiveItemCatalog.Items)
        {
            routes.Add(
                new RouteAction<EmptyRequestData>(
                    $"/singleplayer/dbpostpatcher/give-item/{entry.ItemTemplateId}",
                    async (url, info, sessionId, output, cancellationToken) =>
                    {
                        if (!templateTable.Items.ContainsKey(new MongoId(entry.ItemTemplateId)))
                        {
                            return httpResponseUtil.GetBody(
                                new object(),
                                SPTarkov.Server.Core.Models.Enums.BackendErrorCodes.UnknownError,
                                $"'{entry.ItemTemplateId}' ({entry.Label}) is in GiveItemCatalog but no longer exists in the database"
                            );
                        }

                        var item = new Item { Id = new MongoId(), Template = new MongoId(entry.ItemTemplateId) };
                        mailSendService.SendSystemMessageToPlayer(sessionId, $"DbPostPatcher: {entry.Label}", [item]);

                        return httpResponseUtil.EmptyResponse();
                    }
                )
            );
        }

        return routes;
    }
}

public sealed record PingResponse
{
    public string Mod { get; init; } = "DbPostPatcher";
    public string Version { get; init; } = "0.1.0";
}
