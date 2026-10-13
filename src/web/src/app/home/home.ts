import { Component, inject } from '@angular/core';
import { Session } from '../auth/session';

// Where a signed-in user lands. A placeholder until the chat screen is built: it only
// shows who is signed in, which proves that the session was read back from the API.
@Component({
  selector: 'app-home',
  template: `
    <main class="home">
      @if (session.user(); as user) {
        <h1>Hoş geldiniz</h1>
        <p>{{ user.email }} olarak giriş yaptınız.</p>
      } @else {
        <h1>Giriş yapılmadı</h1>
      }
    </main>
  `,
  styles: `
    .home {
      padding: 3rem;
    }

    h1 {
      margin: 0 0 0.5rem;
      font: var(--mat-sys-headline-medium);
    }

    p {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class Home {
  protected readonly session = inject(Session);
}
