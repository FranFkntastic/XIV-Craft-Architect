using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed record ProfileHostMigrationCommitAttempt(
    ProfileHostMigrationCommitResponse? Response,
    ProfileHostMigrationPreflightResponse? Conflict)
{
    public bool Success => Response != null;
}

public sealed partial class SqliteProfileHostStore
{
    private static readonly JsonSerializerOptions MigrationJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    public async Task<ProfileHostMigrationPreflightResponse> PreflightMigrationAsync(
        string profileId,
        ProfileHostMigrationPreflightRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var response = await BuildMigrationPreflightAsync(
            connection,
            transaction,
            profileId,
            request,
            ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    public async Task<ProfileHostMigrationCommitAttempt> CommitMigrationAsync(
        string profileId,
        ProfileHostMigrationCommitRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var preflightRequest = new ProfileHostMigrationPreflightRequest
        {
            MigrationId = request.MigrationId,
            Objects = request.Objects ?? Array.Empty<ProfileHostMigrationObjectInput>(),
            Resolutions = request.Resolutions ?? Array.Empty<ProfileHostMigrationResolution>()
        };
        var requestHash = ComputeRequestHash(preflightRequest);
        var receipt = await LoadMigrationReceiptAsync(
            connection,
            transaction,
            profileId,
            request.MigrationId,
            ct);
        if (receipt != null)
        {
            if (string.Equals(receipt.Value.RequestHash, requestHash, StringComparison.Ordinal))
            {
                var replay = JsonSerializer.Deserialize<ProfileHostMigrationCommitResponse>(
                    receipt.Value.ResponseJson,
                    MigrationJsonOptions) ??
                    throw new InvalidOperationException(
                        "The hosted migration receipt contains an invalid response.");
                await transaction.RollbackAsync(ct);
                return new ProfileHostMigrationCommitAttempt(replay, null);
            }

            await transaction.RollbackAsync(ct);
            return new ProfileHostMigrationCommitAttempt(
                null,
                CreateCommitBlocker(
                    request.MigrationId,
                    requestHash,
                    ProfileHostMigrationBlockerCodes.MigrationIdConflict,
                    "This migration ID is already committed with different content."));
        }

        var preflight = await BuildMigrationPreflightAsync(
            connection,
            transaction,
            profileId,
            preflightRequest,
            ct);
        if (!string.Equals(
                request.PreflightHash,
                preflight.PreflightHash,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(ct);
            return new ProfileHostMigrationCommitAttempt(
                null,
                WithBlocker(
                    preflight,
                    ProfileHostMigrationBlockerCodes.PreflightChanged,
                    "The hosted profile changed after preflight. Run preflight again."));
        }

        if (!preflight.CanCommit)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileHostMigrationCommitAttempt(null, preflight);
        }

        var inputs = preflightRequest.Objects
            .Where(item => ProfileSyncCollections.All.Contains(item.Collection))
            .ToDictionary(MigrationIdentity, StringTupleComparer.Ordinal);
        var authoritative = await LoadMigrationObjectsAsync(
            connection,
            transaction,
            profileId,
            ct);
        var committed = new List<ProfileHostMigrationAuthoritativeObject>(
            preflight.Objects.Count);
        var now = DateTime.UtcNow;
        foreach (var assessment in preflight.Objects
                     .OrderBy(item => item.Collection, StringComparer.Ordinal)
                     .ThenBy(item => item.ObjectId, StringComparer.Ordinal))
        {
            var identity = (assessment.Collection, assessment.ObjectId);
            var shouldWrite =
                assessment.Disposition == ProfileHostMigrationObjectDisposition.Insert ||
                assessment is
                {
                    Disposition: ProfileHostMigrationObjectDisposition.SameIdDifferentContent,
                    Resolution: ProfileHostMigrationConflictResolution.UseIncoming
                } ||
                assessment is
                {
                    Disposition: ProfileHostMigrationObjectDisposition.AuthoritativeTombstone,
                    Resolution: ProfileHostMigrationConflictResolution.ResurrectIncoming
                };
            long revision;
            var deleted = false;
            if (shouldWrite)
            {
                revision = await ReserveNextRevisionAsync(
                    connection,
                    transaction,
                    profileId,
                    ct);
                await UpsertMigratedObjectAsync(
                    connection,
                    transaction,
                    profileId,
                    inputs[identity],
                    revision,
                    now,
                    ct);
            }
            else
            {
                if (!authoritative.TryGetValue(identity, out var current))
                {
                    throw new InvalidOperationException(
                        "A resolved hosted migration object lost its authoritative revision.");
                }

                revision = current.Revision;
                deleted = current.Deleted;
            }

            committed.Add(new ProfileHostMigrationAuthoritativeObject
            {
                Collection = assessment.Collection,
                ObjectId = assessment.ObjectId,
                Revision = revision,
                Deleted = deleted
            });
        }

        var serverRevision = await GetServerRevisionAsync(
            connection,
            profileId,
            ct,
            transaction);
        var response = new ProfileHostMigrationCommitResponse
        {
            MigrationId = request.MigrationId,
            RequestHash = requestHash,
            ServerRevision = serverRevision,
            Objects = committed
        };
        response.ReceiptHash = ComputeReceiptHash(profileId, response);
        var responseJson = JsonSerializer.Serialize(response, MigrationJsonOptions);
        await InsertMigrationReceiptAsync(
            connection,
            transaction,
            profileId,
            response,
            responseJson,
            now,
            ct);
        await transaction.CommitAsync(ct);
        return new ProfileHostMigrationCommitAttempt(response, null);
    }

    private static async Task<ProfileHostMigrationPreflightResponse> BuildMigrationPreflightAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        ProfileHostMigrationPreflightRequest request,
        CancellationToken ct)
    {
        var blockers = new List<ProfileHostMigrationBlocker>();
        var assessments = new List<ProfileHostMigrationObjectAssessment>();
        var objects = request.Objects ?? Array.Empty<ProfileHostMigrationObjectInput>();
        var resolutions = request.Resolutions ?? Array.Empty<ProfileHostMigrationResolution>();
        var requestHash = ComputeRequestHash(request);
        if (request.MigrationId == Guid.Empty)
        {
            AddBlocker(
                blockers,
                ProfileHostMigrationBlockerCodes.InvalidMigrationId,
                "MigrationId must be a non-empty GUID.");
        }

        if (objects.Count == 0)
        {
            AddBlocker(
                blockers,
                ProfileHostMigrationBlockerCodes.EmptyMigration,
                "A hosted migration must contain at least one object.");
        }

        var resolutionMap = new Dictionary<
            (string Collection, string ObjectId),
            ProfileHostMigrationConflictResolution>(StringTupleComparer.Ordinal);
        foreach (var resolution in resolutions)
        {
            var identity = (resolution.Collection ?? string.Empty, resolution.ObjectId ?? string.Empty);
            if (!resolutionMap.TryAdd(identity, resolution.Resolution))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.DuplicateObjectIdentity,
                    "A migration resolution identity appears more than once.",
                    identity.Item1,
                    identity.Item2);
            }
        }

        var authoritative = await LoadMigrationObjectsAsync(
            connection,
            transaction,
            profileId,
            ct);
        var inputs = new Dictionary<
            (string Collection, string ObjectId),
            ProfileHostMigrationObjectInput>(StringTupleComparer.Ordinal);
        foreach (var input in objects)
        {
            var collection = input?.Collection ?? string.Empty;
            var objectId = input?.ObjectId ?? string.Empty;
            if (string.Equals(
                    collection,
                    ProfileHostMigrationCollections.TradeOrderCraftSnapshots,
                    StringComparison.Ordinal))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.UnsupportedOrderCraftSnapshot,
                    "Trade order craft snapshots are not supported by hosted migration and were not dropped.",
                    collection,
                    objectId);
                continue;
            }

            if (!ProfileSyncCollections.All.Contains(collection))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.UnsupportedCollection,
                    $"Collection '{collection}' is not accepted by hosted migration.",
                    collection,
                    objectId);
                continue;
            }

            var identity = (collection, objectId);
            if (string.IsNullOrWhiteSpace(objectId) || !inputs.TryAdd(identity, input!))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.DuplicateObjectIdentity,
                    "A migration object identity is empty or appears more than once.",
                    collection,
                    objectId);
                continue;
            }

            if (!TryCanonicalizeJson(
                    input!.PayloadJson ?? string.Empty,
                    out var canonical,
                    out var incomingHash))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.InvalidPayload,
                    "The migration object payload is not valid JSON.",
                    collection,
                    objectId);
                continue;
            }

            var disposition = ProfileHostMigrationObjectDisposition.Insert;
            long? authoritativeRevision = null;
            var authoritativeDeleted = false;
            DateTime? authoritativeDeletedAtUtc = null;
            string? authoritativeHash = null;
            if (authoritative.TryGetValue(identity, out var current))
            {
                authoritativeRevision = current.Revision;
                authoritativeDeleted = current.Deleted;
                authoritativeDeletedAtUtc = current.DeletedAtUtc;
                if (!TryCanonicalizeJson(
                        current.PayloadJson,
                        out var authoritativeCanonical,
                        out authoritativeHash))
                {
                    authoritativeCanonical = current.PayloadJson;
                    authoritativeHash = HashText(authoritativeCanonical);
                }

                disposition = current.Deleted
                    ? ProfileHostMigrationObjectDisposition.AuthoritativeTombstone
                    : string.Equals(
                            canonical,
                            authoritativeCanonical,
                            StringComparison.Ordinal)
                        ? ProfileHostMigrationObjectDisposition.Identical
                        : ProfileHostMigrationObjectDisposition.SameIdDifferentContent;
            }

            resolutionMap.TryGetValue(identity, out var selectedResolution);
            ProfileHostMigrationConflictResolution? resolution =
                resolutionMap.ContainsKey(identity) ? selectedResolution : null;
            var requiresResolution = disposition is
                ProfileHostMigrationObjectDisposition.SameIdDifferentContent or
                ProfileHostMigrationObjectDisposition.AuthoritativeTombstone;
            if (requiresResolution && resolution == null)
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.ResolutionRequired,
                    disposition == ProfileHostMigrationObjectDisposition.AuthoritativeTombstone
                        ? "The authoritative object is deleted. Explicitly keep the deletion or resurrect the incoming object."
                        : "The same hosted object ID has different content and requires an explicit resolution.",
                    collection,
                    objectId);
            }
            else if (disposition ==
                         ProfileHostMigrationObjectDisposition.SameIdDifferentContent &&
                     resolution == ProfileHostMigrationConflictResolution.ResurrectIncoming)
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.UnexpectedResolution,
                    "Resurrection is only valid when the authoritative object is deleted.",
                    collection,
                    objectId);
            }
            else if (disposition ==
                         ProfileHostMigrationObjectDisposition.AuthoritativeTombstone &&
                     resolution == ProfileHostMigrationConflictResolution.UseIncoming)
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.UnexpectedResolution,
                    "A deleted authoritative object requires the explicit resurrection resolution.",
                    collection,
                    objectId);
            }
            else if (disposition is not (
                         ProfileHostMigrationObjectDisposition.SameIdDifferentContent or
                         ProfileHostMigrationObjectDisposition.AuthoritativeTombstone) &&
                     resolution != null)
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.UnexpectedResolution,
                    "Only same-ID-different-content objects may carry a migration resolution.",
                    collection,
                    objectId);
            }

            assessments.Add(new ProfileHostMigrationObjectAssessment
            {
                Collection = collection,
                ObjectId = objectId,
                Disposition = disposition,
                Resolution = resolution,
                AuthoritativeRevision = authoritativeRevision,
                AuthoritativeDeleted = authoritativeDeleted,
                AuthoritativeDeletedAtUtc = authoritativeDeletedAtUtc,
                IncomingContentHash = incomingHash,
                AuthoritativeContentHash = authoritativeHash
            });
        }

        foreach (var resolution in resolutionMap.Keys)
        {
            if (!inputs.ContainsKey(resolution))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.UnexpectedResolution,
                    "A migration resolution does not identify an incoming object.",
                    resolution.Collection,
                    resolution.ObjectId);
            }
        }

        var finalObjects = authoritative
            .Where(pair => !pair.Value.Deleted &&
                           ProfileSyncCollections.All.Contains(pair.Key.Collection))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.PayloadJson,
                StringTupleComparer.Ordinal);
        foreach (var assessment in assessments)
        {
            var identity = (assessment.Collection, assessment.ObjectId);
            if (assessment.Disposition == ProfileHostMigrationObjectDisposition.Insert ||
                assessment.Resolution is
                    ProfileHostMigrationConflictResolution.UseIncoming or
                    ProfileHostMigrationConflictResolution.ResurrectIncoming)
            {
                finalObjects[identity] = inputs[identity].PayloadJson ?? string.Empty;
            }
        }

        var validator = new MigrationGraphValidator(finalObjects, blockers);
        foreach (var assessment in assessments)
        {
            var identity = (assessment.Collection, assessment.ObjectId);
            if (ProfileSyncCollections.All.Contains(identity.Collection) &&
                (assessment.Disposition !=
                    ProfileHostMigrationObjectDisposition.AuthoritativeTombstone ||
                 assessment.Resolution ==
                    ProfileHostMigrationConflictResolution.ResurrectIncoming))
            {
                validator.Validate(identity);
            }
        }

        var response = new ProfileHostMigrationPreflightResponse
        {
            MigrationId = request.MigrationId,
            RequestHash = requestHash,
            Objects = assessments
                .OrderBy(item => item.Collection, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                .ToArray(),
            Blockers = blockers
                .OrderBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Collection, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                .ToArray()
        };
        response.CanCommit = response.Blockers.Count == 0;
        response.PreflightHash = ComputePreflightHash(response);
        return response;
    }

    private static async Task<Dictionary<
        (string Collection, string ObjectId),
        ProfileSyncObjectEnvelope>> LoadMigrationObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc
            from sync_objects
            where profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        var result = new Dictionary<
            (string Collection, string ObjectId),
            ProfileSyncObjectEnvelope>(StringTupleComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = ReadObject(reader);
            result[(item.Collection, item.ObjectId)] = item;
        }

        return result;
    }

    private static async Task UpsertMigratedObjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        ProfileHostMigrationObjectInput input,
        long revision,
        DateTime updatedAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into sync_objects (
                profile_id,
                collection,
                object_id,
                payload_json,
                revision,
                updated_at_utc,
                deleted,
                deleted_at_utc
            )
            values (
                $profileId,
                $collection,
                $objectId,
                $payloadJson,
                $revision,
                $updatedAtUtc,
                0,
                null
            )
            on conflict(profile_id, collection, object_id) do update set
                payload_json = excluded.payload_json,
                revision = excluded.revision,
                updated_at_utc = excluded.updated_at_utc,
                deleted = 0,
                deleted_at_utc = null;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", input.Collection);
        command.Parameters.AddWithValue("$objectId", input.ObjectId);
        command.Parameters.AddWithValue("$payloadJson", input.PayloadJson ?? string.Empty);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updatedAtUtc", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string RequestHash, string ResponseJson)?> LoadMigrationReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        Guid migrationId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select request_hash, response_json
            from profile_migration_receipts
            where profile_id = $profileId and migration_id = $migrationId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$migrationId", migrationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task InsertMigrationReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        ProfileHostMigrationCommitResponse response,
        string responseJson,
        DateTime createdAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into profile_migration_receipts (
                profile_id,
                migration_id,
                request_hash,
                receipt_hash,
                response_json,
                created_at_utc
            )
            values (
                $profileId,
                $migrationId,
                $requestHash,
                $receiptHash,
                $responseJson,
                $createdAtUtc
            );
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$migrationId", response.MigrationId.ToString("D"));
        command.Parameters.AddWithValue("$requestHash", response.RequestHash);
        command.Parameters.AddWithValue("$receiptHash", response.ReceiptHash);
        command.Parameters.AddWithValue("$responseJson", responseJson);
        command.Parameters.AddWithValue("$createdAtUtc", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string ComputeRequestHash(ProfileHostMigrationPreflightRequest request)
    {
        var builder = new StringBuilder();
        AppendHashPart(builder, request.MigrationId.ToString("D"));
        foreach (var item in (request.Objects ?? Array.Empty<ProfileHostMigrationObjectInput>())
                     .OrderBy(item => item?.Collection, StringComparer.Ordinal)
                     .ThenBy(item => item?.ObjectId, StringComparer.Ordinal))
        {
            AppendHashPart(builder, item?.Collection ?? string.Empty);
            AppendHashPart(builder, item?.ObjectId ?? string.Empty);
            AppendHashPart(
                builder,
                item != null &&
                TryCanonicalizeJson(item.PayloadJson ?? string.Empty, out var canonical, out _)
                    ? canonical
                    : item?.PayloadJson ?? string.Empty);
        }

        foreach (var resolution in (request.Resolutions ?? Array.Empty<ProfileHostMigrationResolution>())
                     .OrderBy(item => item?.Collection, StringComparer.Ordinal)
                     .ThenBy(item => item?.ObjectId, StringComparer.Ordinal))
        {
            AppendHashPart(builder, resolution?.Collection ?? string.Empty);
            AppendHashPart(builder, resolution?.ObjectId ?? string.Empty);
            AppendHashPart(
                builder,
                ((int)(resolution?.Resolution ??
                    ProfileHostMigrationConflictResolution.KeepAuthoritative)).ToString());
        }

        return HashText(builder.ToString());
    }

    private static string ComputePreflightHash(ProfileHostMigrationPreflightResponse response)
    {
        var builder = new StringBuilder();
        AppendHashPart(builder, response.RequestHash);
        foreach (var item in response.Objects)
        {
            AppendHashPart(builder, item.Collection);
            AppendHashPart(builder, item.ObjectId);
            AppendHashPart(builder, ((int)item.Disposition).ToString());
            AppendHashPart(builder, item.Resolution.HasValue
                ? ((int)item.Resolution.Value).ToString()
                : string.Empty);
            AppendHashPart(builder, item.AuthoritativeRevision?.ToString() ?? string.Empty);
            AppendHashPart(builder, item.AuthoritativeDeleted ? "1" : "0");
            AppendHashPart(
                builder,
                item.AuthoritativeDeletedAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty);
            AppendHashPart(builder, item.IncomingContentHash);
            AppendHashPart(builder, item.AuthoritativeContentHash ?? string.Empty);
        }

        foreach (var blocker in response.Blockers)
        {
            AppendHashPart(builder, blocker.Code);
            AppendHashPart(builder, blocker.Collection ?? string.Empty);
            AppendHashPart(builder, blocker.ObjectId ?? string.Empty);
            AppendHashPart(builder, blocker.ReferencedCollection ?? string.Empty);
            AppendHashPart(builder, blocker.ReferencedObjectId ?? string.Empty);
        }

        return HashText(builder.ToString());
    }

    private static string ComputeReceiptHash(
        string profileId,
        ProfileHostMigrationCommitResponse response)
    {
        var builder = new StringBuilder();
        AppendHashPart(builder, profileId);
        AppendHashPart(builder, response.MigrationId.ToString("D"));
        AppendHashPart(builder, response.RequestHash);
        AppendHashPart(builder, response.ServerRevision.ToString());
        foreach (var item in response.Objects)
        {
            AppendHashPart(builder, item.Collection);
            AppendHashPart(builder, item.ObjectId);
            AppendHashPart(builder, item.Revision.ToString());
            AppendHashPart(builder, item.Deleted ? "1" : "0");
        }

        return HashText(builder.ToString());
    }

    private static bool TryCanonicalizeJson(
        string payloadJson,
        out string canonical,
        out string contentHash)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonicalJson(writer, document.RootElement);
            }

            canonical = Encoding.UTF8.GetString(stream.ToArray());
            contentHash = HashText(canonical);
            return true;
        }
        catch (JsonException)
        {
            canonical = string.Empty;
            contentHash = string.Empty;
            return false;
        }
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void AppendHashPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private static (string Collection, string ObjectId) MigrationIdentity(
        ProfileHostMigrationObjectInput input) =>
        (input.Collection, input.ObjectId);

    private static ProfileHostMigrationPreflightResponse CreateCommitBlocker(
        Guid migrationId,
        string requestHash,
        string code,
        string message)
    {
        var response = new ProfileHostMigrationPreflightResponse
        {
            MigrationId = migrationId,
            RequestHash = requestHash,
            CanCommit = false,
            Blockers =
            [
                new ProfileHostMigrationBlocker
                {
                    Code = code,
                    Message = message
                }
            ]
        };
        response.PreflightHash = ComputePreflightHash(response);
        return response;
    }

    private static ProfileHostMigrationPreflightResponse WithBlocker(
        ProfileHostMigrationPreflightResponse response,
        string code,
        string message)
    {
        var blocked = new ProfileHostMigrationPreflightResponse
        {
            MigrationId = response.MigrationId,
            RequestHash = response.RequestHash,
            Objects = response.Objects,
            Blockers = response.Blockers
                .Append(new ProfileHostMigrationBlocker
                {
                    Code = code,
                    Message = message
                })
                .ToArray(),
            CanCommit = false
        };
        blocked.PreflightHash = ComputePreflightHash(blocked);
        return blocked;
    }

    private static void AddBlocker(
        ICollection<ProfileHostMigrationBlocker> blockers,
        string code,
        string message,
        string? collection = null,
        string? objectId = null,
        string? referencedCollection = null,
        string? referencedObjectId = null)
    {
        blockers.Add(new ProfileHostMigrationBlocker
        {
            Code = code,
            Message = message,
            Collection = collection,
            ObjectId = objectId,
            ReferencedCollection = referencedCollection,
            ReferencedObjectId = referencedObjectId
        });
    }

    private sealed class MigrationGraphValidator(
        IReadOnlyDictionary<(string Collection, string ObjectId), string> objects,
        ICollection<ProfileHostMigrationBlocker> blockers)
    {
        private readonly HashSet<(string Collection, string ObjectId)> _visited =
            new(StringTupleComparer.Ordinal);

        public void Validate((string Collection, string ObjectId) identity)
        {
            if (!_visited.Add(identity) ||
                !IsTradeCollection(identity.Collection))
            {
                return;
            }

            if (!objects.TryGetValue(identity, out var payloadJson))
            {
                AddMissing(identity, identity.Collection switch
                {
                    ProfileSyncCollections.TradeCompanyProfiles =>
                        ProfileHostMigrationBlockerCodes.MissingCompany,
                    ProfileSyncCollections.TradeCrafters =>
                        ProfileHostMigrationBlockerCodes.MissingCrafter,
                    ProfileSyncCollections.TradeOrders =>
                        ProfileHostMigrationBlockerCodes.MissingOrder,
                    _ => ProfileHostMigrationBlockerCodes.MissingPayrollDraft
                });
                return;
            }

            switch (identity.Collection)
            {
                case ProfileSyncCollections.TradeCompanyProfiles:
                    ValidateCompany(identity, payloadJson);
                    break;
                case ProfileSyncCollections.TradeCrafters:
                    ValidateCrafter(identity, payloadJson);
                    break;
                case ProfileSyncCollections.TradeOrders:
                    ValidateOrder(identity, payloadJson);
                    break;
                case ProfileSyncCollections.TradePayrollDrafts:
                    ValidatePayroll(identity, payloadJson);
                    break;
            }
        }

        private void ValidateCompany(
            (string Collection, string ObjectId) identity,
            string payloadJson)
        {
            if (!TryDeserialize(identity, payloadJson, out TradeCompanyProfile? company))
            {
                return;
            }

            if (!Guid.TryParse(identity.ObjectId, out var objectId) ||
                company!.Id != objectId)
            {
                AddIdentityMismatch(identity, "Trade company payload ID does not match ObjectId.");
            }
        }

        private void ValidateCrafter(
            (string Collection, string ObjectId) identity,
            string payloadJson)
        {
            if (!TryDeserialize(identity, payloadJson, out TradeCrafterProfile? crafter))
            {
                return;
            }

            if (!Guid.TryParse(identity.ObjectId, out var objectId) ||
                crafter!.Id != objectId)
            {
                AddIdentityMismatch(identity, "Trade crafter payload ID does not match ObjectId.");
            }

            ValidateCompanyReference(identity, crafter!.CompanyProfileId);
        }

        private void ValidateOrder(
            (string Collection, string ObjectId) identity,
            string payloadJson)
        {
            if (!TryDeserialize(identity, payloadJson, out TradeOrder? order))
            {
                return;
            }

            if (!Guid.TryParse(identity.ObjectId, out var objectId) ||
                order!.Id != objectId)
            {
                AddIdentityMismatch(identity, "Trade order payload ID does not match ObjectId.");
            }

            ValidateCompanyReference(identity, order!.CompanyProfileId);
            if (order.AssignedCrafterId is { } assignedCrafterId)
            {
                ValidateCrafterReference(identity, assignedCrafterId, order.CompanyProfileId);
            }

            foreach (var history in order.History ?? Array.Empty<TradeOrderHistoryEvent>())
            {
                if (history.CompanyProfileId != order.CompanyProfileId ||
                    history.OrderId != order.Id)
                {
                    AddReferenceMismatch(
                        identity,
                        ProfileSyncCollections.TradeOrders,
                        order.Id.ToString("D"),
                        "Trade order history does not preserve its company and order ownership.");
                }

                if (history.CrafterId is { } historyCrafterId)
                {
                    ValidateCrafterReference(identity, historyCrafterId, order.CompanyProfileId);
                }
            }

            if (!string.IsNullOrWhiteSpace(order.PayrollDraftId))
            {
                var payrollIdentity = (
                    ProfileSyncCollections.TradePayrollDrafts,
                    order.PayrollDraftId);
                if (!TryLoad(payrollIdentity, out TradePayrollWorkflowDraft? payroll))
                {
                    AddMissingReference(
                        identity,
                        payrollIdentity,
                        ProfileHostMigrationBlockerCodes.MissingPayrollDraft,
                        "Trade order references a missing payroll draft.");
                }
                else if (payroll!.CompanyProfileId != order.CompanyProfileId ||
                         payroll.OrderId != order.Id)
                {
                    AddReferenceMismatch(
                        identity,
                        payrollIdentity.Item1,
                        payrollIdentity.Item2,
                        "Trade order payroll reference belongs to another company or order.");
                }

                Validate(payrollIdentity);
            }
        }

        private void ValidatePayroll(
            (string Collection, string ObjectId) identity,
            string payloadJson)
        {
            if (!TryDeserialize(identity, payloadJson, out TradePayrollWorkflowDraft? payroll))
            {
                return;
            }

            if (!string.Equals(identity.ObjectId, payroll!.Id, StringComparison.Ordinal))
            {
                AddIdentityMismatch(identity, "Trade payroll payload ID does not match ObjectId.");
            }

            ValidateCompanyReference(identity, payroll!.CompanyProfileId);
            if (payroll.AssignedCrafterId is { } assignedCrafterId)
            {
                ValidateCrafterReference(identity, assignedCrafterId, payroll.CompanyProfileId);
            }

            if (payroll.OrderId is { } orderId)
            {
                var orderIdentity = (
                    ProfileSyncCollections.TradeOrders,
                    orderId.ToString("D"));
                if (!TryLoad(orderIdentity, out TradeOrder? order))
                {
                    AddMissingReference(
                        identity,
                        orderIdentity,
                        ProfileHostMigrationBlockerCodes.MissingOrder,
                        "Trade payroll draft references a missing order.");
                }
                else if (order!.CompanyProfileId != payroll.CompanyProfileId)
                {
                    AddReferenceMismatch(
                        identity,
                        orderIdentity.Item1,
                        orderIdentity.Item2,
                        "Trade payroll order belongs to another company.");
                }

                Validate(orderIdentity);
            }
        }

        private void ValidateCompanyReference(
            (string Collection, string ObjectId) source,
            Guid companyId)
        {
            var companyIdentity = (
                ProfileSyncCollections.TradeCompanyProfiles,
                companyId.ToString("D"));
            if (!TryLoad(companyIdentity, out TradeCompanyProfile? company))
            {
                AddMissingReference(
                    source,
                    companyIdentity,
                    ProfileHostMigrationBlockerCodes.MissingCompany,
                    "Trade object references a missing company.");
            }
            else if (company!.Id != companyId)
            {
                AddReferenceMismatch(
                    source,
                    companyIdentity.Item1,
                    companyIdentity.Item2,
                    "Referenced company payload does not preserve its identity.");
            }

            Validate(companyIdentity);
        }

        private void ValidateCrafterReference(
            (string Collection, string ObjectId) source,
            Guid crafterId,
            Guid companyId)
        {
            var crafterIdentity = (
                ProfileSyncCollections.TradeCrafters,
                crafterId.ToString("D"));
            if (!TryLoad(crafterIdentity, out TradeCrafterProfile? crafter))
            {
                AddMissingReference(
                    source,
                    crafterIdentity,
                    ProfileHostMigrationBlockerCodes.MissingCrafter,
                    "Trade object references a missing crafter.");
            }
            else if (crafter!.CompanyProfileId != companyId)
            {
                AddReferenceMismatch(
                    source,
                    crafterIdentity.Item1,
                    crafterIdentity.Item2,
                    "Referenced crafter belongs to another company.");
            }

            Validate(crafterIdentity);
        }

        private bool TryLoad<T>(
            (string Collection, string ObjectId) identity,
            out T? value)
            where T : class
        {
            if (!objects.TryGetValue(identity, out var payloadJson))
            {
                value = null;
                return false;
            }

            return TryDeserialize(identity, payloadJson, out value);
        }

        private bool TryDeserialize<T>(
            (string Collection, string ObjectId) identity,
            string payloadJson,
            out T? value)
            where T : class
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(
                    payloadJson,
                    MigrationJsonOptions);
                if (value != null)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }

            value = null;
            AddBlocker(
                blockers,
                ProfileHostMigrationBlockerCodes.InvalidPayload,
                $"Hosted migration payload is not a valid {typeof(T).Name}.",
                identity.Collection,
                identity.ObjectId);
            return false;
        }

        private void AddMissing(
            (string Collection, string ObjectId) identity,
            string code) =>
            AddBlocker(
                blockers,
                code,
                "A required Trade migration object is missing.",
                identity.Collection,
                identity.ObjectId);

        private void AddMissingReference(
            (string Collection, string ObjectId) source,
            (string Collection, string ObjectId) referenced,
            string code,
            string message) =>
            AddBlocker(
                blockers,
                code,
                message,
                source.Collection,
                source.ObjectId,
                referenced.Collection,
                referenced.ObjectId);

        private void AddIdentityMismatch(
            (string Collection, string ObjectId) identity,
            string message) =>
            AddBlocker(
                blockers,
                ProfileHostMigrationBlockerCodes.ObjectIdentityMismatch,
                message,
                identity.Collection,
                identity.ObjectId);

        private void AddReferenceMismatch(
            (string Collection, string ObjectId) source,
            string referencedCollection,
            string referencedObjectId,
            string message) =>
            AddBlocker(
                blockers,
                ProfileHostMigrationBlockerCodes.CompanyReferenceMismatch,
                message,
                source.Collection,
                source.ObjectId,
                referencedCollection,
                referencedObjectId);

        private static bool IsTradeCollection(string collection) =>
            collection is
                ProfileSyncCollections.TradeCompanyProfiles or
                ProfileSyncCollections.TradeCrafters or
                ProfileSyncCollections.TradeOrders or
                ProfileSyncCollections.TradePayrollDrafts;
    }

    private sealed class StringTupleComparer :
        IEqualityComparer<(string Collection, string ObjectId)>
    {
        public static StringTupleComparer Ordinal { get; } = new();

        public bool Equals(
            (string Collection, string ObjectId) x,
            (string Collection, string ObjectId) y) =>
            string.Equals(x.Collection, y.Collection, StringComparison.Ordinal) &&
            string.Equals(x.ObjectId, y.ObjectId, StringComparison.Ordinal);

        public int GetHashCode((string Collection, string ObjectId) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Collection),
                StringComparer.Ordinal.GetHashCode(obj.ObjectId));
    }
}
