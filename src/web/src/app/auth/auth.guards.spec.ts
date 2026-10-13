import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  Router,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { safeReturnUrl, signedInOnly, signedOutOnly } from './auth.guards';
import { Session } from './session';

// The guards only ask the session one question, so a stand-in that answers it is enough.
function setUp(signedIn: boolean): void {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), { provide: Session, useValue: { isSignedIn: () => signedIn } }],
  });
}

// Runs a guard for a visit to `url` and gives back what it answered: true, or the
// address it sends the user to instead.
function visit(guard: typeof signedInOnly, url: string): true | string {
  const result = TestBed.runInInjectionContext(() =>
    guard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
  );

  return result instanceof UrlTree ? TestBed.inject(Router).serializeUrl(result) : (result as true);
}

describe('signedInOnly', () => {
  it('lets a signed-in user through', () => {
    setUp(true);

    expect(visit(signedInOnly, '/belgeler')).toBe(true);
  });

  it('sends a signed-out user to the login screen and remembers where they were heading', () => {
    setUp(false);

    expect(visit(signedInOnly, '/belgeler?sayfa=2')).toBe(
      '/login?returnUrl=%2Fbelgeler%3Fsayfa%3D2',
    );
  });

  // The home page is where the login screen leads anyway.
  it('does not remember the home page', () => {
    setUp(false);

    expect(visit(signedInOnly, '/')).toBe('/login');
  });
});

describe('signedOutOnly', () => {
  it('lets a signed-out user see the login and register screens', () => {
    setUp(false);

    expect(visit(signedOutOnly, '/login')).toBe(true);
  });

  it('sends a signed-in user home', () => {
    setUp(true);

    expect(visit(signedOutOnly, '/login')).toBe('/');
  });
});

// The address comes from the page's own URL, which anybody can write into a link.
describe('safeReturnUrl', () => {
  it.each(['/belgeler', '/belgeler?sayfa=2', '/sohbet/123#son'])(
    'keeps an address inside the app: %s',
    (url) => {
      expect(safeReturnUrl(url)).toBe(url);
    },
  );

  it.each([null, undefined, ''])('leads home when there is none: %s', (url) => {
    expect(safeReturnUrl(url)).toBe('/');
  });

  // Each of these would take the user to another site right after they signed in.
  it.each([
    'https://baska-site.example/giris',
    '//baska-site.example/giris',
    '/\\baska-site.example',
    '\\\\baska-site.example',
    'javascript:alert(1)',
    'belgeler',
  ])('leads home instead of following %s', (url) => {
    expect(safeReturnUrl(url)).toBe('/');
  });
});
