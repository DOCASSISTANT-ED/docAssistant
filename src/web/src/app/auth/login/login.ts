import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { identityLimits } from '../../api/identity';
import { problemOf } from '../../api/problem';
import { safeReturnUrl } from '../auth.guards';
import { AuthLayout } from '../auth-layout/auth-layout';
import { Session } from '../session';
import { PendingVerification } from '../verify-email/pending-verification';

@Component({
  imports: [
    AuthLayout,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    ReactiveFormsModule,
    RouterLink,
  ],
  selector: 'app-login',
  styleUrl: '../auth-form.css',
  templateUrl: './login.html',
})
export class Login {
  private readonly session = inject(Session);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly pending = inject(PendingVerification);

  // The same limits as the server (LoginRequest): anything longer would be rejected.
  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(identityLimits.emailMaxLength)]],
    password: ['', [Validators.required, Validators.maxLength(identityLimits.passwordMaxLength)]],
  });

  // True while the request is on its way: the button waits and cannot be pressed twice.
  protected readonly submitting = signal(false);

  // What went wrong with the last attempt, in words for the user; null when nothing did.
  protected readonly failure = signal<string | null>(null);

  protected readonly passwordVisible = signal(false);

  protected async submit(): Promise<void> {
    if (this.submitting()) {
      return;
    }

    if (this.form.invalid) {
      // Shows the message under every field that is still wrong.
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.failure.set(null);

    try {
      await this.session.login(this.form.getRawValue());

      // The API took the password, but no session followed: the session cookie did not
      // come back (decisions #60), most likely because the browser refused to keep it.
      // Moving on would only bring the user back here without a word.
      if (!this.session.isSignedIn()) {
        this.failure.set(noSessionMessage);
        return;
      }

      // Back to the screen the user was sent here from, if any (auth.guards.ts).
      await this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
    } catch (error) {
      // The password was right, but the address is not verified yet (decisions #70): on to
      // typing the code, which the API has just sent when it could. The page asked for
      // before signing in (returnUrl) goes along.
      const unverified = unverifiedAccount(error);
      if (unverified) {
        this.pending.start(this.form.controls.email.value, unverified.codeSent);
        await this.router.navigate(['/dogrula'], { queryParamsHandling: 'preserve' });
        return;
      }

      this.failure.set(messageFor(error));
    } finally {
      this.submitting.set(false);
    }
  }
}

const noSessionMessage =
  'Giriş yapılamadı: oturum açılamadı. Tarayıcınız çerezleri engelliyor olabilir; bu siteye çerez izni verip yeniden deneyin.';

function messageFor(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      // The API gives the same answer for an unknown email and a wrong password, so that
      // nobody can find out which emails are registered.
      case 401:
        return 'E-posta ya da parola hatalı.';
      case 403:
        // decisions #67: the account stays when its person is removed from the organization;
        // a new invitation link brings it back (with its own password).
        return 'Bu hesap artık bir firmaya bağlı değil. Yeniden katılmak için firmanızın yöneticisinden yeni bir davet bağlantısı isteyin.';
      // Status 0: no answer at all (the API is down or the network is).
      case 0:
        return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';
    }
  }

  return 'Giriş yapılamadı. Biraz sonra yeniden deneyin.';
}

// The API's 403 for an account whose address is not verified (decisions #70), with whether it
// sent a new code; null for any other error, including the 403 of an account in no company.
function unverifiedAccount(error: unknown): { codeSent: boolean } | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 403) {
    return null;
  }

  const problem = problemOf(error) as { emailNotVerified?: unknown; verificationSent?: unknown } | null;

  return problem?.emailNotVerified === true ? { codeSent: problem.verificationSent === true } : null;
}
