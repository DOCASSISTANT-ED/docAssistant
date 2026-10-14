import { Injectable, signal } from '@angular/core';
import { ChatSource } from '../api/chat';

// A source shown in the panel, with the answer it belongs to: numbers repeat from answer
// to answer, so the number alone does not say which card was pressed.
export interface OpenedSource {
  answerKey: string;
  source: ChatSource;
}

// Which source the panel beside the chat shows, if any (docs/decisions.md #63). The
// cards and the numbers in an answer open it; the panel itself and the chat screen
// close it.
@Injectable({ providedIn: 'root' })
export class SourcePanelStore {
  private readonly current = signal<OpenedSource | null>(null);

  // What had the focus when the panel opened, to give it back when the panel closes:
  // keyboard users continue where they were.
  private returnFocusTo: HTMLElement | null = null;

  readonly opened = this.current.asReadonly();

  open(answerKey: string, source: ChatSource, trigger: HTMLElement | null = null): void {
    // Moving from one source to another keeps the place to return to of the first.
    if (this.current() === null) {
      this.returnFocusTo = trigger;
    }

    this.current.set({ answerKey, source });
  }

  close(): void {
    if (this.current() === null) {
      return;
    }

    this.current.set(null);
    this.returnFocusTo?.focus();
    this.returnFocusTo = null;
  }

  isOpen(answerKey: string, number: number): boolean {
    const current = this.current();

    return current !== null && current.answerKey === answerKey && current.source.number === number;
  }
}
