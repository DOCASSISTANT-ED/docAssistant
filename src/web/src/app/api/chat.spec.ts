import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { Conversation, ConversationSummary, ConversationsApi } from './chat';

const base = `${environment.apiBaseUrl}/conversations`;

// The calls that read the chat history (docs/decisions.md #57).
describe('ConversationsApi', () => {
  let conversations: ConversationsApi;
  let api: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    conversations = TestBed.inject(ConversationsApi);
    api = TestBed.inject(HttpTestingController);
  });

  afterEach(() => api.verify());

  it('reads the list of conversations', async () => {
    const list: ConversationSummary[] = [
      { id: 'c-2', title: 'Harcırah ne kadar?', createdAt: '2026-10-13T12:00:00Z' },
      { id: 'c-1', title: 'Yıllık izin kaç gün?', createdAt: '2026-10-13T11:00:00Z' },
    ];
    const answer = firstValueFrom(conversations.list());

    const request = api.expectOne(base);
    expect(request.request.method).toBe('GET');
    request.flush(list);

    expect(await answer).toEqual(list);
  });

  it('reads one conversation by its id', async () => {
    const conversation: Conversation = {
      id: 'c-1',
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
      ],
    };
    const answer = firstValueFrom(conversations.get('c-1'));

    const request = api.expectOne(`${base}/c-1`);
    expect(request.request.method).toBe('GET');
    request.flush(conversation);

    expect(await answer).toEqual(conversation);
  });
});
