#!/usr/bin/env node

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const errors = [];

const requiredDocs = [
  'docs/README.md',
  'docs/governance/documentation.md',
  'docs/architecture/system-overview.md',
  'docs/architecture/data-publication-flow.md',
  'docs/architecture/data-storage.md',
  'docs/components/web-app.md',
  'docs/components/service-api.md',
  'docs/components/worker.md',
  'docs/components/shared-code.md',
  'docs/reference/api-contract.md',
  'docs/reference/configuration.md',
  'docs/reference/feature-flags.md',
  'docs/reference/cli.md',
  'docs/reference/tooling.md',
  'docs/operations/deployment.md',
  'docs/operations/vpn-proxy-pool.md',
  'docs/operations/live-safety.md',
  'docs/operations/runbooks/README.md',
  'docs/testing/README.md',
  'docs/roadmap/README.md',
  'docs/roadmap/data.md',
  'docs/decisions/README.md',
  'docs/decisions/0001-split-service-worker-roles.md',
  'docs/decisions/0002-publication-generation.md',
  'docs/decisions/0003-vpn-http-proxy-isolation.md',
  'docs/decisions/0004-web-deployment-modes.md',
  'docs/database/ImprovementNotificationRecoveryRunbook.md',
  'docs/database/ScoreHistoryDedupMaintenanceRunbook.md',
  'docs/database/SnapshotReuseRunbook.md',
  'docs/database/SoloFamilyRankingBackfillRunbook.md',
];

const removedLegacyPaths = [
  'docs/database/FSTServiceDatabaseDesign.md',
  'docs/database/PostgresPersistencePriorityPlan.md',
  'docs/database/BandHistoryCompactionRunbook.md',
  'docs/database/LogicalLeaderboardShadowRetirementRunbook.md',
  'docs/database/OrphanReclaimRunbook.md',
  'docs/database/RetiredPhysicalSchemaCleanupRunbook.md',
  'docs/database/StorageOwnershipReadinessRunbook.md',
  'docs/database/StoredRankFilteredReadsRolloutRunbook.md',
];

const removedLegacyPrefixes = [
  'docs/archive/',
  'docs/audits/',
  'docs/design/',
  'docs/refactor/',
];

const allowedStatuses = new Set([
  'canonical',
  'living-runbook',
  'roadmap',
  'decision',
]);

const metadataKeys = [
  'status',
  'owner',
  'last_verified',
  'last_verified_commit',
  'sources',
  'update_triggers',
];

function read(relativePath) {
  return fs.readFileSync(path.join(repoRoot, relativePath), 'utf8');
}

function walk(directory) {
  if (!fs.existsSync(directory)) return [];
  const entries = fs.readdirSync(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    if (entry.name === '.git' || entry.name === 'node_modules'
      || entry.name === 'bin' || entry.name === 'obj'
      || entry.name === 'coverage' || entry.name === 'dist') {
      continue;
    }
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walk(fullPath));
    else if (entry.isFile() && entry.name.endsWith('.md')) files.push(fullPath);
  }
  return files;
}

function frontMatter(relativePath, content) {
  if (!content.startsWith('---\n')) {
    errors.push(`${relativePath}: missing YAML front matter`);
    return '';
  }
  const end = content.indexOf('\n---\n', 4);
  if (end < 0) {
    errors.push(`${relativePath}: unterminated YAML front matter`);
    return '';
  }
  return content.slice(4, end);
}

for (const relativePath of requiredDocs) {
  const absolutePath = path.join(repoRoot, relativePath);
  if (!fs.existsSync(absolutePath)) {
    errors.push(`${relativePath}: required documentation path is missing`);
  }
}

const managedDocs = walk(path.join(repoRoot, 'docs'))
  .map(file => path.relative(repoRoot, file))
  .sort();
const documentStatus = new Map();
for (const relativePath of managedDocs) {
  if (removedLegacyPrefixes.some(prefix => relativePath.startsWith(prefix))) {
    errors.push(`${relativePath}: obsolete documentation namespace must remain removed`);
  }
  const content = read(relativePath);
  const metadata = frontMatter(relativePath, content);
  for (const key of metadataKeys) {
    if (!new RegExp(`^${key}:`, 'm').test(metadata)) {
      errors.push(`${relativePath}: missing metadata key '${key}'`);
    }
  }
  const status = metadata.match(/^status:\s*(\S+)/m)?.[1];
  if (status) {
    documentStatus.set(relativePath, status);
    if (!allowedStatuses.has(status)) {
      errors.push(`${relativePath}: unsupported documentation status '${status}'`);
    }
  }
}

for (const relativePath of removedLegacyPaths) {
  if (fs.existsSync(path.join(repoRoot, relativePath))) {
    errors.push(`${relativePath}: obsolete documentation path must remain removed`);
  }
}

const docsIndex = read('docs/README.md');
for (const [relativePath, status] of documentStatus) {
  if (status !== 'canonical' || relativePath === 'docs/README.md') continue;
  const target = path.relative('docs', relativePath).split(path.sep).join('/');
  if (!docsIndex.includes(`](${target})`)) {
    errors.push(`docs/README.md: missing canonical index link to ${target}`);
  }
}

const rootReadmeLines = read('README.md').trimEnd().split('\n').length;
if (rootReadmeLines > 150) {
  errors.push(`README.md: ${rootReadmeLines} lines exceeds the 150-line landing-page limit`);
}

const markdownFiles = [
  path.join(repoRoot, 'README.md'),
  path.join(repoRoot, 'CONTRIBUTING.md'),
  path.join(repoRoot, 'AGENTS.md'),
  ...walk(path.join(repoRoot, '.github')),
  ...walk(path.join(repoRoot, 'docs')),
  ...walk(path.join(repoRoot, 'FSTService')).filter(file => file.endsWith('AGENTS.md')),
  ...walk(path.join(repoRoot, 'FortniteFestivalWeb')).filter(file => file.endsWith('AGENTS.md')),
  ...walk(path.join(repoRoot, 'tools')).filter(file => file.endsWith('README.md')),
];

const markdownLink = /!?\[[^\]]*]\(([^)]+)\)/g;
for (const absolutePath of new Set(markdownFiles)) {
  const relativePath = path.relative(repoRoot, absolutePath);
  const content = fs.readFileSync(absolutePath, 'utf8');
  for (const match of content.matchAll(markdownLink)) {
    let target = match[1].trim();
    if (target.startsWith('<') && target.endsWith('>')) {
      target = target.slice(1, -1);
    }
    target = target.split(/\s+["']/)[0];
    if (!target || target.startsWith('#')
      || /^[a-z][a-z0-9+.-]*:/i.test(target)) {
      continue;
    }
    target = target.split('#')[0].split('?')[0];
    if (!target) continue;
    const decoded = decodeURIComponent(target);
    const resolved = decoded.startsWith('/')
      ? path.join(repoRoot, decoded.slice(1))
      : path.resolve(path.dirname(absolutePath), decoded);
    if (!fs.existsSync(resolved)) {
      errors.push(`${relativePath}: broken relative link '${match[1]}'`);
    }
  }
}

const featureOptions = read('FSTService/FeatureOptions.cs');
const featureDoc = read('docs/reference/feature-flags.md');
const featureNames = [...featureOptions.matchAll(/public bool (\w+)\s*\{/g)]
  .map(match => match[1]);
for (const featureName of featureNames) {
  if (!featureDoc.includes(`\`${featureName}\``)) {
    errors.push(`docs/reference/feature-flags.md: missing FeatureOptions.${featureName}`);
  }
}

const cliSourceFiles = [
  'FSTService/Program.cs',
  'FSTService/Persistence/ScoreHistoryDedupMaintenanceCommand.cs',
  'FSTService/Scraping/SoloFamilyRankingBackfillCommand.cs',
  'FSTService/Scraping/LeaderboardRivalsRecomputeCommand.cs',
];
const cliFlags = new Set();
for (const sourceFile of cliSourceFiles) {
  for (const match of read(sourceFile).matchAll(/--[a-z][a-z0-9-]*/g)) {
    cliFlags.add(match[0]);
  }
}
const cliDoc = read('docs/reference/cli.md');
for (const flag of [...cliFlags].sort()) {
  if (!cliDoc.includes(`\`${flag}\``)) {
    errors.push(`docs/reference/cli.md: missing CLI flag ${flag}`);
  }
}

const instructions = read('.github/instructions/documentation.instructions.md');
if (!instructions.includes('Documentation impact: updated <paths>')
  || !instructions.includes('Documentation impact: none - <specific reason>')) {
  errors.push('.github/instructions/documentation.instructions.md: missing completion impact contract');
}

const activeText = markdownFiles
  .map(file => fs.readFileSync(file, 'utf8'))
  .join('\n');
if (/retired physical-schema cleanup is prepared but \*\*not executed\*\*/i.test(activeText)) {
  errors.push('Active documentation still describes the completed retired-schema cleanup as unexecuted');
}

const repositoryPath = /`((?:\.github|FSTService|FSTService\.Tests|FortniteFestival\.Core|FortniteFestivalWeb|packages|tools|deploy|docs)\/[^`\n]+\.(?:cs|csproj|ts|tsx|js|mjs|cjs|json|yml|yaml|md|sh|sql|css|env|conf))`/g;
for (const absolutePath of new Set(markdownFiles)) {
  const relativePath = path.relative(repoRoot, absolutePath);
  const content = fs.readFileSync(absolutePath, 'utf8');
  for (const match of content.matchAll(repositoryPath)) {
    const candidate = match[1];
    if (candidate.includes('*') || candidate.includes('?')
      || candidate.includes('<') || candidate.includes('>')) {
      continue;
    }
    if (!fs.existsSync(path.join(repoRoot, candidate))) {
      errors.push(`${relativePath}: backticked repository path does not exist: ${candidate}`);
    }
  }
}

const apiDirectory = path.join(repoRoot, 'FSTService', 'Api');
const routeFileCounts = fs.readdirSync(apiDirectory)
  .filter(name => name.endsWith('Endpoints.cs'))
  .map(name => {
    const content = fs.readFileSync(path.join(apiDirectory, name), 'utf8');
    return [...content.matchAll(/\.Map(?:Get|Post|Put|Delete|Patch)\(/g)].length;
  });
const routeCount = routeFileCounts.reduce((sum, count) => sum + count, 0);
const routeFileCount = routeFileCounts.filter(count => count > 0).length;
const apiContractDoc = read('docs/reference/api-contract.md').replace(/\s+/g, ' ');
const serviceApiDoc = read('docs/components/service-api.md').replace(/\s+/g, ' ');
const apiCountPattern = new RegExp(
  `(^|[^0-9])${routeCount} HTTP routes across ${routeFileCount} route-bearing endpoint files([^0-9]|$)`,
  'm',
);
const serviceCountPattern = new RegExp(
  `(^|[^0-9])${routeCount} HTTP mappings across ${routeFileCount} route-bearing endpoint files([^0-9]|$)`,
  'm',
);
if (!apiCountPattern.test(apiContractDoc)) {
  errors.push(
    'docs/reference/api-contract.md: expected exact current HTTP route/file count statement',
  );
}
if (!serviceCountPattern.test(serviceApiDoc)) {
  errors.push(
    'docs/components/service-api.md: expected exact current HTTP mapping/file count statement',
  );
}

if (errors.length > 0) {
  console.error(`Documentation check failed with ${errors.length} issue(s):`);
  for (const error of errors) console.error(`- ${error}`);
  process.exit(1);
}

console.log(
  `Documentation check passed: ${managedDocs.length} managed docs, `
  + `${new Set(markdownFiles).size} active Markdown files, `
  + `${removedLegacyPaths.length} removed legacy paths enforced.`,
);
