import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { identityLimits } from '../../api/identity';
import { InvitationPreview, InvitationsApi, roleLabels } from '../../api/invitations';
import { problemOf } from '../../api/problem';
import { AuthLayout } from '../auth-layout/auth-layout';
import { PendingVerification } from '../verify-email/pending-verification';

// What the screen shows: the link is being looked at, it can be used, it cannot be used
// (unknown, used, revoked or expired: the API does not say which), or the API did not
// answer.
type LinkState =
  | { kind: 'loading' }
  | { kind: 'usable'; preview: InvitationPreview }
  | { kind: 'unusable' }
  | { kind: 'unreachable' };

// /davet/<key>: joining an organization with an invitation link (docs/decisions.md #67).
// The screen first shows which organization and role the link is for, then creates the
// account with an email and a password, and sends the person on to type the code e-mailed
// to that address (#70); the code signs them in.
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
  selector: 'app-join',
  styleUrls: ['../auth-form.css', './join.css'],
  templateUrl: './join.html',
})
export class Join implements OnInit {
  private readonly api = inject(InvitationsApi);
  private readonly pending = inject(PendingVerification);
  private readonly router = inject(Router);

  // The key, from the address (the route's :token parameter).
  private readonly token = inject(ActivatedRoute).snapshot.paramMap.get('token') ?? '';

  protected readonly limits = identityLimits;
  protected readonly roleLabels = roleLabels;

  // The same rules as the server (AcceptInvitationRequest, as for registration).
  protected readonly form = inject(NonNullableFormBuilder).group({
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

  protected readonly link = signal<LinkState>({ kind: 'loading' });

  // The link's details while it can be used.
  protected readonly preview = computed(() => {
    const link = this.link();
    return link.kind === 'usable' ? link.preview : null;
  });
  protected readonly submitting = signal(false);
  protected readonly failure = signal<string | null>(null);
  protected readonly passwordVisible = signal(false);

  async ngOnInit(): Promise<void> {
    await this.lookUp();
  }

  protected async lookUp(): Promise<void> {
    this.link.set({ kind: 'loading' });

    try {
      const preview = await firstValueFrom(this.api.preview(this.token));
      this.link.set({ kind: 'usable', preview });
    } catch (error) {
      this.link.set({
        kind:
          error instanceof HttpErrorResponse && error.status === 404 ? 'unusable' : 'unreachable',
      });
    }
  }

  protected expiry(preview: InvitationPreview): string {
    return new Intl.DateTimeFormat('tr-TR', { dateStyle: 'long' }).format(
      new Date(preview.expiresAt),
    );
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

    try {
      const request = this.form.getRawValue();
      const joined = await firstValueFrom(this.api.accept(this.token, request));

      // decisions #70: the account is made and the link used up, but the address has to be
      // proved first. The code was e-mailed; typing it on the verification screen signs the
      // person in. The address goes along in memory (PendingVerification), as from login.
      this.pending.start(request.email, joined.verificationSent);
      await this.router.navigateByUrl('/dogrula');
    } catch (error) {
      this.explain(error);
    } finally {
      this.submitting.set(false);
    }
  }

  // Turns the API's answer into a message, and marks the fields the server rejected.
  private explain(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.failure.set('Katılım tamamlanamadı. Biraz sonra yeniden deneyin.');
      return;
    }

    switch (error.status) {
      // Used, revoked or expired while the form was being filled in.
      case 404:
        this.link.set({ kind: 'unusable' });
        return;

      case 409:
        this.form.controls.email.setErrors({ taken: true });
        this.failure.set(
          'Bu e-posta adresiyle zaten bir hesap var. Bir kişi yalnızca bir firmaya katılabilir.',
        );
        return;

      case 400: {
        // The company takes only its own addresses (decisions #70); the answer names them.
        const domains = emailDomainsOf(error);
        if (domains.length > 0) {
          this.form.controls.email.setErrors({ domain: true });
          this.failure.set(
            `Bu firmaya yalnızca ${domains.map((domain) => '@' + domain).join(', ')} uzantılı e-posta adresleriyle katılabilirsiniz.`,
          );
          return;
        }

        // The server names fields its own way ("Email"); ours start lower case.
        for (const field of Object.keys(problemOf(error)?.errors ?? {})) {
          const name = field.charAt(0).toLowerCase() + field.slice(1);

          if (name === 'email' || name === 'password') {
            this.form.controls[name].setErrors({ server: true });
          }
        }

        this.failure.set('Bazı bilgiler kabul edilmedi. İşaretli alanları kontrol edin.');
        return;
      }

      case 0:
        this.failure.set('Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.');
        return;

      default:
        this.failure.set('Katılım tamamlanamadı. Biraz sonra yeniden deneyin.');
    }
  }
}

// The domains a company with an email rule takes, from the API's answer to a join with
// another address (decisions #70); empty for any other answer.
function emailDomainsOf(error: HttpErrorResponse): string[] {
  const domains = (problemOf(error) as { emailDomains?: unknown } | null)?.emailDomains;

  return Array.isArray(domains)
    ? domains.filter((domain): domain is string => typeof domain === 'string')
    : [];
}
