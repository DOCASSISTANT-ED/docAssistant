import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

// The chat module of the API (docs/decisions.md #59): the shapes of #55 (asking),
// #56 (the answer stream) and #57 (the history), written by hand after the server's
// types (Modules/Chat), and the calls that read the history.
//
// Asking a question is not here: its answer is a stream, read in chat-stream.ts.

// Server: ChatRequest. conversationId is left out to start a new conversation.
export interface ChatRequest {
  conversationId?: string;
  question: string;
}

// The limit the server validates (ChatRequest.QuestionMaxLength).
export const questionMaxLength = 2000;

// The whole answer when the documents do not hold one (ChatPrompt.NotFoundAnswer,
// docs/decisions.md #55, #61).
export const notFoundAnswer = 'Bu bilgi belgelerde bulunamadı.';

// Server: ChatSource. One of the numbered sources the model was given; number is what the
// answer text uses for it: 3 for "[3]". The chunk's text is not part of it.
export interface ChatSource {
  number: number;
  documentId: string;
  documentTitle: string;
  sectionPath: string | null;
  pageNumber: number | null;
}

// The four events of the answer stream (#56), as one type: `type` is the event's name and
// tells which other fields are there.
//
// - sources: once, before the answer.
// - delta:   the next piece of the answer, as the model writes it.
// - done:    once, at the end; the answer has been saved.
// - error:   instead of done, when the answer could not be produced. Nothing was saved.
export type ChatStreamEvent =
  | { type: 'sources'; sources: ChatSource[] }
  | { type: 'delta'; text: string }
  | { type: 'done'; conversationId: string; messageId: string }
  | { type: 'error'; message: string };

// Server: ConversationSummary. The title is the start of the first question.
export interface ConversationSummary {
  id: string;
  title: string;
  createdAt: string;
}

export type MessageRole = 'user' | 'assistant';

// Server: MessageResponse. sources is empty for a question.
export interface ChatMessage {
  id: string;
  role: MessageRole;
  content: string;
  sources: ChatSource[];
  createdAt: string;
}

// Server: ConversationResponse. Messages come oldest first.
export interface Conversation {
  id: string;
  title: string;
  createdAt: string;
  messages: ChatMessage[];
}

@Injectable({ providedIn: 'root' })
export class ConversationsApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/conversations`;

  // The signed-in user's own conversations, newest first (at most 100).
  list(): Observable<ConversationSummary[]> {
    return this.http.get<ConversationSummary[]>(this.baseUrl);
  }

  // Fails with 404 for an unknown conversation and for somebody else's alike.
  get(id: string): Observable<Conversation> {
    return this.http.get<Conversation>(`${this.baseUrl}/${id}`);
  }
}
