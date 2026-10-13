import { HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { signedInOnly } from '../auth.guards';
import { Session } from '../session';
import { Register } from './register';

@Component({ template: '' })
class Blank {}

// The register screen, driven the way a user drives it (see login.spec.ts). The session
// is a stand-in; what the API does with a registration is covered in session.spec.ts.
describe('Register', () => {
  let harness: RouterTestingHarness;
  let screen: HTMLElement;
  let router: Router;

  let signedIn: boolean;
  let register: ReturnType<typeof vi.fn>;

  async function open(url = '/register'): Promise<void> {
    signedIn = false;
    register = vi.fn(async () => {
      signedIn = true;
    });

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'register', component: Register },
          { path: 'login', component: Blank },
          { path: 'belgeler', component: Blank, canActivate: [signedInOnly] },
          { path: '', pathMatch: 'full', component: Blank, canActivate: [signedInOnly] },
        ]),
        { provide: Session, useValue: { register, isSignedIn: () => signedIn } },
      ],
    });

    harness = await RouterTestingHarness.create(url);
    screen = harness.routeNativeElement!;
    router = TestBed.inject(Router);
  }

  async function settle(): Promise<void> {
    await new Promise<void>((resolve) => setTimeout(resolve));
    harness.detectChanges();
  }

  function field(name: string): HTMLInputElement {
    return screen.querySelector<HTMLInputElement>(`input[formcontrolname="${name}"]`)!;
  }

  function type(name: string, value: string): void {
    field(name).value = value;
    field(name).dispatchEvent(new Event('input'));
  }

  function fillIn(values: { companyName?: string; email?: string; password?: string } = {}): void {
    type('companyName', values.companyName ?? 'Örnek Lojistik');
    type('email', values.email ?? 'ayse@example.com');
    type('password', values.password ?? 'Gizli123!');
  }

  async function submit(): Promise<void> {
    screen.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
  }

  const submitButton = () => screen.querySelector<HTMLButtonElement>('button[type="submit"]')!;
  const failure = () => screen.querySelector('[role="alert"]')?.textContent?.trim() ?? null;
  const fieldErrors = () =>
    Array.from(screen.querySelectorAll('mat-error')).map((error) => error.textContent?.trim());

  // `errors` is what the API lists on a 400: one entry per rejected field, named the
  // server's way ("CompanyName").
  function rejectWith(status: number, errors?: Record<string, string[]>): void {
    register.mockRejectedValueOnce(
      new HttpErrorResponse({ status, error: errors ? { status, errors } : null }),
    );
  }

  describe('registering', () => {
    it('registers with what was typed and goes home', async () => {
      await open();
      fillIn({ companyName: 'Örnek Lojistik', email: 'ayse@example.com', password: 'Gizli123!' });

      await submit();

      expect(register).toHaveBeenCalledExactlyOnceWith({
        companyName: 'Örnek Lojistik',
        email: 'ayse@example.com',
        password: 'Gizli123!',
      });
      expect(router.url).toBe('/');
      expect(failure()).toBeNull();
    });

    it('goes back to the screen the user was sent here from', async () => {
      await open('/register?returnUrl=%2Fbelgeler');
      fillIn();

      await submit();

      expect(router.url).toBe('/belgeler');
    });

    it('goes home when the address to return to is not one of the app', async () => {
      await open('/register?returnUrl=https%3A%2F%2Fbaska-site.example');
      fillIn();

      await submit();

      expect(router.url).toBe('/');
    });
  });

  describe('a form that is not ready', () => {
    it('sends nothing and says what is missing when the form is empty', async () => {
      await open();

      await submit();

      expect(register).not.toHaveBeenCalled();
      expect(fieldErrors()).toEqual([
        'Firmanızın adını girin.',
        'E-posta adresinizi girin.',
        'Bir parola belirleyin.',
      ]);
    });

    // The limits are the server's (RegisterRequest), so the user hears about them
    // before the request is sent.
    it.each([
      [{ companyName: 'a'.repeat(201) }, 'Firma adı en çok 200 karakter olabilir.'],
      [{ email: 'ayse-at-example' }, 'Geçerli bir e-posta adresi girin.'],
      [{ password: 'Kisa123' }, 'Parola en az 8 karakter olmalı.'],
      [{ password: 'a'.repeat(129) }, 'Parola en çok 128 karakter olabilir.'],
    ])('sends nothing for %o', async (values, message) => {
      await open();
      fillIn(values);

      await submit();

      expect(register).not.toHaveBeenCalled();
      expect(fieldErrors()).toEqual([message]);
    });

    it.each([
      { companyName: 'a'.repeat(200) },
      { password: 'Sekiz123' },
      { password: 'a'.repeat(128) },
    ])('accepts a value right at the limit: %o', async (values) => {
      await open();
      fillIn(values);

      await submit();

      expect(register).toHaveBeenCalledOnce();
    });

    it('tells the user the shortest password before they try', async () => {
      await open();

      expect(screen.querySelector('mat-hint')?.textContent?.trim()).toBe('En az 8 karakter.');
      expect(fieldErrors()).toEqual([]);
    });
  });

  describe('a rejected attempt', () => {
    it('says the email is taken, at the top and under the field', async () => {
      await open();
      rejectWith(409);
      fillIn();

      await submit();

      expect(failure()).toBe('Bu e-posta adresiyle zaten bir hesap var.');
      expect(fieldErrors()).toEqual(['Bu adres kullanılıyor.']);
      expect(router.url).toBe('/register');
    });

    // The server has the last word: it can reject what the form let through.
    it('marks the fields the server rejected', async () => {
      await open();
      rejectWith(400, { CompanyName: ['too long'], Password: ['too short'] });
      fillIn();

      await submit();

      expect(failure()).toBe('Bazı bilgiler kabul edilmedi. İşaretli alanları kontrol edin.');
      expect(fieldErrors()).toEqual(['Bu firma adı kabul edilmedi.', 'Bu parola kabul edilmedi.']);
    });

    it('marks a rejected email', async () => {
      await open();
      rejectWith(400, { Email: ['invalid'] });
      fillIn();

      await submit();

      expect(fieldErrors()).toEqual(['Geçerli bir e-posta adresi girin.']);
    });

    it('copes with a 400 that names no field of the form', async () => {
      await open();
      rejectWith(400, { Something: ['else'] });
      fillIn();

      await submit();

      expect(failure()).toBe('Bazı bilgiler kabul edilmedi. İşaretli alanları kontrol edin.');
      expect(fieldErrors()).toEqual([]);
    });

    it('copes with a 400 without problem details', async () => {
      await open();
      rejectWith(400);
      fillIn();

      await submit();

      expect(failure()).toBe('Bazı bilgiler kabul edilmedi. İşaretli alanları kontrol edin.');
    });

    it.each([
      [0, 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.'],
      [500, 'Kayıt oluşturulamadı. Biraz sonra yeniden deneyin.'],
    ])('explains a %s answer and stays on the screen', async (status, message) => {
      await open();
      rejectWith(status);
      fillIn();

      await submit();

      expect(failure()).toBe(message);
      expect(router.url).toBe('/register');
    });

    it('explains a failure that is not an answer of the API', async () => {
      await open();
      register.mockRejectedValueOnce(new Error('boom'));
      fillIn();

      await submit();

      expect(failure()).toBe('Kayıt oluşturulamadı. Biraz sonra yeniden deneyin.');
    });

    // The mark the server left goes away once the user changes the field.
    it('lets the user correct a taken email and try again', async () => {
      await open();
      rejectWith(409);
      fillIn();
      await submit();
      expect(submitButton().disabled).toBe(false);

      let finish!: () => void;
      register.mockReturnValueOnce(new Promise<void>((resolve) => (finish = resolve)));
      type('email', 'baska@example.com');
      await submit();

      expect(register).toHaveBeenCalledTimes(2);
      expect(register.mock.lastCall).toEqual([
        { companyName: 'Örnek Lojistik', email: 'baska@example.com', password: 'Gizli123!' },
      ]);
      expect(failure()).toBeNull();

      signedIn = true;
      finish();
      await settle();
      expect(router.url).toBe('/');
    });
  });

  describe('while the request is on its way', () => {
    it('makes the button wait and ignores a second press', async () => {
      await open();
      let fail!: (error: unknown) => void;
      register.mockReturnValueOnce(new Promise<void>((_resolve, reject) => (fail = reject)));
      fillIn();

      await submit();
      expect(submitButton().disabled).toBe(true);
      expect(screen.querySelector('mat-spinner')).not.toBeNull();

      await submit();
      expect(register).toHaveBeenCalledOnce();

      // The attempt ends (here: badly, so the screen stays) and the button is back.
      fail(new HttpErrorResponse({ status: 500 }));
      await settle();
      expect(submitButton().disabled).toBe(false);
      expect(screen.querySelector('mat-spinner')).toBeNull();
    });
  });

  it('hides the password until the user asks to see it', async () => {
    await open();
    const reveal = screen.querySelector<HTMLButtonElement>('button.reveal')!;

    expect(field('password').type).toBe('password');

    reveal.click();
    await settle();

    expect(field('password').type).toBe('text');
    expect(reveal.getAttribute('aria-pressed')).toBe('true');
    expect(register).not.toHaveBeenCalled();
  });

  it('links to the login screen and carries the address to return to along', async () => {
    await open('/register?returnUrl=%2Fbelgeler');

    const link = screen.querySelector<HTMLAnchorElement>('.switch a')!;

    expect(link.getAttribute('href')).toBe('/login?returnUrl=%2Fbelgeler');
  });
});
