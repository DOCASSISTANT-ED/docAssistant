import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { CurrentUser, IdentityApi } from './identity';

const base = `${environment.apiBaseUrl}/auth`;
const token = { token: 'a-secret-token', expiresAt: '2026-10-13T12:00:00Z' };

describe('IdentityApi', () => {
  let identity: IdentityApi;
  let api: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    identity = TestBed.inject(IdentityApi);
    api = TestBed.inject(HttpTestingController);
  });

  afterEach(() => api.verify());

  // docs/decisions.md #60: the API also answers with the token in the body, and the app
  // keeps it nowhere. Callers of this service never get to see it.
  it('drops the token of a login answer', async () => {
    const answer = firstValueFrom(
      identity.login({ email: 'ayse@example.com', password: 'Gizli123!' }),
    );

    const request = api.expectOne(`${base}/login`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'ayse@example.com', password: 'Gizli123!' });
    request.flush(token);

    expect(await answer).toBeUndefined();
  });

  it('drops the token of a registration answer', async () => {
    const registration = {
      companyName: 'Örnek Lojistik',
      email: 'ayse@example.com',
      password: 'Gizli123!',
    };
    const answer = firstValueFrom(identity.register(registration));

    const request = api.expectOne(`${base}/register`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(registration);
    request.flush(token, { status: 201, statusText: 'Created' });

    expect(await answer).toBeUndefined();
  });

  it('posts to the logout address without a body', async () => {
    const answer = firstValueFrom(identity.logout(), { defaultValue: 'done' });

    const request = api.expectOne(`${base}/logout`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBeNull();
    request.flush(null, { status: 204, statusText: 'No Content' });

    await answer;
  });

  it('gives back the user the API names', async () => {
    const user: CurrentUser = {
      userId: 'u-1',
      email: 'ayse@example.com',
      tenantId: 't-1',
      role: 'Member',
    };
    const answer = firstValueFrom(identity.currentUser());

    const request = api.expectOne(`${base}/me`);
    expect(request.request.method).toBe('GET');
    request.flush(user);

    expect(await answer).toEqual(user);
  });
});
