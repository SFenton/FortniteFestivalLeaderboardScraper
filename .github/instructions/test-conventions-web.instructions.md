---
description: "Use when writing Vitest unit tests for FortniteFestivalWeb. Covers TestProviders, render helpers, testing-library patterns."
applyTo: "FortniteFestivalWeb/__test__/**/*.ts"
---

# Test Conventions — FortniteFestivalWeb (Vitest)

- Framework: Vitest + @testing-library/react.
- Test file mirrors src/ structure.
- Wrap components in `TestProviders` for context.
- Use `render()` from testing-library, not ReactDOM.
- Use `screen.getByText()`, `screen.getByRole()` for queries.
- Prefer `userEvent` over `fireEvent` for interactions.
- Mock API calls, not internal state.
