import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Role } from '../api/identity';
import {
  CreatedInvitation,
  InvitationsApi,
  Member,
  PendingInvitation,
  roleLabels,
} from '../api/invitations';
import { Session } from '../auth/session';

// Whether the lists could be shown.
type ListState = 'loading' | 'ready' | 'failed';

// The people of the organization and its invitation links (docs/decisions.md #67).
// Admins only: the route lets nobody else in, and the API refuses everyone else anyway.
@Component({
  selector: 'app-members',
  styleUrl: './members.css',
  templateUrl: './members.html',
})
export class Members {
  private readonly api = inject(InvitationsApi);
  private readonly session = inject(Session);
  private readonly origin = inject(DOCUMENT).location.origin;

  protected readonly roleLabels = roleLabels;
  protected readonly roles: Role[] = ['Member', 'Admin'];

  protected readonly members = signal<Member[]>([]);
  protected readonly invitations = signal<PendingInvitation[]>([]);
  protected readonly listState = signal<ListState>('loading');

  // ---- Inviting ----

  protected readonly inviteRole = signal<Role>('Member');
  protected readonly creating = signal(false);

  // The link just created. Its key is shown by the server this once, so the page keeps
  // it only until it is closed or the page is left.
  protected readonly created = signal<{ invitation: CreatedInvitation; url: string } | null>(null);
  protected readonly copied = signal(false);

  // ---- Removing and revoking ----

  protected readonly confirming = signal<string | null>(null);
  protected readonly removing = signal<string | null>(null);
  protected readonly revoking = signal<string | null>(null);

  // What went wrong with the last action, in words for the user.
  protected readonly failure = signal<string | null>(null);

  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
    void this.load();
  }

  protected reload(): void {
    this.listState.set('loading');
    void this.load();
  }

  protected isSelf(member: Member): boolean {
    return member.userId === this.session.user()?.userId;
  }

  // ---- Inviting ----

  protected async createInvitation(): Promise<void> {
    if (this.creating()) {
      return;
    }

    this.creating.set(true);
    this.failure.set(null);
    this.copied.set(false);

    try {
      const invitation = await firstValueFrom(this.api.create(this.inviteRole()));

      this.created.set({ invitation, url: `${this.origin}/davet/${invitation.token}` });
      this.invitations.update((invitations) => [
        {
          id: invitation.id,
          role: invitation.role,
          createdAt: new Date().toISOString(),
          expiresAt: invitation.expiresAt,
        },
        ...invitations,
      ]);
    } catch (error) {
      this.failure.set(failureFor(error, 'Davet bağlantısı oluşturulamadı.'));
    } finally {
      this.creating.set(false);
    }
  }

  // The Clipboard API (navigator.clipboard) works only on a secure page (https, or
  // localhost) and may be refused by the browser; the link stays on screen to copy by hand.
  protected async copyLink(url: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(url);
      this.copied.set(true);
    } catch {
      this.failure.set('Bağlantı kopyalanamadı. Kutudaki bağlantıyı seçip elle kopyalayın.');
    }
  }

  protected closeCreated(): void {
    this.created.set(null);
    this.copied.set(false);
  }

  protected async revoke(invitation: PendingInvitation): Promise<void> {
    if (this.revoking() !== null) {
      return;
    }

    this.revoking.set(invitation.id);
    this.failure.set(null);

    try {
      await firstValueFrom(this.api.revoke(invitation.id));
      this.dropInvitation(invitation.id);
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 404) {
        // Used or revoked in the meantime: either way it is no longer waiting.
        this.dropInvitation(invitation.id);
      } else {
        this.failure.set(failureFor(error, 'Davet iptal edilemedi.'));
      }
    } finally {
      this.revoking.set(null);
    }
  }

  // ---- Removing a member ----

  protected askToRemove(userId: string): void {
    this.failure.set(null);
    this.confirming.set(userId);
  }

  protected cancelRemove(): void {
    this.confirming.set(null);
  }

  protected async remove(member: Member): Promise<void> {
    if (this.removing() !== null) {
      return;
    }

    this.confirming.set(null);
    this.removing.set(member.userId);
    this.failure.set(null);

    try {
      await firstValueFrom(this.api.removeMember(member.userId));
      this.dropMember(member.userId);
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 404) {
        // Already removed by someone else: the list catches up.
        this.dropMember(member.userId);
      } else {
        // The page offers no button for yourself, so a refusal here is for the last admin.
        this.failure.set(
          failureFor(
            error,
            `${member.email} firmadan çıkarılamadı.`,
            'Firmanın son yöneticisi çıkarılamaz.',
          ),
        );
      }
    } finally {
      this.removing.set(null);
    }
  }

  // ---- Showing ----

  protected dateLabel(value: string): string {
    return new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium' }).format(new Date(value));
  }

  // ---- Internals ----

  private async load(): Promise<void> {
    try {
      const [members, invitations] = await Promise.all([
        firstValueFrom(this.api.members()),
        firstValueFrom(this.api.pending()),
      ]);

      if (this.destroyed) {
        return;
      }

      this.members.set(members);
      this.invitations.set(invitations);
      this.listState.set('ready');
    } catch {
      if (!this.destroyed) {
        this.listState.set('failed');
      }
    }
  }

  private dropMember(userId: string): void {
    this.members.update((members) => members.filter((member) => member.userId !== userId));
  }

  private dropInvitation(id: string): void {
    this.invitations.update((invitations) => invitations.filter((i) => i.id !== id));
  }
}

// The sentence shown when an action fails. conflict is for the action's 409, if it has one.
function failureFor(error: unknown, action: string, conflict?: string): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 409 && conflict) {
      return `${action} ${conflict}`;
    }

    if (error.status === 0) {
      return `${action} Sunucuya ulaşılamıyor; bağlantınızı kontrol edip yeniden deneyin.`;
    }
  }

  return `${action} Biraz sonra yeniden deneyin.`;
}
