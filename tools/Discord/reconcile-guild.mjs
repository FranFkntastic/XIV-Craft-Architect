import { appendFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

const DiscordApi = 'https://discord.com/api/v10';
const Administrator = 1n << 3n;
const RequiredPermissions =
  (1n << 4n) | // Manage Channels
  (1n << 10n) | // View Channels
  (1n << 11n) | // Send Messages
  (1n << 14n); // Embed Links

export function requireValue(configuration, name) {
  const value = configuration[name]?.trim();
  if (!value) throw new Error(`${name} is required.`);
  return value;
}

export async function discordRequest(fetchImpl, token, path, init = {}) {
  for (let attempt = 0; attempt < 4; attempt += 1) {
    const response = await fetchImpl(`${DiscordApi}${path}`, {
      ...init,
      signal: AbortSignal.timeout(15_000),
      headers: {
        authorization: `Bot ${token}`,
        ...(init.body ? { 'content-type': 'application/json' } : {}),
        ...init.headers
      }
    });
    const text = await response.text();
    let payload = null;
    try {
      payload = text ? JSON.parse(text) : null;
    } catch {
      payload = null;
    }
    if (response.ok) return payload;

    if (response.status === 429 && attempt < 3) {
      const retryAfterSeconds = Number(
        payload?.retry_after ?? response.headers.get('retry-after'));
      if (!Number.isFinite(retryAfterSeconds) ||
          retryAfterSeconds < 0 ||
          retryAfterSeconds > 30) {
        throw new Error(`Discord returned an invalid retry interval for ${path}.`);
      }
      await new Promise(resolve => setTimeout(resolve, retryAfterSeconds * 1000));
      continue;
    }
    if (response.status >= 500 && attempt < 3) {
      await new Promise(resolve => setTimeout(resolve, 250 * (2 ** attempt)));
      continue;
    }

    const detail = payload?.message ?? `HTTP ${response.status}`;
    throw new Error(`Discord ${init.method ?? 'GET'} ${path} failed: ${detail}.`);
  }
  throw new Error(`Discord ${init.method ?? 'GET'} ${path} exhausted its retry budget.`);
}

export async function reconcileDiscordGuild(configuration, fetchImpl = fetch) {
  const token = requireValue(configuration, 'DISCORD_BOT_TOKEN');
  if (!/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token)) {
    throw new Error('DISCORD_BOT_TOKEN is not a Discord bot token.');
  }
  const applicationId = requireValue(configuration, 'DISCORD_APPLICATION_ID');
  const guildId = requireValue(configuration, 'DISCORD_GUILD_ID');
  const channelName = requireValue(configuration, 'DISCORD_CHANNEL_NAME');
  const parentId = requireValue(configuration, 'DISCORD_CHANNEL_PARENT_ID');

  const bot = await discordRequest(fetchImpl, token, '/users/@me');
  if (bot.id !== applicationId || bot.bot !== true) {
    throw new Error('The Discord credential does not belong to the configured bot application.');
  }

  const guilds = await discordRequest(fetchImpl, token, '/users/@me/guilds');
  const guild = guilds.find(candidate => candidate.id === guildId);
  if (!guild) {
    throw new Error(`The bot has not joined configured guild ${guildId}.`);
  }

  const permissions = BigInt(guild.permissions);
  if ((permissions & Administrator) === 0n &&
      (permissions & RequiredPermissions) !== RequiredPermissions) {
    throw new Error('The bot lacks Manage Channels, View Channels, Send Messages, or Embed Links.');
  }

  const channels = await discordRequest(fetchImpl, token, `/guilds/${guildId}/channels`);
  const matches = channels.filter(channel =>
    channel.name === channelName &&
    channel.parent_id === parentId &&
    channel.type === 0);
  if (matches.length > 1) {
    throw new Error(`More than one #${channelName} text channel exists under the configured category.`);
  }

  const channel = matches[0] ?? await discordRequest(
    fetchImpl,
    token,
    `/guilds/${guildId}/channels`,
    {
      method: 'POST',
      body: JSON.stringify({
        name: channelName,
        type: 0,
        parent_id: parentId,
        topic: 'Craft Architect commission briefs: scope, material responsibility, payment basis, and evidence in one place.'
      })
    });

  const commands = await discordRequest(
    fetchImpl,
    token,
    `/applications/${applicationId}/guilds/${guildId}/commands`,
    {
      method: 'PUT',
      body: JSON.stringify([
        {
          name: 'commission',
          description: 'Publish a Craft Architect commission brief',
          type: 1,
          dm_permission: false,
          options: [
            {
              type: 1,
              name: 'post',
              description: 'Post a published commission brief',
              options: [
                {
                  type: 3,
                  name: 'brief',
                  description: 'Craft Architect commission brief link',
                  required: true
                }
              ]
            }
          ]
        }
      ])
    });
  if (commands.length !== 1 || commands[0].name !== 'commission') {
    throw new Error('Discord did not retain the managed commission command.');
  }

  return {
    applicationId,
    botId: bot.id,
    guildId,
    guildName: guild.name,
    channelId: channel.id,
    channelName: channel.name,
    commandId: commands[0].id
  };
}

async function main() {
  const result = await reconcileDiscordGuild(process.env);
  if (process.env.GITHUB_OUTPUT) {
    await appendFile(
      process.env.GITHUB_OUTPUT,
      `channel_id=${result.channelId}\ncommand_id=${result.commandId}\n`,
      'utf8');
  }
  console.log(
    `Discord integration ready: ${result.guildName} / #${result.channelName} / commission.`);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
