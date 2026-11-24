import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { SearchService } from './search.service';
import { Health, SearchHit, SearchMode, SearchResponse, SectionDetail } from './models';
import { Segment, toSegments } from './highlight';

@Component({
  selector: 'app-root',
  imports: [FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly api = inject(SearchService);

  protected readonly examples = ['איחור בתשלום לספק', 'הכספת התקלקלה מה עושים עם המסמכים', 'קיבלנו צו עיקול על כספים של ספק', '1.4.5'];
  protected readonly modes: { value: SearchMode; label: string }[] = [
    { value: 'Hybrid', label: 'משולב (מילים + משמעות)' },
    { value: 'Keyword', label: 'מילים בלבד' },
    { value: 'Semantic', label: 'משמעות בלבד' },
  ];

  protected readonly query = signal('');
  protected readonly mode = signal<SearchMode>('Hybrid');
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly response = signal<SearchResponse | null>(null);
  protected readonly health = signal<Health | null>(null);
  protected readonly section = signal<SectionDetail | null>(null);
  protected readonly canSearch = computed(() => this.query().trim().length > 0 && !this.loading());

  ngOnInit(): void {
    this.api.health().subscribe({ next: h => this.health.set(h), error: () => this.health.set(null) });
  }

  protected search(text?: string): void {
    if (text !== undefined) this.query.set(text);
    const q = this.query().trim();
    if (!q) return;
    this.loading.set(true);
    this.error.set(null);
    this.section.set(null);
    this.api.search(q, this.mode()).subscribe({
      next: r => { this.response.set(r); this.loading.set(false); },
      error: (e: HttpErrorResponse) => {
        this.response.set(null);
        this.loading.set(false);
        this.error.set(e.status === 0 || e.status >= 500
          ? 'שירות החיפוש אינו זמין כרגע. נסו שוב מאוחר יותר.'
          : (e.error?.detail ?? 'הבקשה אינה תקינה.'));
      },
    });
  }

  protected openSection(sectionId: string): void {
    this.api.section(sectionId).subscribe({
      next: s => this.section.set(s),
      error: () => this.error.set('לא ניתן לטעון את הסעיף.'),
    });
  }

  protected closeSection(): void { this.section.set(null); }

  protected segments(hit: SearchHit): Segment[] { return toSegments(hit.snippet, hit.highlights); }

  protected sectionTitle(label: string, heading: string): string {
    if (!label) return 'ההוראה כולה';
    return label.startsWith('נספח') ? `${label}${heading ? ' – ' + heading : ''}` : `סעיף ${label}`;
  }

  protected matchLabel(hit: SearchHit): string {
    switch (hit.matchedBy) {
      case 'DirectiveNumber': return 'מספר הוראה';
      case 'Both': return 'מילים ומשמעות';
      case 'Keyword': return 'מילים';
      default: return 'משמעות';
    }
  }
}
