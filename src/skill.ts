import { createHash } from 'node:crypto';

const LINE = /^- \[([a-z0-9-]+)\] (.+)$/;

export function parseSkill(source: string): { header: string[]; lines: SkillLineWithIndex[] } {
  const header: string[] = [];
  const lines: SkillLineWithIndex[] = [];
  source.split('\n').forEach((raw, index) => {
    const m = LINE.exec(raw);
    if (m) lines.push({ id: m[1]!, text: m[2]!, index });
    else header.push(raw);
  });
  return { header, lines };
}

export interface SkillLineWithIndex {
  id: string;
  text: string;
  index: number;
}

export function renderSkill(source: string, omit?: string): string {
  return source
    .split('\n')
    .filter((raw) => {
      const m = LINE.exec(raw);
      return !(m && m[1] === omit);
    })
    .map((raw) => {
      const m = LINE.exec(raw);
      return m ? `- ${m[2]}` : raw;
    })
    .join('\n');
}

export function annotate(clean: string, ids: Record<string, string>): string {
  const byText = new Map(Object.entries(ids).map(([id, text]) => [text, id]));
  const seen = new Set<string>();
  const out = clean.split('\n').map((raw) => {
    const m = /^- (.+)$/.exec(raw);
    if (!m) return raw;
    const id = byText.get(m[1]!);
    if (!id) throw new Error(`no id for skill line: ${m[1]}`);
    seen.add(id);
    return `- [${id}] ${m[1]}`;
  });
  for (const id of Object.keys(ids)) if (!seen.has(id)) throw new Error(`id ${id} not found in skill`);
  return out.join('\n');
}

export const hashSkill = (source: string) => createHash('sha256').update(source).digest('hex').slice(0, 12);
