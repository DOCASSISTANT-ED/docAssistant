import { TestBed } from '@angular/core/testing';
import { environment } from '../../environments/environment';
import { ChatStreamEvent } from './chat';
import { ChatRequestError, ChatStream } from './chat-stream';

// The browser's fetch is replaced: each test says what the API answers, and looks at
// what was sent.
describe('ChatStream', () => {
  let stream: ChatStream;
  let fetchCall: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchCall = vi.fn();
    vi.stubGlobal('fetch', fetchCall);

    stream = TestBed.inject(ChatStream);
  });

  afterEach(() => vi.unstubAllGlobals());

  // An answer stream with the given text as its body.
  function answerWith(body: string): void {
    fetchCall.mockResolvedValueOnce(
      new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' } }),
    );
  }

  async function ask(question = 'Yıllık izin kaç gün?'): Promise<ChatStreamEvent[]> {
    const events: ChatStreamEvent[] = [];

    for await (const event of stream.ask({ question })) {
      events.push(event);
    }

    return events;
  }

  describe('the request', () => {
    it('posts the question to the chat address of the API', async () => {
      answerWith('');

      await ask('Yıllık izin kaç gün?');

      const [url, options] = fetchCall.mock.calls[0] as [string, RequestInit];
      expect(url).toBe(`${environment.apiBaseUrl}/chat`);
      expect(options.method).toBe('POST');
      expect(JSON.parse(options.body as string)).toEqual({ question: 'Yıllık izin kaç gün?' });
    });

    it('names the conversation when the question continues one', async () => {
      answerWith('');

      for await (const _event of stream.ask({
        conversationId: 'c-1',
        question: 'Peki ne zaman?',
      })) {
        // Nothing arrives.
      }

      const [, options] = fetchCall.mock.calls[0] as [string, RequestInit];
      expect(JSON.parse(options.body as string)).toEqual({
        conversationId: 'c-1',
        question: 'Peki ne zaman?',
      });
    });

    // docs/decisions.md #60. HttpClient's interceptor adds these to every other request;
    // this one goes through fetch and has to bring them itself, or the API turns it away.
    it('sends the session cookie and the header the API asks of cookie requests', async () => {
      answerWith('');

      await ask();

      const [, options] = fetchCall.mock.calls[0] as [string, RequestInit];
      const headers = new Headers(options.headers);
      expect(options.credentials).toBe('include');
      expect(headers.get('X-Requested-With')).toBe('XMLHttpRequest');
      expect(headers.get('Content-Type')).toBe('application/json');
    });

    // Aborting the signal is how an answer is stopped.
    it('hands the signal to the request', async () => {
      answerWith('');
      const controller = new AbortController();

      for await (const _event of stream.ask({ question: 'x' }, controller.signal)) {
        // Nothing arrives.
      }

      const [, options] = fetchCall.mock.calls[0] as [string, RequestInit];
      expect(options.signal).toBe(controller.signal);
    });
  });

  describe('the answer', () => {
    // docs/decisions.md #56.
    it('hands out the four events with their data', async () => {
      answerWith(
        'event: sources\ndata: [{"number":1,"documentId":"d-1","documentTitle":"İK Yönetmeliği","sectionPath":"Bölüm 2","pageNumber":3}]\n\n' +
          'event: delta\ndata: {"text":"Yıllık izin "}\n\n' +
          'event: delta\ndata: {"text":"14 gündür [1]."}\n\n' +
          'event: done\ndata: {"conversationId":"c-1","messageId":"m-1"}\n\n',
      );

      expect(await ask()).toEqual([
        {
          type: 'sources',
          sources: [
            {
              number: 1,
              documentId: 'd-1',
              documentTitle: 'İK Yönetmeliği',
              sectionPath: 'Bölüm 2',
              pageNumber: 3,
            },
          ],
        },
        { type: 'delta', text: 'Yıllık izin ' },
        { type: 'delta', text: '14 gündür [1].' },
        { type: 'done', conversationId: 'c-1', messageId: 'm-1' },
      ]);
    });

    it('hands out the error event', async () => {
      answerWith(
        'event: sources\ndata: []\n\nevent: error\ndata: {"message":"The answer could not be generated."}\n\n',
      );

      expect(await ask()).toEqual([
        { type: 'sources', sources: [] },
        { type: 'error', message: 'The answer could not be generated.' },
      ]);
    });

    // The server may learn new events before the app does.
    it('skips an event it does not know', async () => {
      answerWith(
        'event: thinking\ndata: {"step":1}\n\nevent: delta\ndata: {"text":"a"}\n\n: keep-alive\n\n',
      );

      expect(await ask()).toEqual([{ type: 'delta', text: 'a' }]);
    });

    // The connection was lost: the loop simply ends, and the caller sees that neither
    // "done" nor "error" came.
    it('ends without an error when the stream breaks off', async () => {
      answerWith('event: delta\ndata: {"text":"Yarım"}\n\nevent: delta\ndata: {"te');

      expect(await ask()).toEqual([{ type: 'delta', text: 'Yarım' }]);
    });
  });

  describe('a request that is turned down', () => {
    // docs/decisions.md #55: an ordinary HTTP error, before any stream.
    it.each([400, 401, 404, 500])(
      'fails with the status %s and the problem details',
      async (status) => {
        fetchCall.mockResolvedValueOnce(
          new Response(JSON.stringify({ status, title: 'Rejected.' }), {
            status,
            headers: { 'Content-Type': 'application/problem+json' },
          }),
        );

        const error = await ask().catch((e: unknown) => e);

        expect(error).toBeInstanceOf(ChatRequestError);
        expect((error as ChatRequestError).status).toBe(status);
        expect((error as ChatRequestError).problem).toEqual({ status, title: 'Rejected.' });
      },
    );

    // A 404 for somebody else's conversation has no body at all.
    it('fails with the status when the answer carries no problem details', async () => {
      fetchCall.mockResolvedValueOnce(new Response(null, { status: 404 }));

      const error = await ask().catch((e: unknown) => e);

      expect((error as ChatRequestError).status).toBe(404);
      expect((error as ChatRequestError).problem).toBeNull();
    });

    it('fails when something in between answered with a page instead', async () => {
      fetchCall.mockResolvedValueOnce(new Response('<html>Bad Gateway</html>', { status: 502 }));

      const error = await ask().catch((e: unknown) => e);

      expect((error as ChatRequestError).status).toBe(502);
      expect((error as ChatRequestError).problem).toBeNull();
    });
  });

  describe('no answer at all', () => {
    // The API is down or the network is: fetch itself fails, and the caller gets that.
    it('fails with the failure of fetch', async () => {
      fetchCall.mockRejectedValueOnce(new TypeError('Failed to fetch'));

      const error = await ask().catch((e: unknown) => e);

      expect(error).toBeInstanceOf(TypeError);
      expect(error).not.toBeInstanceOf(ChatRequestError);
    });

    it('fails with the abort when the answer is stopped', async () => {
      fetchCall.mockRejectedValueOnce(new DOMException('The operation was aborted.', 'AbortError'));

      const error = await ask().catch((e: unknown) => e);

      expect((error as DOMException).name).toBe('AbortError');
    });
  });
});
