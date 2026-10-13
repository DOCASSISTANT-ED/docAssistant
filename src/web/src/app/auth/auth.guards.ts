import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Session } from './session';

// Route guards: the router asks them before showing a screen. Returning true lets the
// screen through; returning an address sends the user there instead.
//
// By the time they run the session is known: the app waits for it at startup
// (app.config.ts). They only decide what to show. What a user may actually read or
// change is decided by the API on every request.

// For screens that need a signed-in user. Others go to the login screen, which
// remembers where they were heading.
export const signedInOnly: CanActivateFn = (_route, state) => {
  if (inject(Session).isSignedIn()) {
    return true;
  }

  return inject(Router).createUrlTree(['/login'], {
    queryParams: state.url === '/' ? {} : { returnUrl: state.url },
  });
};

// For the login and register screens: somebody already signed in has no use for them.
export const signedOutOnly: CanActivateFn = () =>
  inject(Session).isSignedIn() ? inject(Router).createUrlTree(['/']) : true;

// Where to go after signing in: the address the login screen was given, if it is one of
// the app's own. Anything else (another site, a protocol-relative "//host") is ignored,
// so a crafted link cannot use the login screen to send people elsewhere.
export function safeReturnUrl(returnUrl: string | null | undefined): string {
  return returnUrl && returnUrl.startsWith('/') && !returnUrl.startsWith('//') && !returnUrl.includes('\\')
    ? returnUrl
    : '/';
}
