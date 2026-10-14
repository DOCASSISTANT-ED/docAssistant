import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, throwError } from 'rxjs';
import { environment } from '../../environments/environment';

// The documents module of the API (docs/decisions.md #59): its response shape, written
// by hand after the server's type (Modules/Documents), and the calls. Components use
// this service; they do not call HttpClient themselves.

// Server: DocumentStatus.
export type DocumentStatus = 'Pending' | 'Processing' | 'Done' | 'Failed';

// Server: DocumentResponse.
export interface DocumentInfo {
  id: string;
  title: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  status: DocumentStatus;
  failureReason: string | null;
  createdAt: string;
  processedAt: string | null;
}

// Server: PassageResponse. One piece of a source's text, in the document's own words;
// pageNumber is null for a DOCX.
export interface Passage {
  pageNumber: number | null;
  text: string;
}

@Injectable({ providedIn: 'root' })
export class DocumentsApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/documents`;

  // Fails with 404 for an unknown document and for another organization's alike.
  get(id: string): Observable<DocumentInfo> {
    return this.http.get<DocumentInfo>(`${this.baseUrl}/${id}`);
  }

  // Whether the document can still be opened. False when it is gone (deleted, or never
  // ours); any other failure, such as the API being unreachable, is passed on.
  exists(id: string): Observable<boolean> {
    return this.get(id).pipe(
      map(() => true),
      catchError((error: unknown) =>
        error instanceof HttpErrorResponse && error.status === 404 ? of(false) : throwError(() => error),
      ),
    );
  }

  // The text at a source's place (decisions #63): the parts of the document with the
  // same section path and page. A value the source does not have is left out, which
  // the server reads as "none": no section (text before the first heading), no page
  // (a DOCX). An empty list means the document no longer has that place; 404, that the
  // document itself is gone.
  passages(id: string, sectionPath: string | null, pageNumber: number | null): Observable<Passage[]> {
    let params = new HttpParams();

    if (sectionPath !== null) {
      params = params.set('sectionPath', sectionPath);
    }

    if (pageNumber !== null) {
      params = params.set('pageNumber', pageNumber);
    }

    return this.http.get<Passage[]>(`${this.baseUrl}/${id}/passages`, { params });
  }

  // The address of the uploaded file (decisions #63). The browser opens it itself, in a
  // tab or as a download, so the session cookie travels with it and no code reads the
  // file. With a page, a PDF opens at that page: "#page=" is understood by the PDF
  // viewers of the major browsers, and the server never sees it.
  fileUrl(id: string, page: number | null = null): string {
    const url = `${this.baseUrl}/${id}/file`;

    return page === null ? url : `${url}#page=${page}`;
  }
}
