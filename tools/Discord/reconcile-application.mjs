import { appendFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { discordRequest, requireValue } from './reconcile-guild.mjs';

function requireInteractionsEndpoint(configuration) {
  const raw = requireValue(configuration, 'DISCORD_INTERACTIONS_ENDPOINT_URL');
  let endpoint;
  try {
    endpoint = new URL(raw);
  } catch {
    throw new Error('DISCORD_INTERACTIONS_ENDPOINT_URL must be an absolute URL.');
  }
  if (endpoint.protocol !== 'https:' ||
      endpoint.username ||
      endpoint.password ||
      endpoint.search ||
      endpoint.hash ||
      endpoint.pathname !== '/api/discord/interactions') {
    throw new Error(
      'DISCORD_INTERACTIONS_ENDPOINT_URL must be an HTTPS /api/discord/interactions endpoint.');
  }
  return endpoint.href;
}

function assertApplication(application, applicationId, endpoint) {
  if (application?.id !== applicationId) {
    throw new Error(
      'The Discord credential does not belong to the configured application.');
  }
  if (application.interactions_endpoint_url !== endpoint) {
    throw new Error(
      'Discord did not retain the configured interactions endpoint URL.');
  }
}

export async function reconcileDiscordApplication(
  configuration,
  fetchImpl = fetch) {
  const token = requireValue(configuration, 'DISCORD_BOT_TOKEN');
  if (!/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token)) {
    throw new Error('DISCORD_BOT_TOKEN is not a Discord bot token.');
  }
  const applicationId = requireValue(configuration, 'DISCORD_APPLICATION_ID');
  const endpoint = requireInteractionsEndpoint(configuration);

  const current = await discordRequest(
    fetchImpl,
    token,
    '/applications/@me');
  if (current?.id !== applicationId) {
    throw new Error(
      'The Discord credential does not belong to the configured application.');
  }

  const changed = current.interactions_endpoint_url !== endpoint;
  if (changed) {
    const updated = await discordRequest(
      fetchImpl,
      token,
      '/applications/@me',
      {
        method: 'PATCH',
        body: JSON.stringify({ interactions_endpoint_url: endpoint })
      });
    assertApplication(updated, applicationId, endpoint);
  }

  const readback = await discordRequest(
    fetchImpl,
    token,
    '/applications/@me');
  assertApplication(readback, applicationId, endpoint);
  return {
    applicationId,
    interactionsEndpointUrl: endpoint,
    changed
  };
}

async function main() {
  const result = await reconcileDiscordApplication(process.env);
  if (process.env.GITHUB_OUTPUT) {
    await appendFile(
      process.env.GITHUB_OUTPUT,
      `application_id=${result.applicationId}\n` +
        `interactions_endpoint_url=${result.interactionsEndpointUrl}\n` +
        `changed=${result.changed}\n`,
      'utf8');
  }
  console.log(
    `Discord application ${result.applicationId} interaction delivery verified at ` +
      `${result.interactionsEndpointUrl}`);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
