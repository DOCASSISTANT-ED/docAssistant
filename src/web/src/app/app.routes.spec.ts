import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ConversationSummary } from './api/chat';
import { CurrentUser } from './api/identity';
import { routes } from './app.routes';
import { Session } from './auth/session';
import { ChatEntry, ChatStore, OpenState } from './chat/chat-store';

const admin: CurrentUser = {
  userId: 'u-1',
  email: 'ayse@example.com',
  tenantId: 't-1',
  role: 'Admin',
};

// The app's real route table, with the real screens and guards: which address shows
// what, to whom. The session and the chat store are stand-ins, so nothing is sent.
describe('routes', () => {
  let harness: RouterTestingHarness;
  let router: Router;

  const user = signal<CurrentUser | null>(admin);
  const store = {
    conversations: signal<ConversationSummary[]>([]).asReadonly(),
    entries: signal<ChatEntry[]>([]),
    openState: signal<OpenState>('idle'),
    conversationId: signal<string | null>(null),
    answering: signal(false),
    loadConversations: vi.fn(async () => undefined),
    open: vi.fn(async (_id: string) => undefined),
    startNew: vi.fn(),
    ask: vi.fn(async (_question: string) => undefined),
    stop: vi.fn(),
    retry: vi.fn(async () => undefined),
  };

  async function visit(url: string, signedInAs: CurrentUser | null = admin): Promise<void> {
    user.set(signedInAs);
    store.conversationId.set(null);
    store.openState.set('idle');
    vi.clearAllMocks();

    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        {
          provide: Session,
          useValue: {
            user: user.asReadonly(),
            isSignedIn: computed(() => user() !== null),
            isAdmin: computed(() => user()?.role === 'Admin'),
          },
        },
        { provide: ChatStore, useValue: store },
      ],
    });

    harness = await RouterTestingHarness.create(url);
    router = TestBed.inject(Router);
  }

  async function settle(): Promise<void> {
    await new Promise<void>((resolve) => setTimeout(resolve));
    harness.detectChanges();
  }

  const page = () => harness.fixture.nativeElement as HTMLElement;
  const box = () => page().querySelector<HTMLTextAreaElement>('textarea')!;

  describe('for a signed-in user', () => {
    it('shows a new conversation inside the frame at the plain address', async () => {
      await visit('/');

      expect(router.url).toBe('/');
      expect(page().querySelector('app-shell app-chat')).not.toBeNull();
      expect(store.startNew).toHaveBeenCalled();
      expect(store.open).not.toHaveBeenCalled();
    });

    it('shows the conversation named in the address', async () => {
      await visit('/c/c-1');

      expect(router.url).toBe('/c/c-1');
      expect(page().querySelector('app-shell app-chat')).not.toBeNull();
      expect(store.open).toHaveBeenCalledExactlyOnceWith('c-1');
    });

    it.each(['/c', '/c/c-1/fazlası', '/belgeler', '/belgeler/c-1', '/c-1'])(
      'leads home from the unknown address %s',
      async (url) => {
        await visit(url);

        expect(router.url).toBe('/');
        expect(page().querySelector('app-chat')).not.toBeNull();
      },
    );

    it.each(['/login', '/register'])('leads home from %s', async (url) => {
      await visit(url);

      expect(router.url).toBe('/');
    });

    // The reason the chat screen has one route with two addresses: with two routes the
    // router would build the screen anew when a new conversation gets its id, and
    // whatever was being typed would be lost.
    it('keeps what is being typed when a new conversation gets its own address', async () => {
      await visit('/');
      box().value = 'Sonraki sorum';
      box().dispatchEvent(new Event('input'));
      await settle();
      const screen = page().querySelector('app-chat');

      store.conversationId.set('c-new');
      await settle();

      expect(router.url).toBe('/c/c-new');
      expect(page().querySelector('app-chat')).toBe(screen);
      expect(box().value).toBe('Sonraki sorum');
    });

    // "Back" should not stop at the empty new conversation on the way.
    it('replaces the plain address in the history instead of adding to it', async () => {
      await visit('/');
      const navigate = vi.spyOn(router, 'navigate');

      store.conversationId.set('c-new');
      await settle();

      expect(navigate).toHaveBeenCalledExactlyOnceWith(['/c', 'c-new'], { replaceUrl: true });
    });
  });

  describe('for a visitor who is not signed in', () => {
    it('shows the login screen', async () => {
      await visit('/login', null);

      expect(router.url).toBe('/login');
      expect(page().querySelector('app-login')).not.toBeNull();
    });

    it('shows the register screen', async () => {
      await visit('/register', null);

      expect(router.url).toBe('/register');
      expect(page().querySelector('app-register')).not.toBeNull();
    });

    it('leads from the home page to the login screen', async () => {
      await visit('/', null);

      expect(router.url).toBe('/login');
      expect(page().querySelector('app-chat')).toBeNull();
    });

    it('leads from a conversation to the login screen, remembering the conversation', async () => {
      await visit('/c/c-1', null);

      expect(router.url).toBe('/login?returnUrl=%2Fc%2Fc-1');
      expect(store.open).not.toHaveBeenCalled();
    });

    it('leads from an unknown address to the login screen', async () => {
      await visit('/belgeler', null);

      expect(router.url).toBe('/login');
    });
  });

  it.each([
    ['/login', null, 'Giriş yap · DocAssistant'],
    ['/register', null, 'Firma kaydı · DocAssistant'],
    ['/', admin, 'DocAssistant'],
  ] as const)('titles the page at %s', async (url, signedInAs, title) => {
    await visit(url, signedInAs);
    await settle();

    expect(document.title).toBe(title);
  });
});
