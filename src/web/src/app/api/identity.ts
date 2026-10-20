import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../environments/environment';

// The identity module of the API (docs/decisions.md #59): its request and response
// shapes, written by hand after the server's types (Modules/Identity), and the calls.
// Components use this service; they do not call HttpClient themselves.

// Server: RegisterRequest. Creates an organization and its first admin.
export interface RegisterRequest {
  companyName: string;
  email: string;
  password: string;
}

// Server: LoginRequest.
export interface LoginRequest {
  email: string;
  password: string;
}

// Server: VerifyEmailRequest (docs/decisions.md #70). The six digits from the e-mail;
// spaces are allowed.
export interface VerifyEmailRequest {
  email: string;
  code: string;
}

// Server: MembershipRole. Admins upload documents; members ask questions.
export type Role = 'Admin' | 'Member';

// Server: CurrentUserResponse, for a signed-in user. The platform admin (decisions #70)
// belongs to no organization: tenantId is null and the role is 'PlatformAdmin'.
export interface CurrentUser {
  userId: string;
  email: string;
  tenantId: string | null;
  role: Role | 'PlatformAdmin';
}

// The limits the server validates (AuthRequests.cs), so that forms can say the same
// before sending. The server stays the one that decides.
export const identityLimits = {
  companyNameMaxLength: 200,
  emailMaxLength: 254,
  passwordMinLength: 8,
  passwordMaxLength: 128,
} as const;

@Injectable({ providedIn: 'root' })
export class IdentityApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/auth`;

  // Register and login also answer with the token in the body. It is dropped right here,
  // so no caller can keep it: in a browser the session travels in an HttpOnly cookie
  // the server sets, and the app keeps the token nowhere (decisions #60).

  register(request: RegisterRequest): Observable<void> {
    return this.http.post<unknown>(`${this.baseUrl}/register`, request).pipe(map(() => undefined));
  }

  login(request: LoginRequest): Observable<void> {
    return this.http.post<unknown>(`${this.baseUrl}/login`, request).pipe(map(() => undefined));
  }

  // Typing the code from the e-mail (decisions #70). The right code verifies the account and
  // signs the user in, as login does; any other code fails with 400.
  verifyEmail(request: VerifyEmailRequest): Observable<void> {
    return this.http.post<unknown>(`${this.baseUrl}/verify-email`, request).pipe(map(() => undefined));
  }

  // Asks for a new code. Answers 204 whether or not the address has an account, and when
  // it is too soon for another code; fails with 503 only when the e-mail could not be sent.
  resendVerification(email: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/verify-email/resend`, { email });
  }

  // Removes the session cookie; succeeds also when nobody is signed in.
  logout(): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/logout`, null);
  }

  // Fails with 401 when nobody is signed in or the session has expired.
  currentUser(): Observable<CurrentUser> {
    return this.http.get<CurrentUser>(`${this.baseUrl}/me`);
  }
}
