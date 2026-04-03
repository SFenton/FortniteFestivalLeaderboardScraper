---
name: web-component
description: "Create a new reusable UI component in FortniteFestivalWeb. Use when building a component for the shared library (common/, display/, page/). Covers component file, CSS module, stagger integration, and tests."
argument-hint: "Component name and purpose (e.g., 'ProgressBar - shows completion percentage')"
---

# New Web Component

## When to Use

- Adding a new reusable component to the shared library
- Creating a design system primitive

## Prerequisites

Read the UX consistency registry: `/memories/repo/design/ux-consistency-registry.md`

## Procedure

### 1. Create Component

Create `FortniteFestivalWeb/src/components/{category}/{ComponentName}.tsx`:

```tsx
import { CSSProperties } from 'react';
import s from './{ComponentName}.module.css';

interface {ComponentName}Props {
  // Required props
  title: string;
  // Optional stagger integration
  style?: CSSProperties;
  onAnimationEnd?: (e: React.AnimationEvent) => void;
}

export function {ComponentName}({ title, style, onAnimationEnd }: {ComponentName}Props) {
  return (
    <div className={s.root} style={style} onAnimationEnd={onAnimationEnd}>
      {title}
    </div>
  );
}
```

Key requirements:
- Support `style?` + `onAnimationEnd?` for stagger integration
- Props interface exported for consumers
- Functional component (not class)

### 2. Create CSS Module

Create `FortniteFestivalWeb/src/components/{category}/{ComponentName}.module.css`:

```css
.root {
  /* Component styles */
}
```

Rules:
- Only create if ≥3 CSS rules needed
- Use CSS custom properties from `theme.css`
- camelCase class names
- Mobile-first responsive

### 3. Write Tests

Create `FortniteFestivalWeb/__test__/components/{category}/{ComponentName}.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { {ComponentName} } from '../../../src/components/{category}/{ComponentName}';

describe('{ComponentName}', () => {
  it('renders with required props', () => {
    render(<{ComponentName} title="Test" />);
    expect(screen.getByText('Test')).toBeInTheDocument();
  });

  it('applies stagger style', () => {
    const style = { opacity: 0 };
    const { container } = render(<{ComponentName} title="Test" style={style} />);
    expect(container.firstChild).toHaveStyle({ opacity: 0 });
  });
});
```

### 4. Verify

```bash
cd FortniteFestivalWeb && npm test
```

## Checklist

- [ ] Component supports `style?` + `onAnimationEnd?` for stagger
- [ ] CSS module co-located (if ≥3 rules)
- [ ] Uses `theme.css` variables (no duplicate custom properties)
- [ ] Props interface exported
- [ ] Unit tests cover rendering + stagger integration
- [ ] Reviewed by web-principal-designer for visual consistency
