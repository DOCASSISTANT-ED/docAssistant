import { HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import {
  ChatRequest,
  ChatSource,
  ChatStreamEvent,
  Conversation,
  ConversationSummary,
  ConversationsApi,
} from '../api/chat';
import { ChatRequestError, ChatStream } from '../api/chat-stream';
import { Session } from '../auth/session';
import { AnswerEntry, ChatStore } from './chat-store';

@Component({ template: '' })
class Blank {}

const source: ChatSource = {
  number: 1,
  documentId: 'd-1',
  documentTitle: 'İK Yönetmeliği',
  sectionPath: 'Bölüm 2',
  pageNumber: 3,
};

function summary(
  id: string,
  title: string,
  createdAt = new Date().toISOString(),
): ConversationSummary {
  return { id, title, createdAt };
}

function conversation(id: string): Conversation {
  return {
    id,
    title: 'Yıllık izin kaç gün?',
    createdAt: '2026-10-13T11:00:00Z',
    messages: [
      {
        id: 'm-1',
        role: 'user',
        content: 'Yıllık izin kaç gün?',
        sources: [],
        createdAt: '2026-10-13T11:00:00Z',
      },
      {
        id: 'm-2',
        role: 'assistant',
        content: '14 gündür [1].',
        sources: [source],
        createdAt: '2026-10-13T11:00:05Z',
      },
    ],
  };
}

// One answer stream that the test feeds by hand: send events, end it, or break it.
// It stops with an AbortError when its signal is aborted, as the real one does.
class Answer {
  request!: ChatRequest;

  private readonly queue: (ChatStreamEvent | Error | null)[] = [];
  private wake: (() => void) | null = null;

  send(...events: ChatStreamEvent[]): void {
    this.push(...events);
  }

  // The stream is over. Without a "done" or "error" before it, the connection was lost.
  end(): void {
    this.push(null);
  }

  fail(error: Error): void {
    this.push(error);
  }

  async *events(signal?: AbortSignal): AsyncGenerator<ChatStreamEvent> {
    while (true) {
      while (this.queue.length === 0) {
        await new Promise<void>((resolve, reject) => {
          this.wake = resolve;
          signal?.addEventListener(
            'abort',
            () => reject(new DOMException('The operation was aborted.', 'AbortError')),
            { once: true },
          );
        });
      }

      const next = this.queue.shift()!;

      if (next === null) {
        return;
      }

      if (next instanceof Error) {
        throw next;
      }

      yield next;
    }
  }

  private push(...items: (ChatStreamEvent | Error | null)[]): void {
    this.queue.push(...items);
    this.wake?.();
    this.wake = null;
  }
}

// The chat screen's state. The API is replaced by stand-ins: the history calls answer
// what the test says, and each question gets an answer stream the test feeds by hand.
describe('ChatStore', () => {
  let store: ChatStore;
  let router: Router;

  let list: ReturnType<typeof vi.fn<() => Observable<ConversationSummary[]>>>;
  let get: ReturnType<typeof vi.fn<(id: string) => Observable<Conversation>>>;
  let expire: ReturnType<typeof vi.fn>;

  // One per question asked, in order.
  let answers: Answer[];

  beforeEach(() => {
    answers = [];
    list = vi.fn(() => of([]));
    get = vi.fn((id: string) => of(conversation(id)));
    expire = vi.fn();

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', component: Blank },
          { path: 'sohbet/:id', component: Blank },
          { path: '', pathMatch: 'full', component: Blank },
        ]),
        { provide: ConversationsApi, useValue: { list, get } },
        {
          provide: ChatStream,
          useValue: {
            ask: (request: ChatRequest, signal?: AbortSignal) => {
              const answer = new Answer();
              answer.request = request;
              answers.push(answer);

              return answer.events(signal);
            },
          },
        },
        { provide: Session, useValue: { expire } },
      ],
    });

    store = TestBed.inject(ChatStore);
    router = TestBed.inject(Router);
  });

  // Lets the store take in what was sent, up to where it waits for more.
  const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

  const lastAnswer = () => store.entries().at(-1) as AnswerEntry;

  // Asks and waits until the question is on its way; the answer is still open.
  async function startAsking(
    question = 'Yıllık izin kaç gün?',
  ): Promise<{ answer: Answer; ended: Promise<void> }> {
    const before = answers.length;
    const ended = store.ask(question);
    await settle();

    return { answer: answers[before], ended };
  }

  // A whole exchange: the question, a short answer, "done".
  async function askAndFinish(question: string, conversationId = 'c-1'): Promise<void> {
    const { answer, ended } = await startAsking(question);

    answer.send(
      { type: 'sources', sources: [source] },
      { type: 'delta', text: 'Cevap [1].' },
      { type: 'done', conversationId, messageId: 'm-9' },
    );
    answer.end();
    await ended;
  }

  it('starts with nothing open', () => {
    expect(store.conversations()).toEqual([]);
    expect(store.conversationId()).toBeNull();
    expect(store.entries()).toEqual([]);
    expect(store.openState()).toBe('idle');
    expect(store.answering()).toBe(false);
  });

  describe('the list of conversations', () => {
    it('holds what the API answers', async () => {
      const conversations = [
        summary('c-2', 'Harcırah ne kadar?'),
        summary('c-1', 'Yıllık izin kaç gün?'),
      ];
      list.mockReturnValueOnce(of(conversations));

      await store.loadConversations();

      expect(store.conversations()).toEqual(conversations);
    });

    // The chat works without the side panel; a failed refresh must not empty it.
    it('stays as it was when it cannot be loaded', async () => {
      list.mockReturnValueOnce(of([summary('c-1', 'Yıllık izin kaç gün?')]));
      await store.loadConversations();
      list.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));

      await expect(store.loadConversations()).resolves.toBeUndefined();

      expect(store.conversations().map((c) => c.id)).toEqual(['c-1']);
    });
  });

  describe('opening a conversation from the history', () => {
    it('shows its questions and answers in order', async () => {
      await store.open('c-1');

      expect(get).toHaveBeenCalledExactlyOnceWith('c-1');
      expect(store.openState()).toBe('ready');
      expect(store.conversationId()).toBe('c-1');
      expect(store.entries()).toEqual([
        { kind: 'question', key: 'm-1', text: 'Yıllık izin kaç gün?' },
        {
          kind: 'answer',
          key: 'm-2',
          text: '14 gündür [1].',
          sources: [source],
          state: 'done',
          failure: null,
        },
      ]);
    });

    it('says it is loading until the API has answered', async () => {
      const pending = new Subject<Conversation>();
      get.mockReturnValueOnce(pending);

      const opening = store.open('c-1');

      expect(store.openState()).toBe('loading');
      expect(store.entries()).toEqual([]);

      pending.next(conversation('c-1'));
      pending.complete();
      await opening;
      expect(store.openState()).toBe('ready');
    });

    // docs/decisions.md #55: unknown and somebody else's look the same.
    it('says "not found" for a 404', async () => {
      get.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 404 })));

      await store.open('c-x');

      expect(store.openState()).toBe('notFound');
      expect(store.entries()).toEqual([]);
    });

    it.each([0, 500])('says "failed" for a %s answer', async (status) => {
      get.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status })));

      await store.open('c-1');

      expect(store.openState()).toBe('failed');
    });

    it('does not load a conversation again that is already on screen', async () => {
      await store.open('c-1');

      await store.open('c-1');

      expect(get).toHaveBeenCalledOnce();
    });

    it('tries again when the last attempt failed', async () => {
      get.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
      await store.open('c-1');

      await store.open('c-1');

      expect(get).toHaveBeenCalledTimes(2);
      expect(store.openState()).toBe('ready');
    });

    // The user clicked a second conversation before the first had loaded.
    it('ignores the answer for a conversation the user has moved on from', async () => {
      const slow = new Subject<Conversation>();
      get.mockReturnValueOnce(slow);
      const first = store.open('c-1');

      await store.open('c-2');
      slow.next({ ...conversation('c-1'), messages: [] });
      slow.complete();
      await first;

      expect(store.conversationId()).toBe('c-2');
      expect(store.entries()).toHaveLength(2);
      expect(store.openState()).toBe('ready');
    });

    it('ignores the failure for a conversation the user has moved on from', async () => {
      const slow = new Subject<Conversation>();
      get.mockReturnValueOnce(slow);
      const first = store.open('c-1');

      await store.open('c-2');
      slow.error(new HttpErrorResponse({ status: 404 }));
      await first;

      expect(store.openState()).toBe('ready');
    });
  });

  describe('starting a new conversation', () => {
    it('clears the screen', async () => {
      await store.open('c-1');

      store.startNew();

      expect(store.conversationId()).toBeNull();
      expect(store.entries()).toEqual([]);
      expect(store.openState()).toBe('idle');
    });

    it('stops the answer that was being written', async () => {
      const { ended } = await startAsking();

      store.startNew();
      await ended;

      expect(store.answering()).toBe(false);
      expect(store.entries()).toEqual([]);
    });
  });

  describe('asking', () => {
    it('shows the question and an answer that is waiting', async () => {
      await startAsking('Yıllık izin kaç gün?');

      expect(store.entries()).toEqual([
        { kind: 'question', key: expect.any(String), text: 'Yıllık izin kaç gün?' },
        {
          kind: 'answer',
          key: expect.any(String),
          text: '',
          sources: [],
          state: 'waiting',
          failure: null,
        },
      ]);
      expect(store.answering()).toBe(true);
    });

    it('sends the question without the spaces around it', async () => {
      const { answer } = await startAsking('   Yıllık izin kaç gün?  ');

      expect(answer.request).toEqual({ question: 'Yıllık izin kaç gün?' });
      expect(store.entries()[0]).toMatchObject({ text: 'Yıllık izin kaç gün?' });
    });

    // The server would reject them (ChatRequest: not empty, at most 2,000 characters).
    it.each([
      ['an empty question', ''],
      ['a question of spaces', '   '],
      ['a question that is too long', 'a'.repeat(2001)],
    ])('sends nothing for %s', async (_what, question) => {
      await store.ask(question);

      expect(answers).toHaveLength(0);
      expect(store.entries()).toEqual([]);
    });

    it('sends a question of exactly the longest length', async () => {
      await startAsking('a'.repeat(2000));

      expect(answers).toHaveLength(1);
    });

    it('sends nothing while an answer is being written', async () => {
      await startAsking('İlk soru');

      await store.ask('İkinci soru');

      expect(answers).toHaveLength(1);
      expect(store.entries()).toHaveLength(2);
    });

    it('fills in the answer as it arrives', async () => {
      const { answer } = await startAsking();

      answer.send({ type: 'sources', sources: [source] });
      await settle();
      expect(lastAnswer()).toMatchObject({ sources: [source], text: '', state: 'waiting' });

      answer.send({ type: 'delta', text: 'Yıllık izin ' });
      await settle();
      expect(lastAnswer()).toMatchObject({ text: 'Yıllık izin ', state: 'streaming' });
      expect(store.answering()).toBe(true);

      answer.send({ type: 'delta', text: '14 gündür [1].' });
      await settle();
      expect(lastAnswer()).toMatchObject({
        text: 'Yıllık izin 14 gündür [1].',
        state: 'streaming',
      });
    });

    it('marks the answer complete on "done"', async () => {
      await askAndFinish('Yıllık izin kaç gün?');

      expect(lastAnswer()).toMatchObject({ text: 'Cevap [1].', state: 'done', failure: null });
      expect(store.answering()).toBe(false);
    });

    it('gives every entry a key of its own', async () => {
      await askAndFinish('Bir');
      await askAndFinish('İki');

      const keys = store.entries().map((entry) => entry.key);
      expect(new Set(keys).size).toBe(4);
    });
  });

  describe('a new conversation after its first answer', () => {
    it('gets the id the server gave it', async () => {
      await askAndFinish('Yıllık izin kaç gün?', 'c-new');

      expect(store.conversationId()).toBe('c-new');
      expect(store.openState()).toBe('ready');
    });

    it('joins the list on top, titled like the server titles it', async () => {
      list.mockReturnValueOnce(of([summary('c-old', 'Eski sohbet')]));
      await store.loadConversations();
      const question = 'Çok uzun bir soru '.repeat(10).trim();

      await askAndFinish(question, 'c-new');

      expect(store.conversations().map((c) => c.id)).toEqual(['c-new', 'c-old']);
      // docs/decisions.md #57: the first 80 characters of the first question.
      expect(store.conversations()[0].title).toBe(question.slice(0, 80));
    });

    it('continues the same conversation with the next question', async () => {
      await askAndFinish('Yıllık izin kaç gün?', 'c-new');

      const { answer } = await startAsking('Peki ne zaman talep edilir?');

      expect(answer.request).toEqual({
        conversationId: 'c-new',
        question: 'Peki ne zaman talep edilir?',
      });
    });

    it('is listed once, however many questions follow', async () => {
      await askAndFinish('Bir', 'c-new');
      await askAndFinish('İki', 'c-new');

      expect(store.conversations().map((c) => c.id)).toEqual(['c-new']);
      expect(store.conversations()[0].title).toBe('Bir');
    });

    // The server creates the conversation when the question arrives, so a refresh of the
    // list during the answer already brings it; "done" must not add it a second time.
    it('is listed once when the list was refreshed while the answer was being written', async () => {
      const { answer, ended } = await startAsking('Yıllık izin kaç gün?');
      list.mockReturnValueOnce(of([summary('c-new', 'Yıllık izin kaç gün?')]));
      await store.loadConversations();

      answer.send(
        { type: 'delta', text: '14 gün.' },
        { type: 'done', conversationId: 'c-new', messageId: 'm-9' },
      );
      answer.end();
      await ended;

      expect(store.conversations().map((c) => c.id)).toEqual(['c-new']);
      expect(store.conversationId()).toBe('c-new');
    });

    it('is not opened again from the history when the address changes to its id', async () => {
      await askAndFinish('Yıllık izin kaç gün?', 'c-new');

      await store.open('c-new');

      expect(get).not.toHaveBeenCalled();
      expect(store.entries()).toHaveLength(2);
    });
  });

  describe('a question in a conversation from the history', () => {
    it('names the conversation and adds to what is on screen', async () => {
      await store.open('c-1');

      const { answer } = await startAsking('Peki ne zaman?');

      expect(answer.request).toEqual({ conversationId: 'c-1', question: 'Peki ne zaman?' });
      expect(store.entries()).toHaveLength(4);
    });

    it('does not add the conversation to the list a second time', async () => {
      list.mockReturnValueOnce(of([summary('c-1', 'Yıllık izin kaç gün?')]));
      await store.loadConversations();
      await store.open('c-1');

      await askAndFinish('Peki ne zaman?', 'c-1');

      expect(store.conversations().map((c) => c.id)).toEqual(['c-1']);
    });
  });

  describe('an answer that fails', () => {
    // docs/decisions.md #56: instead of "done"; nothing was saved.
    it('says so when the server reports an error', async () => {
      const { answer, ended } = await startAsking();

      answer.send(
        { type: 'delta', text: 'Yarım ' },
        { type: 'error', message: 'The answer could not be generated.' },
      );
      answer.end();
      await ended;

      expect(lastAnswer()).toMatchObject({
        state: 'failed',
        failure: 'Cevap üretilemedi. Yeniden deneyin.',
        text: 'Yarım ',
      });
      expect(store.answering()).toBe(false);
    });

    it('says the connection was lost when the stream ends without saying how', async () => {
      const { answer, ended } = await startAsking();

      answer.send({ type: 'delta', text: 'Yarım ' });
      answer.end();
      await ended;

      expect(lastAnswer()).toMatchObject({
        state: 'failed',
        failure: 'Bağlantı kesildi; cevap tamamlanamadı. Yeniden deneyin.',
      });
    });

    it('says the server cannot be reached when the request itself fails', async () => {
      const { answer, ended } = await startAsking();

      answer.fail(new TypeError('Failed to fetch'));
      await ended;

      expect(lastAnswer()).toMatchObject({
        state: 'failed',
        failure: 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.',
      });
    });

    it.each([
      [404, 'Bu sohbet artık bulunamıyor. Yeni bir sohbet başlatın.'],
      [400, 'Soru gönderilemedi. Soruyu kısaltıp yeniden deneyin.'],
      [500, 'Cevap alınamadı. Biraz sonra yeniden deneyin.'],
    ])('explains a request the API turned down with %s', async (status, message) => {
      const { answer, ended } = await startAsking();

      answer.fail(new ChatRequestError(status, null));
      await ended;

      expect(lastAnswer()).toMatchObject({ state: 'failed', failure: message });
      expect(expire).not.toHaveBeenCalled();
    });

    it('never throws, whatever goes wrong', async () => {
      const { answer, ended } = await startAsking();

      answer.fail(new Error('boom'));

      await expect(ended).resolves.toBeUndefined();
    });
  });

  // The session lasts 60 minutes (docs/decisions.md #60). Other requests are handled by
  // an interceptor; this one uses fetch, so the store does the same by hand.
  describe('a question asked after the session has ended', () => {
    it('forgets the user and shows the login screen, remembering where they were', async () => {
      await router.navigateByUrl('/sohbet/c-1');
      const { answer, ended } = await startAsking();

      answer.fail(new ChatRequestError(401, null));
      await ended;
      await settle();

      expect(expire).toHaveBeenCalledOnce();
      expect(router.url).toBe('/login?returnUrl=%2Fsohbet%2Fc-1');
      expect(lastAnswer()).toMatchObject({
        state: 'failed',
        failure: 'Oturumunuz sona erdi. Yeniden giriş yapın.',
      });
    });
  });

  describe('stopping an answer', () => {
    it('keeps what was written and marks the answer stopped', async () => {
      const { answer, ended } = await startAsking();
      answer.send({ type: 'sources', sources: [source] }, { type: 'delta', text: 'Yıllık izin ' });
      await settle();

      store.stop();
      await ended;

      expect(lastAnswer()).toMatchObject({
        state: 'stopped',
        text: 'Yıllık izin ',
        sources: [source],
        failure: null,
      });
      expect(store.answering()).toBe(false);
    });

    it('lets the next question be asked', async () => {
      const { ended } = await startAsking('Bir');
      store.stop();
      await ended;

      await startAsking('İki');

      expect(answers).toHaveLength(2);
    });

    it('does nothing when no answer is being written', async () => {
      await askAndFinish('Bir');

      store.stop();

      expect(lastAnswer().state).toBe('done');
    });
  });

  // A first question that did not reach "done" still created its conversation on the
  // server (the question is saved before the answer, docs/decisions.md #56), but the id
  // only comes with "done". Without it the next question would start yet another one.
  describe('a first question whose answer did not finish', () => {
    it('finds the conversation it started and continues it', async () => {
      const { answer, ended } = await startAsking('Yıllık izin kaç gün?');
      list.mockReturnValueOnce(
        of([summary('c-new', 'Yıllık izin kaç gün?'), summary('c-old', 'Eski sohbet')]),
      );

      answer.end();
      await ended;

      expect(store.conversationId()).toBe('c-new');
      expect(store.conversations().map((c) => c.id)).toEqual(['c-new', 'c-old']);

      const next = await startAsking('Yeniden soruyorum');
      expect(next.answer.request).toEqual({
        conversationId: 'c-new',
        question: 'Yeniden soruyorum',
      });
    });

    // The server saved the question and created the conversation before the model failed
    // (decisions #56), exactly as when the connection is lost. Without finding it, "Try
    // again" would start a second conversation, and the first would stay in the history
    // with a question and no answer.
    it('finds it after the server reported an error too', async () => {
      const { answer, ended } = await startAsking('Yıllık izin kaç gün?');
      list.mockReturnValueOnce(of([summary('c-new', 'Yıllık izin kaç gün?')]));

      answer.send({ type: 'error', message: 'The answer could not be generated.' });
      answer.end();
      await ended;

      expect(store.conversationId()).toBe('c-new');

      void store.retry();
      await settle();
      expect(answers[1].request).toEqual({
        conversationId: 'c-new',
        question: 'Yıllık izin kaç gün?',
      });
    });

    it('finds it after the user stopped the answer too', async () => {
      const { ended } = await startAsking('Yıllık izin kaç gün?');
      list.mockReturnValueOnce(of([summary('c-new', 'Yıllık izin kaç gün?')]));

      store.stop();
      await ended;

      expect(store.conversationId()).toBe('c-new');
    });

    // The server cuts the title at 80 characters.
    it('recognizes it by a title that is the start of a long question', async () => {
      const question = 'Çok uzun bir soru '.repeat(10).trim();
      const { answer, ended } = await startAsking(question);
      list.mockReturnValueOnce(of([summary('c-new', question.slice(0, 80))]));

      answer.end();
      await ended;

      expect(store.conversationId()).toBe('c-new');
    });

    it('does not take a conversation with another title', async () => {
      const { answer, ended } = await startAsking('Yıllık izin kaç gün?');
      list.mockReturnValueOnce(of([summary('c-other', 'Harcırah ne kadar?')]));

      answer.end();
      await ended;

      expect(store.conversationId()).toBeNull();
    });

    // The same question asked last week is another conversation.
    it('does not take an older conversation with the same title', async () => {
      const { answer, ended } = await startAsking('Yıllık izin kaç gün?');
      const lastWeek = new Date(Date.now() - 7 * 24 * 60 * 60 * 1000).toISOString();
      list.mockReturnValueOnce(of([summary('c-old', 'Yıllık izin kaç gün?', lastWeek)]));

      answer.end();
      await ended;

      expect(store.conversationId()).toBeNull();
    });

    it('does not look for one when the question continued a conversation', async () => {
      await store.open('c-1');
      list.mockClear();
      const { answer, ended } = await startAsking('Peki ne zaman?');

      answer.end();
      await ended;

      expect(list).not.toHaveBeenCalled();
      expect(store.conversationId()).toBe('c-1');
    });

    it('does not look for one when the answer finished', async () => {
      await askAndFinish('Yıllık izin kaç gün?');

      expect(list).not.toHaveBeenCalled();
    });
  });

  describe('trying again', () => {
    it('asks the last question again in place of the failed attempt', async () => {
      const first = await startAsking('Yıllık izin kaç gün?');
      first.answer.send({ type: 'error', message: 'failed' });
      first.answer.end();
      await first.ended;

      const retrying = store.retry();
      await settle();

      expect(answers).toHaveLength(2);
      expect(answers[1].request.question).toBe('Yıllık izin kaç gün?');
      expect(store.entries()).toHaveLength(2);
      expect(lastAnswer()).toMatchObject({ state: 'waiting', failure: null, text: '' });

      answers[1].send(
        { type: 'delta', text: '14 gün.' },
        { type: 'done', conversationId: 'c-1', messageId: 'm-9' },
      );
      answers[1].end();
      await retrying;
      expect(lastAnswer()).toMatchObject({ state: 'done', text: '14 gün.' });
    });

    it('works after the user stopped the answer', async () => {
      const first = await startAsking('Yıllık izin kaç gün?');
      store.stop();
      await first.ended;

      void store.retry();
      await settle();

      expect(answers).toHaveLength(2);
      expect(store.entries()).toHaveLength(2);
    });

    it('keeps the earlier questions and answers on screen', async () => {
      await store.open('c-1');
      const first = await startAsking('Peki ne zaman?');
      first.answer.send({ type: 'error', message: 'failed' });
      first.answer.end();
      await first.ended;

      void store.retry();
      await settle();

      expect(store.entries()).toHaveLength(4);
      expect(store.entries()[0]).toMatchObject({ key: 'm-1' });
      expect(answers[1].request).toEqual({ conversationId: 'c-1', question: 'Peki ne zaman?' });
    });

    it('does nothing after an answer that is complete', async () => {
      await askAndFinish('Yıllık izin kaç gün?');

      await store.retry();

      expect(answers).toHaveLength(1);
      expect(store.entries()).toHaveLength(2);
    });

    it('does nothing when nothing was asked', async () => {
      await store.retry();

      expect(answers).toHaveLength(0);
    });
  });
});
