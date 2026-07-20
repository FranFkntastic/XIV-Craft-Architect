const TIMEOUT_SPECS = {
  globalMs: ['CA_ORACLE_GLOBAL_TIMEOUT_MS', 30_000, 900_000],
  importMs: ['CA_ORACLE_IMPORT_TIMEOUT_MS', 5_000, 300_000],
  analysisMs: ['CA_ORACLE_ANALYSIS_TIMEOUT_MS', 5_000, 300_000],
  routeReturnMs: ['CA_ORACLE_ROUTE_RETURN_TIMEOUT_MS', 2_000, 120_000],
  routeSettleMs: ['CA_ORACLE_ROUTE_SETTLE_TIMEOUT_MS', 1_000, 60_000],
  navigationMs: ['CA_ORACLE_NAVIGATION_TIMEOUT_MS', 2_000, 120_000],
  reloadMs: ['CA_ORACLE_RELOAD_TIMEOUT_MS', 5_000, 300_000],
  stallMs: ['CA_ORACLE_STALL_TIMEOUT_MS', 2_000, 120_000],
  operationMs: ['CA_ORACLE_BROWSER_OPERATION_TIMEOUT_MS', 1_000, 60_000],
  closeMs: ['CA_ORACLE_CLOSE_TIMEOUT_MS', 500, 30_000],
  heartbeatMs: ['CA_ORACLE_HEARTBEAT_MS', 250, 10_000],
  settledMs: ['CA_ORACLE_SETTLED_WINDOW_MS', 250, 10_000]
};

const SEEDED_DEFAULTS = {
  globalMs: 180_000,
  importMs: 90_000,
  analysisMs: 60_000,
  routeReturnMs: 30_000,
  routeSettleMs: 10_000,
  navigationMs: 30_000,
  reloadMs: 60_000,
  stallMs: 30_000,
  operationMs: 10_000,
  closeMs: 5_000,
  heartbeatMs: 2_000,
  settledMs: 2_000
};

const LIVE_DEFAULTS = {
  ...SEEDED_DEFAULTS,
  globalMs: 300_000,
  importMs: 120_000,
  analysisMs: 180_000,
  routeReturnMs: 60_000,
  reloadMs: 120_000,
  stallMs: 60_000
};

export const REQUIRED_MARKERS = [
  'analysis-publication-complete',
  'autosave-complete',
  'route-generation-started',
  'route-generation-complete'
];

export function loadOracleBudgets(evidenceMode, environment = process.env) {
  const defaults = evidenceMode === 'live' ? LIVE_DEFAULTS : SEEDED_DEFAULTS;
  const budgets = { ...defaults };
  for (const [key, [environmentName, minimum, maximum]] of Object.entries(TIMEOUT_SPECS)) {
    const raw = environment[environmentName];
    if (raw == null || raw === '') continue;
    if (!/^\d+$/.test(raw)) {
      throw new Error(`${environmentName} must be an integer number of milliseconds`);
    }
    const value = Number(raw);
    if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
      throw new Error(`${environmentName} must be between ${minimum} and ${maximum} milliseconds`);
    }
    budgets[key] = value;
  }
  if (budgets.routeSettleMs > budgets.routeReturnMs) {
    throw new Error('CA_ORACLE_ROUTE_SETTLE_TIMEOUT_MS cannot exceed CA_ORACLE_ROUTE_RETURN_TIMEOUT_MS');
  }
  if (budgets.stallMs >= budgets.globalMs) {
    throw new Error('CA_ORACLE_STALL_TIMEOUT_MS must be shorter than CA_ORACLE_GLOBAL_TIMEOUT_MS');
  }
  return budgets;
}

export function createLifecycleFingerprint(snapshot, markerNames = [], networkProgress = 0) {
  const data = snapshot?.data || {};
  const autosave = snapshot?.autosave || {};
  return JSON.stringify({
    planSessionVersion: data.planSessionVersion || '',
    planRootCount: data.planRootCount || '',
    planNodeCount: data.planNodeCount || '',
    marketAnalysisCount: data.marketAnalysisCount || '',
    marketIntelligenceId: data.marketIntelligenceId || '',
    publicationPlanSessionVersion: data.publicationPlanSessionVersion || '',
    publicationMarketVersion: data.publicationMarketVersion || '',
    publicationKind: data.publicationKind || '',
    routeValidity: data.routeValidity || '',
    routeHasDecision: data.routeHasDecision || '',
    routeFailure: data.routeFailure || '',
    routeBasisPlanSessionVersion: data.routeBasisPlanSessionVersion || '',
    routeBasisMarketIntelligenceId: data.routeBasisMarketIntelligenceId || '',
    routeReconciling: data.routeReconciling || '',
    routeReconciliationScheduled: data.routeReconciliationScheduled || '',
    isBusy: data.isBusy || '',
    progressPercent: data.progressPercent || '',
    currentOperation: data.currentOperation || '',
    activeWorkflows: data.activeWorkflows || '',
    dirtyPersistedBuckets: data.dirtyPersistedBuckets || '',
    lastAutosave: data.lastAutosave || '',
    statuses: snapshot?.statuses || [],
    analyzingVisible: Boolean(snapshot?.analyzingVisible),
    visibleBusy: Boolean(snapshot?.visibleBusy),
    autosaveModifiedAt: autosave.modifiedAt || '',
    markerNames: [...markerNames].sort(),
    networkProgress
  });
}

export function getMissingCompletionGates(snapshot, markerNames = []) {
  const data = snapshot?.data || {};
  const autosave = snapshot?.autosave;
  const markers = new Set(markerNames);
  const missing = [];
  const identitiesMatch = Boolean(data.planSessionVersion) &&
    data.planSessionVersion === data.publicationPlanSessionVersion &&
    data.planSessionVersion === data.routeBasisPlanSessionVersion &&
    Boolean(data.marketIntelligenceId) &&
    data.marketIntelligenceId === data.routeBasisMarketIntelligenceId;
  if (!identitiesMatch) missing.push('basis-identities');
  if (!(Number(data.cacheFetchedPairs) > 0 || Number(data.cacheFreshHits) > 0)) missing.push('cache-lane');
  if (data.publicationKind !== 'Known') missing.push('publication');
  if (data.routeValidity !== 'Current') missing.push('route-validity');
  if (data.routeHasDecision !== 'true') missing.push('route-decision');
  if (data.routeFailure) missing.push('route-failure');
  if (data.routeReconciling !== 'false') missing.push('route-reconciling');
  if (data.routeReconciliationScheduled !== 'false') missing.push('route-reconciliation-scheduled');
  if (data.isBusy !== 'false') missing.push('busy');
  if (Number(data.progressPercent) !== 0) missing.push('progress');
  if (data.currentOperation) missing.push('current-operation');
  if (data.activeWorkflows) missing.push('active-workflows');
  if (data.dirtyPersistedBuckets !== 'None') missing.push('dirty-persistence');
  if (!data.lastAutosave) missing.push('last-autosave');
  if (autosave?.id !== 'autosave' || !(autosave?.projectItemCount > 0) || !autosave?.hasPlan || !autosave?.hasMarketIntelligence) {
    missing.push('autosave-readback');
  }
  if (snapshot?.analyzingVisible) missing.push('analyzing-visible');
  if (snapshot?.visibleBusy) missing.push('visible-busy');
  for (const marker of REQUIRED_MARKERS) {
    if (!markers.has(marker)) missing.push(`marker:${marker}`);
  }
  return missing;
}

export function evaluateOracleState({
  nowMs,
  globalStartedAtMs,
  phase,
  phaseStartedAtMs,
  phaseBudgetMs,
  lastProgressAtMs,
  routeTerminalAtMs,
  settledSinceMs,
  snapshot,
  markerNames = [],
  routeResult,
  budgets
}) {
  const data = snapshot?.data || {};
  const status = (snapshot?.statuses || []).join(' | ');
  const missingGates = getMissingCompletionGates(snapshot, markerNames);
  const idle = data.routeReconciling !== 'true' &&
    data.routeReconciliationScheduled !== 'true' &&
    data.isBusy !== 'true' &&
    !data.currentOperation &&
    !data.activeWorkflows &&
    !snapshot?.analyzingVisible &&
    !snapshot?.visibleBusy;

  if (data.routeFailure || /suspect cache|failed|could not|unhandled exception/i.test(status)) {
    return {
      disposition: 'terminal-failure',
      reason: data.routeFailure || status,
      missingGates
    };
  }
  if (routeResult?.workflowStatus && routeResult.workflowStatus !== 'Published') {
    return {
      disposition: 'terminal-failure',
      reason: `Route workflow returned ${routeResult.workflowStatus}.`,
      missingGates
    };
  }

  const publishedRouteReturned = routeResult?.workflowStatus === 'Published';
  const decisionReturned = routeResult?.routeDecision === true;
  const authoritativeRouteMissing = data.routeValidity !== 'Current' ||
    data.routeHasDecision !== 'true' ||
    !data.routeBasisPlanSessionVersion ||
    !data.routeBasisMarketIntelligenceId;
  if (publishedRouteReturned && decisionReturned && idle && authoritativeRouteMissing && routeTerminalAtMs != null &&
      nowMs - routeTerminalAtMs >= budgets.routeSettleMs) {
    return {
      disposition: 'terminal-contradiction',
      reason: 'Route workflow returned Published with a decision, but no current authoritative route basis appeared.',
      missingGates
    };
  }

  if (missingGates.length === 0) {
    if (settledSinceMs == null || nowMs - settledSinceMs < budgets.settledMs) {
      return { disposition: 'wait', reason: 'Completion gates are satisfied; observing stable settle window.', missingGates, settleCandidate: true };
    }
    return { disposition: 'complete', reason: 'All full-transaction gates remained satisfied.', missingGates };
  }

  if (nowMs - globalStartedAtMs >= budgets.globalMs) {
    return { disposition: 'global-timeout', reason: `Overall oracle budget exceeded during ${phase}.`, missingGates };
  }
  if (nowMs - phaseStartedAtMs >= phaseBudgetMs) {
    return { disposition: 'phase-timeout', reason: `${phase} exceeded its ${phaseBudgetMs}ms budget.`, missingGates };
  }
  if (nowMs - lastProgressAtMs >= budgets.stallMs) {
    return { disposition: 'stalled', reason: `No meaningful progress during ${phase} for ${budgets.stallMs}ms.`, missingGates };
  }
  return { disposition: 'wait', reason: `Waiting for ${phase}.`, missingGates };
}

export function createDiagnosticSnapshot({
  phase,
  disposition,
  nowMs,
  lastProgressAtMs,
  progressFingerprint,
  lifecycle,
  markerNames,
  routeResult,
  pendingRequests,
  budgets
}) {
  return {
    phase,
    disposition,
    elapsedMs: Math.round(nowMs),
    lastMeaningfulProgressAtMs: Math.round(lastProgressAtMs),
    progressFingerprint,
    lifecycle,
    missingGates: getMissingCompletionGates(lifecycle, markerNames),
    markerNames: [...markerNames].sort(),
    routeResult: routeResult || null,
    pendingRequests: [...pendingRequests].slice(0, 25),
    budgets
  };
}
