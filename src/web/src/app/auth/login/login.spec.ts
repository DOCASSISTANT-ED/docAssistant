import { HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { signedInOnly } from '../auth.guards';
import { Session } from '../session';
import { Login } from './login';

@Component({ template: '' })
class Blank {}

// The login screen, driven the way a user drives it: type into the fields, press the
// button, read what the screen says. The session is a stand-in, so each test decides
// what signing in leads to; the API's side of it is covered in session.spec.ts.
describe('Login', () => {
  let harness: RouterTestingHarness;
  let screen: HTMLElement;
  let router: Router;

  let signedIn: boolean;
  let login: ReturnType<typeof vi.fn>;

  // Opens the screen at `url`, as the router would.
  async function open(url = '/login'): Promise<void> {
    signedIn = false;
    login = vi.fn(async () => {
      signedIn = true;
    });

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', component: Login },
          { path: 'register', component: Blank },
          { path: 'belgeler', component: Blank, canActivate: [signedInOnly] },
          { path: '', pathMatch: 'full', component: Blank, canActivate: [signedInOnly] },
        ]),
        { provide: Session, useValue: { login, isSignedIn: () => signedIn } },
      ],
    });

    harness = await RouterTestingHarness.create(url);
    screen = harness.routeNativeElement!;
    router = TestBed.inject(Router);
  }

  // Lets what the screen started (the request, the navigation) run to its end.
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

  function fillIn(email = 'ayse@example.com', password = 'Gizli123!'): void {
    type('email', email);
    type('password', password);
  }

  async function submit(): Promise<void> {
    screen.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
  }

  const submitButton = () => screen.querySelector<HTMLButtonElement>('button[type="submit"]')!;
  const failure = () => screen.querySelector('[role="alert"]')?.textContent?.trim() ?? null;
  const fieldErrors = () =>
    Array.from(screen.querySelectorAll('mat-error')).map((error) => error.textContent?.trim());

  function rejectWith(status: number): void {
    login.mockRejectedValueOnce(new HttpErrorResponse({ status }));
  }

  describe('signing in', () => {
    it('signs in with what was typed and goes home', async () => {
      await open();
      fillIn('ayse@example.com', 'Gizli123!');

      await submit();

      expect(login).toHaveBeenCalledExactlyOnceWith({
        email: 'ayse@example.com',
        password: 'Gizli123!',
      });
      expect(router.url).toBe('/');
      expect(failure()).toBeNull();
    });

    // The guard sent the user here from another screen; signing in brings them back.
    it('goes back to the screen the user was sent here from', async () => {
      await open('/login?returnUrl=%2Fbelgeler%3Fsayfa%3D2');
      fillIn();

      await submit();

      expect(router.url).toBe('/belgeler?sayfa=2');
    });

    // A link somebody else wrote must not use this screen to send the user elsewhere.
    it('goes home when the address to return to is not one of the app', async () => {
      await open('/login?returnUrl=%2F%2Fbaska-site.example');
      fillIn();

      await submit();

      expect(router.url).toBe('/');
    });
  });

  describe('a form that is not ready', () => {
    it('sends nothing and says what is missing when the form is empty', async () => {
      await open();

      await submit();

      expect(login).not.toHaveBeenCalled();
      expect(fieldErrors()).toEqual(['E-posta adresinizi girin.', 'Parolanızı girin.']);
    });

    it('sends nothing when the email is not an email address', async () => {
      await open();
      fillIn('ayse-at-example');

      await submit();

      expect(login).not.toHaveBeenCalled();
      expect(fieldErrors()).toEqual(['Geçerli bir e-posta adresi girin.']);
    });

    // The server would reject it anyway (LoginRequest: 128 characters).
    it('sends nothing when the password is longer than the server accepts', async () => {
      await open();
      fillIn('ayse@example.com', 'a'.repeat(129));

      await submit();

      expect(login).not.toHaveBeenCalled();
      expect(fieldErrors()).toEqual(['Parola çok uzun.']);
    });

    it('shows no complaints before the user has tried', async () => {
      await open();

      expect(fieldErrors()).toEqual([]);
      expect(failure()).toBeNull();
    });
  });

  describe('a rejected attempt', () => {
    it.each([
      [401, 'E-posta ya da parola hatalı.'],
      [403, 'Bu hesap bir firmaya bağlı değil. Firmanızın yöneticisine başvurun.'],
      [0, 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.'],
      [500, 'Giriş yapılamadı. Biraz sonra yeniden deneyin.'],
    ])('explains a %s answer and stays on the screen', async (status, message) => {
      await open('/login?returnUrl=%2Fbelgeler');
      rejectWith(status);
      fillIn();

      await submit();

      expect(failure()).toBe(message);
      expect(router.url).toBe('/login?returnUrl=%2Fbelgeler');
    });

    it('explains a failure that is not an answer of the API', async () => {
      await open();
      login.mockRejectedValueOnce(new Error('boom'));
      fillIn();

      await submit();

      expect(failure()).toBe('Giriş yapılamadı. Biraz sonra yeniden deneyin.');
    });

    it('lets the user try again, and forgets the old failure when they do', async () => {
      await open();
      rejectWith(401);
      fillIn();
      await submit();
      expect(submitButton().disabled).toBe(false);

      // The second attempt is held back, so the screen can be looked at while it runs.
      let finish!: () => void;
      login.mockReturnValueOnce(new Promise<void>((resolve) => (finish = resolve)));
      type('password', 'Dogru123!');
      await submit();

      expect(login).toHaveBeenCalledTimes(2);
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
      login.mockReturnValueOnce(new Promise<void>((_resolve, reject) => (fail = reject)));
      fillIn();

      await submit();
      expect(submitButton().disabled).toBe(true);
      expect(screen.querySelector('mat-spinner')).not.toBeNull();

      await submit();
      expect(login).toHaveBeenCalledOnce();

      // The attempt ends (here: badly, so the screen stays) and the button is back.
      fail(new HttpErrorResponse({ status: 500 }));
      await settle();
      expect(submitButton().disabled).toBe(false);
      expect(screen.querySelector('mat-spinner')).toBeNull();
    });
  });

  describe('the password field', () => {
    it('hides the password until the user asks to see it', async () => {
      await open();
      const reveal = screen.querySelector<HTMLButtonElement>('button.reveal')!;

      expect(field('password').type).toBe('password');
      expect(reveal.getAttribute('aria-pressed')).toBe('false');

      reveal.click();
      harness.detectChanges();

      expect(field('password').type).toBe('text');
      expect(reveal.getAttribute('aria-pressed')).toBe('true');
      expect(reveal.textContent?.trim()).toBe('Gizle');
    });

    // The button sits inside the form; pressing it must not send the form.
    it('does not send the form when the password is shown', async () => {
      await open();
      fillIn();

      screen.querySelector<HTMLButtonElement>('button.reveal')!.click();
      await settle();

      expect(login).not.toHaveBeenCalled();
    });
  });

  it('links to the register screen and carries the address to return to along', async () => {
    await open('/login?returnUrl=%2Fbelgeler');

    const link = screen.querySelector<HTMLAnchorElement>('.switch a')!;

    expect(link.getAttribute('href')).toBe('/register?returnUrl=%2Fbelgeler');
  });

  // The API accepts the password but no session follows, for example because the browser
  // refused to store the cookie: Session.login() ends without an error. The screen must
  // not move on (the guard would only send the user straight back here, without a word);
  // it stays and tells the user that signing in did not work.
  it('tells the user when signing in left no session', async () => {
    await open();
    login.mockImplementationOnce(async () => {
      // Accepted, but nobody is signed in afterwards.
    });
    fillIn();

    await submit();

    expect(router.url).toBe('/login');
    expect(failure()).toBe(
      'Giriş yapılamadı: oturum açılamadı. Tarayıcınız çerezleri engelliyor olabilir; bu siteye çerez izni verip yeniden deneyin.',
    );
    expect(submitButton().disabled).toBe(false);
  });
});
