import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';
import { ChatRequest, ChatSource, ChatStreamEvent } from './chat';
import { ProblemDetails } from './problem';
import { readServerSentEvents } from './sse';

// The request was turned down before any answer started (docs/decisions.md #55): an
// ordinary HTTP error instead of a stream. 400: the question is empty or too long.
// 404: the conversation does not exist or is somebody else's. 401: not signed in (any more).
export class ChatRequestError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails | null,
  ) {
    super(`The chat request was rejected with status ${status}.`);
  }
}

// Asks a question (POST /chat) and hands out the answer's events as they arrive
// (decisions #56).
//
// This is the one API call that does not go through HttpClient: HttpClient gives a
// response once it is complete, and here the point is to show the answer while it is
// still being written. fetch lets the body be read piece by piece. What the interceptor
// adds to HttpClient requests (the session cookie, the CSRF header; #60) is therefore
// added here by hand.
@Injectable({ providedIn: 'root' })
export class ChatStream {
  // Use with `for await`. The loop ends when the answer is complete (after "done" or
  // "error") or when the connection is lost; in the last case neither of the two came.
  //
  // signal stops the answer: aborting it ends the request, and the server stops the
  // model. The loop then fails with an AbortError.
  async *ask(request: ChatRequest, signal?: AbortSignal): AsyncGenerator<ChatStreamEvent> {
    const response = await fetch(`${environment.apiBaseUrl}/chat`, {
      method: 'POST',
      credentials: 'include',
      headers: {
        Accept: 'text/event-stream',
        'Content-Type': 'application/json',
        'X-Requested-With': 'XMLHttpRequest',
      },
      body: JSON.stringify(request),
      signal,
    });

    if (!response.ok || !response.body) {
      throw new ChatRequestError(response.status, await problemIn(response));
    }

    for await (const { event, data } of readServerSentEvents(response.body)) {
      const parsed = toChatEvent(event, data);

      // An event this version of the app does not know is skipped, not treated as an
      // error: the server may learn new ones.
      if (parsed) {
        yield parsed;
      }
    }
  }
}

function toChatEvent(event: string, data: string): ChatStreamEvent | null {
  switch (event) {
    case 'sources':
      return { type: 'sources', sources: JSON.parse(data) as ChatSource[] };
    case 'delta':
      return { type: 'delta', ...(JSON.parse(data) as { text: string }) };
    case 'done':
      return { type: 'done', ...(JSON.parse(data) as { conversationId: string; messageId: string }) };
    case 'error':
      return { type: 'error', ...(JSON.parse(data) as { message: string }) };
    default:
      return null;
  }
}

async function problemIn(response: Response): Promise<ProblemDetails | null> {
  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return null;
  }
}
