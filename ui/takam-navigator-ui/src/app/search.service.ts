import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Health, SearchMode, SearchResponse, SectionDetail } from './models';

/** Calls to Takam.Api. In development, /api is forwarded to the .NET API by proxy.conf.json. */
@Injectable({ providedIn: 'root' })
export class SearchService {
  private readonly http = inject(HttpClient);

  search(query: string, mode: SearchMode, top = 5): Observable<SearchResponse> {
    const params = new HttpParams().set('q', query).set('mode', mode).set('top', top);
    return this.http.get<SearchResponse>('/api/search', { params });
  }

  /** A section id is "{code}#{order}" (e.g. "1.4.3#7"). */
  section(sectionId: string): Observable<SectionDetail> {
    const [code, order] = sectionId.split('#');
    return this.http.get<SectionDetail>(`/api/directives/${encodeURIComponent(code)}/sections/${order}`);
  }

  health(): Observable<Health> {
    return this.http.get<Health>('/api/health');
  }
}
