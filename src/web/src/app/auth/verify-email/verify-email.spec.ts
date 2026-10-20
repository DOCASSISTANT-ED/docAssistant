import { HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of, throwError } from 'rxjs';
import { IdentityApi } from '../../api/identity';
import { Session } from '../session';
import { PendingVerification } from './pending-verification';
import { VerifyEmail } from './verify-email';

@Component({ template: '' })
class Blank {}

// The screen for typing the six-digit code e-mailed to a new account (docs/decisions.md
// #70). The address comes from the screen that sent the person here (PendingVerification);
// after a reload it is not known and the person types it. The sixth digit sends the code,
// the right one signs in, and a new code can be asked for once a minute.
describe('VerifyEmail', () => {
  let harness: RouterTestingHarness;
  let screen: HTMLElement;
  let router: Router;

  let signedIn: boolean;
  let verifyEmail: ReturnType<typeof vi.fn>;
  let resendVerification: ReturnType<typeof vi.fn>;

  // Opens /dogrula (or `url`) as the router would. `handedOver` is what the previous
  // screen wrote into PendingVerification; null when the person came by a reload.
  async function open(
    handedOver: { email: string; codeSent: boolean } | null,
    url = '/dogrula',
  ): Promise<void> {
    signedIn = false;
    verifyEmail = vi.fn(async () => {
      signedIn = true;
    });
    resendVerification = vi.fn(() => of(undefined));

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'dogrula', component: VerifyEmail },
          { path: 'login', component: Blank },
          { path: 'belgeler', component: Blank },
          { path: '', pathMatch: 'full', component: Blank },
        ]),
        { provide: Session, useValue: { verifyEmail, isSignedIn: () => signedIn } },
        { provide: IdentityApi, useValue: { resendVerification } },
      ],
    });

    if (handedOver) {
      TestBed.inject(PendingVerification).start(handedOver.email, handedOver.codeSent);
    }

    harness = await RouterTestingHarness.create(url);
    screen = harness.routeNativeElement!;
    router = TestBed.inject(Router);
  }

  // Lets what the screen started (the request, the navigation) run to its end.
  async function settle(): Promise<void> {
    await new Promise<void>((resolve) => setTimeout(resolve));
    harness.detectChanges();
  }

  const codeInput = () => screen.querySelector<HTMLInputElement>('#verification-code')!;
  const emailInput = () => screen.querySelector<HTMLInputElement>('input[formcontrolname="email"]');
  const slots = () =>
    Array.from(screen.querySelectorAll('.slot')).map((slot) => slot.textContent?.trim());
  const failure = () => screen.querySelector('[role="alert"]')?.textContent?.trim() ?? null;
  const notice = () => screen.querySelector('.notice')?.textContent?.trim() ?? null;
  const resendButton = () => screen.querySelector<HTMLButtonElement>('.resend-button');
  const waitText = () => screen.querySelector('.wait')?.textContent?.trim() ?? null;
  const subtitle = () =>
    screen.querySelector('.subtitle')?.textContent?.replace(/\s+/g, ' ').trim();

  async function typeCode(value: string): Promise<void> {
    codeInput().value = value;
    codeInput().dispatchEvent(new Event('input'));
    await settle();
  }

  async function typeEmail(value: string): Promise<void> {
    emailInput()!.value = value;
    emailInput()!.dispatchEvent(new Event('input'));
    harness.detectChanges();
  }

  async function submit(): Promise<void> {
    screen.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
  }

  afterEach(() => {
    vi.useRealTimers();
  });

  // ---- Where the person came from ----

  describe('with the address handed over', () => {
    it('names the address and asks only for the code', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });

      expect(subtitle()).toContain('ayse@example.com adresine 6 haneli bir kod gönderdik.');
      expect(emailInput()).toBeNull();
      expect(document.activeElement).toBe(codeInput());
    });

    it('makes the person wait a minute before asking for another code, as the API does', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });

      expect(waitText()).toBe('1:00 sonra yeni kod isteyebilirsiniz');
      expect(resendButton()).toBeNull();
    });

    it('offers a new code at once when none went out, and says so', async () => {
      await open({ email: 'ayse@example.com', codeSent: false });

      expect(notice()).toBe('Kod şu an gönderilemedi. Birazdan yeni kod isteyin.');
      expect(resendButton()).not.toBeNull();
      expect(waitText()).toBeNull();
    });
  });

  describe('without the address (after a reload)', () => {
    it('asks for the address too, and does not make the person wait', async () => {
      await open(null);

      expect(emailInput()).not.toBeNull();
      expect(subtitle()).toBe(
        'Hesabınızın e-posta adresini ve size gönderilen 6 haneli kodu girin.',
      );
      expect(resendButton()).not.toBeNull();
    });

    it('sends the typed address with the code', async () => {
      await open(null);
      await typeEmail('ali@example.com');

      await typeCode('123456');

      expect(verifyEmail).toHaveBeenCalledWith({ email: 'ali@example.com', code: '123456' });
    });

    it('does not send a code without a valid address', async () => {
      await open(null);
      await typeEmail('ali');

      await typeCode('123456');

      expect(verifyEmail).not.toHaveBeenCalled();
      expect(screen.querySelector('mat-error')?.textContent?.trim()).toBe(
        'Geçerli bir e-posta adresi girin.',
      );
    });

    it('does not ask for a new code without an address', async () => {
      await open(null);

      resendButton()!.click();
      await settle();

      expect(resendVerification).not.toHaveBeenCalled();
      expect(screen.querySelector('mat-error')?.textContent?.trim()).toBe(
        'E-posta adresinizi girin.',
      );
    });
  });

  // ---- Typing the code ----

  describe('typing the code', () => {
    it('keeps only digits, at most six, and fills the boxes', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });

      await typeCode('1a2 3');

      expect(codeInput().value).toBe('123');
      expect(slots()).toEqual(['1', '2', '3', '', '', '']);
      expect(verifyEmail).not.toHaveBeenCalled();
    });

    it('sends the code by itself at the sixth digit', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });

      await typeCode('123456');

      expect(verifyEmail).toHaveBeenCalledTimes(1);
      expect(verifyEmail).toHaveBeenCalledWith({ email: 'ayse@example.com', code: '123456' });
    });

    // A pasted code often has a space in the middle, or something after it.
    it.each([['123 456'], ['123-456'], ['1234567890']])('takes a pasted %j', async (pasted) => {
      await open({ email: 'ayse@example.com', codeSent: true });

      await typeCode(pasted);

      expect(verifyEmail).toHaveBeenCalledWith({
        email: 'ayse@example.com',
        code: pasted.replace(/\D/g, '').slice(0, 6),
      });
    });

    it('asks for all six digits when sent short', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });
      await typeCode('123');

      await submit();

      expect(verifyEmail).not.toHaveBeenCalled();
      expect(screen.querySelector('.code-error')?.textContent?.trim()).toBe(
        'Kodun 6 rakamını girin.',
      );
    });

    it('sends the code once while it is being checked', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });
      verifyEmail.mockReturnValueOnce(new Promise<void>(() => undefined));

      await typeCode('123456');
      await submit();

      expect(verifyEmail).toHaveBeenCalledTimes(1);
      expect(codeInput().disabled).toBe(true);
    });
  });

  // ---- The right code ----

  describe('the right code', () => {
    it('signs in and leads home, forgetting the handed-over address', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });

      await typeCode('123456');

      expect(router.url).toBe('/');
      expect(TestBed.inject(PendingVerification).email()).toBeNull();
    });

    it('goes on to the page asked for before signing in', async () => {
      await open({ email: 'ayse@example.com', codeSent: true }, '/dogrula?returnUrl=%2Fbelgeler');

      await typeCode('123456');

      expect(router.url).toBe('/belgeler');
    });

    // safeReturnUrl: a crafted link cannot send the person to another site.
    it.each([['//kotu.site'], ['https://kotu.site']])(
      'ignores the return address %s',
      async (target) => {
        await open(
          { email: 'ayse@example.com', codeSent: true },
          `/dogrula?returnUrl=${encodeURIComponent(target)}`,
        );

        await typeCode('123456');

        expect(router.url).toBe('/');
      },
    );

    // As on the login screen (decisions #60): the API took the code, but the session cookie
    // did not come back.
    it('says so when the session did not come back, and stays', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });
      verifyEmail.mockResolvedValueOnce(undefined);

      await typeCode('123456');

      expect(failure()).toBe(
        'Kod doğrulandı ama oturum açılamadı. Tarayıcınız çerezleri engelliyor olabilir; bu siteye çerez izni verip giriş yapın.',
      );
      expect(router.url).toBe('/dogrula');
      expect(TestBed.inject(PendingVerification).email()).toBe('ayse@example.com');
    });
  });

  // ---- A code that is refused ----

  describe('a refused code', () => {
    it.each([
      [400, 'Kod yanlış ya da süresi dolmuş. Yeni kod isteyebilirsiniz.'],
      [403, 'Bu hesap bir firmaya bağlı değil. Firmanızın yöneticisine başvurun.'],
      [0, 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.'],
      [500, 'Kod doğrulanamadı. Biraz sonra yeniden deneyin.'],
    ])('explains a %s answer and stays', async (status, message) => {
      await open({ email: 'ayse@example.com', codeSent: true });
      verifyEmail.mockRejectedValueOnce(new HttpErrorResponse({ status }));

      await typeCode('123456');

      expect(failure()).toBe(message);
      expect(router.url).toBe('/dogrula');
    });

    it('clears the boxes for the next try, and forgets the message when typing again', async () => {
      await open({ email: 'ayse@example.com', codeSent: true });
      verifyEmail.mockRejectedValueOnce(new HttpErrorResponse({ status: 400 }));
      await typeCode('123456');

      expect(codeInput().value).toBe('');
      expect(slots()).toEqual(['', '', '', '', '', '']);
      expect(codeInput().disabled).toBe(false);

      await typeCode('6');
      expect(failure()).toBeNull();
    });
  });

  // ---- Asking for a new code ----

  describe('a new code', () => {
    it('is asked for the handed-over address, and the minute starts again', async () => {
      await open({ email: 'ayse@example.com', codeSent: false });

      resendButton()!.click();
      await settle();

      expect(resendVerification).toHaveBeenCalledWith('ayse@example.com');
      expect(notice()).toBe('Yeni bir kod gönderdik. E-postanızı kontrol edin.');
      expect(waitText()).toBe('1:00 sonra yeni kod isteyebilirsiniz');
    });

    it('clears a half-typed code', async () => {
      await open({ email: 'ayse@example.com', codeSent: false });
      await typeCode('12');

      resendButton()!.click();
      await settle();

      expect(codeInput().value).toBe('');
    });

    it.each([
      [503, 'Kod şu an gönderilemedi. Biraz sonra yeniden deneyin.'],
      [0, 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.'],
      [500, 'Yeni kod istenemedi. Biraz sonra yeniden deneyin.'],
    ])('explains a %s answer and lets the person try again', async (status, message) => {
      await open({ email: 'ayse@example.com', codeSent: false });
      resendVerification.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status })));

      resendButton()!.click();
      await settle();

      expect(failure()).toBe(message);
      expect(resendButton()).not.toBeNull();
      expect(resendButton()!.disabled).toBe(false);
    });
  });

  // ---- The minute's countdown ----

  describe('the countdown', () => {
    it('counts the minute down and then offers a new code', async () => {
      vi.useFakeTimers();
      await open({ email: 'ayse@example.com', codeSent: true });

      await vi.advanceTimersByTimeAsync(1000);
      harness.detectChanges();
      expect(waitText()).toBe('0:59 sonra yeni kod isteyebilirsiniz');

      await vi.advanceTimersByTimeAsync(59_000);
      harness.detectChanges();
      expect(waitText()).toBeNull();
      expect(resendButton()).not.toBeNull();
    });
  });

  it('leads back to the login screen for another account', async () => {
    await open({ email: 'ayse@example.com', codeSent: true });

    expect(screen.querySelector('a[href="/login"]')?.textContent?.trim()).toBe('Girişe dönün');
  });
});
