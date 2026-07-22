import { spawnSync } from 'node:child_process';

function run(executable, args) {
  const result = spawnSync(executable, args, {
    encoding: 'utf8',
    shell: false,
    maxBuffer: 16 * 1024 * 1024
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`${executable} dependency audit failed (${result.status}).\n${result.stderr || result.stdout}`);
  }
  return result.stdout;
}

function collectVulnerabilities(value, found = []) {
  if (Array.isArray(value)) {
    for (const item of value) collectVulnerabilities(item, found);
  } else if (value && typeof value === 'object') {
    if (Array.isArray(value.vulnerabilities)) found.push(...value.vulnerabilities);
    for (const child of Object.values(value)) collectVulnerabilities(child, found);
  }
  return found;
}

const dotnetReport = JSON.parse(run('dotnet', [
  'list', 'FFXIV Craft Architect.sln', 'package', '--vulnerable', '--include-transitive',
  '--no-restore', '--format', 'json'
]));
const expectedProjects = [
  'src/FFXIV Craft Architect.ContractTests/FFXIV Craft Architect.ContractTests.csproj',
  'src/FFXIV Craft Architect.Core/FFXIV Craft Architect.Core.csproj',
  'src/FFXIV Craft Architect.LocalLauncher/FFXIV Craft Architect.LocalLauncher.csproj',
  'src/FFXIV Craft Architect.LodestoneLookup/FFXIV Craft Architect.LodestoneLookup.csproj',
  'src/FFXIV Craft Architect.SpecTests/FFXIV Craft Architect.SpecTests.csproj',
  'src/FFXIV Craft Architect.Web/FFXIV Craft Architect.Web.csproj'
];
const actualProjects = (dotnetReport.projects ?? [])
  .map(project => project.path?.replaceAll('\\', '/'))
  .map(projectPath => expectedProjects.find(expected => projectPath?.endsWith(`/${expected}`)) ?? projectPath)
  .sort();
if (JSON.stringify(actualProjects) !== JSON.stringify(expectedProjects)) {
  throw new Error('NuGet audit did not inspect all six solution projects.');
}
const nugetVulnerabilities = collectVulnerabilities(dotnetReport);
if (nugetVulnerabilities.length > 0) {
  throw new Error(`NuGet audit found ${nugetVulnerabilities.length} vulnerable package entries.`);
}

const npmArgs = ['--prefix', 'tools/IndexedDbBrowserTests', 'audit', '--audit-level=high', '--json'];
const npmReport = JSON.parse(process.platform === 'win32'
  ? run(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', 'npm', ...npmArgs])
  : run('npm', npmArgs));
const npmVulnerabilities = npmReport?.metadata?.vulnerabilities;
if (!npmVulnerabilities || npmVulnerabilities.high !== 0 || npmVulnerabilities.critical !== 0) {
  throw new Error('npm audit found high or critical vulnerabilities.');
}

console.log('Dependency audit passed for six NuGet projects and pinned browser tooling.');
