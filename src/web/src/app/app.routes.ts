import { Routes } from '@angular/router';
import { signedInOnly, signedOutOnly } from './auth/auth.guards';

// Screens are loaded when first visited (loadComponent), not with the first page: a
// visitor who only signs in does not download the signed-in part of the app, and the
// other way round.
export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./auth/login/login').then((m) => m.Login),
    canActivate: [signedOutOnly],
    title: 'Giriş yap · DocAssistant',
  },
  {
    path: 'register',
    loadComponent: () => import('./auth/register/register').then((m) => m.Register),
    canActivate: [signedOutOnly],
    title: 'Firma kaydı · DocAssistant',
  },

  // Everything a signed-in user sees is shown inside the shell (top bar, account menu).
  {
    path: '',
    loadComponent: () => import('./shell/shell').then((m) => m.Shell),
    canActivate: [signedInOnly],
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () => import('./home/home').then((m) => m.Home),
        title: 'DocAssistant',
      },
    ],
  },

  // Unknown addresses lead home (and from there to the login screen when signed out).
  { path: '**', redirectTo: '' },
];
