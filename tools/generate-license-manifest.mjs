#!/usr/bin/env node
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const NPM_PACKAGE_MANIFESTS = [
  { path: 'FortniteFestivalWeb/package.json', workspaceRoot: 'FortniteFestivalWeb' },
  { path: 'FortniteFestivalRN/package.json', workspaceRoot: 'FortniteFestivalRN' },
  { path: 'packages/core/package.json', workspaceRoot: '.' },
  { path: 'packages/theme/package.json', workspaceRoot: '.' },
  { path: 'packages/ui-utils/package.json', workspaceRoot: '.' },
  { path: 'packages/auth/package.json', workspaceRoot: '.' },
  { path: 'packages/native/package.json', workspaceRoot: '.' },
  { path: 'FortniteFestivalRN/packages/local-app/package.json', workspaceRoot: 'FortniteFestivalRN' },
  { path: 'FortniteFestivalRN/packages/server-app/package.json', workspaceRoot: 'FortniteFestivalRN' },
  { path: 'FortniteFestivalRN/packages/app-screens/package.json', workspaceRoot: 'FortniteFestivalRN' },
  { path: 'FortniteFestivalRN/packages/contexts/package.json', workspaceRoot: 'FortniteFestivalRN' },
  { path: 'FortniteFestivalRN/packages/ui/package.json', workspaceRoot: 'FortniteFestivalRN' },
  { path: 'tools/mcp/package.json', workspaceRoot: 'tools/mcp' },
];

const NUGET_SCAN_ROOTS = [
  'FSTService',
  'FSTService.Tests',
  'FortniteFestival.Core',
  'tools',
];

const OUTPUT_PATH = 'FortniteFestivalWeb/src/generated/licenseManifest.ts';
const OVERRIDES_PATH = 'tools/license-overrides.json';

const LICENSE_TEXTS = {
  MIT: `MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.`,
  'Apache-2.0': `Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/

TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

1. Definitions.

"License" shall mean the terms and conditions for use, reproduction, and
distribution as defined by Sections 1 through 9 of this document.

"Licensor" shall mean the copyright owner or entity authorized by the
copyright owner that is granting the License.

"Legal Entity" shall mean the union of the acting entity and all other
entities that control, are controlled by, or are under common control with
that entity. For the purposes of this definition, "control" means (i) the
power, direct or indirect, to cause the direction or management of such
entity, whether by contract or otherwise, or (ii) ownership of fifty percent
(50%) or more of the outstanding shares, or (iii) beneficial ownership of
such entity.

"You" (or "Your") shall mean an individual or Legal Entity exercising
permissions granted by this License.

"Source" form shall mean the preferred form for making modifications,
including but not limited to software source code, documentation source,
and configuration files.

"Object" form shall mean any form resulting from mechanical transformation
or translation of a Source form, including but not limited to compiled
object code, generated documentation, and conversions to other media types.

"Work" shall mean the work of authorship, whether in Source or Object form,
made available under the License, as indicated by a copyright notice that is
included in or attached to the work.

"Derivative Works" shall mean any work, whether in Source or Object form,
that is based on or derived from the Work and for which the editorial
revisions, annotations, elaborations, or other modifications represent, as a
whole, an original work of authorship. For the purposes of this License,
Derivative Works shall not include works that remain separable from, or
merely link (or bind by name) to the interfaces of, the Work and Derivative
Works thereof.

"Contribution" shall mean any work of authorship, including the original
version of the Work and any modifications or additions to that Work or
Derivative Works thereof, that is intentionally submitted to Licensor for
inclusion in the Work by the copyright owner or by an individual or Legal
Entity authorized to submit on behalf of the copyright owner.

"Contributor" shall mean Licensor and any individual or Legal Entity on
behalf of whom a Contribution has been received by Licensor and subsequently
incorporated within the Work.

2. Grant of Copyright License. Subject to the terms and conditions of this
License, each Contributor hereby grants to You a perpetual, worldwide,
non-exclusive, no-charge, royalty-free, irrevocable copyright license to
reproduce, prepare Derivative Works of, publicly display, publicly perform,
sublicense, and distribute the Work and such Derivative Works in Source or
Object form.

3. Grant of Patent License. Subject to the terms and conditions of this
License, each Contributor hereby grants to You a perpetual, worldwide,
non-exclusive, no-charge, royalty-free, irrevocable patent license to make,
have made, use, offer to sell, sell, import, and otherwise transfer the Work.

4. Redistribution. You may reproduce and distribute copies of the Work or
Derivative Works thereof in any medium, with or without modifications, and in
Source or Object form, provided that You meet the following conditions:

(a) You must give any other recipients of the Work or Derivative Works a copy
of this License; and

(b) You must cause any modified files to carry prominent notices stating that
You changed the files; and

(c) You must retain, in the Source form of any Derivative Works that You
distribute, all copyright, patent, trademark, and attribution notices from the
Source form of the Work, excluding those notices that do not pertain to any
part of the Derivative Works; and

(d) If the Work includes a "NOTICE" text file as part of its distribution,
then any Derivative Works that You distribute must include a readable copy of
the attribution notices contained within such NOTICE file, excluding those
notices that do not pertain to any part of the Derivative Works.

5. Submission of Contributions. Unless You explicitly state otherwise, any
Contribution intentionally submitted for inclusion in the Work by You to the
Licensor shall be under the terms and conditions of this License, without any
additional terms or conditions.

6. Trademarks. This License does not grant permission to use the trade names,
trademarks, service marks, or product names of the Licensor, except as
required for reasonable and customary use in describing the origin of the Work.

7. Disclaimer of Warranty. Unless required by applicable law or agreed to in
writing, Licensor provides the Work on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied.

8. Limitation of Liability. In no event and under no legal theory, whether in
tort, contract, or otherwise, unless required by applicable law, shall any
Contributor be liable to You for damages, including any direct, indirect,
special, incidental, or consequential damages arising as a result of this
License or out of the use or inability to use the Work.

9. Accepting Warranty or Additional Liability. While redistributing the Work
or Derivative Works thereof, You may choose to offer support, warranty,
indemnity, or other liability obligations. However, in accepting such
obligations, You may act only on Your own behalf and on Your sole
responsibility.

END OF TERMS AND CONDITIONS`,
  'BSD-3-Clause': `BSD 3-Clause License

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
this list of conditions and the following disclaimer in the documentation
and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors
may be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
STRICT LIABILITY, OR TORT ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.`,
  PostgreSQL: `PostgreSQL License

Permission to use, copy, modify, and distribute this software and its
documentation for any purpose, without fee, and without a written agreement is
hereby granted, provided that the above copyright notice and this paragraph and
the following two paragraphs appear in all copies.

IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE TO ANY PARTY FOR DIRECT,
INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, INCLUDING LOST PROFITS,
ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, EVEN IF THE
COPYRIGHT HOLDER HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

THE COPYRIGHT HOLDER SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS ON AN "AS IS" BASIS,
AND THE COPYRIGHT HOLDER HAS NO OBLIGATIONS TO PROVIDE MAINTENANCE, SUPPORT,
UPDATES, ENHANCEMENTS, OR MODIFICATIONS.`,
};

const SUPPORTED_LICENSE_TYPES = new Set(Object.keys(LICENSE_TEXTS));
const DEPENDENCY_GROUPS = ['dependencies', 'devDependencies', 'peerDependencies', 'optionalDependencies'];

function findRepoRoot(startDir = process.cwd()) {
  let currentDir = path.resolve(startDir);
  while (currentDir !== path.dirname(currentDir)) {
    if (fs.existsSync(path.join(currentDir, 'FortniteFestivalLeaderboardScraper.sln'))) {
      return currentDir;
    }
    currentDir = path.dirname(currentDir);
  }
  throw new Error('Unable to find repository root from current working directory.');
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, 'utf8'));
}

function readJsonIfExists(filePath) {
  if (!fs.existsSync(filePath)) return null;
  return readJson(filePath);
}

function slashPath(filePath) {
  return filePath.split(path.sep).join('/');
}

function isExternalNpmPackage(packageName, versionSpec) {
  return !packageName.startsWith('@festival/')
    && !versionSpec.startsWith('portal:')
    && !versionSpec.startsWith('workspace:')
    && !versionSpec.startsWith('file:');
}

function parseYarnLock(lockPath) {
  const versionByDescriptor = new Map();
  if (!fs.existsSync(lockPath)) return versionByDescriptor;

  const lines = fs.readFileSync(lockPath, 'utf8').split(/\r?\n/);
  let descriptors = [];

  for (const line of lines) {
    if (line.startsWith('"') && line.endsWith(':')) {
      const rawKey = line.slice(1, -2);
      descriptors = rawKey.split(/",\s*"|,\s+/);
      continue;
    }

    const versionMatch = line.match(/^\s+version:\s+(.+)$/);
    if (!versionMatch || descriptors.length === 0) continue;

    const version = versionMatch[1].trim().replace(/^"|"$/g, '');
    for (const descriptor of descriptors) {
      const npmMarkerIndex = descriptor.lastIndexOf('@npm:');
      if (npmMarkerIndex < 0) continue;
      const packageName = descriptor.slice(0, npmMarkerIndex);
      const versionSpec = descriptor.slice(npmMarkerIndex + '@npm:'.length);
      versionByDescriptor.set(`${packageName}\n${versionSpec}`, version);
    }
  }

  return versionByDescriptor;
}

function parsePackageLock(lockPath) {
  const versionByPackage = new Map();
  const lock = readJsonIfExists(lockPath);
  if (!lock?.packages) return versionByPackage;

  for (const [packagePath, packageInfo] of Object.entries(lock.packages)) {
    if (!packagePath.startsWith('node_modules/') || !packageInfo?.version) continue;
    const packageName = packagePath.slice('node_modules/'.length);
    versionByPackage.set(packageName, packageInfo.version);
  }

  return versionByPackage;
}

function getLockMaps(repoRoot) {
  const workspaceRoots = new Set(NPM_PACKAGE_MANIFESTS.map(manifest => manifest.workspaceRoot));
  const lockMaps = new Map();

  for (const workspaceRoot of workspaceRoots) {
    const absoluteWorkspaceRoot = path.join(repoRoot, workspaceRoot);
    lockMaps.set(workspaceRoot, {
      yarn: parseYarnLock(path.join(absoluteWorkspaceRoot, 'yarn.lock')),
      packageLock: parsePackageLock(path.join(absoluteWorkspaceRoot, 'package-lock.json')),
    });
  }

  return lockMaps;
}

function collectNpmReferences(repoRoot) {
  const references = [];

  for (const manifest of NPM_PACKAGE_MANIFESTS) {
    const absolutePath = path.join(repoRoot, manifest.path);
    if (!fs.existsSync(absolutePath)) continue;
    const packageJson = readJson(absolutePath);

    for (const dependencyGroup of DEPENDENCY_GROUPS) {
      const dependencies = packageJson[dependencyGroup] ?? {};
      for (const [packageName, versionSpec] of Object.entries(dependencies)) {
        if (!isExternalNpmPackage(packageName, versionSpec)) continue;
        references.push({
          ecosystem: 'npm',
          name: packageName,
          versionSpec,
          sourcePath: manifest.path,
          workspaceRoot: manifest.workspaceRoot,
          dependencyGroup,
        });
      }
    }
  }

  return references;
}

function collectNugetReferences(repoRoot) {
  const references = [];

  for (const scanRoot of NUGET_SCAN_ROOTS) {
    const absoluteScanRoot = path.join(repoRoot, scanRoot);
    if (!fs.existsSync(absoluteScanRoot)) continue;
    for (const csprojPath of walkFiles(absoluteScanRoot, filePath => filePath.endsWith('.csproj'))) {
      const relativePath = slashPath(path.relative(repoRoot, csprojPath));
      const csprojText = fs.readFileSync(csprojPath, 'utf8');
      const packageRegex = /<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"\s*\/>/g;
      let packageMatch;
      while ((packageMatch = packageRegex.exec(csprojText)) !== null) {
        references.push({
          ecosystem: 'nuget',
          name: packageMatch[1],
          version: packageMatch[2],
          sourcePath: relativePath,
        });
      }
    }
  }

  return references;
}

function walkFiles(directory, predicate) {
  const matches = [];
  for (const directoryEntry of fs.readdirSync(directory, { withFileTypes: true })) {
    const absolutePath = path.join(directory, directoryEntry.name);
    if (directoryEntry.isDirectory()) {
      matches.push(...walkFiles(absolutePath, predicate));
    } else if (predicate(absolutePath)) {
      matches.push(absolutePath);
    }
  }
  return matches;
}

function resolveNpmVersions(references, lockMaps) {
  const knownVersionsByWorkspaceAndName = new Map();
  const resolvedReferences = references.map(reference => {
    const lockMap = lockMaps.get(reference.workspaceRoot);
    const descriptorKey = `${reference.name}\n${reference.versionSpec}`;
    const versionFromYarn = lockMap?.yarn.get(descriptorKey);
    const versionFromPackageLock = lockMap?.packageLock.get(reference.name);
    const versionFromExactSpec = parseExactVersionSpec(reference.versionSpec);
    const version = versionFromYarn ?? versionFromPackageLock ?? versionFromExactSpec;

    if (version && reference.versionSpec !== '*') {
      const workspaceKey = `${reference.workspaceRoot}\n${reference.name}`;
      if (!knownVersionsByWorkspaceAndName.has(workspaceKey)) knownVersionsByWorkspaceAndName.set(workspaceKey, new Set());
      knownVersionsByWorkspaceAndName.get(workspaceKey).add(version);
    }

    return { ...reference, version: version ?? null };
  });

  return resolvedReferences.map(reference => {
    if (reference.version) return reference;
    if (reference.versionSpec !== '*') return reference;

    const workspaceKey = `${reference.workspaceRoot}\n${reference.name}`;
    const versions = [...(knownVersionsByWorkspaceAndName.get(workspaceKey) ?? [])];
    if (versions.length === 1) return { ...reference, version: versions[0] };
    return reference;
  });
}

function parseExactVersionSpec(versionSpec) {
  if (/^\d+\.\d+\.\d+/.test(versionSpec)) return versionSpec;
  return null;
}

function readNpmPackageMetadata(repoRoot, workspaceRoot, packageName) {
  const packagePathParts = packageName.split('/');
  const packageJsonPath = path.join(repoRoot, workspaceRoot, 'node_modules', ...packagePathParts, 'package.json');
  if (!fs.existsSync(packageJsonPath)) return null;
  return readJson(packageJsonPath);
}

function readNugetPackageMetadata(packageName, version) {
  const packageRoot = path.join(os.homedir(), '.nuget/packages', packageName.toLowerCase(), version);
  const nuspecPath = path.join(packageRoot, `${packageName.toLowerCase()}.nuspec`);
  if (!fs.existsSync(nuspecPath)) return null;
  const nuspecText = fs.readFileSync(nuspecPath, 'utf8');
  const licenseExpressionMatch = nuspecText.match(/<license[^>]*type="expression"[^>]*>([^<]+)<\/license>/i);
  const licenseFileMatch = nuspecText.match(/<license[^>]*type="file"[^>]*>([^<]+)<\/license>/i);
  const repositoryUrlMatch = nuspecText.match(/<repository[^>]*url="([^"]+)"/i);
  const projectUrlMatch = nuspecText.match(/<projectUrl>([^<]+)<\/projectUrl>/i);

  let licenseType = licenseExpressionMatch?.[1]?.trim() ?? null;
  let licenseText = null;
  if (!licenseType && licenseFileMatch?.[1]) {
    const licensePath = path.join(packageRoot, licenseFileMatch[1].trim());
    if (fs.existsSync(licensePath)) {
      licenseText = fs.readFileSync(licensePath, 'utf8').trim();
      licenseType = inferLicenseTypeFromText(licenseText);
    }
  }

  return {
    licenseType,
    licenseText,
    repositoryUrl: repositoryUrlMatch?.[1] ?? null,
    packageUrl: projectUrlMatch?.[1] ?? null,
  };
}

function inferLicenseTypeFromText(licenseText) {
  const firstChunk = licenseText.slice(0, 500).toLowerCase();
  if (firstChunk.includes('mit license')) return 'MIT';
  if (firstChunk.includes('apache license')) return 'Apache-2.0';
  if (firstChunk.includes('bsd 3-clause')) return 'BSD-3-Clause';
  if (firstChunk.includes('postgresql')) return 'PostgreSQL';
  return null;
}

function normalizeLicenseType(rawLicenseType) {
  if (!rawLicenseType) return null;
  const trimmedLicenseType = String(rawLicenseType).trim().replace(/^\((.*)\)$/u, '$1');
  if (SUPPORTED_LICENSE_TYPES.has(trimmedLicenseType)) return trimmedLicenseType;
  return null;
}

function getNpmPackageUrl(packageName) {
  return `https://www.npmjs.com/package/${encodeURIComponent(packageName).replace('%2F', '/')}`;
}

function getNugetPackageUrl(packageName, version) {
  return `https://www.nuget.org/packages/${encodeURIComponent(packageName)}/${encodeURIComponent(version)}`;
}

function addEntry(entryMap, entry) {
  const existingEntry = entryMap.get(entry.id);
  if (existingEntry) {
    for (const consumer of entry.consumers) existingEntry.consumers.add(consumer);
    return;
  }
  entryMap.set(entry.id, { ...entry, consumers: new Set(entry.consumers) });
}

export function buildLicenseManifest(options = {}) {
  const repoRoot = options.repoRoot ?? findRepoRoot();
  const overrides = readJson(path.join(repoRoot, OVERRIDES_PATH));
  const lockMaps = getLockMaps(repoRoot);
  const npmReferences = resolveNpmVersions(collectNpmReferences(repoRoot), lockMaps);
  const nugetReferences = collectNugetReferences(repoRoot);
  const entryMap = new Map();
  const errors = [];

  for (const reference of npmReferences) {
    if (!reference.version) {
      errors.push(`Unable to resolve npm package version for ${reference.name} from ${reference.sourcePath}.`);
      continue;
    }

    const packageMetadata = readNpmPackageMetadata(repoRoot, reference.workspaceRoot, reference.name);
    const licenseType = normalizeLicenseType(overrides.npm?.[reference.name])
      ?? normalizeLicenseType(packageMetadata?.license);
    const licenseText = licenseType ? LICENSE_TEXTS[licenseType] : null;
    if (!licenseType || !licenseText) {
      errors.push(`Missing supported license metadata for npm package ${reference.name}@${reference.version}. Add it to ${OVERRIDES_PATH}.`);
      continue;
    }

    addEntry(entryMap, {
      id: `npm:${reference.name}@${reference.version}`,
      ecosystem: 'npm',
      name: reference.name,
      version: reference.version,
      licenseType,
      licenseText,
      packageUrl: getNpmPackageUrl(reference.name),
      repositoryUrl: normalizeRepositoryUrl(packageMetadata?.repository),
      consumers: [`${reference.sourcePath} (${reference.dependencyGroup})`],
    });
  }

  for (const reference of nugetReferences) {
    const packageMetadata = readNugetPackageMetadata(reference.name, reference.version);
    const licenseType = normalizeLicenseType(overrides.nuget?.[reference.name])
      ?? normalizeLicenseType(packageMetadata?.licenseType);
    const licenseText = packageMetadata?.licenseText ?? (licenseType ? LICENSE_TEXTS[licenseType] : null);
    if (!licenseType || !licenseText) {
      errors.push(`Missing supported license metadata for NuGet package ${reference.name}@${reference.version}. Add it to ${OVERRIDES_PATH}.`);
      continue;
    }

    addEntry(entryMap, {
      id: `nuget:${reference.name}@${reference.version}`,
      ecosystem: 'nuget',
      name: reference.name,
      version: reference.version,
      licenseType,
      licenseText,
      packageUrl: packageMetadata?.packageUrl ?? getNugetPackageUrl(reference.name, reference.version),
      repositoryUrl: packageMetadata?.repositoryUrl ?? null,
      consumers: [reference.sourcePath],
    });
  }

  for (const manualEntry of overrides.other ?? []) {
    const licenseType = normalizeLicenseType(manualEntry.licenseType);
    const licenseText = manualEntry.licenseText ?? (licenseType ? LICENSE_TEXTS[licenseType] : null);
    if (!manualEntry.name || !manualEntry.version || !licenseType || !licenseText) {
      errors.push(`Invalid manual license override entry in ${OVERRIDES_PATH}.`);
      continue;
    }
    addEntry(entryMap, {
      id: `other:${manualEntry.name}@${manualEntry.version}`,
      ecosystem: 'other',
      name: manualEntry.name,
      version: manualEntry.version,
      licenseType,
      licenseText,
      packageUrl: manualEntry.packageUrl ?? null,
      repositoryUrl: manualEntry.repositoryUrl ?? null,
      consumers: [manualEntry.sourcePath ?? 'manual override'],
    });
  }

  if (errors.length > 0) {
    throw new Error(errors.join('\n'));
  }

  return [...entryMap.values()]
    .map(entry => ({ ...entry, consumers: [...entry.consumers].sort() }))
    .sort(compareLicenseEntries);
}

function normalizeRepositoryUrl(repository) {
  if (!repository) return null;
  if (typeof repository === 'string') return repository;
  return repository.url ?? null;
}

function compareLicenseEntries(leftEntry, rightEntry) {
  return leftEntry.name.localeCompare(rightEntry.name, undefined, { sensitivity: 'base' })
    || leftEntry.version.localeCompare(rightEntry.version, undefined, { numeric: true })
    || leftEntry.ecosystem.localeCompare(rightEntry.ecosystem);
}

export function renderLicenseManifestModule(entries) {
  const entryLines = entries.map(entry => renderEntry(entry)).join(',\n');
  return `/* eslint-disable */\n// This file is generated by tools/generate-license-manifest.mjs. Do not edit by hand.\n\nexport type LicenseEcosystem = 'npm' | 'nuget' | 'other';\n\nexport type LicenseManifestEntry = {\n  readonly id: string;\n  readonly ecosystem: LicenseEcosystem;\n  readonly name: string;\n  readonly version: string;\n  readonly licenseType: string;\n  readonly licenseText: string;\n  readonly packageUrl: string | null;\n  readonly repositoryUrl: string | null;\n  readonly consumers: readonly string[];\n};\n\nconst licenseTexts = ${JSON.stringify(LICENSE_TEXTS, null, 2)} as const;\n\nexport const licenseManifest = [\n${entryLines}\n] as const satisfies readonly LicenseManifestEntry[];\n`;
}

function renderEntry(entry) {
  const licenseTextReference = SUPPORTED_LICENSE_TYPES.has(entry.licenseType)
    ? `licenseTexts[${JSON.stringify(entry.licenseType)}]`
    : JSON.stringify(entry.licenseText);

  return `  {\n    id: ${JSON.stringify(entry.id)},\n    ecosystem: ${JSON.stringify(entry.ecosystem)},\n    name: ${JSON.stringify(entry.name)},\n    version: ${JSON.stringify(entry.version)},\n    licenseType: ${JSON.stringify(entry.licenseType)},\n    licenseText: ${licenseTextReference},\n    packageUrl: ${JSON.stringify(entry.packageUrl)},\n    repositoryUrl: ${JSON.stringify(entry.repositoryUrl)},\n    consumers: ${JSON.stringify(entry.consumers)},\n  }`;
}

function runCli() {
  const repoRoot = findRepoRoot();
  const checkOnly = process.argv.includes('--check');
  const outputPath = path.join(repoRoot, OUTPUT_PATH);
  const entries = buildLicenseManifest({ repoRoot });
  const nextContent = renderLicenseManifestModule(entries);

  if (checkOnly) {
    const currentContent = fs.existsSync(outputPath) ? fs.readFileSync(outputPath, 'utf8') : '';
    if (currentContent !== nextContent) {
      console.error(`${OUTPUT_PATH} is stale. Run npm run licenses:generate from FortniteFestivalWeb.`);
      process.exitCode = 1;
    }
    return;
  }

  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, nextContent, 'utf8');
  console.log(`Wrote ${OUTPUT_PATH} with ${entries.length} license entries.`);
}

const invokedPath = process.argv[1] ? path.resolve(process.argv[1]) : '';
if (invokedPath === fileURLToPath(import.meta.url)) {
  runCli();
}
