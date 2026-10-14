import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { ChatSource } from '../api/chat';
import { DocumentInfo, Passage } from '../api/documents';
import { SourcePanel } from './source-panel';
import { SourcePanelStore } from './source-panel-store';

const base = `${environment.apiBaseUrl}/documents`;

// A source from a PDF: it has a page.
const pdfSource: ChatSource = {
  number: 2,
  documentId: 'd-pdf',
  documentTitle: 'İK Yönetmeliği',
  sectionPath: 'Bölüm 2: Yıllık İzin',
  pageNumber: 3,
};

// A source from a DOCX: no pages (docs/decisions.md #22).
const docxSource: ChatSource = {
  number: 1,
  documentId: 'd-docx',
  documentTitle: 'Uzaktan Çalışma Politikası',
  sectionPath: '3. Ekipman',
  pageNumber: null,
};

const documentInfo = { id: 'd-pdf', status: 'Done' } as DocumentInfo;

// A tab as window.open gives it back, with what the panel does to it.
function fakeTab() {
  return {
    opener: {} as unknown,
    document: { title: '', body: { textContent: '' } },
    location: { replace: vi.fn() },
    close: vi.fn(),
  };
}

// The panel beside the chat that shows what a source says and opens the original
// (docs/decisions.md #63). The API is replaced by HttpTestingController; the panel's
// store is the real one.
describe('SourcePanel', () => {
  let fixture: ComponentFixture<SourcePanel>;
  let element: HTMLElement;
  let panel: SourcePanelStore;
  let api: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    panel = TestBed.inject(SourcePanelStore);
    api = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    api.verify();
    fixture?.destroy();
    vi.restoreAllMocks();
    document.body.replaceChildren();
  });

  // Opens the panel on a source, as a card does, and shows it on the page.
  async function show(source: ChatSource): Promise<void> {
    panel.open('a-1', source);
    fixture = TestBed.createComponent(SourcePanel);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);
    await settle();
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise<void>((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  const textOf = (selector: string) => element.querySelector(selector)?.textContent?.trim() ?? null;
  const paragraphs = () =>
    Array.from(element.querySelectorAll('.quote .paragraph')).map((p) => p.textContent);
  const openButton = () => element.querySelector<HTMLButtonElement>('button.open');

  function passagesRequest(documentId = pdfSource.documentId): TestRequest {
    return api.expectOne((request) => request.url === `${base}/${documentId}/passages`);
  }

  async function answerText(passages: Passage[], documentId?: string): Promise<void> {
    passagesRequest(documentId).flush(passages);
    await settle();
  }

  describe('the heading', () => {
    it('names the source: its number, the document and where in it', async () => {
      await show(pdfSource);
      await answerText([]);

      expect(textOf('.number')).toBe('2');
      expect(textOf('.title')).toBe('İK Yönetmeliği');
      expect(textOf('.place')).toBe('Bölüm 2: Yıllık İzin · s. 3');
    });

    it('leaves out the place when the source has none', async () => {
      await show({ ...pdfSource, sectionPath: null, pageNumber: null });
      await answerText([]);

      expect(element.querySelector('.place')).toBeNull();
    });

    // The keyboard is where the reading is.
    it('takes the focus when it opens', async () => {
      await show(pdfSource);

      expect(document.activeElement).toBe(element.querySelector('button.close'));

      await answerText([]);
    });
  });

  describe('the text of the source', () => {
    it("asks for the text at the source's section and page", async () => {
      await show(pdfSource);

      const request = passagesRequest();
      expect(request.request.params.get('sectionPath')).toBe('Bölüm 2: Yıllık İzin');
      expect(request.request.params.get('pageNumber')).toBe('3');
      request.flush([]);
    });

    it('says it is loading until the text is here', async () => {
      await show(pdfSource);

      expect(element.querySelector('[role="status"]')?.getAttribute('aria-label')).toBe(
        'Kaynağın metni yükleniyor',
      );
      expect(element.querySelector('.body')!.getAttribute('aria-busy')).toBe('true');

      await answerText([]);
      expect(element.querySelector('.body')!.getAttribute('aria-busy')).toBe('false');
    });

    // Each passage is a chunk; chunks hold paragraphs separated by an empty line.
    it('shows the text paragraph by paragraph, in order', async () => {
      await show(pdfSource);

      await answerText([
        { pageNumber: 3, text: 'Birinci paragraf.\n\nİkinci paragraf.' },
        { pageNumber: 3, text: 'Üçüncü paragraf.' },
      ]);

      expect(paragraphs()).toEqual(['Birinci paragraf.', 'İkinci paragraf.', 'Üçüncü paragraf.']);
      expect(textOf('.caption')).toBe('Belgenin bu bölümündeki metin; cevap buna dayanıyor.');
    });

    it('leaves out empty paragraphs and the space around them', async () => {
      await show(pdfSource);

      await answerText([{ pageNumber: 3, text: '  Bir.  \n \n\n\n İki. \n\n   ' }]);

      expect(paragraphs()).toEqual(['Bir.', 'İki.']);
    });

    // A single line break is part of a paragraph (a table row, for one).
    it('keeps single line breaks inside a paragraph', async () => {
      await show(pdfSource);

      await answerText([
        { pageNumber: 3, text: 'Kıdem: 1–5 yıl; 14 gün\nKıdem: 5–15 yıl; 20 gün' },
      ]);

      expect(paragraphs()).toEqual(['Kıdem: 1–5 yıl; 14 gün\nKıdem: 5–15 yıl; 20 gün']);
    });

    // A source's text is shown as written, never as HTML.
    it('shows the text as plain text', async () => {
      await show(pdfSource);

      await answerText([{ pageNumber: 3, text: '<b>kalın</b> <script>x</script>' }]);

      expect(paragraphs()).toEqual(['<b>kalın</b> <script>x</script>']);
      expect(element.querySelector('.quote b')).toBeNull();
    });

    // The document is there but no longer has that place (decisions #63).
    it('says the place is gone when the document no longer has it', async () => {
      await show(pdfSource);

      await answerText([]);

      expect(textOf('.message')).toContain('Bu bölüm belgenin şimdiki hâlinde bulunamadı');
      expect(openButton()).not.toBeNull();
    });

    it('treats text with nothing in it as a place that is gone', async () => {
      await show(pdfSource);

      await answerText([{ pageNumber: 3, text: '  \n\n  ' }]);

      expect(textOf('.message')).toContain('bulunamadı');
    });

    // Deleted, or another company's: nothing to open either.
    it('says the document is gone for a 404, and offers no original', async () => {
      await show(pdfSource);

      passagesRequest().flush(null, { status: 404, statusText: 'Not Found' });
      await settle();

      expect(textOf('.message')).toBe('Bu belge artık bulunamıyor.');
      expect(openButton()).toBeNull();
    });

    it('says it could not load the text, and loads it again on request', async () => {
      await show(pdfSource);
      passagesRequest().flush(null, { status: 500, statusText: 'Error' });
      await settle();

      expect(textOf('[role="alert"]')).toBe(
        'Kaynağın metni yüklenemedi. Bağlantınızı kontrol edip yeniden deneyin.',
      );

      element.querySelector<HTMLButtonElement>('button.secondary')!.click();
      await settle();
      await answerText([{ pageNumber: 3, text: 'Yıllık izin 14 gündür.' }]);

      expect(paragraphs()).toEqual(['Yıllık izin 14 gündür.']);
    });

    // The user pressed another card before the first text came: the late answer must
    // not show the wrong source's text under the new heading.
    it('drops the text of a source the user has moved on from', async () => {
      await show(pdfSource);
      const first = passagesRequest();

      panel.open('a-1', docxSource);
      await settle();
      await answerText(
        [{ pageNumber: null, text: 'Doğru kaynağın metni.' }],
        docxSource.documentId,
      );

      first.flush([{ pageNumber: 3, text: 'Eski kaynağın metni.' }]);
      await settle();

      expect(textOf('.title')).toBe('Uzaktan Çalışma Politikası');
      expect(paragraphs()).toEqual(['Doğru kaynağın metni.']);
    });

    it('drops the failure of a source the user has moved on from', async () => {
      await show(pdfSource);
      const first = passagesRequest();

      panel.open('a-1', docxSource);
      await settle();
      await answerText(
        [{ pageNumber: null, text: 'Doğru kaynağın metni.' }],
        docxSource.documentId,
      );

      first.flush(null, { status: 404, statusText: 'Not Found' });
      await settle();

      expect(paragraphs()).toEqual(['Doğru kaynağın metni.']);
    });
  });

  describe('closing', () => {
    it('closes with its own button', async () => {
      await show(pdfSource);
      await answerText([]);

      element.querySelector<HTMLButtonElement>('button.close')!.click();

      expect(panel.opened()).toBeNull();
    });

    it('closes with Escape', async () => {
      await show(pdfSource);
      await answerText([]);

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

      expect(panel.opened()).toBeNull();
    });

    it('names itself for a screen reader', async () => {
      await show(pdfSource);
      await answerText([]);

      expect(element.getAttribute('role')).toBe('complementary');
      expect(element.getAttribute('aria-label')).toBe('Kaynak');
    });
  });

  // ---- The original ----

  describe('opening the original of a PDF', () => {
    let tab: ReturnType<typeof fakeTab>;
    let open: ReturnType<typeof vi.spyOn>;

    beforeEach(() => {
      tab = fakeTab();
      open = vi.spyOn(window, 'open').mockReturnValue(tab as unknown as Window);
    });

    async function pressOpen(): Promise<void> {
      openButton()!.click();
      await settle();
    }

    function answerExists(status = 200): void {
      const request = api.expectOne(`${base}/${pdfSource.documentId}`);

      if (status === 200) {
        request.flush(documentInfo);
      } else if (status === 0) {
        request.error(new ProgressEvent('error'));
      } else {
        request.flush(null, { status, statusText: 'Error' });
      }
    }

    it('offers to open it', async () => {
      await show(pdfSource);
      await answerText([]);

      expect(openButton()!.textContent).toContain('Belgeyi aç');
    });

    // A tab opened after waiting for an answer counts as a pop-up and is blocked, so
    // it is opened during the press, before the API has answered.
    it('opens a waiting tab at once, cut off from the app', async () => {
      await show(pdfSource);
      await answerText([]);

      await pressOpen();

      expect(open).toHaveBeenCalledExactlyOnceWith('', '_blank');
      expect(tab.opener).toBeNull();
      expect(tab.document.title).toBe('Belge açılıyor…');
      expect(tab.location.replace).not.toHaveBeenCalled();

      answerExists();
      await settle();
    });

    it("shows the file at the source's page in that tab once the document is there", async () => {
      await show(pdfSource);
      await answerText([]);

      await pressOpen();
      answerExists();
      await settle();

      expect(tab.location.replace).toHaveBeenCalledExactlyOnceWith(`${base}/d-pdf/file#page=3`);
      expect(tab.close).not.toHaveBeenCalled();
      expect(element.querySelector('.file-note')).toBeNull();
    });

    it('waits for one answer before it can be pressed again', async () => {
      await show(pdfSource);
      await answerText([]);

      await pressOpen();
      await pressOpen();

      expect(open).toHaveBeenCalledOnce();
      expect(openButton()!.getAttribute('aria-busy')).toBe('true');

      answerExists();
      await settle();
      expect(openButton()!.getAttribute('aria-busy')).toBe('false');
    });

    // No error page in a new tab for a deleted document: the tab goes, the panel says why.
    it('closes the tab and says so when the document is gone', async () => {
      await show(pdfSource);
      await answerText([]);

      await pressOpen();
      answerExists(404);
      await settle();

      expect(tab.close).toHaveBeenCalledOnce();
      expect(tab.location.replace).not.toHaveBeenCalled();
      expect(textOf('.file-note')).toBe('Bu belge artık bulunamıyor.');
    });

    it.each([0, 500])(
      'closes the tab and says it could not open the document for %s',
      async (status) => {
        await show(pdfSource);
        await answerText([]);

        await pressOpen();
        answerExists(status);
        await settle();

        expect(tab.close).toHaveBeenCalledOnce();
        expect(textOf('.file-note')).toBe(
          'Belge açılamadı. Bağlantınızı kontrol edip yeniden deneyin.',
        );
      },
    );

    // The browser refused even the immediate tab: one more try, with the address.
    it('tries once more with the address when the browser refused the tab', async () => {
      open.mockReturnValueOnce(null);
      await show(pdfSource);
      await answerText([]);

      await pressOpen();
      answerExists();
      await settle();

      expect(open).toHaveBeenLastCalledWith(`${base}/d-pdf/file#page=3`, '_blank', 'noopener');
    });

    it('forgets an earlier failure when another source is shown', async () => {
      await show(pdfSource);
      await answerText([]);
      await pressOpen();
      answerExists(404);
      await settle();

      panel.open('a-1', { ...pdfSource, number: 3 });
      await settle();
      await answerText([]);

      expect(element.querySelector('.file-note')).toBeNull();
    });
  });

  describe('opening the original of a DOCX', () => {
    it('offers to download it', async () => {
      await show(docxSource);
      await answerText([], docxSource.documentId);

      expect(openButton()!.textContent).toContain('Belgeyi indir');
    });

    // A browser cannot show a DOCX; it is downloaded and the page stays. No tab.
    it('opens no tab, and asks first whether the document is still there', async () => {
      const open = vi.spyOn(window, 'open');
      await show(docxSource);
      await answerText([], docxSource.documentId);

      openButton()!.click();
      await settle();

      expect(open).not.toHaveBeenCalled();
      api
        .expectOne(`${base}/${docxSource.documentId}`)
        .flush(null, { status: 404, statusText: 'Not Found' });
      await settle();
      expect(textOf('.file-note')).toBe('Bu belge artık bulunamıyor.');
    });
  });
});
