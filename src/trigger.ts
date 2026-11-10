import { complete as realComplete } from './providers.js';
import { hashSkill } from './skill.js';
import type { Completion, ModelSpec } from './types.js';

export interface TriggerCase {
  id: string;
  prompt: string;
  expect: boolean;
  targets: string[];
  split?: 'tune' | 'holdout';
}

export interface TriggerSpec {
  skillName: string;
  distractors: { name: string; description: string }[];
  clauses: { id: string; text: string }[];
  baseline: string;
  cases: TriggerCase[];
}

export interface DescriptionVariant {
  id: string;
  description: string;
}

export interface TriggerTrial {
  model: string;
  variant: string;
  caseId: string;
  trial: number;
  expect: boolean;
  selected: string[] | null;
  pass: boolean;
  output: string;
}

type Complete = (spec: ModelSpec, system: string, prompt: string) => Promise<Completion>;

const join = (texts: string[]) => texts.join(' ');

export function variantsOf(spec: TriggerSpec, current?: string): DescriptionVariant[] {
  const all = spec.clauses.map((c) => c.text);
  const out: DescriptionVariant[] = [
    { id: 'none', description: '' },
    { id: 'baseline', description: spec.baseline },
    { id: 'full', description: join(all) },
    ...spec.clauses.map((c) => ({ id: `no-${c.id}`, description: join(spec.clauses.filter((x) => x !== c).map((x) => x.text)) })),
  ];
  if (current !== undefined && !out.some((v) => v.description === current)) out.push({ id: 'current', description: current });
  return out;
}

export function buildSystem(spec: TriggerSpec, description: string): string {
  const others = spec.distractors.map((d) => `- **${d.name}**: ${d.description}`);
  const mine = `- **${spec.skillName}**:${description ? ` ${description}` : ''}`;
  others.splice(Math.floor(others.length / 2), 0, mine);
  return [
    'You are a coding agent. These skills are available. Invoke a skill only when the user request clearly matches its description.',
    '',
    ...others,
  ].join('\n');
}

export const ask = (request: string) =>
  `${request}\n\nReply with only a JSON array of the skill names you would invoke, such as ["name"], or [] if none.`;

export function parseSelection(text: string): string[] | null {
  for (const m of text.matchAll(/\[[^\[\]]*\]/g)) {
    try {
      const v: unknown = JSON.parse(m[0]);
      if (Array.isArray(v) && v.every((x) => typeof x === 'string')) return v as string[];
    } catch {}
  }
  return null;
}

export const gradeTrigger = (name: string, expect: boolean, selected: string[] | null) =>
  selected !== null && selected.includes(name) === expect;

export function descriptionOf(skillSource: string): string {
  const m = /^---\n([\s\S]*?)\n---/.exec(skillSource);
  const d = m && /^description:\s*(.*)$/m.exec(m[1]!);
  if (!d) throw new Error('SKILL.md has no frontmatter description');
  return d[1]!.trim();
}

export interface TriggerCell {
  model: string;
  variant: string;
  scope: string;
  rate: number;
  n: number;
}

export function rates(trials: TriggerTrial[], spec: TriggerSpec): TriggerCell[] {
  const cells: TriggerCell[] = [];
  const models = [...new Set(trials.map((t) => t.model))];
  const variants = [...new Set(trials.map((t) => t.variant))];
  const byCase = new Map(spec.cases.map((c) => [c.id, c]));
  const scopes: [string, (c: TriggerCase) => boolean][] = [
    ['all', () => true],
    ['positives', (c) => c.expect],
    ['negatives', (c) => !c.expect],
    ...spec.clauses.map((cl): [string, (c: TriggerCase) => boolean] => [cl.id, (c) => c.targets.includes(cl.id)]),
  ];
  for (const model of models)
    for (const variant of variants)
      for (const [scope, keep] of scopes) {
        const mine = trials.filter((t) => t.model === model && t.variant === variant && keep(byCase.get(t.caseId)!));
        if (mine.length) cells.push({ model, variant, scope, rate: mine.filter((t) => t.pass).length / mine.length, n: mine.length });
      }
  return cells;
}

export interface ClauseVerdict {
  model: string;
  clause: string;
  withRate: number;
  withoutRate: number;
  delta: number;
  verdict: 'load-bearing' | 'not-load-bearing';
}

export function clauseVerdicts(cells: TriggerCell[], spec: TriggerSpec, minDelta: number): ClauseVerdict[] {
  const get = (model: string, variant: string, scope: string) => cells.find((c) => c.model === model && c.variant === variant && c.scope === scope)?.rate;
  const out: ClauseVerdict[] = [];
  for (const model of new Set(cells.map((c) => c.model)))
    for (const cl of spec.clauses) {
      const w = get(model, 'full', cl.id);
      const wo = get(model, `no-${cl.id}`, cl.id);
      if (w === undefined || wo === undefined) continue;
      out.push({ model, clause: cl.id, withRate: w, withoutRate: wo, delta: w - wo, verdict: w - wo >= minDelta ? 'load-bearing' : 'not-load-bearing' });
    }
  return out;
}

export async function runTrigger(
  spec: TriggerSpec,
  models: ModelSpec[],
  trials: number,
  current?: string,
  complete: Complete = realComplete,
  concurrency = 4,
  onTrial: (t: TriggerTrial) => void = () => {},
  options: { split?: 'tune' | 'holdout'; variantIds?: string[] } = {},
): Promise<{ trials: TriggerTrial[]; variants: DescriptionVariant[] }> {
  const variants = variantsOf(spec, current).filter((v) => !options.variantIds || options.variantIds.includes(v.id));
  const cases = spec.cases.filter((c) => !options.split || c.split === options.split);
  const jobs: (() => Promise<void>)[] = [];
  const results: TriggerTrial[] = [];
  for (const model of models)
    for (const v of variants)
      for (const c of cases)
        for (let n = 0; n < trials; n++)
          jobs.push(async () => {
            const r = await complete(model, buildSystem(spec, v.description), ask(c.prompt));
            const selected = parseSelection(r.text);
            const t: TriggerTrial = {
              model: model.id, variant: v.id, caseId: c.id, trial: n, expect: c.expect, selected,
              pass: gradeTrigger(spec.skillName, c.expect, selected), output: r.text,
            };
            results.push(t);
            onTrial(t);
          });
  const queue = [...jobs];
  await Promise.all(Array.from({ length: concurrency }, async () => { for (let j = queue.shift(); j; j = queue.shift()) await j(); }));
  return { trials: results, variants };
}

export const triggerHash = (variants: DescriptionVariant[]) => hashSkill(JSON.stringify(variants));

export const holdoutSpent = (entries: { split?: string; description?: string }[], description: string): boolean =>
  entries.some((e) => e.split === 'holdout' && e.description === description);

export function splitProblems(spec: TriggerSpec): string[] {
  const problems: string[] = [];
  for (const c of spec.cases) if (!c.split) problems.push(`case ${c.id} has no split`);
  for (const split of ['tune', 'holdout'] as const) {
    const mine = spec.cases.filter((c) => c.split === split);
    if (!mine.some((c) => c.expect)) problems.push(`${split} has no positives`);
    if (!mine.some((c) => !c.expect)) problems.push(`${split} has no negatives`);
  }
  const prompts = spec.cases.map((c) => c.prompt);
  if (new Set(prompts).size !== prompts.length) problems.push('duplicate prompts');
  return problems;
}
