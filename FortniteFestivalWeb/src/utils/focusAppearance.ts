import styles from '../styles/focusAppearance.module.css';

const QUIET_FOCUS_ATTRIBUTE = 'data-fst-quiet-focus';
const LEGACY_IME_KEY_CODE = 229;
const installations = new WeakMap<Document, () => void>();
const navigationKeys = new Set([
  'Tab', 'Escape', 'Enter', ' ', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight',
  'Home', 'End', 'PageUp', 'PageDown',
]);
const nonTextInputTypes = new Set([
  'button', 'submit', 'reset', 'checkbox', 'radio', 'range', 'color', 'file', 'image', 'hidden',
]);

function isTextEntry(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) return false;
  const control = target.closest('input, textarea, [contenteditable], [role="textbox"]');
  if (control instanceof HTMLInputElement) return !nonTextInputTypes.has(control.type);
  if (control instanceof HTMLTextAreaElement) return true;
  return control != null && control.getAttribute('contenteditable') !== 'false';
}

/** Keep browser focus ownership/heuristics intact; only quiet passive and pointer decoration. */
export function installFocusAppearance(doc: Document = document): () => void {
  const installed = installations.get(doc);
  if (installed) return installed;

  const root = doc.documentElement;
  const rootClass = styles.root!;
  const hadRootClass = root.classList.contains(rootClass);
  const previousAttribute = root.getAttribute(QUIET_FOCUS_ATTRIBUTE);
  const quiet = () => root.setAttribute(QUIET_FOCUS_ATTRIBUTE, '');
  const native = () => root.removeAttribute(QUIET_FOCUS_ATTRIBUTE);
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.isComposing || event.keyCode === LEGACY_IME_KEY_CODE || !navigationKeys.has(event.key)) return;
    if (isTextEntry(event.target) && event.key !== 'Tab' && event.key !== 'Escape') return;
    native();
  };
  const onClick = (event: MouseEvent) => {
    // Browser-originated non-pointer activation can escape stale touch state.
    // Application clicks (for example, a download link) are not new input.
    if (event.isTrusted && event.detail === 0 && !(event as PointerEvent).pointerType) native();
  };

  root.classList.add(rootClass);
  quiet();
  doc.addEventListener('pointerdown', quiet, { capture: true, passive: true });
  doc.addEventListener('keydown', onKeyDown, true);
  doc.addEventListener('click', onClick, true);
  const dispose = () => {
    doc.removeEventListener('pointerdown', quiet, true);
    doc.removeEventListener('keydown', onKeyDown, true);
    doc.removeEventListener('click', onClick, true);
    if (previousAttribute == null) native();
    else root.setAttribute(QUIET_FOCUS_ATTRIBUTE, previousAttribute);
    if (!hadRootClass) root.classList.remove(rootClass);
    installations.delete(doc);
  };
  installations.set(doc, dispose);
  return dispose;
}
