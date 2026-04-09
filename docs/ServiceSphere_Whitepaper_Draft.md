# ServiceSphere White Paper Documentation Starter

## 1) Executive Overview
ServiceSphere is an ASP.NET Core MVC service desk platform built for internal IT operations and employee self-service. The application combines ticket intake, assignment/routing, collaboration, reporting, asset and subscription tracking, knowledge management, and administrative controls in a single system.

At a high level, ServiceSphere supports three major user experiences:
- **IT Operations Console** (Admin, IT Agent, Viewer roles) for managing tickets, assets, reports, and system settings.
- **Employee Self-Service Portal** for ticket submission, ticket follow-up, and knowledge base search.
- **Email-integrated Service Desk** for inbound/outbound ticket communication using Gmail API threading and background polling.

---

## 2) Application Scope and Feature Set

### 2.1 Core Service Desk / Ticketing
ServiceSphere ticketing includes:
- Ticket creation (manual, portal-based, and email-ingested).
- Priority, category, subcategory, status, assignee, and branch context.
- SLA-style due date support.
- Ticket history/audit events.
- Internal and external notes.
- Attachment uploads.
- Quick updates and bulk updates.
- Ticket merge capability.
- CSV/TSV import with preview/validation.
- Saved ticket views and defaults for personalization.

### 2.2 Self-Service Portal
Employees can:
- Sign in and view their own ticket dashboard.
- Submit new tickets against active company services.
- Add replies on existing tickets.
- View non-internal ticket conversation history.
- Search and filter published knowledge base articles.

### 2.3 Email Operations and Notifications
ServiceSphere integrates with Gmail to:
- Poll configured inboxes in the background.
- Parse/ingest inbound messages as new tickets or threaded updates.
- Detect ticket references in subject line (`[#SS-12345]`).
- Thread via `In-Reply-To` and `References` headers.
- Prevent loops and duplicate processing.
- Send confirmations, assignment notifications, update notices, comment notifications, and password reset emails.

### 2.4 Assignment and Routing
Assignment is rules-driven and evaluated by specificity:
1. Category + Subcategory + Branch
2. Category + Subcategory
3. Category + Branch
4. Category only
5. Branch only
6. Catch-all
7. Configured default assignee fallback

Automatic category detection is also supported for email-created tickets using keyword scoring.

### 2.5 Operations Management Modules
Beyond ticketing, the app includes:
- **Employees** management (including active status and branch linkage).
- **Assets** inventory and assignment tracking.
- **Subscriptions** tracking (costs, renewal dates, active status).
- **Services** catalog for ticket context.
- **Knowledge Base** article lifecycle and publication controls.
- **Reports** for tickets, assets, users, and KPI drilldowns.

### 2.6 Administrative Configuration
Admin-level settings include:
- Roles and portal users.
- Branches and groups.
- Ticket states.
- Resolution codes.
- Canned responses.
- Assignment rules.
- Ticket subcategories.
- Email integration configuration.
- Account/system settings via key-value app settings.

---

## 3) How the Application Works (Functional Architecture)

### 3.1 Technology and Runtime
- **Framework:** ASP.NET Core MVC
- **Data Access:** Entity Framework Core with SQL Server
- **Auth:** Cookie-based authentication and role-based authorization
- **Background Processing:** Hosted service for Gmail polling

### 3.2 Access Model
- Default policy requires authenticated users.
- IT dashboard capabilities are guarded by role policy (`Admin`, `IT Agent`).
- Self-service portal is available to authenticated users, with role-based redirect behavior after login.

### 3.3 Startup Lifecycle
On startup, ServiceSphere:
1. Configures DI services (EF DbContext, auth, hosted Gmail service, app services).
2. Applies schema upgrades (incremental, idempotent SQL checks).
3. Seeds reference/system data.
4. Starts web request pipeline and hosted background polling.

### 3.4 Domain/Data Model Overview
The system is centered around these entity families:
- **People & Access:** Employee, PortalUser, Role, UserGroup.
- **Service Desk Core:** Ticket, TicketNote, TicketEmail, TicketAttachment, TicketHistory.
- **Service Taxonomy:** CompanyService, TicketSubCategory, TicketState, ResolutionCode.
- **IT Operations:** Asset, Subscription, Branch.
- **Knowledge:** KbArticle.
- **Automation/Configuration:** AssignmentRule, EmailConfiguration, AppSetting, SavedTicketView, CannedResponse.

### 3.5 UI and Controller Boundaries
- `HomeController` provides operations dashboard and KPI APIs.
- `TicketsController` handles the full ticket lifecycle and bulk/merge/import workflows.
- `PortalController` drives end-user portal actions and KB consumption.
- `ReportsController` serves analytics views and report APIs.
- `SettingsController` centralizes admin configuration features.
- Resource controllers exist for Employees, Assets, Services, Subscriptions, and KB administration.

---

## 4) Process Flows (White Paper Ready)

### 4.1 Primary Service Request Flow (Portal)
1. Employee authenticates.
2. Employee submits ticket in portal.
3. Ticket is created with branch/service context.
4. System note is appended.
5. Optional confirmation email is dispatched.
6. IT agent triages, updates status, and collaborates via notes.
7. Resolution is recorded and ticket moves to resolved/closed states.

### 4.2 Email-Initiated Ticket Flow
1. Gmail background poller retrieves new messages from authorized mailboxes.
2. Loop/duplicate/autoreply guards are evaluated.
3. System attempts to match ticket thread by subject token or email headers.
4. If match found, message becomes a new ticket note.
5. If no match and mailbox allows ticket creation, new ticket is generated.
6. Category is detected; assignment rules determine assignee.
7. Thread metadata is stored so future replies stay linked.

### 4.3 Agent Triage and Resolution Flow
1. Agent opens ticket list with filters, saved views, and sorting.
2. Agent claims/assigns ticket (or bulk assigns).
3. Agent uses canned response and notes to communicate.
4. Attachments and history provide operational context.
5. Agent updates status (open → in progress → resolved/closed).
6. Notifications are sent to relevant participants.

### 4.4 Administration and Governance Flow
1. Admin configures roles/users/groups and branch structure.
2. Admin defines ticket states, resolution codes, categories/subcategories.
3. Admin defines assignment rules and default assignee strategy.
4. Admin configures email integrations.
5. Admin monitors reporting trends and adjusts processes.

---

## 5) Reporting and Operational Intelligence
ServiceSphere provides ticket/asset/user analytics including:
- Status and priority distributions.
- Category mix and monthly ticket trends.
- Resolution time indicators.
- Top assignee load/performance snapshots.
- Asset value, type/status spread, and warranty-risk windows.
- Department-level usage and service demand indicators.

This supports both tactical support operations and strategic IT planning inputs for executive white papers.

---

## 6) Security, Controls, and Reliability Notes
- Cookie authentication with explicit login/logout/access-denied paths.
- Role-based authorization for operational boundaries.
- Password hashing and reset token expiry handling.
- Ticket/email anti-duplication constraints.
- Data integrity constraints (unique indexes on key identifiers like email, asset tag, etc.).
- Hosted service resilience via exception handling and periodic retry loops.

---

## 7) Suggested White Paper Narrative Structure
Use this application understanding to build formal white papers in this sequence:
1. **Business Problem and Operational Pain Points**
2. **ServiceSphere Solution Overview**
3. **Functional Capabilities and Differentiators**
4. **Process Modernization (Current vs. Future State)**
5. **Architecture and Security Posture**
6. **Adoption Model (Roles, governance, rollout)**
7. **Value Realization Metrics (KPIs and outcomes)**
8. **Roadmap and scalability opportunities**

---

## 8) Draft KPI Set for White Paper Outcomes
- Mean time to acknowledge (MTTA)
- Mean time to resolve (MTTR)
- SLA breach rate
- First-response time
- Reopen rate
- Ticket backlog aging
- Category/priority volume trends
- Asset warranty risk and replacement timing
- Cost visibility (subscription monthly spend)

---

## 9) Next Documentation Artifacts to Produce
Recommended follow-on documents:
1. **System Architecture Diagram** (logical components + integrations)
2. **Data Dictionary** (entity-level definitions and relationships)
3. **Role/Permission Matrix** (Admin, IT Agent, Viewer, End User)
4. **Standard Operating Procedures** (triage, escalation, closure)
5. **Incident/Request Lifecycle SLA Matrix**
6. **Deployment & Environment Guide**
7. **Disaster Recovery / Backup Strategy**
8. **Security & Compliance Controls Mapping**

---

## 10) Notes and Assumptions
This starter document is based on code-level review of controllers, services, models, and infrastructure setup. It is intended as a white paper foundation and should be supplemented by:
- Environment-specific deployment architecture (hosting, networking, secrets management)
- Real operational policy (actual SLAs, escalation tiers)
- Current production metrics and business outcomes
