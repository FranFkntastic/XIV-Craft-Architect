import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { spawn } from 'node:child_process';
import http from 'node:http';
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, firefox } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(here, '../..');
let publishRoot = process.env.ENGINE_WORKER_PUBLISH_ROOT
  ? path.resolve(process.env.ENGINE_WORKER_PUBLISH_ROOT)
  : null;
let temporaryPublishRoot;
let server;
let origin;

before(async () => {
  if (!publishRoot) {
    temporaryPublishRoot = await mkdtemp(path.join(tmpdir(), 'craft-architect-engine-worker-'));
    await run('dotnet', [
      'publish',
      path.join(repositoryRoot, 'src', 'FFXIV Craft Architect.Web', 'FFXIV Craft Architect.Web.csproj'),
      '-c', 'Release',
      '--no-restore',
      '-o', temporaryPublishRoot
    ], repositoryRoot);
    publishRoot = path.join(temporaryPublishRoot, 'wwwroot');
  }
  assert.equal((await stat(path.join(publishRoot, 'index.html'))).isFile(), true);
  server = http.createServer(async (request, response) => {
    try {
      const requestPath = new URL(request.url, 'http://localhost').pathname;
      if (requestPath === '/engine-worker-harness.html') {
        response.writeHead(200, {
          'content-type': 'text/html',
          'cache-control': 'no-store'
        });
        response.end('<!doctype html><script src="/indexedDB.js"></script>');
        return;
      }
      const relativePath = requestPath === '/' ? 'index.html' : decodeURIComponent(requestPath.slice(1));
      const filePath = path.resolve(publishRoot, relativePath);
      if (!filePath.startsWith(`${publishRoot}${path.sep}`) && filePath !== path.join(publishRoot, 'index.html')) {
        response.writeHead(403).end();
        return;
      }
      const body = await readFile(filePath);
      response.writeHead(200, {
        'content-type': contentType(filePath),
        'cache-control': 'no-store'
      });
      response.end(body);
    } catch {
      response.writeHead(404).end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
}, { timeout: 120_000 });

after(async () => {
  if (server) await new Promise(resolve => server.close(resolve));
  if (temporaryPublishRoot) await rm(temporaryPublishRoot, { recursive: true, force: true });
});

for (const [name, browserType] of [['chromium', chromium], ['firefox', firefox]]) {
  test(`${name}: managed worker executes bounded procurement and remains killable`, {
    timeout: 90_000
  }, async () => {
    const browser = await browserType.launch({ headless: true });
    try {
      const page = await browser.newPage();
      const errors = [];
      page.on('console', message => {
        if (message.type() === 'error') errors.push(message.text());
      });
      page.on('pageerror', error => errors.push(error.message));
      // Exercise the Worker in its real origin without booting a second copy of
      // the application. The production topology owns one replaceable Worker;
      // loading index.html here would start the app Worker against the same
      // IndexedDB session while this harness deliberately mutates it directly.
      await page.goto(`${origin}/engine-worker-harness.html`, { waitUntil: 'load' });

      const evidence = await page.evaluate(async () => {
        function waitFor(worker, predicate, timeoutMs, description) {
          return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
              worker.removeEventListener('message', listener);
              reject(new Error(`${description} timed out.`));
            }, timeoutMs);
            const listener = event => {
              if (!predicate(event.data)) return;
              clearTimeout(timeout);
              worker.removeEventListener('message', listener);
              resolve(event.data);
            };
            worker.addEventListener('message', listener);
          });
        }

        async function startWorker(generation) {
          const worker = new Worker('/engine-worker.js?acceptance=true', {
            type: 'module',
            name: `engine-worker-test-${generation}`
          });
          const capabilityPromise = waitFor(
            worker,
            message => message?.kind === 'capability' && message.generation === generation,
            45_000,
            'Worker capability');
          worker.postMessage({
            protocolVersion: '4', kind: 'ping', generation,
            executionId: null, transactionId: null, payload: null
          });
          return { worker, capability: await capabilityPromise };
        }

        await IndexedDB.savePlan({
          id: 'autosave',
          name: 'Legacy autosave',
          dataCenter: 'Aether',
          projectItems: [{ id: 42, name: 'Worker item', iconId: 0, quantity: 2, mustBeHq: false }],
          planJson: null,
          savedAt: new Date().toISOString()
        });

        const active = await startWorker(1);
        async function sendSessionCommand(commandKind, expectedRevision, payload) {
          const commandId = crypto.randomUUID();
          const responsePromise = waitFor(
            active.worker,
            message => {
              if (message?.kind !== 'managed-json' || message.messageKind !== 'session-result') return false;
              const decoded = JSON.parse(message.messageJson);
              return decoded.executionId === commandId && decoded.transactionId === commandId;
            },
            45_000,
            `Worker session ${commandKind}`);
          const message = {
            protocolVersion: '4',
            kind: 'session-command',
            generation: 1,
            executionId: commandId,
            transactionId: commandId,
            payload: {
              contractVersion: '1',
              commandKind,
              expectedRevision,
              payload
            }
          };
          active.worker.postMessage({
            kind: 'managed-json',
            messageJson: JSON.stringify(message),
            generation: 1,
            messageKind: 'session-command',
            executionId: commandId,
            transactionId: commandId
          });
          const response = await responsePromise;
          return JSON.parse(response.messageJson);
        }

        // The routed page can request a projection before MainLayout reaches its
        // explicit startup bootstrap. The cached restore result must still be
        // rebound to the later bootstrap command's identity.
        const projectionBeforeBootstrap = await sendSessionCommand('shell', 0, {});
        const bootstrappedSession = await sendSessionCommand('bootstrap', 0, {});
        const replacedSession = await sendSessionCommand('replace', 1, {
          storedPlan: {
            id: 'autosave',
            name: 'Worker replacement',
            dataCenter: 'Aether',
            projectItems: [
              { id: 42, name: 'Worker item', iconId: 0, quantity: 2, mustBeHq: false },
              { id: 43, name: 'Second item', iconId: 0, quantity: 1, mustBeHq: false }
            ],
            planJson: null,
            savedAt: new Date().toISOString()
          },
          trackStoredPlanIdentity: false
        });
        const staleSession = await sendSessionCommand('shell', 1, {});
        const currentSession = await sendSessionCommand('shell', 2, {});
        const mutatedSession = await sendSessionCommand('mutate-project-items', 2, {
          operation: 'add',
          item: { id: 44, name: 'Worker-owned item', iconId: 0, quantity: 3, mustBeHq: false }
        });
        const durableMutationShell = await sendSessionCommand('shell', 3, {});

        const malformedExecutionId = crypto.randomUUID();
        const malformedTransactionId = crypto.randomUUID();
        const malformedPromise = waitFor(
          active.worker,
          message => message?.kind === 'protocol-error' &&
            message.executionId === malformedExecutionId &&
            message.transactionId === malformedTransactionId,
          10_000,
          'Malformed managed JSON rejection');
        active.worker.postMessage({
          kind: 'managed-json',
          messageJson: '{',
          generation: 1,
          messageKind: 'execute',
          executionId: malformedExecutionId,
          transactionId: malformedTransactionId
        });
        const malformed = await malformedPromise;
        const progress = [];
        active.worker.addEventListener('message', event => {
          const message = event.data?.kind === 'managed-json'
            ? JSON.parse(event.data.messageJson)
            : event.data;
          if (message?.kind === 'progress') progress.push(message);
        });
        let heartbeatCount = 0;
        let heartbeatMaxGapMs = 0;
        let previousHeartbeat = performance.now();
        const heartbeat = setInterval(() => {
          const now = performance.now();
          heartbeatMaxGapMs = Math.max(heartbeatMaxGapMs, now - previousHeartbeat);
          previousHeartbeat = now;
          heartbeatCount++;
        }, 10);
        const requestPromise = waitFor(
          active.worker,
          message => message?.kind === 'acceptance-request',
          45_000,
          'Managed procurement request');
        active.worker.postMessage({
          protocolVersion: '4', kind: 'acceptance-request', generation: 1,
          executionId: null, transactionId: null, payload: null
        });
        const request = await requestPromise;
        const resultPromise = waitFor(
          active.worker,
          message => {
            const decoded = message?.kind === 'managed-json'
              ? JSON.parse(message.messageJson)
              : message;
            return decoded?.kind === 'computation-result' || decoded?.kind === 'protocol-error';
          },
          45_000,
          'Managed procurement result');
        active.worker.postMessage({
          kind: 'managed-json',
          messageJson: request.messageJson,
          generation: 1,
          messageKind: 'execute'
        });
        const resultEnvelope = await resultPromise;
        const result = resultEnvelope.kind === 'managed-json'
          ? JSON.parse(resultEnvelope.messageJson)
          : resultEnvelope;
        const secondRequestPromise = waitFor(
          active.worker,
          message => message?.kind === 'acceptance-request',
          45_000,
          'Second managed procurement request');
        active.worker.postMessage({
          protocolVersion: '4', kind: 'acceptance-request', generation: 1,
          executionId: null, transactionId: null, payload: null
        });
        const secondRequest = await secondRequestPromise;
        const secondResultPromise = waitFor(
          active.worker,
          message => {
            const decoded = message?.kind === 'managed-json'
              ? JSON.parse(message.messageJson)
              : message;
            return decoded?.transactionId === JSON.parse(secondRequest.messageJson).transactionId &&
              (decoded?.kind === 'computation-result' || decoded?.kind === 'protocol-error');
          },
          45_000,
          'Second managed procurement result');
        active.worker.postMessage({
          kind: 'managed-json',
          messageJson: secondRequest.messageJson,
          generation: 1,
          messageKind: 'execute'
        });
        const secondResultEnvelope = await secondResultPromise;
        const secondResult = secondResultEnvelope.kind === 'managed-json'
          ? JSON.parse(secondResultEnvelope.messageJson)
          : secondResultEnvelope;
        clearInterval(heartbeat);
        active.worker.terminate();

        const hanging = await startWorker(10);
        const hangStarted = waitFor(
          hanging.worker,
          message => message?.kind === 'acceptance-hang-started',
          10_000,
          'Managed hang start');
        hanging.worker.postMessage({
          protocolVersion: '4', kind: 'acceptance-hang', generation: 10,
          executionId: null, transactionId: null, payload: null
        });
        await hangStarted;
        let terminationHeartbeat = 0;
        const terminationTimer = setInterval(() => terminationHeartbeat++, 10);
        await new Promise(resolve => setTimeout(resolve, 100));
        const terminationStarted = performance.now();
        hanging.worker.terminate();
        const terminationCallMs = performance.now() - terminationStarted;
        const replacement = await startWorker(11);
        clearInterval(terminationTimer);
        replacement.worker.terminate();

        return {
          capability: active.capability,
          projectionBeforeBootstrap,
          bootstrappedSession,
          replacedSession,
          staleSession,
          currentSession,
          mutatedSession,
          durableMutationShell,
          malformed,
          result,
          secondResult,
          progress,
          heartbeatCount,
          heartbeatMaxGapMs,
          hangingCapability: hanging.capability,
          replacementCapability: replacement.capability,
          terminationHeartbeat,
          terminationCallMs
        };
      });

      assert.equal(evidence.capability.payload.dedicatedWorker, true);
      assert.equal(evidence.capability.payload.managedRuntimeReady, true);
      assert.equal(evidence.capability.payload.executionSupported, true);
      assert.equal(evidence.capability.payload.managedRuntimeAssembly, 'FFXIV_Craft_Architect.Web');
      assert.match(evidence.capability.payload.managedRuntimeProofHash, /^[0-9a-f]{64}$/i);
      assert.match(evidence.capability.payload.workerInstanceId, /^[0-9a-f-]{36}$/i);
      assert.equal(evidence.projectionBeforeBootstrap.payload.accepted, false);
      assert.equal(evidence.projectionBeforeBootstrap.payload.rejectionCode, 'stale-revision');
      assert.equal(evidence.projectionBeforeBootstrap.payload.revision, 1);
      assert.equal(evidence.bootstrappedSession.payload.accepted, true);
      assert.equal(evidence.bootstrappedSession.payload.revision, 1);
      assert.equal(evidence.bootstrappedSession.payload.projection.migratedFromLegacy, true);
      assert.equal(evidence.replacedSession.payload.accepted, true);
      assert.equal(evidence.replacedSession.payload.revision, 2);
      assert.equal(evidence.replacedSession.payload.projection.projectItemCount, 2);
      assert.equal(evidence.staleSession.payload.accepted, false);
      assert.equal(evidence.staleSession.payload.rejectionCode, 'stale-revision');
      assert.equal(evidence.currentSession.payload.accepted, true);
      assert.equal(evidence.currentSession.payload.revision, 2);
      assert.equal(evidence.mutatedSession.payload.accepted, true);
      assert.equal(evidence.mutatedSession.payload.revision, 3);
      assert.equal(evidence.mutatedSession.payload.projection.shell.projectItemCount, 3);
      assert.equal(evidence.mutatedSession.payload.projection.view.projectItems.length, 3);
      assert.equal(evidence.mutatedSession.payload.projection.durableState, undefined);
      assert.equal(evidence.durableMutationShell.payload.accepted, true);
      assert.equal(evidence.durableMutationShell.payload.revision, 3);
      assert.equal(evidence.malformed.payload.code, 'managed-json-invalid');

      assert.equal(evidence.result.kind, 'computation-result');
      assert.equal(evidence.result.payload.status, 1, 'managed computation must complete');
      assert.equal(evidence.result.payload.finalPhase, 7, 'procurement must reach reconciliation');
      assert.match(evidence.result.payload.computationHash, /^[0-9a-f]{64}$/i);
      assert.equal(evidence.result.payload.computationEvidence['phase:Reconciling'], 'complete');
      assert.equal(evidence.secondResult.kind, 'computation-result');
      assert.equal(evidence.secondResult.payload.status, 1, 'the same Worker must accept a second command');
      assert.notEqual(
        evidence.secondResult.transactionId,
        evidence.result.transactionId,
        'sequential commands must retain distinct transaction identity');
      const route = evidence.result.payload.result.procurementRouteResult;
      const decision = route.routeDecision;
      assert.equal(route.isComplete, true);
      assert.equal(typeof decision.routeSearchWasTruncated, 'boolean');

      assert.ok(evidence.progress.length >= 5, `received ${evidence.progress.length} progress messages`);
      assert.ok(evidence.progress.some(message =>
        message.payload.phase === 7 && /Optimizing procurement route/.test(message.payload.message)));
      assert.ok(evidence.heartbeatCount >= 2, `heartbeat advanced ${evidence.heartbeatCount} times`);
      assert.ok(evidence.heartbeatMaxGapMs < 1000, `heartbeat gap was ${evidence.heartbeatMaxGapMs}ms`);

      assert.notEqual(
        evidence.hangingCapability.payload.workerInstanceId,
        evidence.replacementCapability.payload.workerInstanceId);
      assert.equal(evidence.replacementCapability.payload.executionSupported, true);
      assert.ok(evidence.terminationHeartbeat >= 2, 'page heartbeat must advance while Worker is hung');
      assert.ok(evidence.terminationCallMs < 100, `Worker.terminate took ${evidence.terminationCallMs}ms`);
      assert.deepEqual(errors, []);
    } finally {
      await browser.close();
    }
  });
}

function run(command, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, stdio: 'inherit' });
    child.once('error', reject);
    child.once('exit', code => {
      if (code === 0) resolve();
      else reject(new Error(`${command} exited with code ${code}.`));
    });
  });
}

function contentType(filePath) {
  switch (path.extname(filePath)) {
    case '.css': return 'text/css';
    case '.html': return 'text/html';
    case '.js': return 'text/javascript';
    case '.json': return 'application/json';
    case '.wasm': return 'application/wasm';
    default: return 'application/octet-stream';
  }
}
