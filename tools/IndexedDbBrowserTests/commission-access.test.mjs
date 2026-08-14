import assert from 'node:assert/strict';
import test from 'node:test';
import {
  CommissionClientError,
  CommissionBriefApiClient,
  ParticipantAccessStore,
  PortableClaimAccountStore,
  createCommandAuthorization,
  loadPortableCommissionProjection,
  readCapabilityFragment,
  replaceClaimCapabilityFragment,
  replaceParticipantCapabilityFragment
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

test('Discord sign-in return restores the exact claim and saves account authority', () => {
  const storage = new MemoryStorage();
  const store = new PortableClaimAccountStore('public-id', storage);
  store.beginSignIn(claimSecret);

  const completed = store.completeSignIn({
    hash: `#signin=${accountAccessKey}`
  });

  assert.equal(completed.kind, 'signin');
  assert.equal(completed.claimCapability, claimSecret);
  assert.equal(completed.account.accessKey, accountAccessKey);
  assert.equal(store.loadAccount().accessKey, accountAccessKey);
  assert.equal(storage.getItem(
    'craftArchitect.companyCommission.discordSignIn.v1.public-id'), null);
});

test('Discord sign-in cannot adopt an account without a saved claim', () => {
  const store = new PortableClaimAccountStore('public-id', new MemoryStorage());

  assert.throws(
    () => store.completeSignIn({ hash: `#signin=${accountAccessKey}` }),
    error => error instanceof CommissionClientError &&
      error.code === 'missing-signin-return');
});

test('restored claim authority replaces the sign-in fragment', () => {
  let replacedUrl = null;
  replaceClaimCapabilityFragment(
    claimSecret,
    {
      replaceState(_state, _title, url) {
        replacedUrl = url;
      }
    },
    { pathname: '/commission.html', search: '?id=public-id' });

  assert.equal(
    replacedUrl,
    `/commission.html?id=public-id#claim=${claimSecret}`);
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

test('canonical projection conflicts retain their server error code', async () => {
  const client = new CommissionBriefApiClient('public-id', async () =>
    new Response(JSON.stringify({
      error: 'projection_conflict',
      message: 'The canonical commission changed.'
    }), {
      status: 409,
      headers: { 'Content-Type': 'application/json' }
    }));

  await assert.rejects(
    client.command('claim', { termsVersion: 1 }, {
      expectedProjectionRevision: 1,
      commandId: '11111111-1111-1111-1111-111111111111',
      claimCapability: claimSecret
    }),
    error => error instanceof CommissionClientError &&
      error.code === 'projection_conflict' &&
      error.status === 409);
});
