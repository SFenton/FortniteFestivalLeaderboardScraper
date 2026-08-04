import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const targetSha = '${{ needs.version-bump.outputs.commit_sha || github.sha }}';
const targetTag = `type=raw,value=sha-${targetSha}`;
const targetRevision = `org.opencontainers.image.revision=${targetSha}`;
const downstreamIf = '${{ always() && !failure() && !cancelled() }}';
const imageIf = "always() && !failure() && !cancelled() && (github.event_name == 'push' || github.event_name == 'workflow_dispatch')";
const webBuildIf = "steps.changes.outputs.bump_enabled == 'true' && (steps.changes.outputs.web == 'true' || steps.changes.outputs.mobile == 'true' || steps.changes.outputs.core_ts == 'true' || steps.changes.outputs.theme_ts == 'true')";

export function validatePublishImageWorkflow(workflow, webDockerfile) {
  const errors = [];
  const parsed = parseWorkflowSubset(workflow);
  const versionJob = requireJob(parsed, 'version-bump', errors);
  const testJob = requireJob(parsed, 'test', errors);
  const webTestJob = requireJob(parsed, 'test-web', errors);
  const serviceImageJob = requireJob(parsed, 'build-and-push-service', errors);
  const webImageJob = requireJob(parsed, 'build-and-push-web', errors);

  requireRaw(workflow, errors, 'version-bump SHA output', 'commit_sha: ${{ steps.commit.outputs.commit_sha }}');
  requireRaw(workflow, errors, 'exact committed SHA capture', 'echo "commit_sha=$(git rev-parse HEAD)" >> "$GITHUB_OUTPUT"');

  if (versionJob) {
    const checkout = requireStep(versionJob, 'Checkout', errors);
    requireNested(checkout, 'with', 'fetch-depth', '0', 'version-bump checkout fetch-depth', errors);

    const detection = requireStep(versionJob, 'Detect changed components', errors);
    const run = detection?.run ?? '';
    for (const [label, command] of [
      ['full push range', 'git diff --no-renames --name-only "$BEFORE_SHA" "$EVENT_SHA"'],
      ['pull-request range', 'git diff --no-renames --name-only "$PR_BASE_SHA" "$PR_HEAD_SHA"'],
      ['dispatch fallback', 'git diff --no-renames --name-only "$EVENT_SHA^" "$EVENT_SHA"'],
      ['zero-before fallback', 'git diff-tree --no-renames --root --no-commit-id --name-only -r "$EVENT_SHA"'],
    ]) {
      if (!run.includes(command)) errors.push(`Detect changed components is missing ${label}`);
    }
    if (!run.includes("grep -qE '^packages/ui-utils/'")
        || !run.includes('echo "ui_utils=true" >> "$GITHUB_OUTPUT"')
        || !uiUtilsBlock(run).includes('echo "web=true" >> "$GITHUB_OUTPUT"')) {
      errors.push('ui-utils changes are not classified as ui-utils and web-affecting');
    }

    const requiredConditions = new Map([
      ['Bump FSTService version', "steps.changes.outputs.bump_enabled == 'true' && steps.changes.outputs.service == 'true'"],
      ['Bump FortniteFestivalWeb version', "steps.changes.outputs.bump_enabled == 'true' && steps.changes.outputs.web == 'true'"],
      ['Bump FortniteFestivalRN version', "steps.changes.outputs.bump_enabled == 'true' && steps.changes.outputs.mobile == 'true'"],
      ['Bump @festival/core version', "steps.changes.outputs.bump_enabled == 'true' && steps.changes.outputs.core_ts == 'true'"],
      ['Bump @festival/theme version', "steps.changes.outputs.bump_enabled == 'true' && steps.changes.outputs.theme_ts == 'true'"],
      ['Bump @festival/ui-utils version', "steps.changes.outputs.bump_enabled == 'true' && steps.changes.outputs.ui_utils == 'true'"],
      ['Regenerate embedded web bundle', webBuildIf],
      ['Verify version-bumped embedded bundle', webBuildIf],
    ]);
    for (const [stepName, expectedIf] of requiredConditions) {
      const step = requireStep(versionJob, stepName, errors);
      if (step && normalizeExpression(step.if) !== normalizeExpression(expectedIf)) {
        errors.push(`${stepName} has incorrect condition`);
      }
    }

    requireStepOrder(versionJob, errors,
      'Regenerate embedded web bundle',
      'Create version bump commit',
      'Verify version-bumped embedded bundle',
      'Push version bump commit');
  }

  validateDownstreamTestJob(testJob, 'test', '[version-bump]', errors);
  validateDownstreamTestJob(webTestJob, 'test-web', '[version-bump]', errors);
  if (webTestJob) {
    for (const [name, run] of [
      ['Build', 'yarn build'],
      ['Verify committed embedded bundle', 'yarn embedded:check'],
      ['Run unit tests', 'yarn test:unit'],
      ['Enforce unit coverage', 'yarn test:coverage'],
      ['Run shared package tests', 'yarn test:shared'],
      ['Enforce shared package coverage', 'yarn test:shared:coverage'],
    ]) {
      const step = requireStep(webTestJob, name, errors);
      if (step?.run.trim() !== run) errors.push(`${name} must run exactly ${run}`);
    }
    const workflowTest = requireStep(webTestJob, 'Validate publish workflow contract', errors);
    if (!workflowTest?.run.includes('node --test tools/validate-publish-image-workflow.test.mjs')) {
      errors.push('workflow mutation tests are not gated in test-web');
    }
  }

  validateImageJob(serviceImageJob, {
    name: 'build-and-push-service',
    needs: '[version-bump, test, test-web]',
    metadataStep: 'Extract FSTService metadata',
    metadataId: 'meta-fst',
    buildStep: 'Build and push FSTService',
  }, errors);
  validateImageJob(webImageJob, {
    name: 'build-and-push-web',
    needs: '[version-bump, test-web]',
    metadataStep: 'Extract FestivalWeb metadata',
    metadataId: 'meta-web',
    buildStep: 'Build and push FestivalWeb',
  }, errors);

  if (!webDockerfile.includes('FST_WEB_OUT_DIR=/webapp-dist yarn build')) {
    errors.push('web image does not use the canonical yarn build command');
  }

  return errors;
}

export function classifyChangedPaths(paths) {
  const result = {
    service: false,
    web: false,
    mobile: false,
    coreTs: false,
    themeTs: false,
    uiUtils: false,
  };
  for (const path of paths) {
    if (/^(FSTService\/|FortniteFestival\.Core\/)/.test(path)) result.service = true;
    if (path.startsWith('FortniteFestivalWeb/')) result.web = true;
    if (path.startsWith('FortniteFestivalRN/')) result.mobile = true;
    if (path.startsWith('packages/core/')) result.coreTs = true;
    if (path.startsWith('packages/theme/')) result.themeTs = true;
    if (path.startsWith('packages/ui-utils/')) {
      result.uiUtils = true;
      result.web = true;
    }
  }
  return result;
}

function validateDownstreamTestJob(job, name, needs, errors) {
  if (!job) return;
  requireField(job, 'needs', needs, `${name} needs`, errors);
  requireField(job, 'if', downstreamIf, `${name} condition`, errors);
  validateCheckout(job, name, errors);
}

function validateImageJob(job, expected, errors) {
  if (!job) return;
  requireField(job, 'needs', expected.needs, `${expected.name} needs`, errors);
  if (normalizeExpression(job.fields.if) !== imageIf) {
    errors.push(`${expected.name} has incorrect publish condition`);
  }
  validateCheckout(job, expected.name, errors);

  const metadata = requireStep(job, expected.metadataStep, errors);
  if (metadata?.uses !== 'docker/metadata-action@v5') {
    errors.push(`${expected.name} metadata step must use docker/metadata-action@v5`);
  }
  if (metadata?.id !== expected.metadataId) {
    errors.push(`${expected.name} metadata step has incorrect id`);
  }
  if (!exactBlockLine(metadata?.with.tags, targetTag)) {
    errors.push(`${expected.name} metadata tags are missing target-SHA tag`);
  }
  if (!exactBlockLine(metadata?.with.labels, targetRevision)) {
    errors.push(`${expected.name} metadata labels are missing target-SHA revision`);
  }

  const build = requireStep(job, expected.buildStep, errors);
  if (build?.uses !== 'docker/build-push-action@v6') {
    errors.push(`${expected.name} build step must use docker/build-push-action@v6`);
  }
  const expectedTags = `\${{ steps.${expected.metadataId}.outputs.tags }}`;
  if (build?.with.tags !== expectedTags) {
    errors.push(`${expected.name} build tags must come from ${expected.metadataId}`);
  }
  const expectedLabels = `\${{ steps.${expected.metadataId}.outputs.labels }}`;
  if (build?.with.labels !== expectedLabels) {
    errors.push(`${expected.name} build labels must come from ${expected.metadataId}`);
  }
}

function validateCheckout(job, name, errors) {
  const checkout = job.steps.filter(step => step.uses === 'actions/checkout@v4');
  if (checkout.length !== 1) {
    errors.push(`${name} must contain exactly one actions/checkout@v4 step`);
    return;
  }
  if (checkout[0].with.ref !== targetSha) {
    errors.push(`${name} checkout ref must be under actions/checkout@v4 with`);
  }
}

function parseWorkflowSubset(source) {
  const jobs = new Map();
  const lines = source.split(/\r?\n/);
  let inJobs = false;
  let job = null;
  let step = null;
  let section = null;
  let block = null;

  const finishBlock = () => {
    if (!block) return;
    block.target[block.key] = block.lines.join('\n').trim();
    block = null;
  };

  for (let index = 0; index < lines.length; index += 1) {
    const raw = lines[index];
    const trimmed = raw.trim();
    const indent = raw.match(/^ */)[0].length;

    if (block) {
      if (trimmed === '' || indent > block.indent) {
        block.lines.push(trimmed === '' ? '' : raw.slice(block.indent + 2).trimEnd());
        continue;
      }
      finishBlock();
      index -= 1;
      continue;
    }

    if (trimmed === '' || trimmed.startsWith('#')) continue;
    if (indent === 0 && trimmed === 'jobs:') {
      inJobs = true;
      continue;
    }
    if (!inJobs) continue;

    const jobMatch = indent === 2 ? trimmed.match(/^([a-zA-Z0-9_-]+):$/) : null;
    if (jobMatch) {
      job = { name: jobMatch[1], fields: {}, steps: [] };
      jobs.set(job.name, job);
      step = null;
      section = null;
      continue;
    }
    if (!job) continue;

    if (indent === 4) {
      step = null;
      section = null;
      const [key, value] = splitKeyValue(trimmed);
      if (key === 'steps') continue;
      assignValue(job.fields, key, value, indent, lines, index, holder => { block = holder; });
      continue;
    }

    if (indent === 6 && trimmed.startsWith('- ')) {
      step = { name: '', uses: '', id: '', if: '', run: '', with: {}, env: {} };
      job.steps.push(step);
      section = null;
      const [key, value] = splitKeyValue(trimmed.slice(2));
      assignStepValue(step, key, value, indent + 2, holder => { block = holder; });
      continue;
    }

    if (indent === 8 && step) {
      const [key, value] = splitKeyValue(trimmed);
      if ((key === 'with' || key === 'env') && value === '') {
        section = key;
      } else {
        section = null;
        assignStepValue(step, key, value, indent, holder => { block = holder; });
      }
      continue;
    }

    if (indent === 10 && step && section) {
      const [key, value] = splitKeyValue(trimmed);
      assignValue(step[section], key, value, indent, lines, index, holder => { block = holder; });
    }
  }
  finishBlock();
  return { jobs };
}

function assignStepValue(step, key, value, indent, setBlock) {
  const targetKey = key === 'name' || key === 'uses' || key === 'id' || key === 'if' || key === 'run'
    ? key
    : null;
  if (!targetKey) return;
  assignValue(step, targetKey, value, indent, [], 0, setBlock);
}

function assignValue(target, key, value, indent, _lines, _index, setBlock) {
  if (/^[>|][+-]?$/.test(value)) {
    setBlock({ target, key, indent, lines: [] });
  } else {
    target[key] = unquote(value);
  }
}

function splitKeyValue(line) {
  const separator = line.indexOf(':');
  if (separator < 0) return [line, ''];
  return [line.slice(0, separator).trim(), line.slice(separator + 1).trim()];
}

function unquote(value) {
  if ((value.startsWith('"') && value.endsWith('"'))
      || (value.startsWith("'") && value.endsWith("'"))) {
    return value.slice(1, -1);
  }
  return value;
}

function uiUtilsBlock(run) {
  return run.match(
    /if echo "\$CHANGED" \| grep -qE '\^packages\/ui-utils\/'; then[\s\S]*?\n\s*fi/,
  )?.[0] ?? '';
}

function requireJob(parsed, name, errors) {
  const job = parsed.jobs.get(name);
  if (!job) errors.push(`missing job ${name}`);
  return job;
}

function requireStep(job, name, errors) {
  if (!job) return null;
  const matches = job.steps.filter(step => step.name === name);
  if (matches.length !== 1) {
    errors.push(`${job.name} must contain exactly one "${name}" step`);
    return null;
  }
  return matches[0];
}

function requireStepOrder(job, errors, ...names) {
  const indexes = names.map(name => job.steps.findIndex(step => step.name === name));
  if (indexes.some(index => index < 0)) return;
  for (let index = 1; index < indexes.length; index += 1) {
    if (indexes[index] <= indexes[index - 1]) {
      errors.push(`${job.name} steps are out of order: ${names.join(' -> ')}`);
      return;
    }
  }
}

function requireField(job, key, expected, label, errors) {
  if (job.fields[key] !== expected) errors.push(`${label} must equal ${expected}`);
}

function requireNested(step, section, key, expected, label, errors) {
  if (step?.[section]?.[key] !== expected) errors.push(`${label} must equal ${expected}`);
}

function requireRaw(source, errors, label, text) {
  if (!source.includes(text)) errors.push(`missing ${label}`);
}

function normalizeExpression(value = '') {
  return value.replace(/\s+/g, ' ').trim();
}

function exactBlockLine(value = '', expected) {
  return value.split('\n').some(line => line.trim() === expected);
}

function runCli() {
  const workflow = readFileSync(resolve('.github/workflows/publish-image.yml'), 'utf8');
  const dockerfile = readFileSync(resolve('FortniteFestivalWeb/Dockerfile'), 'utf8');
  const errors = validatePublishImageWorkflow(workflow, dockerfile);
  if (errors.length > 0) {
    for (const error of errors) console.error(`[workflow] ${error}`);
    process.exit(1);
  }
  console.log('[workflow] structured range, SHA, bump, shared-test, and image-job contract is valid.');
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  runCli();
}
