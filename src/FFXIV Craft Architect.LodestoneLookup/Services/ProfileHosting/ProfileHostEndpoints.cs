using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public static class ProfileHostEndpoints
{
    private const string AccessKeyHeaderName = "X-Profile-Key";

    public static RouteGroupBuilder MapProfileHostEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/profile-host");

        group.MapGet("/health", (ProfileHostOptions options) => Results.Ok(new ProfileHostHealthResponse
        {
            ProfileHostEnabled = options.Enabled,
            ProtocolVersion = 1
        }));

        group.MapGet(
            "/profile",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                return profile == null ? Results.Unauthorized() : Results.Ok(profile);
            });

        group.MapGet(
            "/keys",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var authenticated = await AuthenticateAccessKeyAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (authenticated == null)
                {
                    return Results.Unauthorized();
                }

                return Results.Ok(await store.LoadActiveAccessKeysAsync(
                    authenticated.Profile.ProfileId,
                    authenticated.KeyIds,
                    cancellationToken));
            });

        group.MapDelete(
            "/keys/current",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var authenticated = await AuthenticateAccessKeyAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (authenticated == null)
                {
                    return Results.Unauthorized();
                }

                foreach (var keyId in authenticated.KeyIds)
                {
                    await store.RevokeAccessKeyAsync(
                        authenticated.Profile.ProfileId,
                        keyId,
                        cancellationToken);
                }
                return Results.NoContent();
            });

        group.MapDelete(
            "/keys/{keyId}",
            async (
                string keyId,
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var authenticated = await AuthenticateAccessKeyAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (authenticated == null)
                {
                    return Results.Unauthorized();
                }
                if (authenticated.KeyIds.Contains(keyId, StringComparer.Ordinal))
                {
                    return Results.BadRequest(new
                    {
                        error = "current_key_requires_sign_out",
                        message = "Use the current-key endpoint to sign out this browser."
                    });
                }

                return await store.RevokeAccessKeyAsync(
                    authenticated.Profile.ProfileId,
                    keyId,
                    cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound();
            });

        group.MapPost(
            "/pairing/create",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                ProfilePairingCodeService pairingCodes,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                var pairingCode = pairingCodes.Create();
                var expiresAtUtc = DateTime.UtcNow.AddMinutes(10);
                await store.CreatePairingCodeAsync(
                    profile.ProfileId,
                    pairingCode.TokenHash,
                    expiresAtUtc,
                    cancellationToken);
                return Results.Ok(new ProfileHostPairingCodeResponse
                {
                    PairingCode = pairingCode.Plaintext,
                    ExpiresAtUtc = expiresAtUtc,
                    ProfileId = profile.ProfileId,
                    DisplayName = profile.DisplayName
                });
            });

        group.MapPost(
            "/pairing/redeem",
            async (
                ProfileHostPairingRedeemRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                ProfilePairingCodeService pairingCodes,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var plaintext = request.PairingCode?.Trim() ?? string.Empty;
                if (plaintext.Length != 37 ||
                    !plaintext.StartsWith("pair_", StringComparison.Ordinal))
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_or_expired_pairing_code",
                        message = "The pairing code is invalid or has expired."
                    });
                }

                CreatedProfileAccessKey? accessKey = null;
                var profile = await authentication.ExecuteAsync(
                    plaintext,
                    async ct =>
                    {
                        accessKey = hasher.CreateAccessKey();
                        return await store.RedeemPairingCodeAsync(
                            pairingCodes.Hash(plaintext),
                            accessKey.StoredHash,
                            accessKey.Fingerprint,
                            DateTime.UtcNow,
                            ct);
                    },
                    cancellationToken);
                if (profile == null || accessKey == null)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_or_expired_pairing_code",
                        message = "The pairing code is invalid or has expired."
                    });
                }

                return Results.Ok(new ProfileHostPairingRedeemResponse
                {
                    AccessKey = accessKey.PlaintextKey,
                    Profile = profile
                });
            });

        group.MapGet(
            "/changes",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                long? sinceRevision,
                int? limit,
                string? collections,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                if (limit is <= 0 or > 50)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_page_limit",
                        message = "The changes page limit must be between 1 and 50."
                    });
                }

                if (!TryParseCollectionFilter(collections, out var collectionFilter))
                {
                    return Results.BadRequest(new
                    {
                        error = "unsupported_collection",
                        message = "The changes filter names a collection that is not syncable."
                    });
                }

                var changes = await store.LoadChangesAsync(
                    profile.ProfileId,
                    sinceRevision ?? 0,
                    cancellationToken,
                    limit,
                    collectionFilter);
                return Results.Ok(ToSummarizedChanges(changes));
            });

        group.MapGet("/changes/stream", StreamChangesAsync);

        group.MapGet(
            "/objects/{collection}/{objectId}",
            async (
                string collection,
                string objectId,
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                if (!ProfileSyncCollections.All.Contains(collection))
                {
                    return Results.BadRequest(new
                    {
                        error = "unsupported_collection",
                        message = $"Collection '{collection}' is not syncable."
                    });
                }

                var hosted = await store.LoadHostedObjectAsync(
                    profile.ProfileId,
                    collection,
                    objectId,
                    cancellationToken);
                return hosted is null or { Deleted: true }
                    ? Results.NotFound()
                    : Results.Ok(hosted);
            });

        group.MapPut(
            "/objects/{collection}/{objectId}",
            async (
                string collection,
                string objectId,
                ProfileSyncPutRequest putRequest,
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                if (!ProfileSyncCollections.All.Contains(collection))
                {
                    return Results.BadRequest(new
                    {
                        error = "unsupported_collection",
                        message = $"Collection '{collection}' is not syncable."
                    });
                }

                var result = await store.PutObjectAsync(
                    profile.ProfileId,
                    collection,
                    objectId,
                    putRequest.PayloadJson,
                    putRequest.ExpectedRevision,
                    cancellationToken);

                return result.Conflict ? Results.Conflict(result) : Results.Ok(result);
            });

        group.MapDelete(
            "/objects/{collection}/{objectId}",
            async (
                string collection,
                string objectId,
                long? expectedRevision,
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                if (!ProfileSyncCollections.All.Contains(collection))
                {
                    return Results.BadRequest(new
                    {
                        error = "unsupported_collection",
                        message = $"Collection '{collection}' is not syncable."
                    });
                }

                var result = await store.DeleteObjectAsync(
                    profile.ProfileId,
                    collection,
                    objectId,
                    expectedRevision ?? 0,
                    cancellationToken);

                return result.Conflict ? Results.Conflict(result) : Results.Ok(result);
            });

        group.MapPost(
            "/bootstrap/upload",
            async (
                ProfileHostBootstrapPayload payload,
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                if (payload.Objects == null ||
                    payload.Objects.Any(item => item == null || !ProfileSyncCollections.All.Contains(item.Collection)))
                {
                    return Results.BadRequest(new
                    {
                        error = "unsupported_collection",
                        message = "Bootstrap contains a collection that is not syncable."
                    });
                }

                foreach (var item in payload.Objects)
                {
                    var result = await store.PutObjectAsync(
                        profile.ProfileId,
                        item.Collection,
                        item.ObjectId,
                        item.PayloadJson,
                        0,
                        cancellationToken);

                    if (result.Conflict)
                    {
                        return Results.Conflict(result);
                    }
                }

                var changes = await store.LoadChangesAsync(profile.ProfileId, 0, cancellationToken);
                return Results.Ok(ToPortableChanges(changes));
            });

        group.MapGet(
            "/bootstrap/export",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                ProfileAuthenticationGate authentication,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(
                    request,
                    authentication,
                    store,
                    hasher,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                var changes = await store.LoadChangesAsync(profile.ProfileId, 0, cancellationToken);
                return Results.Ok(new ProfileHostBootstrapPayload
                {
                    Objects = PortableObjects(changes.Objects)
                });
            });

        return group;
    }

    private static ProfileSyncChangesResponse ToPortableChanges(
        ProfileSyncChangesResponse changes) =>
        new()
        {
            ServerRevision = changes.ServerRevision,
            HasMore = changes.HasMore,
            Objects = PortableObjects(changes.Objects)
        };

    private static ProfileSyncChangesResponse ToSummarizedChanges(
        ProfileSyncChangesResponse changes) =>
        new()
        {
            ServerRevision = changes.ServerRevision,
            HasMore = changes.HasMore,
            Objects = PortableObjects(changes.Objects)
                .Select(TradeOrderArchiveSummaryProjector.Apply)
                .ToArray()
        };

    private static bool TryParseCollectionFilter(
        string? collections,
        out IReadOnlyList<string>? filter)
    {
        filter = null;
        if (string.IsNullOrWhiteSpace(collections))
        {
            return true;
        }

        var parsed = collections
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parsed.Length == 0 || parsed.Any(item => !ProfileSyncCollections.All.Contains(item)))
        {
            return false;
        }

        filter = parsed;
        return true;
    }

    private static IReadOnlyList<ProfileSyncObjectEnvelope> PortableObjects(
        IReadOnlyList<ProfileSyncObjectEnvelope> objects) =>
        objects
            .Where(item => ProfileSyncCollections.All.Contains(item.Collection))
            .ToArray();

    private static async Task StreamChangesAsync(
        HttpContext context,
        long? sinceRevision,
        ProfileHostOptions options,
        ProfileAuthenticationGate authentication,
        SqliteProfileHostStore store,
        ProfileAccessKeyHasher hasher,
        ProfileHostChangeSignal changeSignal)
    {
        if (!options.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var cancellationToken = context.RequestAborted;
        var profile = await AuthenticateAsync(
            context.Request,
            authentication,
            store,
            hasher,
            cancellationToken);
        if (profile == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var cursor = sinceRevision ?? 0;
        if (cursor < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = "invalid_revision_cursor",
                    message = "The profile revision cursor cannot be negative."
                },
                cancellationToken);
            return;
        }

        if (cursor > profile.ServerRevision)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = "revision_cursor_ahead",
                    message = "The profile revision cursor is newer than the authenticated profile."
                },
                cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        await context.Response.StartAsync(cancellationToken);

        var lease = options.ChangeStreamLease > TimeSpan.Zero
            ? options.ChangeStreamLease
            : TimeSpan.FromMinutes(1);
        var heartbeat = options.ChangeStreamHeartbeat > TimeSpan.Zero
            ? options.ChangeStreamHeartbeat
            : TimeSpan.FromSeconds(15);
        var leaseEndsAt = DateTimeOffset.UtcNow.Add(lease);
        while (!cancellationToken.IsCancellationRequested)
        {
            var remainingLease = leaseEndsAt - DateTimeOffset.UtcNow;
            if (remainingLease <= TimeSpan.Zero)
            {
                break;
            }

            var observation = changeSignal.Observe(profile.ProfileId);
            var currentRevision = await store.LoadServerRevisionAsync(
                profile.ProfileId,
                cancellationToken);
            if (currentRevision > cursor)
            {
                await WriteRevisionEventAsync(
                    context.Response,
                    currentRevision,
                    cancellationToken);
                cursor = currentRevision;
                continue;
            }

            var heartbeatDelay = Task.Delay(
                remainingLease < heartbeat
                    ? remainingLease
                    : heartbeat,
                cancellationToken);
            var completed = await Task.WhenAny(observation.Changed, heartbeatDelay);
            if (completed == heartbeatDelay)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await context.Response.WriteAsync(": keepalive\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
    }

    private static async Task WriteRevisionEventAsync(
        HttpResponse response,
        long revision,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { serverRevision = revision });
        await response.WriteAsync(
            $"id: {revision}\nevent: profile-revision\ndata: {payload}\n\n",
            cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task<ProfileHostProfileResponse?> AuthenticateAsync(
        HttpRequest request,
        ProfileAuthenticationGate authentication,
        SqliteProfileHostStore store,
        ProfileAccessKeyHasher hasher,
        CancellationToken cancellationToken)
    {
        var accessKey = ReadAccessKey(request);
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            return null;
        }

        return await authentication.ExecuteAsync(
            accessKey,
            ct => store.TryAuthenticateCachedAsync(accessKey, hasher, ct),
            ct => store.AuthenticateAsync(accessKey, hasher, ct),
            cancellationToken);
    }

    private static async Task<AuthenticatedProfileAccessKey?> AuthenticateAccessKeyAsync(
        HttpRequest request,
        ProfileAuthenticationGate authentication,
        SqliteProfileHostStore store,
        ProfileAccessKeyHasher hasher,
        CancellationToken cancellationToken)
    {
        var accessKey = ReadAccessKey(request);
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            return null;
        }

        return await authentication.ExecuteAsync(
            accessKey,
            ct => store.TryAuthenticateCachedAccessKeyAsync(accessKey, hasher, ct),
            ct => store.AuthenticateAccessKeyAsync(accessKey, hasher, ct),
            cancellationToken);
    }

    private static string? ReadAccessKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(AccessKeyHeaderName, out var value))
        {
            return value.ToString();
        }

        return null;
    }
}
