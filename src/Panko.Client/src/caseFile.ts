import type {
  AiDiagnosis,
  AiSummaryPart,
  AiSummaryReference,
  AiSynthesis,
  CaseFile,
  CaseOrigin,
  CaseOriginKind,
  CausalMarker,
  CodeReference,
  Crumb,
  CrumbSourceHealth,
  CrumbSourceRequestState,
  CrumbSourceStatus,
  PatternContext,
  PatternOccurrence,
  PossiblePatternMatch,
  SignatureStage,
  SourceLink,
  TrailEntry,
} from './api-client/types.gen'

export type SummaryPart = AiSummaryPart
export type SummaryReference = AiSummaryReference
export type CaseDiagnosis = AiDiagnosis
export type CaseAnalysis = AiSynthesis
export type Pattern = PatternContext
export type Link = SourceLink
export type {
  CaseFile,
  CaseOrigin,
  CaseOriginKind,
  CausalMarker,
  CodeReference,
  Crumb,
  CrumbSourceHealth,
  CrumbSourceRequestState,
  CrumbSourceStatus,
  PatternOccurrence,
  PossiblePatternMatch,
  SignatureStage,
  TrailEntry,
}

export function crumbSubmissionMetadata(crumb: Crumb) {
  return {
    submitted: crumb.source.toLowerCase() === 'submitted'
      || provenanceText(crumb, 'trustLevel')?.toLowerCase() === 'submitted',
    caseInputId: provenanceText(crumb, 'caseInputId'),
    declaredSource: provenanceText(crumb, 'declaredSource'),
  }
}

export function caseInputIdForTrailEntry(entry: TrailEntry) {
  return entry.id?.match(/^case-input:([^:]+):trail$/i)?.[1] ?? null
}

function provenanceText(crumb: Crumb, key: string) {
  const value = crumb.provenance?.[key]
  return typeof value === 'string' && value.trim() ? value : null
}
