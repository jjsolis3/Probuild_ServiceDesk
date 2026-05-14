# ServiceSphere AI Solution Design

## Document Control

- **System:** ServiceSphere Web Application
- **Document Type:** AI Solution Design (Target Architecture, Data Contracts, Rollout Controls)
- **Audience:** Product Owners, IT Operations, Engineering, Security/Compliance, Support Leadership
- **Status:** Draft v1

---

## 1) Purpose and Scope

This document defines a practical and implementation-ready AI architecture for ServiceSphere. It is designed to:

1. Add a **portal AI assistant** that helps end users self-resolve issues from trusted knowledge.
2. Add **AI-assisted triage** for ticket intake from portal and email channels.
3. Preserve ServiceSphere’s deterministic routing controls and operational governance.
4. Introduce a phased rollout model with clear safety gates and measurable outcomes.

### In Scope

- Target architecture (logical components, interfaces, runtime flow).
- Data contracts for AI requests/responses, retrieval results, and triage recommendations.
- Rollout controls: feature flags, approval gates, confidence thresholds, monitoring, and fallback.

### Out of Scope (for this version)

- UI mockups and front-end pixel design.
- Vendor/legal procurement decisions.
- Final production infrastructure sizing.

---

## 2) Design Principles

1. **Human first:** AI recommends; humans approve state-changing actions initially.
2. **Grounded responses:** User-facing answers must be anchored to KB/canned response sources.
3. **Deterministic guardrails:** Existing assignment and rules engine remain authoritative.
4. **Auditability:** Every AI recommendation and acceptance/rejection is logged.
5. **Safe rollout:** Progressive enablement by feature flag, cohort, and confidence.
6. **Fail-safe:** If AI is unavailable/low-confidence, default to current non-AI behavior.

---

## 3) Current-State Integration Anchors (ServiceSphere)

The AI design intentionally integrates with existing application boundaries:

- **Portal flows** (`PortalController`) for KB help and ticket submission support.
- **Email intake** (`GmailApiService`) for parsing/classification/triage hints.
- **Routing core** (`AssignmentResolverService`) for final assignee decision logic.
- **Notification layer** (`EmailNotificationService`) for draft generation and approved outbound messaging.
- **Settings hub** (`SettingsController`) for governance controls and feature toggles.
- **Data layer** (`ServiceDeskDbContext`) for AI audit/recommendation persistence.

---

## 4) Target Architecture

## 4.1 Logical Component Diagram (Textual)

```text
[Portal UI / Agent UI / Email Intake]
              |
              v
       [AI Orchestrator API]
      /         |          \
     v          v           v
[Policy Engine] [Retriever] [Prompt Builder]
     |          |           |
     |          v           v
     |    [Vector Index]  [LLM Gateway]
     |          |           |
     |          v           v
     \------> [AI Decision Normalizer]
                     |
                     v
        [ServiceSphere Domain Services]
     (Tickets, AssignmentResolver, Notifications)
                     |
                     v
              [SQL + Audit Logs]
```

## 4.2 Runtime Components

### A) AI Orchestrator API (new)

Central service (internal API) that exposes domain-oriented endpoints:

- `/ai/assist/query`
- `/ai/triage/recommend`
- `/ai/reply/draft`
- `/ai/health`

Responsibilities:

- Request validation + schema normalization.
- Context assembly (ticket/user/channel metadata).
- Policy enforcement (role, feature flag, environment mode).
- Calling retrieval + model inference.
- Structured response generation with confidence + citations.

### B) Retriever Service (new)

- Retrieves candidate KB/canned-response documents.
- Supports keyword + vector retrieval hybrid strategy.
- Returns ranked passages with source metadata.

### C) LLM Gateway (new)

Abstraction over underlying model providers (commercial or open-source):

- Standard interface for chat completion/inference.
- Timeout/retry/circuit-breaker controls.
- Prompt/version tagging.
- Optional provider failover.

### D) Policy Engine (new)

- Reads AI configuration from Settings/AppSettings.
- Applies confidence thresholds and action permissions.
- Enforces “recommend-only” vs “auto-apply” modes per feature.

### E) AI Decision Normalizer (new)

- Converts model output into strict contracts.
- Performs validation (enum checks, assignee existence, category validity).
- Sets `decisionStatus` (`accepted`, `needs_human_review`, `rejected`).

### F) AI Audit Store (new tables)

Stores prompt metadata, model outputs, user decisions, and outcomes.

---

## 5) Key AI Use Cases and Flows

## 5.1 Portal AI Assistant (RAG)

### Goal
Help users resolve common issues without opening tickets unnecessarily.

### Flow

1. User asks question in portal assistant widget.
2. Orchestrator validates scope and redacts sensitive tokens.
3. Retriever pulls top KB/canned passages.
4. Prompt builder constructs grounded prompt with citations.
5. LLM returns answer + confidence + suggested next actions.
6. Response shown with linked source articles.
7. If confidence below threshold, recommend ticket creation.

### Guardrails

- Must include source references for technical guidance.
- If no relevant sources: return “insufficient knowledge” response.
- No ticket mutation from assistant in phase 1.

## 5.2 AI Triage at Ticket Intake

### Goal
Improve speed and consistency of classification, prioritization, and assignment recommendations.

### Flow

1. New ticket arrives via portal or email.
2. Orchestrator receives ticket text, requester context, branch/service metadata.
3. Model predicts category, subcategory, urgency, and summary.
4. Assignment suggestion generated (agent/group) with rationale.
5. Deterministic rules still run through `AssignmentResolverService`.
6. Policy engine compares AI confidence + rule output.
7. System records recommendation and (phase dependent):
   - Human review queue, or
   - Auto-apply if high-confidence and allowed.

### Guardrails

- AI cannot bypass blocked categories or compliance flags.
- High-risk categories (e.g., security incidents) always require human approval initially.
- Unknown/conflicting suggestions must fallback to deterministic routing.

## 5.3 Agent Reply Copilot

### Goal
Generate first-draft responses grounded in ticket history + KB.

### Flow

1. Agent opens ticket and clicks “Draft Reply with AI”.
2. Orchestrator gathers ticket timeline, latest note, and top KB references.
3. LLM drafts response with tone/policy template.
4. Agent edits and approves before sending.
5. Send event logged with `aiAssisted=true` and final edit diff score.

### Guardrails

- No direct send by AI in initial phases.
- Mandatory human review before outbound notification.

---

## 6) Data Contracts

All AI interfaces should be strongly typed and versioned.

## 6.1 Common Envelope

```json
{
  "contractVersion": "1.0",
  "requestId": "uuid",
  "timestampUtc": "2026-04-10T12:00:00Z",
  "tenantId": "default",
  "channel": "portal|email|agent_console"
}
```

## 6.2 Assistant Query Contract

### Request: `POST /ai/assist/query`

```json
{
  "contractVersion": "1.0",
  "requestId": "8e87f6c2-7c62-49da-9d2e-7cb4ea8a8fab",
  "channel": "portal",
  "user": {
    "portalUserId": 123,
    "employeeId": 456,
    "role": "End User",
    "branchId": 8
  },
  "question": "My VPN is failing after password reset. What should I do?",
  "context": {
    "companyServiceId": 4,
    "deviceType": "Windows Laptop",
    "locale": "en-US"
  },
  "options": {
    "maxCitations": 5,
    "allowTicketCreationSuggestion": true
  }
}
```

### Response

```json
{
  "requestId": "8e87f6c2-7c62-49da-9d2e-7cb4ea8a8fab",
  "decisionStatus": "accepted",
  "confidence": 0.86,
  "answer": "Try reconnecting with the updated credentials and clear cached VPN credentials...",
  "citations": [
    {
      "sourceType": "kb_article",
      "sourceId": 77,
      "title": "VPN Access After Password Changes",
      "snippet": "...clear Windows Credential Manager entries...",
      "score": 0.91
    }
  ],
  "recommendedActions": [
    "Reset cached credentials",
    "Re-authenticate VPN client",
    "Open ticket if issue persists"
  ],
  "fallback": {
    "shouldOfferTicket": false,
    "reason": null
  }
}
```

## 6.3 Triage Recommendation Contract

### Request: `POST /ai/triage/recommend`

```json
{
  "contractVersion": "1.0",
  "requestId": "e5d97213-0e6c-499a-a8c6-40c2d44aaf0b",
  "channel": "email",
  "ticketDraft": {
    "title": "Cannot access shared drive from remote",
    "description": "User reports VPN connects but \"Z drive unavailable\".",
    "submittedByEmail": "user@company.com",
    "branchId": 8,
    "companyServiceId": 2,
    "attachments": []
  },
  "constraints": {
    "allowedCategories": [
      "ServiceRequest",
      "HardwareIssue",
      "SoftwareIssue",
      "EmployeeIssue",
      "NetworkIssue",
      "SecurityIncident",
      "Other"
    ],
    "highRiskRequiresApproval": true
  }
}
```

### Response

```json
{
  "requestId": "e5d97213-0e6c-499a-a8c6-40c2d44aaf0b",
  "decisionStatus": "needs_human_review",
  "confidence": 0.74,
  "predictions": {
    "category": "NetworkIssue",
    "subcategoryId": 14,
    "priority": "Medium",
    "urgency": "Normal",
    "impact": "Single User"
  },
  "assignment": {
    "recommendedAssigneeId": 33,
    "recommendedGroupId": 5,
    "reasoning": "Remote connectivity + drive mapping incidents are handled by Network Ops group."
  },
  "summary": {
    "oneLine": "Remote user cannot access mapped drive despite VPN connection.",
    "structured": {
      "symptom": "Mapped drive unavailable",
      "environment": "Remote VPN session",
      "businessImpact": "User blocked from shared files"
    }
  },
  "policy": {
    "autoApplyEligible": false,
    "blockedReasons": [
      "confidence_below_threshold"
    ]
  }
}
```

## 6.4 Reply Draft Contract

### Request: `POST /ai/reply/draft`

```json
{
  "contractVersion": "1.0",
  "requestId": "b9f8a5d6-54b0-4e5d-a2a2-b15a9f5b44da",
  "ticketId": 10234,
  "audience": "end_user",
  "tone": "professional_friendly",
  "goal": "request_more_information",
  "constraints": {
    "maxWords": 180,
    "mustIncludeChecklist": true
  }
}
```

### Response

```json
{
  "requestId": "b9f8a5d6-54b0-4e5d-a2a2-b15a9f5b44da",
  "decisionStatus": "accepted",
  "confidence": 0.9,
  "subjectSuggestion": "Re: [#SS-10234] Additional details needed",
  "draftBody": "Thanks for the update. To move quickly, please share...",
  "sourceRefs": [
    {
      "sourceType": "canned_response",
      "sourceId": 2,
      "title": "Requesting More Information"
    }
  ],
  "warnings": []
}
```

## 6.5 Audit Event Contract

```json
{
  "eventId": "uuid",
  "eventType": "ai.triage.recommendation",
  "requestId": "uuid",
  "ticketId": 10234,
  "actor": {
    "type": "system|agent|portal_user",
    "id": "123"
  },
  "model": {
    "provider": "openai|ollama|vllm",
    "name": "model-id",
    "promptVersion": "triage-v3"
  },
  "inputHash": "sha256",
  "outputHash": "sha256",
  "confidence": 0.74,
  "decisionStatus": "needs_human_review",
  "applied": false,
  "review": {
    "reviewedBy": 44,
    "reviewAction": "approved|edited|rejected",
    "reviewedAtUtc": "2026-04-10T12:05:00Z"
  }
}
```

---

## 7) Database Design Additions (Proposed)

## 7.1 `AiRunLog`

Tracks every AI request/response execution.

Columns (minimum):

- `Id` (PK)
- `RequestId` (unique)
- `Feature` (`assistant|triage|draft_reply`)
- `TicketId` (nullable)
- `PortalUserId` (nullable)
- `ModelProvider`, `ModelName`, `PromptVersion`
- `InputJson` (redacted)
- `OutputJson` (redacted)
- `Confidence` (decimal)
- `DecisionStatus`
- `LatencyMs`
- `CreatedDateUtc`

## 7.2 `AiRecommendation`

Stores structured recommendation objects tied to tickets.

- `Id` (PK)
- `TicketId` (FK)
- `RecommendationType` (`triage|assignment|priority|summary`)
- `PayloadJson`
- `Confidence`
- `IsApplied`
- `AppliedByPortalUserId` (nullable)
- `AppliedDateUtc` (nullable)
- `CreatedDateUtc`

## 7.3 `AiFeatureFlag`

Scoped controls for rollout and policy.

- `Id` (PK)
- `FeatureKey`
- `IsEnabled`
- `Environment` (`dev|test|prod`)
- `Cohort` (`all|pilot|role:<name>|branch:<id>`)
- `Mode` (`off|recommend_only|human_approval|auto_apply`)
- `ConfidenceThreshold`
- `UpdatedBy`
- `UpdatedDateUtc`

---

## 8) Rollout Controls and Governance

## 8.1 Feature Modes

Each AI capability must support these modes:

1. **OFF** – capability disabled.
2. **RECOMMEND_ONLY** – AI visible to user/agent but no state change.
3. **HUMAN_APPROVAL** – AI can prepare actions; human must approve.
4. **AUTO_APPLY** – allowed only for low-risk, high-confidence scenarios.

## 8.2 Gating Dimensions

- Environment (dev/test/prod)
- Role (Admin, IT Agent, Viewer, End User)
- Branch/team cohort
- Ticket category risk level
- Confidence threshold
- Time-windowed ramp (e.g., 10% → 25% → 50% → 100%)

## 8.3 Hard Stops (Non-Negotiable)

- SecurityIncident category cannot auto-close.
- AI cannot delete tickets/comments.
- AI cannot send outbound messages without approval in early phases.
- Any schema validation failure forces fallback to non-AI path.

## 8.4 Fallback Strategy

If AI fails (timeout/error/low confidence):

- Use existing deterministic category/assignment logic.
- Present standard portal KB search results.
- Continue standard ticket creation and notification workflow.
- Record fallback reason in audit logs.

## 8.5 Observability and SLOs

Track:

- Request success rate
- P95 latency by feature
- Recommendation acceptance rate
- Auto-apply rollback rate
- Hallucination incident count
- Triage precision/recall (against human-labeled outcomes)

Suggested initial SLOs:

- Assistant response latency P95 < 3.5s
- Triage recommendation latency P95 < 2.5s
- AI service availability >= 99.5% (business hours)

---

## 9) Security, Privacy, and Compliance Controls

1. **Data minimization:** send only required fields to model.
2. **PII handling:** redact tokens, secrets, and sensitive identifiers.
3. **Encryption:** TLS in transit; encrypted audit storage at rest.
4. **Access control:** RBAC for AI settings and AI review actions.
5. **Retention policy:** shorter retention for raw prompts than structured outcomes.
6. **Prompt injection defense:** retrieval source allowlist + instruction hierarchy.
7. **Output safety filter:** validate for policy violations before user display.

---

## 10) Implementation Roadmap

## Phase 0: Foundations (2–3 weeks)

- Implement AI orchestrator service skeleton.
- Add audit tables and feature flag tables.
- Add health and telemetry instrumentation.

## Phase 1: Portal Assistant Pilot (3–4 weeks)

- RAG from KB + canned responses.
- End-user pilot cohort (single branch/team).
- Recommend-only mode.

Exit criteria:

- >= 20% deflection on pilot-eligible issues.
- < 3% “unsafe/incorrect” response incidents.

## Phase 2: Triage Recommendations (4–6 weeks)

- Add intake recommendation API for portal/email.
- Human approval queue in ticket console.
- Confidence-based prioritization.

Exit criteria:

- >= 70% category agreement with agents.
- >= 60% assignment recommendation acceptance.

## Phase 3: Controlled Automation (4–8 weeks)

- Enable auto-apply for approved low-risk categories.
- Add rollback and drift detection dashboards.
- Expand to broader cohorts gradually.

Exit criteria:

- Stable acceptance and low rollback for 30 days.

---

## 11) API and UI Integration Checklist

### Backend

- Add `AiController` endpoints with authorization policies.
- Add orchestrator interfaces and provider adapters.
- Add structured validators for each contract.
- Persist AI run logs and recommendation records.

### Portal UI

- Add assistant panel to portal KB/ticket submit pages.
- Add citation rendering and “create ticket” handoff.

### Agent Console UI

- Add triage recommendation card in ticket details.
- Add approve/edit/reject controls with reasoning.
- Add AI draft-reply action and final review state.

### Settings UI

- Add AI feature toggles, thresholds, and mode controls.
- Add model/provider configuration and prompt version selectors.

---

## 12) Risks and Mitigations

1. **Hallucinated solutions**  
   Mitigation: strict retrieval grounding + citations + fallback messaging.

2. **Mis-triage causing delays**  
   Mitigation: recommend-only rollout and deterministic rule comparison.

3. **Agent trust/adoption risk**  
   Mitigation: transparent reasoning, editable suggestions, KPI transparency.

4. **Cost/runtime unpredictability**  
   Mitigation: model routing policies, caching, token budgets, latency budgets.

5. **Prompt drift/regression**  
   Mitigation: prompt versioning, canary cohorts, offline benchmark suite.

---

## 13) Definition of Done (AI Release Readiness)

A feature is production-ready only when:

- Contracts are versioned and validated.
- Audit logs are complete and queryable.
- Fallback path is tested and proven.
- Security controls pass review.
- Runbooks and on-call alerts are in place.
- KPI dashboard is live with baseline comparisons.

---

## 14) Recommended Next Artifacts

1. **Prompt Library Spec** (system/user templates, safety clauses, versioning).
2. **AI Evaluation Harness** (golden ticket set + expected outcomes).
3. **Risk Register** with severity/likelihood scoring.
4. **Operational Runbook** for incidents, degradations, and rollback.
5. **Cost Model Worksheet** (per feature and per ticket volume band).

