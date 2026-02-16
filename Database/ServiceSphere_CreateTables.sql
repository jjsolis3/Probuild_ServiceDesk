-- =====================================================================
-- ServiceSphere Database - Table Creation & Seed Data Script
-- Target: Microsoft SQL Server 2016+
-- Database: ServiceSphere
-- Server: 74.114.167.52
-- =====================================================================
-- Run this script against the ServiceSphere database after it has been created.
-- =====================================================================

USE [ServiceSphere];
GO

-- =====================================================================
-- DROP EXISTING TABLES (if re-running script)
-- Drop in reverse dependency order
-- =====================================================================
IF OBJECT_ID('dbo.Tickets', 'U') IS NOT NULL DROP TABLE dbo.Tickets;
IF OBJECT_ID('dbo.Assets', 'U') IS NOT NULL DROP TABLE dbo.Assets;
IF OBJECT_ID('dbo.Subscriptions', 'U') IS NOT NULL DROP TABLE dbo.Subscriptions;
IF OBJECT_ID('dbo.CompanyServices', 'U') IS NOT NULL DROP TABLE dbo.CompanyServices;
IF OBJECT_ID('dbo.Employees', 'U') IS NOT NULL DROP TABLE dbo.Employees;
GO

-- =====================================================================
-- TABLE 1: Employees
-- =====================================================================
CREATE TABLE dbo.Employees (
    Id              INT             IDENTITY(1,1) NOT NULL,
    FirstName       NVARCHAR(100)   NOT NULL,
    LastName        NVARCHAR(100)   NOT NULL,
    Email           NVARCHAR(200)   NOT NULL,
    Phone           NVARCHAR(20)    NULL,
    Department      NVARCHAR(100)   NOT NULL,
    JobTitle        NVARCHAR(100)   NULL,
    IsActive        BIT             NOT NULL DEFAULT 1,
    HireDate        DATETIME2       NOT NULL,

    CONSTRAINT PK_Employees PRIMARY KEY (Id)
);
GO

-- Unique index on Email
CREATE UNIQUE INDEX IX_Employees_Email ON dbo.Employees (Email);
GO

-- =====================================================================
-- TABLE 2: CompanyServices
-- =====================================================================
CREATE TABLE dbo.CompanyServices (
    Id                  INT             IDENTITY(1,1) NOT NULL,
    Name                NVARCHAR(200)   NOT NULL,
    Description         NVARCHAR(1000)  NULL,
    Category            NVARCHAR(100)   NOT NULL,
    Status              INT             NOT NULL DEFAULT 0,
        -- 0 = Active
        -- 1 = Inactive
        -- 2 = Maintenance
        -- 3 = Deprecated
    ServiceOwner        NVARCHAR(200)   NULL,
    SupportContact      NVARCHAR(500)   NULL,
    DocumentationUrl    NVARCHAR(500)   NULL,
    SlaHours            INT             NULL,

    CONSTRAINT PK_CompanyServices PRIMARY KEY (Id)
);
GO

-- =====================================================================
-- TABLE 3: Tickets
-- =====================================================================
CREATE TABLE dbo.Tickets (
    Id                  INT             IDENTITY(1,1) NOT NULL,
    Title               NVARCHAR(200)   NOT NULL,
    Description         NVARCHAR(2000)  NOT NULL,
    Category            INT             NOT NULL,
        -- 0 = ServiceRequest
        -- 1 = HardwareIssue
        -- 2 = SoftwareIssue
        -- 3 = EmployeeIssue
        -- 4 = NetworkIssue
        -- 5 = SecurityIncident
        -- 6 = Other
    Status              INT             NOT NULL DEFAULT 0,
        -- 0 = Open
        -- 1 = InProgress
        -- 2 = OnHold
        -- 3 = Resolved
        -- 4 = Closed
        -- 5 = Cancelled
    Priority            INT             NOT NULL DEFAULT 1,
        -- 0 = Low
        -- 1 = Medium
        -- 2 = High
        -- 3 = Critical
    CreatedDate         DATETIME2       NOT NULL,
    UpdatedDate         DATETIME2       NULL,
    ResolvedDate        DATETIME2       NULL,
    ClosedDate          DATETIME2       NULL,
    ResolutionNotes     NVARCHAR(2000)  NULL,

    -- Foreign Keys
    SubmittedById       INT             NOT NULL,
    AssignedToId        INT             NULL,
    CompanyServiceId    INT             NULL,

    CONSTRAINT PK_Tickets PRIMARY KEY (Id),
    CONSTRAINT FK_Tickets_SubmittedBy FOREIGN KEY (SubmittedById)
        REFERENCES dbo.Employees(Id) ON DELETE NO ACTION,
    CONSTRAINT FK_Tickets_AssignedTo FOREIGN KEY (AssignedToId)
        REFERENCES dbo.Employees(Id) ON DELETE NO ACTION,
    CONSTRAINT FK_Tickets_CompanyService FOREIGN KEY (CompanyServiceId)
        REFERENCES dbo.CompanyServices(Id) ON DELETE SET NULL
);
GO

-- Indexes for frequently queried columns
CREATE INDEX IX_Tickets_SubmittedById ON dbo.Tickets (SubmittedById);
CREATE INDEX IX_Tickets_AssignedToId ON dbo.Tickets (AssignedToId);
CREATE INDEX IX_Tickets_CompanyServiceId ON dbo.Tickets (CompanyServiceId);
CREATE INDEX IX_Tickets_Status ON dbo.Tickets (Status);
CREATE INDEX IX_Tickets_Priority ON dbo.Tickets (Priority);
GO

-- =====================================================================
-- TABLE 4: Assets
-- =====================================================================
CREATE TABLE dbo.Assets (
    Id              INT             IDENTITY(1,1) NOT NULL,
    Name            NVARCHAR(200)   NOT NULL,
    AssetTag        NVARCHAR(100)   NOT NULL,
    AssetType       INT             NOT NULL,
        -- 0 = Laptop
        -- 1 = Desktop
        -- 2 = Monitor
        -- 3 = Printer
        -- 4 = Phone
        -- 5 = Tablet
        -- 6 = Server
        -- 7 = NetworkEquipment
        -- 8 = Peripheral
        -- 9 = Software
        -- 10 = Other
    Status          INT             NOT NULL DEFAULT 0,
        -- 0 = Available
        -- 1 = Assigned
        -- 2 = InRepair
        -- 3 = Retired
        -- 4 = Lost
        -- 5 = Disposed
    Manufacturer    NVARCHAR(100)   NULL,
    Model           NVARCHAR(100)   NULL,
    SerialNumber    NVARCHAR(100)   NULL,
    PurchaseDate    DATETIME2       NULL,
    PurchaseCost    DECIMAL(18,2)   NULL,
    WarrantyExpiry  DATETIME2       NULL,
    Location        NVARCHAR(200)   NULL,
    Notes           NVARCHAR(500)   NULL,

    -- Foreign Key
    AssignedToId    INT             NULL,

    CONSTRAINT PK_Assets PRIMARY KEY (Id),
    CONSTRAINT FK_Assets_AssignedTo FOREIGN KEY (AssignedToId)
        REFERENCES dbo.Employees(Id) ON DELETE SET NULL
);
GO

-- Unique index on AssetTag
CREATE UNIQUE INDEX IX_Assets_AssetTag ON dbo.Assets (AssetTag);
CREATE INDEX IX_Assets_AssignedToId ON dbo.Assets (AssignedToId);
GO

-- =====================================================================
-- TABLE 5: Subscriptions
-- =====================================================================
CREATE TABLE dbo.Subscriptions (
    Id              INT             IDENTITY(1,1) NOT NULL,
    Name            NVARCHAR(200)   NOT NULL,
    Provider        NVARCHAR(200)   NOT NULL,
    Description     NVARCHAR(500)   NULL,
    Status          INT             NOT NULL DEFAULT 0,
        -- 0 = Active
        -- 1 = Expiring
        -- 2 = Expired
        -- 3 = Cancelled
        -- 4 = PendingRenewal
    LicenseCount    INT             NULL,
    LicensesUsed    INT             NULL,
    MonthlyCost     DECIMAL(18,2)   NOT NULL,
    AnnualCost      DECIMAL(18,2)   NULL,
    StartDate       DATETIME2       NOT NULL,
    RenewalDate     DATETIME2       NULL,
    Notes           NVARCHAR(500)   NULL,

    CONSTRAINT PK_Subscriptions PRIMARY KEY (Id)
);
GO


-- =====================================================================
-- SEED DATA
-- =====================================================================

-- ----- Employees -----
SET IDENTITY_INSERT dbo.Employees ON;

INSERT INTO dbo.Employees (Id, FirstName, LastName, Email, Phone, Department, JobTitle, IsActive, HireDate) VALUES
(1, N'John',     N'Smith',    N'john.smith@probuild.com',       N'555-0101', N'IT',          N'IT Manager',           1, '2020-03-15'),
(2, N'Sarah',    N'Johnson',  N'sarah.johnson@probuild.com',    N'555-0102', N'IT',          N'System Administrator', 1, '2021-06-01'),
(3, N'Mike',     N'Davis',    N'mike.davis@probuild.com',       N'555-0103', N'IT',          N'Help Desk Technician', 1, '2022-01-10'),
(4, N'Emily',    N'Wilson',   N'emily.wilson@probuild.com',     N'555-0104', N'Engineering', N'Software Engineer',    1, '2021-09-20'),
(5, N'David',    N'Brown',    N'david.brown@probuild.com',      N'555-0105', N'Finance',     N'Financial Analyst',    1, '2020-11-05'),
(6, N'Lisa',     N'Martinez', N'lisa.martinez@probuild.com',    N'555-0106', N'HR',          N'HR Specialist',        1, '2022-04-15'),
(7, N'Robert',   N'Taylor',   N'robert.taylor@probuild.com',    N'555-0107', N'Operations',  N'Operations Lead',      1, '2019-08-01'),
(8, N'Jennifer', N'Anderson', N'jennifer.anderson@probuild.com',N'555-0108', N'Marketing',   N'Marketing Manager',    1, '2021-02-14');

SET IDENTITY_INSERT dbo.Employees OFF;
GO

-- ----- CompanyServices -----
SET IDENTITY_INSERT dbo.CompanyServices ON;

INSERT INTO dbo.CompanyServices (Id, Name, Description, Category, Status, ServiceOwner, SupportContact, DocumentationUrl, SlaHours) VALUES
(1, N'Email & Collaboration',  N'Microsoft 365 email, Teams, SharePoint services',  N'Communication',          0, N'Sarah Johnson', N'it-support@probuild.com',  NULL, 4),
(2, N'VPN & Remote Access',    N'Corporate VPN and remote desktop services',         N'Network',                0, N'Sarah Johnson', N'network@probuild.com',     NULL, 2),
(3, N'ERP System',             N'Enterprise resource planning system',               N'Business Applications',  0, N'John Smith',    N'erp-support@probuild.com', NULL, 8),
(4, N'Print Services',         N'Network printing and scanning services',            N'Infrastructure',         0, N'Mike Davis',    N'helpdesk@probuild.com',    NULL, 24),
(5, N'Backup & Recovery',      N'Data backup and disaster recovery services',        N'Infrastructure',         0, N'Sarah Johnson', N'backup@probuild.com',      NULL, 1);

SET IDENTITY_INSERT dbo.CompanyServices OFF;
GO

-- ----- Assets -----
SET IDENTITY_INSERT dbo.Assets ON;

INSERT INTO dbo.Assets (Id, Name, AssetTag, AssetType, Status, Manufacturer, Model, SerialNumber, PurchaseDate, PurchaseCost, WarrantyExpiry, Location, Notes, AssignedToId) VALUES
(1, N'Dell Latitude 5540',    N'LAP-001', 0, 1, N'Dell',  N'Latitude 5540',    N'DL5540-001',    '2024-01-15', 1299.99, '2027-01-15', N'Office - 2nd Floor', NULL, 4),
(2, N'Dell Latitude 5540',    N'LAP-002', 0, 1, N'Dell',  N'Latitude 5540',    N'DL5540-002',    '2024-01-15', 1299.99, '2027-01-15', N'Office - 3rd Floor', NULL, 5),
(3, N'HP LaserJet Pro',       N'PRT-001', 3, 0, N'HP',    N'LaserJet Pro M404dn', N'HP-LJ-001',  '2023-06-01', 349.99,  '2026-06-01', N'Office - 1st Floor', NULL, NULL),
(4, N'Dell UltraSharp 27"',   N'MON-001', 2, 1, N'Dell',  N'U2723QE',          N'DU27-001',      '2024-03-10', 549.99,  '2027-03-10', N'Office - 2nd Floor', NULL, 4),
(5, N'Cisco Catalyst Switch', N'NET-001', 7, 0, N'Cisco', N'Catalyst 9200L',   N'CC-9200L-001',  '2023-01-20', 2499.99, '2028-01-20', N'Server Room',        NULL, NULL),
(6, N'Dell PowerEdge R750',   N'SRV-001', 6, 0, N'Dell',  N'PowerEdge R750',   N'DPE-R750-001',  '2023-09-05', 8999.99, '2028-09-05', N'Server Room',        NULL, NULL),
(7, N'iPhone 15 Pro',         N'PHN-001', 4, 1, N'Apple', N'iPhone 15 Pro',    N'APL-IP15P-001', '2024-10-01', 999.99,  '2026-10-01', N'Office',             NULL, 1);

SET IDENTITY_INSERT dbo.Assets OFF;
GO

-- ----- Subscriptions -----
SET IDENTITY_INSERT dbo.Subscriptions ON;

INSERT INTO dbo.Subscriptions (Id, Name, Provider, Description, Status, LicenseCount, LicensesUsed, MonthlyCost, AnnualCost, StartDate, RenewalDate, Notes) VALUES
(1, N'Microsoft 365 Business Premium', N'Microsoft',  N'Email, Office apps, Teams, SharePoint, OneDrive',  0, 50, 42, 1100.00, 13200.00, '2024-01-01', '2025-01-01', NULL),
(2, N'Adobe Creative Cloud',           N'Adobe',      N'Photoshop, Illustrator, InDesign, Premiere Pro',   0, 10,  8,  549.90,  6598.80, '2024-03-01', '2025-03-01', NULL),
(3, N'Zoom Business',                  N'Zoom',       N'Video conferencing and webinars',                  0, 30, 28,  549.70,  6596.40, '2024-06-01', '2025-06-01', NULL),
(4, N'Slack Business+',                N'Slack',      N'Team messaging and collaboration',                 0, 50, 45,  625.00,  7500.00, '2024-02-01', '2025-02-01', NULL),
(5, N'Jira Software',                  N'Atlassian',  N'Project management and issue tracking',            0, 25, 20,  187.50,  2250.00, '2024-04-01', '2025-04-01', NULL);

SET IDENTITY_INSERT dbo.Subscriptions OFF;
GO

-- ----- Tickets -----
SET IDENTITY_INSERT dbo.Tickets ON;

INSERT INTO dbo.Tickets (Id, Title, Description, Category, Status, Priority, CreatedDate, UpdatedDate, ResolvedDate, ClosedDate, ResolutionNotes, SubmittedById, AssignedToId, CompanyServiceId) VALUES
(1, N'Cannot connect to VPN',
    N'Unable to establish VPN connection from home. Getting timeout errors.',
    4, 0, 2, DATEADD(DAY, -2, GETUTCDATE()), NULL, NULL, NULL, NULL, 4, 2, 2),

(2, N'Laptop screen flickering',
    N'Dell laptop screen flickers intermittently, especially when on battery power.',
    1, 1, 1, DATEADD(DAY, -5, GETUTCDATE()), NULL, NULL, NULL, NULL, 5, 3, NULL),

(3, N'New employee onboarding - IT setup',
    N'New hire starting next Monday in Marketing. Need full IT setup: laptop, email, Teams, required software.',
    3, 0, 2, DATEADD(DAY, -1, GETUTCDATE()), NULL, NULL, NULL, NULL, 6, 3, NULL),

(4, N'Email not syncing on mobile',
    N'Outlook app on iPhone stopped syncing emails two days ago.',
    2, 3, 0, DATEADD(DAY, -7, GETUTCDATE()), DATEADD(DAY, -5, GETUTCDATE()), DATEADD(DAY, -5, GETUTCDATE()), NULL,
    N'Removed and re-added the email account. Syncing normally now.', 8, 3, 1),

(5, N'Request access to ERP system',
    N'Need access to the ERP system for financial reporting module.',
    0, 4, 1, DATEADD(DAY, -14, GETUTCDATE()), NULL, DATEADD(DAY, -12, GETUTCDATE()), DATEADD(DAY, -10, GETUTCDATE()),
    N'ERP access granted with Finance Reader role.', 5, 1, 3),

(6, N'Printer jamming frequently',
    N'1st floor printer keeps jamming. Happens multiple times per day.',
    1, 1, 1, DATEADD(DAY, -3, GETUTCDATE()), NULL, NULL, NULL, NULL, 7, 3, 4),

(7, N'Suspected phishing email received',
    N'Received suspicious email claiming to be from CEO asking for wire transfer.',
    5, 0, 3, GETUTCDATE(), NULL, NULL, NULL, NULL, 8, 1, NULL),

(8, N'Software installation request - Visual Studio',
    N'Need Visual Studio 2024 Enterprise installed for development work.',
    0, 0, 0, DATEADD(DAY, -1, GETUTCDATE()), NULL, NULL, NULL, NULL, 4, 2, NULL);

SET IDENTITY_INSERT dbo.Tickets OFF;
GO

-- =====================================================================
-- VERIFICATION QUERIES
-- Run these to confirm everything was created correctly
-- =====================================================================
SELECT 'Employees' AS TableName, COUNT(*) AS RowCount FROM dbo.Employees
UNION ALL
SELECT 'CompanyServices', COUNT(*) FROM dbo.CompanyServices
UNION ALL
SELECT 'Assets', COUNT(*) FROM dbo.Assets
UNION ALL
SELECT 'Subscriptions', COUNT(*) FROM dbo.Subscriptions
UNION ALL
SELECT 'Tickets', COUNT(*) FROM dbo.Tickets;
GO

PRINT '=============================================';
PRINT 'ServiceSphere database setup complete!';
PRINT '5 tables created, seed data inserted.';
PRINT '=============================================';
GO
