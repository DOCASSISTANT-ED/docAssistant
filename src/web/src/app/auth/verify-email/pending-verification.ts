import { Injectable, signal } from '@angular/core';

// The account waiting for its e-mail code (docs/decisions.md #70), handed from the screen
// that sent the person here (login, admin registration, joining by invitation) to the
// verification screen. Kept in memory only, never in the address bar or the browser's
// storage, so the address stays out of history and server logs. A reload forgets it, and
// the verification screen then asks for the address itself.
@Injectable({ providedIn: 'root' })
export class PendingVerification {
  private readonly pendingEmail = signal<string | null>(null);
  private readonly pendingCodeSent = signal(false);

  // The address the code went to; null when no screen has handed one over.
  readonly email = this.pendingEmail.asReadonly();

  // Whether a code went out just before the person arrived. When not (the e-mail could not
  // be sent, or one went out a moment ago), the screen offers a new one at once.
  readonly codeSent = this.pendingCodeSent.asReadonly();

  start(email: string, codeSent: boolean): void {
    this.pendingEmail.set(email.trim());
    this.pendingCodeSent.set(codeSent);
  }

  // Called once the address is verified, so nothing of it stays behind.
  clear(): void {
    this.pendingEmail.set(null);
    this.pendingCodeSent.set(false);
  }
}
