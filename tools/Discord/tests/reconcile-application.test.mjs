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
