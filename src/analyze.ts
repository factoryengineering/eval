import { fisherOneSided } from './stats.js';
import type { Trial } from './types.js';

export interface LineRow {
  model: string;
  line: string;
  withoutPass: number;
  withoutN: number;
  withPass: number;
  withN: number;
  p: number;
}

export function analyzeLines(trials: Trial[]): LineRow[] {
  const keys = [...new Set(trials.filter((t) => t.line !== '(guard)').map((t) => `${t.model}\u0000${t.line}`))];
  return keys.map((key) => {
    const [model, line] = key.split('\u0000') as [string, string];
    const mine = trials.filter((t) => t.model === model && t.line === line);
    const withTrials = mine.filter((t) => t.variant === 'with');
    const withoutTrials = mine.filter((t) => t.variant === 'without');
    const withPass = withTrials.filter((t) => t.pass).length;
    const withoutPass = withoutTrials.filter((t) => t.pass).length;
    return { model, line, withoutPass, withoutN: withoutTrials.length, withPass, withN: withTrials.length, p: fisherOneSided(withPass, withTrials.length, withoutPass, withoutTrials.length) };
  });
}

export const significant = (row: LineRow, alpha: number, minDelta: number): boolean =>
  row.p < alpha && row.withPass / row.withN - row.withoutPass / row.withoutN >= minDelta;

export function analysisReport(rows: LineRow[], alpha = 0.05, minDelta = 0.3): string {
  const bonferroni = alpha / rows.length;
  const lines = rows.map((r) => {
    const mark = significant(r, alpha, minDelta) ? (r.p < bonferroni ? 'LOAD-BEARING (survives Bonferroni)' : 'LOAD-BEARING') : 'not significant';
    return `${r.model.padEnd(12)} ${r.line.padEnd(16)} ${r.withoutPass}/${r.withoutN} -> ${r.withPass}/${r.withN}  p=${r.p.toFixed(4)}  ${mark}`;
  });
  const byLine = [...new Set(rows.map((r) => r.line))].map((line) => {
    const mine = rows.filter((r) => r.line === line && significant(r, alpha, minDelta));
    return `${line.padEnd(16)} significant on ${mine.length} of ${rows.filter((r) => r.line === line).length} models${mine.length ? ` (${mine.map((r) => r.model).join(', ')})` : ''}`;
  });
  return [`alpha ${alpha}, minDelta ${minDelta}, Bonferroni alpha ${bonferroni.toFixed(4)} over ${rows.length} comparisons`, ...lines, '', ...byLine].join('\n');
}
