#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { validatePublishImageWorkflow } from './validate-publish-image-workflow.mjs';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

export function inspectOpportunity(root, opportunityId) {
  const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');
  const policy = JSON.parse(read('.github/agent-opportunities.json'));
  assert(policy.version === 3, 'opportunity policy must be version 3');
  const policyId = ['application-release', 'destructive-maintenance'].includes(opportunityId)
    ? 'release-destruction'
    : opportunityId;
  const opportunity = policy.opportunities.find(item => item.id === policyId);
  assert(opportunity, `unknown opportunity: ${opportunityId}`);
  const routine = opportunity.team?.coordinator?.profile;
  assert(routine?.model === 'gpt-5.6-sol',
    'sensitive routine coordinator must remain Sol');
  assert(routine.effort === 'medium' && routine.context === 'default',
    'routine coordinator must use medium/default');
  const exception = opportunity.conditionalProfiles?.find(profile =>
    profile.kind === 'risk-triggered-frontier-review');
  assert(exception?.profile?.model === 'gpt-5.6-sol' &&
    exception.profile.effort === 'max' &&
    exception.profile.context === 'long_context',
  'critical reviewer must use Sol max/long_context');
  assert(exception.requiresTriggerReceipt === true &&
    exception.triggerIds?.length >= 1,
  'critical review requires explicit trigger ids and a receipt');

  let facts;
  if (opportunityId === 'publication') {
    const capture = read('tools/capture-publication-route-contract.sh');
    const middleware = read('FSTService/Api/PublicReadGateMiddleware.cs');
    assert(capture.includes('idle, unfrozen publication') &&
      capture.includes('expected exactly 55 route captures'),
    'publication capture must preserve idle/unfrozen and route-count gates');
    for (const value of [
      'X-FST-Public-Read-Mode',
      'X-FST-Public-Read-Freeze-Reason',
      'StatusCodes.Status503ServiceUnavailable',
      'EndpointHandlesMaxScoreMaintenanceRead',
      '/api/songs',
      '/api/paths/',
    ]) {
      assert(middleware.includes(value), `publication gate is missing ${value}`);
    }
    facts = {
      headers: ['X-FST-Public-Read-Mode', 'X-FST-Public-Read-Freeze-Reason'],
      failClosedStatus: 503,
      endpointOwnedMaintenanceReads: ['/api/songs', '/api/paths/*'],
      routeContractCount: 55,
    };
  } else if (opportunityId === 'provenance') {
    const agent = read('AGENTS.md');
    const docs = read('docs/governance/agent-budget.md');
    const schema = read('FSTService/Persistence/PublicationPathArtifactSchema.cs');
    assert(/provenance/i.test(agent) && /historical correctness/i.test(agent),
      'canonical agent contract must preserve provenance and historical correctness');
    assert(opportunity.phases?.some(phase =>
      phase.kind === 'deterministic' &&
      phase.tool === 'preflight-provenance'),
      'provenance opportunity must require the registered provenance manifest preflight');
    assert(/provenance/i.test(docs), 'budget governance must retain provenance routing');
    for (const value of [
      'ContractVersion',
      'ManifestVersion',
      "'table'",
      "'publicationId'",
      "'scrapeId'",
      "'source'",
      "'authoritative'",
      'legacy_live_backfill',
    ]) {
      assert(schema.includes(value), `provenance schema is missing ${value}`);
    }
    assert(schema.includes('Bootstrap of the current publication from live rows'),
      'provenance bootstrap must remain current-publication-only');
    facts = {
      versions: ['ContractVersion', 'ManifestVersion'],
      authoritativeBindingFields: ['table', 'publicationId', 'scrapeId', 'source', 'authoritative'],
      bootstrapSource: 'legacy_live_backfill',
      bootstrapScope: 'current publication only',
    };
  } else if (opportunityId === 'application-release') {
    const errors = validatePublishImageWorkflow(
      read('.github/workflows/publish-image.yml'),
      read('FortniteFestivalWeb/Dockerfile'),
    );
    assert(errors.length === 0, `publish workflow contract failed: ${errors.join('; ')}`);
    assert(opportunity.variants.includes('application-release'),
      'application release variant missing');
    assert(opportunity.phases.some(phase =>
      phase.kind === 'deterministic' &&
      phase.variant === 'application-release' &&
      phase.tool === 'preflight-application-release'),
    'application release variant must use its registered preflight');
    facts = {
      variant: 'application-release',
      targetShaRequired: true,
      immutableImageRevisionRequired: true,
      publicPathVerificationRequired: true,
      rollbackClass: 'immutable image rollback',
    };
  } else if (opportunityId === 'destructive-maintenance') {
    const schemaTests = read('FSTService.Tests/Unit/SnapshotGenerationDropSchemaTests.cs');
    const dropProgram = read('tools/FstSnapshotGenerationDrop/Program.cs');
    const runbook = read('docs/database/SnapshotGenerationDropRunbook.md');
    execFileSync(process.execPath, ['tools/check-docs.mjs'], { cwd: root, stdio: 'pipe' });
    assert(opportunity.variants.includes('destructive-maintenance'),
      'destructive maintenance variant missing');
    assert(opportunity.phases.some(phase =>
      phase.kind === 'deterministic' &&
      phase.variant === 'destructive-maintenance' &&
      phase.tool === 'preflight-destructive-maintenance'),
    'destructive maintenance variant must use its registered preflight');
    for (const command of ['select-canary', 'plan', 'drop', 'confirm', 'attest']) {
      assert(dropProgram.includes(`"${command}" =>`), `drop CLI is missing ${command}`);
    }
    const forbiddenSql = [
      'DROP TABLE IF EXISTS',
      'CASCADE',
      'DROP INDEX',
      'SECURITY DEFINER',
      'GRANT ',
    ];
    for (const forbidden of forbiddenSql) {
      assert(schemaTests.includes(forbidden), `drop schema tests are missing ${forbidden}`);
    }
    assert(schemaTests.includes('DROP FUNCTION IF EXISTS'),
      'drop schema tests must retain the exact non-cascading function-drop contract');
    assert(/Assert\.Equal\(\s*6,\s*CountOccurrences\([\s\S]*?"DROP FUNCTION IF EXISTS"/m
      .test(schemaTests), 'drop schema tests must retain the exact function-drop count');
    assert(runbook.includes('publication `162` remains current, idle, and unfrozen'),
      'drop runbook must retain the exact current-publication gate');
    assert(/Before DROP, rollback is ordinary Q2 reattach\. After committed DROP, rollback\s+is logical archive restore only\./i
      .test(runbook),
    'destructive rollback must switch from reattach to logical restore at DROP');
    facts = {
      variant: 'destructive-maintenance',
      commands: ['select-canary', 'plan', 'drop', 'confirm', 'attest'],
      forbiddenSql,
      allowedFunctionDropPattern: 'DROP FUNCTION IF EXISTS with exact expected count',
      rollbackAfterCommit: 'logical restore, never reattach',
      historicalPublicationGate: 'publication 162 remains current, idle, and unfrozen',
    };
  } else {
    throw new Error(`unsupported preflight opportunity: ${opportunityId}`);
  }

  return {
    opportunity: opportunityId,
    routine,
    exception: exception.profile,
    escalationTriggers: exception.triggerIds,
    requiresTriggerReceipt: exception.requiresTriggerReceipt,
    facts,
  };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  try {
    console.log(JSON.stringify(inspectOpportunity(process.cwd(), process.argv[2]), null, 2));
  } catch (error) {
    console.error(`agent-opportunity-preflight: ${error.message}`);
    process.exitCode = 1;
  }
}
