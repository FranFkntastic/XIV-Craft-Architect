using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.CompanyMigration;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Dialogs;

public partial class CompanyMigrationDialog : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ProfileHostMigrationConflictResolution> _resolutions =
        new(StringComparer.Ordinal);
    private readonly List<ProfileHostMigrationCanonicalMapping> _mappings = [];
    private readonly List<MigrationSourceEntry> _sourceEntries = [];
    private IReadOnlyList<CompanyMigrationSourceBlocker> _sourceOperationBlockers =
        Array.Empty<CompanyMigrationSourceBlocker>();

    [CascadingParameter] private MudDialogInstance MudDialog { get; set; } = default!;
    [Inject] private CompanyMigrationWorkflowService Workflow { get; set; } = default!;

    [Parameter] public string InitialHostUrl { get; set; } = string.Empty;
    [Parameter] public string InitialAccessKey { get; set; } = string.Empty;

    private ElementReference _stepHeading;
    private CompanyMigrationExportBundle? _source;
    private ProfileHostProfileResponse? _destination;
    private ProfileHostMigrationPreflightResponse? _preflight;
    private MigrationReviewSnapshot? _review;
    private ProfileHostMigrationCommitResponse? _commit;
    private WizardStep _step;
    private ReconcileFilter _reconcileFilter = ReconcileFilter.NeedsAttention;
    private string _hostUrl = string.Empty;
    private string _accessKey = string.Empty;
    private string _verifiedHostUrl = string.Empty;
    private string _verifiedAccessKey = string.Empty;
    private string? _sourceError;
    private string? _importError;
    private string? _destinationError;
    private string? _preflightError;
    private string? _commitError;
    private string? _activationError;
    private string _liveMessage = "Inventorying the current browser.";
    private bool _isCapturing;
    private bool _isImporting;
    private bool _isRecoveringActivation;
    private bool _isVerifying;
    private bool _isPreflighting;
    private bool _isCommitting;
    private bool _rememberAccessKey;
    private bool _hasPendingCommit;
    private bool _hasPendingActivation;
    private bool _activationRecovered;

    private static readonly StepItem[] StepItems =
    [
        new(WizardStep.Sources, "Sources"),
        new(WizardStep.Destination, "Destination"),
        new(WizardStep.Reconcile, "Reconcile"),
        new(WizardStep.Review, "Review"),
        new(WizardStep.Move, "Move")
    ];

    private static readonly ReconcileFilter[] ReconcileFilters =
    [
        ReconcileFilter.All,
        ReconcileFilter.NeedsAttention,
        ReconcileFilter.Identical,
        ReconcileFilter.New
    ];

    protected override async Task OnInitializedAsync()
    {
        _hostUrl = InitialHostUrl;
        _accessKey = InitialAccessKey;
        _rememberAccessKey = !string.IsNullOrWhiteSpace(InitialAccessKey);
        try
        {
            await Workflow.InitializeAsync(_lifetime.Token);
            _hasPendingCommit = Workflow.HasPendingCommit;
            _hasPendingActivation = Workflow.HasPendingActivation;
        }
        catch (Exception ex)
        {
            _activationError = ex.Message;
        }
        await RefreshSourceAsync();
        if (_hasPendingCommit || _hasPendingActivation)
        {
            _step = WizardStep.Destination;
        }
    }

    private async Task RefreshSourceAsync()
    {
        _isCapturing = true;
        _sourceError = null;
        _liveMessage = "Inventorying the current browser.";
        try
        {
            var current = await Workflow.CaptureCurrentBrowserAsync(_lifetime.Token);
            var candidateEntries = _sourceEntries
                .Where(entry => !entry.IsCurrentBrowser)
                .Prepend(new MigrationSourceEntry("Current browser", current, true))
                .ToArray();
            var combination = Workflow.CombineSources(
                candidateEntries.Select(entry => entry.Bundle).ToArray());
            if (!combination.CanUse || combination.Bundle is null)
            {
                _sourceOperationBlockers = combination.Blockers;
                _sourceError = "The refreshed browser inventory could not be combined with the selected exports.";
                _liveMessage = "The refreshed source combination is blocked.";
                return;
            }
            _sourceEntries.Clear();
            _sourceEntries.AddRange(candidateEntries);
            _source = combination.Bundle;
            _sourceOperationBlockers = Array.Empty<CompanyMigrationSourceBlocker>();
            _importError = null;
            ClearAfterSource();
            _liveMessage = _source.Manifest.CanPreflight
                ? $"{_sourceEntries.Count} source inventories are ready."
                : $"The combined inventory has {HardSourceBlockers.Count} blocking conditions.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _sourceError = ex.Message;
            _liveMessage = "Current browser inventory failed.";
        }
        finally
        {
            _isCapturing = false;
        }
    }

    private async Task ImportBundleAsync(InputFileChangeEventArgs args)
    {
        const long MaxMigrationBundleBytes = 32 * 1024 * 1024;
        _isImporting = true;
        _importError = null;
        _liveMessage = $"Reading migration export {args.File.Name}.";
        try
        {
            await using var stream = args.File.OpenReadStream(
                MaxMigrationBundleBytes,
                _lifetime.Token);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(_lifetime.Token);
            var validation = Workflow.ParseUploadedBundle(json);
            if (!validation.IsValid || validation.Bundle is null)
            {
                _sourceOperationBlockers = validation.Blockers;
                _importError = $"{args.File.Name} is not a valid migration bundle.";
                _liveMessage = $"Migration export {args.File.Name} was rejected.";
                return;
            }
            var candidateEntries = _sourceEntries
                .Append(new MigrationSourceEntry(args.File.Name, validation.Bundle, false))
                .ToArray();
            var combination = Workflow.CombineSources(
                candidateEntries.Select(entry => entry.Bundle).ToArray());
            if (!combination.CanUse || combination.Bundle is null)
            {
                _sourceOperationBlockers = combination.Blockers;
                _importError = $"{args.File.Name} conflicts with the selected source inventories.";
                _liveMessage = $"Migration export {args.File.Name} was not added.";
                return;
            }
            _sourceEntries.Clear();
            _sourceEntries.AddRange(candidateEntries);
            _source = combination.Bundle;
            _sourceOperationBlockers = Array.Empty<CompanyMigrationSourceBlocker>();
            _sourceError = null;
            ClearAfterSource();
            _liveMessage = _source.Manifest.CanPreflight
                ? $"{_sourceEntries.Count} source inventories are combined and ready."
                : $"The combined inventory has {HardSourceBlockers.Count} blocking conditions.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _importError = ex.Message;
            _liveMessage = $"Migration export {args.File.Name} was not added.";
        }
        finally
        {
            _isImporting = false;
        }
    }

    private async Task VerifyDestinationAsync()
    {
        _isVerifying = true;
        _destinationError = null;
        _destination = null;
        ClearAfterDestination();
        _liveMessage = "Verifying the hosted profile.";
        try
        {
            var normalizedHost = _hostUrl.Trim();
            var accessKey = _accessKey.Trim();
            Workflow.SetRememberAccessKey(_rememberAccessKey);
            _destination = await Workflow.VerifyDestinationAsync(
                normalizedHost,
                accessKey,
                _lifetime.Token);
            _verifiedHostUrl = normalizedHost;
            _verifiedAccessKey = accessKey;
            _liveMessage = $"Hosted profile {DisplayProfileName(_destination)} is verified.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _destinationError = ex.Message;
            _liveMessage = "Hosted destination verification failed.";
        }
        finally
        {
            _isVerifying = false;
        }
    }

    private async Task ResumeRecoveryAsync()
    {
        if ((!_hasPendingCommit && !_hasPendingActivation) ||
            string.IsNullOrWhiteSpace(_accessKey))
        {
            return;
        }

        _isRecoveringActivation = true;
        _activationError = null;
        _liveMessage = _hasPendingCommit
            ? "Retrying the exact saved migration commit."
            : "Resuming the committed hosted-profile activation.";
        try
        {
            if (_hasPendingCommit)
            {
                var result = await Workflow.RetryCommitAsync(
                    _accessKey.Trim(),
                    _rememberAccessKey,
                    _lifetime.Token);
                if (!result.Succeeded)
                {
                    _hasPendingCommit = false;
                    _activationError =
                        "The destination changed. Start a fresh preflight with the latest hosted state.";
                    _liveMessage = "The saved commit no longer matches the destination.";
                    return;
                }
            }
            else
            {
                await Workflow.RetryActivationAsync(
                    _accessKey.Trim(),
                    _rememberAccessKey,
                    _lifetime.Token);
            }

            _hasPendingCommit = false;
            _hasPendingActivation = false;
            _activationRecovered = true;
            _commit = Workflow.LastReceipt;
            _liveMessage = "Hosted-profile activation is complete.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _activationError = ex.Message;
            _liveMessage = "Hosted-profile activation could not be resumed.";
        }
        finally
        {
            _isRecoveringActivation = false;
        }
    }

    private void CloseRecovered()
    {
        var receipt = _commit ?? Workflow.LastReceipt;
        if (receipt != null)
        {
            MudDialog.Close(DialogResult.Ok(receipt));
        }
    }

    private async Task RunPreflightAsync()
    {
        if (_source is null || _destination is null || !DestinationIsCurrent)
        {
            return;
        }

        _isPreflighting = true;
        _preflightError = null;
        _review = null;
        _commit = null;
        _commitError = null;
        _liveMessage = "Reconciling browser records with the hosted profile.";
        try
        {
            var response = await Workflow.PreflightAsync(
                _verifiedHostUrl,
                _verifiedAccessKey,
                _source,
                ResolutionList,
                _mappings.ToArray(),
                _lifetime.Token);
            _preflight = response;
            MergeServerResolutions(response);
            _mappings.Clear();
            _mappings.AddRange(response.Mappings);
            _liveMessage = response.CanCommit
                ? "Reconciliation is complete and ready for review."
                : $"Reconciliation needs {DecisionCount} decisions or blocker fixes.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _preflight = null;
            _preflightError = ex.Message;
            _liveMessage = "Migration preflight failed.";
        }
        finally
        {
            _isPreflighting = false;
        }
    }

    private async Task OnResolutionChangedAsync(
        ProfileHostMigrationObjectAssessment assessment,
        ChangeEventArgs args)
    {
        var key = ResolutionKey(assessment.Collection, assessment.ObjectId);
        if (Enum.TryParse<ProfileHostMigrationConflictResolution>(
                args.Value?.ToString(),
                out var resolution))
        {
            _resolutions[key] = resolution;
        }
        else
        {
            _resolutions.Remove(key);
        }

        await RunPreflightAsync();
    }

    private async Task CommitAsync()
    {
        if (_review is null || _isCommitting)
        {
            return;
        }

        _isCommitting = true;
        _commitError = null;
        _liveMessage = "Committing the resolved migration.";
        try
        {
            Workflow.SetRememberAccessKey(_rememberAccessKey);
            var result = await Workflow.CommitAsync(
                _verifiedHostUrl,
                _verifiedAccessKey,
                _review.Source,
                _review.Preflight,
                _review.Resolutions,
                _review.Mappings,
                _lifetime.Token);
            if (result.Succeeded)
            {
                _commit = result.Commit;
                _liveMessage = $"Migration committed at server revision {_commit!.ServerRevision}.";
                return;
            }

            _preflight = result.Conflict;
            if (_preflight is not null)
            {
                MergeServerResolutions(_preflight);
                _mappings.Clear();
                _mappings.AddRange(_preflight.Mappings);
            }
            _review = null;
            _commitError = "The destination changed after review. Reconcile the latest authoritative state before retrying.";
            _preflightError = _commitError;
            _liveMessage = "Commit paused because the authoritative destination changed.";
            await SetStepAsync(WizardStep.Reconcile);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _commitError = ex.Message;
            _liveMessage = "Migration commit failed. The immutable request can be retried.";
        }
        finally
        {
            _isCommitting = false;
        }
    }

    private async Task ContinueAsync()
    {
        switch (_step)
        {
            case WizardStep.Sources:
                await SetStepAsync(WizardStep.Destination);
                break;
            case WizardStep.Destination:
                await RunPreflightAsync();
                if (_preflight is not null)
                {
                    await SetStepAsync(WizardStep.Reconcile);
                }
                break;
            case WizardStep.Reconcile:
                if (_preflight?.CanCommit == true && _source is not null && _destination is not null)
                {
                    _review = new MigrationReviewSnapshot(
                        _source,
                        _destination,
                        _preflight,
                        ResolutionList,
                        _mappings.ToArray());
                    await SetStepAsync(WizardStep.Review);
                }
                break;
            case WizardStep.Review:
                await SetStepAsync(WizardStep.Move);
                break;
        }
    }

    private async Task BackAsync()
    {
        if (_step == WizardStep.Sources || _isCommitting)
        {
            return;
        }

        if (_step <= WizardStep.Review)
        {
            _review = null;
        }
        await SetStepAsync(_step - 1);
    }

    private async Task GoToStepAsync(WizardStep step)
    {
        if (CanEnterStep(step) && !_isCommitting)
        {
            if (step < WizardStep.Review)
            {
                _review = null;
            }
            await SetStepAsync(step);
        }
    }

    private async Task SetStepAsync(WizardStep step)
    {
        _step = step;
        await InvokeAsync(StateHasChanged);
        await Task.Yield();
        await _stepHeading.FocusAsync();
    }

    private void Close()
    {
        if (!_isCommitting)
        {
            _lifetime.Cancel();
            MudDialog.Cancel();
        }
    }

    private void CloseWithReceipt()
    {
        if (_commit is not null)
        {
            MudDialog.Close(DialogResult.Ok(_commit));
        }
    }

    private void ClearAfterSource()
    {
        ClearAfterDestination();
    }

    private void ClearAfterDestination()
    {
        _preflight = null;
        _review = null;
        _commit = null;
        _resolutions.Clear();
        _mappings.Clear();
        _preflightError = null;
        _commitError = null;
    }

    private void MergeServerResolutions(ProfileHostMigrationPreflightResponse response)
    {
        foreach (var assessment in response.Objects.Where(item => item.Resolution is not null))
        {
            _resolutions[ResolutionKey(assessment.Collection, assessment.ObjectId)] =
                assessment.Resolution!.Value;
        }
    }

    private bool CanEnterStep(WizardStep step) =>
        step switch
        {
            WizardStep.Sources => true,
            WizardStep.Destination => CanSourceProceed,
            WizardStep.Reconcile => CanSourceProceed &&
                                    _destination is not null &&
                                    DestinationIsCurrent,
            WizardStep.Review => _review is not null || _preflight?.CanCommit == true,
            WizardStep.Move => _review is not null,
            _ => false
        };

    private bool IsStepComplete(WizardStep step) =>
        step switch
        {
            WizardStep.Sources => CanSourceProceed,
            WizardStep.Destination => _destination is not null && DestinationIsCurrent,
            WizardStep.Reconcile => _preflight?.CanCommit == true,
            WizardStep.Review => _review is not null,
            WizardStep.Move => _commit is not null,
            _ => false
        };

    private bool CanVerifyDestination =>
        !_isVerifying &&
        !_hasPendingCommit &&
        !_hasPendingActivation &&
        !_activationRecovered &&
        !_isCommitting &&
        !string.IsNullOrWhiteSpace(_hostUrl) &&
        !string.IsNullOrWhiteSpace(_accessKey);

    private bool DestinationIsCurrent =>
        _destination is not null &&
        string.Equals(_hostUrl.Trim(), _verifiedHostUrl, StringComparison.Ordinal) &&
        string.Equals(_accessKey.Trim(), _verifiedAccessKey, StringComparison.Ordinal);

    private bool CanContinue =>
        !_isCapturing &&
        !_isImporting &&
        !_isRecoveringActivation &&
        !_isVerifying &&
        !_isPreflighting &&
        !_isCommitting &&
        _step switch
        {
            WizardStep.Sources => CanSourceProceed,
            WizardStep.Destination => _destination is not null && DestinationIsCurrent,
            WizardStep.Reconcile => _preflight?.CanCommit == true,
            WizardStep.Review => _review is not null,
            _ => false
        };

    private string ContinueLabel =>
        _step switch
        {
            WizardStep.Sources => "Continue to destination",
            WizardStep.Destination => "Continue to reconcile",
            WizardStep.Reconcile => "Continue to review",
            WizardStep.Review => "Continue to move",
            _ => "Continue"
        };

    private string CurrentStepTitle =>
        _step switch
        {
            WizardStep.Sources => "Collect company sources",
            WizardStep.Destination when _hasPendingCommit => "Retry the exact hosted commit",
            WizardStep.Destination when _hasPendingActivation => "Finish hosted activation",
            WizardStep.Destination => "Verify the hosted profile",
            WizardStep.Reconcile => "Resolve authoritative differences",
            WizardStep.Review => "Review the immutable request",
            WizardStep.Move => "Move the companies once",
            _ => string.Empty
        };

    private string CurrentStepDescription =>
        _step switch
        {
            WizardStep.Sources => "The current browser and explicit migration exports are combined by stable identity; branch and origin remain provenance.",
            WizardStep.Destination when _hasPendingCommit => "A prior request may already be committed. The saved migration ID and request are retried unchanged.",
            WizardStep.Destination when _hasPendingActivation => "A prior hosted commit has a durable receipt but local activation did not finish.",
            WizardStep.Destination => "The URL and access key identify one hosted profile; no browser-selected active company is implied.",
            WizardStep.Reconcile => "Identical and new records resolve automatically. Conflicts require an explicit authoritative choice.",
            WizardStep.Review => "These counts, decisions, mappings, and hashes are frozen for the commit request.",
            WizardStep.Move => "The hosted service applies the full resolved bundle atomically and returns a durable receipt.",
            _ => string.Empty
        };

    private string GetCurrentBadgeText() =>
        _step switch
        {
            WizardStep.Sources when CanSourceProceed => "Source ready",
            WizardStep.Sources when _source is not null => "Source blocked",
            WizardStep.Sources => "Inventory pending",
            WizardStep.Destination when _hasPendingCommit => "Commit retry required",
            WizardStep.Destination when _hasPendingActivation => "Activation required",
            WizardStep.Destination when _activationRecovered => "Activation complete",
            WizardStep.Destination when DestinationIsCurrent => "Verified",
            WizardStep.Destination => "Verification required",
            WizardStep.Reconcile when _preflight?.CanCommit == true => "Ready",
            WizardStep.Reconcile => $"{DecisionCount} require attention",
            WizardStep.Review => "Immutable summary",
            WizardStep.Move when _commit is not null => "Committed",
            WizardStep.Move when _isCommitting => "Committing",
            WizardStep.Move => "Ready to commit",
            _ => string.Empty
        };

    private string GetCurrentBadgeClass() =>
        GetCurrentBadgeText() switch
        {
            "Source ready" or "Verified" or "Ready" or "Committed" or "Activation complete" => "migration-badge good",
            "Activation required" or "Commit retry required" => "migration-badge warn",
            "Source blocked" => "migration-badge bad",
            _ => "migration-badge"
        };

    private string FooterStatus
    {
        get
        {
            if (_isCapturing || _isImporting || _isRecoveringActivation || _isVerifying || _isPreflighting || _isCommitting)
            {
                return _liveMessage;
            }
            return _step switch
            {
                WizardStep.Sources when CanSourceProceed =>
                    $"{GetCount("companies")} companies are ready to move.",
                WizardStep.Sources when _source is not null =>
                    $"{HardSourceBlockers.Count + _sourceOperationBlockers.Count} source blockers prevent migration.",
                WizardStep.Destination when DestinationIsCurrent =>
                    $"Hosted profile {DisplayProfileName(_destination!)} is verified.",
                WizardStep.Destination when _hasPendingCommit =>
                    "Retry the exact saved migration commit before starting another move.",
                WizardStep.Destination when _hasPendingActivation =>
                    "Resume the committed migration activation before starting another move.",
                WizardStep.Destination when _activationRecovered =>
                    "This browser is now using the committed hosted profile.",
                WizardStep.Destination => "Verify a hosted profile to continue.",
                WizardStep.Reconcile when _preflight?.CanCommit == true =>
                    "All authoritative differences are resolved.",
                WizardStep.Reconcile => $"{DecisionCount} decisions or blockers prevent review.",
                WizardStep.Review => "The immutable migration request is ready.",
                WizardStep.Move when _commit is not null =>
                    $"Committed at server revision {_commit.ServerRevision}.",
                WizardStep.Move => "Nothing has been written yet.",
                _ => _liveMessage
            };
        }
    }

    private string GetFooterStatusClass() =>
        (_source is not null && !CanSourceProceed) ||
        (_step == WizardStep.Reconcile && _preflight?.CanCommit != true) ||
        _sourceError is not null ||
        _activationError is not null ||
        _destinationError is not null ||
        _preflightError is not null ||
        _commitError is not null
            ? "migration-footer-status blocked"
            : "migration-footer-status";

    private string GetFooterStatusIcon() =>
        GetFooterStatusClass().Contains("blocked", StringComparison.Ordinal)
            ? Icons.Material.Filled.Error
            : Icons.Material.Filled.CheckCircle;

    private int DecisionCount =>
        (_preflight?.Objects.Count(item => RequiresResolution(item) && !HasResolution(item)) ?? 0) +
        (_preflight?.Blockers.Count ?? 0);

    private bool CanSourceProceed =>
        _source?.Manifest.CanPreflight == true;

    private IReadOnlyList<CompanyMigrationSourceBlocker> HardSourceBlockers =>
        _source?.Manifest.Blockers
            .Where(blocker => !blocker.IsArchiveOnly)
            .ToArray() ??
        Array.Empty<CompanyMigrationSourceBlocker>();

    private IReadOnlyList<CompanyMigrationSourceBlocker> ArchiveSourceBlockers =>
        _source?.Manifest.Blockers
            .Where(blocker => blocker.IsArchiveOnly)
            .ToArray() ??
        Array.Empty<CompanyMigrationSourceBlocker>();

    private IReadOnlyList<ProfileHostMigrationObjectAssessment> FilteredAssessments =>
        (_preflight?.Objects ?? Array.Empty<ProfileHostMigrationObjectAssessment>())
        .Where(item => _reconcileFilter switch
        {
            ReconcileFilter.NeedsAttention =>
                RequiresResolution(item) || item.Disposition == ProfileHostMigrationObjectDisposition.AuthoritativeTombstone,
            ReconcileFilter.Identical =>
                item.Disposition == ProfileHostMigrationObjectDisposition.Identical,
            ReconcileFilter.New =>
                item.Disposition == ProfileHostMigrationObjectDisposition.Insert,
            _ => true
        })
        .ToArray();

    private IReadOnlyList<ProfileHostMigrationResolution> ResolutionList =>
        _resolutions
            .Select(pair =>
            {
                var parts = pair.Key.Split('\0', 2);
                return new ProfileHostMigrationResolution
                {
                    Collection = parts[0],
                    ObjectId = parts[1],
                    Resolution = pair.Value
                };
            })
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();

    private IReadOnlyList<(string Label, int Value)> ReviewCounts =>
    [
        ("Companies", GetCount("companies")),
        ("Crafters", GetCount("crafters")),
        ("Orders", GetCount("orders")),
        ("Payroll drafts", GetCount("payrollDrafts"))
    ];

    private int GetCount(string key) =>
        _source?.Manifest.Counts.TryGetValue(key, out var count) == true ? count : 0;

    private static int BundleCount(CompanyMigrationExportBundle bundle, string key) =>
        bundle.Manifest.Counts.TryGetValue(key, out var count) ? count : 0;

    private int FilterCount(ReconcileFilter filter) =>
        (_preflight?.Objects ?? Array.Empty<ProfileHostMigrationObjectAssessment>())
        .Count(item => filter switch
        {
            ReconcileFilter.NeedsAttention =>
                RequiresResolution(item) || item.Disposition == ProfileHostMigrationObjectDisposition.AuthoritativeTombstone,
            ReconcileFilter.Identical =>
                item.Disposition == ProfileHostMigrationObjectDisposition.Identical,
            ReconcileFilter.New =>
                item.Disposition == ProfileHostMigrationObjectDisposition.Insert,
            _ => true
        });

    private string FilterLabel(ReconcileFilter filter) =>
        filter switch
        {
            ReconcileFilter.All => $"All {FilterCount(filter)}",
            ReconcileFilter.NeedsAttention => $"Needs attention {FilterCount(filter)}",
            ReconcileFilter.Identical => $"Identical {FilterCount(filter)}",
            ReconcileFilter.New => $"New {FilterCount(filter)}",
            _ => string.Empty
        };

    private string GetFilterClass(ReconcileFilter filter) =>
        filter == _reconcileFilter ? "migration-filter active" : "migration-filter";

    private string GetStepClass(WizardStep step)
    {
        var classes = new List<string> { "migration-step" };
        if (step == _step)
        {
            classes.Add("active");
        }
        if (IsStepComplete(step))
        {
            classes.Add("done");
        }
        return string.Join(' ', classes);
    }

    private string? GetAriaCurrent(WizardStep step) => step == _step ? "step" : null;

    private string GetStepSummary(WizardStep step) =>
        step switch
        {
            WizardStep.Sources when _source is null => "Current browser",
            WizardStep.Sources => $"{_sourceEntries.Count} sources · {GetCount("companies")} companies",
            WizardStep.Destination when _hasPendingCommit => "Retry exact commit",
            WizardStep.Destination when _hasPendingActivation => "Resume activation",
            WizardStep.Destination when _activationRecovered => "Hosted profile active",
            WizardStep.Destination when DestinationIsCurrent => DisplayProfileName(_destination!),
            WizardStep.Destination => "Hosted profile",
            WizardStep.Reconcile when _preflight is null => "Preflight",
            WizardStep.Reconcile => $"{DecisionCount} require attention",
            WizardStep.Review => "Counts and ownership",
            WizardStep.Move when _commit is not null => $"Revision {_commit.ServerRevision}",
            WizardStep.Move => "Transactional commit",
            _ => string.Empty
        };

    private static bool RequiresResolution(ProfileHostMigrationObjectAssessment assessment) =>
        assessment.Disposition is
            ProfileHostMigrationObjectDisposition.SameIdDifferentContent or
            ProfileHostMigrationObjectDisposition.AuthoritativeTombstone;

    private bool HasResolution(ProfileHostMigrationObjectAssessment assessment) =>
        _resolutions.ContainsKey(ResolutionKey(assessment.Collection, assessment.ObjectId)) ||
        assessment.Resolution is not null;

    private string GetResolutionValue(ProfileHostMigrationObjectAssessment assessment) =>
        _resolutions.TryGetValue(ResolutionKey(assessment.Collection, assessment.ObjectId), out var value)
            ? value.ToString()
            : assessment.Resolution?.ToString() ?? string.Empty;

    private static IReadOnlyList<ProfileHostMigrationConflictResolution> GetResolutionOptions(
        ProfileHostMigrationObjectAssessment assessment) =>
        assessment.Disposition == ProfileHostMigrationObjectDisposition.AuthoritativeTombstone
            ?
            [
                ProfileHostMigrationConflictResolution.KeepAuthoritative,
                ProfileHostMigrationConflictResolution.ResurrectIncoming
            ]
            :
            [
                ProfileHostMigrationConflictResolution.KeepAuthoritative,
                ProfileHostMigrationConflictResolution.UseIncoming
            ];

    private static string DisplayResolution(ProfileHostMigrationConflictResolution resolution) =>
        resolution switch
        {
            ProfileHostMigrationConflictResolution.KeepAuthoritative => "Keep hosted version",
            ProfileHostMigrationConflictResolution.UseIncoming => "Use browser version",
            ProfileHostMigrationConflictResolution.ResurrectIncoming => "Restore browser version",
            ProfileHostMigrationConflictResolution.KeepBothAsCopy => "Keep both; copy browser record",
            _ => resolution.ToString()
        };

    private static string DisplayDisposition(ProfileHostMigrationObjectDisposition disposition) =>
        disposition switch
        {
            ProfileHostMigrationObjectDisposition.Insert => "New record",
            ProfileHostMigrationObjectDisposition.Identical => "Identical",
            ProfileHostMigrationObjectDisposition.SameIdDifferentContent => "Same ID, different content",
            ProfileHostMigrationObjectDisposition.AuthoritativeTombstone => "Hosted deletion exists",
            _ => disposition.ToString()
        };

    private static string GetDispositionBadgeClass(ProfileHostMigrationObjectDisposition disposition) =>
        disposition switch
        {
            ProfileHostMigrationObjectDisposition.Insert => "migration-badge good",
            ProfileHostMigrationObjectDisposition.Identical => "migration-badge",
            _ => "migration-badge warn"
        };

    private static string GetAssessmentRowClass(ProfileHostMigrationObjectAssessment assessment) =>
        RequiresResolution(assessment) ? "requires-resolution" : string.Empty;

    private static string DisplayCollection(string collection) =>
        collection switch
        {
            ProfileSyncCollections.TradeCompanyProfiles => "Company",
            ProfileSyncCollections.TradeCrafters => "Crafter",
            ProfileSyncCollections.TradeOrders => "Trade order",
            ProfileSyncCollections.TradePayrollDrafts => "Payroll draft",
            ProfileSyncCollections.Plans => "Craft plan",
            ProfileHostMigrationCollections.TradeOrderCraftSnapshots => "Order craft snapshot",
            _ => collection
        };

    private static string ResolutionKey(string collection, string objectId) =>
        $"{collection}\0{objectId}";

    private static string DisplayOrigin(string? origin) =>
        string.IsNullOrWhiteSpace(origin) ? "Browser origin unavailable" : origin;

    private static string DisplayHost(string hostUrl) =>
        Uri.TryCreate(hostUrl, UriKind.Absolute, out var uri) ? uri.Host : hostUrl;

    private static string DisplayProfileName(ProfileHostProfileResponse profile) =>
        string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.ProfileId : profile.DisplayName;

    private static string DisplayCompanyName(CompanyMigrationCompanySummary company) =>
        string.IsNullOrWhiteSpace(company.Name) ? "Unnamed company" : company.Name;

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unavailable";
        }
        return value.Length <= 14 ? value : $"{value[..8]}…{value[^4..]}";
    }

    private static string FormatLocalTime(DateTime value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string DatabaseState(bool exists, int? version) =>
        exists ? $"Schema v{version?.ToString() ?? "unknown"}" : "Not present";

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private enum WizardStep
    {
        Sources,
        Destination,
        Reconcile,
        Review,
        Move
    }

    private enum ReconcileFilter
    {
        All,
        NeedsAttention,
        Identical,
        New
    }

    private sealed record StepItem(WizardStep Step, string Label);

    private sealed record MigrationSourceEntry(
        string Label,
        CompanyMigrationExportBundle Bundle,
        bool IsCurrentBrowser);

    private sealed record MigrationReviewSnapshot(
        CompanyMigrationExportBundle Source,
        ProfileHostProfileResponse Destination,
        ProfileHostMigrationPreflightResponse Preflight,
        IReadOnlyList<ProfileHostMigrationResolution> Resolutions,
        IReadOnlyList<ProfileHostMigrationCanonicalMapping> Mappings);
}
