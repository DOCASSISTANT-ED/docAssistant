import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { DocumentInfo, DocumentStatus } from '../api/documents';
import { Session } from '../auth/session';
import { Documents } from './documents';

const base = `${environment.apiBaseUrl}/documents`;

function info(id: string, details: Partial<DocumentInfo> = {}): DocumentInfo {
  return {
    id,
    title: `Belge ${id}`,
    fileName: `belge-${id}.pdf`,
    contentType: 'application/pdf',
    sizeBytes: 512 * 1024,
    status: 'Done',
    failureReason: null,
    createdAt: '2026-10-15T08:00:00Z',
    processedAt: '2026-10-15T08:00:05Z',
    ...details,
  };
}

// The documents page (docs/decisions.md #66), driven the way a user drives it. The API
// is replaced by HttpTestingController, which answers each request as the test says;
// the session is a stand-in whose role the test chooses.
describe('Documents', () => {
  let fixture: ComponentFixture<Documents>;
  let element: HTMLElement;
  let api: HttpTestingController;
  let isAdmin: ReturnType<typeof signal<boolean>>;

  beforeEach(() => {
    isAdmin = signal(true);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Session, useValue: { isAdmin: isAdmin.asReadonly() } },
      ],
    });

    api = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture?.destroy();
    api.verify();
    document.body.replaceChildren();
  });

  // Opens the page as a member of the given role. It asks for the list at once; the
  // test answers it with answerList (or leaves it waiting).
  async function open(role: 'admin' | 'member' = 'admin'): Promise<void> {
    isAdmin.set(role === 'admin');
    fixture = TestBed.createComponent(Documents);
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

  async function answerList(documents: DocumentInfo[]): Promise<void> {
    api.expectOne((request) => request.method === 'GET' && request.url === base).flush(documents);
    await settle();
  }

  async function failList(): Promise<void> {
    api
      .expectOne((request) => request.method === 'GET' && request.url === base)
      .flush(null, { status: 0, statusText: 'Unknown Error' });
    await settle();
  }

  const textOf = (selector: string) => element.querySelector(selector)?.textContent?.trim() ?? null;
  const rows = () => Array.from(element.querySelectorAll<HTMLElement>('li.document'));
  const row = (title: string) => rows().find((item) => item.querySelector('.name')?.textContent?.trim() === title)!;
  const rowText = (title: string, selector: string) => row(title).querySelector(selector)?.textContent?.trim() ?? null;
  const button = (text: string) =>
    Array.from(element.querySelectorAll<HTMLButtonElement>('button')).find(
      (candidate) => candidate.textContent?.trim() === text,
    );

  // ---- The list -------------------------------------------------------------------

  describe('the list', () => {
    it('says it is loading until the list arrives', async () => {
      await open();

      expect(textOf('[role="status"]')).toBe('Belgeler yükleniyor…');
      expect(rows()).toEqual([]);

      await answerList([info('d-1')]);

      expect(element.textContent).not.toContain('Belgeler yükleniyor…');
    });

    it('shows the documents in the order the API gives them', async () => {
      await open();

      await answerList([info('d-3'), info('d-2'), info('d-1')]);

      expect(rows().map((item) => item.querySelector('.name')?.textContent?.trim())).toEqual([
        'Belge d-3',
        'Belge d-2',
        'Belge d-1',
      ]);
    });

    it('tells an admin how to add the first document', async () => {
      await open('admin');

      await answerList([]);

      expect(textOf('.message.empty')).toContain('Henüz belge yok.');
      expect(textOf('.message.empty')).toContain('"Belge yükle" ile ilk belgenizi ekleyin.');
    });

    // A member cannot upload, so the page does not point at a button they do not have.
    it('tells a member only that there are no documents yet', async () => {
      await open('member');

      await answerList([]);

      expect(textOf('.message.empty')).toBe('Henüz belge yok.');
    });

    it('says the list could not be loaded and offers to try again', async () => {
      await open();

      await failList();

      expect(textOf('[role="alert"] p')).toBe('Belgeler yüklenemedi. Bağlantınızı kontrol edip yeniden deneyin.');
      expect(button('Yeniden dene')).toBeDefined();
    });

    it('loads the list again when asked to try again', async () => {
      await open();
      await failList();

      button('Yeniden dene')!.click();
      await settle();

      expect(textOf('[role="status"]')).toBe('Belgeler yükleniyor…');

      await answerList([info('d-1')]);

      expect(rows().length).toBe(1);
      expect(button('Yeniden dene')).toBeUndefined();
    });
  });

  // ---- A document in the list -------------------------------------------------------

  describe('a document in the list', () => {
    it.each<[DocumentStatus, string]>([
      ['Pending', 'Sırada'],
      ['Processing', 'İşleniyor'],
      ['Done', 'Hazır'],
      ['Failed', 'İşlenemedi'],
    ])('shows the %s state as "%s"', async (status, label) => {
      await open();

      await answerList([info('d-1', { status })]);

      expect(rowText('Belge d-1', '.status')).toBe(label);
    });

    // The reason is written for the user (#24): the list is where an admin reads it.
    it('shows why a document could not be processed', async () => {
      await open();

      await answerList([info('d-1', { status: 'Failed', failureReason: 'Dosya parolalı olduğu için okunamadı.' })]);

      expect(rowText('Belge d-1', '.reason')).toBe('Dosya parolalı olduğu için okunamadı.');
    });

    it('shows no reason for a document that did not fail', async () => {
      await open();

      await answerList([info('d-1', { status: 'Done', failureReason: 'Eski bir neden.' })]);

      expect(row('Belge d-1').querySelector('.reason')).toBeNull();
    });

    it.each([
      ['application/pdf', 'PDF'],
      ['application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'DOCX'],
    ])('names a document of type %s %s', async (contentType, label) => {
      await open();

      await answerList([info('d-1', { contentType })]);

      expect(rowText('Belge d-1', '.type')).toBe(label);
      expect(rowText('Belge d-1', '.details')).toContain(label);
    });

    it('shows the size and the date of the upload', async () => {
      await open();

      await answerList([info('d-1', { sizeBytes: Math.round(2.4 * 1024 * 1024), createdAt: '2026-10-15T08:00:00Z' })]);

      const details = rowText('Belge d-1', '.details')!;
      expect(details).toContain('2,4 MB');
      expect(details).toContain('15 Eki 2026');
    });
  });

  // ---- Uploading ------------------------------------------------------------------

  describe('uploading', () => {
    const fileInput = () => element.querySelector<HTMLInputElement>('input.file-input');
    const uploadRows = () => Array.from(element.querySelectorAll<HTMLElement>('li.upload'));
    const uploadPosts = () =>
      api.match((request) => request.method === 'POST' && request.url === base);

    function pdf(name = 'yonetmelik.pdf', size = 1024): File {
      return new File([new Uint8Array(size)], name, { type: 'application/pdf' });
    }

    // What the browser does when files are chosen: the input holds them and says it changed.
    async function choose(...files: File[]): Promise<void> {
      const input = fileInput()!;
      Object.defineProperty(input, 'files', { value: files, configurable: true });
      input.dispatchEvent(new Event('change'));
      await settle();
    }

    async function answerUpload(document: DocumentInfo): Promise<void> {
      api
        .expectOne((request) => request.method === 'POST' && request.url === base)
        .flush(document, { status: 202, statusText: 'Accepted' });
      await settle();
    }

    async function failUpload(status: number): Promise<void> {
      api
        .expectOne((request) => request.method === 'POST' && request.url === base)
        .flush(null, { status, statusText: 'Error' });
      await settle();
    }

    it('is offered to an admin, for PDF and DOCX files, several at a time', async () => {
      await open('admin');
      await answerList([]);

      expect(textOf('.upload-button')).toContain('Belge yükle');
      expect(fileInput()!.accept).toBe('.pdf,.docx');
      expect(fileInput()!.multiple).toBe(true);
    });

    it('is not offered to a member', async () => {
      await open('member');
      await answerList([]);

      expect(fileInput()).toBeNull();
      expect(element.textContent).not.toContain('Belge yükle');
    });

    it('sends a chosen file and says it is on its way', async () => {
      await open();
      await answerList([]);

      await choose(pdf('yonetmelik.pdf'));

      const [request] = uploadPosts();
      expect((request.request.body as FormData).get('file')).toBeInstanceOf(File);
      expect(uploadRows().map((item) => item.querySelector('.upload-name')?.textContent)).toEqual([
        'yonetmelik.pdf',
      ]);
      expect(uploadRows()[0].querySelector('[role="status"]')?.textContent?.trim()).toBe('Yükleniyor…');

      request.flush(info('d-new', { status: 'Done' }), { status: 202, statusText: 'Accepted' });
      await settle();
    });

    // The new document goes on top, where the newest are (#66); no need to wait for the
    // next list.
    it('puts the uploaded document at the top of the list and clears its upload line', async () => {
      await open();
      await answerList([info('d-old')]);

      await choose(pdf('yeni.pdf'));
      await answerUpload(info('d-new', { title: 'Yeni', status: 'Done' }));

      expect(rows().map((item) => item.querySelector('.name')?.textContent?.trim())).toEqual(['Yeni', 'Belge d-old']);
      expect(uploadRows()).toEqual([]);
    });

    // A list that came in meanwhile may already hold it.
    it('does not show the uploaded document twice', async () => {
      await open();
      await answerList([info('d-new', { title: 'Yeni' })]);

      await choose(pdf('yeni.pdf'));
      await answerUpload(info('d-new', { title: 'Yeni', status: 'Done' }));

      expect(rows().length).toBe(1);
    });

    it.each([
      ['rapor.doc', 1024, 'Yalnızca PDF ve DOCX dosyaları yüklenebilir.'],
      ['bos.pdf', 0, 'Dosya boş.'],
      ['buyuk.pdf', 20 * 1024 * 1024 + 1, "Dosya 20 MB'tan büyük."],
    ])('does not send %s and says why', async (name, size, message) => {
      await open();
      await answerList([]);

      await choose(new File([new Uint8Array(size)], name));

      expect(uploadPosts()).toEqual([]);
      const line = uploadRows()[0];
      expect(line.querySelector('.upload-name')?.textContent).toBe(name);
      expect(line.querySelector('[role="alert"]')?.textContent?.trim()).toBe(message);
    });

    it.each([
      [413, "Dosya 20 MB'tan büyük."],
      [415, 'Bu dosya PDF ya da DOCX değil.'],
      [400, 'Dosya boş ya da okunamadı.'],
      [403, 'Belge yalnızca yöneticiler tarafından yüklenebilir.'],
      [0, 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.'],
      [500, 'Dosya yüklenemedi. Biraz sonra yeniden deneyin.'],
    ])('turns a %s from the server into a message', async (status, message) => {
      await open();
      await answerList([]);

      await choose(pdf());
      await failUpload(status);

      expect(uploadRows()[0].querySelector('[role="alert"]')?.textContent?.trim()).toBe(message);
      expect(rows()).toEqual([]);
    });

    it('takes a failed upload line away when it is closed', async () => {
      await open();
      await answerList([]);
      await choose(new File(['x'], 'not.txt'));

      button('Kapat')!.click();
      await settle();

      expect(uploadRows()).toEqual([]);
    });

    // One at a time, so the list shows them in the order they were chosen.
    it('sends several files one after the other, in the order they were chosen', async () => {
      await open();
      await answerList([]);

      await choose(pdf('bir.pdf'), pdf('iki.pdf'));

      // expectOne fails if both had been sent at once.
      const first = api.expectOne((request) => request.method === 'POST' && request.url === base);
      expect(((first.request.body as FormData).get('file') as File).name).toBe('bir.pdf');
      first.flush(info('d-1', { title: 'Bir', status: 'Done' }), { status: 202, statusText: 'Accepted' });
      await settle();

      const second = api.expectOne((request) => request.method === 'POST' && request.url === base);
      expect(((second.request.body as FormData).get('file') as File).name).toBe('iki.pdf');
      second.flush(info('d-2', { title: 'İki', status: 'Done' }), { status: 202, statusText: 'Accepted' });
      await settle();

      expect(rows().map((item) => item.querySelector('.name')?.textContent?.trim())).toEqual(['İki', 'Bir']);
    });

    // A file that breaks the rules does not stop the others.
    it('still sends the good files when one of the chosen files is refused', async () => {
      await open();
      await answerList([]);

      await choose(new File(['x'], 'not.txt'), pdf('iyi.pdf'));
      await answerUpload(info('d-1', { title: 'İyi', status: 'Done' }));

      expect(rows().map((item) => item.querySelector('.name')?.textContent?.trim())).toEqual(['İyi']);
      expect(uploadRows().map((item) => item.querySelector('.upload-name')?.textContent)).toEqual(['not.txt']);
    });

    // So that the same file can be chosen again after it failed.
    it('empties the file input after the files are taken', async () => {
      await open();
      await answerList([]);
      const input = fileInput()!;
      let emptied = false;
      Object.defineProperty(input, 'value', {
        configurable: true,
        get: () => '',
        set: (value: string) => {
          emptied = value === '';
        },
      });

      await choose(new File(['x'], 'not.txt'));

      expect(emptied).toBe(true);
    });
  });

  // ---- What each role is told -------------------------------------------------------

  describe('the rules above the list', () => {
    it('tell an admin what can be uploaded', async () => {
      await open('admin');
      await answerList([]);

      expect(textOf('.rules')).toBe(
        'PDF ya da DOCX, en fazla 20 MB. Yüklenen belge birkaç saniye içinde aranabilir olur.',
      );
    });

    it('tell a member who uploads documents', async () => {
      await open('member');
      await answerList([]);

      expect(textOf('.rules')).toBe('Belgeleri firmanızın yöneticisi yükler.');
    });
  });
});
