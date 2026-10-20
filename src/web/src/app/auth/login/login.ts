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
        return (
          unverifiedMessage(error) ??
          'Bu hesap bir firmaya bağlı değil. Firmanızın yöneticisine başvurun.'
        );
      // Status 0: no answer at all (the API is down or the network is).
      case 0:
        return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';
    }
  }

  return 'Giriş yapılamadı. Biraz sonra yeniden deneyin.';
}

// The password was right, but the address is not verified yet (decisions #70); the API sends
// a new code with this answer when it may. Null for any other 403. The screen for typing the
// code comes later; until then the message says where the code is.
function unverifiedMessage(error: HttpErrorResponse): string | null {
  const problem = problemOf(error) as {
    emailNotVerified?: unknown;
    verificationSent?: unknown;
  } | null;

  if (problem?.emailNotVerified !== true) {
    return null;
  }

  return problem.verificationSent === true
    ? 'E-posta adresiniz henüz doğrulanmadı. Adresinize yeni bir doğrulama kodu gönderdik.'
    : 'E-posta adresiniz henüz doğrulanmadı. Doğrulama kodunu e-postanızda bulamıyorsanız biraz sonra yeniden giriş yapmayı deneyin.';
}
