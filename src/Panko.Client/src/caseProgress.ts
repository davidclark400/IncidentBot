import type {
  AiSynthesisProgressState,
  CaseEarlyCrumb,
  CaseProgressPhase,
  CaseProgressProjection,
  CrumbSourceProgress,
  CrumbSourceProgressState,
} from './api-client/types.gen'

export type CaseProgress = CaseProgressProjection
export type CaseSourceProgress = CrumbSourceProgress
export type SourceProgressState = CrumbSourceProgressState
export type {
  AiSynthesisProgressState,
  CaseEarlyCrumb,
  CaseProgressPhase,
}
