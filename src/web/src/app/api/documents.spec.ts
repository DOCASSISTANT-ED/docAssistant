import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { DocumentInfo, DocumentsApi, Passage } from './documents';

const base = `${environment.apiBaseUrl}/documents`;

const document: DocumentInfo = {
  id: 'd-1',
  title: 'İK Yönetmeliği',
  fileName: 'ik-yonetmeligi.pdf',
  contentType: 'application/pdf',
  sizeBytes: 1024,
  status: 'Done',
  failureReason: null,
  createdAt: '2026-10-14T08:00:00Z',
  processedAt: '2026-10-14T08:00:05Z',
};

// The calls the source panel makes (docs/decisions.md #63).
describe('DocumentsApi', () => {
  let documents: DocumentsApi;
  let api: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    documents = TestBed.inject(DocumentsApi);
    api = TestBed.inject(HttpTestingController);
  });

  afterEach(() => api.verify());

  it('reads a document by its id', async () => {
    const answer = firstValueFrom(documents.get('d-1'));

    const request = api.expectOne(`${base}/d-1`);
    expect(request.request.method).toBe('GET');
    request.flush(document);

    expect(await answer).toEqual(document);
  });

  // Asked before opening the original, so that a deleted document gives a message
  // instead of an error page in a new tab.
  describe('whether a document still exists', () => {
    it('is true when the API finds it', async () => {
      const answer = firstValueFrom(documents.exists('d-1'));

      api.expectOne(`${base}/d-1`).flush(document);

      expect(await answer).toBe(true);
    });

    // Deleted, or another company's: the API answers 404 to both (decisions #63).
    it('is false when the API says 404', async () => {
      const answer = firstValueFrom(documents.exists('d-1'));

      api.expectOne(`${base}/d-1`).flush(null, { status: 404, statusText: 'Not Found' });

      expect(await answer).toBe(false);
    });

    // "Not reachable" is not "gone": the user should try again, not be told it is deleted.
    it.each([
      [0, 'the API cannot be reached'],
      [500, 'the API fails'],
      [401, 'the session has ended'],
    ])('fails with the error when %s answer comes (%s)', async (status) => {
      const answer = firstValueFrom(documents.exists('d-1'));

      const request = api.expectOne(`${base}/d-1`);
      if (status === 0) {
        request.error(new ProgressEvent('error'));
      } else {
        request.flush(null, { status, statusText: 'Error' });
      }

      const error = await answer.catch((e: unknown) => e);
      expect(error).toBeInstanceOf(HttpErrorResponse);
      expect((error as HttpErrorResponse).status).toBe(status);
    });
  });

  describe('the text at a source', () => {
    const passages: Passage[] = [{ pageNumber: 3, text: 'Yıllık izin 14 gündür.' }];

    it('asks with the section and the page of the source', async () => {
      const answer = firstValueFrom(documents.passages('d-1', 'Bölüm 2: Yıllık İzin', 3));

      const request = api.expectOne((r) => r.url === `${base}/d-1/passages`);
      expect(request.request.method).toBe('GET');
      expect(request.request.params.get('sectionPath')).toBe('Bölüm 2: Yıllık İzin');
      expect(request.request.params.get('pageNumber')).toBe('3');
      request.flush(passages);

      expect(await answer).toEqual(passages);
    });

    // A value the source does not have is left out; the server reads that as "none".
    it('leaves out a page the source does not have (a DOCX)', () => {
      void firstValueFrom(documents.passages('d-1', 'Bölüm 2', null));

      const request = api.expectOne((r) => r.url === `${base}/d-1/passages`);
      expect(request.request.params.has('pageNumber')).toBe(false);
      expect(request.request.params.get('sectionPath')).toBe('Bölüm 2');
      request.flush([]);
    });

    it('leaves out a section the source does not have (before the first heading)', () => {
      void firstValueFrom(documents.passages('d-1', null, 1));

      const request = api.expectOne((r) => r.url === `${base}/d-1/passages`);
      expect(request.request.params.has('sectionPath')).toBe(false);
      expect(request.request.params.get('pageNumber')).toBe('1');
      request.flush([]);
    });

    it('sends no parameters for a source that has neither', () => {
      void firstValueFrom(documents.passages('d-1', null, null));

      const request = api.expectOne(`${base}/d-1/passages`);
      expect(request.request.params.keys()).toEqual([]);
      request.flush([]);
    });

    // An empty section path is a section path; only a missing one means "none".
    it('sends an empty section as it is', () => {
      void firstValueFrom(documents.passages('d-1', '', 1));

      const request = api.expectOne((r) => r.url === `${base}/d-1/passages`);
      expect(request.request.params.get('sectionPath')).toBe('');
      request.flush([]);
    });

    // Section paths have ">", "&" and Turkish letters; the address must carry them whole.
    it('sends a section path with special characters so the server reads it back whole', () => {
      const section = 'Bölüm 4: İş Güvenliği > 4.1 Ağır Yük & Kaldırma';
      void firstValueFrom(documents.passages('d-1', section, 7));

      const request = api.expectOne((r) => r.url === `${base}/d-1/passages`);
      const query = request.request.params.toString();
      expect(new URLSearchParams(query).get('sectionPath')).toBe(section);
      request.flush([]);
    });
  });

  // The browser opens the file itself, so the session cookie travels with it.
  describe('the address of the original', () => {
    it('is the file address of the document', () => {
      expect(documents.fileUrl('d-1')).toBe(`${base}/d-1/file`);
    });

    // "#page=" is read by the browser's PDF viewer; the server never sees it.
    it('opens a PDF at the given page', () => {
      expect(documents.fileUrl('d-1', 3)).toBe(`${base}/d-1/file#page=3`);
    });

    it('has no page for a document without pages', () => {
      expect(documents.fileUrl('d-1', null)).toBe(`${base}/d-1/file`);
    });
  });
});
