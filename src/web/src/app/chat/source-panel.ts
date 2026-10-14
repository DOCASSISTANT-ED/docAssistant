import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ChatSource } from '../api/chat';
import { DocumentsApi } from '../api/documents';
import { SourcePanelStore } from './source-panel-store';

// How far the text of the source has come:
// - loading:  asked for.
// - ready:    here; paragraphs holds it.
// - empty:    the document is there but no longer has this place (processed again
//             with a renamed section, for one; decisions #63).
// - gone:     the document itself is gone.
// - failed:   could not be fetched; trying again may help.
type TextState = 'loading' | 'ready' | 'empty' | 'gone' | 'failed';

// Where opening the original has got to; shown on the button.
type FileState = 'idle' | 'opening' | 'gone' | 'failed';

// The panel beside the chat that shows what a source says, in the document's own words,
// and opens the original (docs/decisions.md #63). A source names its place by section and
// page, so this is the text there now, not a copy kept with the answer.
@Component({
  selector: 'app-source-panel',
  styleUrl: './source-panel.css',
  templateUrl: './source-panel.html',
  host: {
    role: 'complementary',
    'aria-label': 'Kaynak',
    '(document:keydown.escape)': 'panel.close()',
  },
})
export class SourcePanel {
  protected readonly panel = inject(SourcePanelStore);
  private readonly documents = inject(DocumentsApi);

  protected readonly textState = signal<TextState>('loading');
  protected readonly paragraphs = signal<string[]>([]);
  protected readonly fileState = signal<FileState>('idle');

  private readonly closeButton = viewChild<ElementRef<HTMLButtonElement>>('closeButton');

  // Counts the sources shown, so that a slow answer for an earlier one is dropped.
  private request = 0;

  constructor() {
    effect(() => {
      const opened = this.panel.opened();

      if (opened) {
        untracked(() => void this.load(opened.source));
      }
    });
  }

  protected source(): ChatSource | null {
    return this.panel.opened()?.source ?? null;
  }

  // "Bölüm 2: Yıllık İzin · s. 3"; either part may be missing (decisions #22, #46).
  protected place(source: ChatSource): string {
    return [source.sectionPath, source.pageNumber === null ? null : `s. ${source.pageNumber}`]
      .filter((part) => part !== null && part !== '')
      .join(' · ');
  }

  protected retry(): void {
    const source = this.source();

    if (source) {
      void this.load(source);
    }
  }

  // A PDF opens in a new tab at the source's page; a DOCX is downloaded, as browsers
  // cannot show one. The source does not say which type its document is, but only PDF
  // sources have page numbers (decisions #22), so the page decides.
  //
  // The document is asked about first, so that a deleted one gives a message here
  // instead of an error page in a new tab. The tab is opened at once, while the click
  // is still going on: a tab opened after waiting for an answer counts as a pop-up, and
  // browsers block those.
  protected async openFile(): Promise<void> {
    const source = this.source();

    if (!source || this.fileState() === 'opening') {
      return;
    }

    const isPdf = source.pageNumber !== null;
    const tab = isPdf ? this.openWaitingTab() : null;

    this.fileState.set('opening');

    try {
      if (!(await firstValueFrom(this.documents.exists(source.documentId)))) {
        tab?.close();
        this.fileState.set('gone');
        return;
      }
    } catch {
      tab?.close();
      this.fileState.set('failed');
      return;
    }

    const url = this.documents.fileUrl(source.documentId, source.pageNumber);

    if (!isPdf) {
      // The server sends a DOCX as an attachment: the browser saves it and the page stays.
      window.location.assign(url);
    } else if (tab) {
      tab.location.replace(url);
    } else {
      // The browser refused even the immediate tab; try once more with the address.
      window.open(url, '_blank', 'noopener');
    }

    this.fileState.set('idle');
  }

  private async load(source: ChatSource): Promise<void> {
    const request = ++this.request;

    this.textState.set('loading');
    this.paragraphs.set([]);
    this.fileState.set('idle');

    // The panel takes the focus when it opens, so the keyboard is where the reading is.
    queueMicrotask(() => this.closeButton()?.nativeElement.focus({ preventScroll: true }));

    try {
      const passages = await firstValueFrom(
        this.documents.passages(source.documentId, source.sectionPath, source.pageNumber),
      );

      if (request !== this.request) {
        return;
      }

      // Each passage is a chunk; chunks hold paragraphs separated by an empty line.
      const paragraphs = passages
        .flatMap((passage) => passage.text.split(/\n\s*\n/))
        .map((paragraph) => paragraph.trim())
        .filter((paragraph) => paragraph !== '');

      this.paragraphs.set(paragraphs);
      this.textState.set(paragraphs.length > 0 ? 'ready' : 'empty');
    } catch (error) {
      if (request !== this.request) {
        return;
      }

      this.textState.set(error instanceof HttpErrorResponse && error.status === 404 ? 'gone' : 'failed');
    }
  }

  // An empty tab that says what it is waiting for. Its link back to this page is cut:
  // the file has no business with the app's window.
  private openWaitingTab(): Window | null {
    const tab = window.open('', '_blank');

    if (tab) {
      tab.opener = null;
      tab.document.title = 'Belge açılıyor…';
      tab.document.body.textContent = 'Belge açılıyor…';
    }

    return tab;
  }
}
