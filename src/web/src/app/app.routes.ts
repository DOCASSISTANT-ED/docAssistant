import { Routes } from '@angular/router';
import { Login } from './auth/login/login';
import { Home } from './home/home';

export const routes: Routes = [
  { path: 'login', component: Login, title: 'Giriş yap · DocAssistant' },
  { path: '', component: Home, title: 'DocAssistant' },
];
