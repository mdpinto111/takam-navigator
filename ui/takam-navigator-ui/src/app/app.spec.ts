import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { App } from './app';
import { toSegments } from './highlight';
import { SearchResponse } from './models';

const hitResponse: SearchResponse = {
  query: 'איחור בתשלום', mode: 'Hybrid', found: true, semanticAvailable: true, elapsedMs: 12,
  results: [{
    sectionId: '1.4.3#15', directiveCode: '1.4.3', directiveName: 'מועדי תשלום', sectionLabel: '2.9', sectionHeading: 'הנחיות לביצוע',
    snippet: 'עיכוב בתשלום: עד ה-10 לכל חודש', highlights: [{ start: 6, length: 6 }], edition: 6,
    officialUrl: 'https://takam.mof.gov.il/document/H.1.4.3', matchedBy: 'Both', score: 0.03, otherSections: [],
  }],
};

describe('App', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function searchFor(text: string, response: SearchResponse | { status: number }) {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges(); // triggers ngOnInit (health call)
    http.expectOne('/api/health').flush({ status: 'Healthy', semantic: true, index: { directives: 34, sections: 632 } });
    (fixture.componentInstance as unknown as { search(t: string): void }).search(text);
    const req = http.expectOne(r => r.url === '/api/search');
    expect(req.request.params.get('q')).toBe(text);
    if ('status' in response) req.flush({ detail: 'x' }, { status: response.status, statusText: 'error' });
    else req.flush(response);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows directive, section and highlighted words', async () => {
    const el = await searchFor('איחור בתשלום', hitResponse);
    expect(el.querySelector('.code')?.textContent).toContain('1.4.3');
    expect(el.querySelector('.section')?.textContent).toContain('סעיף 2.9');
    expect(el.querySelector('mark')?.textContent).toBe('בתשלום');
    expect(el.querySelector('a')?.getAttribute('href')).toBe('https://takam.mof.gov.il/document/H.1.4.3');
  });

  it('shows the not-found message', async () => {
    const el = await searchFor('מזג האוויר', { ...hitResponse, found: false, results: [], message: 'לא נמצאה הוראה שעוסקת בנושא זה' });
    expect(el.querySelector('.alert.empty')?.textContent).toContain('לא נמצאה הוראה');
    expect(el.querySelector('.card')).toBeNull();
  });

  it('shows an error when the service is down', async () => {
    const el = await searchFor('תשלום', { status: 503 });
    expect(el.querySelector('.alert.error')?.textContent).toContain('אינו זמין');
  });
});

describe('toSegments', () => {
  it('splits text around highlights', () => {
    expect(toSegments('אבג דהו זחט', [{ start: 4, length: 3 }])).toEqual([
      { text: 'אבג ', mark: false }, { text: 'דהו', mark: true }, { text: ' זחט', mark: false },
    ]);
  });

  it('ignores out-of-range and overlapping ranges', () => {
    expect(toSegments('abcdef', [{ start: 1, length: 3 }, { start: 2, length: 2 }, { start: 5, length: 9 }]))
      .toEqual([{ text: 'a', mark: false }, { text: 'bcd', mark: true }, { text: 'ef', mark: false }]);
  });
});
