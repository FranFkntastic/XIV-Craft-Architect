import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { appendFile, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { gunzipSync, gzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const ZERO_BLOCK = Buffer.alloc(512);
const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const GIT_SHA_PATTERN = /^[0-9a-f]{40}$/;
const ACCEPTANCE_FILES = ['engine-worker.test.mjs', 'package-lock.json', 'package.json', 'truth-suite.mjs'];
const execFileAsync = promisify(execFile);
const COMMON_OUTCOMES = [
  { id: 'suite-structure', subjectKind: 'source' },
  { id: 'dependency-audit', subjectKind: 'source' },
  { id: 'solution-build', subjectKind: 'source' },
  { id: 'spec-tests', subjectKind: 'source' },
  { id: 'contract-tests', subjectKind: 'source' },
  { id: 'web-publish', subjectKind: 'source' },
  { id: 'product-configuration', subjectKind: 'archive' }
];

function targetConfiguration(slot) {
  if (slot === 'local-dev') {
    return {
      procurementRoutesGenerationEnabled: true,
      engineRewriteExecutionEnabled: true,
      worker: {
        required: true,
        status: 'required-enabled-browser-worker',
        outcomeId: 'engine-browser-tests',
        protocolVersion: '4',
        schemaVersion: '1'
      }
    };
  }
  if (slot === 'main') {
    return {
      procurementRoutesGenerationEnabled: false,
      engineRewriteExecutionEnabled: false,
      worker: {
        required: false,
        status: 'production-disabled',
        outcomeId: null,
        protocolVersion: null,
        schemaVersion: null
      }
    };
  }
  throw new Error(`Unsupported deployment slot: ${slot}`);
}

function requiredOutcomes(slot) {
  return [
    ...COMMON_OUTCOMES,
    {
      id: slot === 'local-dev' ? 'engine-browser-tests' : 'deterministic-browser-tests',
      subjectKind: 'archive'
    }
  ];
}

export function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function canonicalJson(value) {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function normalizeRelativePath(value) {
  const normalized = value.split(path.sep).join('/');
  const segments = normalized.split('/');
  if (!normalized || normalized.startsWith('/') || normalized.includes('\\') ||
      segments.some(segment => !segment || segment === '.' || segment === '..')) {
    throw new Error(`Unsafe relative path: ${value}`);
  }
  return normalized;
}

function comparePaths(left, right) {
  return Buffer.compare(Buffer.from(left, 'utf8'), Buffer.from(right, 'utf8'));
}

async function collectFiles(root) {
  const files = [];

  async function visit(directory, relativeDirectory) {
    const entries = await readdir(directory, { withFileTypes: true });
    entries.sort((left, right) => comparePaths(left.name, right.name));
    for (const entry of entries) {
      const absolutePath = path.join(directory, entry.name);
      const relativePath = normalizeRelativePath(path.join(relativeDirectory, entry.name));
      if (entry.isDirectory()) {
        await visit(absolutePath, relativePath);
      } else if (entry.isFile()) {
        const data = await readFile(absolutePath);
        files.push({ path: relativePath, data, bytes: data.length, sha256: sha256(data) });
      } else {
        throw new Error(`Unsupported published entry type: ${relativePath}`);
      }
    }
  }

  await visit(root, '');
  files.sort((left, right) => comparePaths(left.path, right.path));
  if (files.length === 0) throw new Error(`No files found under ${root}`);
  return files;
}

async function collectAcceptanceFiles(root) {
  const files = [];
  for (const relativePath of ACCEPTANCE_FILES) {
    const data = await readFile(path.join(root, relativePath));
    files.push({ path: relativePath, data, bytes: data.length, sha256: sha256(data) });
  }
  return files;
}

function treeSha256(files) {
  const canonicalLines = files.map(file => `${file.sha256}  ${file.bytes}  ${file.path}\n`).join('');
  return sha256(Buffer.from(canonicalLines, 'utf8'));
}

function writeString(header, offset, length, value) {
  const encoded = Buffer.from(value, 'utf8');
  if (encoded.length > length) throw new Error(`Tar field is too long: ${value}`);
  encoded.copy(header, offset);
}

function writeOctal(header, offset, length, value) {
  const octal = value.toString(8);
  if (octal.length > length - 1) throw new Error(`Tar numeric field overflow: ${value}`);
  writeString(header, offset, length, `${octal.padStart(length - 1, '0')}\0`);
}

function splitTarPath(relativePath) {
  if (Buffer.byteLength(relativePath) <= 100) return { name: relativePath, prefix: '' };
  for (let index = relativePath.lastIndexOf('/'); index > 0; index = relativePath.lastIndexOf('/', index - 1)) {
    const prefix = relativePath.slice(0, index);
    const name = relativePath.slice(index + 1);
    if (Buffer.byteLength(prefix) <= 155 && Buffer.byteLength(name) <= 100) return { name, prefix };
  }
  throw new Error(`Published path cannot be represented as ustar: ${relativePath}`);
}

function createTarHeader(file) {
  const header = Buffer.alloc(512);
  const tarPath = splitTarPath(file.path);
  writeString(header, 0, 100, tarPath.name);
  writeOctal(header, 100, 8, 0o644);
  writeOctal(header, 108, 8, 0);
  writeOctal(header, 116, 8, 0);
  writeOctal(header, 124, 12, file.bytes);
  writeOctal(header, 136, 12, 0);
  header.fill(0x20, 148, 156);
  header[156] = 0x30;
  writeString(header, 257, 6, 'ustar\0');
  writeString(header, 263, 2, '00');
  writeString(header, 265, 32, 'root');
  writeString(header, 297, 32, 'root');
  writeString(header, 345, 155, tarPath.prefix);
  const checksum = header.reduce((sum, byte) => sum + byte, 0);
  writeString(header, 148, 8, `${checksum.toString(8).padStart(6, '0')}\0 `);
  return header;
}

export function createDeterministicArchive(files) {
  const chunks = [];
  for (const file of files) {
    chunks.push(createTarHeader(file), file.data);
    const padding = (512 - (file.bytes % 512)) % 512;
    if (padding > 0) chunks.push(Buffer.alloc(padding));
  }
  chunks.push(ZERO_BLOCK, ZERO_BLOCK);
  const archive = gzipSync(Buffer.concat(chunks), { level: 9, mtime: 0 });
  archive[9] = 0xff;
  return archive;
}

function readNullTerminated(buffer, offset, length) {
  const field = buffer.subarray(offset, offset + length);
  const end = field.indexOf(0);
  return field.subarray(0, end === -1 ? field.length : end).toString('utf8');
}

function readOctal(buffer, offset, length) {
  const value = readNullTerminated(buffer, offset, length).trim();
  if (!/^[0-7]+$/.test(value)) throw new Error(`Malformed tar numeric field: ${value}`);
  return Number.parseInt(value, 8);
}

function isZeroBlock(block) {
  return block.every(byte => byte === 0);
}

export function extractDeterministicArchive(archive) {
  if (archive.length < 10 || archive[0] !== 0x1f || archive[1] !== 0x8b) {
    throw new Error('Archive is not gzip data.');
  }
  if (!archive.subarray(4, 8).every(byte => byte === 0)) {
    throw new Error('Archive gzip timestamp is not normalized.');
  }

  const tar = gunzipSync(archive);
  if (tar.length % 512 !== 0) throw new Error('Tar length is not block-aligned.');
  const files = [];
  const seen = new Set();
  let offset = 0;
  let foundEnd = false;

  while (offset < tar.length) {
    const header = tar.subarray(offset, offset + 512);
    offset += 512;
    if (isZeroBlock(header)) {
      if (offset + 512 > tar.length || !isZeroBlock(tar.subarray(offset, offset + 512))) {
        throw new Error('Tar has only one end marker.');
      }
      offset += 512;
      if (!tar.subarray(offset).every(byte => byte === 0)) throw new Error('Tar contains data after end markers.');
      foundEnd = true;
      break;
    }

    const checksumHeader = Buffer.from(header);
    const recordedChecksum = readOctal(header, 148, 8);
    checksumHeader.fill(0x20, 148, 156);
    const calculatedChecksum = checksumHeader.reduce((sum, byte) => sum + byte, 0);
    if (recordedChecksum !== calculatedChecksum) throw new Error('Tar header checksum mismatch.');
    if (readNullTerminated(header, 257, 6) !== 'ustar') throw new Error('Archive entry is not ustar.');
    if (readNullTerminated(header, 263, 2) !== '00') throw new Error('Archive has unsupported ustar version.');
    if (readOctal(header, 100, 8) !== 0o644 || readOctal(header, 108, 8) !== 0 ||
        readOctal(header, 116, 8) !== 0 || readOctal(header, 136, 12) !== 0) {
      throw new Error('Archive metadata is not normalized.');
    }
    if (header[156] !== 0x30 && header[156] !== 0) throw new Error('Archive contains a non-file entry.');

    const name = readNullTerminated(header, 0, 100);
    const prefix = readNullTerminated(header, 345, 155);
    const relativePath = normalizeRelativePath(prefix ? `${prefix}/${name}` : name);
    if (seen.has(relativePath)) throw new Error(`Archive contains duplicate path: ${relativePath}`);
    seen.add(relativePath);

    const bytes = readOctal(header, 124, 12);
    if (!Number.isSafeInteger(bytes) || bytes < 0 || offset + bytes > tar.length) {
      throw new Error(`Archive entry has invalid size: ${relativePath}`);
    }
    const data = Buffer.from(tar.subarray(offset, offset + bytes));
    files.push({ path: relativePath, data, bytes, sha256: sha256(data) });
    offset += bytes + ((512 - (bytes % 512)) % 512);
  }

  if (!foundEnd) throw new Error('Tar end markers are missing.');
  if (files.length === 0) throw new Error('Archive contains no files.');
  const sorted = [...files].sort((left, right) => comparePaths(left.path, right.path));
  if (files.some((file, index) => file.path !== sorted[index].path)) {
    throw new Error('Archive file order is not canonical.');
  }
  return files;
}

function publicFileManifest(files) {
  return {
    schemaVersion: 1,
    algorithm: 'sha256',
    treeSha256: treeSha256(files),
    files: files.map(file => ({ path: file.path, bytes: file.bytes, sha256: file.sha256 }))
  };
}

function assertFileManifest(manifest) {
  if (manifest?.schemaVersion !== 1 || manifest?.algorithm !== 'sha256' || !SHA256_PATTERN.test(manifest?.treeSha256)) {
    throw new Error('Published-file manifest header is invalid.');
  }
  if (!Array.isArray(manifest.files) || manifest.files.length === 0) throw new Error('Published-file manifest is empty.');
  let previous = '';
  for (const file of manifest.files) {
    const relativePath = normalizeRelativePath(file?.path);
    if (previous && comparePaths(relativePath, previous) <= 0) {
      throw new Error('Published-file manifest order is not canonical.');
    }
    if (!Number.isSafeInteger(file?.bytes) || file.bytes < 0 || !SHA256_PATTERN.test(file?.sha256)) {
      throw new Error(`Published-file manifest entry is invalid: ${relativePath}`);
    }
    previous = relativePath;
  }
  const calculated = treeSha256(manifest.files);
  if (calculated !== manifest.treeSha256) throw new Error('Published-file tree hash is invalid.');
}

function assertSameFiles(actualFiles, manifest) {
  const actual = publicFileManifest(actualFiles);
  if (actual.treeSha256 !== manifest.treeSha256 || actual.files.length !== manifest.files.length) {
    throw new Error('Archive contents do not match published-file manifest.');
  }
  for (let index = 0; index < actual.files.length; index += 1) {
    const left = actual.files[index];
    const right = manifest.files[index];
    if (left.path !== right.path || left.bytes !== right.bytes || left.sha256 !== right.sha256) {
      throw new Error(`Archive file mismatch: ${left.path}`);
    }
  }
}

function assertEffectiveConfiguration(files, buildManifest) {
  const config = files.find(file => file.path === 'appsettings.json');
  const releaseIdentity = files.find(file => file.path === 'release.json');
  if (!config) throw new Error('Effective appsettings.json is missing.');
  if (!releaseIdentity || releaseIdentity.sha256 !== buildManifest.artifact.releaseIdentitySha256) {
    throw new Error('Public release identity does not match build manifest.');
  }
  if (config.sha256 !== buildManifest.artifact.effectiveConfigurationSha256) {
    throw new Error('Effective appsettings hash does not match build manifest.');
  }
  const parsed = JSON.parse(config.data.toString('utf8'));
  const expectedBaseAddress = `https://${buildManifest.target.domain}/api/`;
  if (parsed?.LodestoneLookup?.BaseAddress !== expectedBaseAddress) {
    throw new Error(`Effective Lodestone base address is not ${expectedBaseAddress}`);
  }
  const expectedTarget = targetConfiguration(buildManifest.target.slot);
  if (parsed?.ProcurementRoutes?.GenerationEnabled !== expectedTarget.procurementRoutesGenerationEnabled ||
      parsed?.EngineRewrite?.ExecutionEnabled !== expectedTarget.engineRewriteExecutionEnabled ||
      parsed?.EngineAcceptance?.Enabled !== false ||
      parsed?.EngineAcceptance?.UseDeterministicEvidence !== false ||
      buildManifest.target.procurementRoutesGenerationEnabled !== expectedTarget.procurementRoutesGenerationEnabled ||
      buildManifest.target.engineRewriteExecutionEnabled !== expectedTarget.engineRewriteExecutionEnabled) {
    throw new Error('Effective product configuration does not match the deployment slot.');
  }
  const release = JSON.parse(releaseIdentity.data.toString('utf8'));
  if (release?.schemaVersion !== 1 || release?.sourceSha !== buildManifest.source.commitSha ||
      release?.treeSha !== buildManifest.source.treeSha || release?.sourceDirty !== false ||
      release?.runId !== buildManifest.run.id ||
      release?.runAttempt !== buildManifest.run.attempt || release?.release !== buildManifest.run.release ||
      release?.slot !== buildManifest.target.slot ||
      release?.effectiveConfigurationSha256 !== buildManifest.artifact.effectiveConfigurationSha256) {
    throw new Error('Public release identity content is stale, foreign, or malformed.');
  }
}

function validateBuildManifest(manifest) {
  if (manifest?.schemaVersion !== 1 || !GIT_SHA_PATTERN.test(manifest?.source?.commitSha) ||
      !GIT_SHA_PATTERN.test(manifest?.source?.treeSha) || manifest?.source?.dirty !== false) {
    throw new Error('Build manifest source identity is invalid.');
  }
  for (const hash of [
    manifest?.artifact?.archiveSha256,
    manifest?.artifact?.archiveChecksumSha256,
    manifest?.artifact?.fileManifestSha256,
    manifest?.artifact?.publishedFileTreeSha256,
    manifest?.artifact?.effectiveConfigurationSha256,
    manifest?.artifact?.releaseIdentitySha256,
    manifest?.acceptance?.inputsTreeSha256,
    manifest?.acceptance?.harnessTreeSha256,
    manifest?.acceptance?.fixtureTreeSha256
  ]) {
    if (!SHA256_PATTERN.test(hash)) throw new Error('Build manifest hash identity is invalid.');
  }
  for (const fileName of [
    manifest?.artifact?.archiveFile,
    manifest?.artifact?.archiveChecksumFile,
    manifest?.artifact?.fileManifestFile
  ]) {
    if (typeof fileName !== 'string' || !/^[A-Za-z0-9._-]+$/.test(fileName) || path.basename(fileName) !== fileName) {
      throw new Error('Build manifest artifact filename is invalid.');
    }
  }
  const expectedTarget = targetConfiguration(manifest?.target?.slot);
  if (manifest?.target?.procurementRoutesGenerationEnabled !== expectedTarget.procurementRoutesGenerationEnabled ||
      manifest?.target?.engineRewriteExecutionEnabled !== expectedTarget.engineRewriteExecutionEnabled) {
    throw new Error('Build manifest target configuration does not match its deployment slot.');
  }
  if (!/^\d+\.\d+\.\d+/.test(manifest?.runtime?.dotnet) || !/^v\d+\./.test(manifest?.runtime?.node) ||
      !/^\d+\.\d+\.\d+$/.test(manifest?.runtime?.playwright) ||
      !Array.isArray(manifest?.runtime?.browsers) || manifest.runtime.browsers.length !== 2 ||
      manifest.runtime.browsers[0]?.name !== 'chromium' || manifest.runtime.browsers[1]?.name !== 'firefox' ||
      manifest.runtime.browsers.some(browser => !/^\d+$/.test(browser.revision) || !browser.version)) {
    throw new Error('Build manifest runtime identity is invalid.');
  }
  if (JSON.stringify(manifest?.acceptance?.requiredOutcomes) !==
      JSON.stringify(requiredOutcomes(manifest.target.slot))) {
    throw new Error('Build manifest required outcomes are incomplete or reordered.');
  }
  if (manifest?.acceptance?.dotnet?.specTestCases !== 56 ||
      manifest?.acceptance?.dotnet?.contractTestCases !== 94) {
    throw new Error('Build manifest .NET test inventory is incomplete.');
  }
  if (JSON.stringify(manifest?.acceptance?.worker) !== JSON.stringify(expectedTarget.worker)) {
    throw new Error('Worker acceptance boundary is not explicit.');
  }
}

async function writeEffectiveConfiguration(root, domain, slot) {
  if (!/^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$/i.test(domain)) {
    throw new Error(`Invalid target domain: ${domain}`);
  }
  const target = targetConfiguration(slot);
  const config = {
    LodestoneLookup: { BaseAddress: `https://${domain}/api/` },
    ProcurementRoutes: { GenerationEnabled: target.procurementRoutesGenerationEnabled },
    EngineRewrite: { ExecutionEnabled: target.engineRewriteExecutionEnabled },
    EngineAcceptance: { Enabled: false, UseDeterministicEvidence: false }
  };
  await writeFile(path.join(root, 'appsettings.json'), canonicalJson(config), 'utf8');
}

async function writeReleaseIdentity(root, identity) {
  await writeFile(path.join(root, 'release.json'), canonicalJson({
    schemaVersion: 1,
    sourceSha: identity.sourceSha,
    treeSha: identity.treeSha,
    sourceDirty: false,
    runId: identity.runId,
    runAttempt: identity.runAttempt,
    release: identity.release,
    slot: identity.slot,
    effectiveConfigurationSha256: identity.effectiveConfigurationSha256
  }), 'utf8');
}

function requiredOption(options, name) {
  const value = options[name];
  if (typeof value !== 'string' || value.length === 0) throw new Error(`Missing --${name}.`);
  return value;
}

async function verifySourceIdentity(options, sourceSha, treeSha) {
  const repositoryRoot = options['repository-root'] ?? '.';
  if (path.isAbsolute(repositoryRoot) || repositoryRoot.split(/[\\/]/).includes('..')) {
    throw new Error('Repository root must be repository-relative.');
  }
  const git = async args => {
    const result = await execFileAsync('git', ['-C', repositoryRoot, ...args], { encoding: 'utf8' });
    return result.stdout.trim();
  };
  const [actualSourceSha, actualTreeSha, status] = await Promise.all([
    git(['rev-parse', 'HEAD']),
    git(['rev-parse', 'HEAD^{tree}']),
    git(['status', '--porcelain', '--untracked-files=all'])
  ]);
  if (actualSourceSha !== sourceSha || actualTreeSha !== treeSha) {
    throw new Error('Artifact source identity does not match checked-out HEAD.');
  }
  if (status) throw new Error('Artifact source Git tree is dirty.');
}

function parseOptions(args) {
  const options = {};
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    if (!argument.startsWith('--')) throw new Error(`Unexpected argument: ${argument}`);
    const name = argument.slice(2);
    const value = args[index + 1];
    if (!value || value.startsWith('--')) throw new Error(`Missing value for ${argument}.`);
    options[name] = value;
    index += 1;
  }
  return options;
}

async function readPlaywrightRuntime(acceptanceRoot, acceptanceFiles) {
  const lock = acceptanceFiles.find(file => file.path === 'package-lock.json');
  if (!lock) throw new Error('Browser acceptance package-lock.json is missing.');
  const parsed = JSON.parse(lock.data.toString('utf8'));
  const version = parsed?.packages?.['node_modules/playwright']?.version;
  if (typeof version !== 'string' || !/^\d+\.\d+\.\d+$/.test(version)) {
    throw new Error('Pinned Playwright version is missing from package lock.');
  }
  const browsersDocument = JSON.parse(await readFile(
    path.join(acceptanceRoot, 'node_modules', 'playwright-core', 'browsers.json'), 'utf8'));
  const browsers = ['chromium', 'firefox'].map(name => {
    const browser = browsersDocument?.browsers?.find(candidate => candidate.name === name);
    if (!browser || typeof browser.revision !== 'string' || typeof browser.browserVersion !== 'string') {
      throw new Error(`Pinned ${name} runtime identity is missing.`);
    }
    return { name, revision: browser.revision, version: browser.browserVersion };
  });
  return { version, browsers };
}

async function appendGithubOutputs(values) {
  if (!process.env.GITHUB_OUTPUT) return;
  const lines = Object.entries(values).map(([name, value]) => `${name}=${value}\n`).join('');
  await appendFile(process.env.GITHUB_OUTPUT, lines, 'utf8');
}

export async function createArtifact(options, behavior = {}) {
  const root = requiredOption(options, 'root');
  const outDir = requiredOption(options, 'out-dir');
  const archiveName = requiredOption(options, 'archive-name');
  const domain = requiredOption(options, 'domain');
  const slot = requiredOption(options, 'slot');
  const sourceSha = requiredOption(options, 'source-sha');
  const treeSha = requiredOption(options, 'tree-sha');
  const sourceDirty = requiredOption(options, 'source-dirty');
  const sourceRef = requiredOption(options, 'source-ref');
  const runId = requiredOption(options, 'run-id');
  const runAttempt = requiredOption(options, 'run-attempt');
  const release = requiredOption(options, 'release');
  const dotnetVersion = requiredOption(options, 'dotnet-version');
  const acceptanceRoot = requiredOption(options, 'acceptance-root');
  const fixturesRoot = requiredOption(options, 'fixtures-root');

  if (!GIT_SHA_PATTERN.test(sourceSha) || !GIT_SHA_PATTERN.test(treeSha)) throw new Error('Source Git identity is invalid.');
  if (sourceDirty !== 'false') throw new Error('Artifact source must be a clean Git tree.');
  if (!/^[A-Za-z0-9._-]+$/.test(archiveName) || !archiveName.endsWith('.tar.gz')) throw new Error('Archive name is invalid.');
  if (!/^[A-Za-z0-9._-]+$/.test(slot) || !/^[A-Za-z0-9._-]+$/.test(release)) throw new Error('Target identity is invalid.');
  if (!/^\d+$/.test(runId) || !/^\d+$/.test(runAttempt)) throw new Error('Run identity is invalid.');
  if (behavior.verifySource !== false) await verifySourceIdentity(options, sourceSha, treeSha);

  const target = targetConfiguration(slot);
  await writeEffectiveConfiguration(root, domain, slot);
  const effectiveConfiguration = await readFile(path.join(root, 'appsettings.json'));
  await writeReleaseIdentity(root, {
    sourceSha,
    treeSha,
    runId,
    runAttempt,
    release,
    slot,
    effectiveConfigurationSha256: sha256(effectiveConfiguration)
  });
  const files = await collectFiles(root);
  const config = files.find(file => file.path === 'appsettings.json');
  const releaseIdentity = files.find(file => file.path === 'release.json');
  if (!config) throw new Error('Effective appsettings.json was not published.');
  if (!releaseIdentity) throw new Error('Public release identity was not published.');
  const fileManifest = publicFileManifest(files);
  const fileManifestText = canonicalJson(fileManifest);
  const archive = createDeterministicArchive(files);
  const acceptanceFiles = await collectAcceptanceFiles(acceptanceRoot);
  const fixtureFiles = await collectFiles(fixturesRoot);
  const playwrightRuntime = await readPlaywrightRuntime(acceptanceRoot, acceptanceFiles);
  const harnessTreeSha = treeSha256(acceptanceFiles);
  const fixtureTreeSha = treeSha256(fixtureFiles);
  const acceptanceTreeSha = sha256(Buffer.from(
    `harness ${harnessTreeSha}\nfixtures ${fixtureTreeSha}\n`, 'utf8'));

  await mkdir(outDir, { recursive: true });
  const archivePath = path.join(outDir, archiveName);
  const archiveChecksumPath = path.join(outDir, 'archive-sha256.txt');
  const fileManifestPath = path.join(outDir, 'published-files.json');
  const buildManifestPath = path.join(outDir, 'build-manifest.json');
  const archiveSha256 = sha256(archive);
  const archiveChecksumText = `${archiveSha256}  ${archiveName}\n`;
  await writeFile(archivePath, archive);
  await writeFile(archiveChecksumPath, archiveChecksumText, 'utf8');
  await writeFile(fileManifestPath, fileManifestText, 'utf8');

  const buildManifest = {
    schemaVersion: 1,
    source: { commitSha: sourceSha, treeSha, ref: sourceRef, dirty: false },
    target: {
      slot,
      domain,
      procurementRoutesGenerationEnabled: target.procurementRoutesGenerationEnabled,
      engineRewriteExecutionEnabled: target.engineRewriteExecutionEnabled
    },
    run: { id: runId, attempt: runAttempt, release },
    runtime: {
      dotnet: dotnetVersion,
      node: process.version,
      playwright: playwrightRuntime.version,
      browsers: playwrightRuntime.browsers
    },
    artifact: {
      archiveFile: archiveName,
      archiveBytes: archive.length,
      archiveSha256,
      archiveChecksumFile: 'archive-sha256.txt',
      archiveChecksumSha256: sha256(Buffer.from(archiveChecksumText, 'utf8')),
      fileManifestFile: 'published-files.json',
      fileManifestSha256: sha256(Buffer.from(fileManifestText, 'utf8')),
      publishedFileTreeSha256: fileManifest.treeSha256,
      effectiveConfigurationSha256: config.sha256,
      releaseIdentitySha256: releaseIdentity.sha256
    },
    acceptance: {
      inputsTreeSha256: acceptanceTreeSha,
      harnessTreeSha256: harnessTreeSha,
      fixtureTreeSha256: fixtureTreeSha,
      requiredOutcomes: requiredOutcomes(slot),
      dotnet: { specTestCases: 56, contractTestCases: 94 },
      worker: target.worker
    }
  };
  const buildManifestText = canonicalJson(buildManifest);
  await writeFile(buildManifestPath, buildManifestText, 'utf8');
  await appendGithubOutputs({
    archive_path: archivePath.split(path.sep).join('/'),
    archive_name: archiveName,
    archive_sha256: buildManifest.artifact.archiveSha256,
    archive_checksum_path: archiveChecksumPath.split(path.sep).join('/'),
    effective_configuration_sha256: buildManifest.artifact.effectiveConfigurationSha256,
    build_manifest_path: buildManifestPath.split(path.sep).join('/'),
    file_manifest_path: fileManifestPath.split(path.sep).join('/'),
    acceptance_harness_sha256: buildManifest.acceptance.harnessTreeSha256,
    acceptance_fixture_sha256: buildManifest.acceptance.fixtureTreeSha256
  });
  return { archivePath, archiveChecksumPath, fileManifestPath, buildManifestPath, buildManifest };
}

export async function verifyArtifact(options) {
  const archivePath = requiredOption(options, 'archive');
  const fileManifestPath = requiredOption(options, 'file-manifest');
  const buildManifestPath = requiredOption(options, 'build-manifest');
  const extractPath = options.extract;
  const acceptanceRoot = options['acceptance-root'];
  const fixturesRoot = options['fixtures-root'];
  if (Boolean(acceptanceRoot) !== Boolean(fixturesRoot)) {
    throw new Error('Acceptance harness and fixture roots must be verified together.');
  }
  const archive = await readFile(archivePath);
  const fileManifestBytes = await readFile(fileManifestPath);
  const fileManifest = JSON.parse(fileManifestBytes.toString('utf8'));
  const buildManifest = JSON.parse(await readFile(buildManifestPath, 'utf8'));
  validateBuildManifest(buildManifest);
  const archiveChecksumPath = path.join(path.dirname(buildManifestPath), buildManifest?.artifact?.archiveChecksumFile ?? '');
  const archiveChecksumBytes = await readFile(archiveChecksumPath);

  assertFileManifest(fileManifest);
  if (path.basename(archivePath) !== buildManifest.artifact.archiveFile ||
      archive.length !== buildManifest.artifact.archiveBytes ||
      sha256(archive) !== buildManifest.artifact.archiveSha256) {
    throw new Error('Archive identity does not match build manifest.');
  }
  const expectedArchiveChecksum = `${buildManifest.artifact.archiveSha256}  ${buildManifest.artifact.archiveFile}\n`;
  if (path.basename(archiveChecksumPath) !== buildManifest.artifact.archiveChecksumFile ||
      archiveChecksumBytes.toString('utf8') !== expectedArchiveChecksum ||
      sha256(archiveChecksumBytes) !== buildManifest.artifact.archiveChecksumSha256) {
    throw new Error('Archive checksum file does not match build manifest.');
  }
  if (path.basename(fileManifestPath) !== buildManifest.artifact.fileManifestFile ||
      sha256(fileManifestBytes) !== buildManifest.artifact.fileManifestSha256 ||
      fileManifest.treeSha256 !== buildManifest.artifact.publishedFileTreeSha256) {
    throw new Error('Published-file identity does not match build manifest.');
  }

  const files = extractDeterministicArchive(archive);
  assertSameFiles(files, fileManifest);
  assertEffectiveConfiguration(files, buildManifest);

  if (acceptanceRoot && fixturesRoot) {
    const acceptanceFiles = await collectAcceptanceFiles(acceptanceRoot);
    const fixtureFiles = await collectFiles(fixturesRoot);
    const harnessTreeSha = treeSha256(acceptanceFiles);
    const fixtureTreeSha = treeSha256(fixtureFiles);
    const inputsTreeSha = sha256(Buffer.from(
      `harness ${harnessTreeSha}\nfixtures ${fixtureTreeSha}\n`, 'utf8'));
    if (harnessTreeSha !== buildManifest.acceptance.harnessTreeSha256 ||
        fixtureTreeSha !== buildManifest.acceptance.fixtureTreeSha256 ||
        inputsTreeSha !== buildManifest.acceptance.inputsTreeSha256) {
      throw new Error('Acceptance inputs are stale, foreign, or malformed.');
    }
  }

  if (extractPath) {
    if (existsSync(extractPath)) throw new Error(`Extraction target already exists: ${extractPath}`);
    await mkdir(extractPath, { recursive: true });
    for (const file of files) {
      const outputPath = path.join(extractPath, ...file.path.split('/'));
      await mkdir(path.dirname(outputPath), { recursive: true });
      await writeFile(outputPath, file.data, { mode: 0o644 });
    }
    const extractedFiles = await collectFiles(extractPath);
    assertSameFiles(extractedFiles, fileManifest);
  }

  return buildManifest;
}

async function main() {
  const [command, ...args] = process.argv.slice(2);
  const options = parseOptions(args);
  if (command === 'create') {
    const result = await createArtifact(options);
    console.log(`Created ${result.buildManifest.artifact.archiveFile} (${result.buildManifest.artifact.archiveSha256}).`);
  } else if (command === 'verify') {
    const manifest = await verifyArtifact(options);
    console.log(`Verified ${manifest.artifact.archiveFile} (${manifest.artifact.archiveSha256}).`);
  } else {
    throw new Error('Usage: truthful-artifact.mjs <create|verify> [options]');
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  main().catch(error => {
    console.error(error.stack ?? error.message);
    process.exitCode = 1;
  });
}
