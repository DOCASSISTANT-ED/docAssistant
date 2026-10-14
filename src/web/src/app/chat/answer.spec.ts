import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChatSource } from '../api/chat';
import { Answer } from './answer';
import { AnswerEntry } from './chat-store';
import { SourcePanelStore } from './source-panel-store';

function source(number: number, details: Partial<ChatSource> = {}): ChatSource {
  return {
    number,
    documentId: `d-${number}`,
    documentTitle: `Belge ${number}`,
    sectionPath: `Bölüm ${number}`,
    pageNumber: number,
    ...details,
  };
}

function entry(details: Partial<AnswerEntry> = {}): AnswerEntry {
  return {
    kind: 'answer',
    key: 'a-1',
    text: 'Yıllık izin 14 gündür [2]. Talep on gün önce yapılır [1].',
    sources: [source(1), source(2), source(3)],
    state: 'done',
    failure: null,
    ...details,
  };
}

// One answer on the chat screen, read the way a user reads it: the text, the numbers in
// it, the cards under it, and what it says about its own state.
describe('Answer', () => {
  let fixture: ComponentFixture<Answer>;
  let element: HTMLElement;

  // jsdom lays nothing out and has no scrollIntoView; pressing a number calls it to bring
  // the number's card into view. Tests that look at the scrolling replace it themselves.
  beforeEach(() => {
    Element.prototype.scrollIntoView = vi.fn();
  });

  afterEach(() => {
    delete (Element.prototype as Partial<Element>).scrollIntoView;
  });

  function show(answer: AnswerEntry, canRetry = false): void {
    fixture = TestBed.createComponent(Answer);
    fixture.componentRef.setInput('entry', answer);
    fixture.componentRef.setInput('canRetry', canRetry);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  }

  const textOf = (selector: string) => element.querySelector(selector)?.textContent?.trim() ?? null;
  const numbersInText = () =>
    Array.from(element.querySelectorAll('button.cite')).map((button) => button.textContent?.trim());
  const cards = () => Array.from(element.querySelectorAll<HTMLElement>('li.source'));
  const busy = () => element.querySelector('article')!.getAttribute('aria-busy');

  describe('while the search is running', () => {
    it('says that the documents are being searched', () => {
      show(entry({ state: 'waiting', text: '', sources: [] }));

      expect(textOf('[role="status"]')).toBe('Belgelerinizde aranıyor');
      expect(element.querySelector('.text')).toBeNull();
      expect(busy()).toBe('true');
    });
  });

  describe('while the answer is being written', () => {
    it('shows the text so far, with its source numbers already as buttons', () => {
      show(entry({ state: 'streaming', text: 'Yıllık izin 14 gündür [2]. Talep' }));

      expect(textOf('.text')).toContain('Yıllık izin 14 gündür');
      expect(numbersInText()).toEqual(['2']);
      expect(element.querySelector('.text')!.classList).toContain('streaming');
      expect(element.querySelector('[role="status"]')).toBeNull();
      expect(busy()).toBe('true');
    });

    // Which sources the answer points at is only known at its end.
    it('does not list sources yet', () => {
      show(entry({ state: 'streaming' }));

      expect(cards()).toEqual([]);
    });

    it('leaves a bracket that is still open as text', () => {
      show(entry({ state: 'streaming', text: 'Yıllık izin 14 gündür [2' }));

      expect(numbersInText()).toEqual([]);
      expect(textOf('.text')).toBe('Yıllık izin 14 gündür [2');
    });
  });

  describe('a complete answer', () => {
    it('shows the text with its source numbers', () => {
      show(entry());

      expect(numbersInText()).toEqual(['2', '1']);
      expect(element.querySelector('.text')!.classList).not.toContain('streaming');
      expect(busy()).toBe('false');
    });

    // The model is given eight sources and uses a few; listing the others next to the
    // answer would suggest they support it.
    it('lists only the sources the answer points at, by number', () => {
      show(entry());

      expect(cards().map((card) => card.querySelector('.source-title')?.textContent)).toEqual([
        'Belge 1',
        'Belge 2',
      ]);
      expect(cards().map((card) => card.querySelector('.source-number')?.textContent)).toEqual([
        '1',
        '2',
      ]);
    });

    it('lists no sources for an answer that points at none', () => {
      show(entry({ text: 'Yıllık izin 14 gündür.' }));

      expect(element.querySelector('.sources')).toBeNull();
    });

    // "[9]" is not one of the sources the model was given: nothing to open.
    it('leaves a number without a source as text', () => {
      show(entry({ text: 'Madde [9] uyarınca 14 gündür [1].' }));

      expect(numbersInText()).toEqual(['1']);
      expect(textOf('.text')).toContain('Madde [9] uyarınca');
    });

    it('names the document for a screen reader on each number', () => {
      show(entry({ text: 'Evet [2].' }));

      expect(element.querySelector('button.cite')!.getAttribute('aria-label')).toBe(
        'Kaynak 2: Belge 2',
      );
    });
  });

  // docs/decisions.md #22, #46: a DOCX has no pages, and text before the first heading
  // has no section.
  describe('where a source is from', () => {
    it.each([
      [{ sectionPath: 'Bölüm 2: Yıllık İzin', pageNumber: 3 }, 'Bölüm 2: Yıllık İzin · s. 3'],
      [{ sectionPath: 'Bölüm 2: Yıllık İzin', pageNumber: null }, 'Bölüm 2: Yıllık İzin'],
      [{ sectionPath: null, pageNumber: 3 }, 's. 3'],
      [{ sectionPath: null, pageNumber: null }, ''],
      [{ sectionPath: '', pageNumber: 1 }, 's. 1'],
    ])('reads %o as "%s"', (details, place) => {
      show(entry({ text: 'Evet [1].', sources: [source(1, details)] }));

      expect(textOf('.source-place')).toBe(place);
    });
  });

  // The one case where the user has to do something else: ask in other words, or upload
  // the document.
  describe('"not in the documents"', () => {
    it('is shown as a notice with a suggestion, not as an answer', () => {
      show(entry({ text: 'Bu bilgi belgelerde bulunamadı.', sources: [source(1)] }));

      expect(textOf('.nothing-title')).toBe('Bu bilgi belgelerde bulunamadı.');
      expect(textOf('.nothing-hint')).toContain('başka sözcüklerle');
      expect(element.querySelector('.text')).toBeNull();
      expect(element.querySelector('.sources')).toBeNull();
    });

    it('is recognized with white space around it', () => {
      show(entry({ text: '  Bu bilgi belgelerde bulunamadı.\n' }));

      expect(element.querySelector('.nothing')).not.toBeNull();
    });

    // While it is arriving, the sentence may still be the start of a longer answer.
    it('is ordinary text until the answer is complete', () => {
      show(entry({ state: 'streaming', text: 'Bu bilgi belgelerde bulunamadı.' }));

      expect(element.querySelector('.nothing')).toBeNull();
      expect(textOf('.text')).toBe('Bu bilgi belgelerde bulunamadı.');
    });

    it('is ordinary text when the answer says more than that', () => {
      show(entry({ text: 'Bu bilgi belgelerde bulunamadı. Ancak izin 14 gündür [1].' }));

      expect(element.querySelector('.nothing')).toBeNull();
      expect(numbersInText()).toEqual(['1']);
    });
  });

  describe('an answer the user stopped', () => {
    it('keeps what was written, lists its sources and says it was stopped', () => {
      show(entry({ state: 'stopped', text: 'Yıllık izin 14 gündür [2]. Talep' }));

      expect(textOf('.text')).toContain('Yıllık izin 14 gündür');
      expect(cards()).toHaveLength(1);
      expect(textOf('.note')).toBe('Cevap durduruldu.');
      expect(busy()).toBe('false');
    });

    it('says so even when nothing had been written yet', () => {
      show(entry({ state: 'stopped', text: '', sources: [] }));

      expect(textOf('.note')).toBe('Cevap durduruldu.');
      expect(element.querySelector('.text')).toBeNull();
    });
  });

  describe('an answer that failed', () => {
    it('says why, as an alert', () => {
      show(entry({ state: 'failed', text: '', failure: 'Cevap üretilemedi. Yeniden deneyin.' }));

      expect(textOf('[role="alert"]')).toBe('Cevap üretilemedi. Yeniden deneyin.');
      expect(busy()).toBe('false');
    });

    // The server saved nothing of it, so its numbers have no cards to go with.
    it('lists no sources', () => {
      show(entry({ state: 'failed', text: 'Yarım cevap [1]', failure: 'Bağlantı kesildi.' }));

      expect(element.querySelector('.sources')).toBeNull();
    });
  });

  describe('trying again', () => {
    const retryButton = () => element.querySelector<HTMLButtonElement>('button.retry');

    it.each(['failed', 'stopped'] as const)(
      'is offered for the last answer when it %s',
      (state) => {
        show(entry({ state, failure: state === 'failed' ? 'Hata.' : null }), true);

        expect(retryButton()?.textContent?.trim()).toBe('Yeniden dene');
      },
    );

    // Only the conversation's last question can be asked again.
    it.each(['failed', 'stopped'] as const)(
      'is not offered for an earlier answer that %s',
      (state) => {
        show(entry({ state, failure: state === 'failed' ? 'Hata.' : null }), false);

        expect(retryButton()).toBeNull();
      },
    );

    it.each(['waiting', 'streaming', 'done'] as const)(
      'is not offered for an answer that is %s',
      (state) => {
        show(entry({ state }), true);

        expect(retryButton()).toBeNull();
      },
    );

    it('tells the screen when the button is pressed', () => {
      show(entry({ state: 'failed', failure: 'Hata.' }), true);
      const asked = vi.fn();
      fixture.componentInstance.retry.subscribe(asked);

      retryButton()!.click();

      expect(asked).toHaveBeenCalledOnce();
    });
  });

  // A number in the text and its card light up together, so it is easy to see which
  // card a number means.
  describe('pointing at a source', () => {
    const number = (value: string) =>
      Array.from(element.querySelectorAll<HTMLButtonElement>('button.cite')).find(
        (button) => button.textContent?.trim() === value,
      )!;
    const card = (value: string) =>
      cards().find((item) => item.querySelector('.source-number')?.textContent === value)!;

    it.each([
      ['the pointer', 'mouseenter', 'mouseleave'],
      ['the keyboard', 'focus', 'blur'],
    ])('lights the number and its card with %s, and only those', (_how, on, off) => {
      show(entry());

      number('2').dispatchEvent(new Event(on));
      fixture.detectChanges();

      expect(number('2').classList).toContain('lit');
      expect(card('2').classList).toContain('lit');
      expect(number('1').classList).not.toContain('lit');
      expect(card('1').classList).not.toContain('lit');

      number('2').dispatchEvent(new Event(off));
      fixture.detectChanges();

      expect(number('2').classList).not.toContain('lit');
      expect(card('2').classList).not.toContain('lit');
    });

    it('lights the number in the text from its card', () => {
      show(entry());

      card('1').dispatchEvent(new Event('mouseenter'));
      fixture.detectChanges();

      expect(number('1').classList).toContain('lit');
    });

    it('brings the card into view when its number is pressed', () => {
      show(entry());
      document.body.appendChild(element);
      const scrolled = vi.fn();
      card('2').scrollIntoView = scrolled;

      number('2').click();

      expect(scrolled).toHaveBeenCalledOnce();
      element.remove();
    });

    // Numbers repeat from answer to answer; a press must find the card of its own answer.
    it('gives the cards of different answers different ids', () => {
      show(entry({ key: 'a-1' }));
      const first = card('1').id;

      show(entry({ key: 'a-2' }));

      expect(card('1').id).not.toBe(first);
      expect(card('1').id).not.toBe('');
    });
  });

  // docs/decisions.md #63: a number in the text opens its source beside the chat, like
  // its card does.
  describe('opening a source', () => {
    it('opens the source of a number in the panel, with this answer', () => {
      show(entry({ key: 'a-7' }));
      const panel = TestBed.inject(SourcePanelStore);

      element.querySelectorAll<HTMLButtonElement>('button.cite')[0].click();

      expect(panel.opened()).toEqual({ answerKey: 'a-7', source: source(2) });
    });

    it('gives the focus back to the number when the panel closes', () => {
      show(entry());
      document.body.appendChild(element);
      const panel = TestBed.inject(SourcePanelStore);
      const number = element.querySelector<HTMLButtonElement>('button.cite')!;

      number.click();
      number.blur();
      panel.close();

      expect(document.activeElement).toBe(number);
      element.remove();
    });

    it('shows each listed source as a card of this answer', () => {
      show(entry({ key: 'a-7' }));

      const panel = TestBed.inject(SourcePanelStore);
      element.querySelector<HTMLButtonElement>('app-source-card button')!.click();

      expect(panel.opened()).toEqual({ answerKey: 'a-7', source: source(1) });
    });
  });
});
