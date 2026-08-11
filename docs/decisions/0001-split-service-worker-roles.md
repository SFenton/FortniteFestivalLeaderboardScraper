---
status: decision
owner: service
last_verified: 2026-08-11
last_verified_commit: 453fd9b6
sources:
  - FSTService/Program.cs
  - FSTService/HostedWorkerMode.cs
  - docker-compose.yml
  - deploy/docker-compose.yml
update_triggers:
  - Service images, hosted modes, or API/worker role ownership changes.
---

# ADR 0001: Split API and worker roles from one image

## Decision

Build one FSTService image and run it with role-specific commands and
configuration. Production uses a public `fstservice` API/frontend role and a
separate `fstworker` mutation role.

## Rationale

- The API remains available while long scrape/post-processing work runs.
- Worker-only CPU, memory, Docker control, and mutation permissions stay off the
  public service.
- One image avoids binary/schema drift between serving and worker code.
- Registration-only, rollout-read-only, setup, and one-shot modes reuse the
  same dependency graph and validation.

## Consequences

- Role files and Compose command lines are part of the architecture.
- Service and worker feature flags may intentionally differ.
- Changes to hosted-service registration require both code and deployment
  documentation review.
