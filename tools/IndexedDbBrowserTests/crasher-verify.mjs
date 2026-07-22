import { chromium, firefox } from 'playwright';
import { rename, rm, writeFile } from 'node:fs/promises';
import {
  REQUIRED_MARKERS,
  createDiagnosticSnapshot,
  createLifecycleFingerprint,
  evaluateOracleState,
  getMissingCompletionGates,
  loadOracleBudgets
} from './workflow-oracle.mjs';

const browserName = process.argv[2];
const url = process.argv[3] ?? 'http://127.0.0.1:5083';
const plan = process.argv[4] ?? 'C:/Users/gianf/Downloads/crasher.craftplan';
const output = process.argv[5] ?? `crasher-${browserName}.json`;
const evidenceMode = process.argv[6] ?? 'seeded';
const executionMode = process.argv[7] ?? 'legacy';
const browserType = { chromium, firefox }[browserName];
if (!browserType) throw new Error(`Unsupported browser ${browserName}`);
if (!['seeded', 'live'].includes(evidenceMode)) throw new Error(`Unsupported evidence mode ${evidenceMode}`);
if (!['legacy', 'engine', 'workflow-engine'].includes(executionMode)) throw new Error(`Unsupported execution mode ${executionMode}`);
if (executionMode === 'engine' && evidenceMode === 'live') {
  throw new Error('The standalone engine acceptance probe requires seeded evidence.');
}
const isSimpleCrasher = plan.toLowerCase().includes('simplecrasher');
const expectedShape = isSimpleCrasher
  ? {
      rootCount: 4,
      nodeCount: 80,
      edgeCount: 76,
      candidateCount: 26,
      leafItemIds: '14,15,16,17,18,19,44848,45968,45969,45970,45984,45985,45986,46243,46244,46246,46252'
    }
  : {
      rootCount: 15,
      nodeCount: 88,
      edgeCount: 73,
      candidateCount: 53,
      leafItemIds: '2,3,4,7,8,9,10,13,14,16,17,5106,5111,5114,5116,5118,5119,5121,5261,5384,5491,5518,5523,5528,5530,12223,12224,12531,12534,12631,12943'
    };

const budgets = loadOracleBudgets(evidenceMode);
if (executionMode === 'workflow-engine') {
  if (!process.env.CA_ORACLE_GLOBAL_TIMEOUT_MS) budgets.globalMs = 600_000;
  if (!process.env.CA_ORACLE_ROUTE_RETURN_TIMEOUT_MS) budgets.routeReturnMs = 420_000;
  if (!process.env.CA_ORACLE_STALL_TIMEOUT_MS) budgets.stallMs = 240_000;
  if (!process.env.CA_ORACLE_BROWSER_OPERATION_TIMEOUT_MS) budgets.operationMs = 60_000;
}
if (browserName === 'firefox' && evidenceMode === 'seeded' && !process.env.CA_ORACLE_BROWSER_OPERATION_TIMEOUT_MS) {
  // Firefox can keep its content thread inside a large IndexedDB structured-clone
  // commit for longer than Chromium while still making forward progress.
  budgets.operationMs = 30_000;
}
if (browserName === 'firefox' && evidenceMode === 'seeded' && !process.env.CA_ORACLE_GLOBAL_TIMEOUT_MS) {
  // Managed WASM route enumeration is substantially slower in Firefox. Keep
  // phase and stall limits strict, but allow the complete bounded transaction.
  budgets.globalMs = 480_000;
}
if (browserName === 'firefox' && evidenceMode === 'seeded' && !process.env.CA_ORACLE_HEARTBEAT_MS) {
  budgets.heartbeatMs = 6_000;
}
if (browserName === 'firefox' && evidenceMode === 'seeded' && !process.env.CA_ORACLE_STALL_TIMEOUT_MS) {
  budgets.stallMs = 60_000;
}
const started = performance.now();
const elapsed = () => performance.now() - started;
const report = {
  browserName,
  url,
  plan,
  evidenceMode,
  executionMode,
  budgets,
  stages: [],
  console: [],
  pageErrors: [],
  requestFailures: [],
  startedAt: new Date().toISOString(),
  currentPhase: 'launching'
};
const temporaryOutput = `${output}.${process.pid}.tmp`;
let writeQueue = Promise.resolve();
let lastWriteAtMs = Number.NEGATIVE_INFINITY;
let browser;
let context;
let page;
let watchdog;
let watchdogTriggered = false;
let cleanupTimedOut = false;
let latestLifecycle = null;
let latestDisposition = 'wait';
let currentPhase = 'launching';
let phaseStartedAtMs = 0;
let lastMeaningfulProgressAtMs = 0;
let lastProgressFingerprint = '';
let lastHeartbeatAtMs = 0;
let processedConsoleEntries = 0;
let networkProgress = 0;
let settledSinceMs = null;
let routeTerminalAtMs = null;
let routeResult = null;
const seenMarkers = new Set();
const pendingRequests = new Map();

class OracleFailure extends Error {
  constructor(classification, message, details = {}) {
    super(message);
    this.name = 'OracleFailure';
    this.classification = classification;
    this.details = details;
  }
}

function stage(name, details = {}) {
  report.stages.push({ name, atMs: Math.round(elapsed()), ...details });
}

function pendingRequestSnapshot() {
  const now = elapsed();
  return [...pendingRequests.values()]
    .map(request => ({ ...request, elapsedMs: Math.round(now - request.startedAtMs) }))
    .sort((left, right) => right.elapsedMs - left.elapsedMs)
    .slice(0, 25);
}

function updateDiagnostic(disposition = latestDisposition) {
  latestDisposition = disposition;
  report.currentPhase = currentPhase;
  report.diagnostic = createDiagnosticSnapshot({
    phase: currentPhase,
    disposition,
    nowMs: elapsed(),
    lastProgressAtMs: lastMeaningfulProgressAtMs,
    progressFingerprint: lastProgressFingerprint,
    lifecycle: latestLifecycle,
    markerNames: seenMarkers,
    routeResult,
    pendingRequests: pendingRequestSnapshot(),
    budgets
  });
}

async function writeReport(force = false) {
  const now = elapsed();
  if (!force && now - lastWriteAtMs < 250) return writeQueue;
  lastWriteAtMs = now;
  updateDiagnostic();
  const serialized = JSON.stringify(report, null, 2);
  writeQueue = writeQueue.catch(() => {}).then(async () => {
    await writeFile(temporaryOutput, serialized);
    try {
      await rename(temporaryOutput, output);
    } catch (error) {
      if (error?.code !== 'EEXIST' && error?.code !== 'EPERM') throw error;
      await rm(output, { force: true });
      await rename(temporaryOutput, output);
    }
  });
  return writeQueue;
}

function setPhase(name) {
  currentPhase = name;
  phaseStartedAtMs = elapsed();
  lastMeaningfulProgressAtMs = phaseStartedAtMs;
  lastProgressFingerprint = '';
  settledSinceMs = null;
  report.currentPhase = name;
  stage(`phase:${name}`);
}

function remainingGlobalMs() {
  return Math.max(1, budgets.globalMs - elapsed());
}

async function withDeadline(label, promiseOrFactory, timeoutMs = budgets.operationMs) {
  const effectiveTimeout = Math.max(1, Math.min(timeoutMs, remainingGlobalMs()));
  let timer;
  try {
    const operation = typeof promiseOrFactory === 'function' ? promiseOrFactory() : promiseOrFactory;
    return await Promise.race([
      operation,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new OracleFailure(
          'browser-operation-timeout',
          `${label} exceeded ${effectiveTimeout}ms`,
          { label, phase: currentPhase, lifecycle: latestLifecycle, pendingRequests: pendingRequestSnapshot() })), effectiveTimeout);
      })
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function closeLaunchedBrowser() {
  if (!browser) return true;
  let timer;
  try {
    await Promise.race([
      browser.close(),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`browser.close exceeded ${budgets.closeMs}ms`)), budgets.closeMs);
      })
    ]);
    return true;
  } catch (error) {
    cleanupTimedOut = true;
    report.cleanupError = error.stack || String(error);
    return false;
  } finally {
    clearTimeout(timer);
  }
}

function observeConsoleProgress() {
  let changed = false;
  for (; processedConsoleEntries < report.console.length; processedConsoleEntries++) {
    const entry = report.console[processedConsoleEntries];
    const text = entry.text;
    const markerPairs = [
      ['hot-state publication applied', 'analysis-publication-complete'],
      ['autosave complete', 'autosave-complete'],
      ['explicit route generation starting', 'route-generation-started'],
      ['explicit route workflow returned', 'route-generation-complete']
    ];
    for (const [needle, name] of markerPairs) {
      if (text.includes(needle) && !seenMarkers.has(name)) {
        seenMarkers.add(name);
        report.stages.push({ name, atMs: Math.round(entry.atMs) });
        changed = true;
      }
    }

    const execution = text.match(/route execution returned \(plans=(\d+), routeDecision=(True|False), activeItems=(\d+)\)/i);
    if (execution) {
      routeResult = {
        ...routeResult,
        executionAtMs: entry.atMs,
        planCount: Number(execution[1]),
        routeDecision: execution[2].toLowerCase() === 'true',
        activeItemCount: Number(execution[3])
      };
      changed = true;
    }
    const workflow = text.match(/explicit route workflow returned \(status=([^,\)]+), plans=(\d+)\)/i);
    if (workflow) {
      routeTerminalAtMs = entry.atMs;
      routeResult = {
        ...routeResult,
        workflowAtMs: entry.atMs,
        workflowStatus: workflow[1],
        shoppingPlanCount: Number(workflow[2])
      };
      changed = true;
    }
  }
  return changed;
}

async function readLifecycle() {
  latestLifecycle = await withDeadline('read lifecycle', () => page.evaluate(async () => {
    const probe = document.querySelector('[data-benchmark-id="operation-lifecycle"]');
    const data = probe ? { ...probe.dataset } : null;
    const saved = await window.IndexedDB.loadPlan('autosave');
    const text = document.body?.innerText || '';
    const statuses = Array.from(document.querySelectorAll('[role="status"], .mud-alert-message, .mud-progress-linear'))
      .map(element => (element.textContent || '').trim()).filter(Boolean);
    return {
      data,
      statuses,
      analyzingVisible: /\bANALYZING\b/i.test(text),
      visibleBusy: Array.from(document.querySelectorAll('.mud-progress-linear, .mud-progress-circular'))
        .some(element => { const rect = element.getBoundingClientRect(); return rect.width > 0 && rect.height > 0; }),
      autosave: saved ? {
        id: saved.id,
        modifiedAt: saved.modifiedAt,
        projectItemCount: saved.projectItems?.length || 0,
        hasPlan: Boolean(saved.planJson),
        hasMarketIntelligence: Boolean(saved.marketIntelligenceJson)
      } : null
    };
  }), budgets.operationMs);
  return latestLifecycle;
}

async function observeProgress(snapshot, forceWrite = false) {
  const consoleChanged = observeConsoleProgress();
  const fingerprint = createLifecycleFingerprint(snapshot, seenMarkers, networkProgress);
  const now = elapsed();
  if (consoleChanged || fingerprint !== lastProgressFingerprint) {
    lastProgressFingerprint = fingerprint;
    lastMeaningfulProgressAtMs = now;
    updateDiagnostic('wait');
    await writeReport(forceWrite);
    return true;
  }
  if (now - lastHeartbeatAtMs >= budgets.heartbeatMs) {
    lastHeartbeatAtMs = now;
    updateDiagnostic('wait');
    await writeReport(true);
  }
  return false;
}

function throwIfPhaseExpired(phaseBudgetMs, snapshot) {
  const now = elapsed();
  const status = (snapshot?.statuses || []).join(' | ');
  const data = snapshot?.data || {};
  if (routeResult?.workflowStatus && routeResult.workflowStatus !== 'Published') {
    throw new OracleFailure(
      'terminal-failure',
      `Procurement workflow returned ${routeResult.workflowStatus}.`,
      { lifecycle: snapshot, routeResult });
  }
  if (data.routeFailure || /suspect cache|failed|could not|unhandled exception/i.test(status)) {
    throw new OracleFailure('terminal-failure', data.routeFailure || status, { lifecycle: snapshot });
  }
  if (now >= budgets.globalMs) {
    throw new OracleFailure('global-timeout', `Overall oracle budget exceeded during ${currentPhase}`, { lifecycle: snapshot });
  }
  if (now - phaseStartedAtMs >= phaseBudgetMs) {
    throw new OracleFailure('phase-timeout', `${currentPhase} exceeded its ${phaseBudgetMs}ms budget`, { lifecycle: snapshot });
  }
  if (now - lastMeaningfulProgressAtMs >= budgets.stallMs) {
    throw new OracleFailure('stalled', `No meaningful progress during ${currentPhase} for ${budgets.stallMs}ms`, { lifecycle: snapshot });
  }
}

async function waitForLifecycle(phase, phaseBudgetMs, predicate) {
  setPhase(phase);
  while (true) {
    const snapshot = await readLifecycle();
    await observeProgress(snapshot);
    if (predicate(snapshot)) return snapshot;
    throwIfPhaseExpired(phaseBudgetMs, snapshot);
    await page.waitForTimeout(100);
  }
}

async function waitForFullSettlement() {
  setPhase('route-return');
  while (true) {
    const snapshot = await readLifecycle();
    await observeProgress(snapshot);
    const now = elapsed();
    if (routeTerminalAtMs != null && currentPhase === 'route-return') {
      setPhase('route-settlement');
      phaseStartedAtMs = routeTerminalAtMs;
      await writeReport(true);
    }

    const missingGates = getMissingCompletionGates(snapshot, seenMarkers);
    if (missingGates.length === 0) {
      settledSinceMs ??= now;
    } else {
      settledSinceMs = null;
    }

    const phaseBudgetMs = currentPhase === 'route-return' ? budgets.routeReturnMs : budgets.routeSettleMs;
    const disposition = evaluateOracleState({
      nowMs: now,
      globalStartedAtMs: 0,
      phase: currentPhase,
      phaseStartedAtMs,
      phaseBudgetMs,
      lastProgressAtMs: lastMeaningfulProgressAtMs,
      routeTerminalAtMs,
      settledSinceMs,
      snapshot,
      markerNames: seenMarkers,
      routeResult,
      budgets
    });
    updateDiagnostic(disposition.disposition);
    if (disposition.disposition === 'complete') {
      stage('full-operation-settled', { lifecycle: snapshot });
      report.completed = true;
      report.lifecycle = snapshot;
      await writeReport(true);
      return snapshot;
    }
    if (disposition.disposition !== 'wait') {
      throw new OracleFailure(disposition.disposition, disposition.reason, {
        missingGates: disposition.missingGates,
        lifecycle: snapshot,
        routeResult
      });
    }
    await page.waitForTimeout(250);
  }
}

watchdog = setTimeout(() => {
  void (async () => {
    if (watchdogTriggered) return;
    watchdogTriggered = true;
    report.failure = {
      classification: 'global-watchdog',
      message: `Hard watchdog exceeded ${budgets.globalMs}ms`,
      phase: currentPhase,
      lifecycle: latestLifecycle,
      pendingRequests: pendingRequestSnapshot()
    };
    report.error = report.failure.message;
    report.durationMs = Math.round(elapsed());
    updateDiagnostic('global-watchdog');
    try { await writeReport(true); } catch {}
    await closeLaunchedBrowser();
    try { await writeReport(true); } catch {}
    process.exit(1);
  })();
}, budgets.globalMs + 1_000);

workflow: try {
  browser = await withDeadline('launch browser', () => browserType.launch({ headless: true }), Math.min(60_000, budgets.importMs));
  context = await withDeadline('create browser context', () => browser.newContext());
  page = await withDeadline('create browser page', () => context.newPage());
  page.on('console', message => report.console.push({ type: message.type(), text: message.text(), atMs: elapsed() }));
  page.on('pageerror', error => report.pageErrors.push({ message: error.message, stack: error.stack, atMs: elapsed() }));
  page.on('request', request => pendingRequests.set(request, {
    method: request.method(),
    url: request.url(),
    startedAtMs: elapsed()
  }));
  page.on('requestfinished', request => {
    if (pendingRequests.delete(request)) networkProgress++;
  });
  page.on('requestfailed', request => {
    pendingRequests.delete(request);
    networkProgress++;
    report.requestFailures.push({ url: request.url(), error: request.failure()?.errorText, atMs: elapsed() });
  });

  if (evidenceMode === 'seeded') {
    await page.route('https://universalis.app/api/v2/**', async route => {
      const parsed = new URL(route.request().url());
      const segments = parsed.pathname.split('/').filter(Boolean);
      if (segments.at(-1) === 'worlds') {
        await route.fulfill({ json: [{ id: 73, name: 'Adamantoise' }, { id: 91, name: 'Balmung' }, { id: 78, name: 'Behemoth' }, { id: 406, name: 'Halicarnassus' }] });
        return;
      }
      if (segments.at(-1) === 'data-centers') {
        await route.fulfill({ json: [
          { name: 'Aether', region: 'North-America', worlds: [73] },
          { name: 'Crystal', region: 'North-America', worlds: [91] },
          { name: 'Primal', region: 'North-America', worlds: [78] },
          { name: 'Dynamis', region: 'North-America', worlds: [406] }
        ] });
        return;
      }
      const dataCenter = decodeURIComponent(segments.at(-2));
      const ids = segments.at(-1).split(',').map(Number);
      const worldByDc = {
        Aether: ['Adamantoise', 73], Crystal: ['Balmung', 91],
        Primal: ['Behemoth', 78], Dynamis: ['Halicarnassus', 406]
      };
      const [worldName, worldId] = worldByDc[dataCenter] || ['Adamantoise', 73];
      const now = Math.floor(Date.now() / 1000);
      const item = itemId => ({
        itemID: itemId,
        dcName: dataCenter,
        lastUploadTime: now * 1000,
        worldUploadTimes: { [worldId]: now * 1000 },
        listings: [
          {
            pricePerUnit: 100 + itemId % 50,
            quantity: 10000000,
            worldName,
            dataCenterName: dataCenter,
            retainerName: `Fixture-NQ-${worldName}`,
            hq: false,
            lastReviewTime: now
          },
          {
            pricePerUnit: 110 + itemId % 50,
            quantity: 10000000,
            worldName,
            dataCenterName: dataCenter,
            retainerName: `Fixture-HQ-${worldName}`,
            hq: true,
            lastReviewTime: now
          }
        ],
        averagePrice: 100 + itemId % 50,
        averagePriceNQ: 100 + itemId % 50,
        minPrice: 100 + itemId % 50,
        minPriceNQ: 100 + itemId % 50
      });
      await route.fulfill({ json: ids.length === 1 ? item(ids[0]) : {
        itemIDs: ids,
        items: Object.fromEntries(ids.map(id => [id, item(id)]))
      } });
    });
  } else {
    await page.route('https://universalis.app/api/v2/**', async route => {
      const response = await route.fetch();
      await route.fulfill({
        response,
        headers: { ...response.headers(), 'access-control-allow-origin': '*' }
      });
    });
  }

  if (executionMode !== 'legacy') {
    await page.route('**/appsettings.json', async route => {
      const response = await route.fetch();
      const settings = await response.json();
      settings.EngineRewrite = { ExecutionEnabled: true };
      settings.EngineAcceptance = {
        Enabled: true,
        UseDeterministicEvidence: true
      };
      if (executionMode === 'workflow-engine') {
        settings.ProcurementRoutes = { GenerationEnabled: true };
      }
      await route.fulfill({ response, json: settings });
    });
  }

  setPhase('application-startup');
  await withDeadline('navigate to application', () => page.goto(`${url}?benchmark-defer-route=1`, { waitUntil: 'networkidle' }), budgets.importMs);
  await withDeadline('wait for IndexedDB module', () => page.waitForFunction(() => window.IndexedDB?.moduleRevision === 16), budgets.importMs);
  stage('app-ready', { moduleRevision: await withDeadline('read IndexedDB module revision', () => page.evaluate(() => window.IndexedDB.moduleRevision)) });
  await withDeadline('enable benchmark settings', () => page.evaluate(async () => {
    await window.IndexedDB.saveSetting('debug.secret_tools_enabled', 'true');
    await window.IndexedDB.saveSetting('debug.defer_automatic_route_reconciliation', 'true');
  }));
  await withDeadline('reload benchmark settings', () => page.reload({ waitUntil: 'networkidle' }), budgets.importMs);
  await withDeadline('clear market cache', () => page.evaluate(() => window.IndexedDB.clearMarketCache()));
  if (evidenceMode === 'seeded') stage('deterministic-market-evidence-route-active');
  await writeReport(true);

  await withDeadline('open import menu', () => page.locator('[data-benchmark-id="main-import-menu"]').click());
  await withDeadline('choose native plan import', () => page.locator('[data-benchmark-id="main-import-native-plan"]').click());
  await withDeadline('select native plan file', () => page.locator('#nativeFileInput').setInputFiles(plan));
  const importButton = page.getByRole('dialog').getByRole('button', { name: 'Import', exact: true });
  await withDeadline('wait for import button', () => importButton.waitFor({ state: 'visible' }));
  await withDeadline('wait for import validation', () => page.waitForFunction(() => {
    const dialog = document.querySelector('[role="dialog"]');
    return Array.from(dialog?.querySelectorAll('button') || [])
      .some(button => (button.textContent || '').trim().toLowerCase() === 'import' && !button.disabled);
  }), budgets.importMs);
  stage('before-import-click');
  await withDeadline('click import', () => importButton.click());
  stage('after-import-click');
  const importedLifecycle = await waitForLifecycle('import-and-expansion', budgets.importMs, snapshot => {
    const data = snapshot?.data;
    return data &&
      Number(data.planRootCount) === expectedShape.rootCount &&
      Number(data.planNodeCount) === expectedShape.nodeCount &&
      Number(data.planEdgeCount) === expectedShape.edgeCount &&
      Number(data.marketCandidateCount) === expectedShape.candidateCount &&
      data.planLeafItemIds === expectedShape.leafItemIds;
  });
  stage('plan-imported-and-recipe-graph-expanded', {
    rootCount: Number(importedLifecycle.data.planRootCount),
    nodeCount: Number(importedLifecycle.data.planNodeCount),
    edgeCount: Number(importedLifecycle.data.planEdgeCount),
    candidateCount: Number(importedLifecycle.data.marketCandidateCount),
    leafItemIds: importedLifecycle.data.planLeafItemIds,
    marketAnalysisCount: Number(importedLifecycle.data.marketAnalysisCount)
  });
  const importSettled = await waitForLifecycle('import-activation-settle', budgets.importMs, snapshot => {
    const data = snapshot?.data;
    return data &&
      data.isBusy === 'false' &&
      !data.currentOperation &&
      !data.activeWorkflows &&
      Number(data.cacheFetchedPairs) > 0;
  });
  stage('plan-import-activation-settled', { lifecycle: importSettled });
  await writeReport(true);

  if (executionMode === 'legacy' || evidenceMode === 'live') {
  await withDeadline('open market analysis', () => page.getByRole('button', { name: 'Market Analysis', exact: true }).click());
  const analysisButton = page.locator('[data-benchmark-id="market-analysis-run"]');
  await withDeadline('wait for market analysis button', () => analysisButton.waitFor({ state: 'visible' }));
  await withDeadline('wait for enabled market analysis button', () => page.waitForFunction(() => {
    const button = document.querySelector('[data-benchmark-id="market-analysis-run"]');
    return button && !button.disabled && button.dataset.canAnalyze === 'true' && button.dataset.isAnalyzing === 'false';
  }));
  const analysisButtonState = await withDeadline('read market analysis button state', () => analysisButton.evaluate(button => ({
    disabled: button.disabled,
    canAnalyze: button.dataset.canAnalyze,
    isAnalyzing: button.dataset.isAnalyzing,
    text: button.textContent?.trim()
  })));
  stage('before-explicit-analysis-click', { button: analysisButtonState });
  await withDeadline('click market analysis', () => analysisButton.click());
  const postClickLifecycle = await waitForLifecycle('analysis-operation-start', budgets.operationMs, snapshot => {
    const data = snapshot?.data;
    return data &&
      (data.currentOperation === 'Market Analysis' ||
       String(data.activeWorkflows).includes('MarketAnalysis') ||
       data.isBusy === 'true');
  });
  stage('after-explicit-analysis-click', { lifecycle: postClickLifecycle });
  await waitForLifecycle('analysis-publication', budgets.analysisMs, () => {
    observeConsoleProgress();
    return seenMarkers.has('autosave-complete');
  });
  stage('explicit-analysis-published');
  await writeReport(true);

  if (evidenceMode === 'live') {
    const liveSnapshot = await readLifecycle();
    const liveData = liveSnapshot?.data || {};
    if (liveData.publicationKind !== 'Known' ||
        Number(liveData.marketAnalysisCount) <= 0 ||
        liveData.isBusy !== 'false' ||
        liveData.activeWorkflows ||
        !liveSnapshot.autosave?.hasMarketIntelligence) {
      throw new OracleFailure('terminal-contradiction', 'Live-network analysis did not settle durably.', {
        lifecycle: liveSnapshot
      });
    }
    report.liveSmoke = {
      scope: 'network fetch, cache, full-graph analysis, publication, and autosave',
      lifecycle: liveSnapshot
    };
    stage('live-network-smoke-complete');
    if (executionMode === 'legacy') {
      report.completed = true;
      report.durationMs = Math.round(elapsed());
      report.finishedAt = new Date().toISOString();
      await writeReport(true);
      break workflow;
    }
  }
  } else {
    stage('test-only-deterministic-engine-evidence-selected');
  }

  if (executionMode === 'workflow-engine' && evidenceMode === 'seeded') {
    await withDeadline('seed deterministic workflow evidence', () =>
      page.locator('[data-benchmark-id="engine-acceptance-seed-evidence"]').click());
    await waitForLifecycle('workflow-engine-evidence-publication', budgets.analysisMs, snapshot => {
      const data = snapshot?.data;
      return data && Number(data.marketAnalysisCount) > 0 && snapshot.autosave?.hasMarketIntelligence;
    });
    stage('workflow-engine-evidence-published');
  }

  if (evidenceMode === 'seeded' && executionMode === 'legacy') {
    await withDeadline('lock acquisition decisions', () => page.$eval(
      '[data-benchmark-id="lock-current-acquisition-decisions"]', element => element.click()));
    stage('deterministic-acquisition-decisions-locked');
  }

  if (executionMode === 'engine') {
    setPhase('engine-worker-transaction');
    const probe = page.locator('[data-benchmark-id="engine-transaction-acceptance"]');
    await withDeadline('wait for engine acceptance probe', () => probe.waitFor({ state: 'attached' }));
    await withDeadline('start page heartbeat', () => page.evaluate(() => {
      const heartbeat = {
        workerCount: 0,
        workerLastAt: 0,
        workerMaxGapMs: 0,
        workerMeasuring: false,
        finalizationCount: 0,
        finalizationLastAt: 0,
        finalizationMaxGapMs: 0,
        finalizationMeasuring: false,
        timer: null
      };
      window.addEventListener('craft-architect-engine-worker-progress', () => {
        if (!heartbeat.workerMeasuring) {
          heartbeat.workerMeasuring = true;
          heartbeat.workerLastAt = performance.now();
        }
      });
      window.addEventListener('craft-architect-engine-worker-complete', () => {
        heartbeat.workerMeasuring = false;
        heartbeat.finalizationMeasuring = true;
        heartbeat.finalizationLastAt = performance.now();
      });
      window.addEventListener('craft-architect-engine-host-finalized', () => {
        heartbeat.finalizationMeasuring = false;
      });
      heartbeat.timer = setInterval(() => {
        const now = performance.now();
        if (heartbeat.workerMeasuring) {
          heartbeat.workerMaxGapMs = Math.max(heartbeat.workerMaxGapMs, now - heartbeat.workerLastAt);
          heartbeat.workerLastAt = now;
          heartbeat.workerCount++;
        }
        if (heartbeat.finalizationMeasuring) {
          heartbeat.finalizationMaxGapMs = Math.max(
            heartbeat.finalizationMaxGapMs,
            now - heartbeat.finalizationLastAt);
          heartbeat.finalizationLastAt = now;
          heartbeat.finalizationCount++;
        }
      }, 50);
      window.__engineTransactionHeartbeat = heartbeat;
    }));
    await withDeadline('start engine Worker transaction', () =>
      page.locator('[data-benchmark-id="engine-transaction-run"]').evaluate(element => element.click()));
    await withDeadline('wait for engine Worker transaction', () => page.waitForFunction(() => {
      const element = document.querySelector('[data-benchmark-id="engine-transaction-acceptance"]');
      return element?.dataset.status === 'complete' || element?.dataset.status === 'failed';
    }, null, { timeout: budgets.globalMs - elapsed() }), budgets.globalMs - elapsed());
    const engineEvidence = await probe.evaluate(element => ({ ...element.dataset }));
    engineEvidence.heartbeat = await withDeadline('read page heartbeat', () => page.evaluate(() => {
      const heartbeat = window.__engineTransactionHeartbeat;
      clearInterval(heartbeat?.timer);
      if (!heartbeat) return null;
      if (heartbeat.finalizationMeasuring) {
        heartbeat.finalizationMaxGapMs = Math.max(
          heartbeat.finalizationMaxGapMs,
          performance.now() - heartbeat.finalizationLastAt);
      }
      return {
        workerCount: heartbeat.workerCount,
        workerMaxGapMs: Math.round(heartbeat.workerMaxGapMs),
        finalizationCount: heartbeat.finalizationCount,
        finalizationMaxGapMs: Math.round(heartbeat.finalizationMaxGapMs)
      };
    }));
    if (engineEvidence.status !== 'complete') {
      throw new OracleFailure('engine-transaction-failed', engineEvidence.error || 'Engine transaction failed.', {
        engineEvidence
      });
    }
    if (engineEvidence.terminalStatus !== 'Succeeded' ||
        engineEvidence.replayMatched !== 'true' ||
        engineEvidence.routeValidity !== 'Current' ||
        engineEvidence.routeHasDecision !== 'true' ||
        engineEvidence.acquisitionTruncated !== 'true' ||
        engineEvidence.routeTruncated !== 'true' ||
        engineEvidence.travelTruncated !== 'true' ||
        Number(engineEvidence.travelRoutesEvaluated) <= 0 ||
        Number(engineEvidence.travelRoutesEvaluated) > 8 ||
        !engineEvidence.heartbeat ||
        engineEvidence.heartbeat.workerCount < 10 ||
        engineEvidence.heartbeat.workerMaxGapMs > budgets.heartbeatMs ||
        engineEvidence.heartbeat.finalizationCount < 1 ||
        engineEvidence.heartbeat.finalizationMaxGapMs > budgets.heartbeatMs ||
        Number(engineEvidence.acquisitionCombinationEvaluations) <= 0 ||
        Number(engineEvidence.acquisitionCombinationEvaluations) >
          Number(importedLifecycle.data.planNodeCount) * 2_048) {
      throw new OracleFailure('engine-settlement-contradiction', 'Engine transaction evidence is incomplete.', {
        engineEvidence
      });
    }
    routeResult = {
      workflowStatus: 'Published',
      routeDecision: true,
      engineEvidence
    };
    report.engineEvidence = engineEvidence;
    stage('engine-worker-transaction-settled', { engineEvidence });
    await writeReport(true);
  } else {
    await withDeadline('open procurement plan', () => page.getByRole('button', { name: 'Procurement Plan', exact: true }).click());
    const routeButton = page.locator('.pp-primary-action');
    await withDeadline('wait for route button', () => routeButton.waitFor({ state: 'visible' }));
    await withDeadline('start route generation', () => routeButton.click());
    stage('explicit-route-generation-started');
    await writeReport(true);
    if (executionMode === 'workflow-engine') {
      const settled = await waitForLifecycle('workflow-engine-route-settlement', budgets.routeReturnMs, snapshot => {
        const data = snapshot?.data || {};
        return routeResult?.workflowStatus === 'Published' &&
          data.routeValidity === 'Current' &&
          data.routeHasDecision === 'true' &&
          data.isBusy === 'false' &&
          !data.activeWorkflows &&
          data.dirtyPersistedBuckets === 'None' &&
          snapshot.autosave?.hasMarketIntelligence;
      });
      stage('full-operation-settled', { lifecycle: settled });
      report.completed = true;
      report.lifecycle = settled;
      await writeReport(true);
    } else {
      await waitForFullSettlement();
    }
  }

  setPhase('post-completion-navigation');
  const interactionStarted = performance.now();
  await withDeadline('open completed procurement route', () => page.getByRole('button', { name: 'Procurement Plan', exact: true }).click(), budgets.navigationMs);
  await withDeadline('wait for procurement route title', () => page.locator('#procurement-route-title').waitFor({ state: 'visible' }), budgets.navigationMs);
  await withDeadline('wait for route generation banner to clear', () => page.waitForFunction(
    () => !/Generating route|Updating Route/i.test(document.body?.innerText || '')), budgets.navigationMs);
  stage('procurement-route-visible', { responseMs: Math.round(performance.now() - interactionStarted) });
  await withDeadline('navigate to recipe planner', () => page.getByRole('button', { name: 'Recipe Planner', exact: true }).click(), budgets.navigationMs);
  await withDeadline('navigate back to procurement plan', () => page.getByRole('button', { name: 'Procurement Plan', exact: true }).click(), budgets.navigationMs);
  await withDeadline('reopen procurement route', () => page.locator('#procurement-route-title').waitFor({ state: 'visible' }), budgets.navigationMs);
  stage('post-completion-navigation-complete');
  await writeReport(true);

  setPhase('reload-restoration');
  if (executionMode === 'legacy') {
    await withDeadline('reenable automatic route reconciliation', () => page.evaluate(
      () => window.IndexedDB.saveSetting('debug.defer_automatic_route_reconciliation', 'false')));
  }
  await withDeadline('reload application', () => page.goto(url, { waitUntil: 'networkidle' }), budgets.reloadMs);
  await withDeadline('wait for startup restoration', () => page.locator('.startup-overlay').waitFor({
    state: 'detached',
    timeout: budgets.reloadMs
  }), budgets.reloadMs);
  await withDeadline('open restored procurement plan', () => page.getByRole('button', { name: 'Procurement Plan', exact: true }).click(), budgets.reloadMs);
  await withDeadline('wait for restored procurement route title', () => page.locator('#procurement-route-title').waitFor({ state: 'visible' }), budgets.reloadMs);
  const restored = await waitForLifecycle('reload-restoration', budgets.reloadMs, snapshot => {
    const data = snapshot?.data || {};
    return data.routeValidity === 'Current' &&
      data.routeHasDecision === 'true' &&
      data.routeReconciling === 'false' &&
      data.routeReconciliationScheduled === 'false' &&
      data.isBusy === 'false' &&
      !data.activeWorkflows &&
      data.planSessionVersion === data.routeBasisPlanSessionVersion &&
      data.marketIntelligenceId === data.routeBasisMarketIntelligenceId;
  });
  stage('autosave-restored-and-route-regenerated-after-reload', { lifecycle: restored });
  report.finalBodyPreview = (await withDeadline('read final body', () => page.locator('body').innerText())).slice(0, 4000);
  report.transientConsoleDiagnostics = report.console.filter(entry =>
    evidenceMode === 'live' &&
    (entry.type === 'error' || entry.type === 'warning') &&
    /status of 429|Rate limited \(429\)|Retry \d+\/\d+ for chunk/i.test(entry.text));
  report.consoleErrors = report.console.filter(entry =>
    (entry.type === 'error' || entry.type === 'warning') &&
    !report.transientConsoleDiagnostics.includes(entry));
  const criticalRequestFailures = report.requestFailures.filter(entry => !/favicon|fonts\.googleapis|fonts\.gstatic/i.test(entry.url));
  if (report.pageErrors.length > 0 || report.consoleErrors.length > 0 || criticalRequestFailures.length > 0) {
    throw new OracleFailure('browser-diagnostics', 'Browser diagnostics were not clean', {
      pageErrors: report.pageErrors,
      consoleErrors: report.consoleErrors,
      requestFailures: criticalRequestFailures
    });
  }
  report.durationMs = Math.round(elapsed());
  report.finishedAt = new Date().toISOString();
  report.finalDisposition = 'complete';
} catch (error) {
  const classification = error.classification || 'unexpected-error';
  report.failure = {
    classification,
    message: error.message || String(error),
    phase: currentPhase,
    details: error.details || {},
    lifecycle: latestLifecycle,
    routeResult,
    pendingRequests: pendingRequestSnapshot()
  };
  report.error = error.stack || String(error);
  report.durationMs = Math.round(elapsed());
  report.finalDisposition = classification;
  updateDiagnostic(classification);
} finally {
  clearTimeout(watchdog);
  const closed = await closeLaunchedBrowser();
  report.browserClosed = closed;
  report.finishedAt ??= new Date().toISOString();
  report.durationMs ??= Math.round(elapsed());
  await writeReport(true);
}

if (report.error) {
  console.error(`${report.failure?.classification || 'error'}: ${report.failure?.message || report.error}`);
  if (cleanupTimedOut) process.exit(1);
  process.exitCode = 1;
} else {
  console.log(JSON.stringify({
    browserName,
    durationMs: report.durationMs,
    stages: report.stages,
    pageErrors: report.pageErrors
  }, null, 2));
}
