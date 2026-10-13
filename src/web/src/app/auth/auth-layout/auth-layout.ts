import { Component } from '@angular/core';

// The frame shared by the login and register screens: the brand panel on one side, the
// screen's own form on the other (placed where <ng-content /> is in the template).
@Component({
  selector: 'app-auth-layout',
  styleUrl: './auth-layout.css',
  templateUrl: './auth-layout.html',
})
export class AuthLayout {}
