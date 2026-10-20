import { Routes, UrlMatcher } from '@angular/router';
import {
  adminOnly,
  companyUserOnly,
  platformAdminOnly,
  signedInOnly,
  signedOutOnly,
} from './auth/auth.guards';

// The chat screen has two addresses: the plain one for a new conversation and /c/<id>
// for one from the history (so that a conversation can be reloaded, bookmarked and
// reached with the back button). One route with a matcher instead of two routes: going
// from one address to the other then keeps the screen as it is. With two routes the
// router would build the screen anew when a new conversation gets its id, and whatever
// was being typed would be lost.
const chatAddress: UrlMatcher = (segments) => {
  if (segments.length === 0) {
    return { consumed: [] };
  }

  if (segments.length === 2 && segments[0].path === 'c') {
    return { consumed: segments, posParams: { id: segments[1] } };
  }

  return null;
};

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
  // Typing the code e-mailed to a new account (decisions #70). An account that is signed in
  // is verified already.
  {
    path: 'dogrula',
    loadComponent: () => import('./auth/verify-email/verify-email').then((m) => m.VerifyEmail),
    canActivate: [signedOutOnly],
    title: 'E-postanızı doğrulayın · DocAssistant',
  },
  // Joining with an invitation link (decisions #67). A person belongs to one organization
  // (decisions #13), so somebody signed in has no use for it either.
  {
    path: 'davet/:token',
    loadComponent: () => import('./auth/join/join').then((m) => m.Join),
    canActivate: [signedOutOnly],
    title: 'Davete katıl · DocAssistant',
  },

  // The platform admin's screen (decisions #70), outside the shell: it has no chat and no
  // organization, only the companies.
  {
    path: 'firmalar',
    loadComponent: () => import('./platform/companies/companies').then((m) => m.Companies),
    canActivate: [signedInOnly, platformAdminOnly],
    title: 'Firmalar · DocAssistant',
  },

  // Everything a signed-in user of an organization sees is shown inside the shell (side
  // panel, account menu).
  {
    path: '',
    loadComponent: () => import('./shell/shell').then((m) => m.Shell),
    canActivate: [signedInOnly, companyUserOnly],
    children: [
      {
        matcher: chatAddress,
        loadComponent: () => import('./chat/chat').then((m) => m.Chat),
        title: 'DocAssistant',
      },
      {
        path: 'belgeler',
        loadComponent: () => import('./documents/documents').then((m) => m.Documents),
        title: 'Belgeler · DocAssistant',
      },
      {
        path: 'kullanicilar',
        loadComponent: () => import('./members/members').then((m) => m.Members),
        canActivate: [adminOnly],
        title: 'Kullanıcılar · DocAssistant',
      },
    ],
  },

  // Unknown addresses lead home (and from there to the login screen when signed out).
  { path: '**', redirectTo: '' },
];
