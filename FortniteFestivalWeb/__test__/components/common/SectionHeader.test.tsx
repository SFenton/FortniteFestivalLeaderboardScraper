import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import SectionHeader from '../../../src/components/common/SectionHeader';

describe('SectionHeader', () => {
  it('renders title text', () => {
    render(<SectionHeader title="My Title" />);
    expect(screen.getByText('My Title')).toBeTruthy();
  });

  it('does not render description when omitted', () => {
    const { container } = render(<SectionHeader title="Title Only" />);
    const divs = container.querySelectorAll('div');
    expect(divs).toHaveLength(1);
    expect(divs[0].textContent).toBe('Title Only');
  });

  it('renders description when provided', () => {
    render(<SectionHeader title="Title" description="Some hint text" />);
    expect(screen.getByText('Title')).toBeTruthy();
    expect(screen.getByText('Some hint text')).toBeTruthy();
  });

  it('applies description class when flush is false', () => {
    const { container } = render(<SectionHeader title="T" description="D" />);
    const descDiv = container.querySelectorAll('div')[1];
    expect(descDiv.className).toContain('description');
    expect(descDiv.className).not.toContain('Flush');
  });

  it('applies descriptionFlush class when flush is true', () => {
    const { container } = render(<SectionHeader title="T" description="D" flush />);
    const descDiv = container.querySelectorAll('div')[1];
    expect(descDiv.className).toContain('descriptionFlush');
  });

  it('does not render description div for empty string', () => {
    const { container } = render(<SectionHeader title="T" description="" />);
    const divs = container.querySelectorAll('div');
    expect(divs).toHaveLength(1);
  });

  it('applies title class to the title div', () => {
    const { container } = render(<SectionHeader title="Styled" />);
    const titleDiv = container.querySelector('div');
    expect(titleDiv?.className).toContain('title');
  });
});
