import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { DocumentInfo, DocumentStatus } from '../api/documents';
import { Session } from '../auth/session';
import { Documents, refreshIntervalMs } from './documents';

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

  // Lets the page finish what it started. With fake timers (the refresh tests) waiting
  // for a real timeout would never end; the clock is moved on by nothing instead.
  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    if (vi.isFakeTimers()) {
      await vi.advanceTimersByTimeAsync(0);
    } else {
      await new Promise<void>((resolve) => setTimeout(resolve));
    }
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

  // ---- Deleting -------------------------------------------------------------------

  describe('deleting', () => {
    const deleteButton = (title: string) => row(title).querySelector<HTMLButtonElement>('button.delete');
    const confirmButton = (title: string) => row(title).querySelector<HTMLButtonElement>('button.danger');
    const failure = () => textOf('p.failure[role="alert"]');
    const deletes = () => api.match((request) => request.method === 'DELETE');

    async function click(target: HTMLButtonElement | null | undefined): Promise<void> {
      target!.click();
      await settle();
    }

    async function confirmDelete(title: string): Promise<void> {
      await click(deleteButton(title));
      await click(confirmButton(title));
    }

    async function answerDelete(id: string, status = 204): Promise<void> {
      api
        .expectOne((request) => request.method === 'DELETE' && request.url === `${base}/${id}`)
        .flush(null, { status, statusText: status === 204 ? 'No Content' : 'Error' });
      await settle();
    }

    it('is offered to an admin for every document, named after it', async () => {
      await open('admin');
      await answerList([info('d-1'), info('d-2')]);

      expect(deleteButton('Belge d-1')?.textContent?.trim()).toBe('Sil');
      expect(deleteButton('Belge d-1')?.getAttribute('aria-label')).toBe('Belge d-1 belgesini sil');
      expect(deleteButton('Belge d-2')).not.toBeNull();
    });

    it('is not offered to a member', async () => {
      await open('member');
      await answerList([info('d-1')]);

      expect(deleteButton('Belge d-1')).toBeNull();
      expect(button('Sil')).toBeUndefined();
    });

    // Deleting cannot be undone; one click must not do it.
    it('asks first, on the document’s own line, and sends nothing yet', async () => {
      await open();
      await answerList([info('d-1'), info('d-2')]);

      await click(deleteButton('Belge d-1'));

      expect(rowText('Belge d-1', '.confirm-text')).toBe('Silinsin mi?');
      expect(row('Belge d-1').querySelector('[role="group"]')?.getAttribute('aria-label')).toBe('Belge d-1 silinsin mi?');
      expect(row('Belge d-2').querySelector('.confirm')).toBeNull();
      expect(deletes()).toEqual([]);
    });

    it('does nothing when the admin changes their mind', async () => {
      await open();
      await answerList([info('d-1')]);
      await click(deleteButton('Belge d-1'));

      await click(button('Vazgeç'));

      expect(row('Belge d-1').querySelector('.confirm')).toBeNull();
      expect(deleteButton('Belge d-1')).not.toBeNull();
      expect(deletes()).toEqual([]);
    });

    // Only one question at a time: asking about another document moves it there.
    it('asks about one document at a time', async () => {
      await open();
      await answerList([info('d-1'), info('d-2')]);
      await click(deleteButton('Belge d-1'));

      await click(deleteButton('Belge d-2'));

      expect(row('Belge d-1').querySelector('.confirm')).toBeNull();
      expect(row('Belge d-2').querySelector('.confirm')).not.toBeNull();
    });

    it('deletes the document once confirmed and takes it off the list', async () => {
      await open();
      await answerList([info('d-1'), info('d-2')]);

      await confirmDelete('Belge d-1');
      await answerDelete('d-1');

      expect(rows().map((item) => item.querySelector('.name')?.textContent?.trim())).toEqual(['Belge d-2']);
      expect(failure()).toBeNull();
    });

    it('shows that the document is being deleted and cannot be pressed again', async () => {
      await open();
      await answerList([info('d-1')]);

      await confirmDelete('Belge d-1');

      expect(deleteButton('Belge d-1')?.textContent?.trim()).toBe('Siliniyor…');
      expect(deleteButton('Belge d-1')?.disabled).toBe(true);

      await answerDelete('d-1');
    });

    // One deletion at a time: a second one waits for the first to end.
    it('does not start a second deletion while one is on its way', async () => {
      await open();
      await answerList([info('d-1'), info('d-2')]);
      await confirmDelete('Belge d-1');

      await confirmDelete('Belge d-2');

      const pending = deletes();
      expect(pending.map((request) => request.request.url)).toEqual([`${base}/d-1`]);
      pending[0].flush(null, { status: 204, statusText: 'No Content' });
      await settle();
    });

    // Someone else deleted it meanwhile: the list catches up, no error is shown.
    it('takes the document off the list when it is already gone', async () => {
      await open();
      await answerList([info('d-1')]);

      await confirmDelete('Belge d-1');
      await answerDelete('d-1', 404);

      expect(rows()).toEqual([]);
      expect(failure()).toBeNull();
    });

    it.each([
      [409, '"Belge d-1" şu an işleniyor. Birkaç saniye sonra yeniden deneyin.'],
      [403, 'Belgeler yalnızca yöneticiler tarafından silinebilir.'],
      [0, 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.'],
      [500, '"Belge d-1" silinemedi. Biraz sonra yeniden deneyin.'],
    ])('keeps the document and explains a %s', async (status, message) => {
      await open();
      await answerList([info('d-1')]);

      await confirmDelete('Belge d-1');
      await answerDelete('d-1', status);

      expect(failure()).toBe(message);
      expect(rows().length).toBe(1);
      expect(deleteButton('Belge d-1')?.disabled).toBe(false);
    });

    // A new attempt starts clean.
    it('clears the last failure when the admin asks to delete again', async () => {
      await open();
      await answerList([info('d-1')]);
      await confirmDelete('Belge d-1');
      await answerDelete('d-1', 409);

      await click(deleteButton('Belge d-1'));

      expect(failure()).toBeNull();
    });
  });

  // ---- Refreshing -----------------------------------------------------------------

  // While a document is waiting or being processed, the list is asked for again every
  // few seconds, so the admin sees it become ready without reloading the page.
  describe('refreshing', () => {
    const listRequests = () => api.match((request) => request.method === 'GET' && request.url === base);

    beforeEach(() => {
      vi.useFakeTimers();
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    async function wait(ms: number): Promise<void> {
      await vi.advanceTimersByTimeAsync(ms);
      fixture.detectChanges();
    }

    it.each<DocumentStatus>(['Pending', 'Processing'])(
      'asks for the list again after a few seconds while a document is %s',
      async (status) => {
        await open();
        await answerList([info('d-1', { status })]);

        await wait(refreshIntervalMs - 1);
        expect(listRequests()).toEqual([]);

        await wait(1);
        await answerList([info('d-1', { status: 'Done' })]);

        expect(rowText('Belge d-1', '.status')).toBe('Hazır');
      },
    );

    it('does not ask again when every document is finished', async () => {
      await open();
      await answerList([info('d-1', { status: 'Done' }), info('d-2', { status: 'Failed' })]);

      await wait(refreshIntervalMs * 5);

      expect(listRequests()).toEqual([]);
    });

    it('keeps asking while a document is still on its way, and stops when it is done', async () => {
      await open();
      await answerList([info('d-1', { status: 'Pending' })]);

      await wait(refreshIntervalMs);
      await answerList([info('d-1', { status: 'Processing' })]);
      expect(rowText('Belge d-1', '.status')).toBe('İşleniyor');

      await wait(refreshIntervalMs);
      await answerList([info('d-1', { status: 'Done' })]);
      expect(rowText('Belge d-1', '.status')).toBe('Hazır');

      await wait(refreshIntervalMs * 5);
      expect(listRequests()).toEqual([]);
    });

    it('stops when the page is left', async () => {
      await open();
      await answerList([info('d-1', { status: 'Pending' })]);

      fixture.destroy();
      await vi.advanceTimersByTimeAsync(refreshIntervalMs * 5);

      expect(listRequests()).toEqual([]);
    });

    // The list on screen is still the best there is; only the first load shows a failure.
    it('keeps the list on screen when a refresh fails, and tries again', async () => {
      await open();
      await answerList([info('d-1', { status: 'Pending' })]);

      await wait(refreshIntervalMs);
      await failList();

      expect(rowText('Belge d-1', '.status')).toBe('Sırada');
      expect(element.textContent).not.toContain('Belgeler yüklenemedi');

      await wait(refreshIntervalMs);
      await answerList([info('d-1', { status: 'Done' })]);
      expect(rowText('Belge d-1', '.status')).toBe('Hazır');
    });

    // A new upload is Pending: the page starts following it without a reload.
    it('starts after an upload', async () => {
      await open();
      await answerList([info('d-old', { status: 'Done' })]);

      const input = element.querySelector<HTMLInputElement>('input.file-input')!;
      Object.defineProperty(input, 'files', {
        value: [new File([new Uint8Array(10)], 'yeni.pdf')],
        configurable: true,
      });
      input.dispatchEvent(new Event('change'));
      await settle();
      api
        .expectOne((request) => request.method === 'POST' && request.url === base)
        .flush(info('d-new', { status: 'Pending' }), { status: 202, statusText: 'Accepted' });
      await settle();

      await wait(refreshIntervalMs);
      await answerList([info('d-new', { status: 'Done' }), info('d-old', { status: 'Done' })]);

      expect(rowText('Belge d-new', '.status')).toBe('Hazır');
    });

    // A refresh that was already on its way when a document was deleted brings back the
    // list as it was before the deletion; the deleted document must not reappear with it.
    it('does not bring back a document deleted while a refresh was on its way', async () => {
      await open();
      await answerList([info('d-1', { status: 'Done' }), info('d-2', { status: 'Pending' })]);

      await wait(refreshIntervalMs);
      const refresh = api.expectOne((request) => request.method === 'GET' && request.url === base);

      row('Belge d-1').querySelector<HTMLButtonElement>('button.delete')!.click();
      await settle();
      row('Belge d-1').querySelector<HTMLButtonElement>('button.danger')!.click();
      await settle();
      api
        .expectOne((request) => request.method === 'DELETE' && request.url === `${base}/d-1`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      expect(rows().map((item) => item.querySelector('.name')?.textContent?.trim())).toEqual(['Belge d-2']);

      // The server read the list before the deletion.
      refresh.flush([info('d-1', { status: 'Done' }), info('d-2', { status: 'Done' })]);
      await settle();

      expect(rows().map((item) => item.querySelector('.name')?.textContent?.trim())).toEqual(['Belge d-2']);
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
