import { Routes } from '@angular/router';
import { signedInOnly, signedOutOnly } from './auth/auth.guards';
import { Login } from './auth/login/login';
import { Register } from './auth/register/register';
import { Home } from './home/home';

export const routes: Routes = [
  { path: 'login', component: Login, canActivate: [signedOutOnly], title: 'Giriş yap · DocAssistant' },
  { path: 'register', component: Register, canActivate: [signedOutOnly], title: 'Firma kaydı · DocAssistant' },
  { path: '', component: Home, canActivate: [signedInOnly], pathMatch: 'full', title: 'DocAssistant' },

  // Unknown addresses lead home (and from there to the login screen when signed out).
  { path: '**', redirectTo: '' },
];
