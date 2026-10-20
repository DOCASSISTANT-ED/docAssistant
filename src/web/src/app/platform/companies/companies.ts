import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Company, PlatformApi, companyLimits } from '../../api/platform';
import { problemOf } from '../../api/problem';
import { Session } from '../../auth/session';

// Whether the list could be shown.
type ListState = 'loading' | 'ready' | 'failed';

// The company's email rule, as chosen on the form (docs/decisions.md #70).
type EmailRule = 'domains' | 'none';

// A registration code the server has just shown. It is in no later answer, so the page
// keeps it only until it is closed or the page is left.
interface ShownCode {
  companyName: string;
  code: string;
  renewed: boolean;
}

// The platform admin's screen (decisions #70): registering companies, their email rule
// and their registration codes. Only the platform admin gets here (platformAdminOnly);
// the API refuses everyone else anyway.
@Component({
  selector: 'app-companies',
  styleUrl: './companies.css',
  templateUrl: './companies.html',
})
export class Companies {
  private readonly api = inject(PlatformApi);
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  protected readonly limits = companyLimits;
  protected readonly email = computed(() => this.session.user()?.email ?? '');

  protected readonly companies = signal<Company[]>([]);
  protected readonly listState = signal<ListState>('loading');

  // ---- The form ----

  protected readonly name = signal('');
  protected readonly emailRule = signal<EmailRule>('domains');
  protected readonly domainsText = signal('');
  protected readonly saving = signal(false);

  // Problems with what was typed, shown under the field they belong to.
  protected readonly nameError = signal<string | null>(null);
  protected readonly domainsError = signal<string | null>(null);

  // ---- Codes ----

  protected readonly shown = signal<ShownCode | null>(null);
  protected readonly copied = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly renewing = signal<string | null>(null);

  // What went wrong with the last action, in words for the user.
  protected readonly failure = signal<string | null>(null);

  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
    void this.load();
  }

  protected reload(): void {
    this.listState.set('loading');
    void this.load();
  }

  // ---- Registering a company ----

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }

    this.failure.set(null);
    this.nameError.set(null);
    this.domainsError.set(null);

    const name = this.name().trim();
    const emailDomains = this.emailRule() === 'domains' ? splitDomains(this.domainsText()) : [];

    if (name.length === 0) {
      this.nameError.set('Firmanın adını girin.');
    }

    if (this.emailRule() === 'domains') {
      if (emailDomains.length === 0) {
        this.domainsError.set(
          'En az bir alan adı girin ya da "Firmanın kendi e-postası yok"u seçin.',
        );
      } else if (emailDomains.length > companyLimits.maxEmailDomains) {
        this.domainsError.set(`En çok ${companyLimits.maxEmailDomains} alan adı girilebilir.`);
      } else if (emailDomains.some((domain) => domain.includes('@'))) {
        this.domainsError.set('Yalnızca @ işaretinden sonraki kısmı yazın: afirma.com gibi.');
      }
    }

    if (this.nameError() || this.domainsError()) {
      return;
    }

    this.saving.set(true);
    this.copied.set(false);

    try {
      const created = await firstValueFrom(this.api.create({ name, emailDomains }));

      this.shown.set({
        companyName: created.company.name,
        code: created.registrationCode,
        renewed: false,
      });
      this.companies.update((companies) => sortByName([...companies, created.company]));
      this.name.set('');
      this.domainsText.set('');
    } catch (error) {
      this.explainSaveFailure(error);
    } finally {
      this.saving.set(false);
    }
  }

  // ---- Codes ----

  protected askToRenew(companyId: string): void {
    this.failure.set(null);
    this.confirming.set(companyId);
  }

  protected cancelRenew(): void {
    this.confirming.set(null);
  }

  // A company registered before #70 has no code yet; the same request makes its first.
  protected async renew(company: Company): Promise<void> {
    if (this.renewing() !== null) {
      return;
    }

    this.confirming.set(null);
    this.renewing.set(company.id);
    this.failure.set(null);
    this.copied.set(false);

    try {
      const renewed = await firstValueFrom(this.api.renewCode(company.id));

      this.shown.set({
        companyName: company.name,
        code: renewed.registrationCode,
        renewed: company.hasRegistrationCode,
      });
      this.companies.update((companies) =>
        companies.map((c) => (c.id === company.id ? { ...c, hasRegistrationCode: true } : c)),
      );
    } catch (error) {
      this.failure.set(failureFor(error, `${company.name} için kayıt kodu oluşturulamadı.`));
    } finally {
      this.renewing.set(null);
    }
  }

  // The Clipboard API works only on a secure page (https, or localhost) and may be refused
  // by the browser; the code stays on screen to copy by hand.
  protected async copyCode(code: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(code);
      this.copied.set(true);
    } catch {
      this.failure.set('Kod kopyalanamadı. Kutudaki kodu seçip elle kopyalayın.');
    }
  }

  protected closeShown(): void {
    this.shown.set(null);
    this.copied.set(false);
  }

  // ---- Signing out ----

  protected async logout(): Promise<void> {
    try {
      await this.session.logout();
      await this.router.navigateByUrl('/login');
    } catch {
      this.failure.set('Çıkış yapılamadı. Bağlantınızı kontrol edip yeniden deneyin.');
    }
  }

  // ---- Showing ----

  protected domainsLabel(company: Company): string {
    return company.emailDomains.length === 0
      ? 'Kendi e-postası yok'
      : company.emailDomains.map((domain) => '@' + domain).join(', ');
  }

  protected dateLabel(value: string): string {
    return new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium' }).format(new Date(value));
  }

  // ---- Internals ----

  private async load(): Promise<void> {
    try {
      const companies = await firstValueFrom(this.api.companies());

      if (!this.destroyed) {
        this.companies.set(companies);
        this.listState.set('ready');
      }
    } catch {
      if (!this.destroyed) {
        this.listState.set('failed');
      }
    }
  }

  private explainSaveFailure(error: unknown): void {
    if (error instanceof HttpErrorResponse && error.status === 409) {
      this.nameError.set('Bu adla bir firma kayıtlı.');
      return;
    }

    if (error instanceof HttpErrorResponse && error.status === 400) {
      // The server names the domain it did not take (invalidDomain).
      const invalid = (problemOf(error) as { invalidDomain?: unknown } | null)?.invalidDomain;
      this.domainsError.set(
        typeof invalid === 'string'
          ? `"${invalid}" bir alan adı değil. afirma.com biçiminde yazın; Türkçe harfli alan adları xn-- biçimiyle yazılır.`
          : 'Bazı bilgiler kabul edilmedi. Alanları kontrol edin.',
      );
      return;
    }

    this.failure.set(failureFor(error, 'Firma kaydedilemedi.'));
  }
}

// What was typed in the domains box: separated by commas, semicolons or spaces, each once.
export function splitDomains(text: string): string[] {
  const domains = text
    .split(/[\s,;]+/)
    .map((domain) => domain.trim().replace(/^@/, '').toLowerCase())
    .filter((domain) => domain.length > 0);

  return [...new Set(domains)];
}

function sortByName(companies: Company[]): Company[] {
  return companies.sort((a, b) => a.name.localeCompare(b.name, 'tr'));
}

// The sentence shown when an action fails.
function failureFor(error: unknown, action: string): string {
  if (error instanceof HttpErrorResponse && error.status === 0) {
    return `${action} Sunucuya ulaşılamıyor; bağlantınızı kontrol edip yeniden deneyin.`;
  }

  return `${action} Biraz sonra yeniden deneyin.`;
}
