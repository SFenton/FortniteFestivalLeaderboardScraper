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
}

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
    'echo "web=true" >> "$GITHUB_OUTPUT"\n          fi\n\n      - name: Setup Node.js',
    'echo "web=false" >> "$GITHUB_OUTPUT"\n          fi\n\n      - name: Setup Node.js',
  );
  assert.ok(validatePublishImageWorkflow(nonWebMutation, dockerfile).some(error => error.includes('web-affecting')));
});

test('moving a ui-utils file outside its root still classifies the source package', () => {
  const result = classifyChangedPaths([
    'packages/ui-utils/src/stagger.ts',
    'archive/stagger.ts',
  ]);
  assert.equal(result.uiUtils, true);
  assert.equal(result.web, true);
});

test('ui-utils bump step cannot be disabled', () => {
  const mutated = mutateStep(
    workflow,
    'version-bump',
    'Bump @festival/ui-utils version',
    block => block.replace(
      /        if: .*/,
      '        if: false',
    ),
  );
  assert.ok(
    validatePublishImageWorkflow(mutated, dockerfile)
      .some(error => error.includes('Bump @festival/ui-utils version has incorrect condition')),
  );
});

test('version bump must regenerate the dependency license manifest', () => {
  const wrongCommand = mutateStep(
    workflow,
    'version-bump',
    'Regenerate dependency license manifest',
    block => block.replace(
      'run: yarn licenses:generate',
      'run: yarn licenses:check',
    ),
  );
  assert.ok(
    validatePublishImageWorkflow(wrongCommand, dockerfile)
      .some(error => error.includes('must run exactly yarn licenses:generate')),
  );

  const wrongDirectory = mutateStep(
    workflow,
    'version-bump',
    'Regenerate dependency license manifest',
    block => block.replace(
      'working-directory: FortniteFestivalWeb',
      'working-directory: .',
    ),
  );
  assert.ok(
    validatePublishImageWorkflow(wrongDirectory, dockerfile)
      .some(error => error.includes('must run from FortniteFestivalWeb')),
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
