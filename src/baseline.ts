import { DEFAULT_MONITOR, criticalFailures, detectionPower, isSignal } from './monitor.js';
import type { MonitorSpec } from './monitor.js';
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

export interface MonitorRow {
  model: string;
  check: string;
  failures: number;
  n: number;
  signal: boolean;
}

export function monitor(trials: Trial[], checks: string[], spec: MonitorSpec = DEFAULT_MONITOR): MonitorRow[] {
  const models = [...new Set(trials.map((t) => t.model))];
  return models.flatMap((model) => {
    const mine = trials.filter((t) => t.model === model);
    return checks.map((check) => {
      const failures = mine.filter((t) => t.failures.some((f) => bucketOf(f) === check)).length;
      return { model, check, failures, n: mine.length, signal: isSignal(failures, mine.length, spec) };
    });
  });
}

export const checksOf = (cases: EvalCase[]): string[] => {
  const checks = new Set<string>(['compile', 'test failed']);
  for (const c of cases) {
    for (const rule of c.tests?.rules ?? []) checks.add(`rule ${rule}`);
    if (c.tests?.mutants.length) checks.add('mutant survived');
  }
  return [...checks];
};

export function monitorReport(rows: MonitorRow[], spec: MonitorSpec = DEFAULT_MONITOR): string {
  const n = rows[0]?.n ?? 0;
  const k = criticalFailures(n, spec);
  const head = `signal when failures >= ${k ?? 'never'} of ${n} per model (noise ${spec.noise}, alpha ${spec.alpha}); power to detect a true ${spec.effect} failure rate ${detectionPower(n, spec).toFixed(2)}`;
  const lines = rows.map((r) => `${r.model.padEnd(12)} ${r.check.padEnd(28)} ${String(r.failures).padStart(2)}/${r.n}  ${r.signal ? 'SIGNAL' : 'quiet'}`);
  return [head, ...lines].join('\n');
}
