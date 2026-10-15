import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { environment } from '../../environments/environment';
import { ConversationSummary } from '../api/chat';
import { CurrentUser } from '../api/identity';
import { routes } from '../app.routes';
import { Session } from '../auth/session';
import { ChatEntry, ChatStore, OpenState } from '../chat/chat-store';

const admin: CurrentUser = { userId: 'u-1', email: 'ayse@example.com', tenantId: 't-1', role: 'Admin' };
const member: CurrentUser = { ...admin, userId: 'u-2', email: 'ali@example.com', role: 'Member' };

// Where the documents page lives (docs/decisions.md #66): /belgeler, inside the signed-in
// frame, for every member; and the link to it in the side panel. The app's real route
// table, screens and guards; the session and the chat store are stand-ins and the API is
// answered by HttpTestingController.
describe('the documents page in the app', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let api: HttpTestingController;

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

  async function visit(url: string, signedInAs: CurrentUser | null): Promise<void> {
    user.set(signedInAs);

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
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });

    harness = await RouterTestingHarness.create(url);
    router = TestBed.inject(Router);
    api = TestBed.inject(HttpTestingController);
    await settle();
  }

  async function settle(): Promise<void> {
    await new Promise<void>((resolve) => setTimeout(resolve));
    harness.detectChanges();
  }

  // The page asks for the list as soon as it opens; it is answered with no documents.
  async function answerList(): Promise<void> {
    api.match(`${environment.apiBaseUrl}/documents`).forEach((request) => request.flush([]));
    await settle();
  }

  afterEach(() => {
    api?.verify();
    document.body.replaceChildren();
  });

  const page = () => harness.fixture.nativeElement as HTMLElement;
  const link = () => page().querySelector<HTMLAnchorElement>('a.page-link[href="/belgeler"]');

  it.each([
    ['an admin', admin],
    ['a member', member],
  ])('opens the documents page at /belgeler for %s, inside the frame', async (_who, signedInAs) => {
    await visit('/belgeler', signedInAs);
    await answerList();

    expect(router.url).toBe('/belgeler');
    expect(page().querySelector('app-documents h1')?.textContent?.trim()).toBe('Belgeler');
    // The side panel of the signed-in frame is there too.
    expect(link()).not.toBeNull();
  });

  it('titles the page', async () => {
    await visit('/belgeler', admin);
    await answerList();

    expect(document.title).toBe('Belgeler · DocAssistant');
  });

  it('leads a visitor who is not signed in to the login screen, remembering the page', async () => {
    await visit('/belgeler', null);

    expect(router.url).toBe('/login?returnUrl=%2Fbelgeler');
  });

  describe('the link in the side panel', () => {
    it.each([
      ['an admin', admin],
      ['a member', member],
    ])('is there for %s and leads to /belgeler', async (_who, signedInAs) => {
      await visit('/', signedInAs);

      expect(link()?.textContent?.trim()).toBe('Belgeler');
      expect(link()?.getAttribute('href')).toBe('/belgeler');
    });

    it('takes the user from the chat to the documents page', async () => {
      await visit('/', admin);

      link()!.click();
      await settle();
      await answerList();

      expect(router.url).toBe('/belgeler');
      expect(page().querySelector('app-documents')).not.toBeNull();
    });

    it('is marked while the documents page is open, and only then', async () => {
      await visit('/belgeler', admin);
      await answerList();
      expect(link()!.classList).toContain('active');

      await harness.navigateByUrl('/');
      await settle();
      expect(link()!.classList).not.toContain('active');
    });
  });
});
