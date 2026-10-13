import { Component } from '@angular/core';

// Where a signed-in user lands. A placeholder until the chat screen is built.
@Component({
  selector: 'app-home',
  template: `
    <main class="home">
      <section class="welcome">
        <p class="eyebrow">Hoş geldiniz</p>
        <h1>Belgeleriniz burada cevap bulacak.</h1>
        <p class="note">
          Sohbet ekranı hazırlanıyor. Yakında buradan belgelerinize soru sorabilecek ve her
          cevabın dayandığı kaynağı görebileceksiniz.
        </p>
      </section>
    </main>
  `,
  styles: `
    .home {
      display: grid;
      place-items: center;
      height: 100%;
      box-sizing: border-box;
      overflow: auto;
      padding: 3rem 1.5rem;
    }

    .welcome {
      max-width: 36rem;
      text-align: center;
      animation: rise 0.8s 0.15s both cubic-bezier(0.2, 0.7, 0.2, 1);
    }

    .eyebrow {
      display: inline-block;
      margin: 0 0 1.25rem;
      padding: 0.3rem 0.9rem;
      border-radius: 2rem;
      background: color-mix(in srgb, var(--brand-amber) 26%, transparent);
      color: var(--brand-on-amber);
      font: var(--mat-sys-label-large);
    }

    h1 {
      margin: 0 0 1rem;
      font-size: clamp(1.9rem, 4vw, 2.75rem);
      font-weight: 500;
      line-height: 1.15;
      letter-spacing: -0.015em;
      color: var(--mat-sys-primary);
    }

    .note {
      margin: 0;
      font: var(--mat-sys-body-large);
      line-height: 1.6;
      color: var(--mat-sys-on-surface-variant);
    }

    @keyframes rise {
      from {
        opacity: 0;
        transform: translateY(1.25rem);
      }
    }

    @media (prefers-reduced-motion: reduce) {
      .welcome {
        animation: none;
      }
    }
  `,
})
export class Home {}
