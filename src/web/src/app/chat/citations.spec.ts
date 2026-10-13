import { ChatSource } from '../api/chat';
import { CitationsPipe, CitedSourcesPipe, citedSources, splitCitations } from './citations';

function source(number: number): ChatSource {
  return {
    number,
    documentId: `d-${number}`,
    documentTitle: `Belge ${number}`,
    sectionPath: null,
    pageNumber: null,
  };
}

// The model was given these three.
const sources = [source(1), source(2), source(3)];

const text = (value: string) => ({ kind: 'text' as const, text: value });
const citation = (number: number) => ({ kind: 'citation' as const, source: source(number) });

// docs/decisions.md #56: "[3]" in an answer means the source whose number is 3.
describe('splitCitations', () => {
  it('leaves an answer without source numbers as one piece of text', () => {
    expect(splitCitations('Yıllık izin 14 gündür.', sources)).toEqual([
      text('Yıllık izin 14 gündür.'),
    ]);
  });

  it('gives nothing for an answer that has not started', () => {
    expect(splitCitations('', sources)).toEqual([]);
  });

  it('cuts the answer at a source number', () => {
    expect(
      splitCitations('Yıllık izin 14 gündür [2]. Talep on gün önce yapılır.', sources),
    ).toEqual([text('Yıllık izin 14 gündür '), citation(2), text('. Talep on gün önce yapılır.')]);
  });

  it('reads a number at the very start and at the very end', () => {
    expect(splitCitations('[1] ve [3]', sources)).toEqual([citation(1), text(' ve '), citation(3)]);
  });

  it('reads numbers that follow each other', () => {
    expect(splitCitations('Evet [1][3].', sources)).toEqual([
      text('Evet '),
      citation(1),
      citation(3),
      text('.'),
    ]);
  });

  // Not the form the prompt asks for, but models write it.
  it.each(['[1, 3]', '[1,3]', '[1 , 3]'])('reads two numbers in one bracket: %s', (written) => {
    expect(splitCitations(`Evet ${written}.`, sources)).toEqual([
      text('Evet '),
      citation(1),
      citation(3),
      text('.'),
    ]);
  });

  it('keeps the same source apart each time it is cited', () => {
    expect(splitCitations('A [1]. B [1].', sources)).toEqual([
      text('A '),
      citation(1),
      text('. B '),
      citation(1),
      text('.'),
    ]);
  });

  describe('what looks like a source number but is not', () => {
    // The document's own wording, or a number the model made up: no source to open.
    it.each(['Madde [12] uyarınca.', 'Bkz. [0].', 'Sonuç [9].'])(
      'leaves a number without a source as text: %s',
      (answer) => {
        expect(splitCitations(answer, sources)).toEqual([text(answer)]);
      },
    );

    // Half converting it would show "[" and "9]" around a clickable 1.
    it('leaves a bracket alone as a whole when one of its numbers has no source', () => {
      expect(splitCitations('Evet [1, 9].', sources)).toEqual([text('Evet [1, 9].')]);
    });

    it.each(['[a]', '[2024 yılı]', '[]', '[ ]', '[1.5]', '[-1]'])(
      'leaves %s as text',
      (written) => {
        expect(splitCitations(`Bkz. ${written}.`, sources)).toEqual([text(`Bkz. ${written}.`)]);
      },
    );

    // While an answer is arriving its last bracket may not be closed yet.
    it.each(['Yıllık izin 14 gündür [', 'Yıllık izin 14 gündür [2', 'Yıllık izin 14 gündür [2,'])(
      'leaves a bracket that is still open as text: %s',
      (answer) => {
        expect(splitCitations(answer, sources)).toEqual([text(answer)]);
      },
    );

    it('still reads the numbers around one it leaves alone', () => {
      expect(splitCitations('A [1], B [9], C [2].', sources)).toEqual([
        text('A '),
        citation(1),
        text(', B [9], C '),
        citation(2),
        text('.'),
      ]);
    });
  });

  it('finds no source numbers when the answer was given none', () => {
    expect(splitCitations('Bu bilgi belgelerde bulunamadı [1].', [])).toEqual([
      text('Bu bilgi belgelerde bulunamadı [1].'),
    ]);
  });

  // Nothing of the answer may get lost on the way to the screen.
  it.each([
    'Düz metin.',
    'A [1]. B [1, 3]. C [9]. D [2',
    '[1][2][3]',
    'Madde [12] ve [3], sonra [a].',
  ])('keeps every character of the answer: %s', (answer) => {
    const rebuilt = splitCitations(answer, sources)
      .map((piece) => (piece.kind === 'text' ? piece.text : ''))
      .join('');
    const withoutCitations = answer.replace(/\[(1|2|3)(\s*,\s*(1|2|3))*\]/g, '');

    expect(rebuilt).toBe(withoutCitations);
  });
});

// The model is given more sources than it uses; only those it points at are listed.
describe('citedSources', () => {
  it('lists the sources the answer points at', () => {
    expect(citedSources('Evet [3].', sources)).toEqual([source(3)]);
  });

  it('lists them in the order of their numbers, each once', () => {
    expect(citedSources('A [3]. B [1]. C [3, 1].', sources)).toEqual([source(1), source(3)]);
  });

  it('lists none for an answer without source numbers', () => {
    expect(citedSources('Bu bilgi belgelerde bulunamadı.', sources)).toEqual([]);
  });

  it('does not list a source for a number that is not one', () => {
    expect(citedSources('Madde [12], bkz. [1, 9].', sources)).toEqual([]);
  });
});

describe('the pipes for templates', () => {
  it('give the same as the functions', () => {
    const answer = 'A [1]. B [3].';

    expect(new CitationsPipe().transform(answer, sources)).toEqual(splitCitations(answer, sources));
    expect(new CitedSourcesPipe().transform(answer, sources)).toEqual(
      citedSources(answer, sources),
    );
  });
});
