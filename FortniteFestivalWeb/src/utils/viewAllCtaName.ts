/**
 * Accessible name for a card's full-width "View all" call to action.
 *
 * Starts with the visible label so voice control matches it (WCAG 2.5.3), then
 * names the card so equal buttons in different cards stay distinct. Mirrors the
 * native apps' `ViewAllCta.Name` rule, e.g. "View all rivals, Lead Rivals".
 */
export function viewAllCtaName(label: string, card: string): string {
  return `${label}, ${card}`;
}
