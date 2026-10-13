import { HttpInterceptorFn } from '@angular/common/http';
import { environment } from '../../environments/environment';

// Runs for every request the app sends and prepares those that go to the API
// (docs/decisions.md #60):
//
// - withCredentials: the browser attaches the session cookie, and keeps the one the API
//   sets at login. Without it a request to another origin (the API in development) goes
//   out without cookies.
// - X-Requested-With: the API rejects cookie-authenticated requests that change something
//   unless they carry this header; a page on another site cannot add it.
//
// Requests to anywhere else are passed on untouched: the cookie and the header are for
// the API only.
export const apiRequestInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith(`${environment.apiBaseUrl}/`)) {
    return next(request);
  }

  return next(
    request.clone({
      withCredentials: true,
      setHeaders: { 'X-Requested-With': 'XMLHttpRequest' },
    }),
  );
};
