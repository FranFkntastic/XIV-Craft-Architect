using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCompanyClientOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITradeCompanyClient _client;
    private readonly Dictionary<(string Kind, string Id), TradeCompanyRecordEnvelope> _records = [];
    private readonly Dictionary<(string Kind, string Id), TradeCompanyPendingMutation> _pending = [];
    private readonly Dictionary<(string Kind, string Id), TradeCompanyRecordConflict> _conflicts = [];
    private CompanyId? _companyId;
    private CompanyRevision _companyRevision = CompanyRevision.None;

    public TradeCompanyClientOrchestrator(ITradeCompanyClient client)
    {
        _client = client;
    }

    public event Action? StateChanged;

    public TradeCompanyConnectionSnapshot Connection { get; private set; } =
        TradeCompanyConnectionSnapshot.LocalOnly();

    public IReadOnlyCollection<TradeCompanyPendingMutation> PendingMutations => _pending.Values;

    public IReadOnlyCollection<TradeCompanyRecordConflict> Conflicts => _conflicts.Values;

    public async Task<TradeCompanyRefreshResult> RefreshAsync(
        TradeCompanyProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveCompanyId(profile, out var companyId))
        {
            ResetLocalOnly();
            return new TradeCompanyRefreshResult(Connection, []);
        }

        if (_companyId != companyId)
        {
            _companyId = companyId;
            _companyRevision = CompanyRevision.None;
            _records.Clear();
            _pending.Clear();
            _conflicts.Clear();
        }

        SetConnection(
            TradeCompanyConnectionState.Refreshing,
            "Refreshing company changes.");

        try
        {
            var company = await _client.GetCompanyAsync(companyId, cancellationToken);
            if (company == null)
            {
                SetConnection(
                    TradeCompanyConnectionState.Unavailable,
                    "The connected Trade Company is unavailable.");
                return new TradeCompanyRefreshResult(Connection, []);
            }

            var changes = await _client.GetChangesAsync(
                companyId,
                _companyRevision,
                cancellationToken);
            foreach (var record in changes.Records)
            {
                ApplyRecord(record);
            }

            _companyRevision = Max(company.Revision, changes.CompanyRevision);
            SetConnection(
                ResolveState(),
                ResolveMessage());
            return new TradeCompanyRefreshResult(Connection, changes.Records);
        }
        catch (Exception ex)
        {
            SetConnection(
                _pending.Count > 0
                    ? TradeCompanyConnectionState.Pending
                    : TradeCompanyConnectionState.Unavailable,
                $"Company refresh failed: {ex.Message}");
            return new TradeCompanyRefreshResult(Connection, []);
        }
    }

    public bool CanPerformExternalAction(Guid orderId, out string reason)
    {
        if (_companyId == null)
        {
            reason = "Connect this browser to the canonical Trade Company first.";
            return false;
        }

        if (Connection.State != TradeCompanyConnectionState.Current)
        {
            reason = Connection.Message ?? "Refresh company state before continuing.";
            return false;
        }

        var key = (TradeCompanyRecordKinds.Order, orderId.ToString("D"));
        if (!_records.TryGetValue(key, out var record) || record.Deleted)
        {
            reason = "Sync this order to the company before continuing.";
            return false;
        }

        if (_pending.ContainsKey(key) || _conflicts.ContainsKey(key))
        {
            reason = "Resolve the pending order update before continuing.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public TradeCompanyPublicationOwnership? GetPublicationOwnership(Guid orderId)
    {
        if (_companyId == null ||
            !_records.TryGetValue(
                (TradeCompanyRecordKinds.Order, orderId.ToString("D")),
                out var record) ||
            record.Deleted)
        {
            return null;
        }

        return new TradeCompanyPublicationOwnership(
            _companyId.Value,
            orderId,
            record.RecordRevision);
    }

    public IReadOnlyList<TradeCompanyRecordEnvelope> GetRecords(string recordKind) =>
        _records.Values
            .Where(record =>
                !record.Deleted &&
                string.Equals(record.RecordKind, recordKind, StringComparison.Ordinal))
            .OrderBy(record => record.CompanyRevision.Value)
            .ToArray();

    public async Task<TradeCompanyWebMutationResult> MutateAsync<TPayload>(
        string recordKind,
        string recordId,
        TPayload payload,
        bool requiresCurrentCompany,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (_companyId == null)
        {
            return new TradeCompanyWebMutationResult(
                requiresCurrentCompany
                    ? TradeCompanyMutationDisposition.Rejected
                    : TradeCompanyMutationDisposition.LocalOnly,
                ErrorCode: "company_not_connected",
                Message: "This company is stored only in this browser.");
        }

        var key = (recordKind, recordId);
        if (requiresCurrentCompany && Connection.State != TradeCompanyConnectionState.Current)
        {
            return new TradeCompanyWebMutationResult(
                TradeCompanyMutationDisposition.Rejected,
                ErrorCode: "company_not_current",
                Message: Connection.Message ?? "Refresh company state before continuing.");
        }

        var request = new TradeCompanyMutationRequest(
            _companyId.Value,
            recordKind,
            recordId,
            JsonSerializer.Serialize(payload, JsonOptions),
            _records.TryGetValue(key, out var current)
                ? current.RecordRevision
                : CompanyRecordRevision.None,
            _companyRevision,
            idempotencyKey ?? Guid.NewGuid().ToString("N"));
        return await ExecuteAsync(request, queueOnFailure: !requiresCurrentCompany, cancellationToken);
    }

    public async Task<IReadOnlyList<TradeCompanyWebMutationResult>> RetryPendingAsync(
        CancellationToken cancellationToken = default)
    {
        if (_companyId == null || _pending.Count == 0)
        {
            return [];
        }

        var results = new List<TradeCompanyWebMutationResult>();
        foreach (var pending in _pending.Values
                     .OrderBy(item => item.QueuedAtUtc)
                     .ToArray())
        {
            results.Add(await ExecuteAsync(
                pending.Request,
                queueOnFailure: true,
                cancellationToken));
        }

        return results;
    }

    public void RegisterIncomingConflict(TradeCompanyRecordEnvelope currentRecord, string message)
    {
        var key = (currentRecord.RecordKind, currentRecord.RecordId);
        _records[key] = currentRecord;
        _conflicts[key] = new TradeCompanyRecordConflict(
            currentRecord.RecordKind,
            currentRecord.RecordId,
            currentRecord,
            DateTime.UtcNow,
            message);
        SetConnection(TradeCompanyConnectionState.Conflict, message);
    }

    public void RegisterRejectedMutation(string recordKind, string recordId, string message)
    {
        var key = (recordKind, recordId);
        _conflicts[key] = new TradeCompanyRecordConflict(
            recordKind,
            recordId,
            _records.GetValueOrDefault(key),
            DateTime.UtcNow,
            message);
        SetConnection(TradeCompanyConnectionState.Conflict, message);
    }

    public void ResolveConflictWithRemote(TradeCompanyRecordEnvelope currentRecord)
    {
        var key = (currentRecord.RecordKind, currentRecord.RecordId);
        ApplyRecord(currentRecord);
        _pending.Remove(key);
        _conflicts.Remove(key);
        SetConnection(ResolveState(), ResolveMessage());
    }

    private async Task<TradeCompanyWebMutationResult> ExecuteAsync(
        TradeCompanyMutationRequest request,
        bool queueOnFailure,
        CancellationToken cancellationToken)
    {
        var key = (request.RecordKind, request.RecordId);
        try
        {
            var result = await _client.MutateAsync(request, cancellationToken);
            if (result.Success && result.Record != null)
            {
                ApplyRecord(result.Record);
                _pending.Remove(key);
                _conflicts.Remove(key);
                SetConnection(ResolveState(), ResolveMessage());
                return new TradeCompanyWebMutationResult(
                    TradeCompanyMutationDisposition.Synced,
                    result.Record,
                    Message: result.Status == TradeCompanyMutationStatus.Replayed
                        ? "The existing company mutation was replayed safely."
                        : "Company state is current.");
            }

            if (result.Status == TradeCompanyMutationStatus.Conflict)
            {
                if (result.CurrentRecord != null)
                {
                    _records[key] = result.CurrentRecord;
                    _companyRevision = Max(_companyRevision, result.CurrentRecord.CompanyRevision);
                }

                _pending.Remove(key);
                _conflicts[key] = new TradeCompanyRecordConflict(
                    request.RecordKind,
                    request.RecordId,
                    result.CurrentRecord,
                    DateTime.UtcNow,
                    result.ErrorMessage ?? "The company record changed in another client.");
                SetConnection(
                    TradeCompanyConnectionState.Conflict,
                    result.ErrorMessage ?? "The company record changed in another client.");
                return new TradeCompanyWebMutationResult(
                    TradeCompanyMutationDisposition.Conflict,
                    CurrentRecord: result.CurrentRecord,
                    ErrorCode: result.ErrorCode,
                    Message: result.ErrorMessage ?? "The company record changed in another client.");
            }

            SetConnection(
                ResolveState(),
                result.ErrorMessage ?? "The company rejected this mutation.");
            return new TradeCompanyWebMutationResult(
                TradeCompanyMutationDisposition.Rejected,
                CurrentRecord: result.CurrentRecord,
                ErrorCode: result.ErrorCode,
                Message: result.ErrorMessage ?? "The company rejected this mutation.");
        }
        catch (Exception ex)
        {
            if (queueOnFailure)
            {
                _pending[key] = new TradeCompanyPendingMutation(
                    request,
                    DateTime.UtcNow,
                    ex.Message);
                SetConnection(
                    TradeCompanyConnectionState.Pending,
                    "Local changes are waiting to reach the Trade Company.");
                return new TradeCompanyWebMutationResult(
                    TradeCompanyMutationDisposition.Pending,
                    ErrorCode: "company_unreachable",
                    Message: ex.Message);
            }

            SetConnection(
                TradeCompanyConnectionState.Unavailable,
                $"Company operation failed: {ex.Message}");
            return new TradeCompanyWebMutationResult(
                TradeCompanyMutationDisposition.Rejected,
                ErrorCode: "company_unreachable",
                Message: ex.Message);
        }
    }

    private void ApplyRecord(TradeCompanyRecordEnvelope record)
    {
        if (_companyId != null && record.CompanyId != _companyId.Value)
        {
            throw new InvalidOperationException("A company change set contained a record from another company.");
        }

        var key = (record.RecordKind, record.RecordId);
        _records[key] = record;
        _companyRevision = Max(_companyRevision, record.CompanyRevision);
    }

    private void ResetLocalOnly()
    {
        _companyId = null;
        _companyRevision = CompanyRevision.None;
        _records.Clear();
        _pending.Clear();
        _conflicts.Clear();
        Connection = TradeCompanyConnectionSnapshot.LocalOnly();
        StateChanged?.Invoke();
    }

    private void SetConnection(TradeCompanyConnectionState state, string? message)
    {
        Connection = new TradeCompanyConnectionSnapshot(
            state,
            _companyId,
            _companyRevision,
            _pending.Count,
            _conflicts.Count,
            message);
        StateChanged?.Invoke();
    }

    private TradeCompanyConnectionState ResolveState()
    {
        if (_conflicts.Count > 0)
        {
            return TradeCompanyConnectionState.Conflict;
        }

        if (_pending.Count > 0)
        {
            return TradeCompanyConnectionState.Pending;
        }

        return _companyId == null
            ? TradeCompanyConnectionState.LocalOnly
            : TradeCompanyConnectionState.Current;
    }

    private string ResolveMessage() =>
        ResolveState() switch
        {
            TradeCompanyConnectionState.Current => "Company state is current.",
            TradeCompanyConnectionState.Pending => "Local changes are waiting to reach the Trade Company.",
            TradeCompanyConnectionState.Conflict => "A company change needs review.",
            _ => Connection.Message ?? "Company state is unavailable."
        };

    private static bool TryResolveCompanyId(TradeCompanyProfile profile, out CompanyId companyId) =>
        CompanyId.TryParse(profile.RemoteId, out companyId);

    private static CompanyRevision Max(CompanyRevision left, CompanyRevision right) =>
        left.Value >= right.Value ? left : right;
}
