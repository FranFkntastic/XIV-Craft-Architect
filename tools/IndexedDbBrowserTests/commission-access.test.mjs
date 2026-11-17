import assert from 'node:assert/strict';
import test from 'node:test';
import {
  CommissionClientError,
  CommissionBriefApiClient,
  ParticipantAccessStore,
  PortableClaimAccountStore,
  createCommandAuthorization,
  loadPortableCommissionProjection
} from '../../src/FFXIV Craft Architect.Web/wwwroot/commission-client.js';

const participantSecret = 'p'.repeat(43);
const claimSecret = 'c'.repeat(43);
const accountAccessKey = 'a'.repeat(43);

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

test('Discord sign-in cannot adopt an account without a saved claim', () => {
  const store = new PortableClaimAccountStore('public-id', new MemoryStorage());

  assert.throws(
    () => store.completeSignIn({ hash: `#signin=${accountAccessKey}` }),
    error => error instanceof CommissionClientError &&
      error.code === 'missing-signin-return');
});

test('verified portable claims send account authority outside the command body', async () => {
  let request = null;
  const client = new CommissionBriefApiClient('public-id', async (url, options) => {
    request = { url, options };
    return new Response(JSON.stringify({ accepted: true }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    });
  });
  const authorization = createCommandAuthorization(
    { public: { projectionRevision: 4 } },
    null,
    {
      claimCapability: claimSecret,
      accountAccessKey
    },
    '11111111-1111-1111-1111-111111111111');

  await client.command('claim', { termsVersion: 1 }, authorization);
  const body = JSON.parse(request.options.body);

  assert.equal(request.options.headers['X-Profile-Key'], accountAccessKey);
  assert.equal(body.claimCapability, claimSecret);
  assert.equal('accountAccessKey' in body, false);
});
