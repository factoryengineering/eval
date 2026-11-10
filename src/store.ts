import { appendFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { hashSkill } from './skill.js';
import type { RunSummary, Trial } from './types.js';

const EVALS = 'logs/evals.jsonl';
const CHANGES = 'logs/changes.jsonl';

async function append(home: string, file: string, value: unknown) {
  await mkdir(`${home}/logs`, { recursive: true });
  await appendFile(`${home}/${file}`, JSON.stringify(value) + '\n');
}

export async function readJsonl<T>(file: string): Promise<T[]> {
  try {
    return (await readFile(file, 'utf8')).split('\n').filter(Boolean).map((l) => JSON.parse(l) as T);
  } catch {
    return [];
  }
}

export async function saveRun(summary: RunSummary, trials: Trial[], home = '.') {
  await mkdir(`${home}/runs`, { recursive: true });
  await writeFile(`${home}/runs/${summary.runId}.json`, JSON.stringify({ summary, trials }, null, 2));
  await append(home, EVALS, summary);
}

export async function logChange(note: string, skillSource: string, home = '.') {
  const hash = hashSkill(skillSource);
  await mkdir(`${home}/logs/snapshots`, { recursive: true });
  await writeFile(`${home}/logs/snapshots/${hash}.md`, skillSource);
  await append(home, CHANGES, { at: new Date().toISOString(), skillHash: hash, note });
  return hash;
}

export const readRuns = (home = '.') => readJsonl<RunSummary>(`${home}/${EVALS}`);

export function compare(prev: RunSummary, cur: RunSummary): string {
  const out = [`${prev.skillHash} (${prev.at}) -> ${cur.skillHash} (${cur.at})`];
  for (const [model, c] of Object.entries(cur.tokens)) {
    const p = prev.tokens[model];
    if (!p) {
      out.push(`${model}: no previous run`);
      continue;
    }
    const d = (a: number, b: number) => `${b - a >= 0 ? '+' : ''}${b - a} (${a} -> ${b})`;
    out.push(`${model}: input ${d(p.input, c.input)}, output ${d(p.output, c.output)}, calls ${d(p.calls, c.calls)}${c.estimated ? ' [estimated]' : ''}`);
  }
  const key = (l: { model: string; line: string }) => `${l.model}/${l.line}`;
  const before = new Map(prev.lines.map((l) => [key(l), l]));
  for (const l of cur.lines) {
    const b = before.get(key(l));
    if (!b) out.push(`${key(l)}: new line, delta ${l.delta.toFixed(2)} ${l.verdict}`);
    else if (b.verdict !== l.verdict || b.delta !== l.delta)
      out.push(`${key(l)}: delta ${b.delta.toFixed(2)} -> ${l.delta.toFixed(2)} (${b.verdict} -> ${l.verdict})`);
  }
  for (const l of prev.lines) if (!cur.lines.some((x) => key(x) === key(l))) out.push(`${key(l)}: removed`);
  return out.join('\n');
}

export function table(s: RunSummary): string {
  const rows = s.lines.map((l) =>
    `${l.model.padEnd(14)} ${l.line.padEnd(18)} without ${l.withoutRate.toFixed(2)}  with ${l.withRate.toFixed(2)}  delta ${l.delta.toFixed(2).padStart(5)}  ${l.verdict}`);
  const toks = Object.entries(s.tokens).map(([m, t]) => `${m.padEnd(14)} in ${t.input}  out ${t.output}  calls ${t.calls}${t.estimated ? '  (estimated)' : ''}`);
  const guards = (s.guards ?? []).map((g) => `${g.model.padEnd(14)} guard ${g.caseId.padEnd(18)} with ${g.rate.toFixed(2)} (n=${g.n})`);
  return [...rows, '', ...guards, ...(guards.length ? [''] : []), ...toks].join('\n');
}
