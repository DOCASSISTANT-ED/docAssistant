import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Chat } from './chat';
import { AnswerEntry, ChatEntry, ChatStore, OpenState } from './chat-store';

@Component({ template: '' })
class Blank {}

function question(key: string, text: string): ChatEntry {
  return { kind: 'question', key, text };
}

function answer(key: string, details: Partial<AnswerEntry> = {}): ChatEntry {
  return {
    kind: 'answer',
    key,
    text: 'Cevap.',
    sources: [],
    state: 'done',
    failure: null,
    ...details,
  };
}

// The chat screen, driven the way a user drives it. The store behind it is a stand-in:
// the test sets what the store holds and sees what the screen asks of it. The store's
// own behaviour is covered in chat-store.spec.ts.
describe('Chat', () => {
  let harness: RouterTestingHarness;
  let screen: HTMLElement;
  let router: Router;

  const store = {
    entries: signal<ChatEntry[]>([]),
    openState: signal<OpenState>('idle'),
    conversationId: signal<string | null>(null),
    answering: signal(false),
    open: vi.fn(async (_id: string) => undefined),
    startNew: vi.fn(),
    ask: vi.fn(async (_question: string) => undefined),
    stop: vi.fn(),
    retry: vi.fn(async () => undefined),
  };

  async function open(url = '/'): Promise<void> {
    store.entries.set([]);
    store.openState.set('idle');
    store.conversationId.set(null);
    store.answering.set(false);
    vi.clearAllMocks();

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', pathMatch: 'full', component: Chat },
          { path: 'c/:id', component: Chat },
          { path: 'login', component: Blank },
        ]),
        { provide: ChatStore, useValue: store },
      ],
    });

    harness = await RouterTestingHarness.create(url);
    screen = harness.routeNativeElement!;
    router = TestBed.inject(Router);
  }

  // Lets the screen take in a change and, where it started one, a navigation.
  async function settle(): Promise<void> {
    await new Promise<void>((resolve) => setTimeout(resolve));
    harness.detectChanges();
    screen = harness.routeNativeElement!;
  }

  const box = () => screen.querySelector<HTMLTextAreaElement>('textarea')!;
  const sendButton = () =>
    screen.querySelector<HTMLButtonElement>('button[aria-label="Soruyu gönder"]');
  const stopButton = () =>
    screen.querySelector<HTMLButtonElement>('button[aria-label="Cevabı durdur"]');
  const textOf = (selector: string) => screen.querySelector(selector)?.textContent?.trim() ?? null;

  async function type(text: string): Promise<void> {
    box().value = text;
    box().dispatchEvent(new Event('input'));
    await settle();
  }

  async function press(key: Partial<KeyboardEventInit>): Promise<KeyboardEvent> {
    const event = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true, ...key });
    box().dispatchEvent(event);
    await settle();

    return event;
  }

  async function submit(): Promise<void> {
    screen.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
  }

  // ---- What the address shows ----

  describe('the address', () => {
    it('starts a new conversation at the plain address', async () => {
      await open('/');

      expect(store.startNew).toHaveBeenCalledOnce();
      expect(store.open).not.toHaveBeenCalled();
    });

    it('opens the conversation named in the address', async () => {
      await open('/c/c-1');

      expect(store.open).toHaveBeenCalledExactlyOnceWith('c-1');
      expect(store.startNew).not.toHaveBeenCalled();
    });

    it('opens the other conversation when the address changes', async () => {
      await open('/c/c-1');

      await harness.navigateByUrl('/c/c-2');

      expect(store.open).toHaveBeenLastCalledWith('c-2');
    });

    // A new conversation gets its id with its first answer. A reload should then show
    // the same conversation, and "back" should not stop at the empty one on the way.
    it('follows a new conversation to its own address once it has an id', async () => {
      await open('/');

      store.conversationId.set('c-new');
      await settle();

      expect(router.url).toBe('/c/c-new');
    });

    it('stays where it is when the open conversation is the one in the address', async () => {
      await open('/c/c-1');
      const navigate = vi.spyOn(router, 'navigate');

      store.conversationId.set('c-1');
      await settle();

      expect(navigate).not.toHaveBeenCalled();
    });
  });

  // ---- What is on screen ----

  describe('a new conversation', () => {
    it('shows the welcome and the box to ask in', async () => {
      await open('/');

      expect(screen.querySelector('app-welcome')).not.toBeNull();
      expect(box().getAttribute('placeholder')).toBe('Belgelerinize bir soru sorun');
      expect(textOf('.hint')).toBe('Cevaplar yalnızca yüklenen belgelere dayanır.');
    });
  });

  describe('a conversation with questions and answers', () => {
    it('shows them in order, and no welcome', async () => {
      await open('/c/c-1');

      store.openState.set('ready');
      store.entries.set([
        question('q-1', 'Yıllık izin kaç gün?'),
        answer('a-1', { text: '14 gündür.' }),
        question('q-2', 'Peki ne zaman talep edilir?'),
        answer('a-2', { text: 'On gün önce.' }),
      ]);
      await settle();

      expect(screen.querySelector('app-welcome')).toBeNull();
      expect(
        Array.from(screen.querySelectorAll('.question .bubble')).map((b) => b.textContent),
      ).toEqual(['Yıllık izin kaç gün?', 'Peki ne zaman talep edilir?']);
      expect(
        Array.from(screen.querySelectorAll('app-answer .text')).map((t) => t.textContent?.trim()),
      ).toEqual(['14 gündür.', 'On gün önce.']);
    });

    // A question is the user's own text and is shown as written, never as HTML.
    it('shows a question as plain text', async () => {
      await open('/');

      store.entries.set([question('q-1', '<b>kalın</b> & <script>x</script>')]);
      await settle();

      expect(textOf('.question .bubble')).toBe('<b>kalın</b> & <script>x</script>');
      expect(screen.querySelector('.question b')).toBeNull();
    });

    it('offers to try again only under the last answer', async () => {
      await open('/c/c-1');

      store.openState.set('ready');
      store.entries.set([
        question('q-1', 'Bir'),
        answer('a-1', { state: 'stopped' }),
        question('q-2', 'İki'),
        answer('a-2', { state: 'failed', failure: 'Cevap üretilemedi. Yeniden deneyin.' }),
      ]);
      await settle();

      const answers = screen.querySelectorAll('app-answer');
      expect(answers[0].querySelector('button.retry')).toBeNull();
      expect(answers[1].querySelector('button.retry')).not.toBeNull();
    });

    it('asks the store to try again when the button is pressed', async () => {
      await open('/');
      store.entries.set([
        question('q-1', 'Bir'),
        answer('a-1', { state: 'failed', failure: 'Hata.' }),
      ]);
      await settle();

      screen.querySelector<HTMLButtonElement>('button.retry')!.click();

      expect(store.retry).toHaveBeenCalledOnce();
    });
  });

  describe('a conversation that is loading', () => {
    it('says so and does not let a question be sent yet', async () => {
      await open('/c/c-1');

      store.openState.set('loading');
      await settle();
      await type('Yıllık izin kaç gün?');

      expect(screen.querySelector('[role="status"]')!.getAttribute('aria-label')).toBe(
        'Sohbet yükleniyor',
      );
      expect(sendButton()!.disabled).toBe(true);

      await submit();
      expect(store.ask).not.toHaveBeenCalled();
    });
  });

  // docs/decisions.md #55: an unknown conversation and somebody else's look the same.
  describe('a conversation that does not exist', () => {
    it('says so, offers a new one, and has no box to ask in', async () => {
      await open('/c/c-x');

      store.openState.set('notFound');
      await settle();

      expect(textOf('.status-title')).toBe('Bu sohbet bulunamadı');
      expect(screen.querySelector<HTMLAnchorElement>('a.status-action')!.getAttribute('href')).toBe(
        '/',
      );
      expect(screen.querySelector('textarea')).toBeNull();
    });
  });

  describe('a conversation that could not be loaded', () => {
    it('says so as an alert and has no box to ask in', async () => {
      await open('/c/c-1');

      store.openState.set('failed');
      await settle();

      expect(screen.querySelector('[role="alert"] .status-title')?.textContent).toBe(
        'Sohbet yüklenemedi',
      );
      expect(screen.querySelector('textarea')).toBeNull();
    });

    it('loads the conversation in the address again when asked to', async () => {
      await open('/c/c-1');
      store.openState.set('failed');
      await settle();
      store.open.mockClear();

      screen.querySelector<HTMLButtonElement>('button.status-action')!.click();

      expect(store.open).toHaveBeenCalledExactlyOnceWith('c-1');
    });
  });

  // ---- Asking ----

  describe('sending a question', () => {
    it('sends what was typed and empties the box', async () => {
      await open('/');
      await type('Yıllık izin kaç gün?');

      await submit();

      expect(store.ask).toHaveBeenCalledExactlyOnceWith('Yıllık izin kaç gün?');
      expect(box().value).toBe('');
    });

    it('sends with Enter', async () => {
      await open('/');
      await type('Yıllık izin kaç gün?');

      const event = await press({});

      expect(store.ask).toHaveBeenCalledExactlyOnceWith('Yıllık izin kaç gün?');
      // Otherwise the box would also get the line break.
      expect(event.defaultPrevented).toBe(true);
    });

    // As in most chat apps.
    it('starts a new line with Shift+Enter instead of sending', async () => {
      await open('/');
      await type('İlk satır');

      const event = await press({ shiftKey: true });

      expect(store.ask).not.toHaveBeenCalled();
      expect(event.defaultPrevented).toBe(false);
    });

    // With an input method (Japanese, Chinese, Korean) Enter confirms the word being
    // composed; it must not send half a sentence.
    it('does not send while a word is being composed', async () => {
      await open('/');
      await type('Yıllık');

      await press({ isComposing: true });

      expect(store.ask).not.toHaveBeenCalled();
    });

    it.each(['', '   ', '\n\n'])('does not send an empty question: "%s"', async (text) => {
      await open('/');
      await type(text);

      expect(sendButton()!.disabled).toBe(true);

      await submit();
      await press({});
      expect(store.ask).not.toHaveBeenCalled();
    });

    it('lets the button be pressed once something is typed', async () => {
      await open('/');
      expect(sendButton()!.disabled).toBe(true);

      await type('Yıllık izin kaç gün?');

      expect(sendButton()!.disabled).toBe(false);
    });

    it('sends in the conversation that is open', async () => {
      await open('/c/c-1');
      store.openState.set('ready');
      await settle();
      await type('Peki ne zaman?');

      await submit();

      expect(store.ask).toHaveBeenCalledExactlyOnceWith('Peki ne zaman?');
    });
  });

  // ChatRequest.QuestionMaxLength on the server.
  describe('the longest question', () => {
    it('is what the box accepts at most', async () => {
      await open('/');

      expect(box().getAttribute('maxlength')).toBe('2000');
    });

    it('is counted down to once it comes near', async () => {
      await open('/');

      await type('a'.repeat(1800));
      expect(screen.querySelector('.counter')).toBeNull();

      await type('a'.repeat(1801));
      expect(textOf('.counter')).toBe('1801 / 2000');
      expect(screen.querySelector('.hint')).toBeNull();
    });
  });

  describe('while an answer is being written', () => {
    it('turns the send button into one that stops the answer', async () => {
      await open('/');

      store.answering.set(true);
      await settle();

      expect(sendButton()).toBeNull();
      expect(stopButton()).not.toBeNull();

      stopButton()!.click();
      expect(store.stop).toHaveBeenCalledOnce();
    });

    it('does not send what is typed meanwhile, and keeps it in the box', async () => {
      await open('/');
      store.answering.set(true);
      await settle();
      await type('Sonraki sorum');

      await press({});
      await submit();

      expect(store.ask).not.toHaveBeenCalled();
      expect(box().value).toBe('Sonraki sorum');
    });

    // The stop button sits inside the form; pressing it must not send the form.
    it("has a stop button that is not the form's own button", async () => {
      await open('/');
      store.answering.set(true);
      await settle();

      expect(stopButton()!.getAttribute('type')).toBe('button');
    });

    it('gives the send button back when the answer has ended', async () => {
      await open('/');
      store.answering.set(true);
      await settle();

      store.answering.set(false);
      await settle();

      expect(stopButton()).toBeNull();
      expect(sendButton()).not.toBeNull();
    });
  });
});
