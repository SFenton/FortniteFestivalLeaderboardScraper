import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { privacyPolicy, PRIVACY_POLICY_CONTACT_URL, type PrivacyPolicy } from '../../../src/pages/settings/privacyPolicy';

const CANONICAL_DOC = resolve(__dirname, '../../../../docs/reference/privacy-policy.md');
const START_MARKER = '<!-- privacy-policy:start -->';
const END_MARKER = '<!-- privacy-policy:end -->';

function renderPrivacyPolicyMarkdown(policy: PrivacyPolicy): string {
  const blocks: string[] = [`**Effective date:** ${policy.effectiveDate}`, ...policy.intro];
  for (const section of policy.sections) {
    blocks.push(`### ${section.heading}`);
    blocks.push(...(section.paragraphs ?? []));
    if (section.items) {
      blocks.push(section.items
        .map(item => (item.label ? `- **${item.label}:** ${item.text}` : `- ${item.text}`))
        .join('\n'));
    }
    blocks.push(...(section.closing ?? []));
  }
  return blocks.join('\n\n');
}

function readCanonicalPolicyBlock(): string {
  const doc = readFileSync(CANONICAL_DOC, 'utf8');
  const start = doc.indexOf(START_MARKER);
  const end = doc.indexOf(END_MARKER);
  expect(start).toBeGreaterThanOrEqual(0);
  expect(end).toBeGreaterThan(start);
  return doc.slice(start + START_MARKER.length, end).trim();
}

describe('privacy policy content', () => {
  it('matches the canonical cross-platform text in docs/reference/privacy-policy.md', () => {
    expect(renderPrivacyPolicyMarkdown(privacyPolicy)).toBe(readCanonicalPolicyBlock());
  });

  it('covers the industry-standard sections with an effective date', () => {
    expect(privacyPolicy.title).toBe('Privacy Policy');
    expect(privacyPolicy.effectiveDate).toMatch(/^[A-Z][a-z]+ \d{1,2}, \d{4}$/);
    expect(privacyPolicy.sections.map(section => section.id)).toEqual([
      'information-we-collect',
      'how-we-use-information',
      'third-parties',
      'retention',
      'your-choices',
      'children',
      'security',
      'changes',
      'contact',
    ]);
    const thirdParties = privacyPolicy.sections.find(section => section.id === 'third-parties');
    expect(thirdParties?.items?.some(item => item.label === 'Epic Games')).toBe(true);
    const contact = privacyPolicy.sections.find(section => section.id === 'contact');
    expect(contact?.paragraphs?.join(' ')).toContain(PRIVACY_POLICY_CONTACT_URL);
  });

  it('uses unique section ids and non-empty content', () => {
    const ids = privacyPolicy.sections.map(section => section.id);
    expect(new Set(ids).size).toBe(ids.length);
    for (const section of privacyPolicy.sections) {
      const text = [...(section.paragraphs ?? []), ...(section.items ?? []).map(item => item.text), ...(section.closing ?? [])];
      expect(text.length).toBeGreaterThan(0);
      for (const value of text) expect(value.trim()).not.toBe('');
    }
  });
});
