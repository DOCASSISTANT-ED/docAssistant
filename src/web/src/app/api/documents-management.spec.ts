import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { DocumentInfo, DocumentsApi, uploadLimits } from './documents';

const base = `${environment.apiBaseUrl}/documents`;

function info(id: string, details: Partial<DocumentInfo> = {}): DocumentInfo {
  return {
    id,
    title: `Belge ${id}`,
    fileName: `belge-${id}.pdf`,
    contentType: 'application/pdf',
    sizeBytes: 2048,
    status: 'Pending',
    failureReason: null,
    createdAt: '2026-10-15T08:00:00Z',
    processedAt: null,
    ...details,
  };
}

// The calls the documents page makes (docs/decisions.md #66): the list, an upload and a
// deletion. The page relies on the API's answers as they come, so the calls must reach
// the right address with the right body.
describe('DocumentsApi: managing documents', () => {
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

  describe('the list', () => {
    it('asks for the organization’s documents and gives them as they come, newest first', async () => {
      const answer = firstValueFrom(documents.list());
      const listed = [info('d-2'), info('d-1')];

      const request = api.expectOne(base);
      expect(request.request.method).toBe('GET');
      expect(request.request.body).toBeNull();
      request.flush(listed);

      expect(await answer).toEqual(listed);
    });

    it('gives an empty list for an organization without documents', async () => {
      const answer = firstValueFrom(documents.list());

      api.expectOne(base).flush([]);

      expect(await answer).toEqual([]);
    });
  });

  describe('an upload', () => {
    // The server reads the file from the "file" form field (decisions #32).
    it('sends the file in the "file" field of a form, under its own name', async () => {
      const file = new File(['%PDF-1.7'], 'İK Yönetmeliği.pdf', { type: 'application/pdf' });
      const answer = firstValueFrom(documents.upload(file));

      const request = api.expectOne(base);
      expect(request.request.method).toBe('POST');

      const form = request.request.body as FormData;
      expect(form).toBeInstanceOf(FormData);
      const sent = form.get('file') as File;
      expect(sent.name).toBe('İK Yönetmeliği.pdf');
      expect(sent.size).toBe(file.size);
      expect([...form.keys()]).toEqual(['file']);

      request.flush(info('d-new'), { status: 202, statusText: 'Accepted' });

      expect((await answer).id).toBe('d-new');
    });

    // The browser sets the form's content type with its boundary; a hand-written one
    // would leave the server unable to find the file.
    it('leaves the content type to the browser', () => {
      void firstValueFrom(documents.upload(new File(['x'], 'a.pdf')));

      const request = api.expectOne(base);
      expect(request.request.headers.has('Content-Type')).toBe(false);
      request.flush(info('d-new'));
    });

    it('passes the server’s refusal on to the page', async () => {
      const answer = firstValueFrom(documents.upload(new File(['x'], 'a.pdf')));

      api.expectOne(base).flush(null, { status: 415, statusText: 'Unsupported Media Type' });

      const error = await answer.catch((e: unknown) => e);
      expect(error).toBeInstanceOf(HttpErrorResponse);
      expect((error as HttpErrorResponse).status).toBe(415);
    });
  });

  describe('a deletion', () => {
    it('asks the document’s own address to delete it', async () => {
      const answer = firstValueFrom(documents.delete('d-1'), { defaultValue: undefined });

      const request = api.expectOne(`${base}/d-1`);
      expect(request.request.method).toBe('DELETE');
      request.flush(null, { status: 204, statusText: 'No Content' });

      expect(await answer).toBeNull();
    });

    // 409 while processing, 404 when already gone: the page tells them apart (#66).
    it.each([
      [409, 'Conflict'],
      [404, 'Not Found'],
      [403, 'Forbidden'],
    ])('passes a %s on to the page', async (status, statusText) => {
      const answer = firstValueFrom(documents.delete('d-1'));

      api.expectOne(`${base}/d-1`).flush(null, { status, statusText });

      const error = await answer.catch((e: unknown) => e);
      expect((error as HttpErrorResponse).status).toBe(status);
    });
  });

  // The same rules the server applies (decisions #32), so that the page can say so at once.
  describe('the upload limits', () => {
    it('are 20 MB, PDF and DOCX', () => {
      expect(uploadLimits.maxSizeBytes).toBe(20 * 1024 * 1024);
      expect([...uploadLimits.extensions]).toEqual(['.pdf', '.docx']);
    });
  });
});
