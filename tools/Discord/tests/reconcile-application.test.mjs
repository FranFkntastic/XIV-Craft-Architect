import test from 'node:test';
import assert from 'node:assert/strict';
import { reconcileDiscordApplication } from '../reconcile-application.mjs';

const applicationId = '1536618931811262464';
const endpoint = 'https://dev.xivcraftarchitect.com/api/discord/interactions';
const configuration = {
  DISCORD_BOT_TOKEN: 'header.payload.signature',
  DISCORD_APPLICATION_ID: applicationId,
  DISCORD_INTERACTIONS_ENDPOINT_URL: endpoint
};

function jsonResponse(payload, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { 'content-type': 'application/json' }
  });
}

test('keeps a correctly paired application and verifies readback', async () => {
  const requests = [];
  const fetchImpl = async (url, init) => {
    requests.push({ url, method: init.method ?? 'GET' });
    return jsonResponse({
      id: applicationId,
      interactions_endpoint_url: endpoint
    });
  };

  const result = await reconcileDiscordApplication(configuration, fetchImpl);

  assert.equal(result.changed, false);
  assert.deepEqual(requests.map(request => request.method), ['GET', 'GET']);
});

test('repairs a mismatched endpoint and verifies the retained value', async () => {
  const requests = [];
  const responses = [
    { id: applicationId, interactions_endpoint_url: 'https://wrong.example/interactions' },
    { id: applicationId, interactions_endpoint_url: endpoint },
    { id: applicationId, interactions_endpoint_url: endpoint }
  ];
  const fetchImpl = async (url, init) => {
    requests.push({ url, method: init.method ?? 'GET', body: init.body });
    return jsonResponse(responses.shift());
  };

  const result = await reconcileDiscordApplication(configuration, fetchImpl);

  assert.equal(result.changed, true);
  assert.deepEqual(
    requests.map(request => request.method),
    ['GET', 'PATCH', 'GET']);
  assert.deepEqual(
    JSON.parse(requests[1].body),
    { interactions_endpoint_url: endpoint });
});

test('refuses to mutate an application that does not match configuration', async () => {
  let requestCount = 0;
  const fetchImpl = async () => {
    requestCount += 1;
    return jsonResponse({
      id: '1532122273543360592',
      interactions_endpoint_url: endpoint
    });
  };

  await assert.rejects(
    reconcileDiscordApplication(configuration, fetchImpl),
    /credential does not belong to the configured application/);
  assert.equal(requestCount, 1);
});

test('fails when Discord readback does not retain the endpoint', async () => {
  const responses = [
    { id: applicationId, interactions_endpoint_url: 'https://wrong.example/interactions' },
    { id: applicationId, interactions_endpoint_url: endpoint },
    { id: applicationId, interactions_endpoint_url: 'https://wrong.example/interactions' }
  ];

  await assert.rejects(
    reconcileDiscordApplication(
      configuration,
      async () => jsonResponse(responses.shift())),
    /did not retain the configured interactions endpoint URL/);
});
