import type {
  CaseInput,
  CasePending,
  CaseStatus,
  CaseStatusChanged,
  CaseTriggerResult,
  CaseUpdated,
  PageOfCaseInput,
  RecentCase,
  RecentCases,
  SubmittedCrumbKind,
} from './api-client/types.gen'

export type {
  CaseInput,
  CasePending,
  CaseStatus,
  CaseStatusChanged,
  CaseTriggerResult,
  CaseUpdated,
  PageOfCaseInput,
  RecentCase,
  RecentCases,
}

export type SubmittedInputType = SubmittedCrumbKind

export function caseInputTypeLabel(type: SubmittedInputType): SubmittedInputType {
  return type
}
