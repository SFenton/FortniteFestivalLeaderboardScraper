import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { I18nextProvider } from 'react-i18next';
import i18n from '../../../src/i18n';
import '../../../src/pages/settings/settingsEnglish';
import SettingsVersionList from '../../../src/pages/settings/SettingsVersionList';

function renderList(commit?: string) {
  return render(
    <I18nextProvider i18n={i18n}>
      <SettingsVersionList
        rows={[
          { id: 'app', label: 'App Version', value: '0.1.1603', commit, testId: 'settings-app-version' },
          { id: 'service', label: 'Service Version', value: '2.4.0' },
        ]}
      />
    </I18nextProvider>,
  );
}

function spokenText(element: Element): string {
  let text = '';
  element.childNodes.forEach(node => {
    if (node.nodeType === Node.TEXT_NODE) {
      text += node.textContent;
    } else if (node instanceof Element && node.getAttribute('aria-hidden') !== 'true') {
      text += spokenText(node);
    }
  });
  return text;
}

describe('SettingsVersionList accessibility', () => {
  it('renders a description list of term/definition pairs in label → value order', () => {
    renderList('abc1234');
    const list = screen.getByTestId('settings-version-list');
    expect(list.tagName).toBe('DL');

    const terms = within(list).getAllByRole('term');
    const definitions = within(list).getAllByRole('definition');
    expect(terms.map(term => term.textContent)).toEqual(['App Version', 'Service Version']);
    expect(definitions).toHaveLength(2);
    terms.forEach((term, index) => {
      const definition = definitions[index]!;
      expect(term.parentElement).toBe(definition.parentElement);
      expect(term.nextElementSibling).toBe(definition);
    });
  });

  it('announces the build commit with a word instead of the decorative middle dot', () => {
    renderList('abc1234');
    const appVersion = screen.getByTestId('settings-app-version');

    const separator = within(appVersion).getByText('·');
    expect(separator.getAttribute('aria-hidden')).toBe('true');
    expect(spokenText(appVersion).replace(/\s+/g, ' ').trim()).toBe('0.1.1603, commit abc1234');
  });

  it('shows only the version when no commit is available', () => {
    renderList('');
    const appVersion = screen.getByTestId('settings-app-version');
    expect(appVersion.textContent).toBe('0.1.1603');
    expect(within(appVersion).queryByText('·')).toBeNull();
  });

  it('contains no interactive targets', () => {
    renderList('abc1234');
    const list = screen.getByTestId('settings-version-list');
    expect(list.querySelectorAll('a, button, input, select, textarea, [tabindex]')).toHaveLength(0);
  });
});
