/**
 * Display helpers. They never round, truncate or re-scale: the digits rendered are exactly the
 * digits the service returned. Only the currency symbol, the percent sign and thousands separators
 * — none of which carry value — are added, matching the legacy `ToString("C2")` / `"0.00"` labels.
 */

export function groupDigits(decimalText: string): string {
  const negative = decimalText.startsWith('-');
  const digits = negative ? decimalText.slice(1) : decimalText;
  const [whole, fraction] = digits.split('.');
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  return (negative ? '-' : '') + (fraction === undefined ? grouped : `${grouped}.${fraction}`);
}

export function money(decimalText: string | null | undefined): string {
  if (decimalText === null || decimalText === undefined || decimalText === '') return '';
  return decimalText.startsWith('-')
    ? `-$${groupDigits(decimalText.slice(1))}`
    : `$${groupDigits(decimalText)}`;
}

export function percent(decimalText: string | null | undefined): string {
  if (decimalText === null || decimalText === undefined || decimalText === '') return '';
  return `${decimalText} %`;
}

/** Legacy `decimal.Parse` / `int.Parse` acceptance, used only for the invalid-numbers message box. */
export function isDecimal(text: string): boolean {
  return /^\s*[-+]?(\d+(\.\d*)?|\.\d+)\s*$/.test(text);
}

export function isInteger(text: string): boolean {
  return /^\s*[-+]?\d+\s*$/.test(text);
}
