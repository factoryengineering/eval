import { grade, verdict } from './grade.js';
import { complete as realComplete } from './providers.js';
import { hashSkill, parseSkill, renderSkill } from './skill.js';
import type { Completion, Config, EvalCase, LineResult, ModelSpec, RunSummary, Trial } from './types.js';

type Complete = (spec: ModelSpec, system: string, prompt: string) => Promise<Completion>;

const rate = (xs: Trial[]) => (xs.length ? xs.filter((t) => t.pass).length / xs.length : 0);

export async function runEval(
  config: Config,
  skillSource: string,
  cases: EvalCase[],
  complete: Complete = realComplete,
  onTrial: (t: Trial) => void = () => {},
  gradeFn: (c: EvalCase, text: string) => Promise<string[]> = async (c, text) => grade(c, text),
): Promise<{ summary: RunSummary; trials: Trial[] }> {
  const { lines } = parseSkill(skillSource);
  const ids = new Set(lines.map((l) => l.id));
  for (const c of cases) for (const t of c.targets) if (!ids.has(t)) throw new Error(`case ${c.id} targets unknown line ${t}`);

  const trials: Trial[] = [];
  const tokens: RunSummary['tokens'] = {};
  const memo = new Map<string, Promise<Completion>>();
  const call = (spec: ModelSpec, c: EvalCase, omit: string | undefined, n: number) => {
    const key = `${spec.id}|${c.id}|${omit ?? '*'}|${n}`;
    let p = memo.get(key);
    if (!p) {
      p = complete(spec, renderSkill(skillSource, omit), c.prompt).then((r) => {
        const t = (tokens[spec.id] ??= { input: 0, output: 0, estimated: false, calls: 0 });
        t.input += r.inputTokens;
        t.output += r.outputTokens;
        t.estimated ||= r.estimated;
        t.calls++;
        return r;
      });
      memo.set(key, p);
    }
    return p;
  };

  for (const spec of config.models) {
    for (const line of lines.filter((l) => !config.lines || config.lines.includes(l.id))) {
      for (const c of cases.filter((x) => x.targets.includes(line.id))) {
        for (let n = 0; n < config.trials; n++) {
          for (const variant of ['without', 'with'] as const) {
            const r = await call(spec, c, variant === 'without' ? line.id : undefined, n);
            const failures = await gradeFn(c, r.text);
            const t: Trial = {
              model: spec.id, line: line.id, variant, caseId: c.id, trial: n, pass: failures.length === 0, failures,
              inputTokens: r.inputTokens, outputTokens: r.outputTokens, estimated: r.estimated, output: r.text,
            };
            trials.push(t);
            onTrial(t);
          }
        }
      }
    }
  }

  const guards: NonNullable<RunSummary['guards']> = [];
  if (!config.lines)
    for (const spec of config.models)
      for (const c of cases.filter((x) => x.guard)) {
        const mine: Trial[] = [];
        for (let n = 0; n < config.trials; n++) {
          const r = await call(spec, c, undefined, n);
          const failures = await gradeFn(c, r.text);
          const t: Trial = {
            model: spec.id, line: '(guard)', variant: 'with', caseId: c.id, trial: n, pass: failures.length === 0, failures,
            inputTokens: r.inputTokens, outputTokens: r.outputTokens, estimated: r.estimated, output: r.text,
          };
          mine.push(t);
          trials.push(t);
          onTrial(t);
        }
        guards.push({ model: spec.id, caseId: c.id, rate: rate(mine), n: mine.length });
      }

  const results: LineResult[] = [];
  for (const spec of config.models) {
    for (const line of lines) {
      const mine = trials.filter((t) => t.model === spec.id && t.line === line.id);
      if (!mine.length) continue;
      const withRate = rate(mine.filter((t) => t.variant === 'with'));
      const withoutRate = rate(mine.filter((t) => t.variant === 'without'));
      results.push({
        model: spec.id, line: line.id, withRate, withoutRate, delta: withRate - withoutRate,
        verdict: verdict(withRate, withoutRate, config.minDelta),
      });
    }
  }

  const at = new Date().toISOString();
  const summary: RunSummary = {
    runId: at.replace(/[:.]/g, '-'), at, skillHash: hashSkill(renderSkill(skillSource)), trials: config.trials, lines: results, guards, tokens,
  };
  return { summary, trials };
}
