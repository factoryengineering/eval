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

export const hashSkill = (source: string) => createHash('sha256').update(source).digest('hex').slice(0, 12);
