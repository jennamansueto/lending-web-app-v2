// Strict numeric parsing that mirrors legacy .NET CurrentCulture (en-US)
// decimal.Parse / int.Parse semantics (BR-ELG-011): thousands separators parse,
// currency symbols and blanks do not.
export class FormatError extends Error {}

const DECIMAL_RE = /^\s*-?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?\s*$/;
const INT_RE = /^\s*-?(\d{1,3}(,\d{3})+|\d+)\s*$/;

export function parseDecimal(text: string): number {
  if (!DECIMAL_RE.test(text)) throw new FormatError();
  return Number(text.trim().replace(/,/g, ''));
}

export function parseIntStrict(text: string): number {
  if (!INT_RE.test(text)) throw new FormatError();
  return Number(text.trim().replace(/,/g, ''));
}
