import { HighlightRange } from './models';

export interface Segment { text: string; mark: boolean; }

/**
 * Découpe l'extrait en morceaux normaux et surlignés à partir des positions renvoyées par l'API.
 * On produit des morceaux de texte (rendus par interpolation) plutôt que du HTML : aucun risque d'injection.
 */
export function toSegments(text: string, highlights: HighlightRange[]): Segment[] {
  const ranges = [...highlights]
    .filter(h => h.start >= 0 && h.length > 0 && h.start + h.length <= text.length)
    .sort((a, b) => a.start - b.start);
  const segments: Segment[] = [];
  let pos = 0;
  for (const r of ranges) {
    if (r.start < pos) continue; // chevauchement : on garde le premier
    if (r.start > pos) segments.push({ text: text.slice(pos, r.start), mark: false });
    segments.push({ text: text.slice(r.start, r.start + r.length), mark: true });
    pos = r.start + r.length;
  }
  if (pos < text.length) segments.push({ text: text.slice(pos), mark: false });
  return segments;
}
