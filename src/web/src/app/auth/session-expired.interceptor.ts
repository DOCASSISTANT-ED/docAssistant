import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { Session } from './session';

// When the API answers 401 to somebody the app believes is signed in, the session has
// ended (the cookie lasts 60 minutes, docs/decisions.md #60). The app forgets the user
// and shows the login screen, which brings them back to where they were afterwards.
//
// A 401 while nobody is signed in is left alone: that is the login screen being told
// "wrong password", or the startup question "who am I?" being answered "nobody".
export const sessionExpiredInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(Session);
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && session.isSignedIn()) {
        session.expire();

        void router.navigate(['/login'], {
          queryParams: router.url === '/' ? {} : { returnUrl: router.url },
        });
      }

      // The caller still gets the error: its request did fail.
      return throwError(() => error);
    }),
  );
};
