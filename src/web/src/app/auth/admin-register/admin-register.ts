import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { IdentityApi, identityLimits } from '../../api/identity';
import { problemOf } from '../../api/problem';
import { AuthLayout } from '../auth-layout/auth-layout';
import { PendingVerification } from '../verify-email/pending-verification';

// XXXX-XXXX-XXXX (RegistrationCode on the server, docs/decisions.md #70).
const codeCharacters = 12;
const codeGroup = 4;

// Registering as one of a company's admins with the company's registration code (decisions
// #70, the API's POST /auth/register-admin). The platform admin opens the company and gives
// the code to it; no list of companies is shown. The account is made unverified, so the next
// step is typing the e-mailed code.
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
  selector: 'app-admin-register',
  styleUrls: ['../auth-form.css', './admin-register.css'],
  templateUrl: './admin-register.html',
})
export class AdminRegister {
  private readonly api = inject(IdentityApi);
  private readonly pending = inject(PendingVerification);
  private readonly router = inject(Router);

  protected readonly limits = identityLimits;

  // The same rules as the server (AdminRegistrationRequest); the server still checks
  // everything itself.
  protected readonly form = inject(NonNullableFormBuilder).group({
    registrationCode: [
      '',
      [Validators.required, Validators.pattern(/^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$/)],
    ],
    email: [
      '',
      [Validators.required, Validators.email, Validators.maxLength(identityLimits.emailMaxLength)],
    ],
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

  // The domains of the company's email rule, when the server named them for a rejected
  // address; shown under the email field.
  protected readonly emailDomains = signal<string[]>([]);

  // Upper case, letters and digits only, a dash every four: typed or pasted, the code always
  // looks the way the platform admin's screen shows it.
  protected formatCode(input: HTMLInputElement): void {
    const characters = input.value
      .toUpperCase()
      .replace(/[^A-Z0-9]/g, '')
      .slice(0, codeCharacters);
    const groups = characters.match(new RegExp(`.{1,${codeGroup}}`, 'g')) ?? [];
    const formatted = groups.join('-');

    input.value = formatted;
    this.form.controls.registrationCode.setValue(formatted);
  }

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
    this.emailDomains.set([]);

    try {
      const request = this.form.getRawValue();
      const answer = await firstValueFrom(this.api.registerAdmin(request));

      // On to the code that was just e-mailed; when it could not be sent, the next screen
      // says so and offers a new one.
      this.pending.start(request.email, answer.verificationSent);
      await this.router.navigate(['/dogrula'], { queryParamsHandling: 'preserve' });
    } catch (error) {
      this.failure.set(this.explain(error));
    } finally {
      this.submitting.set(false);
    }
  }

  // Turns the API's answer into a message, and marks the field it is about.
  private explain(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) {
      return 'Kayıt oluşturulamadı. Biraz sonra yeniden deneyin.';
    }

    switch (error.status) {
      case 409:
        this.form.controls.email.setErrors({ taken: true });
        return 'Bu e-posta adresiyle zaten bir hesap var.';

      case 400:
        return this.explainRejected(error);

      case 0:
        return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';

      default:
        return 'Kayıt oluşturulamadı. Biraz sonra yeniden deneyin.';
    }
  }

  // The server's 400 is one of three: fields it did not accept (errors), an address outside
  // the company's domains (emailDomains), or a code that belongs to no company (neither).
  private explainRejected(error: HttpErrorResponse): string {
    const problem = problemOf(error) as {
      errors?: Record<string, string[]>;
      emailDomains?: unknown;
    } | null;

    const domains = Array.isArray(problem?.emailDomains)
      ? problem.emailDomains.filter((domain): domain is string => typeof domain === 'string')
      : [];
    if (domains.length > 0) {
      this.emailDomains.set(domains);
      this.form.controls.email.setErrors({ domain: true });
      return `Bu firmaya yalnızca ${domains.map((domain) => '@' + domain).join(', ')} adresleriyle kaydolabilirsiniz.`;
    }

    const rejected = Object.keys(problem?.errors ?? {});
    if (rejected.length > 0) {
      // The server names fields its own way ("RegistrationCode"); ours start lower case.
      for (const field of rejected) {
        const name = field.charAt(0).toLowerCase() + field.slice(1);

        if (name === 'registrationCode' || name === 'email' || name === 'password') {
          this.form.controls[name].setErrors({ server: true });
        }
      }

      return 'Bazı bilgiler kabul edilmedi. İşaretli alanları kontrol edin.';
    }

    this.form.controls.registrationCode.setErrors({ unknown: true });
    return 'Kayıt kodu geçersiz. Kodu sistem yöneticinizden yeniden isteyin.';
  }
}
