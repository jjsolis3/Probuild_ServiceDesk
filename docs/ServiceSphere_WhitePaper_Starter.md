# ServiceSphere White Paper Starter

## 1) Executive Overview

ServiceSphere is an ASP.NET Core MVC service desk platform for IT operations with two major user experiences:

- **IT Operations Console** for admins/agents to manage tickets, employees, assets, services, reporting, and system settings.
- **Self-Service User Portal** for end users to submit tickets, track status, collaborate on existing tickets, and search the knowledge base.

The current implementation already includes advanced service desk capabilities such as:

- Rule-based assignment and intake routing.
- Email-to-ticket ingestion with threading and anti-duplicate guardrails.
- Configurable outbound notification templates.
- Knowledge base publishing and portal search.
- Saved ticket views, subcategories, canned responses, and ticket history/audit support.

---

## 2) How the Application Works (Technical View)

### 2.1 Platform and Runtime

- Built on **ASP.NET Core** with MVC controllers and Razor views.
- Uses **Entity Framework Core** with SQL Server.
- Uses **cookie authentication** with role-based authorization.
- Registers a background hosted service to poll Gmail and ingest email into tickets.

### 2.2 Core Architecture

ServiceSphere follows a layered structure:

- `ServiceDesk.Core`: Domain models, enums, and business utilities.
- `ServiceDesk.Infrastructure`: EF Core `DbContext` and startup schema upgrade/seeding logic.
- `ServiceDesk.Web`: Controllers, views, filters, and operational services.

### 2.3 Security and Access Model

- Default authentication requires signed-in users.
- Policy `ITStaff` targets `Admin` and `IT Agent` roles.
- IT ticket console actions are role-restricted.
- Portal users can only see and reply to their own tickets.

---

## 3) Feature Inventory for White Paper Content

### 3.1 Ticket Management (Core)

- Multi-criteria filtering (status, category, priority, assignee, requester, unmatched, unassigned).
- Full-text style query across title/description/resolution notes.
- Saved/shared/default ticket view presets.
- Pagination and server-side sorting.
- Details view with notes, attachments, history, service, branch, and subcategory context.

### 3.2 Ticket Intake and Routing

- Manual ticket creation in console and self-service submission in portal.
- SLA due date auto-calculation based on priority.
- Rule-driven assignee resolution with specificity tiers:
  1. Category + Subcategory + Branch
  2. Category + Subcategory
  3. Category + Branch
  4. Category only
  5. Branch only
  6. Catch-all
  7. Default assignee fallback
- Category inference from keyword matching (database-backed keywords with hardcoded fallback).

### 3.3 Email Integration and Notifications

- Polls active authorized Gmail inboxes on interval.
- Detects existing ticket conversations via subject token format: `[#SS-<id>]`.
- Uses `In-Reply-To` and `References` headers for email threading.
- Prevents duplication and loops through multiple guardrails:
  - Skip self-sent messages
  - Skip internal auto-generated messages
  - Skip previously processed Message-ID and Gmail message IDs
  - Skip common auto-replies/bounces
- Creates new tickets from emails when enabled, including auto-categorization and assignment.
- Sends branded outbound notifications for:
  - Ticket created confirmation
  - Ticket assigned
  - Ticket updated
  - Note/comment added
  - Password reset

### 3.4 Settings Hub

- Segmented settings domains:
  - Account settings and app-level controls
  - Branding controls (company identity and visual theme)
  - Email template management
  - Email integration configuration
  - Roles/users/groups/branches
  - Ticket states, resolution codes, assignment rules
  - Category keywords, subcategories, canned responses
- Branding updates invalidate in-memory cache to take effect immediately.

### 3.5 User Portal

- End user dashboard with KPI counters and ticket list pagination.
- Ticket submission with service selection and status tracking.
- Ticket detail visibility restricted to submitter.
- End-user replies are added as ticket notes and can trigger agent notifications.
- Portal knowledge base search by free-text and category.

### 3.6 Knowledge Base and Resolution Enablement

- KB article lifecycle with publish state and category indexing.
- Agent canned responses for repeatable support communication.
- Ticket subcategories and keyword tagging improve classification and routing quality.

---

## 4) Process Flow Narratives (White Paper Ready)

### 4.1 End-User Portal Ticket Flow

1. User signs in to portal.
2. User submits ticket with category/priority/service details.
3. System creates ticket and logs a system note.
4. Confirmation email can be sent to requester.
5. IT agent receives and works ticket.
6. User can view status and reply in portal.
7. Ticket transitions through statuses until resolution/closure.

### 4.2 Email-to-Ticket Flow

1. Gmail polling job checks active inbox configurations.
2. New email is validated against anti-loop and anti-duplicate rules.
3. System attempts to map email to existing ticket via subject token + email headers.
4. If matched: adds note to ticket thread.
5. If not matched and intake is enabled: creates new ticket.
6. Category detection + assignment resolution applied.
7. Optional confirmation/notification emails sent with thread-safe headers.

### 4.3 Admin/Agent Operations Flow

1. Agent opens ticket console with default or selected saved view.
2. Agent applies filters/sort and opens ticket details.
3. Agent updates assignee/status/priority/notes.
4. System writes history entries and emits configured notifications.
5. Agent optionally converts repeat outcomes into KB/canned responses.

---

## 5) “What’s New” Areas to Highlight in the White Paper

Given your note that updates were applied to tickets, settings hub, email notifications, and user portal, the current codebase reflects those areas through:

- Enhanced ticket operations with saved views, broader filtering, and richer detail context.
- Expanded settings scope (templates, routing, branding, access structures).
- More complete email pipeline (inbound parsing/threading + outbound templated notifications).
- Stronger portal experience (self-service ticketing, response loop, and KB access).

---

## 6) AI Component Feasibility and Options

Yes—an AI component is very feasible for ServiceSphere. The current architecture already has ideal integration points (portal, ticket intake, email processing, and routing services).

### 6.1 High-Value AI Use Cases

#### A) Portal AI Assistant (User-facing)

- Conversational helper in the portal to answer “how do I fix X?”
- Retrieves relevant KB articles and known resolution snippets.
- Can suggest whether to create a ticket if no solution is found.

**Business impact:** faster self-resolution, fewer low-complexity tickets, better user satisfaction.

#### B) AI-Assisted Ticket Intake and Triage

- Classify incoming ticket category/subcategory from text/email.
- Recommend priority and assignment.
- Draft concise summaries for agents (“issue, environment, impact, requested action”).
- Suggest initial response text using canned response + KB context.

**Business impact:** reduced triage overhead, better routing accuracy, shorter first-response times.

#### C) Agent Copilot

- Inline “next best action” and troubleshooting guidance.
- Auto-generate reply drafts based on ticket history and KB.
- Recommend related incidents to identify larger outage patterns.

**Business impact:** improved agent throughput and consistency.

### 6.2 Integration Points in Current Code

- **PortalController**: inject assistant endpoints into `KnowledgeBase`, `SubmitTicket`, and `TicketDetail` interactions.
- **GmailApiService**: run AI enrichment after inbound parse (category, summary, urgency cues).
- **AssignmentResolverService**: combine deterministic rules with AI recommendation/confidence.
- **EmailNotificationService**: generate intelligent response drafts for approval before send.
- **Settings**: add AI governance controls (on/off, confidence thresholds, human approval mode).

### 6.3 Implementation Strategy (Recommended)

1. **Phase 1: AI Read-Only Assistant**
   - Portal search helper grounded strictly in KB + canned responses.
   - No autonomous ticket changes.
2. **Phase 2: AI Triage Recommendations**
   - Add predicted category/subcategory/assignee/priority fields as recommendations.
   - Human acceptance required.
3. **Phase 3: Controlled Automation**
   - Auto-apply only high-confidence routing under policy.
   - Maintain audit logs and rollback controls.

### 6.4 Open-Source AI Options

You can use open-source components, especially if you want host-controlled/private deployment.

- **LLM serving/orchestration**: Ollama, vLLM, Hugging Face Transformers.
- **Retrieval / RAG tooling**: Haystack, LlamaIndex, LangChain.
- **Vector stores**: Qdrant, Weaviate, Milvus, pgvector (PostgreSQL extension).
- **Embedding models (open-source)**: BAAI/bge family, sentence-transformers variants.
- **Rerankers**: bge-reranker or cross-encoder models for better KB result quality.

### 6.5 Commercial + Open Model Hybrid (Practical Option)

A very practical architecture is hybrid:

- Use a managed model API for advanced reasoning and quality.
- Keep retrieval and business rules in your own app/database.
- Retain assignment/routing guardrails in deterministic logic.

This gives rapid rollout with strong control over safety and explainability.

### 6.6 Governance Requirements (Must-Have)

For production AI in ServiceSphere, include:

- Human-in-the-loop approvals for state-changing actions.
- Prompt/context logging with privacy controls.
- Role-based AI feature access.
- Confidence thresholds and fallback behavior.
- Hallucination controls via retrieval grounding and citation of KB source.
- Metrics: deflection rate, triage accuracy, first-response time, reopen rate.

---

## 7) Suggested White Paper Structure (You Can Reuse)

1. Problem Statement and ITSM Context
2. ServiceSphere Platform Overview
3. Core Functional Capabilities
4. Updated Enhancements (Tickets, Settings, Email, Portal)
5. End-to-End Process Flows
6. Security, Governance, and Auditability
7. AI Roadmap and Operating Model
8. Deployment Options (Cloud / Hybrid / Private)
9. Measurable Outcomes and KPIs
10. Future Roadmap

---

## 8) Next Deliverables You Can Request

To continue this documentation effort, next artifacts can be produced as separate documents:

- **Detailed System Architecture White Paper** (technical deep dive).
- **Process Flow Diagrams** (BPMN/swimlane style for portal, email, triage).
- **AI Solution Design** (target architecture, data contracts, rollout controls).
- **Implementation Backlog** (epics/stories for phased delivery).
- **Security & Compliance Addendum** for AI-assisted support workflows.

