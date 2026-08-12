import assert from 'node:assert/strict';
import test from 'node:test';
import {
  CommissionClientError,
  ParticipantAccessStore,
  loadPortableCommissionProjection,
  readCapabilityFragment,
  replaceParticipantCapabilityFragment
} from '../../src/FFXIV Craft Architect.Web/wwwroot/commission-client.js';

const participantSecret = 'p'.repeat(43);
const claimSecret = 'c'.repeat(43);

class MemoryStorage {
  constructor() {
    this.values = new Map();
  }

  getItem(key) {
    return this.values.get(key) ?? null;
  }

  setItem(key, value) {
    this.values.set(key, value);
  }

  removeItem(key) {
    this.values.delete(key);
  }
}

test('private participant fragments are exclusive authority', () => {
  const parsed = readCapabilityFragment({
    hash: `#participant=${participantSecret}`
  });

  assert.equal(parsed.participantCapability, participantSecret);
  assert.equal(parsed.claimCapability, null);
  assert.equal(parsed.recoveryCapability, null);
  assert.equal(parsed.bootstrapToken, null);

  assert.throws(
    () => readCapabilityFragment({
      hash: `#claim=${claimSecret}&participant=${participantSecret}`
    }),
    error => error instanceof CommissionClientError &&
      error.code === 'ambiguous-authority');
});

test('validated participant access becomes a restorable private URL', () => {
  const storage = new MemoryStorage();
  const store = new ParticipantAccessStore('public-id', storage);
  const access = store.adoptParticipantSecret(participantSecret);
  let replacedUrl = null;
  const history = {
    replaceState(_state, _title, url) {
      replacedUrl = url;
    }
  };

  replaceParticipantCapabilityFragment(
    access.participantSecret,
    history,
    { pathname: '/commission', search: '?id=public-id' });

  assert.equal(
    replacedUrl,
    `/commission?id=public-id#participant=${participantSecret}`);
  assert.deepEqual(store.load(), access);
});

test('malformed participant access never replaces saved authority', () => {
  const storage = new MemoryStorage();
  const store = new ParticipantAccessStore('public-id', storage);
  const saved = store.adoptParticipantSecret(participantSecret);

  assert.throws(
    () => store.adoptParticipantSecret('not-valid'),
    error => error instanceof CommissionClientError &&
      error.code === 'invalid-authority');
  assert.deepEqual(store.load(), saved);
});

test('stale private links quietly recover from still-valid saved access', async () => {
  const staleSecret = 's'.repeat(43);
  const calls = [];
  const participantProjection = { kind: 'participant' };
  const client = {
    async load(secret) {
      calls.push(secret ?? null);
      if (secret === staleSecret) {
        throw new CommissionClientError('Stale link', 'unauthorized', 401);
      }
      assert.equal(secret, participantSecret);
      return participantProjection;
    }
  };

  const loaded = await loadPortableCommissionProjection(
    client,
    { participantCapability: staleSecret },
    { participantSecret, pending: null });

  assert.deepEqual(calls, [staleSecret, participantSecret]);
  assert.equal(loaded.projection, participantProjection);
  assert.equal(loaded.participantCapability, null);
});

test('the original shared claim link resumes only locally saved participant access', async () => {
  const calls = [];
  const participantProjection = { kind: 'participant' };
  const client = {
    async load(secret) {
      calls.push(secret ?? null);
      return secret === participantSecret
        ? participantProjection
        : { kind: 'anonymous', public: { isClaimed: true } };
    }
  };

  const loaded = await loadPortableCommissionProjection(
    client,
    { claimCapability: claimSecret, participantCapability: null },
    { participantSecret, pending: null });

  assert.deepEqual(calls, [null, participantSecret]);
  assert.equal(loaded.projection, participantProjection);
  assert.equal(loaded.participantCapability, null);

  calls.length = 0;
  const otherHolder = await loadPortableCommissionProjection(
    client,
    { claimCapability: claimSecret, participantCapability: null },
    null);
  assert.deepEqual(calls, [null]);
  assert.equal(otherHolder.projection.kind, 'anonymous');
});
