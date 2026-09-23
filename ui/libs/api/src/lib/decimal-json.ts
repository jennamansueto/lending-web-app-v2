/**
 * The service serializes money and rates as .NET `decimal`, so the JSON text carries the exact
 * scale the business rules produced (`4500.00`, `6.50`). `JSON.parse` would turn those into IEEE
 * doubles and lose both the scale and, for large values, precision. Every numeric literal is
 * therefore captured as its verbatim source text and handed to the screens as a string, which is
 * what they render.
 */
export function parseDecimalJson<T>(text: string): T {
  return JSON.parse(quoteNumbers(text)) as T;
}

const NUMBER_LITERAL = /(:\s*|\[\s*|,\s*)(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)(?=\s*[,}\]])/g;

function quoteNumbers(text: string): string {
  let result = '';
  let index = 0;
  let inString = false;
  let escaped = false;

  // Only literals outside of JSON strings may be quoted, so the scan tracks string state itself
  // instead of running the expression over the whole document.
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (inString) {
      if (escaped) escaped = false;
      else if (ch === '\\') escaped = true;
      else if (ch === '"') {
        inString = false;
        result += text.slice(index, i + 1);
        index = i + 1;
      }
      continue;
    }
    if (ch === '"') {
      inString = true;
      result += text.slice(index, i).replace(NUMBER_LITERAL, '$1"$2"');
      index = i;
    }
  }
  result += text.slice(index).replace(NUMBER_LITERAL, '$1"$2"');
  return result;
}
