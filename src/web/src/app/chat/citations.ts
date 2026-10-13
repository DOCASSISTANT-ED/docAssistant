import { Pipe, PipeTransform } from '@angular/core';
import { ChatSource } from '../api/chat';

// An answer cut into the pieces the screen shows differently: plain text, and the
// numbers that point at sources.
export type AnswerPiece = { kind: 'text'; text: string } | { kind: 'citation'; source: ChatSource };

// "[3]", and also "[1, 3]" as models sometimes write two sources at once.
const citationPattern = /\[(\d+(?:\s*,\s*\d+)*)\]/g;

// Splits an answer at its source numbers (docs/decisions.md #56: "[3]" in the text means
// the source whose number is 3).
//
// A number in brackets becomes a citation only if a source with that number was given
// to the model. Anything else stays text exactly as written: a bracketed number may be
// part of the document's own wording ("Madde [12]"), and while an answer is still
// arriving its last "[1" has no closing bracket yet.
export function splitCitations(text: string, sources: readonly ChatSource[]): AnswerPiece[] {
  const byNumber = new Map(sources.map((source) => [source.number, source]));
  const pieces: AnswerPiece[] = [];
  let textStart = 0;

  for (const match of text.matchAll(citationPattern)) {
    const cited = match[1].split(',').map((part) => byNumber.get(Number(part.trim())));

    // "[1, 9]" with an unknown 9 is left alone as a whole rather than half converted.
    if (cited.some((source) => source === undefined)) {
      continue;
    }

    if (match.index > textStart) {
      pieces.push({ kind: 'text', text: text.slice(textStart, match.index) });
    }

    for (const source of cited) {
      pieces.push({ kind: 'citation', source: source! });
    }

    textStart = match.index + match[0].length;
  }

  if (textStart < text.length) {
    pieces.push({ kind: 'text', text: text.slice(textStart) });
  }

  return pieces;
}

// The sources an answer actually points at, in the order of their numbers. The model is
// given more sources than it uses (8, #48); showing those it did not use next to the
// answer would suggest they support it.
export function citedSources(text: string, sources: readonly ChatSource[]): ChatSource[] {
  const cited = new Set(
    splitCitations(text, sources)
      .filter((piece) => piece.kind === 'citation')
      .map((piece) => piece.source.number),
  );

  return sources.filter((source) => cited.has(source.number));
}

// For templates. Pure: Angular runs it again only when the text or the sources change,
// not on every repaint of the screen.
@Pipe({ name: 'citations' })
export class CitationsPipe implements PipeTransform {
  transform(text: string, sources: readonly ChatSource[]): AnswerPiece[] {
    return splitCitations(text, sources);
  }
}

@Pipe({ name: 'citedSources' })
export class CitedSourcesPipe implements PipeTransform {
  transform(text: string, sources: readonly ChatSource[]): ChatSource[] {
    return citedSources(text, sources);
  }
}
