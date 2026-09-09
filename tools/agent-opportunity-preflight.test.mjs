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
  test(`${opportunity} uses deterministic routine coordination with exception-only max/long`, () => {
    const result = inspectOpportunity(process.cwd(), opportunity);
    assert.equal(result.routine.effort, 'medium');
    assert.equal(result.routine.context, 'default');
    assert.equal(result.exception.effort, 'max');
    assert.equal(result.exception.context, 'long_context');
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
        assert.doesNotMatch(text, /auto[- ]approved|no longer externally gated/i, file);
        assert.match(text, /authorization/i, file);
      }
    });
  });
}
