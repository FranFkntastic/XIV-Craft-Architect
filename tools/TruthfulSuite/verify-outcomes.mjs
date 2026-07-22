import { createHash } from 'node:crypto';
import { appendFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyArtifact } from './truthful-artifact.mjs';

const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const GIT_SHA_PATTERN = /^[0-9a-f]{40}$/;
const SCENARIO_ASSERTIONS = {
  'indexeddb-empty-current': [
    'production-module-loaded', 'empty-database-created-at-current-schema',
    'current-store-contract-present', 'market-timestamp-index-present',
    'setting-sentinel-durable-after-current-reopen', 'plan-sentinel-durable-after-current-reopen',
    'current-schema-stable-after-reload', 'browser-diagnostics-clean'
  ],
  'indexeddb-v3-upgrade': [
    'historical-v3-fixture-seeded', 'production-module-upgraded-historical-schema',
    'historical-plan-sentinel-survived', 'historical-setting-sentinel-survived',
    'historical-market-record-survived', 'historical-plan-summary-rebuilt',
    'upgraded-market-timestamp-index-present', 'browser-diagnostics-clean'
  ],
  'production-procurement-kill-switch': [
    'production-kill-switch-config-loaded', 'native-plan-imported-through-visible-flow',
    'explicit-market-analysis-published', 'market-analysis-durable-in-autosave',
    'acquisition-evaluation-available-with-kill-switch', 'procurement-route-control-visibly-disabled',
    'disabled-route-action-does-not-execute', 'name-first-item-search-returned-product-result',
    'name-first-item-selection-updated-project', 'ordinary-navigation-remained-usable',
    'reload-restored-imported-plan', 'reload-restored-market-analysis',
    'manual-acquisition-choice-remained-usable', 'no-route-execution-observed-through-final-interaction',
    'no-worker-request-observed', 'no-unexpected-external-request-observed', 'browser-diagnostics-clean'
  ]
};
const REQUIRED_COMMANDS = {
  'suite-structure': {
    executable: 'pwsh', cwd: '.', timeoutSeconds: 120,
    arguments: ['-NoProfile', '-File', 'scripts/Assert-TruthfulTestSuite.ps1']
  },
  'dependency-audit': {
    executable: 'node', cwd: '.', timeoutSeconds: 300,
    arguments: ['tools/TruthfulSuite/check-dependencies.mjs']
  },
  'solution-build': {
    executable: 'dotnet', cwd: '.', timeoutSeconds: 600,
    arguments: ['build', 'FFXIV Craft Architect.sln', '--configuration', 'Release', '--no-restore']
  },
  'spec-tests': {
    executable: 'dotnet', cwd: '.', timeoutSeconds: 600,
    arguments: ['test', 'src/FFXIV Craft Architect.SpecTests/FFXIV Craft Architect.SpecTests.csproj',
      '--configuration', 'Release', '--no-build', '--no-restore', '--blame-hang',
      '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'mini',
      '--logger', 'trx;LogFileName=spec-tests.trx', '--logger', 'console;verbosity=normal']
  },
  'contract-tests': {
    executable: 'dotnet', cwd: '.', timeoutSeconds: 600,
    arguments: ['test', 'src/FFXIV Craft Architect.ContractTests/FFXIV Craft Architect.ContractTests.csproj',
      '--configuration', 'Release', '--no-build', '--no-restore', '--blame-hang',
      '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'mini',
      '--logger', 'trx;LogFileName=contract-tests.trx', '--logger', 'console;verbosity=normal']
  },
  'web-publish': {
    executable: 'dotnet', cwd: '.', timeoutSeconds: 600,
    arguments: buildManifest => ['publish', 'src/FFXIV Craft Architect.Web/FFXIV Craft Architect.Web.csproj',
      '--configuration', 'Release', '--output', 'dist/publish', '--no-restore',
      `-p:BuildInfoBranchName=${buildManifest.source.ref}`]
  },
  'product-kill-switch': {
    executable: 'node', cwd: '.', timeoutSeconds: 60,
    arguments: buildManifest => ['tools/TruthfulSuite/check-product.mjs',
      'dist/subject/src/FFXIV Craft Architect.Web/wwwroot', buildManifest.target.domain]
  },
  'deterministic-browser-tests': {
    executable: 'npm', cwd: 'dist/subject/tools/IndexedDbBrowserTests', timeoutSeconds: 600,
    arguments: ['test', '--', '--web-root', '../../src/FFXIV Craft Architect.Web/wwwroot',
      '--output', '../../../evidence/browser-truth-report.json']
  }
};

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function canonicalJson(value) {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function requiredOption(options, name) {
  const value = options[name];
  if (!value) throw new Error(`Missing --${name}.`);
  return value;
}

function parseOptions(args) {
  const options = {};
  for (let index = 0; index < args.length; index += 2) {
    const name = args[index];
    const value = args[index + 1];
    if (!name?.startsWith('--') || value === undefined) throw new Error(`Malformed option: ${name}`);
    options[name.slice(2)] = value;
  }
  return options;
}

function validateOutcome(outcome, expected, buildManifest) {
  const subjectSha = expected.subjectKind === 'source'
    ? buildManifest.source.commitSha
    : buildManifest.artifact.archiveSha256;
  if (outcome?.schemaVersion !== 1 || outcome?.id !== expected.id || outcome?.status !== 'passed') {
    throw new Error(`Required outcome did not pass: ${expected.id}`);
  }
  if (outcome?.run?.id !== buildManifest.run.id || outcome?.run?.attempt !== buildManifest.run.attempt ||
      outcome?.sourceCommitSha !== buildManifest.source.commitSha) {
    throw new Error(`Required outcome is stale or foreign: ${expected.id}`);
  }
  const identityPattern = expected.subjectKind === 'source' ? GIT_SHA_PATTERN : SHA256_PATTERN;
  if (outcome?.subject?.kind !== expected.subjectKind || outcome?.subject?.identity !== subjectSha ||
      !identityPattern.test(outcome.subject.identity)) {
    throw new Error(`Required outcome has wrong subject: ${expected.id}`);
  }
  if (outcome?.exitCode !== 0 || outcome?.signal !== null) {
    throw new Error(`Required outcome has nonterminal process evidence: ${expected.id}`);
  }
  const requiredCommand = REQUIRED_COMMANDS[expected.id];
  const requiredArguments = typeof requiredCommand?.arguments === 'function'
    ? requiredCommand.arguments(buildManifest)
    : requiredCommand?.arguments;
  if (!requiredCommand || outcome?.command?.executable !== requiredCommand.executable ||
      outcome?.command?.cwd !== requiredCommand.cwd || outcome?.timeoutSeconds !== requiredCommand.timeoutSeconds ||
      JSON.stringify(outcome?.command?.arguments) !== JSON.stringify(requiredArguments)) {
    throw new Error(`Required outcome has wrong command identity: ${expected.id}`);
  }
}

async function validateBrowserReport(reportPath, fileManifestPath, buildManifest) {
  const reportBytes = await readFile(reportPath);
  const report = JSON.parse(reportBytes.toString('utf8'));
  if (report?.suite !== 'craft-architect-browser-truth' || report?.version !== 1 || report?.status !== 'passed') {
    throw new Error('Browser terminal report is missing, malformed, or non-passing.');
  }
  if (!Array.isArray(report.scenarios) || report.scenarios.length !== 6 ||
      report.scenarios.some(scenario => scenario?.status !== 'passed') ||
      !Array.isArray(report.blockers) || report.blockers.length !== 0 ||
      !Array.isArray(report.cleanupFailures) || report.cleanupFailures.length !== 0 ||
      report?.summary?.scenarioCount !== 6 || report?.summary?.passedScenarioCount !== 6 ||
      report?.summary?.failedScenarioCount !== 0 || report?.summary?.assertionCount !== 66 ||
      report?.summary?.passedAssertionCount !== 66) {
    throw new Error('Browser terminal report does not contain six clean passing scenarios.');
  }
  const expectedScenarios = [
    'chromium:indexeddb-empty-current',
    'chromium:indexeddb-v3-upgrade',
    'chromium:production-procurement-kill-switch',
    'firefox:indexeddb-empty-current',
    'firefox:indexeddb-v3-upgrade',
    'firefox:production-procurement-kill-switch'
  ];
  const actualScenarios = report.scenarios.map(scenario => `${scenario.browser}:${scenario.name}`);
  if (JSON.stringify(actualScenarios) !== JSON.stringify(expectedScenarios)) {
    throw new Error('Browser terminal report scenario inventory is incomplete or reordered.');
  }
  for (const scenario of report.scenarios) {
    const requiredAssertions = SCENARIO_ASSERTIONS[scenario.name];
    const actualAssertions = scenario.assertions?.map(assertion => assertion?.name);
    if (!requiredAssertions || JSON.stringify(scenario.requiredAssertions) !== JSON.stringify(requiredAssertions) ||
        JSON.stringify(actualAssertions) !== JSON.stringify(requiredAssertions) ||
        scenario.assertions.some(assertion => assertion?.passed !== true) ||
        scenario.assertionCount !== requiredAssertions.length ||
        scenario.passedAssertionCount !== requiredAssertions.length) {
      throw new Error(`Browser scenario assertion inventory is incomplete: ${scenario.browser}:${scenario.name}`);
    }
  }
  if (report?.identity?.runId !== buildManifest.run.id ||
      report?.identity?.runAttempt !== buildManifest.run.attempt ||
      report?.identity?.sourceCommitSha !== buildManifest.source.commitSha ||
      report?.identity?.archiveSha256 !== buildManifest.artifact.archiveSha256 ||
      report?.identity?.harnessTreeSha256 !== buildManifest.acceptance.harnessTreeSha256 ||
      report?.identity?.fixtureTreeSha256 !== buildManifest.acceptance.fixtureTreeSha256) {
    throw new Error('Browser terminal report identity is stale or foreign.');
  }
  const expectedRuntime = {
    node: buildManifest.runtime.node,
    playwright: buildManifest.runtime.playwright,
    browsers: buildManifest.runtime.browsers.map(browser => ({
      name: browser.name, revision: browser.revision, version: browser.version
    }))
  };
  if (JSON.stringify(report.runtime) !== JSON.stringify(expectedRuntime)) {
    throw new Error('Browser terminal report runtime identity is stale or foreign.');
  }
  if (report?.publish?.appSettings?.ProcurementRoutes?.GenerationEnabled !== false ||
      report?.publish?.appSettings?.LodestoneLookup?.BaseAddress !== `https://${buildManifest.target.domain}/api/`) {
    throw new Error('Browser terminal report did not observe exact effective configuration.');
  }
  const fileManifest = JSON.parse(await readFile(fileManifestPath, 'utf8'));
  const indexedDb = fileManifest.files.find(file => file.path === 'indexedDB.js');
  if (!indexedDb || report?.publish?.indexedDbSha256 !== indexedDb.sha256) {
    throw new Error('Browser terminal report did not consume manifested indexedDB.js bytes.');
  }
  return { bytes: reportBytes, sha256: sha256(reportBytes) };
}

async function validateTrx(reportPath, label, expectedTotal) {
  const bytes = await readFile(reportPath);
  const text = bytes.toString('utf8');
  if (!/<ResultSummary\b[^>]*outcome="Completed"/i.test(text)) {
    throw new Error(`${label} TRX terminal outcome is not completed.`);
  }
  const countersMatch = text.match(/<Counters\b([^>]*)\/?\s*>/i);
  if (!countersMatch) throw new Error(`${label} TRX counters are missing.`);
  const counters = {};
  for (const match of countersMatch[1].matchAll(/([A-Za-z]+)="(\d+)"/g)) {
    counters[match[1]] = Number.parseInt(match[2], 10);
  }
  const rejected = ['failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted',
    'notRunnable', 'notExecuted', 'disconnected', 'warning', 'inProgress', 'pending'];
  if (counters.total !== expectedTotal ||
      counters.executed !== counters.total || counters.passed !== counters.total ||
      rejected.some(name => counters[name] !== 0)) {
    throw new Error(`${label} TRX does not prove a nonempty, completely passing test inventory.`);
  }
  const results = [...text.matchAll(/<UnitTestResult\b([^>]*)\/?\s*>/gi)].map(result => {
    const attributes = {};
    for (const match of result[1].matchAll(/([A-Za-z]+)="([^"]*)"/g)) attributes[match[1]] = match[2];
    return attributes;
  });
  if (results.length !== expectedTotal || results.some(result =>
    !result.executionId || !result.testId || !result.testName || result.outcome !== 'Passed') ||
      new Set(results.map(result => result.executionId)).size !== expectedTotal ||
      new Set(results.map(result => result.testId)).size !== expectedTotal ||
      new Set(results.map(result => result.testName)).size !== expectedTotal) {
    throw new Error(`${label} TRX result inventory is missing, duplicated, or non-passing.`);
  }
  const inventory = [...results.map(result => result.testName)].sort().join('\n');
  return { total: counters.total, sha256: sha256(bytes), inventorySha256: sha256(Buffer.from(inventory, 'utf8')) };
}

export async function verifyOutcomes(options) {
  const buildManifestPath = requiredOption(options, 'build-manifest');
  const fileManifestPath = requiredOption(options, 'file-manifest');
  const archivePath = requiredOption(options, 'archive');
  const evidenceDir = requiredOption(options, 'evidence-dir');
  const acceptanceManifestPath = requiredOption(options, 'acceptance-manifest');
  const acceptanceRoot = requiredOption(options, 'acceptance-root');
  const fixturesRoot = requiredOption(options, 'fixtures-root');
  const specTrxPath = requiredOption(options, 'spec-trx');
  const contractTrxPath = requiredOption(options, 'contract-trx');
  const verifyOnly = options['verify-only'] === 'true';
  const buildManifestBytes = await readFile(buildManifestPath);
  const buildManifestSha256 = sha256(buildManifestBytes);

  const buildManifest = await verifyArtifact({
    archive: archivePath,
    'file-manifest': fileManifestPath,
    'build-manifest': buildManifestPath,
    'acceptance-root': acceptanceRoot,
    'fixtures-root': fixturesRoot
  });
  const browserReport = await validateBrowserReport(
    path.join(evidenceDir, 'browser-truth-report.json'), fileManifestPath, buildManifest);
  const dotnetReports = {
    spec: await validateTrx(specTrxPath, 'Specification tests', buildManifest.acceptance.dotnet.specTestCases),
    contract: await validateTrx(
      contractTrxPath, 'Contract tests', buildManifest.acceptance.dotnet.contractTestCases)
  };
  const outcomes = [];
  for (const expected of buildManifest.acceptance.requiredOutcomes) {
    const outcomePath = path.join(evidenceDir, `${expected.id}.json`);
    const outcomeBytes = await readFile(outcomePath);
    const outcome = JSON.parse(outcomeBytes.toString('utf8'));
    validateOutcome(outcome, expected, buildManifest);
    outcomes.push({ id: expected.id, sha256: sha256(outcomeBytes), subjectKind: expected.subjectKind });
  }

  if (buildManifest.acceptance.worker.required !== false ||
      buildManifest.acceptance.worker.status !== 'blocked-source-not-merged') {
    throw new Error('Worker acceptance must fail closed until Worker source merges.');
  }

  const transactionInput = canonicalJson({
    run: buildManifest.run,
    sourceCommitSha: buildManifest.source.commitSha,
    archiveSha256: buildManifest.artifact.archiveSha256,
    buildManifestSha256,
    browserReportSha256: browserReport.sha256,
    dotnetReports,
    outcomes
  });
  const acceptanceManifest = {
    schemaVersion: 1,
    status: 'passed',
    transactionSha256: sha256(Buffer.from(transactionInput, 'utf8')),
    run: buildManifest.run,
    sourceCommitSha: buildManifest.source.commitSha,
    archiveSha256: buildManifest.artifact.archiveSha256,
    buildManifestSha256,
    publishedFileTreeSha256: buildManifest.artifact.publishedFileTreeSha256,
    effectiveConfigurationSha256: buildManifest.artifact.effectiveConfigurationSha256,
    browserReportSha256: browserReport.sha256,
    dotnetReports,
    outcomes,
    workerAcceptance: buildManifest.acceptance.worker
  };
  const expectedText = canonicalJson(acceptanceManifest);
  if (options['expected-transaction'] &&
      options['expected-transaction'] !== acceptanceManifest.transactionSha256) {
    throw new Error('Acceptance transaction does not match originating final gate.');
  }

  if (verifyOnly) {
    const actualText = await readFile(acceptanceManifestPath, 'utf8');
    if (actualText !== expectedText) throw new Error('Acceptance manifest is missing, stale, foreign, or malformed.');
  } else {
    await mkdir(path.dirname(acceptanceManifestPath), { recursive: true });
    await writeFile(acceptanceManifestPath, expectedText, 'utf8');
  }

  if (process.env.GITHUB_OUTPUT) {
    await appendFile(process.env.GITHUB_OUTPUT,
      `transaction_sha256=${acceptanceManifest.transactionSha256}\n`, 'utf8');
  }
  return acceptanceManifest;
}

async function main() {
  const result = await verifyOutcomes(parseOptions(process.argv.slice(2)));
  console.log(`Truthful suite passed (${result.transactionSha256}).`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  main().catch(error => {
    console.error(error.stack ?? error.message);
    process.exitCode = 1;
  });
}
