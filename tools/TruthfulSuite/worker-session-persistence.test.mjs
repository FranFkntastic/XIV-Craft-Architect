import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../../src/FFXIV Craft Architect.Web/wwwroot/engine-worker.js', import.meta.url), 'utf8');
const commands = source.slice(source.indexOf('function executeSessionCommand('), source.indexOf('async function loadDurableSession('));

// Exercise the production transaction adapter with deterministic managed/IDB
// boundaries. Browser acceptance separately covers their concrete implementations.
function fixture({ durableRevision = 95, managedRevision = durableRevision, rejectMutation = false, rejectReplacement = false, commit } = {}) {
  let durable = { revision: durableRevision, storedPlan: { quantity: 4 }, trackStoredPlanIdentity: false };
  let managed = { revision: managedRevision, quantity: 4 };
  const calls = [];
  const context = vm.createContext({
    performance, console: { info() {} },
    loadDurableSession: async () => structuredClone(durable),
    commitDurableSession: async (expected, revision, storedPlan) => {
      assert.equal(expected, durable.revision);
      if (commit) await commit();
      durable = { ...durable, revision, storedPlan: structuredClone(storedPlan) };
    }
  });
  vm.runInContext('let sessionCommandQueue = Promise.resolve(); let sessionFault = null; let sessionBootstrapPromise = Promise.resolve("{}");\n' + commands, context);
  const host = { async ExecuteSessionCommandJson(json) {
    const request = JSON.parse(json);
    const c = request.payload;
    calls.push(c.commandKind);
    let accepted = true, rejectionCode = null, message = null, projection;
    if (c.expectedRevision !== managed.revision) {
      accepted = false; rejectionCode = 'stale-revision';
    } else if (c.commandKind === 'restore') {
      managed = { revision: c.payload.revision, quantity: c.payload.storedPlan.quantity };
      if (rejectReplacement && managed.quantity === 99) {
        accepted = false; rejectionCode = 'command-rejected'; message = 'Invalid replacement';
      }
    } else if (c.commandKind.startsWith('mutate-')) {
      managed.revision++;
      managed.quantity = c.payload.quantity ?? 99;
      if (rejectMutation) {
        accepted = false; rejectionCode = 'command-rejected'; message = 'Route unavailable';
      } else {
        projection = { shell: { revision: managed.revision }, durableState: { quantity: managed.quantity }, publicProjection: { quantity: managed.quantity } };
      }
    }
    return JSON.stringify({ ...request, kind: 'session-result', payload: {
      commandKind: c.commandKind, revision: managed.revision, accepted, rejectionCode, message,
      projection: projection ?? { revision: managed.revision, quantity: managed.quantity }
    } });
  } };
  return {
    calls,
    state: () => ({ durable, managed }),
    async send(kind, revision, payload = {}) {
      return JSON.parse(await context.executeSessionCommand(JSON.stringify({
        protocolVersion: '4', kind: 'session-command', generation: 1,
        executionId: 'fixture', transactionId: 'fixture',
        payload: { contractVersion: '1', commandKind: kind, expectedRevision: revision, payload }
      }), host)).payload;
    }
  };
}

test('the observed 96/95 divergence recovers committed truth and applies the pending quantity exactly once', async () => {
  const f = fixture({ managedRevision: 96 });
  const result = await f.send('mutate-project-items', 96, { quantity: 5 });
  assert.equal(result.accepted, true);
  assert.equal(result.revision, 98);
  assert.deepEqual(f.calls, ['shell', 'restore', 'mutate-project-items']);
  assert.equal(f.state().durable.storedPlan.quantity, 5);
  assert.equal(f.state().managed.revision, f.state().durable.revision);
});

test('a genuinely stale edit is rejected, never converted into a successful shell or replayed', async () => {
  const f = fixture();
  const result = await f.send('mutate-project-items', 94, { quantity: 9 });
  assert.equal(result.accepted, false);
  assert.equal(result.rejectionCode, 'stale-revision');
  assert.equal(result.commandKind, 'mutate-project-items');
  assert.deepEqual(f.calls, ['shell']);
  assert.equal(f.state().durable.storedPlan.quantity, 4);
});

test('a rejected mutation restores the committed plan before returning its actual error', async () => {
  const f = fixture({ rejectMutation: true });
  const result = await f.send('mutate-procurement', 95);
  assert.equal(result.accepted, false);
  assert.equal(result.message, 'Route unavailable');
  assert.equal(result.revision, 97);
  assert.equal(result.projection.quantity, 4);
  assert.equal(f.state().managed.quantity, 4);
  assert.equal(f.state().durable.storedPlan.quantity, 4);
  assert.equal(f.state().durable.revision, 97);
});

test('recovery does not replay revision-bound market evidence', async () => {
  const f = fixture({ managedRevision: 96 });
  const result = await f.send('mutate-market-evidence-publication', 96);
  assert.equal(result.rejectionCode, 'stale-revision');
  assert.deepEqual(f.calls, ['shell', 'restore']);
  assert.equal(f.state().durable.storedPlan.quantity, 4);
});

test('reads wait for the durable commit rather than observing the managed candidate', async () => {
  let release, entered;
  const waiting = new Promise(resolve => { entered = resolve; });
  const barrier = new Promise(resolve => { release = resolve; });
  const f = fixture({ commit: async () => { entered(); await barrier; } });
  const mutation = f.send('mutate-project-items', 95, { quantity: 5 });
  await waiting;
  const read = f.send('shell', 96);
  await Promise.resolve();
  assert.deepEqual(f.calls, ['shell', 'mutate-project-items']);
  assert.equal(f.state().durable.revision, 95);
  release();
  await mutation;
  assert.equal((await read).accepted, true);
  assert.equal(f.state().durable.revision, 96);
});

test('a failed commit fences all subsequent reads until the transport replaces the Worker', async () => {
  const f = fixture({ commit: async () => { throw new Error('disk full'); } });
  await assert.rejects(f.send('mutate-project-items', 95, { quantity: 5 }), /disk full/);
  await assert.rejects(f.send('shell', 96), /disk full/);
  assert.deepEqual(f.calls, ['shell', 'mutate-project-items']);
  assert.equal(f.state().durable.revision, 95);
  assert.equal(f.state().durable.storedPlan.quantity, 4);
});

test('plan replacement also reconciles an unpublished revision before committing', async () => {
  const f = fixture({ managedRevision: 96 });
  const result = await f.send('replace', 96, { storedPlan: { quantity: 7 }, trackStoredPlanIdentity: false });
  assert.equal(result.accepted, true);
  assert.equal(f.state().durable.storedPlan.quantity, 7);
  assert.equal(f.state().managed.revision, f.state().durable.revision);
});

test('the first edit also recovers when the UI still observes the last committed revision', async () => {
  const f = fixture({ managedRevision: 96 });
  const result = await f.send('mutate-project-items', 95, { quantity: 6 });
  assert.equal(result.accepted, true);
  assert.equal(f.state().durable.storedPlan.quantity, 6);
});

test('a rejected replacement cannot strand its candidate as the active plan', async () => {
  const f = fixture({ rejectReplacement: true });
  const result = await f.send('replace', 95, { storedPlan: { quantity: 99 } });
  assert.equal(result.accepted, false);
  assert.equal(result.message, 'Invalid replacement');
  assert.equal(result.projection.quantity, 4);
  assert.equal(f.state().managed.quantity, 4);
  assert.equal(f.state().durable.storedPlan.quantity, 4);
  assert.equal(f.state().managed.revision, f.state().durable.revision);
});
