import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { DocumentInfo, DocumentStatus, DocumentsApi, uploadLimits } from '../api/documents';
import { Session } from '../auth/session';

// Whether the list could be shown.
type ListState = 'loading' | 'ready' | 'failed';

// A file the admin chose, while it is being sent and if it could not be.
interface Upload {
  key: number;
  name: string;
  failure: string | null;
}

// How often the list is asked for again while a document is waiting or being processed.
// Processing takes seconds (decisions #50), so the page does not need to be faster.
export const refreshIntervalMs = 3000;

// "100 MB": the upload limit as the page says it.
const maxSize = `${uploadLimits.maxSizeBytes / (1024 * 1024)} MB`;

// The organization's documents (docs/decisions.md #66): every member sees them, so they
// know what they can ask about; admins also upload and delete them.
@Component({
  selector: 'app-documents',
  styleUrl: './documents.css',
  templateUrl: './documents.html',
})
export class Documents {
  private readonly api = inject(DocumentsApi);
  protected readonly session = inject(Session);

  protected readonly accept = uploadLimits.extensions.join(',');
  protected readonly maxSize = maxSize;

  protected readonly documents = signal<DocumentInfo[]>([]);
  protected readonly listState = signal<ListState>('loading');
  protected readonly uploads = signal<Upload[]>([]);

  // The document whose deletion is being confirmed, and the one being deleted.
  protected readonly confirming = signal<string | null>(null);
  protected readonly deleting = signal<string | null>(null);

  // What went wrong with the last deletion, in words for the user.
  protected readonly deleteFailure = signal<string | null>(null);

  // A document still on its way to being searchable: the list is asked for again.
  protected readonly anyInProgress = computed(() =>
    this.documents().some(
      (document) => document.status === 'Pending' || document.status === 'Processing',
    ),
  );

  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  // Documents deleted on this page. A refresh that was already on its way when one was
  // deleted brings back the list as it was before; these stay out of it.
  private readonly deleted = new Set<string>();
  private nextUploadKey = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.stopRefreshing();
    });

    void this.load();
  }

  protected reload(): void {
    this.listState.set('loading');
    void this.load();
  }

  // ---- Upload ----

  // Each chosen file is checked here first, then sent; one at a time, so the list shows
  // them in the order they were chosen.
  protected async upload(input: HTMLInputElement): Promise<void> {
    const files = Array.from(input.files ?? []);

    // The same file can then be chosen again, after a failure for one.
    input.value = '';

    for (const file of files) {
      const entry: Upload = {
        key: this.nextUploadKey++,
        name: file.name,
        failure: checkFile(file),
      };
      this.uploads.update((uploads) => [...uploads, entry]);

      if (entry.failure !== null) {
        continue;
      }

      try {
        const document = await firstValueFrom(this.api.upload(file));

        this.uploads.update((uploads) => uploads.filter((upload) => upload.key !== entry.key));
        this.documents.update((documents) => [
          document,
          ...documents.filter((d) => d.id !== document.id),
        ]);
        this.scheduleRefresh();
      } catch (error) {
        this.uploads.update((uploads) =>
          uploads.map((upload) =>
            upload.key === entry.key ? { ...upload, failure: uploadFailure(error) } : upload,
          ),
        );
      }
    }
  }

  protected dismissUpload(key: number): void {
    this.uploads.update((uploads) => uploads.filter((upload) => upload.key !== key));
  }

  protected isUploading(upload: Upload): boolean {
    return upload.failure === null;
  }

  // ---- Delete ----

  protected askToDelete(id: string): void {
    this.deleteFailure.set(null);
    this.confirming.set(id);
  }

  protected cancelDelete(): void {
    this.confirming.set(null);
  }

  protected async delete(document: DocumentInfo): Promise<void> {
    if (this.deleting() !== null) {
      return;
    }

    this.confirming.set(null);
    this.deleting.set(document.id);
    this.deleteFailure.set(null);

    try {
      await firstValueFrom(this.api.delete(document.id));
      this.removeFromList(document.id);
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 404) {
        // Already gone (someone else deleted it): the list catches up.
        this.removeFromList(document.id);
      } else {
        this.deleteFailure.set(deleteFailure(document, error));
      }
    } finally {
      this.deleting.set(null);
    }
  }

  // ---- Showing a document ----

  protected statusLabel(status: DocumentStatus): string {
    switch (status) {
      case 'Pending':
        return 'Sırada';
      case 'Processing':
        return 'İşleniyor';
      case 'Done':
        return 'Hazır';
      case 'Failed':
        return 'İşlenemedi';
    }
  }

  protected typeLabel(document: DocumentInfo): string {
    return document.contentType === 'application/pdf' ? 'PDF' : 'DOCX';
  }

  protected sizeLabel(bytes: number): string {
    return formatSize(bytes);
  }

  protected dateLabel(value: string): string {
    return new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium', timeStyle: 'short' }).format(
      new Date(value),
    );
  }

  // ---- Internals ----

  private async load(): Promise<void> {
    this.stopRefreshing();

    try {
      const documents = await firstValueFrom(this.api.list());

      if (this.destroyed) {
        return;
      }

      this.documents.set(documents.filter((document) => !this.deleted.has(document.id)));
      this.listState.set('ready');
    } catch {
      if (this.destroyed) {
        return;
      }

      // A list already on screen stays; only the first load shows the failure.
      if (this.listState() === 'loading') {
        this.listState.set('failed');
        return;
      }
    }

    this.scheduleRefresh();
  }

  private scheduleRefresh(): void {
    if (this.destroyed || this.refreshTimer !== null || !this.anyInProgress()) {
      return;
    }

    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = null;
      void this.load();
    }, refreshIntervalMs);
  }

  private stopRefreshing(): void {
    if (this.refreshTimer !== null) {
      clearTimeout(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  private removeFromList(id: string): void {
    this.deleted.add(id);
    this.documents.update((documents) => documents.filter((document) => document.id !== id));
  }
}

// Why a file is not sent, or null when it may be. The same rules as the server
// (decisions #32, #72), so that the user hears about them at once.
export function checkFile(file: File): string | null {
  const name = file.name.toLocaleLowerCase('tr');

  if (!uploadLimits.extensions.some((extension) => name.endsWith(extension))) {
    return 'Yalnızca PDF ve DOCX dosyaları yüklenebilir.';
  }

  if (file.size === 0) {
    return 'Dosya boş.';
  }

  if (file.size > uploadLimits.maxSizeBytes) {
    return `Dosya ${maxSize}'tan büyük.`;
  }

  return null;
}

function uploadFailure(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      case 413:
        return `Dosya ${maxSize}'tan büyük.`;
      case 415:
        return 'Bu dosya PDF ya da DOCX değil.';
      case 400:
        return 'Dosya boş ya da okunamadı.';
      case 403:
        return 'Belge yalnızca yöneticiler tarafından yüklenebilir.';
      case 0:
        return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';
    }
  }

  return 'Dosya yüklenemedi. Biraz sonra yeniden deneyin.';
}

function deleteFailure(document: DocumentInfo, error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    switch (error.status) {
      case 409:
        return `"${document.title}" şu an işleniyor. Birkaç saniye sonra yeniden deneyin.`;
      case 403:
        return 'Belgeler yalnızca yöneticiler tarafından silinebilir.';
      case 0:
        return 'Sunucuya ulaşılamıyor. Bağlantınızı kontrol edip yeniden deneyin.';
    }
  }

  return `"${document.title}" silinemedi. Biraz sonra yeniden deneyin.`;
}

// "512 KB", "2,4 MB": Turkish decimal comma, one decimal for megabytes.
export function formatSize(bytes: number): string {
  // Rounded first: a size just under a megabyte rounds to 1024 KB, which is written as 1 MB.
  const kilobytes = Math.max(1, Math.round(bytes / 1024));

  if (kilobytes < 1024) {
    return `${kilobytes} KB`;
  }

  const megabytes = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 1 }).format(
    bytes / (1024 * 1024),
  );

  return `${megabytes} MB`;
}
