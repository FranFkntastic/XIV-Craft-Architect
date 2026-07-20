import assert from 'node:assert/strict';
import test from 'node:test';
import {
  REQUIRED_MARKERS,
  createDiagnosticSnapshot,
  createLifecycleFingerprint,
  evaluateOracleState,
  getMissingCompletionGates,
  loadOracleBudgets
} from './workflow-oracle.mjs';

const budgets = loadOracleBudgets('seeded', {});

function completeSnapshot(overrides = {}) {
  return {
    data: {
      planSessionVersion: '2',
      publicationPlanSessionVersion: '2',
      routeBasisPlanSessionVersion: '2',
      marketIntelligenceId: 'market-1',
      routeBasisMarketIntelligenceId: 'market-1',
      cacheFetchedPairs: '212',
      cacheFreshHits: '0',
      publicationKind: 'Known',
      routeValidity: 'Current',
      routeHasDecision: 'true',
      routeFailure: '',
      routeReconciling: 'false',
      routeReconciliationScheduled: 'false',
      isBusy: 'false',
      progressPercent: '0',
      currentOperation: '',
      activeWorkflows: '',
      dirtyPersistedBuckets: 'None',
      lastAutosave: '2026-07-20T12:00:00Z',
      ...overrides
    },
    statuses: [],
    analyzingVisible: false,
    visibleBusy: false,
    autosave: {
      id: 'autosave',
      modifiedAt: '2026-07-20T12:00:00Z',
      projectItemCount: 15,
      hasPlan: true,
      hasMarketIntelligence: true
    }
  };
}

function evaluate(overrides = {}) {
  return evaluateOracleState({
    nowMs: 10_000,
    globalStartedAtMs: 0,
    phase: 'route-settlement',
    phaseStartedAtMs: 5_000,
    phaseBudgetMs: budgets.routeSettleMs,
    lastProgressAtMs: 9_000,
    routeTerminalAtMs: 5_000,
    settledSinceMs: 7_500,
    snapshot: completeSnapshot(),
    markerNames: REQUIRED_MARKERS,
    routeResult: { workflowStatus: 'Published', routeDecision: true },
    budgets,
    ...overrides
  });
}

test('completion requires the stable settle window', () => {
  const waiting = evaluate({ nowMs: 9_000, settledSinceMs: 8_000 });
  assert.equal(waiting.disposition, 'wait');
  assert.equal(waiting.settleCandidate, true);

  const completed = evaluate({ nowMs: 10_000, settledSinceMs: 7_500 });
  assert.equal(completed.disposition, 'complete');
  assert.deepEqual(completed.missingGates, []);
});

test('published route decision without authoritative route fails after grace', () => {
  const snapshot = completeSnapshot({
    routeValidity: 'None',
    routeHasDecision: '',
    routeBasisPlanSessionVersion: '',
    routeBasisMarketIntelligenceId: ''
  });
  const beforeGrace = evaluate({ nowMs: 14_999, routeTerminalAtMs: 5_000, snapshot, settledSinceMs: null });
  assert.equal(beforeGrace.disposition, 'wait');

  const afterGrace = evaluate({ nowMs: 15_000, routeTerminalAtMs: 5_000, snapshot, settledSinceMs: null });
  assert.equal(afterGrace.disposition, 'terminal-contradiction');
  assert.match(afterGrace.reason, /no current authoritative route basis/i);
});

test('active reconciliation prevents a premature route contradiction', () => {
  const snapshot = completeSnapshot({
    routeValidity: 'None',
    routeHasDecision: '',
    routeBasisPlanSessionVersion: '',
    routeBasisMarketIntelligenceId: '',
    routeReconciling: 'true'
  });
  const result = evaluate({
    nowMs: 15_000,
    routeTerminalAtMs: 5_000,
    phaseStartedAtMs: 12_000,
    phaseBudgetMs: 30_000,
    snapshot,
    settledSinceMs: null
  });
  assert.equal(result.disposition, 'wait');
});

test('explicit route failure is terminal immediately', () => {
  const snapshot = completeSnapshot({ routeFailure: 'No route exists.' });
  const result = evaluate({ nowMs: 5_100, routeTerminalAtMs: null, snapshot, settledSinceMs: null });
  assert.equal(result.disposition, 'terminal-failure');
  assert.equal(result.reason, 'No route exists.');
});

test('non-published route workflow result is terminal immediately', () => {
  const result = evaluate({
    nowMs: 5_100,
    routeTerminalAtMs: 5_000,
    routeResult: { workflowStatus: 'NoCompleteRoute', routeDecision: false },
    settledSinceMs: null
  });
  assert.equal(result.disposition, 'terminal-failure');
  assert.match(result.reason, /NoCompleteRoute/);
});

test('lack of meaningful progress triggers a stall', () => {
  const snapshot = completeSnapshot({ routeValidity: 'None', routeHasDecision: '' });
  const result = evaluate({
    nowMs: 40_000,
    phaseStartedAtMs: 20_000,
    phaseBudgetMs: 60_000,
    lastProgressAtMs: 10_000,
    routeTerminalAtMs: null,
    routeResult: null,
    snapshot,
    settledSinceMs: null
  });
  assert.equal(result.disposition, 'stalled');
});

test('meaningful progress resets the stall clock', () => {
  const snapshot = completeSnapshot({ routeValidity: 'None', routeHasDecision: '' });
  const result = evaluate({
    nowMs: 40_000,
    phaseStartedAtMs: 20_000,
    phaseBudgetMs: 60_000,
    lastProgressAtMs: 39_000,
    routeTerminalAtMs: null,
    routeResult: null,
    snapshot,
    settledSinceMs: null
  });
  assert.equal(result.disposition, 'wait');
});

test('phase and global timeouts are classified separately', () => {
  const snapshot = completeSnapshot({ routeValidity: 'None', routeHasDecision: '' });
  const phaseTimeout = evaluate({
    nowMs: 20_000,
    phaseStartedAtMs: 5_000,
    phaseBudgetMs: 10_000,
    lastProgressAtMs: 19_000,
    routeTerminalAtMs: null,
    routeResult: null,
    snapshot,
    settledSinceMs: null
  });
  assert.equal(phaseTimeout.disposition, 'phase-timeout');

  const globalTimeout = evaluate({
    nowMs: budgets.globalMs,
    phaseStartedAtMs: budgets.globalMs - 1_000,
    phaseBudgetMs: 60_000,
    lastProgressAtMs: budgets.globalMs - 500,
    routeTerminalAtMs: null,
    routeResult: null,
    snapshot,
    settledSinceMs: null
  });
  assert.equal(globalTimeout.disposition, 'global-timeout');
});

test('timeout overrides are validated and bounded', () => {
  const overridden = loadOracleBudgets('seeded', {
    CA_ORACLE_GLOBAL_TIMEOUT_MS: '60000',
    CA_ORACLE_STALL_TIMEOUT_MS: '5000',
    CA_ORACLE_ROUTE_RETURN_TIMEOUT_MS: '12000',
    CA_ORACLE_ROUTE_SETTLE_TIMEOUT_MS: '2000'
  });
  assert.equal(overridden.globalMs, 60_000);
  assert.equal(overridden.stallMs, 5_000);
  assert.equal(overridden.routeSettleMs, 2_000);
  assert.throws(() => loadOracleBudgets('seeded', { CA_ORACLE_GLOBAL_TIMEOUT_MS: 'forever' }), /integer/);
  assert.throws(() => loadOracleBudgets('seeded', { CA_ORACLE_ROUTE_SETTLE_TIMEOUT_MS: '999999' }), /between/);
  assert.throws(() => loadOracleBudgets('seeded', {
    CA_ORACLE_GLOBAL_TIMEOUT_MS: '30000',
    CA_ORACLE_STALL_TIMEOUT_MS: '30000'
  }), /shorter/);
});

test('fingerprints change only when observed progress changes', () => {
  const snapshot = completeSnapshot();
  const first = createLifecycleFingerprint(snapshot, ['autosave-complete'], 4);
  const same = createLifecycleFingerprint(snapshot, ['autosave-complete'], 4);
  const changed = createLifecycleFingerprint(snapshot, ['autosave-complete', 'route-generation-complete'], 4);
  assert.equal(first, same);
  assert.notEqual(first, changed);
});

test('diagnostic snapshots retain lifecycle and bounded pending requests', () => {
  const pending = Array.from({ length: 30 }, (_, index) => ({ url: `https://example/${index}` }));
  const diagnostic = createDiagnosticSnapshot({
    phase: 'route-settlement',
    disposition: 'wait',
    nowMs: 12_345.4,
    lastProgressAtMs: 12_000.2,
    progressFingerprint: 'fingerprint',
    lifecycle: completeSnapshot(),
    markerNames: REQUIRED_MARKERS,
    routeResult: { workflowStatus: 'Published', routeDecision: true },
    pendingRequests: pending,
    budgets
  });
  assert.equal(diagnostic.pendingRequests.length, 25);
  assert.equal(diagnostic.lifecycle.data.routeValidity, 'Current');
  assert.deepEqual(diagnostic.missingGates, []);
  assert.equal(diagnostic.elapsedMs, 12_345);
});

test('missing completion gates identify the absent route basis', () => {
  const missing = getMissingCompletionGates(completeSnapshot({
    routeValidity: 'None',
    routeHasDecision: '',
    routeBasisPlanSessionVersion: '',
    routeBasisMarketIntelligenceId: ''
  }), REQUIRED_MARKERS);
  assert.ok(missing.includes('basis-identities'));
  assert.ok(missing.includes('route-validity'));
  assert.ok(missing.includes('route-decision'));
});
