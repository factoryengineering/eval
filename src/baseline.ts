import { complete as realComplete } from './providers.js';
import type { Completion, Config, EvalCase, ModelSpec, Trial } from './types.js';

type Complete = (spec: ModelSpec, system: string, prompt: string) => Promise<Completion>;

export const bucketOf = (failure: string): string => {
  if (failure.startsWith('rule ')) return failure.split(':')[0]!;
  if (failure.startsWith('test failed')) return 'test failed';
  if (failure.startsWith('tests found')) return 'tests found';
  if (failure.startsWith('mutant ')) return failure.split(':')[0]!;
  return failure.split(':')[0]!;
};

export interface BaselineCell {
  model: string;
  caseId: string;
  n: number;
  passRate: number;
  buckets: Record<string, number>;
}

export function summarize(trials: Trial[]): BaselineCell[] {
  const keys = [...new Set(trials.map((t) => `${t.model}\u0000${t.caseId}`))];
  return keys.map((key) => {
    const [model, caseId] = key.split('\u0000') as [string, string];
    const mine = trials.filter((t) => t.model === model && t.caseId === caseId);
    const buckets: Record<string, number> = {};
    for (const t of mine) for (const b of new Set(t.failures.map(bucketOf))) buckets[b] = (buckets[b] ?? 0) + 1 / mine.length;
    return { model, caseId, n: mine.length, passRate: mine.filter((t) => t.pass).length / mine.length, buckets };
  });
}

export const report = (cells: BaselineCell[]): string =>
  cells
    .map((c) => {
      const failing = Object.entries(c.buckets).sort((a, b) => b[1] - a[1]).map(([b, r]) => `${b} ${r.toFixed(2)}`).join('; ');
      return `${c.model.padEnd(12)} ${c.caseId.padEnd(22)} pass ${c.passRate.toFixed(2)} (n=${c.n})${failing ? `  fails: ${failing}` : ''}`;
    })
    .join('\n');

export async function runBaseline(
  config: Config,
  cases: EvalCase[],
  gradeFn: (c: EvalCase, text: string) => Promise<string[]>,
  complete: Complete = realComplete,
  onTrial: (t: Trial) => void = () => {},
  concurrency = 4,
): Promise<Trial[]> {
  const jobs = config.models.flatMap((spec) => cases.flatMap((c) => Array.from({ length: config.trials }, (_, n) => ({ spec, c, n }))));
  const trials: Trial[] = new Array(jobs.length);
  let next = 0;
  const worker = async () => {
    for (let i = next++; i < jobs.length; i = next++) {
      const { spec, c, n } = jobs[i]!;
      const r = await complete(spec, '', c.prompt);
      const failures = await gradeFn(c, r.text);
      trials[i] = {
        model: spec.id, line: '(baseline)', variant: 'without', caseId: c.id, trial: n, pass: failures.length === 0, failures,
        inputTokens: r.inputTokens, outputTokens: r.outputTokens, estimated: r.estimated, output: r.text,
      };
      onTrial(trials[i]!);
    }
  };
  await Promise.all(Array.from({ length: Math.min(concurrency, jobs.length) }, worker));
  return trials;
}
