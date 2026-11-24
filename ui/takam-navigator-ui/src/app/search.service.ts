import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Health, SearchMode, SearchResponse, SectionDetail } from './models';

/** Appels à Takam.Api. En développement, /api est redirigé vers l'API .NET par proxy.conf.json. */
@Injectable({ providedIn: 'root' })
export class SearchService {
  private readonly http = inject(HttpClient);

  search(query: string, mode: SearchMode, top = 5): Observable<SearchResponse> {
    const params = new HttpParams().set('q', query).set('mode', mode).set('top', top);
    return this.http.get<SearchResponse>('/api/search', { params });
  }

  /** L'identifiant d'une section est "{code}#{ordre}" (ex. "1.4.3#7"). */
  section(sectionId: string): Observable<SectionDetail> {
    const [code, order] = sectionId.split('#');
    return this.http.get<SectionDetail>(`/api/directives/${encodeURIComponent(code)}/sections/${order}`);
  }

  health(): Observable<Health> {
    return this.http.get<Health>('/api/health');
  }
}
