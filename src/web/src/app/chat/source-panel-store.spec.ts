import { TestBed } from '@angular/core/testing';
import { ChatSource } from '../api/chat';
import { SourcePanelStore } from './source-panel-store';

function source(number: number): ChatSource {
  return {
    number,
    documentId: `d-${number}`,
    documentTitle: `Belge ${number}`,
    sectionPath: null,
    pageNumber: null,
  };
}

// Which source the panel beside the chat shows (docs/decisions.md #63).
describe('SourcePanelStore', () => {
  let panel: SourcePanelStore;

  beforeEach(() => {
    panel = TestBed.inject(SourcePanelStore);
  });

  // Two buttons on the page, to see where the focus goes.
  function button(name: string): HTMLButtonElement {
    const element = document.createElement('button');
    element.textContent = name;
    document.body.appendChild(element);

    return element;
  }

  afterEach(() => document.body.replaceChildren());

  it('shows nothing at first', () => {
    expect(panel.opened()).toBeNull();
    expect(panel.isOpen('a-1', 1)).toBe(false);
  });

  it('shows the source it is asked to, with its answer', () => {
    panel.open('a-1', source(2));

    expect(panel.opened()).toEqual({ answerKey: 'a-1', source: source(2) });
  });

  it('shows nothing once closed', () => {
    panel.open('a-1', source(2));

    panel.close();

    expect(panel.opened()).toBeNull();
  });

  it('moves to another source without closing', () => {
    panel.open('a-1', source(1));

    panel.open('a-2', source(3));

    expect(panel.opened()).toEqual({ answerKey: 'a-2', source: source(3) });
  });

  // Numbers repeat from answer to answer: [1] of one answer is not [1] of another.
  describe('which card is the open one', () => {
    it('is the number of the same answer', () => {
      panel.open('a-1', source(1));

      expect(panel.isOpen('a-1', 1)).toBe(true);
    });

    it('is not the same number in another answer', () => {
      panel.open('a-1', source(1));

      expect(panel.isOpen('a-2', 1)).toBe(false);
    });

    it('is not another number in the same answer', () => {
      panel.open('a-1', source(1));

      expect(panel.isOpen('a-1', 2)).toBe(false);
    });
  });

  // Keyboard users continue where they were.
  describe('the focus', () => {
    it('goes back to what opened the panel when it closes', () => {
      const card = button('Kart');
      panel.open('a-1', source(1), card);
      button('Kapat').focus();

      panel.close();

      expect(document.activeElement).toBe(card);
    });

    it('goes back to what opened the panel first, after moving between sources', () => {
      const first = button('İlk kart');
      const second = button('İkinci kart');
      panel.open('a-1', source(1), first);
      panel.open('a-1', source(2), second);

      panel.close();

      expect(document.activeElement).toBe(first);
    });

    it('goes back only once', () => {
      const card = button('Kart');
      panel.open('a-1', source(1), card);
      panel.close();
      const elsewhere = button('Başka');
      elsewhere.focus();

      panel.close();

      expect(document.activeElement).toBe(elsewhere);
    });

    it('stays where it is when nothing was named', () => {
      const elsewhere = button('Başka');
      panel.open('a-1', source(1));
      elsewhere.focus();

      panel.close();

      expect(document.activeElement).toBe(elsewhere);
    });

    // Closing a panel that is not open must not pull the focus away.
    it('is not moved by closing a panel that is not open', () => {
      const card = button('Kart');
      panel.open('a-1', source(1), card);
      panel.close();
      const elsewhere = button('Başka');
      elsewhere.focus();

      panel.close();
      panel.close();

      expect(document.activeElement).toBe(elsewhere);
    });

    it('remembers the opener of the next opening, not of an earlier one', () => {
      const old = button('Eski kart');
      panel.open('a-1', source(1), old);
      panel.close();
      const next = button('Yeni kart');
      panel.open('a-2', source(1), next);
      button('Kapat').focus();

      panel.close();

      expect(document.activeElement).toBe(next);
    });
  });
});
