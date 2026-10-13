import { Component, computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ConversationSummary } from '../api/chat';
import { CurrentUser } from '../api/identity';
import { Session } from '../auth/session';
import { ChatStore } from '../chat/chat-store';
import { Shell } from './shell';

@Component({ template: '<p class="inner-screen">İçerik</p>' })
class Inner {}

@Component({ template: '' })
class Blank {}

const admin: CurrentUser = {
  userId: 'u-1',
  email: 'ayse@example.com',
  tenantId: 't-1',
  role: 'Admin',
};

// Noon of the day `days` before today, in the user's own time zone: far from midnight,
// so the test means the same day at whatever hour it runs.
function daysAgo(days: number): string {
  const now = new Date();

  return new Date(now.getFullYear(), now.getMonth(), now.getDate() - days, 12).toISOString();
}

function conversation(id: string, title: string, createdAt = daysAgo(0)): ConversationSummary {
  return { id, title, createdAt };
}

// The frame around the signed-in screens: the side panel with the history and the
// account menu. The session and the chat store are stand-ins the test fills.
describe('Shell', () => {
  let harness: RouterTestingHarness;
  let shell: HTMLElement;
  let router: Router;

  let logout: ReturnType<typeof vi.fn>;
  let loadConversations: ReturnType<typeof vi.fn>;
  let startNew: ReturnType<typeof vi.fn>;
  const conversations = signal<ConversationSummary[]>([]);

  async function open(
    user: CurrentUser | null,
    history: ConversationSummary[] = [],
    url = '/',
  ): Promise<void> {
    const current = signal(user);
    logout = vi.fn(async () => undefined);
    loadConversations = vi.fn(async () => undefined);
    startNew = vi.fn();
    conversations.set(history);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: '',
            component: Shell,
            children: [
              { path: '', pathMatch: 'full', component: Inner },
              { path: 'c/:id', component: Inner },
            ],
          },
          { path: 'login', component: Blank },
        ]),
        {
          provide: Session,
          useValue: {
            user: current.asReadonly(),
            isAdmin: computed(() => current()?.role === 'Admin'),
            logout,
          },
        },
        {
          provide: ChatStore,
          useValue: { conversations: conversations.asReadonly(), loadConversations, startNew },
        },
      ],
    });

    harness = await RouterTestingHarness.create(url);
    shell = harness.routeNativeElement!;
    router = TestBed.inject(Router);
  }

  async function settle(): Promise<void> {
    await new Promise<void>((resolve) => setTimeout(resolve));
    harness.detectChanges();
  }

  // The menu and the notice are drawn outside the shell's own element, on top of the page.
  const page = () => document.body.textContent ?? '';

  async function openAccountMenu(): Promise<void> {
    shell.querySelector<HTMLButtonElement>('button.account')!.click();
    await settle();
  }

  async function pressSignOut(): Promise<void> {
    const item = Array.from(
      document.querySelectorAll<HTMLButtonElement>('button[mat-menu-item]'),
    ).find((button) => button.textContent?.includes('Çıkış yap'))!;

    item.click();
    await settle();
  }

  it('shows the current screen inside the frame', async () => {
    await open(admin);

    expect(shell.querySelector('.inner-screen')?.textContent).toBe('İçerik');
    expect(shell.querySelector('.brand-name')?.textContent).toBe('DocAssistant');
  });

  // ---- The history ----

  describe('the history', () => {
    const links = () => Array.from(shell.querySelectorAll<HTMLAnchorElement>('a.conversation'));
    const headings = () =>
      Array.from(shell.querySelectorAll('.group-label')).map((h) => h.textContent);

    it('asks for the list of conversations when the frame appears', async () => {
      await open(admin);

      expect(loadConversations).toHaveBeenCalledOnce();
    });

    it('says where the conversations will be when there are none yet', async () => {
      await open(admin);

      expect(shell.querySelector('.history-empty')?.textContent).toBe(
        'Sohbetleriniz burada listelenecek.',
      );
      expect(links()).toEqual([]);
    });

    it('lists each conversation with a link to it', async () => {
      await open(admin, [
        conversation('c-2', 'Harcırah ne kadar?'),
        conversation('c-1', 'Yıllık izin kaç gün?'),
      ]);

      expect(links().map((link) => link.textContent?.trim())).toEqual([
        'Harcırah ne kadar?',
        'Yıllık izin kaç gün?',
      ]);
      expect(links().map((link) => link.getAttribute('href'))).toEqual(['/c/c-2', '/c/c-1']);
      expect(shell.querySelector('.history-empty')).toBeNull();
    });

    // The panel is narrow and cuts long titles off; the whole title is in the tooltip.
    it('carries the whole title as a tooltip', async () => {
      const title = 'Çok uzun bir soru '.repeat(4).trim();
      await open(admin, [conversation('c-1', title)]);

      expect(links()[0].getAttribute('title')).toBe(title);
    });

    it('puts the conversations under headings by age', async () => {
      await open(admin, [
        conversation('c-5', 'Bugünkü', daysAgo(0)),
        conversation('c-4', 'Dünkü', daysAgo(1)),
        conversation('c-3', 'İki gün önceki', daysAgo(2)),
        conversation('c-2', 'Yedi gün önceki', daysAgo(7)),
        conversation('c-1', 'Sekiz gün önceki', daysAgo(8)),
      ]);

      expect(headings()).toEqual(['Bugün', 'Dün', 'Son 7 gün', 'Daha eski']);

      const underHeading = Array.from(shell.querySelectorAll('ul.group')).map((group) =>
        Array.from(group.querySelectorAll('a.conversation')).map((link) =>
          link.textContent?.trim(),
        ),
      );
      expect(underHeading).toEqual([
        ['Bugünkü'],
        ['Dünkü'],
        ['İki gün önceki', 'Yedi gün önceki'],
        ['Sekiz gün önceki'],
      ]);
    });

    it('leaves out the headings that have nothing under them', async () => {
      await open(admin, [
        conversation('c-2', 'Bugünkü', daysAgo(0)),
        conversation('c-1', 'Eski', daysAgo(40)),
      ]);

      expect(headings()).toEqual(['Bugün', 'Daha eski']);
    });

    // "Yesterday" is the calendar day before, not "24 hours ago": a conversation from
    // late last night is yesterday's even in the early morning.
    it('counts in calendar days, not in hours', async () => {
      const now = new Date();
      const lateLastNight = new Date(
        now.getFullYear(),
        now.getMonth(),
        now.getDate() - 1,
        23,
        59,
      ).toISOString();
      const earlyToday = new Date(
        now.getFullYear(),
        now.getMonth(),
        now.getDate(),
        0,
        0,
        1,
      ).toISOString();

      await open(admin, [
        conversation('c-2', 'Bu sabah', earlyToday),
        conversation('c-1', 'Dün gece', lateLastNight),
      ]);

      expect(headings()).toEqual(['Bugün', 'Dün']);
    });

    // The browser's clock may be a little behind the server's.
    it('files a conversation dated slightly in the future under today', async () => {
      const soon = new Date(Date.now() + 5 * 60_000).toISOString();

      await open(admin, [conversation('c-1', 'Az önce', soon)]);

      expect(headings()).toEqual(['Bugün']);
    });

    it('shows a conversation that joins the list later', async () => {
      await open(admin, [conversation('c-1', 'Eski soru')]);

      conversations.update((list) => [conversation('c-2', 'Yeni soru'), ...list]);
      await settle();

      expect(links().map((link) => link.textContent?.trim())).toEqual(['Yeni soru', 'Eski soru']);
    });

    it('marks the conversation that is open', async () => {
      await open(
        admin,
        [conversation('c-2', 'Açık olan'), conversation('c-1', 'Diğeri')],
        '/c/c-2',
      );
      await settle();

      expect(links()[0].classList).toContain('active');
      expect(links()[1].classList).not.toContain('active');
    });

    it('marks none while a new conversation is on screen', async () => {
      await open(admin, [conversation('c-1', 'Eski soru')]);
      await settle();

      expect(links()[0].classList).not.toContain('active');
    });

    it('links "new chat" to the plain address', async () => {
      await open(admin, [], '/c/c-1');

      const link = shell.querySelector<HTMLAnchorElement>('a.new-chat')!;

      expect(link.textContent?.trim()).toContain('Yeni sohbet');
      expect(link.getAttribute('href')).toBe('/');
    });
  });

  // ---- The drawer on narrow screens ----

  describe('the panel as a drawer', () => {
    const menuButton = () => shell.querySelector<HTMLButtonElement>('button.menu-button')!;
    const panel = () => shell.querySelector('aside.panel')!;
    const backdrop = () => shell.querySelector<HTMLElement>('.backdrop');

    it('is closed at first', async () => {
      await open(admin);

      expect(panel().classList).not.toContain('open');
      expect(backdrop()).toBeNull();
      expect(menuButton().getAttribute('aria-expanded')).toBe('false');
    });

    it('opens with the menu button', async () => {
      await open(admin);

      menuButton().click();
      await settle();

      expect(panel().classList).toContain('open');
      expect(backdrop()).not.toBeNull();
      expect(menuButton().getAttribute('aria-expanded')).toBe('true');
    });

    it('closes with a press next to it', async () => {
      await open(admin);
      menuButton().click();
      await settle();

      backdrop()!.click();
      await settle();

      expect(panel().classList).not.toContain('open');
      expect(backdrop()).toBeNull();
    });

    // Choosing a conversation should also put the drawer away.
    it('closes when the user goes to a conversation', async () => {
      await open(admin, [conversation('c-1', 'Yıllık izin kaç gün?')]);
      menuButton().click();
      await settle();

      await harness.navigateByUrl('/c/c-1');
      await settle();

      expect(panel().classList).not.toContain('open');
    });
  });

  // ---- The account ----

  describe('the account', () => {
    it('shows who is signed in', async () => {
      await open(admin);

      expect(shell.querySelector('.account-email')?.textContent).toBe('ayse@example.com');
      expect(shell.querySelector('.avatar')?.textContent).toBe('A');
    });

    // Turkish has two letters where English has one "i": the capital of "i" is "İ".
    it('writes the initial the Turkish way', async () => {
      await open({ ...admin, email: 'ilker@example.com' });

      expect(shell.querySelector('.avatar')?.textContent).toBe('İ');
    });

    it.each([
      ['Admin', 'Yönetici'],
      ['Member', 'Üye'],
    ] as const)('calls a user with the %s role "%s" in the account menu', async (role, label) => {
      await open({ ...admin, role });

      await openAccountMenu();

      expect(document.querySelector('.role')?.textContent).toBe(label);
      expect(document.querySelector('.account-card-email')?.textContent).toBe('ayse@example.com');
    });

    it('has no account menu when nobody is signed in', async () => {
      await open(null);

      expect(shell.querySelector('button.account')).toBeNull();
    });
  });

  describe('signing out', () => {
    it('signs out and shows the login screen', async () => {
      await open(admin);
      await openAccountMenu();

      await pressSignOut();

      expect(logout).toHaveBeenCalledOnce();
      expect(router.url).toBe('/login');
    });

    // The store lives as long as the page does. Without this the next person to sign in
    // on the same computer would find the previous user's conversation still on screen.
    it('clears the conversation that was on screen', async () => {
      await open(admin);
      await openAccountMenu();

      await pressSignOut();

      expect(startNew).toHaveBeenCalledOnce();
    });

    // Only the API can remove the session cookie. If it was not reached the user is
    // still signed in, and must not be shown the login screen as if they were not.
    it('keeps the user where they are and says so when signing out fails', async () => {
      await open(admin);
      logout.mockRejectedValueOnce(new Error('unreachable'));
      await openAccountMenu();

      await pressSignOut();

      expect(router.url).toBe('/');
      expect(page()).toContain('Çıkış yapılamadı. Bağlantınızı kontrol edip yeniden deneyin.');
      expect(startNew).not.toHaveBeenCalled();
    });

    it('says nothing when signing out works', async () => {
      await open(admin);
      await openAccountMenu();

      await pressSignOut();

      expect(page()).not.toContain('Çıkış yapılamadı');
    });
  });
});
