# Phase 15: Agent Tooling — MCP Server, Agent, Instructions

**Status:** ⬜ Not Started
**Depends on:** Nothing
**Parallel with:** Anything

## Goal
Build FST MCP server (14 tools), custom agent, coding standards instructions, hooks, and prompt templates.

## Steps

### 15.1 — MCP Server
- [ ] Create `tools/fst-mcp/package.json` + `tsconfig.json`
- [ ] Implement `fst_find_page` tool
- [ ] Implement `fst_find_component` tool
- [ ] Implement `fst_find_hook` tool
- [ ] Implement `fst_page_tree` tool
- [ ] Implement `fst_api_endpoints` tool
- [ ] Implement `fst_db_schema` tool
- [ ] Implement `fst_settings_map` tool
- [ ] Implement `fst_fre_slides` tool
- [ ] Implement `fst_route_map` tool
- [ ] Implement `fst_coverage_check` tool
- [ ] Implement `fst_lint_check` tool
- [ ] Implement `fst_v8_ignore_check` tool
- [ ] Implement `fst_test_check` tool
- [ ] Implement `fst_design_token` tool

### 15.2 — Custom Agent
- [ ] Create `.github/agents/fst-guardian.agent.md`

### 15.3 — Coding Standards Instructions
- [ ] Create `.github/instructions/coding-standards.instructions.md` (web src/)
- [ ] Create `.github/instructions/testing-standards.instructions.md` (web __test__/)
- [ ] Create `.github/instructions/service-standards.instructions.md` (FSTService/)

### 15.4 — Pre-commit Hook
- [ ] Create `.github/hooks/pre-commit.json` (blocks v8 ignore insertion)

### 15.5 — Prompt Templates
- [ ] Create `.github/prompts/add-page.prompt.md`
- [ ] Create `.github/prompts/add-component.prompt.md`
- [ ] Create `.github/prompts/add-api-endpoint.prompt.md`
- [ ] Create `.github/prompts/add-fre-slide.prompt.md`

### 15.6 — Configuration
- [ ] Update `.vscode/mcp.json` with fst server entry
- [ ] Rewrite `.github/copilot-instructions.md` for post-refactor architecture

## Verification Checks

- [ ] `fst_find_page Songs` returns correct file tree
- [ ] `fst_v8_ignore_check` returns empty list
- [ ] `fst_coverage_check` returns all files ≥ 95%
- [ ] FST Guardian agent blocks v8 ignore insertion
- [ ] All .instructions.md load in Copilot
- [ ] All .prompt.md templates produce valid output
