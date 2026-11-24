// Contrat de l'API Takam.Api (voir src/Takam.Core/Search/SearchModels.cs).

export type SearchMode = 'Hybrid' | 'Keyword' | 'Semantic';
export type MatchKind = 'DirectiveNumber' | 'Keyword' | 'Semantic' | 'Both';

export interface HighlightRange { start: number; length: number; }
export interface SectionRef { sectionId: string; label: string; heading: string; }

export interface SearchHit {
  sectionId: string;
  directiveCode: string;
  directiveName: string;
  subChapter?: string;
  sectionLabel: string;
  sectionHeading: string;
  snippet: string;
  highlights: HighlightRange[];
  edition: number;
  validFrom?: string;
  officialUrl: string;
  matchedBy: MatchKind;
  score: number;
  otherSections: SectionRef[];
}

export interface SearchResponse {
  query: string;
  mode: SearchMode;
  found: boolean;
  message?: string;
  results: SearchHit[];
  bestSemanticScore?: number;
  semanticAvailable: boolean;
  elapsedMs: number;
}

export interface SectionDetail {
  id: string;
  order: number;
  label: string;
  heading: string;
  text: string;
  kind: string;
  directive: { code: string; name: string; subChapter?: string; edition: number; validFrom?: string; officialUrl: string };
  sections: { order: number; label: string; heading: string }[];
}

export interface Health {
  status: 'Healthy' | 'Degraded' | 'Unhealthy';
  index?: { directives: number; sections: number; fetchedAt?: string; builtAt?: string };
  semantic: boolean;
}
