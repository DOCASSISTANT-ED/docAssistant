import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ChatSource, ConversationSummary, ConversationsApi, questionMaxLength } from '../api/chat';
import { ChatRequestError, ChatStream } from '../api/chat-stream';
import { Session } from '../auth/session';

// How far an answer has come:
// - waiting:   the question was sent; nothing of the answer has arrived yet (the search is
//              running, or the model has not started writing).
// - streaming: text is arriving.
// - done:      complete, and saved by the server.
// - stopped:   the user stopped it. What was written stays on screen, but the server
//              saves no unfinished answers (docs/decisions.md #56).
// - failed:    could not be produced; `failure` says why, in words for the user.
export type AnswerState = 'waiting' | 'streaming' | 'done' | 'stopped' | 'failed';

export interface QuestionEntry {
  kind: 'question';
  key: string;
  text: string;
}

export interface AnswerEntry {
  kind: 'answer';
  key: string;
  text: string;
  sources: ChatSource[];
  state: AnswerState;
  failure: string | null;
}

export type ChatEntry = QuestionEntry | AnswerEntry;

// Whether the conversation named in the address could be shown.
export type OpenState = 'idle' | 'loading' | 'ready' | 'notFound' | 'failed';

// The chat screen's state: the list of conversations, the one that is open, and the
// answer that is being written. Screens read the signals and call the methods; nothing
// else changes the state.
@Injectable({ providedIn: 'root' })
export class ChatStore {
  private readonly conversationsApi = inject(ConversationsApi);
  private readonly chatStream = inject(ChatStream);
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  private readonly conversationList = signal<ConversationSummary[]>([]);
  private readonly openConversationId = signal<string | null>(null);
  private readonly entryList = signal<ChatEntry[]>([]);
  private readonly openStatus = signal<OpenState>('idle');

  // Set while an answer is on its way; aborting it stops the answer.
  private running: AbortController | null = null;
  private nextKey = 0;

  readonly conversations = this.conversationList.asReadonly();
  readonly conversationId = this.openConversationId.asReadonly();
  readonly entries = this.entryList.asReadonly();
  readonly openState = this.openStatus.asReadonly();

  // True from sending a question until its answer has ended one way or another.
  readonly answering = computed(() => {
    const last = this.entryList().at(-1);

    return last?.kind === 'answer' && (last.state === 'waiting' || last.state === 'streaming');
  });

  // ---- Conversations ----

  // The side panel's list. A failure leaves the list as it was: the chat itself still
  // works without it.
  async loadConversations(): Promise<void> {
    try {
      this.conversationList.set(await firstValueFrom(this.conversationsApi.list()));
    } catch {
      // Nothing to show instead; the next load tries again.
    }
  }

  // An empty conversation; the first question creates it on the server.
  startNew(): void {
    this.stop();
    this.openConversationId.set(null);
    this.entryList.set([]);
    this.openStatus.set('idle');
  }

  // Shows a conversation from the history.
  async open(id: string): Promise<void> {
    // Already on screen, for example right after its first answer gave it this id.
    if (this.openConversationId() === id && this.openStatus() !== 'failed') {
      return;
    }

    this.stop();
    this.openConversationId.set(id);
    this.entryList.set([]);
    this.openStatus.set('loading');

    try {
      const conversation = await firstValueFrom(this.conversationsApi.get(id));

      // The user may have moved on to another conversation while this one was loading.
      if (this.openConversationId() !== id) {
        return;
      }

      this.entryList.set(
        conversation.messages.map((message): ChatEntry =>
          message.role === 'user'
            ? { kind: 'question', key: message.id, text: message.content }
            : {
                kind: 'answer',
                key: message.id,
                text: message.content,
                sources: message.sources,
                state: 'done',
                failure: null,
              },
        ),
      );
      this.openStatus.set('ready');
    } catch (error) {
      if (this.openConversationId() !== id) {
        return;
      }

      // Unknown and somebody else's look the same on purpose (decisions #55).
      this.openStatus.set(error instanceof HttpErrorResponse && error.status === 404 ? 'notFound' : 'failed');
    }
  }

  // ---- Asking ----

  // Sends a question and fills in the answer as it arrives. Returns once the answer has
  // ended (complete, stopped or failed); never throws.
  async ask(question: string): Promise<void> {
    const text = question.trim();

    if (text === '' || text.length > questionMaxLength || this.answering()) {
      return;
    }

    const answerKey = this.newKey();

    this.entryList.update((entries) => [
      ...entries,
      { kind: 'question', key: this.newKey(), text },
      { kind: 'answer', key: answerKey, text: '', sources: [], state: 'waiting', failure: null },
    ]);

    const controller = new AbortController();
    this.running = controller;

    const conversationId = this.openConversationId();
    const startedAt = Date.now();
    let ended = false;

    try {
      const events = this.chatStream.ask(
        conversationId ? { conversationId, question: text } : { question: text },
        controller.signal,
      );

      for await (const event of events) {
        switch (event.type) {
          case 'sources':
            this.updateAnswer(answerKey, { sources: event.sources });
            break;

          case 'delta':
            this.updateAnswer(answerKey, (answer) => ({ text: answer.text + event.text, state: 'streaming' }));
            break;

          case 'done':
            ended = true;
            this.updateAnswer(answerKey, { state: 'done' });
            this.conversationSaved(event.conversationId, text);
            break;

          case 'error':
            ended = true;
            this.updateAnswer(answerKey, { state: 'failed', failure: 'Cevap üretilemedi. Yeniden deneyin.' });
            break;
        }
      }

      // The stream ended without saying how: the connection was lost on the way.
      if (!ended) {
        this.updateAnswer(answerKey, {
          state: 'failed',
          failure: 'Bağlantı kesildi; cevap tamamlanamadı. Yeniden deneyin.',
        });
      }
    } catch (error) {
      if (controller.signal.aborted) {
        this.updateAnswer(answerKey, { state: 'stopped' });
      } else {
        this.updateAnswer(answerKey, { state: 'failed', failure: this.explain(error) });
      }
    } finally {
      if (this.running === controller) {
        this.running = null;
      }
    }

    // A first question that did not reach "done" still created its conversation on the
    // server, but its id only comes with "done". Find it, so that the next question
    // continues it instead of starting yet another one.
    if (!ended && conversationId === null && this.openConversationId() === null) {
      await this.adoptConversationStartedBy(text, startedAt);
    }
  }

  // Stops the answer that is being written, if any.
  stop(): void {
    this.running?.abort();
  }

  // Asks the last question again after its answer failed or was stopped. The failed
  // attempt is taken off the screen; on the server its question stays in the history.
  async retry(): Promise<void> {
    const entries = this.entryList();
    const answer = entries.at(-1);
    const question = entries.at(-2);

    if (
      answer?.kind !== 'answer' ||
      question?.kind !== 'question' ||
      (answer.state !== 'failed' && answer.state !== 'stopped')
    ) {
      return;
    }

    this.entryList.set(entries.slice(0, -2));
    await this.ask(question.text);
  }

  // ---- Internals ----

  private newKey(): string {
    return `local-${this.nextKey++}`;
  }

  private updateAnswer(
    key: string,
    change: Partial<AnswerEntry> | ((answer: AnswerEntry) => Partial<AnswerEntry>),
  ): void {
    this.entryList.update((entries) =>
      entries.map((entry) =>
        entry.kind === 'answer' && entry.key === key
          ? { ...entry, ...(typeof change === 'function' ? change(entry) : change) }
          : entry,
      ),
    );
  }

  // The answer was saved. A new conversation gets its id now and joins the list, on top.
  private conversationSaved(id: string, firstQuestion: string): void {
    if (this.openConversationId() === id) {
      return;
    }

    this.openConversationId.set(id);
    this.openStatus.set('ready');

    if (!this.conversationList().some((conversation) => conversation.id === id)) {
      this.conversationList.update((list) => [
        // The same title the server gives it: the start of the first question (#57).
        { id, title: firstQuestion.slice(0, 80), createdAt: new Date().toISOString() },
        ...list,
      ]);
    }
  }

  private async adoptConversationStartedBy(question: string, startedAt: number): Promise<void> {
    await this.loadConversations();

    // Newest first; the title is the start of the first question. The clocks of the
    // browser and the server may differ a little, hence the minute of slack.
    const created = this.conversationList().find(
      (conversation) =>
        question.startsWith(conversation.title) && Date.parse(conversation.createdAt) >= startedAt - 60_000,
    );

    if (created && this.openConversationId() === null) {
      this.openConversationId.set(created.id);
      this.openStatus.set('ready');
    }
  }

  private explain(error: unknown): string {
    if (error instanceof ChatRequestError) {
      switch (error.status) {
        case 401:
          // The session ended (60 minutes, #60): the same as for every other request
          // (session-expired.interceptor.ts), done by hand because this one uses fetch.
          this.session.expire();
          void this.router.navigate(['/login'], { queryParams: { returnUrl: this.router.url } });
          return 'Oturumunuz sona erdi. Yeniden giriş yapın.';
        case 404:
          return 'Bu sohbet artık bulunamıyor. Yeni bir sohbet başlatın.';
        case 400:
          return 'Soru gönderilemedi. Soruyu kısaltıp yeniden deneyin.';
      }

      return 'Cevap alınamadı. Biraz sonra yeniden deneyin.';
    }

    // fetch itself failed: no answer at all (the API is down, or the network is).
    return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';
  }
}
