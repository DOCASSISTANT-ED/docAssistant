import { TestBed } from '@angular/core/testing';
import { PendingVerification } from './pending-verification';

// The account waiting for its e-mail code, handed from screen to screen in memory only
// (docs/decisions.md #70).
describe('PendingVerification', () => {
  let pending: PendingVerification;

  beforeEach(() => {
    pending = TestBed.inject(PendingVerification);
  });

  it('knows nothing until a screen hands an address over', () => {
    expect(pending.email()).toBeNull();
    expect(pending.codeSent()).toBe(false);
  });

  it('keeps the address, without the spaces around it, and whether a code went out', () => {
    pending.start('  ayse@example.com ', true);

    expect(pending.email()).toBe('ayse@example.com');
    expect(pending.codeSent()).toBe(true);
  });

  it('takes the newest hand-over', () => {
    pending.start('ayse@example.com', true);
    pending.start('ali@example.com', false);

    expect(pending.email()).toBe('ali@example.com');
    expect(pending.codeSent()).toBe(false);
  });

  it('forgets everything once cleared', () => {
    pending.start('ayse@example.com', true);

    pending.clear();

    expect(pending.email()).toBeNull();
    expect(pending.codeSent()).toBe(false);
  });

  // Never in the browser's storage, so the address does not outlive the page.
  it('writes nothing to the browser storage', () => {
    const before = [localStorage.length, sessionStorage.length];

    pending.start('ayse@example.com', true);

    expect([localStorage.length, sessionStorage.length]).toEqual(before);
  });
});
