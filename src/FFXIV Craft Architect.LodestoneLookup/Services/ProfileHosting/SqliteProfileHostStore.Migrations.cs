using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var normalizedRequest = NormalizeMigrationRequest(request);
        var response = await BuildMigrationPreflightAsync(
            connection,
            transaction,
            profileId,
            normalizedRequest,
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
        var preflightRequest = NormalizeMigrationRequest(new ProfileHostMigrationPreflightRequest
        {
            MigrationId = request.MigrationId,
            Objects = request.Objects ?? Array.Empty<ProfileHostMigrationObjectInput>(),
            Resolutions = request.Resolutions ?? Array.Empty<ProfileHostMigrationResolution>(),
            Mappings = request.Mappings ?? Array.Empty<ProfileHostMigrationCanonicalMapping>()
        });
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

        var mappingMap = preflight.Mappings.ToDictionary(
            item => (item.Collection, item.SourceObjectId),
            item => item.TargetObjectId,
            StringTupleComparer.Ordinal);
        var inputs = preflightRequest.Objects
            .Where(item => ProfileSyncCollections.All.Contains(item.Collection))
            .ToDictionary(
                MigrationIdentity,
                item =>
                {
                    var sourceIdentity = MigrationIdentity(item);
                    var targetId = mappingMap.GetValueOrDefault(
                        sourceIdentity,
                        sourceIdentity.ObjectId);
                    if (!TryRewriteMigrationInput(
                            item,
                            targetId,
                            mappingMap,
                            out var rewritten,
                            out var error))
                    {
                        throw new InvalidOperationException(error);
                    }

                    return rewritten;
                },
                StringTupleComparer.Ordinal);
        var authoritative = await LoadMigrationObjectsAsync(
            connection,
            transaction,
            profileId,
            ct);
        var committed = new List<ProfileHostMigrationAuthoritativeObject>(
            preflight.Objects.Count);
        var retiredSources = new List<ProfileHostMigrationRetiredSource>();
        var now = DateTime.UtcNow;
        foreach (var assessment in preflight.Objects
                     .OrderBy(item => item.Collection, StringComparer.Ordinal)
                     .ThenBy(item => item.ObjectId, StringComparer.Ordinal))
        {
            var sourceIdentity = (assessment.Collection, assessment.ObjectId);
            var identity = (assessment.Collection, assessment.CanonicalObjectId);
            var shouldWrite = ShouldWriteMigrationObject(assessment);
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
                    inputs[sourceIdentity],
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
                SourceObjectId = assessment.ObjectId,
                ObjectId = assessment.CanonicalObjectId,
                Revision = revision,
                Deleted = deleted
            });

            authoritative.TryGetValue(sourceIdentity, out var authoritativeSource);
            if (assessment.RetiresAuthoritativeSource)
            {
                if (authoritativeSource == null || authoritativeSource.Deleted)
                {
                    throw new InvalidOperationException(
                        "A migration source selected for retirement lost its live authoritative state.");
                }

                var retirementRevision = await ReserveNextRevisionAsync(
                    connection,
                    transaction,
                    profileId,
                    ct);
                await TombstoneMigratedSourceAsync(
                    connection,
                    transaction,
                    profileId,
                    authoritativeSource,
                    retirementRevision,
                    now,
                    ct);
                retiredSources.Add(new ProfileHostMigrationRetiredSource
                {
                    Collection = assessment.Collection,
                    ObjectId = assessment.ObjectId,
                    Revision = retirementRevision,
                    Deleted = true
                });
            }
            else if (mappingMap.ContainsKey(sourceIdentity) &&
                     assessment.Resolution !=
                        ProfileHostMigrationConflictResolution.KeepBothAsCopy &&
                     authoritativeSource is { Deleted: true })
            {
                retiredSources.Add(new ProfileHostMigrationRetiredSource
                {
                    Collection = assessment.Collection,
                    ObjectId = assessment.ObjectId,
                    Revision = authoritativeSource.Revision,
                    Deleted = true
                });
            }
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
            Objects = committed,
            Mappings = preflight.Mappings,
            RetiredSources = retiredSources
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
        var mappings = request.Mappings ?? Array.Empty<ProfileHostMigrationCanonicalMapping>();
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

            if (!Enum.IsDefined(
                    typeof(ProfileHostMigrationConflictResolution),
                    resolution.Resolution))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.InvalidResolution,
                    "The migration resolution value is not defined by this server contract.",
                    identity.Item1,
                    identity.Item2);
            }
        }

        var authoritative = await LoadMigrationObjectsAsync(
            connection,
            transaction,
            profileId,
            ct,
            blockers);
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
                    out _,
                    out _))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.InvalidPayload,
                    "The migration object payload is not valid JSON.",
                    collection,
                    objectId);
                continue;
            }
        }

        var mappingMap = new Dictionary<
            (string Collection, string ObjectId),
            string>(StringTupleComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            var collection = mapping?.Collection ?? string.Empty;
            var sourceObjectId = mapping?.SourceObjectId ?? string.Empty;
            var targetObjectId = mapping?.TargetObjectId ?? string.Empty;
            var sourceIdentity = (collection, sourceObjectId);
            if (!ProfileSyncCollections.All.Contains(collection) ||
                string.IsNullOrWhiteSpace(sourceObjectId) ||
                string.IsNullOrWhiteSpace(targetObjectId) ||
                string.Equals(sourceObjectId, targetObjectId, StringComparison.Ordinal))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.InvalidCanonicalMapping,
                    "A canonical mapping must name one supported collection and distinct non-empty source and target IDs.",
                    collection,
                    sourceObjectId,
                    collection,
                    targetObjectId);
                continue;
            }

            if (IsGuidIdentityCollection(collection))
            {
                if (!Guid.TryParse(sourceObjectId, out var sourceGuid) ||
                    sourceGuid == Guid.Empty ||
                    !Guid.TryParse(targetObjectId, out var targetGuid) ||
                    targetGuid == Guid.Empty)
                {
                    AddBlocker(
                        blockers,
                        ProfileHostMigrationBlockerCodes.InvalidCanonicalMapping,
                        "Trade company, crafter, and order mappings require non-empty GUID source and target IDs.",
                        collection,
                        sourceObjectId,
                        collection,
                        targetObjectId);
                    continue;
                }

                targetObjectId = targetGuid.ToString("D");
            }

            if (!inputs.ContainsKey(sourceIdentity))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.InvalidCanonicalMapping,
                    "A canonical mapping source does not identify an incoming object.",
                    collection,
                    sourceObjectId,
                    collection,
                    targetObjectId);
                continue;
            }

            if (!mappingMap.TryAdd(sourceIdentity, targetObjectId))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.InvalidCanonicalMapping,
                    "An incoming object has more than one canonical target.",
                    collection,
                    sourceObjectId,
                    collection,
                    targetObjectId);
            }
        }

        foreach (var mapping in mappingMap)
        {
            if (mappingMap.ContainsKey((mapping.Key.Collection, mapping.Value)))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.InvalidCanonicalMapping,
                    "Canonical mappings must be one hop; chains and cycles are not accepted.",
                    mapping.Key.Collection,
                    mapping.Key.ObjectId,
                    mapping.Key.Collection,
                    mapping.Value);
            }
        }

        foreach (var group in inputs.Keys.GroupBy(
                     identity => (
                         identity.Collection,
                         ObjectId: mappingMap.GetValueOrDefault(identity, identity.ObjectId)),
                     StringTupleComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            foreach (var source in group)
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.CanonicalTargetConflict,
                    "Multiple incoming objects resolve to the same canonical identity.",
                    source.Collection,
                    source.ObjectId,
                    group.Key.Collection,
                    group.Key.ObjectId);
            }
        }

        var canonicalInputs = new Dictionary<
            (string Collection, string ObjectId),
            ProfileHostMigrationObjectInput>(StringTupleComparer.Ordinal);
        foreach (var pair in inputs)
        {
            var targetObjectId = mappingMap.GetValueOrDefault(
                pair.Key,
                pair.Key.ObjectId);
            if (!TryRewriteMigrationInput(
                    pair.Value,
                    targetObjectId,
                    mappingMap,
                    out var rewritten,
                    out var rewriteError))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.InvalidPayload,
                    rewriteError,
                    pair.Key.Collection,
                    pair.Key.ObjectId);
                continue;
            }

            canonicalInputs[pair.Key] = rewritten;
        }

        foreach (var pair in canonicalInputs)
        {
            var sourceIdentity = pair.Key;
            var targetIdentity = (pair.Key.Collection, pair.Value.ObjectId);
            resolutionMap.TryGetValue(sourceIdentity, out var selectedResolution);
            ProfileHostMigrationConflictResolution? resolution =
                resolutionMap.ContainsKey(sourceIdentity) ? selectedResolution : null;
            var hasMapping = mappingMap.ContainsKey(sourceIdentity);
            var keepBoth =
                resolution == ProfileHostMigrationConflictResolution.KeepBothAsCopy;
            authoritative.TryGetValue(targetIdentity, out var targetCurrent);
            ProfileSyncObjectEnvelope? sourceCurrent = null;
            if (hasMapping &&
                authoritative.TryGetValue(sourceIdentity, out var existingSource))
            {
                sourceCurrent = existingSource;
            }

            if (sourceIdentity.Collection == ProfileSyncCollections.TradeOrders &&
                OrderPublicationIdentityWouldBeRemapped(
                    sourceIdentity,
                    inputs[sourceIdentity].PayloadJson,
                    mappingMap) &&
                (HasCommissionPublication(inputs[sourceIdentity].PayloadJson) ||
                 HasCommissionPublication(sourceCurrent?.PayloadJson) ||
                 HasCommissionPublication(targetCurrent?.PayloadJson)))
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.PublishedOrderRemapRequiresReissue,
                    "A published trade order cannot be remapped until its external publication is atomically revoked and reissued.",
                    sourceIdentity.Collection,
                    sourceIdentity.ObjectId,
                    targetIdentity.Collection,
                    targetIdentity.ObjectId);
            }

            var classifyCurrent = keepBoth ? sourceCurrent : targetCurrent;
            var payloadForClassification =
                keepBoth
                    ? inputs[sourceIdentity].PayloadJson ?? string.Empty
                    : pair.Value.PayloadJson ?? string.Empty;
            TryCanonicalizeJson(
                payloadForClassification,
                out var canonicalForClassification,
                out _);
            TryCanonicalizeJson(
                pair.Value.PayloadJson ?? string.Empty,
                out _,
                out var incomingHash);

            var disposition = ProfileHostMigrationObjectDisposition.Insert;
            string? targetHash = null;
            if (targetCurrent != null)
            {
                _ = CanonicalizeAuthoritativePayload(targetCurrent, out targetHash);
            }

            string? sourceHash = null;
            if (sourceCurrent != null)
            {
                _ = CanonicalizeAuthoritativePayload(sourceCurrent, out sourceHash);
            }

            if (classifyCurrent != null)
            {
                var classifyCanonical = CanonicalizeAuthoritativePayload(
                    classifyCurrent,
                    out _);
                disposition = classifyCurrent.Deleted
                    ? ProfileHostMigrationObjectDisposition.AuthoritativeTombstone
                    : string.Equals(
                            canonicalForClassification,
                            classifyCanonical,
                            StringComparison.Ordinal)
                        ? ProfileHostMigrationObjectDisposition.Identical
                        : ProfileHostMigrationObjectDisposition.SameIdDifferentContent;
            }

            if (resolution == ProfileHostMigrationConflictResolution.KeepBothAsCopy)
            {
                if (!mappingMap.ContainsKey(sourceIdentity))
                {
                    AddBlocker(
                        blockers,
                        ProfileHostMigrationBlockerCodes.InvalidCanonicalMapping,
                        "Keeping both versions requires an explicit fresh target ID.",
                        sourceIdentity.Collection,
                        sourceIdentity.ObjectId);
                }
                else if (authoritative.ContainsKey(targetIdentity))
                {
                    AddBlocker(
                        blockers,
                        ProfileHostMigrationBlockerCodes.CanonicalTargetConflict,
                        "The copy target already has authoritative state, including a deletion tombstone.",
                        sourceIdentity.Collection,
                        sourceIdentity.ObjectId,
                        targetIdentity.Collection,
                        targetIdentity.ObjectId);
                }

                if (disposition !=
                    ProfileHostMigrationObjectDisposition.SameIdDifferentContent)
                {
                    AddBlocker(
                        blockers,
                        ProfileHostMigrationBlockerCodes.UnexpectedResolution,
                        "Keeping both as copies is only valid for a live same-ID content conflict.",
                        sourceIdentity.Collection,
                        sourceIdentity.ObjectId);
                }
            }

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
                    sourceIdentity.Collection,
                    sourceIdentity.ObjectId);
            }
            else if (disposition ==
                         ProfileHostMigrationObjectDisposition.SameIdDifferentContent &&
                     resolution == ProfileHostMigrationConflictResolution.ResurrectIncoming)
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.UnexpectedResolution,
                    "Resurrection is only valid when the authoritative object is deleted.",
                    sourceIdentity.Collection,
                    sourceIdentity.ObjectId);
            }
            else if (disposition ==
                         ProfileHostMigrationObjectDisposition.AuthoritativeTombstone &&
                     resolution is
                         ProfileHostMigrationConflictResolution.UseIncoming or
                         ProfileHostMigrationConflictResolution.KeepBothAsCopy)
            {
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.UnexpectedResolution,
                    "A deleted authoritative object requires the explicit resurrection resolution.",
                    sourceIdentity.Collection,
                    sourceIdentity.ObjectId);
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
                    sourceIdentity.Collection,
                    sourceIdentity.ObjectId);
            }

            assessments.Add(new ProfileHostMigrationObjectAssessment
            {
                Collection = sourceIdentity.Collection,
                ObjectId = sourceIdentity.ObjectId,
                CanonicalObjectId = targetIdentity.ObjectId,
                Disposition = disposition,
                Resolution = resolution,
                AuthoritativeRevision = targetCurrent?.Revision,
                AuthoritativeDeleted = targetCurrent?.Deleted ?? false,
                AuthoritativeDeletedAtUtc = targetCurrent?.DeletedAtUtc,
                RetiresAuthoritativeSource =
                    hasMapping &&
                    !keepBoth &&
                    sourceCurrent is { Deleted: false },
                AuthoritativeSourceRevision = sourceCurrent?.Revision,
                AuthoritativeSourceDeleted = sourceCurrent?.Deleted ?? false,
                AuthoritativeSourceDeletedAtUtc = sourceCurrent?.DeletedAtUtc,
                AuthoritativeSourceContentHash = sourceHash,
                IncomingContentHash = incomingHash,
                AuthoritativeContentHash = targetHash
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
            var sourceIdentity = (assessment.Collection, assessment.ObjectId);
            var identity = (assessment.Collection, assessment.CanonicalObjectId);
            if (assessment.RetiresAuthoritativeSource)
            {
                finalObjects.Remove(sourceIdentity);
            }

            if (assessment.Disposition == ProfileHostMigrationObjectDisposition.Insert ||
                assessment.Resolution is
                    ProfileHostMigrationConflictResolution.UseIncoming or
                    ProfileHostMigrationConflictResolution.ResurrectIncoming or
                    ProfileHostMigrationConflictResolution.KeepBothAsCopy)
            {
                finalObjects[identity] =
                    canonicalInputs[sourceIdentity].PayloadJson ?? string.Empty;
            }
        }

        var validator = new MigrationGraphValidator(finalObjects, blockers);
        var identitiesToValidate = assessments.Any(item =>
            item.RetiresAuthoritativeSource)
            ? finalObjects.Keys
            : assessments.Select(item => (item.Collection, item.CanonicalObjectId));
        foreach (var identity in identitiesToValidate)
        {
            if (ProfileSyncCollections.All.Contains(identity.Collection) &&
                finalObjects.ContainsKey(identity))
            {
                validator.Validate(identity);
            }
        }
        validator.ValidatePayrollLinkage();

        var response = new ProfileHostMigrationPreflightResponse
        {
            MigrationId = request.MigrationId,
            RequestHash = requestHash,
            Objects = assessments
                .OrderBy(item => item.Collection, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                .ToArray(),
            Mappings = mappingMap
                .OrderBy(item => item.Key.Collection, StringComparer.Ordinal)
                .ThenBy(item => item.Key.ObjectId, StringComparer.Ordinal)
                .Select(item => new ProfileHostMigrationCanonicalMapping
                {
                    Collection = item.Key.Collection,
                    SourceObjectId = item.Key.ObjectId,
                    TargetObjectId = item.Value
                })
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

    private static bool ShouldWriteMigrationObject(
        ProfileHostMigrationObjectAssessment assessment) =>
        (assessment.Disposition, assessment.Resolution) switch
        {
            (ProfileHostMigrationObjectDisposition.Insert, null) => true,
            (ProfileHostMigrationObjectDisposition.Identical, null) => false,
            (
                ProfileHostMigrationObjectDisposition.SameIdDifferentContent,
                ProfileHostMigrationConflictResolution.KeepAuthoritative) => false,
            (
                ProfileHostMigrationObjectDisposition.SameIdDifferentContent,
                ProfileHostMigrationConflictResolution.UseIncoming) => true,
            (
                ProfileHostMigrationObjectDisposition.SameIdDifferentContent,
                ProfileHostMigrationConflictResolution.KeepBothAsCopy) => true,
            (
                ProfileHostMigrationObjectDisposition.AuthoritativeTombstone,
                ProfileHostMigrationConflictResolution.KeepAuthoritative) => false,
            (
                ProfileHostMigrationObjectDisposition.AuthoritativeTombstone,
                ProfileHostMigrationConflictResolution.ResurrectIncoming) => true,
            _ => throw new InvalidOperationException(
                $"Hosted migration assessment '{assessment.Collection}/{assessment.ObjectId}' " +
                "has no valid commit behavior.")
        };

    private static async Task<Dictionary<
        (string Collection, string ObjectId),
        ProfileSyncObjectEnvelope>> LoadMigrationObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        CancellationToken ct,
        ICollection<ProfileHostMigrationBlocker>? blockers = null)
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
            var identity = (
                item.Collection,
                NormalizeMigrationObjectId(item.Collection, item.ObjectId));
            if (!result.TryAdd(identity, item))
            {
                if (blockers == null)
                {
                    throw new InvalidOperationException(
                        $"Authoritative migration identity '{identity.Collection}/{identity.ObjectId}' has aliases.");
                }

                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.DuplicateAuthoritativeIdentity,
                    "Authoritative hosted state contains more than one spelling of the same logical GUID identity.",
                    identity.Collection,
                    identity.ObjectId);
            }
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

    private static async Task TombstoneMigratedSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        ProfileSyncObjectEnvelope source,
        long revision,
        DateTime deletedAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update sync_objects
            set revision = $revision,
                updated_at_utc = $deletedAtUtc,
                deleted = 1,
                deleted_at_utc = $deletedAtUtc
            where profile_id = $profileId
              and collection = $collection
              and object_id = $objectId
              and revision = $expectedRevision
              and deleted = 0;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", source.Collection);
        command.Parameters.AddWithValue("$objectId", source.ObjectId);
        command.Parameters.AddWithValue("$expectedRevision", source.Revision);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$deletedAtUtc", deletedAt.ToString("O"));
        var changed = await command.ExecuteNonQueryAsync(ct);
        if (changed != 1)
        {
            throw new DBConcurrencyException(
                "The authoritative migration source changed before atomic retirement.");
        }
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

    private static bool TryRewriteMigrationInput(
        ProfileHostMigrationObjectInput input,
        string targetObjectId,
        IReadOnlyDictionary<(string Collection, string ObjectId), string> mappings,
        out ProfileHostMigrationObjectInput rewritten,
        out string error)
    {
        rewritten = new ProfileHostMigrationObjectInput
        {
            Collection = input.Collection,
            ObjectId = targetObjectId,
            PayloadJson = input.PayloadJson
        };
        error = string.Empty;
        var identityChanged = !string.Equals(
            input.ObjectId,
            targetObjectId,
            StringComparison.Ordinal);
        var mayContainMappedReferences =
            mappings.Count > 0 &&
            input.Collection is
                ProfileSyncCollections.TradeCrafters or
                ProfileSyncCollections.TradeOrders or
                ProfileSyncCollections.TradePayrollDrafts or
                ProfileSyncCollections.Plans;
        if (!identityChanged && !mayContainMappedReferences)
        {
            return true;
        }

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(input.PayloadJson) as JsonObject;
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload == null)
        {
            error = "The migration payload must be a JSON object before identity remapping.";
            return false;
        }

        switch (input.Collection)
        {
            case ProfileSyncCollections.TradeCompanyProfiles:
                if (!string.Equals(
                        input.ObjectId,
                        targetObjectId,
                        StringComparison.Ordinal) &&
                    !TryWriteGuidIdentity(payload, "id", targetObjectId, out error))
                {
                    return false;
                }

                break;
            case ProfileSyncCollections.TradeCrafters:
                if (!string.Equals(
                        input.ObjectId,
                        targetObjectId,
                        StringComparison.Ordinal) &&
                    !TryWriteGuidIdentity(payload, "id", targetObjectId, out error))
                {
                    return false;
                }

                if (
                    !TryRewriteGuidReference(
                        payload,
                        "companyProfileId",
                        ProfileSyncCollections.TradeCompanyProfiles,
                        mappings,
                        out error))
                {
                    return false;
                }

                break;
            case ProfileSyncCollections.TradeOrders:
                if (!string.Equals(
                        input.ObjectId,
                        targetObjectId,
                        StringComparison.Ordinal) &&
                    !TryWriteGuidIdentity(payload, "id", targetObjectId, out error))
                {
                    return false;
                }

                if (!TryRewriteOrderPayload(payload, mappings, out error))
                {
                    return false;
                }

                break;
            case ProfileSyncCollections.TradePayrollDrafts:
                if (!string.Equals(
                        input.ObjectId,
                        targetObjectId,
                        StringComparison.Ordinal) &&
                    !TryWriteStringIdentity(payload, "id", targetObjectId, out error))
                {
                    return false;
                }

                if (!TryRewriteGuidReference(
                        payload,
                        "companyProfileId",
                        ProfileSyncCollections.TradeCompanyProfiles,
                        mappings,
                        out error) ||
                    !TryRewriteGuidReference(
                        payload,
                        "orderId",
                        ProfileSyncCollections.TradeOrders,
                        mappings,
                        out error) ||
                    !TryRewriteGuidReference(
                        payload,
                        "assignedCrafterId",
                        ProfileSyncCollections.TradeCrafters,
                        mappings,
                        out error))
                {
                    return false;
                }

                break;
            case ProfileSyncCollections.Plans:
                if (!string.Equals(
                        input.ObjectId,
                        targetObjectId,
                        StringComparison.Ordinal) &&
                    !TryWriteStringIdentity(payload, "id", targetObjectId, out error))
                {
                    return false;
                }

                if (!TryRewriteStringReference(
                        payload,
                        "sourcePlanId",
                        ProfileSyncCollections.Plans,
                        mappings,
                        out error) ||
                    !TryRewriteGuidReference(
                        payload,
                        "companyProfileId",
                        ProfileSyncCollections.TradeCompanyProfiles,
                        mappings,
                        out error))
                {
                    return false;
                }

                break;
        }

        rewritten.PayloadJson = payload.ToJsonString(MigrationJsonOptions);
        return true;
    }

    private static bool TryRewriteOrderPayload(
        JsonObject payload,
        IReadOnlyDictionary<(string Collection, string ObjectId), string> mappings,
        out string error)
    {
        if (!TryRewriteGuidReference(
                payload,
                "companyProfileId",
                ProfileSyncCollections.TradeCompanyProfiles,
                mappings,
                out error) ||
            !TryRewriteGuidReference(
                payload,
                "assignedCrafterId",
                ProfileSyncCollections.TradeCrafters,
                mappings,
                out error) ||
            !TryRewriteStringReference(
                payload,
                "payrollDraftId",
                ProfileSyncCollections.TradePayrollDrafts,
                mappings,
                out error) ||
            !TryRewriteStringReference(
                payload,
                "craftPlanId",
                ProfileSyncCollections.Plans,
                mappings,
                out error))
        {
            return false;
        }

        if (TryGetProperty(payload, "sourceSnapshot", out _, out var sourceNode) &&
            sourceNode is JsonObject sourceSnapshot &&
            !TryRewriteStringReference(
                sourceSnapshot,
                "sourcePlanId",
                ProfileSyncCollections.Plans,
                mappings,
                out error))
        {
            return false;
        }

        if (TryGetProperty(payload, "history", out _, out var historyNode) &&
            historyNode is JsonArray history)
        {
            foreach (var eventNode in history)
            {
                if (eventNode is not JsonObject historyEvent)
                {
                    error = "Trade order history must contain JSON objects.";
                    return false;
                }

                if (!TryRewriteGuidReference(
                        historyEvent,
                        "companyProfileId",
                        ProfileSyncCollections.TradeCompanyProfiles,
                        mappings,
                        out error) ||
                    !TryRewriteGuidReference(
                        historyEvent,
                        "orderId",
                        ProfileSyncCollections.TradeOrders,
                        mappings,
                        out error) ||
                    !TryRewriteGuidReference(
                        historyEvent,
                        "crafterId",
                        ProfileSyncCollections.TradeCrafters,
                        mappings,
                        out error))
                {
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool OrderPublicationIdentityWouldBeRemapped(
        (string Collection, string ObjectId) orderIdentity,
        string payloadJson,
        IReadOnlyDictionary<(string Collection, string ObjectId), string> mappings)
    {
        if (mappings.ContainsKey(orderIdentity))
        {
            return true;
        }

        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject payload)
            {
                return false;
            }

            if (JsonPropertyHasMapping(
                    payload,
                    "companyProfileId",
                    ProfileSyncCollections.TradeCompanyProfiles,
                    mappings))
            {
                return true;
            }

            return TryGetProperty(
                       payload,
                       "commissionPublication",
                       out _,
                       out var publicationNode) &&
                   publicationNode is JsonObject publication &&
                   TryGetProperty(
                       publication,
                       "ownership",
                       out _,
                       out var ownershipNode) &&
                   ownershipNode is JsonObject ownership &&
                   (JsonPropertyHasMapping(
                        ownership,
                        "companyId",
                        ProfileSyncCollections.TradeCompanyProfiles,
                        mappings) ||
                    JsonPropertyHasMapping(
                        ownership,
                        "orderId",
                        ProfileSyncCollections.TradeOrders,
                        mappings));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonPropertyHasMapping(
        JsonObject payload,
        string propertyName,
        string collection,
        IReadOnlyDictionary<(string Collection, string ObjectId), string> mappings)
    {
        return TryGetProperty(payload, propertyName, out _, out var node) &&
               node != null &&
               TryReadString(node, out var objectId) &&
               TryGetMappedId(mappings, collection, objectId, out _);
    }

    private static bool HasCommissionPublication(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(payloadJson) is JsonObject payload &&
                   TryGetProperty(
                       payload,
                       "commissionPublication",
                       out _,
                       out var publication) &&
                   publication != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryWriteGuidIdentity(
        JsonObject payload,
        string propertyName,
        string targetObjectId,
        out string error)
    {
        if (!Guid.TryParse(targetObjectId, out var parsed) || parsed == Guid.Empty)
        {
            error = $"Canonical target '{targetObjectId}' must be a non-empty GUID.";
            return false;
        }

        if (!TryGetProperty(payload, propertyName, out var actualName, out _))
        {
            error = $"Property '{propertyName}' is required for identity remapping.";
            return false;
        }

        payload[actualName] = parsed.ToString("D");
        error = string.Empty;
        return true;
    }

    private static bool TryWriteStringIdentity(
        JsonObject payload,
        string propertyName,
        string targetObjectId,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(targetObjectId))
        {
            error = "Canonical target IDs cannot be empty.";
            return false;
        }

        if (!TryGetProperty(payload, propertyName, out var actualName, out _))
        {
            error = $"Property '{propertyName}' is required for identity remapping.";
            return false;
        }

        payload[actualName] = targetObjectId;
        error = string.Empty;
        return true;
    }

    private static bool TryRewriteGuidReference(
        JsonObject payload,
        string propertyName,
        string collection,
        IReadOnlyDictionary<(string Collection, string ObjectId), string> mappings,
        out string error)
    {
        if (!TryGetProperty(payload, propertyName, out var actualName, out var node) ||
            node == null)
        {
            error = string.Empty;
            return true;
        }

        if (!TryReadString(node, out var sourceId) ||
            !Guid.TryParse(sourceId, out var parsed) ||
            parsed == Guid.Empty)
        {
            error = $"Property '{propertyName}' must contain a non-empty GUID.";
            return false;
        }

        if (!TryGetMappedId(mappings, collection, sourceId, out var canonical) &&
            !TryGetMappedId(
                mappings,
                collection,
                parsed.ToString("D"),
                out canonical))
        {
            error = string.Empty;
            return true;
        }

        if (!Guid.TryParse(canonical, out var canonicalGuid) || canonicalGuid == Guid.Empty)
        {
            error = $"Canonical reference '{canonical}' for '{propertyName}' must be a non-empty GUID.";
            return false;
        }

        payload[actualName] = canonicalGuid.ToString("D");
        error = string.Empty;
        return true;
    }

    private static bool TryRewriteStringReference(
        JsonObject payload,
        string propertyName,
        string collection,
        IReadOnlyDictionary<(string Collection, string ObjectId), string> mappings,
        out string error)
    {
        if (!TryGetProperty(payload, propertyName, out var actualName, out var node) ||
            node == null)
        {
            error = string.Empty;
            return true;
        }

        if (!TryReadString(node, out var sourceId) || string.IsNullOrWhiteSpace(sourceId))
        {
            error = $"Property '{propertyName}' must contain a non-empty string ID.";
            return false;
        }

        if (TryGetMappedId(mappings, collection, sourceId, out var canonical))
        {
            payload[actualName] = canonical;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetMappedId(
        IReadOnlyDictionary<(string Collection, string ObjectId), string> mappings,
        string collection,
        string sourceObjectId,
        out string targetObjectId)
    {
        if (mappings.TryGetValue(
                (collection, sourceObjectId),
                out targetObjectId!))
        {
            return true;
        }

        if (!Guid.TryParse(sourceObjectId, out var sourceGuid))
        {
            return false;
        }

        foreach (var mapping in mappings)
        {
            if (string.Equals(
                    mapping.Key.Collection,
                    collection,
                    StringComparison.Ordinal) &&
                Guid.TryParse(mapping.Key.ObjectId, out var mappedSourceGuid) &&
                mappedSourceGuid == sourceGuid)
            {
                targetObjectId = mapping.Value;
                return true;
            }
        }

        targetObjectId = string.Empty;
        return false;
    }

    private static bool IsGuidIdentityCollection(string collection) =>
        collection is
            ProfileSyncCollections.TradeCompanyProfiles or
            ProfileSyncCollections.TradeCrafters or
            ProfileSyncCollections.TradeOrders;

    private static bool TryGetProperty(
        JsonObject payload,
        string propertyName,
        out string actualName,
        out JsonNode? value)
    {
        foreach (var property in payload)
        {
            if (string.Equals(
                    property.Key,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                actualName = property.Key;
                value = property.Value;
                return true;
            }
        }

        actualName = propertyName;
        value = null;
        return false;
    }

    private static bool TryReadString(JsonNode node, out string value)
    {
        try
        {
            value = node.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static string ComputeRequestHash(ProfileHostMigrationPreflightRequest request)
    {
        request = NormalizeMigrationRequest(request);
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

        foreach (var mapping in (request.Mappings ??
                                 Array.Empty<ProfileHostMigrationCanonicalMapping>())
                     .OrderBy(item => item?.Collection, StringComparer.Ordinal)
                     .ThenBy(item => item?.SourceObjectId, StringComparer.Ordinal)
                     .ThenBy(item => item?.TargetObjectId, StringComparer.Ordinal))
        {
            AppendHashPart(builder, mapping?.Collection ?? string.Empty);
            AppendHashPart(builder, mapping?.SourceObjectId ?? string.Empty);
            AppendHashPart(builder, mapping?.TargetObjectId ?? string.Empty);
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
            AppendHashPart(builder, item.CanonicalObjectId);
            AppendHashPart(builder, ((int)item.Disposition).ToString());
            AppendHashPart(builder, item.Resolution.HasValue
                ? ((int)item.Resolution.Value).ToString()
                : string.Empty);
            AppendHashPart(builder, item.AuthoritativeRevision?.ToString() ?? string.Empty);
            AppendHashPart(builder, item.AuthoritativeDeleted ? "1" : "0");
            AppendHashPart(
                builder,
                item.AuthoritativeDeletedAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty);
            AppendHashPart(builder, item.RetiresAuthoritativeSource ? "1" : "0");
            AppendHashPart(
                builder,
                item.AuthoritativeSourceRevision?.ToString() ?? string.Empty);
            AppendHashPart(builder, item.AuthoritativeSourceDeleted ? "1" : "0");
            AppendHashPart(
                builder,
                item.AuthoritativeSourceDeletedAtUtc?.ToUniversalTime().ToString("O") ??
                string.Empty);
            AppendHashPart(
                builder,
                item.AuthoritativeSourceContentHash ?? string.Empty);
            AppendHashPart(builder, item.IncomingContentHash);
            AppendHashPart(builder, item.AuthoritativeContentHash ?? string.Empty);
        }

        foreach (var mapping in response.Mappings)
        {
            AppendHashPart(builder, mapping.Collection);
            AppendHashPart(builder, mapping.SourceObjectId);
            AppendHashPart(builder, mapping.TargetObjectId);
        }

        foreach (var blocker in response.Blockers)
        {
            AppendHashPart(builder, blocker.Code);
            AppendHashPart(builder, blocker.Collection ?? string.Empty);
            AppendHashPart(builder, blocker.ObjectId ?? string.Empty);
            AppendHashPart(builder, blocker.ReferencedCollection ?? string.Empty);
            AppendHashPart(builder, blocker.ReferencedObjectId ?? string.Empty);
        }

        foreach (var source in response.RetiredSources)
        {
            AppendHashPart(builder, source.Collection);
            AppendHashPart(builder, source.ObjectId);
            AppendHashPart(builder, source.Revision.ToString());
            AppendHashPart(builder, source.Deleted ? "1" : "0");
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
            AppendHashPart(builder, item.SourceObjectId);
            AppendHashPart(builder, item.ObjectId);
            AppendHashPart(builder, item.Revision.ToString());
            AppendHashPart(builder, item.Deleted ? "1" : "0");
        }

        foreach (var mapping in response.Mappings)
        {
            AppendHashPart(builder, mapping.Collection);
            AppendHashPart(builder, mapping.SourceObjectId);
            AppendHashPart(builder, mapping.TargetObjectId);
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

    private static string CanonicalizeAuthoritativePayload(
        ProfileSyncObjectEnvelope current,
        out string contentHash)
    {
        if (TryCanonicalizeJson(
                current.PayloadJson,
                out var canonical,
                out contentHash))
        {
            return canonical;
        }

        canonical = current.PayloadJson;
        contentHash = HashText(canonical);
        return canonical;
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

    private static ProfileHostMigrationPreflightRequest NormalizeMigrationRequest(
        ProfileHostMigrationPreflightRequest request) =>
        new()
        {
            MigrationId = request.MigrationId,
            Objects = (request.Objects ?? Array.Empty<ProfileHostMigrationObjectInput>())
                .Select(item => item == null
                    ? new ProfileHostMigrationObjectInput()
                    : new ProfileHostMigrationObjectInput
                    {
                        Collection = item.Collection ?? string.Empty,
                        ObjectId = NormalizeMigrationObjectId(
                            item.Collection ?? string.Empty,
                            item.ObjectId ?? string.Empty),
                        PayloadJson = item.PayloadJson ?? string.Empty
                    })
                .ToArray(),
            Resolutions = (request.Resolutions ??
                           Array.Empty<ProfileHostMigrationResolution>())
                .Select(item => item == null
                    ? new ProfileHostMigrationResolution()
                    : new ProfileHostMigrationResolution
                    {
                        Collection = item.Collection ?? string.Empty,
                        ObjectId = NormalizeMigrationObjectId(
                            item.Collection ?? string.Empty,
                            item.ObjectId ?? string.Empty),
                        Resolution = item.Resolution
                    })
                .ToArray(),
            Mappings = (request.Mappings ??
                        Array.Empty<ProfileHostMigrationCanonicalMapping>())
                .Select(item => item == null
                    ? new ProfileHostMigrationCanonicalMapping()
                    : new ProfileHostMigrationCanonicalMapping
                    {
                        Collection = item.Collection ?? string.Empty,
                        SourceObjectId = NormalizeMigrationObjectId(
                            item.Collection ?? string.Empty,
                            item.SourceObjectId ?? string.Empty),
                        TargetObjectId = NormalizeMigrationObjectId(
                            item.Collection ?? string.Empty,
                            item.TargetObjectId ?? string.Empty)
                    })
                .ToArray()
        };

    private static string NormalizeMigrationObjectId(
        string collection,
        string objectId) =>
        IsGuidIdentityCollection(collection) &&
        Guid.TryParse(objectId, out var parsed) &&
        parsed != Guid.Empty
            ? parsed.ToString("D")
            : objectId;

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
            Mappings = response.Mappings,
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
                !IsValidatedCollection(identity.Collection))
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
                    ProfileSyncCollections.TradePayrollDrafts =>
                        ProfileHostMigrationBlockerCodes.MissingPayrollDraft,
                    _ => ProfileHostMigrationBlockerCodes.MissingPlan
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
                case ProfileSyncCollections.Plans:
                    ValidatePlan(identity, payloadJson);
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
                if (objects.ContainsKey(payrollIdentity))
                {
                    Validate(payrollIdentity);
                }
            }

            if (!string.IsNullOrWhiteSpace(order.CraftPlanId))
            {
                var planIdentity = (
                    ProfileSyncCollections.Plans,
                    order.CraftPlanId);
                if (!objects.TryGetValue(planIdentity, out var planPayload))
                {
                    AddMissingReference(
                        identity,
                        planIdentity,
                        ProfileHostMigrationBlockerCodes.MissingPlan,
                        "Trade order references a missing craft plan.");
                }
                else if (TryReadPlanCompanyId(
                             planIdentity,
                             planPayload,
                             out var planCompanyId) &&
                         planCompanyId.HasValue &&
                         planCompanyId.Value != order.CompanyProfileId)
                {
                    AddReferenceMismatch(
                        identity,
                        planIdentity.Item1,
                        planIdentity.Item2,
                        "Trade order craft plan belongs to another company.",
                        ProfileHostMigrationBlockerCodes.PlanReferenceMismatch);
                }

                Validate(planIdentity);
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
                if (objects.ContainsKey(orderIdentity))
                {
                    Validate(orderIdentity);
                }
            }
        }

        public void ValidatePayrollLinkage()
        {
            var orders = new Dictionary<Guid, (
                (string Collection, string ObjectId) Identity,
                TradeOrder Order)>();
            var payrolls = new Dictionary<string, (
                (string Collection, string ObjectId) Identity,
                TradePayrollWorkflowDraft Payroll)>(StringComparer.Ordinal);
            foreach (var pair in objects)
            {
                try
                {
                    if (pair.Key.Collection == ProfileSyncCollections.TradeOrders)
                    {
                        var order = JsonSerializer.Deserialize<TradeOrder>(
                            pair.Value,
                            MigrationJsonOptions);
                        if (order != null &&
                            Guid.TryParse(pair.Key.ObjectId, out var orderObjectId))
                        {
                            orders[orderObjectId] = (pair.Key, order);
                        }
                    }
                    else if (pair.Key.Collection ==
                             ProfileSyncCollections.TradePayrollDrafts)
                    {
                        var payroll = JsonSerializer.Deserialize<TradePayrollWorkflowDraft>(
                            pair.Value,
                            MigrationJsonOptions);
                        if (payroll != null)
                        {
                            payrolls[pair.Key.ObjectId] = (pair.Key, payroll);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Payload diagnostics belong to the ordinary typed validation path.
                }
            }

            var payrollByOrder = new Dictionary<Guid, string>();
            foreach (var payrollEntry in payrolls.Values)
            {
                var payroll = payrollEntry.Payroll;
                if (payroll.OrderId is not { } orderId)
                {
                    continue;
                }

                if (payrollByOrder.TryGetValue(orderId, out var existingPayrollId) &&
                    !string.Equals(
                        existingPayrollId,
                        payrollEntry.Identity.ObjectId,
                        StringComparison.Ordinal))
                {
                    AddBlocker(
                        blockers,
                        ProfileHostMigrationBlockerCodes.DuplicatePayrollLink,
                        "More than one payroll draft links to the same trade order.",
                        payrollEntry.Identity.Collection,
                        payrollEntry.Identity.ObjectId,
                        ProfileSyncCollections.TradePayrollDrafts,
                        existingPayrollId);
                }
                else
                {
                    payrollByOrder[orderId] = payrollEntry.Identity.ObjectId;
                }

                if (!orders.TryGetValue(orderId, out var orderEntry))
                {
                    AddMissingReference(
                        payrollEntry.Identity,
                        (ProfileSyncCollections.TradeOrders, orderId.ToString("D")),
                        ProfileHostMigrationBlockerCodes.MissingOrder,
                        "Trade payroll draft references a missing order.");
                    continue;
                }

                if (orderEntry.Order.CompanyProfileId != payroll.CompanyProfileId ||
                    !string.Equals(
                        orderEntry.Order.PayrollDraftId,
                        payrollEntry.Identity.ObjectId,
                        StringComparison.Ordinal))
                {
                    AddReferenceMismatch(
                        payrollEntry.Identity,
                        orderEntry.Identity.Collection,
                        orderEntry.Identity.ObjectId,
                        "Trade payroll and order links are not reciprocal or cross company boundaries.",
                        ProfileHostMigrationBlockerCodes.OrderReferenceMismatch);
                }
            }

            var orderByPayroll = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var orderEntry in orders.Values)
            {
                var order = orderEntry.Order;
                if (string.IsNullOrWhiteSpace(order.PayrollDraftId))
                {
                    continue;
                }

                var orderObjectId = Guid.Parse(orderEntry.Identity.ObjectId);
                if (orderByPayroll.TryGetValue(
                        order.PayrollDraftId,
                        out var existingOrderId) &&
                    existingOrderId != orderObjectId)
                {
                    AddBlocker(
                        blockers,
                        ProfileHostMigrationBlockerCodes.DuplicatePayrollLink,
                        "More than one trade order references the same payroll draft.",
                        orderEntry.Identity.Collection,
                        orderEntry.Identity.ObjectId,
                        ProfileSyncCollections.TradeOrders,
                        existingOrderId.ToString("D"));
                }
                else
                {
                    orderByPayroll[order.PayrollDraftId] = orderObjectId;
                }

                if (!payrolls.TryGetValue(order.PayrollDraftId, out var payrollEntry))
                {
                    AddMissingReference(
                        orderEntry.Identity,
                        (
                            ProfileSyncCollections.TradePayrollDrafts,
                            order.PayrollDraftId),
                        ProfileHostMigrationBlockerCodes.MissingPayrollDraft,
                        "Trade order references a missing payroll draft.");
                    continue;
                }

                if (payrollEntry.Payroll.CompanyProfileId != order.CompanyProfileId ||
                    payrollEntry.Payroll.OrderId != orderObjectId)
                {
                    AddReferenceMismatch(
                        orderEntry.Identity,
                        payrollEntry.Identity.Collection,
                        payrollEntry.Identity.ObjectId,
                        "Trade order and payroll links are not reciprocal or cross company boundaries.",
                        ProfileHostMigrationBlockerCodes.OrderReferenceMismatch);
                }
            }
        }

        private void ValidatePlan(
            (string Collection, string ObjectId) identity,
            string payloadJson)
        {
            if (!TryReadPlanObject(identity, payloadJson, out var plan))
            {
                return;
            }

            if (!TryGetProperty(plan!, "id", out _, out var idNode) ||
                idNode == null ||
                !TryReadString(idNode, out var planId) ||
                !string.Equals(planId, identity.ObjectId, StringComparison.Ordinal))
            {
                AddIdentityMismatch(identity, "Craft plan payload ID does not match ObjectId.");
            }

            if (TryReadPlanCompanyId(identity, plan!, out var companyId) &&
                companyId.HasValue)
            {
                ValidateCompanyReference(identity, companyId.Value);
            }
        }

        private bool TryReadPlanCompanyId(
            (string Collection, string ObjectId) identity,
            string payloadJson,
            out Guid? companyId)
        {
            if (!TryReadPlanObject(identity, payloadJson, out var plan))
            {
                companyId = null;
                return false;
            }

            return TryReadPlanCompanyId(identity, plan!, out companyId);
        }

        private bool TryReadPlanCompanyId(
            (string Collection, string ObjectId) identity,
            JsonObject plan,
            out Guid? companyId)
        {
            if (!TryGetProperty(
                    plan,
                    "companyProfileId",
                    out _,
                    out var companyNode) ||
                companyNode == null)
            {
                companyId = null;
                return true;
            }

            if (!TryReadString(companyNode, out var companyIdText) ||
                !Guid.TryParse(companyIdText, out var parsedCompanyId) ||
                parsedCompanyId == Guid.Empty)
            {
                companyId = null;
                AddBlocker(
                    blockers,
                    ProfileHostMigrationBlockerCodes.PlanReferenceMismatch,
                    "Craft plan company ownership must be a non-empty GUID when encoded.",
                    identity.Collection,
                    identity.ObjectId);
                return false;
            }

            companyId = parsedCompanyId;
            return true;
        }

        private bool TryReadPlanObject(
            (string Collection, string ObjectId) identity,
            string payloadJson,
            out JsonObject? plan)
        {
            try
            {
                plan = JsonNode.Parse(payloadJson) as JsonObject;
            }
            catch (JsonException)
            {
                plan = null;
            }

            if (plan != null)
            {
                return true;
            }

            AddBlocker(
                blockers,
                ProfileHostMigrationBlockerCodes.InvalidPayload,
                "Hosted migration payload is not a valid craft plan object.",
                identity.Collection,
                identity.ObjectId);
            return false;
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
            string message,
            string code = ProfileHostMigrationBlockerCodes.CompanyReferenceMismatch) =>
            AddBlocker(
                blockers,
                code,
                message,
                source.Collection,
                source.ObjectId,
                referencedCollection,
                referencedObjectId);

        private static bool IsValidatedCollection(string collection) =>
            collection is
                ProfileSyncCollections.TradeCompanyProfiles or
                ProfileSyncCollections.TradeCrafters or
                ProfileSyncCollections.TradeOrders or
                ProfileSyncCollections.TradePayrollDrafts or
                ProfileSyncCollections.Plans;
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
