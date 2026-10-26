import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { complete as realComplete } from './providers.js';
import type { Completion, ModelSpec } from './types.js';

type Complete = (spec: ModelSpec, system: string, prompt: string) => Promise<Completion>;
export type Verdicts = Record<string, boolean>;

export const RUBRIC = [
  'You label C# identifiers. An identifier is meaningful when it is made of whole ordinary English words',
  '(post, popularPosts, totalViews, cancellationToken, id, item, result, query).',
  'It is NOT meaningful when it is a single letter, a cryptic abbreviation, or letters followed by a digit',
  '(p, g, x1, db, ctx, qry, res, tmp, cnt, dto, conn, cfg, val, itm, item2).',
  'Reply with only a JSON object that maps every identifier you were given to true (meaningful) or false (not meaningful).',
].join(' ');

export const rubricHash = () => createHash('sha256').update(RUBRIC).digest('hex').slice(0, 8);

export function parseVerdicts(text: string, wanted: string[]): Verdicts {
  const start = text.indexOf('{');
  const end = text.lastIndexOf('}');
  if (start < 0 || end < start) return {};
  try {
    const raw = JSON.parse(text.slice(start, end + 1)) as Record<string, unknown>;
    return Object.fromEntries(wanted.filter((id) => typeof raw[id] === 'boolean').map((id) => [id, raw[id] as boolean]));
  } catch {
    return {};
  }
}

export async function judgeIdentifiers(
  ids: string[],
  known: Verdicts,
  spec: ModelSpec,
  complete: Complete = realComplete,
): Promise<Verdicts> {
  const verdicts: Verdicts = { ...known };
  for (let attempt = 0; attempt < 2; attempt++) {
    const todo = [...new Set(ids)].filter((id) => verdicts[id] === undefined);
    for (let i = 0; i < todo.length; i += 40) {
      const batch = todo.slice(i, i + 40);
      const r = await complete(spec, RUBRIC, `Identifiers: ${JSON.stringify(batch)}`);
      Object.assign(verdicts, parseVerdicts(r.text, batch));
    }
  }
  return verdicts;
}

export const offenders = (ids: string[], verdicts: Verdicts) =>
  [...new Set(ids)].filter((id) => id.length < 2 || verdicts[id] !== true);

export function agreement(labels: Verdicts, verdicts: Verdicts) {
  const ids = Object.keys(labels);
  const disagreements = ids.filter((id) => verdicts[id] !== labels[id]);
  return { n: ids.length, agree: ids.length - disagreements.length, disagreements };
}

interface Lexicon {
  rubric: string;
  judge: string;
  verdicts: Verdicts;
}

export class NameJudge {
  private lexicon: Lexicon;
  private dirty = false;

  private constructor(private file: string, private spec: ModelSpec, lexicon: Lexicon, private complete: Complete) {
    this.lexicon = lexicon;
  }

  static async open(file: string, spec: ModelSpec, complete: Complete = realComplete): Promise<NameJudge> {
    const fresh: Lexicon = { rubric: rubricHash(), judge: spec.model, verdicts: {} };
    try {
      const saved = JSON.parse(await readFile(file, 'utf8')) as Lexicon;
      if (saved.rubric === fresh.rubric && saved.judge === fresh.judge) return new NameJudge(file, spec, saved, complete);
    } catch {}
    return new NameJudge(file, spec, fresh, complete);
  }

  async bad(ids: string[]): Promise<string[]> {
    const before = Object.keys(this.lexicon.verdicts).length;
    this.lexicon.verdicts = await judgeIdentifiers(ids.filter((id) => id.length >= 2), this.lexicon.verdicts, this.spec, this.complete);
    this.dirty ||= Object.keys(this.lexicon.verdicts).length !== before;
    return offenders(ids, this.lexicon.verdicts);
  }

  async save() {
    if (this.dirty) await writeFile(this.file, JSON.stringify(this.lexicon, null, 2) + '\n');
    this.dirty = false;
  }
}
