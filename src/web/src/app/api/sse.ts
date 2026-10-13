// Reads a Server-Sent Events stream (the format of `text/event-stream` responses).
//
// The server sends events as blocks of text separated by an empty line:
//
//     event: delta
//     data: {"text":"Merhaba"}
//
// The bytes arrive in pieces of any size, so one piece may end in the middle of an event,
// or even in the middle of a letter such as "ı" (two bytes in UTF-8). The text is
// therefore collected until an empty line shows that an event is complete.
//
// The browser's own EventSource does this too, but it can only send GET requests without
// a body; asking a question is a POST (docs/decisions.md #55).

export interface ServerSentEvent {
  // The name after "event:"; "message" when the block has none, as the standard says.
  event: string;

  // The text after "data:"; several data lines are joined with a line break.
  data: string;
}

export async function* readServerSentEvents(
  body: ReadableStream<Uint8Array>,
): AsyncGenerator<ServerSentEvent> {
  const reader = body.getReader();

  // stream: true keeps the bytes of a letter that was cut in half for the next piece.
  const decoder = new TextDecoder('utf-8');
  let buffer = '';

  try {
    while (true) {
      const { done, value } = await reader.read();

      buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });

      // Line ends may be \n, \r\n or \r; one form makes the rest simple. A lone \r at
      // the very end waits for the next piece: it may be the first half of \r\n.
      const endsWithCarriageReturn = !done && buffer.endsWith('\r');
      const text = (endsWithCarriageReturn ? buffer.slice(0, -1) : buffer).replace(/\r\n?/g, '\n');

      const blocks = text.split('\n\n');

      // The last part is an event that is still arriving (or empty); keep it.
      buffer = blocks.pop()! + (endsWithCarriageReturn ? '\r' : '');

      for (const block of blocks) {
        const event = parseBlock(block);

        if (event) {
          yield event;
        }
      }

      if (done) {
        // Whatever is left was never closed by an empty line: the connection was cut
        // in the middle of an event, and half an event is dropped.
        return;
      }
    }
  } finally {
    // Also runs when the caller stops listening early: tells the browser to let go of
    // the connection.
    reader.releaseLock();
  }
}

function parseBlock(block: string): ServerSentEvent | null {
  let event = 'message';
  const data: string[] = [];

  for (const line of block.split('\n')) {
    // A line starting with ":" is a comment; servers send them to keep a connection open.
    if (line === '' || line.startsWith(':')) {
      continue;
    }

    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);

    // One space after the colon belongs to the format, not to the value.
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) {
      value = value.slice(1);
    }

    if (field === 'event') {
      event = value;
    } else if (field === 'data') {
      data.push(value);
    }

    // Other fields ("id", "retry") are for reconnecting, which this app does not do.
  }

  return data.length > 0 ? { event, data: data.join('\n') } : null;
}
