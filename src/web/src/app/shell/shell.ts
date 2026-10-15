import { Component, computed, inject, signal } from '@angular/core';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ConversationSummary } from '../api/chat';
import { Session } from '../auth/session';
import { ChatStore } from '../chat/chat-store';

interface ConversationGroup {
  label: string;
  conversations: ConversationSummary[];
}

// The frame around every screen a signed-in user sees: the side panel (new chat, the
// history, the account menu) and next to it the current screen, which the router puts
// where <router-outlet /> is.
@Component({
  imports: [MatMenuModule, RouterLink, RouterLinkActive, RouterOutlet],
  selector: 'app-shell',
  styleUrl: './shell.css',
  templateUrl: './shell.html',
})
export class Shell {
  private readonly session = inject(Session);
  private readonly chat = inject(ChatStore);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly user = this.session.user;

  // The letter shown in the round account button.
  protected readonly initial = computed(() => this.user()?.email.charAt(0).toLocaleUpperCase('tr') ?? '');

  protected readonly isAdmin = this.session.isAdmin;
  protected readonly roleLabel = computed(() => (this.session.isAdmin() ? 'Yönetici' : 'Üye'));

  // The history, newest first, under headings by age.
  protected readonly groups = computed(() => groupByAge(this.chat.conversations(), new Date()));

  // On narrow screens the panel is a drawer that slides in over the screen.
  protected readonly panelOpen = signal(false);

  protected readonly signingOut = signal(false);

  constructor() {
    void this.chat.loadConversations();

    // Choosing a conversation in the drawer should also put the drawer away.
    this.router.events.subscribe((event) => {
      if (event instanceof NavigationEnd) {
        this.panelOpen.set(false);
      }
    });
  }

  protected async signOut(): Promise<void> {
    if (this.signingOut()) {
      return;
    }

    this.signingOut.set(true);

    try {
      await this.session.logout();
      this.chat.startNew();
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

// Whole days between two moments, by the calendar of the user's own time zone.
function daysBetween(earlier: Date, later: Date): number {
  const startOf = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();

  return Math.round((startOf(later) - startOf(earlier)) / 86_400_000);
}

function groupByAge(conversations: readonly ConversationSummary[], now: Date): ConversationGroup[] {
  const groups: ConversationGroup[] = [
    { label: 'Bugün', conversations: [] },
    { label: 'Dün', conversations: [] },
    { label: 'Son 7 gün', conversations: [] },
    { label: 'Daha eski', conversations: [] },
  ];

  for (const conversation of conversations) {
    const age = daysBetween(new Date(conversation.createdAt), now);

    groups[age <= 0 ? 0 : age === 1 ? 1 : age <= 7 ? 2 : 3].conversations.push(conversation);
  }

  return groups.filter((group) => group.conversations.length > 0);
}
