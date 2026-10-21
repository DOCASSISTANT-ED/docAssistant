import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../environments/environment';
import { Role } from './identity';

// Invitations and members of the API (docs/decisions.md #59, #67): their shapes, written
// by hand after the server's types (Modules/Identity/InvitationEndpoints.cs and
// MemberEndpoints.cs), and the calls. Components use this service; they do not call
// HttpClient themselves.

// How roles are named on screen; the same words as the account menu (shell.ts).
export const roleLabels: Record<Role, string> = { Admin: 'Yönetici', Member: 'Üye' };

// Server: InvitationCreatedResponse. token is the key for the link; the server shows it
// in this answer only and keeps just its hash.
export interface CreatedInvitation {
  id: string;
  token: string;
  role: Role;
  expiresAt: string;
}

// Server: InvitationResponse. An invitation that can still be used.
export interface PendingInvitation {
  id: string;
  role: Role;
  createdAt: string;
  expiresAt: string;
}

// Server: InvitationPreviewResponse. What a link is for, shown before joining.
export interface InvitationPreview {
  companyName: string;
  role: Role;
  expiresAt: string;
}

// Server: AcceptInvitationRequest. The same rules as registration (identityLimits).
export interface AcceptInvitationRequest {
  email: string;
  password: string;
}

// What joining led to. A new account (202, server: JoinedResponse) is not signed in yet:
// its address is verified with an e-mailed code first, and verificationSent says whether
// one went out (decisions #70). An account without an organization coming back with its
// own password (200, #67) is signed in at once: the server set the session cookie.
export interface JoinedInvitation {
  signedIn: boolean;
  verificationSent: boolean;
}

// Server: MemberResponse.
export interface Member {
  userId: string;
  email: string;
  role: Role;
  joinedAt: string;
}

@Injectable({ providedIn: 'root' })
export class InvitationsApi {
  private readonly http = inject(HttpClient);
  private readonly invitationsUrl = `${environment.apiBaseUrl}/invitations`;
  private readonly membersUrl = `${environment.apiBaseUrl}/members`;

  // ---- Admins only ---------------------------------------------------------------------

  create(role: Role): Observable<CreatedInvitation> {
    return this.http.post<CreatedInvitation>(this.invitationsUrl, { role });
  }

  // Invitations that are neither used nor expired, newest first.
  pending(): Observable<PendingInvitation[]> {
    return this.http.get<PendingInvitation[]>(this.invitationsUrl);
  }

  // The link stops working at once. Fails with 404 when it was used in the meantime.
  revoke(id: string): Observable<void> {
    return this.http.delete<void>(`${this.invitationsUrl}/${id}`);
  }

  // Everyone in the organization, in the order they joined.
  members(): Observable<Member[]> {
    return this.http.get<Member[]>(this.membersUrl);
  }

  // The membership goes; the account and its conversations stay. Fails with 409 for
  // yourself and for the last admin.
  removeMember(userId: string): Observable<void> {
    return this.http.delete<void>(`${this.membersUrl}/${userId}`);
  }

  // ---- Whoever has the link, without signing in ------------------------------------------

  // The key goes in the path, encoded: it is base64url, but the app does not rely on
  // what an address it was given contains.

  // Fails with 404 when the link is unknown, used, revoked or expired.
  preview(token: string): Observable<InvitationPreview> {
    return this.http.get<InvitationPreview>(`${this.invitationsUrl}/${encodeURIComponent(token)}`);
  }

  // Joins. A new account is not signed in: the API e-mails a verification code, and typing
  // it signs the person in (decisions #70). An account without an organization that comes
  // back with its own password is signed in at once (#67); the token in that answer is
  // dropped, as at login. Fails with 404 as preview(), with 409 when the email belongs to
  // another organization's account or the password is not that account's, and with 400
  // for an address outside the company's email rule.
  accept(token: string, request: AcceptInvitationRequest): Observable<JoinedInvitation> {
    return this.http
      .post<{ verificationSent?: boolean }>(
        `${this.invitationsUrl}/${encodeURIComponent(token)}/accept`,
        request,
        { observe: 'response' },
      )
      .pipe(
        map((response) =>
          response.status === 200
            ? { signedIn: true, verificationSent: false }
            : { signedIn: false, verificationSent: response.body?.verificationSent === true },
        ),
      );
  }
}
