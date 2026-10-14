import { Component, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ChatSource } from '../api/chat';
import { DocumentsApi } from '../api/documents';

// Where opening a source has got to. "gone" and "unreachable" stay on the card until
// the next try, so the user can read why nothing opened.
type OpenState = 'idle' | 'opening' | 'gone' | 'unreachable';

// One source under an answer: which document, where in it, and a click that opens the
// original (docs/decisions.md #63).
@Component({
  selector: 'app-source-card',
  styleUrl: './source-card.css',
  templateUrl: './source-card.html',
  host: { '[class.lit]': 'lit()' },
})
export class SourceCard {
  private readonly documents = inject(DocumentsApi);

  readonly source = input.required<ChatSource>();

  // Set while the same number is pointed at in the answer's text.
  readonly lit = input(false);

  protected readonly state = signal<OpenState>('idle');

  // "Bölüm 2: Yıllık İzin · s. 3". A DOCX has no pages and text before the first heading
  // has no section (decisions #22, #46), so either part may be missing.
  protected place(): string {
    const source = this.source();

    return [source.sectionPath, source.pageNumber === null ? null : `s. ${source.pageNumber}`]
      .filter((part) => part !== null && part !== '')
      .join(' · ');
  }

  // A PDF opens in a new tab at the source's page; a DOCX is downloaded, as browsers
  // cannot show one. The source does not say which type its document is, but only PDF
  // sources have page numbers (decisions #22), so the page decides.
  //
  // The document is asked about first, so that a deleted one gives a message here
  // instead of an error page in a new tab. The tab is opened at once, while the click
  // is still going on: a tab opened after waiting for an answer counts as a pop-up, and
  // browsers block those.
  protected async open(): Promise<void> {
    if (this.state() === 'opening') {
      return;
    }

    const source = this.source();
    const isPdf = source.pageNumber !== null;
    const tab = isPdf ? this.openWaitingTab() : null;

    this.state.set('opening');

    try {
      if (!(await firstValueFrom(this.documents.exists(source.documentId)))) {
        tab?.close();
        this.state.set('gone');
        return;
      }
    } catch {
      tab?.close();
      this.state.set('unreachable');
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

    this.state.set('idle');
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
