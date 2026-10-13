import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Session } from './session';
import { sessionExpiredInterceptor } from './session-expired.interceptor';

@Component({ template: '' })
class Blank {}

// What the app does when the API answers 401 (docs/decisions.md #60: the session cookie
// lasts 60 minutes and there is no refresh).
describe('sessionExpiredInterceptor', () => {
  let http: HttpClient;
  let api: HttpTestingController;
  let router: Router;

  // A stand-in for the session: the test decides whether somebody is signed in and
  // sees whether the interceptor told the session to forget them.
  let signedIn: boolean;
  let expire: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    signedIn = true;
    expire = vi.fn(() => (signedIn = false));

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', component: Blank },
          { path: 'belgeler', component: Blank },
          { path: '', component: Blank },
        ]),
        provideHttpClient(withInterceptors([sessionExpiredInterceptor])),
        provideHttpClientTesting(),
        { provide: Session, useValue: { isSignedIn: () => signedIn, expire } },
      ],
    });

    http = TestBed.inject(HttpClient);
    api = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => api.verify());

  // The interceptor starts the navigation and does not wait for it.
  const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

  // Sends a request, answers it with `status`, and gives back the error the caller got.
  async function answer(status: number): Promise<unknown> {
    const failure = new Promise<unknown>((resolve) =>
      http.get('/api/veri').subscribe({ error: resolve }),
    );

    api.expectOne('/api/veri').flush(null, { status, statusText: 'Error' });
    await settle();

    return failure;
  }

  it('forgets the user and shows the login screen, remembering where they were', async () => {
    await router.navigateByUrl('/belgeler?sayfa=2');

    await answer(401);

    expect(expire).toHaveBeenCalledOnce();
    expect(router.url).toBe('/login?returnUrl=%2Fbelgeler%3Fsayfa%3D2');
  });

  it('does not remember the home page', async () => {
    await router.navigateByUrl('/');

    await answer(401);

    expect(router.url).toBe('/login');
  });

  // Its request did fail, whatever else happens.
  it('still gives the error to whoever sent the request', async () => {
    await router.navigateByUrl('/belgeler');

    const error = await answer(401);

    expect(error).toBeInstanceOf(HttpErrorResponse);
    expect((error as HttpErrorResponse).status).toBe(401);
  });

  // "Wrong password" on the login screen, or "nobody" at startup: nothing has expired.
  it('leaves a 401 alone while nobody is signed in', async () => {
    signedIn = false;
    await router.navigateByUrl('/belgeler');

    const error = await answer(401);

    expect(expire).not.toHaveBeenCalled();
    expect(router.url).toBe('/belgeler');
    expect((error as HttpErrorResponse).status).toBe(401);
  });

  // 403 means "signed in, but not allowed": the session is fine.
  it.each([400, 403, 404, 500])('leaves a %s answer alone', async (status) => {
    await router.navigateByUrl('/belgeler');

    const error = await answer(status);

    expect(expire).not.toHaveBeenCalled();
    expect(router.url).toBe('/belgeler');
    expect((error as HttpErrorResponse).status).toBe(status);
  });

  it('does nothing when the request succeeds', async () => {
    await router.navigateByUrl('/belgeler');
    const received = new Promise<unknown>((resolve) => http.get('/api/veri').subscribe(resolve));

    api.expectOne('/api/veri').flush({ ok: true });

    expect(await received).toEqual({ ok: true });
    expect(expire).not.toHaveBeenCalled();
    expect(router.url).toBe('/belgeler');
  });
});
