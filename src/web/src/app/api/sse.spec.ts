import { ServerSentEvent, readServerSentEvents } from './sse';

const encoder = new TextEncoder();

// A response body that arrives in the given pieces. A string is sent as its UTF-8 bytes.
function bodyOf(...pieces: (string | Uint8Array)[]): ReadableStream<Uint8Array> {
  return new ReadableStream<Uint8Array>({
    start(controller) {
      for (const piece of pieces) {
        controller.enqueue(typeof piece === 'string' ? encoder.encode(piece) : piece);
      }

      controller.close();
    },
  });
}

async function eventsOf(...pieces: (string | Uint8Array)[]): Promise<ServerSentEvent[]> {
  const events: ServerSentEvent[] = [];

  for await (const event of readServerSentEvents(bodyOf(...pieces))) {
    events.push(event);
  }

  return events;
}

describe('readServerSentEvents', () => {
  describe('one event', () => {
    it('reads the name and the data', async () => {
      expect(await eventsOf('event: delta\ndata: {"text":"Merhaba"}\n\n')).toEqual([
        { event: 'delta', data: '{"text":"Merhaba"}' },
      ]);
    });

    // The standard's name for an event that does not say what it is.
    it('calls an event without a name "message"', async () => {
      expect(await eventsOf('data: merhaba\n\n')).toEqual([{ event: 'message', data: 'merhaba' }]);
    });

    it('takes one space after the colon as part of the format, and no more', async () => {
      expect(await eventsOf('event:delta\ndata:  iki boşluk\n\n')).toEqual([
        { event: 'delta', data: ' iki boşluk' },
      ]);
    });

    // JSON is full of colons; only the first one ends the field's name.
    it('keeps the colons inside the data', async () => {
      expect(await eventsOf('data: {"a":"b:c"}\n\n')).toEqual([
        { event: 'message', data: '{"a":"b:c"}' },
      ]);
    });

    it('joins several data lines with a line break', async () => {
      expect(await eventsOf('event: delta\ndata: bir\ndata: iki\n\n')).toEqual([
        { event: 'delta', data: 'bir\niki' },
      ]);
    });

    it('reads a data line that is empty', async () => {
      expect(await eventsOf('data\n\n')).toEqual([{ event: 'message', data: '' }]);
    });
  });

  describe('several events', () => {
    it('gives them in the order they were sent', async () => {
      const stream =
        'event: sources\ndata: []\n\n' +
        'event: delta\ndata: {"text":"a"}\n\n' +
        'event: done\ndata: {}\n\n';

      expect((await eventsOf(stream)).map((event) => event.event)).toEqual([
        'sources',
        'delta',
        'done',
      ]);
    });

    it('does not carry the name of one event over to the next', async () => {
      expect(await eventsOf('event: delta\ndata: a\n\ndata: b\n\n')).toEqual([
        { event: 'delta', data: 'a' },
        { event: 'message', data: 'b' },
      ]);
    });

    it('copes with more than one empty line between events', async () => {
      expect(await eventsOf('data: a\n\n\n\ndata: b\n\n')).toHaveLength(2);
    });
  });

  describe('what is not an event', () => {
    // Servers send comments to keep a connection open.
    it('skips comments', async () => {
      expect(await eventsOf(': keep-alive\n\nevent: delta\n: inside\ndata: a\n\n')).toEqual([
        { event: 'delta', data: 'a' },
      ]);
    });

    it('skips the fields for reconnecting', async () => {
      expect(await eventsOf('id: 7\nretry: 3000\nevent: delta\ndata: a\n\n')).toEqual([
        { event: 'delta', data: 'a' },
      ]);
    });

    it('drops a block that carries no data', async () => {
      expect(await eventsOf('event: delta\n\ndata: a\n\n')).toEqual([
        { event: 'message', data: 'a' },
      ]);
    });

    it('gives nothing for an empty body', async () => {
      expect(await eventsOf()).toEqual([]);
    });
  });

  describe('line ends', () => {
    it.each([
      ['\\n', '\n'],
      ['\\r\\n', '\r\n'],
      ['\\r', '\r'],
    ])('reads events whose lines end with %s', async (_name, end) => {
      const stream = `event: delta${end}data: a${end}${end}event: done${end}data: b${end}${end}`;

      expect(await eventsOf(stream)).toEqual([
        { event: 'delta', data: 'a' },
        { event: 'done', data: 'b' },
      ]);
    });

    // The \r that ends one piece and the \n that starts the next are one line end.
    it('treats \\r\\n as one line end when it is cut in two', async () => {
      expect(await eventsOf('event: delta\r', '\ndata: a\r', '\n\r', '\n')).toEqual([
        { event: 'delta', data: 'a' },
      ]);
    });
  });

  // The network delivers the bytes in pieces of any size.
  describe('a stream that arrives in pieces', () => {
    const stream =
      'event: sources\ndata: [{"number":1,"documentTitle":"İK Yönetmeliği"}]\n\n' +
      'event: delta\ndata: {"text":"Yıllık izin 14 gündür [1]."}\n\n' +
      'event: done\ndata: {"conversationId":"c-1","messageId":"m-1"}\n\n';

    it('gives the same events wherever the stream is cut in two', async () => {
      const whole = await eventsOf(stream);
      const bytes = encoder.encode(stream);
      expect(whole).toHaveLength(3);

      // Every position, including those in the middle of "İ", "ı" and "ü" (two bytes each).
      for (let cut = 1; cut < bytes.length; cut++) {
        expect(await eventsOf(bytes.slice(0, cut), bytes.slice(cut))).toEqual(whole);
      }
    });

    it('gives the same events when every byte arrives on its own', async () => {
      const bytes = encoder.encode(stream);
      const single = Array.from(bytes, (byte) => new Uint8Array([byte]));

      expect(await eventsOf(...single)).toEqual(await eventsOf(stream));
    });

    it('gives an event as soon as its empty line has arrived', async () => {
      let send!: (text: string) => void;
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          send = (text) => controller.enqueue(encoder.encode(text));
        },
      });
      const events = readServerSentEvents(body);

      send('event: delta\ndata: a\n\nevent: delta\ndata: b');

      // The second event is still open; the first is handed out without waiting for it.
      expect((await events.next()).value).toEqual({ event: 'delta', data: 'a' });
    });
  });

  describe('a stream that ends early', () => {
    // Half an event is not an event: its data may be cut in the middle.
    it('drops an event that was never closed by an empty line', async () => {
      expect(await eventsOf('event: delta\ndata: a\n\nevent: delta\ndata: {"text":"yar')).toEqual([
        { event: 'delta', data: 'a' },
      ]);
    });

    it('drops an event whose last line end is missing', async () => {
      expect(await eventsOf('event: done\ndata: {}\n')).toEqual([]);
    });
  });

  // Otherwise the browser would keep the connection of an answer the user stopped.
  it('lets go of the body when the caller stops listening', async () => {
    const body = bodyOf('data: a\n\n', 'data: b\n\n');

    for await (const _event of readServerSentEvents(body)) {
      break;
    }

    expect(body.locked).toBe(false);
  });

  it('lets go of the body when the stream is over', async () => {
    const body = bodyOf('data: a\n\n');

    for await (const _event of readServerSentEvents(body)) {
      // Read to the end.
    }

    expect(body.locked).toBe(false);
  });
});
