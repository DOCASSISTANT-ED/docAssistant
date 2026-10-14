import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChatSource } from '../api/chat';
import { SourceCard } from './source-card';
import { SourcePanelStore } from './source-panel-store';

function source(details: Partial<ChatSource> = {}): ChatSource {
  return {
    number: 2,
    documentId: 'd-2',
    documentTitle: 'İK Yönetmeliği',
    sectionPath: 'Bölüm 2: Yıllık İzin',
    pageNumber: 3,
    ...details,
  };
}

// One source under an answer. A press shows it in the panel beside the chat
// (docs/decisions.md #63). The panel's store is the real one.
describe('SourceCard', () => {
  let fixture: ComponentFixture<SourceCard>;
  let card: HTMLElement;
  let panel: SourcePanelStore;

  function show(value: ChatSource, answerKey = 'a-1', lit = false): void {
    fixture = TestBed.createComponent(SourceCard);
    fixture.componentRef.setInput('source', value);
    fixture.componentRef.setInput('answerKey', answerKey);
    fixture.componentRef.setInput('lit', lit);
    fixture.detectChanges();

    card = fixture.nativeElement as HTMLElement;
    panel = TestBed.inject(SourcePanelStore);
  }

  const button = () => card.querySelector<HTMLButtonElement>('button.card')!;
  const textOf = (selector: string) => card.querySelector(selector)?.textContent?.trim() ?? null;

  it('shows the number, the document and where in it', () => {
    show(source());

    expect(textOf('.source-number')).toBe('2');
    expect(textOf('.source-title')).toBe('İK Yönetmeliği');
    expect(textOf('.source-place')).toBe('Bölüm 2: Yıllık İzin · s. 3');
  });

  // decisions #22, #46: a DOCX has no pages, the text before the first heading no section.
  it.each([
    [{ pageNumber: null }, 'Bölüm 2: Yıllık İzin'],
    [{ sectionPath: null }, 's. 3'],
    [{ sectionPath: null, pageNumber: null }, ''],
    [{ sectionPath: '' }, 's. 3'],
  ])('reads the place of %o as "%s"', (details, place) => {
    show(source(details));

    expect(textOf('.source-place')).toBe(place);
  });

  it('names the document for a screen reader', () => {
    show(source());

    expect(button().getAttribute('aria-label')).toBe('İK Yönetmeliği kaynağını göster');
  });

  // The card sits inside an answer; it must never send a form.
  it('is a button that sends nothing', () => {
    show(source());

    expect(button().getAttribute('type')).toBe('button');
  });

  describe('a press', () => {
    it('opens the source in the panel, with its answer', () => {
      show(source(), 'a-7');

      button().click();

      expect(panel.opened()).toEqual({ answerKey: 'a-7', source: source() });
    });

    // So the panel can give the focus back to the card when it closes.
    it('tells the panel which card opened it', () => {
      show(source());
      document.body.appendChild(card);
      button().click();
      button().blur();

      panel.close();

      expect(document.activeElement).toBe(button());
      card.remove();
    });
  });

  describe('the card that is open in the panel', () => {
    it('is marked as pressed and selected', () => {
      show(source(), 'a-1');

      button().click();
      fixture.detectChanges();

      expect(button().getAttribute('aria-pressed')).toBe('true');
      expect(card.classList).toContain('selected');
    });

    it('is not marked before it is pressed', () => {
      show(source());

      expect(button().getAttribute('aria-pressed')).toBe('false');
      expect(card.classList).not.toContain('selected');
    });

    // Numbers repeat: [2] of another answer being open is not this card.
    it('is not marked when the same number of another answer is open', () => {
      show(source(), 'a-1');

      panel.open('a-2', source());
      fixture.detectChanges();

      expect(button().getAttribute('aria-pressed')).toBe('false');
    });

    it('loses the mark when the panel closes', () => {
      show(source());
      button().click();
      fixture.detectChanges();

      panel.close();
      fixture.detectChanges();

      expect(card.classList).not.toContain('selected');
    });
  });

  it('lights up while its number is pointed at in the answer', () => {
    show(source(), 'a-1', true);

    expect(card.classList).toContain('lit');
  });

  it('is not lit otherwise', () => {
    show(source(), 'a-1', false);

    expect(card.classList).not.toContain('lit');
  });
});
