import { Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterOutlet } from '@angular/router';
import { environment } from '../environments/environment';

type ApiStatus = 'checking' | 'up' | 'down';

@Component({
  imports: [RouterOutlet],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  private readonly http = inject(HttpClient);

  protected readonly title = 'DocAssistant';

  // Skeleton only: proves the app can reach the API. Real screens arrive in phase 4.
  protected readonly apiStatus = signal<ApiStatus>('checking');

  constructor() {
    this.http.get(`${environment.apiBaseUrl}/health`, { responseType: 'text' }).subscribe({
      next: () => this.apiStatus.set('up'),
      error: () => this.apiStatus.set('down'),
    });
  }
}
