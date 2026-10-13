import { Component, input, output, signal } from '@angular/core';
import { ChatSource } from '../api/chat';
import { AnswerEntry } from './chat-store';
import { CitationsPipe, CitedSourcesPipe } from './citations';

// One answer in the conversation: the text with its source numbers, the cards of the
// sources it points at, and whatever state it is in (still searching, being written,
// stopped, failed).
@Component({
  imports: [CitationsPipe, CitedSourcesPipe],
  selector: 'app-answer',
  styleUrl: './answer.css',
  templateUrl: './answer.html',
})
export class Answer {
  readonly entry = input.required<AnswerEntry>();

  // Only the conversation's last answer can be asked again.
  readonly canRetry = input(false);

  readonly retry = output<void>();

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

  // "Bölüm 2: Yıllık İzin · s. 3". A DOCX has no pages and text before the first heading
  // has no section (docs/decisions.md #22, #46), so either part may be missing.
  protected placeOf(source: ChatSource): string {
    return [source.sectionPath, source.pageNumber === null ? null : `s. ${source.pageNumber}`]
      .filter((part) => part !== null && part !== '')
      .join(' · ');
  }
}
