import { Routes } from '@angular/router';
import { Login } from './auth/login/login';
import { Register } from './auth/register/register';
import { Home } from './home/home';

export const routes: Routes = [
  { path: 'login', component: Login, title: 'Giriş yap · DocAssistant' },
  { path: 'register', component: Register, title: 'Firma kaydı · DocAssistant' },
  { path: '', component: Home, title: 'DocAssistant' },
];
