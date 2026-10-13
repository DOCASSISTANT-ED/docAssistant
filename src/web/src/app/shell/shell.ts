import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { Session } from '../auth/session';

// The frame around every screen a signed-in user sees: the top bar with the account
// menu, and below it the current screen (the router puts it where <router-outlet /> is).
@Component({
  imports: [MatButtonModule, MatMenuModule, RouterLink, RouterOutlet],
  selector: 'app-shell',
  styleUrl: './shell.css',
  templateUrl: './shell.html',
})
export class Shell {
  private readonly session = inject(Session);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly user = this.session.user;

  // The letter shown in the round account button.
  protected readonly initial = computed(() => this.user()?.email.charAt(0).toLocaleUpperCase('tr') ?? '');

  protected readonly roleLabel = computed(() => (this.session.isAdmin() ? 'Yönetici' : 'Üye'));

  protected readonly signingOut = signal(false);

  protected async signOut(): Promise<void> {
    if (this.signingOut()) {
      return;
    }

    this.signingOut.set(true);

    try {
      await this.session.logout();
      await this.router.navigateByUrl('/login');
    } catch {
      // Only the API can remove the session cookie; if it could not be reached the
      // user is still signed in and should know.
      this.snackBar.open('Çıkış yapılamadı. Bağlantınızı kontrol edip yeniden deneyin.', 'Tamam', {
        duration: 6000,
      });
    } finally {
      this.signingOut.set(false);
    }
  }
}
