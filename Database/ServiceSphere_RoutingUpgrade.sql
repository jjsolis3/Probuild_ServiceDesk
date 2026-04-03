-- =====================================================================
-- ServiceSphere Database - Routing & Assignment Upgrade Script
-- Target: Microsoft SQL Server 2016+
-- Database: ServiceSphere
-- =====================================================================
--
-- PURPOSE:
--   Adds branch/location awareness and smart ticket routing.
--
-- CHANGES:
--   1. Employees table:
--      - ADD BranchId (nullable FK to Branches)
--
--   2. Tickets table:
--      - ADD BranchId (nullable FK to Branches, snapshot of submitter's
--        branch at the time the ticket was created)
--
--   3. New table: AssignmentRules
--      - Each rule maps (Category?, BranchId?) → AssigneeId
--      - Evaluated in SortOrder ASC; first match wins
--      - NULL Category  = match any category
--      - NULL BranchId  = match any branch/location
--
-- IDEMPOTENCY:
--   Safe to run multiple times. All DDL operations are guarded with
--   IF EXISTS / IF NOT EXISTS checks.
--
-- =====================================================================

USE [ServiceSphere];
GO

-- =====================================================================
-- STEP 1: Add BranchId to Employees
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Employees')
      AND name = 'BranchId'
)
BEGIN
    ALTER TABLE dbo.Employees
        ADD BranchId INT NULL
            CONSTRAINT FK_Employees_Branches
            REFERENCES dbo.Branches(Id)
            ON DELETE SET NULL;

    PRINT 'Added BranchId column to Employees.';
END
ELSE
    PRINT 'Employees.BranchId already exists — skipping.';
GO

-- =====================================================================
-- STEP 2: Add BranchId to Tickets
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Tickets')
      AND name = 'BranchId'
)
BEGIN
    ALTER TABLE dbo.Tickets
        ADD BranchId INT NULL
            CONSTRAINT FK_Tickets_Branches
            REFERENCES dbo.Branches(Id)
            ON DELETE SET NULL;

    PRINT 'Added BranchId column to Tickets.';
END
ELSE
    PRINT 'Tickets.BranchId already exists — skipping.';
GO

-- =====================================================================
-- STEP 3: Create AssignmentRules table
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = 'AssignmentRules'
)
BEGIN
    CREATE TABLE dbo.AssignmentRules (
        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        Name        NVARCHAR(200)   NOT NULL,
        -- NULL = match any category (enum: 0=ServiceRequest … 6=Other)
        Category    INT             NULL,
        -- NULL = match any branch
        BranchId    INT             NULL
            CONSTRAINT FK_AssignmentRules_Branches
            REFERENCES dbo.Branches(Id)
            ON DELETE SET NULL,
        AssigneeId  INT             NOT NULL
            CONSTRAINT FK_AssignmentRules_Employees
            REFERENCES dbo.Employees(Id)
            ON DELETE NO ACTION,
        SortOrder   INT             NOT NULL DEFAULT 100,
        IsActive    BIT             NOT NULL DEFAULT 1,
        CreatedDate DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
    );

    -- Index for fast rule lookup (active rules ordered by priority)
    CREATE INDEX IX_AssignmentRules_Active_Sort
        ON dbo.AssignmentRules (IsActive, SortOrder);

    PRINT 'AssignmentRules table created.';
END
ELSE
    PRINT 'AssignmentRules already exists — skipping.';
GO

-- =====================================================================
-- STEP 4: (Optional) Seed example rules — REMOVE if not needed
-- =====================================================================
-- Uncomment and customize these to pre-populate rules after running:
--
-- INSERT INTO dbo.AssignmentRules (Name, Category, BranchId, AssigneeId, SortOrder)
-- VALUES
--   -- Hardware tickets anywhere → Mike Davis (employee 3)
--   ('Hardware – Any Location', 1, NULL, 3, 20),
--   -- Network tickets anywhere → Sarah Johnson (employee 2)
--   ('Network – Any Location',  4, NULL, 2, 20),
--   -- Security incidents → John Smith (employee 1)
--   ('Security Incidents',      5, NULL, 1, 10),
--   -- All other tickets (catch-all) → Mike Davis
--   ('Catch-All',               NULL, NULL, 3, 999);
--
-- Category enum values:
--   0 = ServiceRequest
--   1 = HardwareIssue
--   2 = SoftwareIssue
--   3 = EmployeeIssue
--   4 = NetworkIssue
--   5 = SecurityIncident
--   6 = Other
-- =====================================================================

-- =====================================================================
-- VERIFICATION
-- =====================================================================
SELECT 'Employees (with BranchId)'  AS Info, COUNT(*) AS Rows FROM dbo.Employees;
SELECT 'Tickets (with BranchId)'    AS Info, COUNT(*) AS Rows FROM dbo.Tickets;
SELECT 'AssignmentRules'            AS Info, COUNT(*) AS Rows FROM dbo.AssignmentRules;
GO

PRINT '=============================================';
PRINT 'ServiceSphere routing upgrade complete!';
PRINT '=============================================';
GO
