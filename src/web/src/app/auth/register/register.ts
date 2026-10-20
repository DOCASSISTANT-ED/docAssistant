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

// Registers a new organization together with its first user, who becomes its admin
// (the API's POST /auth/register), and signs that user in.
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
  selector: 'app-register',
  styleUrl: '../auth-form.css',
  templateUrl: './register.html',
})
export class Register {
  private readonly session = inject(Session);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly limits = identityLimits;

  // The same rules as the server (RegisterRequest), so most mistakes are caught before
  // the request is sent. The server still checks everything itself.
  protected readonly form = inject(NonNullableFormBuilder).group({
    companyName: ['', [Validators.required, Validators.maxLength(identityLimits.companyNameMaxLength)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(identityLimits.emailMaxLength)]],
    password: [
      '',
      [
        Validators.required,
        Validators.minLength(identityLimits.passwordMinLength),
        Validators.maxLength(identityLimits.passwordMaxLength),
      ],
    ],
  });

  protected readonly submitting = signal(false);
  protected readonly failure = signal<string | null>(null);
  protected readonly passwordVisible = signal(false);

  // Open registration is off on this server (decisions #70); the screen points to the
  // registration with a company's code instead.
  protected readonly closed = signal(false);

  protected async submit(): Promise<void> {
    if (this.submitting()) {
      return;
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.failure.set(null);

    try {
      await this.session.register(this.form.getRawValue());

      // The account was created, but no session followed: the session cookie did not
      // come back (decisions #60), most likely because the browser refused to keep it.
      // Registering again would fail (the email is taken), so the user is told so.
      if (!this.session.isSignedIn()) {
        this.failure.set(
          'Hesabınız oluşturuldu ama oturum açılamadı. Tarayıcınız çerezleri engelliyor olabilir; bu siteye çerez izni verip giriş yapın.',
        );
        return;
      }

      // Back to the screen the user was sent here from, if any (auth.guards.ts).
      await this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
    } catch (error) {
      this.failure.set(this.explain(error));
    } finally {
      this.submitting.set(false);
    }
  }

  // Turns the API's answer into a message, and marks the fields the server rejected.
  private explain(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) {
      return 'Kayıt oluşturulamadı. Biraz sonra yeniden deneyin.';
    }

    switch (error.status) {
      case 409:
        this.form.controls.email.setErrors({ taken: true });
        return 'Bu e-posta adresiyle zaten bir hesap var.';

      case 400: {
        // The server names fields its own way ("CompanyName"); ours start lower case.
        const rejected = Object.keys(problemOf(error)?.errors ?? {});

        for (const field of rejected) {
          const name = field.charAt(0).toLowerCase() + field.slice(1);

          if (name === 'companyName' || name === 'email' || name === 'password') {
            this.form.controls[name].setErrors({ server: true });
          }
        }

        return 'Bazı bilgiler kabul edilmedi. İşaretli alanları kontrol edin.';
      }

      // Open registration is off here (Auth:OpenRegistration, decisions #70): the endpoint is
      // not there at all.
      case 404:
        this.closed.set(true);
        return 'Bu ortamda yeni firma açılamıyor. Firmanızın kayıt koduyla kaydolun.';

      case 0:
        return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';

      default:
        return 'Kayıt oluşturulamadı. Biraz sonra yeniden deneyin.';
    }
  }
}
