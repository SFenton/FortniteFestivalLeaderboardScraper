import { useMemo } from 'react';
import katex from 'katex';
import 'katex/dist/katex.min.css';

interface MathProps {
  /** LaTeX expression to render. */
  tex: string;
  /** Render as a block-level element instead of inline. */
  block?: boolean;
}

/**
 * Render a LaTeX expression via KaTeX.
 * Uses `renderToString` with `throwOnError: false` so invalid TeX
 * degrades gracefully rather than crashing the component.
 */
export default function Math({ tex, block }: MathProps) {
  const html = useMemo(
    () => katex.renderToString(tex, { displayMode: !!block, throwOnError: false }),
    [tex, block],
  );

  if (block) {
    return <div dangerouslySetInnerHTML={{ __html: html }} />;
  }
  return <span dangerouslySetInnerHTML={{ __html: html }} />;
}
