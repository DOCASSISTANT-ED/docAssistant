import { Component, computed, input, output, signal } from '@angular/core';
import { notFoundAnswer } from '../api/chat';
import { AnswerEntry } from './chat-store';
import { CitationsPipe, CitedSourcesPipe } from './citations';
import { SourceCard } from './source-card';

// One answer in the conversation: the text with its source numbers, the cards of the
// sources it points at, and whatever state it is in (still searching, being written,
// stopped, failed).
@Component({
  imports: [CitationsPipe, CitedSourcesPipe, SourceCard],
  selector: 'app-answer',
  styleUrl: './answer.css',
  templateUrl: './answer.html',
})
export class Answer {
  readonly entry = input.required<AnswerEntry>();

  // Only the conversation's last answer can be asked again.
  readonly canRetry = input(false);

  readonly retry = output<void>();

  // The documents had no answer. Shown differently from an answer: it is the one case
  // where the user has to do something else (ask in other words, or upload the document).
  protected readonly nothingFound = computed(() => {
    const answer = this.entry();

    return answer.state === 'done' && answer.text.trim() === notFoundAnswer;
  });

  // The source under the pointer or the keyboard focus: its number in the text and its
  // card light up together, so it is easy to see which card a number means.
  private readonly lit = signal<number | null>(null);

  protected light(number: number | null): void {
    this.lit.set(number);
  }

  protected isLit(number: number): boolean {
    return this.lit() === number;
  }

  // Numbers repeat from answer to answer, so a card's id also carries its answer.
  protected sourceId(number: number): string {
    return `source-${this.entry().key}-${number}`;
  }

  // A click on a number in the text brings its card into view.
  protected showSource(number: number): void {
    document.getElementById(this.sourceId(number))?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }
}
