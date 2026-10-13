import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { apiRequestInterceptor } from './api/api-request.interceptor';
import { sessionExpiredInterceptor } from './auth/session-expired.interceptor';
import { routes } from './app.routes';
import { Session } from './auth/session';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withFetch(), withInterceptors([apiRequestInterceptor, sessionExpiredInterceptor])),

    // Before the first screen is shown the app must know whether somebody is signed in;
    // otherwise a signed-in user reloading the page would see the login screen flash by.
    provideAppInitializer(() => inject(Session).load()),
  ]
};
