import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { CurrentUser } from '../api/identity';
import { Session } from './session';

const me = `${environment.apiBaseUrl}/auth/me`;
const login = `${environment.apiBaseUrl}/auth/login`;
const register = `${environment.apiBaseUrl}/auth/register`;
const logout = `${environment.apiBaseUrl}/auth/logout`;

const admin: CurrentUser = {
  userId: 'u-1',
  email: 'ayse@example.com',
  tenantId: 't-1',
  role: 'Admin',
};
const member: CurrentUser = { ...admin, role: 'Member' };

const credentials = { email: 'ayse@example.com', password: 'Gizli123!' };
const unauthorized = { status: 401, statusText: 'Unauthorized' };

// The API is replaced by HttpTestingController: each test says what the API answers.
describe('Session', () => {
  let session: Session;
  let api: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    session = TestBed.inject(Session);
    api = TestBed.inject(HttpTestingController);
  });

  // No test leaves a request it did not expect.
  afterEach(() => api.verify());

  // Lets the code under test carry on after an answer, up to its next request.
  const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

  async function loadAs(user: CurrentUser): Promise<void> {
    const loading = session.load();
    api.expectOne(me).flush(user);
    await loading;
  }

  it('does not know who is signed in before it has asked', () => {
    expect(session.status()).toBe('unknown');
    expect(session.user()).toBeNull();
    expect(session.isSignedIn()).toBe(false);
    expect(session.isAdmin()).toBe(false);
  });

  describe('load', () => {
    it('remembers the user the API names', async () => {
      await loadAs(admin);

      expect(session.status()).toBe('signedIn');
      expect(session.isSignedIn()).toBe(true);
      expect(session.user()).toEqual(admin);
    });

    it('tells admins and members apart', async () => {
      await loadAs(admin);
      expect(session.isAdmin()).toBe(true);

      await loadAs(member);
      expect(session.isAdmin()).toBe(false);
    });

    it('counts the user as signed out when the API says nobody is signed in', async () => {
      const loading = session.load();
      api.expectOne(me).flush(null, unauthorized);
      await loading;

      expect(session.status()).toBe('signedOut');
      expect(session.user()).toBeNull();
    });

    // The app waits for this at startup; a failure here would keep it from starting.
    it('does not fail when the API cannot be reached', async () => {
      const loading = session.load();
      api.expectOne(me).error(new ProgressEvent('error'));

      await expect(loading).resolves.toBeUndefined();
      expect(session.status()).toBe('signedOut');
    });

    it('forgets the user when a later answer says nobody is signed in', async () => {
      await loadAs(admin);

      const loading = session.load();
      api.expectOne(me).flush(null, unauthorized);
      await loading;

      expect(session.isSignedIn()).toBe(false);
      expect(session.user()).toBeNull();
    });
  });

  describe('login', () => {
    it('sends the credentials, then asks the API who is signed in', async () => {
      const signingIn = session.login(credentials);

      const request = api.expectOne(login);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual(credentials);
      request.flush({ token: 'a-token', expiresAt: '2026-10-13T12:00:00Z' });

      await settle();
      api.expectOne(me).flush(admin);
      await signingIn;

      expect(session.isSignedIn()).toBe(true);
      expect(session.user()).toEqual(admin);
    });

    it('fails with the answer of the API when the credentials are rejected, and asks nothing more', async () => {
      const signingIn = session.login(credentials);
      api.expectOne(login).flush({ title: 'Invalid email or password.' }, unauthorized);

      const error = await signingIn.catch((e: unknown) => e);

      expect(error).toBeInstanceOf(HttpErrorResponse);
      expect((error as HttpErrorResponse).status).toBe(401);
      expect(session.isSignedIn()).toBe(false);
    });

    // The API accepted the password, but the browser sends no session cookie back (for
    // example because it refused to store it). The session then says "signed out".
    it('ends signed out when no session follows an accepted login', async () => {
      const signingIn = session.login(credentials);
      api.expectOne(login).flush({ token: 'a-token', expiresAt: '2026-10-13T12:00:00Z' });

      await settle();
      api.expectOne(me).flush(null, unauthorized);
      await signingIn;

      expect(session.status()).toBe('signedOut');
    });
  });

  describe('register', () => {
    const registration = { companyName: 'Örnek Lojistik', ...credentials };

    it('sends the registration, then asks the API who is signed in', async () => {
      const registering = session.register(registration);

      const request = api.expectOne(register);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual(registration);
      request.flush(
        { token: 'a-token', expiresAt: '2026-10-13T12:00:00Z' },
        { status: 201, statusText: 'Created' },
      );

      await settle();
      api.expectOne(me).flush(admin);
      await registering;

      expect(session.isSignedIn()).toBe(true);
      expect(session.isAdmin()).toBe(true);
    });

    it('fails with the answer of the API when the email is taken', async () => {
      const registering = session.register(registration);
      api.expectOne(register).flush({ title: 'taken' }, { status: 409, statusText: 'Conflict' });

      const error = await registering.catch((e: unknown) => e);

      expect((error as HttpErrorResponse).status).toBe(409);
      expect(session.isSignedIn()).toBe(false);
    });
  });

  describe('logout', () => {
    it('asks the API to remove the cookie, then forgets the user', async () => {
      await loadAs(admin);

      const signingOut = session.logout();
      const request = api.expectOne(logout);
      expect(request.request.method).toBe('POST');
      request.flush(null, { status: 204, statusText: 'No Content' });
      await signingOut;

      expect(session.status()).toBe('signedOut');
      expect(session.user()).toBeNull();
    });

    // Only the API can remove the cookie. If it was not reached the cookie is still in
    // the browser, and saying "signed out" would be untrue.
    it('keeps the user signed in when the API could not be reached', async () => {
      await loadAs(admin);

      const signingOut = session.logout();
      api.expectOne(logout).error(new ProgressEvent('error'));

      await expect(signingOut).rejects.toBeInstanceOf(HttpErrorResponse);
      expect(session.isSignedIn()).toBe(true);
      expect(session.user()).toEqual(admin);
    });
  });

  describe('expire', () => {
    it('forgets the user without asking the API', async () => {
      await loadAs(admin);

      session.expire();

      expect(session.status()).toBe('signedOut');
      expect(session.user()).toBeNull();
    });
  });
});
