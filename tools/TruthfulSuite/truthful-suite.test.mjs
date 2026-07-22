import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createArtifact, verifyArtifact } from './truthful-artifact.mjs';
import { verifyOutcomes } from './verify-outcomes.mjs';

const createTestArtifact = options => createArtifact(options, { verifySource: false });

async function fixture() {
  const temporaryRoot = await mkdtemp(path.join(os.tmpdir(), 'truthful-suite-'));
  const published = path.join(temporaryRoot, 'published');
  const acceptance = path.join(temporaryRoot, 'browser');
  const fixtures = path.join(temporaryRoot, 'fixtures');
  await mkdir(path.join(published, '_framework'), { recursive: true });
  await mkdir(path.join(acceptance, 'node_modules', 'playwright-core'), { recursive: true });
  await mkdir(fixtures, { recursive: true });
  await writeFile(path.join(published, 'index.html'), '<html><script src="_framework/blazor.webassembly.js"></script></html>');
  await writeFile(path.join(published, 'indexedDB.js'), 'globalThis.IndexedDB = {};\n');
  await writeFile(path.join(published, '_framework', 'app.wasm'), Buffer.from([0, 1, 2, 3]));
  await writeFile(path.join(acceptance, 'truth-suite.mjs'), 'export {};\n');
  await writeFile(path.join(acceptance, 'engine-worker.test.mjs'), 'export {};\n');
  await writeFile(path.join(acceptance, 'package.json'), '{"private":true}\n');
  await writeFile(path.join(acceptance, 'package-lock.json'), JSON.stringify({
    packages: { 'node_modules/playwright': { version: '1.2.3' } }
  }));
  await writeFile(path.join(acceptance, 'node_modules', 'playwright-core', 'browsers.json'), JSON.stringify({
    browsers: [
      { name: 'chromium', revision: '100', browserVersion: '1.0' },
      { name: 'firefox', revision: '200', browserVersion: '2.0' }
    ]
  }));
  await writeFile(path.join(fixtures, 'fixture.json'), '{}\n');
  return { temporaryRoot, published, acceptance, fixtures };
}

function options(root, outDir, acceptance, fixtures) {
  return {
    root,
    'out-dir': outDir,
    'archive-name': 'web-test.tar.gz',
    domain: 'example.com',
    slot: 'main',
    'source-sha': '1'.repeat(40),
    'tree-sha': '2'.repeat(40),
    'source-dirty': 'false',
    'source-ref': 'main',
    'run-id': '100',
    'run-attempt': '1',
    release: '100-1-1111111',
    'dotnet-version': '8.0.100',
    'acceptance-root': acceptance,
    'fixtures-root': fixtures
  };
}

test('archive creation is deterministic and exact-byte extraction verifies configuration', async () => {
  const first = await fixture();
  const second = await fixture();
  const firstOut = path.join(first.temporaryRoot, 'out');
  const secondOut = path.join(second.temporaryRoot, 'out');
  const firstResult = await createTestArtifact(options(first.published, firstOut, first.acceptance, first.fixtures));
  const secondResult = await createTestArtifact(options(second.published, secondOut, second.acceptance, second.fixtures));
  const firstArchive = await readFile(firstResult.archivePath);
  const secondArchive = await readFile(secondResult.archivePath);
  assert.deepEqual(firstArchive, secondArchive);

  const extracted = path.join(first.temporaryRoot, 'extracted');
  await verifyArtifact({
    archive: firstResult.archivePath,
    'file-manifest': firstResult.fileManifestPath,
    'build-manifest': firstResult.buildManifestPath,
    'acceptance-root': first.acceptance,
    'fixtures-root': first.fixtures,
    extract: extracted
  });
  const config = JSON.parse(await readFile(path.join(extracted, 'appsettings.json'), 'utf8'));
  const release = JSON.parse(await readFile(path.join(extracted, 'release.json'), 'utf8'));
  assert.equal(config.ProcurementRoutes.GenerationEnabled, false);
  assert.equal(config.EngineRewrite.ExecutionEnabled, false);
  assert.equal(config.EngineAcceptance.Enabled, false);
  assert.equal(config.LodestoneLookup.BaseAddress, 'https://example.com/api/');
  assert.equal(release.sourceDirty, false);
  assert.equal(firstResult.buildManifest.source.dirty, false);

  const localDev = await fixture();
  const localDevOptions = options(
    localDev.published, path.join(localDev.temporaryRoot, 'out'), localDev.acceptance, localDev.fixtures);
  localDevOptions.slot = 'local-dev';
  localDevOptions.domain = 'dev.example.com';
  localDevOptions['source-ref'] = 'local-dev';
  const localDevResult = await createTestArtifact(localDevOptions);
  const localDevExtracted = path.join(localDev.temporaryRoot, 'extracted');
  await verifyArtifact({
    archive: localDevResult.archivePath,
    'file-manifest': localDevResult.fileManifestPath,
    'build-manifest': localDevResult.buildManifestPath,
    'acceptance-root': localDev.acceptance,
    'fixtures-root': localDev.fixtures,
    extract: localDevExtracted
  });
  const localDevConfig = JSON.parse(await readFile(path.join(localDevExtracted, 'appsettings.json'), 'utf8'));
  assert.equal(localDevConfig.ProcurementRoutes.GenerationEnabled, true);
  assert.equal(localDevConfig.EngineRewrite.ExecutionEnabled, true);
  assert.equal(localDevResult.buildManifest.acceptance.worker.required, true);
  assert.equal(localDevResult.buildManifest.acceptance.worker.outcomeId, 'engine-browser-tests');

  await writeFile(path.join(first.fixtures, 'fixture.json'), '{"changed":true}\n');
  await assert.rejects(() => verifyArtifact({
    archive: firstResult.archivePath,
    'file-manifest': firstResult.fileManifestPath,
    'build-manifest': firstResult.buildManifestPath,
    'acceptance-root': first.acceptance,
    'fixtures-root': first.fixtures
  }), /Acceptance inputs/);
});

test('archive verification rejects changed bytes', async () => {
  const value = await fixture();
  const result = await createTestArtifact(options(
    value.published, path.join(value.temporaryRoot, 'out'), value.acceptance, value.fixtures));
  const archive = await readFile(result.archivePath);
  archive[archive.length - 1] ^= 0xff;
  await writeFile(result.archivePath, archive);
  await assert.rejects(() => verifyArtifact({
    archive: result.archivePath,
    'file-manifest': result.fileManifestPath,
    'build-manifest': result.buildManifestPath
  }), /Archive identity/);
});

test('terminal verifier accepts only complete outcomes for exact source and archive identities', async () => {
  const value = await fixture();
  const outDir = path.join(value.temporaryRoot, 'out');
  const evidenceDir = path.join(value.temporaryRoot, 'evidence');
  const result = await createTestArtifact(options(value.published, outDir, value.acceptance, value.fixtures));
  await mkdir(evidenceDir, { recursive: true });
  const commands = {
    'suite-structure': ['pwsh', '.', ['-NoProfile', '-File', 'scripts/Assert-TruthfulTestSuite.ps1'], 120],
    'dependency-audit': ['node', '.', ['tools/TruthfulSuite/check-dependencies.mjs'], 300],
    'solution-build': ['dotnet', '.', ['build', 'FFXIV Craft Architect.sln', '--configuration', 'Release', '--no-restore'], 600],
    'spec-tests': ['dotnet', '.', ['test', 'src/FFXIV Craft Architect.SpecTests/FFXIV Craft Architect.SpecTests.csproj', '--configuration', 'Release', '--no-build', '--no-restore', '--blame-hang', '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'mini', '--logger', 'trx;LogFileName=spec-tests.trx', '--logger', 'console;verbosity=normal'], 600],
    'contract-tests': ['dotnet', '.', ['test', 'src/FFXIV Craft Architect.ContractTests/FFXIV Craft Architect.ContractTests.csproj', '--configuration', 'Release', '--no-build', '--no-restore', '--blame-hang', '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'mini', '--logger', 'trx;LogFileName=contract-tests.trx', '--logger', 'console;verbosity=normal'], 600],
    'web-publish': ['dotnet', '.', ['publish', 'src/FFXIV Craft Architect.Web/FFXIV Craft Architect.Web.csproj', '--configuration', 'Release', '--output', 'dist/publish', '--no-restore', '-p:BuildInfoBranchName=main'], 600],
    'product-configuration': ['node', '.', ['tools/TruthfulSuite/check-product.mjs', 'dist/subject/src/FFXIV Craft Architect.Web/wwwroot', 'example.com', 'main'], 60],
    'deterministic-browser-tests': ['npm', 'dist/subject/tools/IndexedDbBrowserTests', ['test', '--', '--web-root', '../../src/FFXIV Craft Architect.Web/wwwroot', '--output', '../../../evidence/browser-truth-report.json'], 600]
  };
  for (const expected of result.buildManifest.acceptance.requiredOutcomes) {
    const identity = expected.subjectKind === 'source'
      ? result.buildManifest.source.commitSha
      : result.buildManifest.artifact.archiveSha256;
    const [executable, cwd, arguments_, timeoutSeconds] = commands[expected.id];
    const outcome = {
      schemaVersion: 1,
      id: expected.id,
      run: result.buildManifest.run,
      sourceCommitSha: result.buildManifest.source.commitSha,
      subject: { kind: expected.subjectKind, identity },
      command: { executable, arguments: arguments_, cwd },
      timeoutSeconds,
      status: 'passed',
      exitCode: 0,
      signal: null
    };
    await writeFile(path.join(evidenceDir, `${expected.id}.json`), `${JSON.stringify(outcome, null, 2)}\n`);
  }
  const fileManifest = JSON.parse(await readFile(result.fileManifestPath, 'utf8'));
  const indexedDbSha256 = fileManifest.files.find(file => file.path === 'indexedDB.js').sha256;
  const scenarioAssertions = {
    'indexeddb-empty-current': ['production-module-loaded', 'empty-database-created-at-current-schema', 'current-store-contract-present', 'market-timestamp-index-present', 'setting-sentinel-durable-after-current-reopen', 'plan-sentinel-durable-after-current-reopen', 'current-schema-stable-after-reload', 'browser-diagnostics-clean'],
    'indexeddb-v3-upgrade': ['historical-v3-fixture-seeded', 'production-module-upgraded-historical-schema', 'historical-plan-sentinel-survived', 'historical-setting-sentinel-survived', 'historical-market-record-survived', 'historical-plan-summary-rebuilt', 'upgraded-market-timestamp-index-present', 'browser-diagnostics-clean'],
    'production-procurement-kill-switch': ['production-kill-switch-config-loaded', 'native-plan-imported-through-visible-flow', 'explicit-market-analysis-published', 'market-analysis-durable-in-autosave', 'acquisition-evaluation-available-with-kill-switch', 'procurement-route-control-visibly-disabled', 'disabled-route-action-does-not-execute', 'name-first-item-search-returned-product-result', 'name-first-item-selection-updated-project', 'ordinary-navigation-remained-usable', 'reload-restored-imported-plan', 'reload-restored-market-analysis', 'manual-acquisition-choice-remained-usable', 'no-route-execution-observed-through-final-interaction', 'no-worker-request-observed', 'no-unexpected-external-request-observed', 'browser-diagnostics-clean']
  };
  const browserReport = {
    suite: 'craft-architect-browser-truth',
    version: 1,
    status: 'passed',
    identity: {
      runId: result.buildManifest.run.id,
      runAttempt: result.buildManifest.run.attempt,
      sourceCommitSha: result.buildManifest.source.commitSha,
      archiveSha256: result.buildManifest.artifact.archiveSha256,
      harnessTreeSha256: result.buildManifest.acceptance.harnessTreeSha256,
      fixtureTreeSha256: result.buildManifest.acceptance.fixtureTreeSha256
    },
    runtime: {
      node: result.buildManifest.runtime.node,
      playwright: result.buildManifest.runtime.playwright,
      browsers: result.buildManifest.runtime.browsers.map(browser => ({
        name: browser.name, revision: browser.revision, version: browser.version
      }))
    },
    publish: {
      appSettings: {
        LodestoneLookup: { BaseAddress: 'https://example.com/api/' },
        ProcurementRoutes: { GenerationEnabled: false },
        EngineRewrite: { ExecutionEnabled: false },
        EngineAcceptance: { Enabled: false, UseDeterministicEvidence: false }
      },
      indexedDbSha256
    },
    scenarios: [
      ['chromium', 'indexeddb-empty-current'],
      ['chromium', 'indexeddb-v3-upgrade'],
      ['chromium', 'production-procurement-kill-switch'],
      ['firefox', 'indexeddb-empty-current'],
      ['firefox', 'indexeddb-v3-upgrade'],
      ['firefox', 'production-procurement-kill-switch']
    ].map(([browser, name]) => ({
      browser,
      name,
      status: 'passed',
      requiredAssertions: scenarioAssertions[name],
      assertions: scenarioAssertions[name].map(assertion => ({ name: assertion, passed: true })),
      assertionCount: scenarioAssertions[name].length,
      passedAssertionCount: scenarioAssertions[name].length
    })),
    blockers: [],
    cleanupFailures: [],
    summary: {
      scenarioCount: 6,
      passedScenarioCount: 6,
      failedScenarioCount: 0,
      assertionCount: 66,
      passedAssertionCount: 66
    }
  };
  await writeFile(path.join(evidenceDir, 'browser-truth-report.json'), `${JSON.stringify(browserReport, null, 2)}\n`);
  const passingTrx = total => `<TestRun><Results>${Array.from({ length: total }, (_, index) => `<UnitTestResult executionId="execution-${index}" testId="test-${index}" testName="Test ${index}" outcome="Passed" />`).join('')}</Results><ResultSummary outcome="Completed"><Counters total="${total}" executed="${total}" passed="${total}" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="${total}" inProgress="0" pending="0" /></ResultSummary></TestRun>`;
  const specTrx = path.join(value.temporaryRoot, 'spec-tests.trx');
  const contractTrx = path.join(value.temporaryRoot, 'contract-tests.trx');
  await writeFile(specTrx, passingTrx(56));
  await writeFile(contractTrx, passingTrx(88));

  const acceptanceManifest = path.join(outDir, 'acceptance-manifest.json');
  const verifyOptions = {
    archive: result.archivePath,
    'file-manifest': result.fileManifestPath,
    'build-manifest': result.buildManifestPath,
    'evidence-dir': evidenceDir,
    'acceptance-manifest': acceptanceManifest,
    'acceptance-root': value.acceptance,
    'fixtures-root': value.fixtures,
    'spec-trx': specTrx,
    'contract-trx': contractTrx
  };
  const accepted = await verifyOutcomes(verifyOptions);
  assert.equal(accepted.status, 'passed');
  await verifyOutcomes({ ...verifyOptions, 'verify-only': 'true' });

  const failedPath = path.join(evidenceDir, 'contract-tests.json');
  const failed = JSON.parse(await readFile(failedPath, 'utf8'));
  failed.status = 'timed-out';
  await writeFile(failedPath, `${JSON.stringify(failed, null, 2)}\n`);
  await assert.rejects(() => verifyOutcomes(verifyOptions), /did not pass: contract-tests/);

  failed.status = 'passed';
  await writeFile(failedPath, `${JSON.stringify(failed, null, 2)}\n`);
  const browserReportPath = path.join(evidenceDir, 'browser-truth-report.json');
  const incompleteBrowserReport = structuredClone(browserReport);
  incompleteBrowserReport.scenarios[0].assertions = [];
  incompleteBrowserReport.scenarios[0].assertionCount = 0;
  incompleteBrowserReport.scenarios[0].passedAssertionCount = 0;
  await writeFile(browserReportPath, `${JSON.stringify(incompleteBrowserReport, null, 2)}\n`);
  await assert.rejects(() => verifyOutcomes(verifyOptions), /assertion inventory is incomplete/);
  await writeFile(browserReportPath, `${JSON.stringify(browserReport, null, 2)}\n`);
  const productPath = path.join(evidenceDir, 'product-configuration.json');
  const wrongCommand = JSON.parse(await readFile(productPath, 'utf8'));
  wrongCommand.command.executable = 'true';
  await writeFile(productPath, `${JSON.stringify(wrongCommand, null, 2)}\n`);
  await assert.rejects(() => verifyOutcomes(verifyOptions), /wrong command identity: product-configuration/);
});
