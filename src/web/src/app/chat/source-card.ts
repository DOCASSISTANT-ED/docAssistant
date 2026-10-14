import { Component, computed, inject, input } from '@angular/core';
import { ChatSource } from '../api/chat';
import { SourcePanelStore } from './source-panel-store';

// One source under an answer: which document and where in it. A click shows what the
// source says in the panel beside the chat (docs/decisions.md #63); the original is
// opened from there.
@Component({
  selector: 'app-source-card',
  styleUrl: './source-card.css',
  templateUrl: './source-card.html',
  host: { '[class.lit]': 'lit()', '[class.selected]': 'selected()' },
})
export class SourceCard {
  private readonly panel = inject(SourcePanelStore);

  readonly source = input.required<ChatSource>();

  // The answer the card belongs to; numbers repeat from answer to answer.
  readonly answerKey = input.required<string>();

  // Set while the same number is pointed at in the answer's text.
  readonly lit = input(false);

  // This source is the one in the panel.
  protected readonly selected = computed(() => this.panel.isOpen(this.answerKey(), this.source().number));

  // "Bölüm 2: Yıllık İzin · s. 3". A DOCX has no pages and text before the first heading
  // has no section (decisions #22, #46), so either part may be missing.
  protected place(): string {
    const source = this.source();

    return [source.sectionPath, source.pageNumber === null ? null : `s. ${source.pageNumber}`]
      .filter((part) => part !== null && part !== '')
      .join(' · ');
  }

  protected open(event: Event): void {
    this.panel.open(this.answerKey(), this.source(), event.currentTarget as HTMLElement);
  }
}
