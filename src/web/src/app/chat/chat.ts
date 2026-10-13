import { TextFieldModule } from '@angular/cdk/text-field';
import { Component, ElementRef, effect, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { questionMaxLength } from '../api/chat';
import { Answer } from './answer';
import { ChatStore } from './chat-store';
import { Welcome } from './welcome';

// The chat screen: the conversation's questions and answers, and the box to ask the next
// question. What is shown comes from ChatStore; this component turns it into a page and
// passes the user's actions on.
@Component({
  imports: [Answer, FormsModule, RouterLink, TextFieldModule, Welcome],
  selector: 'app-chat',
  styleUrl: './chat.css',
  templateUrl: './chat.html',
})
export class Chat {
  protected readonly chat = inject(ChatStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly maxLength = questionMaxLength;

  // What is typed in the question box.
  protected readonly draft = signal('');

  private readonly scroller = viewChild.required<ElementRef<HTMLElement>>('scroller');
  private readonly input = viewChild<ElementRef<HTMLTextAreaElement>>('input');

  // While true, new text keeps the end of the conversation in view. Scrolling up to
  // read something earlier turns it off, so the page does not jump away under the reader.
  private followingEnd = true;

  constructor() {
    // The address decides what is shown: /c/<id> is a conversation from the history,
    // the plain address a new one.
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const id = params.get('id');

      if (id) {
        void this.chat.open(id);
      } else {
        this.chat.startNew();
      }

      this.followingEnd = true;
    });

    // A new conversation gets its id with its first answer; the address follows, so
    // that a reload shows the same conversation. replaceUrl: the back button should not
    // stop at the empty "new conversation" on the way.
    effect(() => {
      const id = this.chat.conversationId();

      if (id && this.route.snapshot.paramMap.get('id') !== id) {
        void this.router.navigate(['/c', id], { replaceUrl: true });
      }
    });

    // Every change to the conversation (a new entry, more text) may need a scroll.
    effect(() => {
      this.chat.entries();

      if (this.followingEnd) {
        // After the browser has laid out the new text.
        requestAnimationFrame(() => {
          const element = this.scroller().nativeElement;
          element.scrollTop = element.scrollHeight;
        });
      }
    });
  }

  protected onScroll(): void {
    const element = this.scroller().nativeElement;

    // "At the end" with a little tolerance: half a line off still counts.
    this.followingEnd = element.scrollHeight - element.scrollTop - element.clientHeight < 48;
  }

  protected canSend(): boolean {
    // Not while an answer is being written, and not before the conversation is on screen.
    return this.draft().trim() !== '' && !this.chat.answering() && this.chat.openState() !== 'loading';
  }

  // Tries again to load the conversation in the address after it could not be loaded.
  protected reload(): void {
    const id = this.route.snapshot.paramMap.get('id');

    if (id) {
      void this.chat.open(id);
    }
  }

  protected send(): void {
    if (!this.canSend()) {
      return;
    }

    const question = this.draft();

    this.draft.set('');

    // Also emptied by hand: when the text was typed and sent within one rendering pass
    // (pasted, then Enter), the box was never told about the text, and would not see
    // going back to empty as a change.
    const input = this.input()?.nativeElement;

    if (input) {
      input.value = '';
    }

    this.followingEnd = true;
    void this.chat.ask(question);
  }

  // Enter sends; Shift+Enter starts a new line, as in most chat apps. While a word is
  // being composed with an input method (isComposing), Enter belongs to that.
  protected onEnter(event: Event): void {
    const key = event as KeyboardEvent;

    if (key.shiftKey || key.isComposing) {
      return;
    }

    key.preventDefault();
    this.send();
  }
}
