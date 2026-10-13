import { HttpErrorResponse } from '@angular/common/http';
import { problemOf } from './problem';

describe('problemOf', () => {
  it('gives the problem details the API answered with', () => {
    const problem = {
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { Email: ['invalid'] },
    };

    expect(problemOf(new HttpErrorResponse({ status: 400, error: problem }))).toEqual(problem);
  });

  // Something in between answered (a proxy's HTML error page), or nothing did.
  it.each([
    ['text', '<html>Bad Gateway</html>'],
    ['nothing', null],
    ['a network failure', new ProgressEvent('error')],
  ])('gives null when the answer carried %s', (_what, body) => {
    expect(problemOf(new HttpErrorResponse({ status: 502, error: body }))).toBeNull();
  });

  it.each([new Error('boom'), 'boom', undefined])(
    'gives null for something that is not an answer of the API: %s',
    (error) => {
      expect(problemOf(error)).toBeNull();
    },
  );
});
