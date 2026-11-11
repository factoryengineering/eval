import { DEFAULT_MONITOR, criticalFailures, detectionPower, isSignal } from './monitor.js';
import type { MonitorSpec } from './monitor.js';
import { complete as realComplete } from './providers.js';
import { apiBuckets } from './api.js';
import type { Completion, Config, EvalCase, GradeNotes, ModelSpec, Trial } from './types.js';

type Complete = (spec: ModelSpec, system: string, prompt: string) => Promise<Completion>;

export const bucketOf = (failure: string): string => {
  if (failure.startsWith('rule ')) return failure.split(':')[0]!;
  if (failure.startsWith('test failed')) return 'test failed';
  if (failure.startsWith('tests found')) return 'tests found';
  if (failure.startsWith('api advisory')) return 'api advisory';
  if (failure.startsWith('mutant ')) return failure.split(':')[0]!;
  return failure.split(':')[0]!;
};

export interface BaselineCell {
  model: string;
  caseId: string;
  n: number;
  passRate: number;
  buckets: Record<string, number>;
  cascade?: { bootstrapFailed: number; firstFail: Record<string, number>; independent: Record<string, number>; independentN: number; advisories: Record<string, number> };
}

const rates = (trialsOf: Trial[], pick: (t: Trial) => string[]): Record<string, number> => {
  const out: Record<string, number> = {};
  for (const t of trialsOf) for (const b of new Set(pick(t))) out[b] = (out[b] ?? 0) + 1 / trialsOf.length;
  return out;
};

export function summarize(trials: Trial[]): BaselineCell[] {
  const keys = [...new Set(trials.map((t) => `${t.model}\u0000${t.caseId}`))];
  return keys.map((key) => {
    const [model, caseId] = key.split('\u0000') as [string, string];
    const mine = trials.filter((t) => t.model === model && t.caseId === caseId);
    const buckets = rates(mine, (t) => t.failures.map(bucketOf));
    const cell: BaselineCell = { model, caseId, n: mine.length, passRate: mine.filter((t) => t.pass).length / mine.length, buckets };
    if (mine.some((t) => t.notes)) {
      const free = mine.filter((t) => !t.bootstrapNote);
      cell.cascade = {
        bootstrapFailed: 1 - free.length / mine.length,
        firstFail: rates(mine, (t) => t.failures.slice(0, 1).map(bucketOf)),
        independent: rates(free, (t) => t.failures.map(bucketOf)),
        independentN: free.length,
        advisories: rates(mine, (t) => (t.notes ?? []).map(bucketOf)),
      };
    }
    return cell;
  });
}

const fmt = (r: Record<string, number>) => Object.entries(r).sort((a, b) => b[1] - a[1]).map(([b, v]) => `${b} ${v.toFixed(2)}`).join('; ') || 'none';

const cascadeLines = (c: NonNullable<BaselineCell['cascade']>) => [
  `    create bootstrap failed ${c.bootstrapFailed.toFixed(2)}; first fail: ${fmt(c.firstFail)}`,
  `    independent of the bootstrap (n=${c.independentN}): ${c.independentN ? fmt(c.independent) : 'no trial to judge'}`,
  ...(Object.keys(c.advisories).length ? [`    advisory (not failing): ${fmt(c.advisories)}`] : []),
];

export const report = (cells: BaselineCell[]): string =>
  cells
    .map((c) => {
      const failing = Object.entries(c.buckets).sort((a, b) => b[1] - a[1]).map(([b, r]) => `${b} ${r.toFixed(2)}`).join('; ');
      const line = `${c.model.padEnd(12)} ${c.caseId.padEnd(22)} pass ${c.passRate.toFixed(2)} (n=${c.n})${failing ? `  fails: ${failing}` : ''}`;
      return c.cascade ? [line, ...cascadeLines(c.cascade)].join('\n') : line;
    })
    .join('\n');

export const withNotes = (n: GradeNotes): Pick<Trial, 'notes' | 'bootstrapNote'> => ({ notes: n.notes, ...(n.bootstrapNote ? { bootstrapNote: n.bootstrapNote } : {}) });

export async function runBaseline(
  config: Config,
  cases: EvalCase[],
  gradeFn: (c: EvalCase, text: string, notes?: GradeNotes) => Promise<string[]>,
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
      const notes: GradeNotes = { notes: [] };
      const failures = await gradeFn(c, r.text, notes);
      trials[i] = {
        model: spec.id, line: '(baseline)', variant: 'without', caseId: c.id, trial: n, pass: failures.length === 0, failures,
        inputTokens: r.inputTokens, outputTokens: r.outputTokens, estimated: r.estimated, output: r.text, ...(c.api ? withNotes(notes) : {}),
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
  const checks = new Set<string>(cases.some((c) => !c.api) ? ['compile', 'forbidden api', 'tests found', 'test failed'] : []);
  for (const c of cases) {
    for (const rule of c.tests?.rules ?? []) checks.add(`rule ${rule}`);
    if (c.tests?.mutants.length) checks.add('mutant survived');
    if (c.api) for (const b of apiBuckets(c.api.graded)) checks.add(b);
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
