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
            ProfileHostEnabled = options.Enabled
        }));

        group.MapGet(
            "/profile",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(request, store, hasher, cancellationToken);
                return profile == null ? Results.Unauthorized() : Results.Ok(profile);
            });

        group.MapGet(
            "/changes",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                long? sinceRevision,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(request, store, hasher, cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                var changes = await store.LoadChangesAsync(profile.ProfileId, sinceRevision ?? 0, cancellationToken);
                return Results.Ok(changes);
            });

        group.MapPut(
            "/objects/{collection}/{objectId}",
            async (
                string collection,
                string objectId,
                ProfileSyncPutRequest putRequest,
                HttpRequest request,
                ProfileHostOptions options,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(request, store, hasher, cancellationToken);
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
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(request, store, hasher, cancellationToken);
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
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(request, store, hasher, cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
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
                return Results.Ok(changes);
            });

        group.MapGet(
            "/bootstrap/export",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                SqliteProfileHostStore store,
                ProfileAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var profile = await AuthenticateAsync(request, store, hasher, cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                var changes = await store.LoadChangesAsync(profile.ProfileId, 0, cancellationToken);
                return Results.Ok(new ProfileHostBootstrapPayload { Objects = changes.Objects });
            });

        return group;
    }

    private static async Task<ProfileHostProfileResponse?> AuthenticateAsync(
        HttpRequest request,
        SqliteProfileHostStore store,
        ProfileAccessKeyHasher hasher,
        CancellationToken cancellationToken)
    {
        var accessKey = ReadAccessKey(request);
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            return null;
        }

        return await store.AuthenticateAsync(accessKey, hasher, cancellationToken);
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
