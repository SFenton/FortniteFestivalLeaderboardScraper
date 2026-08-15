import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { resolve } from 'node:path';
import {
  classifyChangedPaths,
  validatePublishImageWorkflow,
} from './validate-publish-image-workflow.mjs';

const workflow = readFileSync(resolve('.github/workflows/publish-image.yml'), 'utf8');
const dockerfile = readFileSync(resolve('FortniteFestivalWeb/Dockerfile'), 'utf8');

test('current publish workflow satisfies the distribution contract', () => {
  assert.deepEqual(validatePublishImageWorkflow(workflow, dockerfile), []);
});

test('web image installs from the immutable Yarn lockfile', () => {
  const mutated = dockerfile.replace('yarn install --immutable', 'yarn install');
  assert.ok(
    validatePublishImageWorkflow(workflow, mutated)
      .some(error => error.includes('committed Yarn lockfile')),
  );
});

test('pull-request and master validation cannot regain path filters', () => {
  for (const event of ['push', 'pull_request']) {
    const mutated = workflow.replace(
      `  ${event}:\n    branches: [master]`,
      `  ${event}:\n    branches: [master]\n    paths:\n      - "FortniteFestivalWeb/**"`,
    );
    assert.ok(
      validatePublishImageWorkflow(mutated, dockerfile)
        .some(error => error.includes('must not use path filters')),
      event,
    );
  }
});

for (const job of ['build-and-push-service', 'build-and-push-web']) {
  const metadataId = job === 'build-and-push-service' ? 'meta-fst' : 'meta-web';

  test(`${job} independently requires target-SHA checkout, tag, and revision`, () => {
    for (const marker of [
      'ref: ${{ needs.version-bump.outputs.commit_sha || github.sha }}',
      'type=raw,value=sha-${{ needs.version-bump.outputs.commit_sha || github.sha }}',
      'org.opencontainers.image.revision=${{ needs.version-bump.outputs.commit_sha || github.sha }}',
    ]) {
      const mutated = mutateJob(workflow, job, block => block.replace(marker, `${marker}-broken`));
      assert.ok(
        validatePublishImageWorkflow(mutated, dockerfile).some(error => error.startsWith(job)),
        `${job} mutation was not rejected: ${marker}`,
      );
    }
  });

  test(`${job} rejects checkout ref moved from with to env`, () => {
    const ref = 'ref: ${{ needs.version-bump.outputs.commit_sha || github.sha }}';
    const mutated = mutateJob(workflow, job, block => block.replace(
      `        with:\n          ${ref}`,
      `        env:\n          ${ref}`,
    ));
    assert.ok(
      validatePublishImageWorkflow(mutated, dockerfile)
        .some(error => error.includes(`${job} checkout ref must be under`)),
    );
  });

  test(`${job} build must consume metadata tags`, () => {
    const expected = `tags: \${{ steps.${metadataId}.outputs.tags }}`;
    const mutated = mutateJob(
      workflow,
      job,
      block => block.replace(expected, 'tags: latest'),
    );
    assert.ok(
      validatePublishImageWorkflow(mutated, dockerfile)
        .some(error => error.includes(`${job} build tags must come from ${metadataId}`)),
    );
  });

  test(`${job} cannot publish or authenticate during pull requests`, () => {
    const alwaysPush = mutateJob(
      workflow,
      job,
      block => block.replace(
        "push: ${{ github.event_name != 'pull_request' }}",
        'push: true',
      ),
    );
    assert.ok(
      validatePublishImageWorkflow(alwaysPush, dockerfile)
        .some(error => error.includes('only publish images outside pull requests')),
    );

    const alwaysLogin = mutateJob(
      workflow,
      job,
      block => block.replace(
        "if: github.event_name != 'pull_request'",
        'if: always()',
      ),
    );
    assert.ok(
      validatePublishImageWorkflow(alwaysLogin, dockerfile)
        .some(error => error.includes('registry login must be skipped')),
    );
  });
}

test('web validation cannot drop Playwright runners', () => {
  const mutated = mutateStep(
    workflow,
    'test-web',
    'Run browser tests',
    block => block.replace('run: yarn e2e:ci', 'run: echo skipped'),
  );
  assert.ok(
    validatePublishImageWorkflow(mutated, dockerfile)
      .some(error => error.includes('Run browser tests must run exactly yarn e2e:ci')),
  );
});

test('web validation cannot drop coverage-ignore integrity', () => {
  const mutated = mutateStep(
    workflow,
    'test-web',
    'Check coverage ignore directives',
    block => block.replace('run: yarn check:coverage-ignores', 'run: echo skipped'),
  );
  assert.ok(
    validatePublishImageWorkflow(mutated, dockerfile)
      .some(error => error.includes('Check coverage ignore directives must run exactly yarn check:coverage-ignores')),
  );
});

test('multi-commit push range cannot regress to HEAD~1', () => {
  const mutated = workflow.replace(
    'git diff --no-renames --name-only "$BEFORE_SHA" "$EVENT_SHA"',
    'git diff --name-only HEAD~1 HEAD',
  );
  assert.ok(validatePublishImageWorkflow(mutated, dockerfile).some(error => error.includes('full push range')));
});

test('rename detection cannot drop --no-renames', () => {
  const mutated = workflow.replace(
    'git diff --no-renames --name-only "$PR_BASE_SHA" "$PR_HEAD_SHA"',
    'git diff --name-only "$PR_BASE_SHA" "$PR_HEAD_SHA"',
  );
  assert.ok(validatePublishImageWorkflow(mutated, dockerfile).some(error => error.includes('pull-request range')));
});

test('ui-utils cannot be removed from web-affecting classification', () => {
  const mutated = workflow.replace("^packages/ui-utils/", '^packages/not-ui-utils/');
  assert.ok(validatePublishImageWorkflow(mutated, dockerfile).some(error => error.includes('ui-utils')));

  const nonWebMutation = workflow.replace(
    `if echo "$CHANGED" | grep -qE '^packages/ui-utils/'; then
            echo "ui_utils=true" >> "$GITHUB_OUTPUT"
            echo "web=true" >> "$GITHUB_OUTPUT"
          fi`,
    `if echo "$CHANGED" | grep -qE '^packages/ui-utils/'; then
            echo "ui_utils=true" >> "$GITHUB_OUTPUT"
            echo "web=false" >> "$GITHUB_OUTPUT"
          fi`,
  );
  assert.ok(validatePublishImageWorkflow(nonWebMutation, dockerfile).some(error => error.includes('web-affecting')));
});

test('bundled CHOpt changes rebuild the service image', () => {
  const result = classifyChangedPaths([
    'tools/chopt-cli-linux/CHOpt',
    'tools/chopt-cli-linux/libs/libQt6Core.so.6',
    'tools/chopt-cli-linux/README.md',
  ]);
  assert.equal(result.service, true);
  assert.equal(result.web, false);

  const mutated = workflow.replace(
    '^(FSTService/|FortniteFestival\\.Core/|tools/chopt-cli-linux/)',
    '^(FSTService/|FortniteFestival\\.Core/)',
  );
  assert.ok(
    validatePublishImageWorkflow(mutated, dockerfile)
      .some(error => error.includes('bundled CHOpt changes')),
  );
});

test('moving a ui-utils file outside its root still classifies the source package', () => {
  const result = classifyChangedPaths([
    'packages/ui-utils/src/stagger.ts',
    'archive/stagger.ts',
  ]);
  assert.equal(result.uiUtils, true);
  assert.equal(result.web, true);
});

test('component classification contains only retained projects', () => {
  assert.deepEqual(classifyChangedPaths([]), {
    service: false,
    web: false,
    coreTs: false,
    themeTs: false,
    uiUtils: false,
  });
});

test('publish workflow contract changes affect both images', () => {
  for (const path of [
    '.github/workflows/publish-image.yml',
    'tools/validate-publish-image-workflow.mjs',
    'tools/validate-publish-image-workflow.test.mjs',
  ]) {
    const result = classifyChangedPaths([path]);
    assert.equal(result.service, true, path);
    assert.equal(result.web, true, path);
  }

  const mutated = workflow.replace(
    "grep -qE '^(\\.github/workflows/publish-image\\.yml|tools/validate-publish-image-workflow(\\.test)?\\.mjs)$'",
    "grep -qE '^tools/not-the-publish-contract\\.mjs$'",
  );
  assert.ok(
    validatePublishImageWorkflow(mutated, dockerfile)
      .some(error => error.includes('publish workflow contract changes')),
  );
});

test('publish workflow cannot push directly to master', () => {
  const mutated = workflow.replace(
    'echo "commit_sha=$GITHUB_SHA" >> "$GITHUB_OUTPUT"',
    'git push',
  );
  assert.ok(
    validatePublishImageWorkflow(mutated, dockerfile).some(error => error.includes('must not push')),
  );
});

test('target commit cannot be replaced by a generated commit', () => {
  const mutated = mutateStep(
    workflow,
    'version-bump',
    'Select target commit',
    block => block.replace(
      'echo "commit_sha=$GITHUB_SHA" >> "$GITHUB_OUTPUT"',
      'echo "commit_sha=$(git rev-parse HEAD^)" >> "$GITHUB_OUTPUT"',
    ),
  );
  assert.ok(
    validatePublishImageWorkflow(mutated, dockerfile)
      .some(error => error.includes('exact target SHA capture')),
  );
});

function mutateJob(source, jobName, mutate) {
  const marker = `  ${jobName}:\n`;
  const start = source.indexOf(marker);
  assert.notEqual(start, -1, `missing job ${jobName}`);
  const remainder = source.slice(start + marker.length);
  const next = remainder.match(/\n  [a-zA-Z0-9_-]+:\n/);
  const end = next ? start + marker.length + next.index + 1 : source.length;
  return `${source.slice(0, start)}${mutate(source.slice(start, end))}${source.slice(end)}`;
}

function mutateStep(source, jobName, stepName, mutate) {
  return mutateJob(source, jobName, jobBlock => {
    const marker = `      - name: ${stepName}\n`;
    const start = jobBlock.indexOf(marker);
    assert.notEqual(start, -1, `missing step ${stepName}`);
    const remainder = jobBlock.slice(start + marker.length);
    const next = remainder.indexOf('\n      - name: ');
    const end = next >= 0 ? start + marker.length + next + 1 : jobBlock.length;
    return `${jobBlock.slice(0, start)}${mutate(jobBlock.slice(start, end))}${jobBlock.slice(end)}`;
  });
}
