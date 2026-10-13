import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { apiRequestInterceptor } from './api-request.interceptor';

// docs/decisions.md #60: the session cookie goes to the API only, and every request to
// the API carries the header the API asks of cookie-authenticated requests.
describe('apiRequestInterceptor', () => {
  let http: HttpClient;
  let sent: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiRequestInterceptor])),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpClient);
    sent = TestBed.inject(HttpTestingController);
  });

  afterEach(() => sent.verify());

  it('sends the cookie and the header with a request that reads from the API', () => {
    const url = `${environment.apiBaseUrl}/auth/me`;

    http.get(url).subscribe();

    const { request } = sent.expectOne(url);
    expect(request.withCredentials).toBe(true);
    expect(request.headers.get('X-Requested-With')).toBe('XMLHttpRequest');
  });

  // The API turns such a request away when the header is missing.
  it.each(['POST', 'PUT', 'PATCH', 'DELETE'])(
    'sends them with a %s request to the API',
    (method) => {
      const url = `${environment.apiBaseUrl}/documents`;

      http.request(method, url, { body: {} }).subscribe();

      const { request } = sent.expectOne(url);
      expect(request.withCredentials).toBe(true);
      expect(request.headers.get('X-Requested-With')).toBe('XMLHttpRequest');
    },
  );

  it('keeps what the caller put into the request', () => {
    const url = `${environment.apiBaseUrl}/chat`;

    http
      .post(url, { question: 'Yıllık izin kaç gün?' }, { headers: { Accept: 'text/event-stream' } })
      .subscribe();

    const { request } = sent.expectOne(url);
    expect(request.body).toEqual({ question: 'Yıllık izin kaç gün?' });
    expect(request.headers.get('Accept')).toBe('text/event-stream');
  });

  it('leaves a request to another site untouched', () => {
    const url = 'https://baska-site.example/veri.json';

    http.get(url).subscribe();

    const { request } = sent.expectOne(url);
    expect(request.withCredentials).toBe(false);
    expect(request.headers.has('X-Requested-With')).toBe(false);
  });

  // An address that merely begins like the API's is still another site. This can only be
  // told apart when the API has an address of its own (development); in a production
  // build the API is on the app's own origin and the base address is empty.
  it.runIf(environment.apiBaseUrl !== '')(
    'leaves a site whose address only begins like that of the API untouched',
    () => {
      const url = `${environment.apiBaseUrl}.baska-site.example/auth/me`;

      http.get(url).subscribe();

      const { request } = sent.expectOne(url);
      expect(request.withCredentials).toBe(false);
      expect(request.headers.has('X-Requested-With')).toBe(false);
    },
  );
});
