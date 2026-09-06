import { afterEach, describe, expect, it, vi } from 'vitest';
import { installFocusAppearance } from '../../src/utils/focusAppearance';
import styles from '../../src/styles/focusAppearance.module.css';

const attribute = 'data-fst-quiet-focus';
let dispose: (() => void) | undefined;

afterEach(() => {
  dispose?.();
  dispose = undefined;
  document.documentElement.removeAttribute(attribute);
  document.documentElement.classList.remove(styles.root!);
  document.body.replaceChildren();
  vi.restoreAllMocks();
});

function start() {
  dispose = installFocusAppearance();
}

function key(target: EventTarget, value: string, init: KeyboardEventInit = {}) {
  target.dispatchEvent(new KeyboardEvent('keydown', { key: value, bubbles: true, cancelable: true, ...init }));
}

function pointer(target: EventTarget = document) {
  target.dispatchEvent(new Event('pointerdown', { bubbles: true, cancelable: true }));
}

describe('focus appearance provenance', () => {
  it('binds the CSS Module scope so production extraction retains it, preserving any existing scope', () => {
    expect(styles.root).toBeTruthy();
    start();
    expect(document.documentElement).toHaveClass(styles.root!);
    dispose?.();
    expect(document.documentElement).not.toHaveClass(styles.root!);
    document.documentElement.classList.add(styles.root!);
    start();
    dispose?.();
    expect(document.documentElement).toHaveClass(styles.root!);
    dispose = undefined;
  });

  it('starts quietly without moving or removing focus', () => {
    const button = document.createElement('button');
    document.body.append(button);
    button.focus();
    const focus = vi.spyOn(HTMLElement.prototype, 'focus');
    const blur = vi.spyOn(HTMLElement.prototype, 'blur');
    start();
    expect(document.documentElement).toHaveAttribute(attribute);
    expect(button).toHaveFocus();
    expect(focus).not.toHaveBeenCalled();
    expect(blur).not.toHaveBeenCalled();
  });

  it.each(['Tab', 'Escape', 'Enter', ' ', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown'])(
    'returns to native visibility on navigation key %s and quiets the next pointer',
    value => {
      start();
      key(document, value);
      expect(document.documentElement).not.toHaveAttribute(attribute);
      pointer();
      expect(document.documentElement).toHaveAttribute(attribute);
    },
  );

  it.each([
    '<input>',
    '<input type="search">',
    '<input type="email">',
    '<textarea></textarea>',
    '<div contenteditable="true"><span></span></div>',
    '<div contenteditable="plaintext-only"><span></span></div>',
    '<div role="textbox"><span></span></div>',
  ])('does not mistake editing %s for navigation', markup => {
    document.body.innerHTML = markup;
    const target = document.body.querySelector('span') ?? document.body.firstElementChild!;
    start();
    for (const value of ['x', 'Enter', ' ', 'ArrowLeft', 'Home']) {
      key(target, value);
      expect(document.documentElement).toHaveAttribute(attribute);
    }
    key(target, 'Tab');
    expect(document.documentElement).not.toHaveAttribute(attribute);
    pointer(target);
    key(target, 'Escape');
    expect(document.documentElement).not.toHaveAttribute(attribute);
  });

  it.each(['button', 'submit', 'reset', 'checkbox', 'radio', 'range', 'color', 'file', 'image'])(
    'recognizes keyboard operation of input type %s',
    type => {
      const input = document.createElement('input');
      input.type = type;
      document.body.append(input);
      start();
      key(input, 'Enter');
      expect(document.documentElement).not.toHaveAttribute(attribute);
    },
  );

  it('honors a non-editable island inside an editor', () => {
    document.body.innerHTML = '<div contenteditable="true"><button contenteditable="false">Action</button></div>';
    start();
    key(document.querySelector('button')!, 'Enter');
    expect(document.documentElement).not.toHaveAttribute(attribute);
  });

  it('ignores composing, legacy IME, printable, and modifier-only keys', () => {
    start();
    key(document, 'Enter', { isComposing: true });
    key(document, 'Enter', { keyCode: 229 });
    key(document, 'x');
    key(document, 'Shift');
    expect(document.documentElement).toHaveAttribute(attribute);
  });

  it('uses capture phase before a component handles a navigation key', () => {
    const button = document.createElement('button');
    document.body.append(button);
    start();
    let quietDuringHandler = true;
    button.addEventListener('keydown', () => {
      quietDuringHandler = document.documentElement.hasAttribute(attribute);
    });
    key(button, 'Tab');
    expect(quietDuringHandler).toBe(false);
  });

  it('does not reset pointer provenance during app-managed focus transfers or blur', () => {
    const launcher = document.createElement('button');
    const panel = document.createElement('div');
    panel.tabIndex = -1;
    document.body.append(launcher, panel);
    start();
    pointer(launcher);
    launcher.focus();
    panel.focus();
    launcher.focus();
    expect(document.documentElement).toHaveAttribute(attribute);
  });

  it('lets unclassified activation escape stale pointer state without canceling activation', () => {
    start();
    pointer();
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    document.dispatchEvent(click);
    expect(document.documentElement).not.toHaveAttribute(attribute);
    expect(click.defaultPrevented).toBe(false);
  });

  it('does not classify a pointer click with zero detail as keyboard or virtual input', () => {
    start();
    const click = new MouseEvent('click', { bubbles: true, detail: 0 });
    Object.defineProperty(click, 'pointerType', { value: 'touch' });
    document.dispatchEvent(click);
    expect(document.documentElement).toHaveAttribute(attribute);
    document.dispatchEvent(new MouseEvent('click', { bubbles: true, detail: 1 }));
    expect(document.documentElement).toHaveAttribute(attribute);
  });

  it('is idempotent and removes listeners on disposal', () => {
    start();
    expect(installFocusAppearance()).toBe(dispose);
    dispose?.();
    pointer();
    expect(document.documentElement).not.toHaveAttribute(attribute);
    start();
    expect(document.documentElement).toHaveAttribute(attribute);
  });

  it('restores the previous attribute on disposal', () => {
    document.documentElement.setAttribute(attribute, 'previous');
    start();
    key(document, 'Tab');
    dispose?.();
    expect(document.documentElement.getAttribute(attribute)).toBe('previous');
    dispose = undefined;
  });
});
