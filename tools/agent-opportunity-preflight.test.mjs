import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { inspectOpportunity } from './agent-opportunity-preflight.mjs';

for (const opportunity of [
  'publication',
  'provenance',
  'application-release',
  'destructive-maintenance',
]) {
  test(`${opportunity} uses Luna routine ownership with receipt-bound Sol research`, () => {
    const result = inspectOpportunity(process.cwd(), opportunity);
    assert.equal(result.routine.model, 'gpt-5.6-luna');
    assert.equal(result.routine.effort, 'medium');
    assert.equal(result.routine.context, 'default');
    assert.equal(result.reviewer.model, 'gpt-5.6-luna');
    assert.equal(result.reviewer.effort, 'medium');
    assert.equal(result.reviewer.context, 'default');
    if (['publication', 'provenance'].includes(opportunity)) {
      assert.deepEqual(result.sourceImplementer, {
        model: 'gpt-6-sol',
        effort: 'max',
        context: 'default',
      });
    } else {
      assert.equal(result.sourceImplementer, null);
    }
    assert.ok(result.researchProfiles.length >= 1);
    for (const profile of result.researchProfiles) {
      assert.equal(profile.kind, 'research-frontier');
      assert.equal(profile.profile.model, 'gpt-5.6-sol');
      assert.equal(profile.profile.effort, 'high');
      assert.equal(profile.profile.context, 'default');
      assert.equal(profile.requiresTriggerReceipt, true);
    }
    assert.equal(result.requiresTriggerReceipt, true);
    assert.ok(result.escalationTriggers.length >= 1);
    assert.ok(Object.keys(result.facts).length >= 4);
    if (opportunity === 'destructive-maintenance') {
      assert.deepEqual(result.facts.forbiddenSql, [
        'DROP TABLE IF EXISTS',
        'CASCADE',
        'DROP INDEX',
        'SECURITY DEFINER',
        'GRANT ',
      ]);
      assert.equal(result.facts.historicalPublicationGate,
        'publication 162 remains current, idle, and unfrozen');
      assert.equal('currentPublicationGate' in result.facts, false);
    }
    if (opportunity === 'application-release') {
      assert.equal(result.facts.rollbackClass, 'immutable image rollback');
    }
  });
}

test('Sol source pin covers enabled source opportunities but not live or release execution', () => {
  const policy = JSON.parse(fs.readFileSync('.github/agent-opportunities.json', 'utf8'));
  const sourceIds = [
    'focused-tests', 'database', 'concurrency', 'performance', 'storage',
    'publication', 'provenance', 'cross-contract', 'debugging',
  ];
  for (const item of policy.opportunities) {
    const sourcePhases = item.phases.filter(phase => phase.kind === 'source-implementation');
    if (sourceIds.includes(item.id)) {
      assert.equal(item.team.sourceImplementer.profile.model, 'gpt-6-sol');
      assert.equal(sourcePhases.length, 1, item.id);
      assert.equal(sourcePhases[0].profileRef, 'source-implementer');
    } else {
      assert.equal(item.team.sourceImplementer, undefined, item.id);
      assert.equal(sourcePhases.length, 0, item.id);
    }
    assert.equal(item.team.repositoryApply.enabled, false, item.id);
  }
});

test('destructive parity is never documented as standing authorization', () => {
  for (const file of [
    '.github/skills/autonomous-plan-executor/SKILL.md',
    '.github/skills/database-management/SKILL.md',
    '.github/skills/database-management/references/orchestrator-modes.md',
    '.github/skills/database-management/references/security-data-safety.md',
    '.github/skills/postgres-database-expert/SKILL.md',
    '.github/skills/database-implementation/SKILL.md',
  ]) {
    const text = fs.readFileSync(file, 'utf8');
    assert.doesNotMatch(text, /auto[- ]approved|frontier authorization|no longer externally gated/i, file);
    assert.match(text, /operator authorization|destructive-maintenance machine/i, file);
  }
});
