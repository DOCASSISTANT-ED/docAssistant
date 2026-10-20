import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { IdentityApi, identityLimits } from '../../api/identity';
import { safeReturnUrl } from '../auth.guards';
import { AuthLayout } from '../auth-layout/auth-layout';
import { Session } from '../session';
import { PendingVerification } from './pending-verification';

// The same rules as the server (EmailVerificationService, docs/decisions.md #70).
const codeLength = 6;
const resendCooldownSeconds = 60;

// Typing the six-digit code that was e-mailed to a new account (decisions #70). The right
// code signs the person in. The address comes from the screen that sent them here
// (PendingVerification); after a reload it is not known, and the screen asks for it.
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
  selector: 'app-verify-email',
  styleUrls: ['../auth-form.css', './verify-email.css'],
  templateUrl: './verify-email.html',
})
export class VerifyEmail {
  private readonly session = inject(Session);
  private readonly api = inject(IdentityApi);
  private readonly pending = inject(PendingVerification);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeInput');

  // Known when another screen handed it over; otherwise the person types it.
  protected readonly knownEmail = this.pending.email();

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: [
      this.knownEmail ?? '',
      [Validators.required, Validators.email, Validators.maxLength(identityLimits.emailMaxLength)],
    ],
    code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]],
  });

  // The six boxes the code is shown in; one input underneath takes the typing.
  protected readonly slots = Array.from({ length: codeLength }, (_, index) => index);
  protected readonly code = signal('');

  protected readonly submitting = signal(false);
  protected readonly resending = signal(false);

  // What went wrong, or what happened, in words for the user; null when nothing.
  protected readonly failure = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);

  // Seconds until a new code can be asked for. A code that went out just before the person
  // arrived starts the wait; when none did, a new one can be asked for at once.
  protected readonly cooldown = signal(
    this.knownEmail && this.pending.codeSent() ? resendCooldownSeconds : 0,
  );

  private timer: ReturnType<typeof setInterval> | undefined;

  constructor() {
    if (this.knownEmail && !this.pending.codeSent()) {
      this.notice.set('Kod şu an gönderilemedi. Birazdan yeni kod isteyin.');
    }

    if (this.cooldown() > 0) {
      this.startTimer();
    }

    inject(DestroyRef).onDestroy(() => clearInterval(this.timer));

    // The code is what the person came to type.
    afterNextRender(() => this.codeInput()?.nativeElement.focus());
  }

  // Digits only, at most six; the sixth sends the code.
  protected typed(input: HTMLInputElement): void {
    const digits = input.value.replace(/\D/g, '').slice(0, codeLength);

    input.value = digits;
    this.code.set(digits);
    this.form.controls.code.setValue(digits);
    this.failure.set(null);

    if (digits.length === codeLength) {
      void this.submit();
    }
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
    this.notice.set(null);

    try {
      await this.session.verifyEmail(this.form.getRawValue());

      // As on the login screen: the API took the code, but the session cookie did not come
      // back (decisions #60), most likely because the browser refused it.
      if (!this.session.isSignedIn()) {
        this.failure.set(noSessionMessage);
        return;
      }

      this.pending.clear();
      await this.router.navigateByUrl(
        safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')),
      );
    } catch (error) {
      this.failure.set(verifyMessageFor(error));
      this.clearCode();
    } finally {
      this.submitting.set(false);
    }
  }

  protected async resend(): Promise<void> {
    if (this.resending() || this.cooldown() > 0) {
      return;
    }

    const email = this.form.controls.email;
    if (email.invalid) {
      email.markAsTouched();
      return;
    }

    this.resending.set(true);
    this.failure.set(null);
    this.notice.set(null);

    try {
      // 204 also when the address has no account: the answer says nothing about it.
      await firstValueFrom(this.api.resendVerification(email.value));

      this.notice.set('Yeni bir kod gönderdik. E-postanızı kontrol edin.');
      this.cooldown.set(resendCooldownSeconds);
      this.startTimer();
      this.clearCode();
    } catch (error) {
      this.failure.set(resendMessageFor(error));
    } finally {
      this.resending.set(false);
    }
  }

  // "0:42"
  protected countdown(seconds: number): string {
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  }

  private startTimer(): void {
    clearInterval(this.timer);

    this.timer = setInterval(() => {
      this.cooldown.update((seconds) => Math.max(seconds - 1, 0));

      if (this.cooldown() === 0) {
        clearInterval(this.timer);
      }
    }, 1000);
  }

  private clearCode(): void {
    const input = this.codeInput()?.nativeElement;
    if (input) {
      input.value = '';
      input.focus();
    }

    this.code.set('');
    this.form.controls.code.reset('');
  }
}

const noSessionMessage =
  'Kod doğrulandı ama oturum açılamadı. Tarayıcınız çerezleri engelliyor olabilir; bu siteye çerez izni verip giriş yapın.';

function verifyMessageFor(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      // Wrong, expired, used or out of attempts: the API does not tell them apart.
      case 400:
        return 'Kod yanlış ya da süresi dolmuş. Yeni kod isteyebilirsiniz.';
      case 403:
        return 'Bu hesap bir firmaya bağlı değil. Firmanızın yöneticisine başvurun.';
      case 0:
        return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';
    }
  }

  return 'Kod doğrulanamadı. Biraz sonra yeniden deneyin.';
}

function resendMessageFor(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      case 503:
        return 'Kod şu an gönderilemedi. Biraz sonra yeniden deneyin.';
      case 0:
        return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';
    }
  }

  return 'Yeni kod istenemedi. Biraz sonra yeniden deneyin.';
}
