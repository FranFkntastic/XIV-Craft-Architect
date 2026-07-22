import { spawn } from 'node:child_process';
import { mkdir, rename, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

function required(value, description) {
  if (!value) throw new Error(`Missing ${description}.`);
  return value;
}

function parseArguments(args) {
  const separator = args.indexOf('--');
  if (separator < 0 || separator === args.length - 1) throw new Error('Command must follow --.');
  const options = {};
  for (let index = 0; index < separator; index += 2) {
    const name = args[index];
    const value = args[index + 1];
    if (!name?.startsWith('--') || value === undefined) throw new Error(`Malformed option: ${name}`);
    options[name.slice(2)] = value;
  }
  return { options, command: args.slice(separator + 1) };
}

async function writeOutcome(outputPath, outcome) {
  await mkdir(path.dirname(outputPath), { recursive: true });
  const temporaryPath = `${outputPath}.tmp`;
  await writeFile(temporaryPath, `${JSON.stringify(outcome, null, 2)}\n`, 'utf8');
  await rename(temporaryPath, outputPath);
}

function terminateProcess(child) {
  if (!child.pid) return;
  try {
    if (process.platform === 'win32') child.kill('SIGKILL');
    else process.kill(-child.pid, 'SIGKILL');
  } catch (error) {
    if (error.code !== 'ESRCH') throw error;
  }
}

export async function runCommand({ options, command }) {
  const id = required(options.id, '--id');
  const outputPath = required(options.out, '--out');
  const subjectKind = options.subject ?? 'source';
  if (!/^[a-z0-9-]+$/.test(id)) throw new Error(`Invalid outcome id: ${id}`);
  if (!['source', 'archive'].includes(subjectKind)) throw new Error(`Invalid subject kind: ${subjectKind}`);

  const sourceSha = required(process.env.TRUTHFUL_SOURCE_SHA, 'TRUTHFUL_SOURCE_SHA');
  const subjectSha = subjectKind === 'source'
    ? sourceSha
    : required(process.env.TRUTHFUL_ARTIFACT_SHA, 'TRUTHFUL_ARTIFACT_SHA');
  const runId = required(process.env.TRUTHFUL_RUN_ID, 'TRUTHFUL_RUN_ID');
  const runAttempt = required(process.env.TRUTHFUL_RUN_ATTEMPT, 'TRUTHFUL_RUN_ATTEMPT');
  const timeoutSeconds = Number.parseInt(required(options['timeout-seconds'], '--timeout-seconds'), 10);
  if (!Number.isSafeInteger(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 1800) {
    throw new Error('Timeout must be an integer from 1 through 1800 seconds.');
  }

  const cwd = options.cwd ?? '.';
  if (path.isAbsolute(cwd) || cwd.split(/[\\/]/).includes('..')) throw new Error('Command cwd must be repository-relative.');
  const baseOutcome = {
    schemaVersion: 1,
    id,
    run: { id: runId, attempt: runAttempt },
    sourceCommitSha: sourceSha,
    subject: { kind: subjectKind, identity: subjectSha },
    command: { executable: command[0], arguments: command.slice(1), cwd },
    timeoutSeconds
  };

  let timedOut = false;
  const spawnCommand = process.platform === 'win32' && command[0] === 'npm'
    ? process.env.ComSpec || 'cmd.exe'
    : command[0];
  const spawnArguments = process.platform === 'win32' && command[0] === 'npm'
    ? ['/d', '/s', '/c', 'npm', ...command.slice(1)]
    : command.slice(1);
  const child = spawn(spawnCommand, spawnArguments, {
    cwd,
    detached: process.platform !== 'win32',
    shell: false,
    stdio: 'inherit'
  });
  const timer = setTimeout(() => {
    timedOut = true;
    terminateProcess(child);
  }, timeoutSeconds * 1000);

  let result;
  try {
    result = await new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('exit', (code, signal) => resolve({ code, signal }));
    });
  } catch (error) {
    await writeOutcome(outputPath, {
      ...baseOutcome,
      status: 'failed',
      exitCode: null,
      signal: null,
      launchError: error.code ?? 'unknown'
    });
    process.exitCode = 1;
    return 'failed';
  } finally {
    clearTimeout(timer);
  }

  const status = timedOut ? 'timed-out' : result.code === 0 ? 'passed' : 'failed';
  await writeOutcome(outputPath, {
    ...baseOutcome,
    status,
    exitCode: result.code,
    signal: result.signal
  });
  if (status !== 'passed') process.exitCode = timedOut ? 124 : (result.code || 1);
  return status;
}

async function main() {
  await runCommand(parseArguments(process.argv.slice(2)));
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  main().catch(error => {
    console.error(error.stack ?? error.message);
    process.exitCode = 1;
  });
}
