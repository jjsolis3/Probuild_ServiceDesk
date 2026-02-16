-- =====================================================================
-- ServiceSphere Database - Settings Tables (Run AFTER initial setup)
-- Target: Microsoft SQL Server 2016+
-- Database: ServiceSphere
-- =====================================================================
-- Run this script against the ServiceSphere database to add
-- the Settings infrastructure tables.
-- =====================================================================

USE [ServiceSphere];
GO

-- =====================================================================
-- TABLE: Roles
-- =====================================================================
IF OBJECT_ID('dbo.Roles', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Roles (
        Id              INT             IDENTITY(1,1) NOT NULL,
        Name            NVARCHAR(100)   NOT NULL,
        Description     NVARCHAR(500)   NULL,
        Permissions     NVARCHAR(2000)  NULL,
        IsSystem        BIT             NOT NULL DEFAULT 0,
        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT PK_Roles PRIMARY KEY (Id)
    );
END;
GO

-- =====================================================================
-- TABLE: Branches
-- =====================================================================
IF OBJECT_ID('dbo.Branches', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Branches (
        Id              INT             IDENTITY(1,1) NOT NULL,
        Name            NVARCHAR(200)   NOT NULL,
        Address         NVARCHAR(500)   NULL,
        City            NVARCHAR(100)   NULL,
        State           NVARCHAR(50)    NULL,
        ZipCode         NVARCHAR(20)    NULL,
        Phone           NVARCHAR(20)    NULL,
        SiteManagerId   INT             NULL,
        IsActive        BIT             NOT NULL DEFAULT 1,
        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT PK_Branches PRIMARY KEY (Id),
        CONSTRAINT FK_Branches_SiteManager FOREIGN KEY (SiteManagerId)
            REFERENCES dbo.Employees(Id) ON DELETE SET NULL
    );
END;
GO

-- =====================================================================
-- TABLE: TicketStates (Custom workflow states)
-- =====================================================================
IF OBJECT_ID('dbo.TicketStates', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TicketStates (
        Id              INT             IDENTITY(1,1) NOT NULL,
        Name            NVARCHAR(100)   NOT NULL,
        ColorHex        NVARCHAR(7)     NOT NULL DEFAULT '#6c757d',
        SortOrder       INT             NOT NULL DEFAULT 0,
        IsSlaEnabled    BIT             NOT NULL DEFAULT 0,
        IsDefault       BIT             NOT NULL DEFAULT 0,
        IsSystem        BIT             NOT NULL DEFAULT 0,
        IsActive        BIT             NOT NULL DEFAULT 1,

        CONSTRAINT PK_TicketStates PRIMARY KEY (Id)
    );
END;
GO

-- =====================================================================
-- TABLE: ResolutionCodes
-- =====================================================================
IF OBJECT_ID('dbo.ResolutionCodes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ResolutionCodes (
        Id              INT             IDENTITY(1,1) NOT NULL,
        Name            NVARCHAR(100)   NOT NULL,
        Description     NVARCHAR(500)   NULL,
        SortOrder       INT             NOT NULL DEFAULT 0,
        IsActive        BIT             NOT NULL DEFAULT 1,

        CONSTRAINT PK_ResolutionCodes PRIMARY KEY (Id)
    );
END;
GO

-- =====================================================================
-- TABLE: AppSettings (Key-value configuration)
-- =====================================================================
IF OBJECT_ID('dbo.AppSettings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AppSettings (
        Id              INT             IDENTITY(1,1) NOT NULL,
        [Key]           NVARCHAR(100)   NOT NULL,
        Value           NVARCHAR(1000)  NULL,
        Category        NVARCHAR(100)   NOT NULL DEFAULT 'General',
        Description     NVARCHAR(500)   NULL,

        CONSTRAINT PK_AppSettings PRIMARY KEY (Id)
    );

    CREATE UNIQUE INDEX IX_AppSettings_Key ON dbo.AppSettings ([Key]);
END;
GO

-- =====================================================================
-- TABLE: PortalUsers
-- =====================================================================
IF OBJECT_ID('dbo.PortalUsers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PortalUsers (
        Id              INT             IDENTITY(1,1) NOT NULL,
        Email           NVARCHAR(200)   NOT NULL,
        FirstName       NVARCHAR(100)   NOT NULL,
        LastName        NVARCHAR(100)   NOT NULL,
        PasswordHash    NVARCHAR(256)   NULL,
        IsActive        BIT             NOT NULL DEFAULT 1,
        LastLogin       DATETIME2       NULL,
        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
        EmployeeId      INT             NULL,
        RoleId          INT             NULL,

        CONSTRAINT PK_PortalUsers PRIMARY KEY (Id),
        CONSTRAINT FK_PortalUsers_Employee FOREIGN KEY (EmployeeId)
            REFERENCES dbo.Employees(Id) ON DELETE SET NULL,
        CONSTRAINT FK_PortalUsers_Role FOREIGN KEY (RoleId)
            REFERENCES dbo.Roles(Id) ON DELETE SET NULL
    );

    CREATE UNIQUE INDEX IX_PortalUsers_Email ON dbo.PortalUsers (Email);
END;
GO

-- =====================================================================
-- TABLE: UserGroups
-- =====================================================================
IF OBJECT_ID('dbo.UserGroups', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserGroups (
        Id              INT             IDENTITY(1,1) NOT NULL,
        Name            NVARCHAR(200)   NOT NULL,
        Description     NVARCHAR(500)   NULL,
        IsActive        BIT             NOT NULL DEFAULT 1,
        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT PK_UserGroups PRIMARY KEY (Id)
    );
END;
GO

-- =====================================================================
-- TABLE: UserGroupMembers (Many-to-Many)
-- =====================================================================
IF OBJECT_ID('dbo.UserGroupMembers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserGroupMembers (
        Id              INT             IDENTITY(1,1) NOT NULL,
        UserGroupId     INT             NOT NULL,
        PortalUserId    INT             NOT NULL,
        JoinedDate      DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT PK_UserGroupMembers PRIMARY KEY (Id),
        CONSTRAINT FK_UserGroupMembers_Group FOREIGN KEY (UserGroupId)
            REFERENCES dbo.UserGroups(Id) ON DELETE CASCADE,
        CONSTRAINT FK_UserGroupMembers_User FOREIGN KEY (PortalUserId)
            REFERENCES dbo.PortalUsers(Id) ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX IX_UserGroupMembers_Unique
        ON dbo.UserGroupMembers (UserGroupId, PortalUserId);
END;
GO

-- =====================================================================
-- TABLE: EmailConfigurations
-- =====================================================================
IF OBJECT_ID('dbo.EmailConfigurations', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmailConfigurations (
        Id                          INT             IDENTITY(1,1) NOT NULL,
        Name                        NVARCHAR(200)   NOT NULL,
        EmailAddress                NVARCHAR(200)   NOT NULL,
        ImapServer                  NVARCHAR(200)   NOT NULL DEFAULT 'imap.gmail.com',
        ImapPort                    INT             NOT NULL DEFAULT 993,
        SmtpServer                  NVARCHAR(200)   NOT NULL DEFAULT 'smtp.gmail.com',
        SmtpPort                    INT             NOT NULL DEFAULT 587,
        Username                    NVARCHAR(200)   NOT NULL,
        Password                    NVARCHAR(500)   NULL,
        UseSsl                      BIT             NOT NULL DEFAULT 1,
        IsActive                    BIT             NOT NULL DEFAULT 0,
        PollIntervalMinutes         INT             NOT NULL DEFAULT 5,
        CreateTicketsFromEmails     BIT             NOT NULL DEFAULT 1,
        DefaultTicketCategoryId     INT             NULL,
        DefaultAssigneeId           INT             NULL,
        LastPolledDate              DATETIME2       NULL,
        LastError                   NVARCHAR(2000)  NULL,

        CONSTRAINT PK_EmailConfigurations PRIMARY KEY (Id),
        CONSTRAINT FK_EmailConfigurations_Assignee FOREIGN KEY (DefaultAssigneeId)
            REFERENCES dbo.Employees(Id) ON DELETE SET NULL
    );
END;
GO


-- =====================================================================
-- SEED DATA
-- =====================================================================

-- ----- Roles -----
IF NOT EXISTS (SELECT 1 FROM dbo.Roles)
BEGIN
    SET IDENTITY_INSERT dbo.Roles ON;

    INSERT INTO dbo.Roles (Id, Name, Description, Permissions, IsSystem) VALUES
    (1, N'Administrator',  N'Full system access - can manage all settings, users, and data',
        N'ManageTickets,ManageAssets,ManageEmployees,ManageSubscriptions,ManageServices,ViewReports,ManageSettings,ManageUsers,ManageRoles', 1),
    (2, N'IT Manager',     N'Manage IT operations - tickets, assets, employees, and reports',
        N'ManageTickets,ManageAssets,ManageEmployees,ViewReports', 1),
    (3, N'Help Desk Agent', N'Handle ticket creation, assignment, and resolution',
        N'ManageTickets,ViewReports', 1),
    (4, N'Employee',       N'Submit and view own tickets through the portal',
        N'SubmitTickets,ViewOwnTickets', 1),
    (5, N'Viewer',         N'Read-only access to view tickets and reports',
        N'ViewTickets,ViewReports', 0);

    SET IDENTITY_INSERT dbo.Roles OFF;
END;
GO

-- ----- Branches -----
IF NOT EXISTS (SELECT 1 FROM dbo.Branches)
BEGIN
    SET IDENTITY_INSERT dbo.Branches ON;

    INSERT INTO dbo.Branches (Id, Name, Address, City, State, ZipCode, Phone, SiteManagerId, IsActive) VALUES
    (1, N'Main Office - Headquarters', N'1000 Corporate Blvd', N'Dallas', N'TX', N'75201', N'214-555-0100', 1, 1),
    (2, N'East Regional Office',       N'500 Commerce St',     N'Atlanta', N'GA', N'30301', N'404-555-0200', 7, 1),
    (3, N'West Coast Office',          N'200 Innovation Way',  N'San Jose', N'CA', N'95110', N'408-555-0300', NULL, 1);

    SET IDENTITY_INSERT dbo.Branches OFF;
END;
GO

-- ----- Ticket States -----
IF NOT EXISTS (SELECT 1 FROM dbo.TicketStates)
BEGIN
    SET IDENTITY_INSERT dbo.TicketStates ON;

    INSERT INTO dbo.TicketStates (Id, Name, ColorHex, SortOrder, IsSlaEnabled, IsDefault, IsSystem, IsActive) VALUES
    (1,  N'New',                   N'#0d6efd', 1,  1, 1, 1, 1),
    (2,  N'Pending Assignment',    N'#6610f2', 2,  1, 0, 0, 1),
    (3,  N'Assigned',              N'#6f42c1', 3,  1, 0, 0, 1),
    (4,  N'In Progress',           N'#ffc107', 4,  1, 0, 0, 1),
    (5,  N'Awaiting Input',        N'#fd7e14', 5,  0, 0, 0, 1),
    (6,  N'Awaiting Development',  N'#20c997', 6,  0, 0, 0, 1),
    (7,  N'On Hold',               N'#6c757d', 7,  0, 0, 0, 1),
    (8,  N'Resolved',              N'#198754', 8,  0, 0, 1, 1),
    (9,  N'Closed',                N'#495057', 9,  0, 0, 1, 1),
    (10, N'Cancelled',             N'#dc3545', 10, 0, 0, 1, 1);

    SET IDENTITY_INSERT dbo.TicketStates OFF;
END;
GO

-- ----- Resolution Codes -----
IF NOT EXISTS (SELECT 1 FROM dbo.ResolutionCodes)
BEGIN
    SET IDENTITY_INSERT dbo.ResolutionCodes ON;

    INSERT INTO dbo.ResolutionCodes (Id, Name, Description, SortOrder, IsActive) VALUES
    (1,  N'Fixed',                   N'Issue has been fixed or repaired',                1,  1),
    (2,  N'Resolved - Workaround',   N'Issue resolved with a temporary workaround',      2,  1),
    (3,  N'User Education',          N'Resolved through user training or guidance',       3,  1),
    (4,  N'Configuration Change',    N'Resolved by changing system configuration',        4,  1),
    (5,  N'Hardware Replacement',    N'Resolved by replacing faulty hardware',            5,  1),
    (6,  N'Software Update',        N'Resolved by updating or patching software',        6,  1),
    (7,  N'Access Granted',         N'Resolved by granting requested access/permissions', 7,  1),
    (8,  N'Duplicate',              N'Closed as duplicate of another ticket',             8,  1),
    (9,  N'Cannot Reproduce',       N'Unable to reproduce the reported issue',            9,  1),
    (10, N'Not a Bug',              N'Working as designed / not an actual issue',         10, 1),
    (11, N'Third Party',            N'Resolved by a third-party vendor',                 11, 1),
    (12, N'No Response',            N'Closed due to no response from requester',         12, 1);

    SET IDENTITY_INSERT dbo.ResolutionCodes OFF;
END;
GO

-- ----- App Settings -----
IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings)
BEGIN
    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description) VALUES
    (N'Timezone',           N'America/Chicago',         N'Account',  N'Default timezone for the application'),
    (N'DefaultAssigneeId',  N'1',                       N'Account',  N'Default employee ID for new ticket assignment'),
    (N'CompanyName',        N'Seamless Flooring Corp',  N'Account',  N'Company name displayed in the application'),
    (N'TicketPrefix',       N'SFC',                     N'Account',  N'Prefix for ticket reference numbers (e.g., SFC-001)'),
    (N'AutoAssign',         N'false',                   N'Account',  N'Automatically assign tickets to default assignee'),
    (N'SlaWarningHours',    N'2',                       N'SLA',      N'Hours before SLA breach to show warning'),
    (N'EmailNotifications', N'true',                    N'Email',    N'Enable email notifications for ticket updates'),
    (N'PortalEnabled',      N'true',                    N'Portal',   N'Enable the customer/employee portal');
END;
GO

-- ----- Portal Users (seed from existing employees) -----
IF NOT EXISTS (SELECT 1 FROM dbo.PortalUsers)
BEGIN
    SET IDENTITY_INSERT dbo.PortalUsers ON;

    INSERT INTO dbo.PortalUsers (Id, Email, FirstName, LastName, IsActive, EmployeeId, RoleId) VALUES
    (1, N'john.smith@probuild.com',       N'John',     N'Smith',    1, 1, 1),
    (2, N'sarah.johnson@probuild.com',    N'Sarah',    N'Johnson',  1, 2, 2),
    (3, N'mike.davis@probuild.com',       N'Mike',     N'Davis',    1, 3, 3);

    SET IDENTITY_INSERT dbo.PortalUsers OFF;
END;
GO

-- ----- User Groups -----
IF NOT EXISTS (SELECT 1 FROM dbo.UserGroups)
BEGIN
    SET IDENTITY_INSERT dbo.UserGroups ON;

    INSERT INTO dbo.UserGroups (Id, Name, Description, IsActive) VALUES
    (1, N'IT Department',     N'All IT staff - receives ticket notifications and assignments', 1),
    (2, N'Help Desk Team',    N'Front-line support agents',                                    1),
    (3, N'Management',        N'Department managers with reporting access',                    1);

    SET IDENTITY_INSERT dbo.UserGroups OFF;
END;
GO

-- ----- User Group Members -----
IF NOT EXISTS (SELECT 1 FROM dbo.UserGroupMembers)
BEGIN
    INSERT INTO dbo.UserGroupMembers (UserGroupId, PortalUserId) VALUES
    (1, 1),  -- John -> IT Department
    (1, 2),  -- Sarah -> IT Department
    (1, 3),  -- Mike -> IT Department
    (2, 3),  -- Mike -> Help Desk Team
    (3, 1);  -- John -> Management
END;
GO

-- ----- Email Configuration (pre-filled for Google Workspace) -----
IF NOT EXISTS (SELECT 1 FROM dbo.EmailConfigurations)
BEGIN
    INSERT INTO dbo.EmailConfigurations
        (Name, EmailAddress, ImapServer, ImapPort, SmtpServer, SmtpPort,
         Username, Password, UseSsl, IsActive, PollIntervalMinutes,
         CreateTicketsFromEmails, DefaultAssigneeId)
    VALUES
        (N'IT Support Inbox',
         N'itsupport@seamlessflooring.com',
         N'imap.gmail.com', 993,
         N'smtp.gmail.com', 587,
         N'itsupport@seamlessflooring.com',
         NULL,  -- Password must be set through the UI (Google App Password)
         1, 0, 5, 1, 1);
END;
GO


-- =====================================================================
-- VERIFICATION
-- =====================================================================
SELECT 'Roles' AS TableName, COUNT(*) AS RowCount FROM dbo.Roles
UNION ALL SELECT 'Branches', COUNT(*) FROM dbo.Branches
UNION ALL SELECT 'TicketStates', COUNT(*) FROM dbo.TicketStates
UNION ALL SELECT 'ResolutionCodes', COUNT(*) FROM dbo.ResolutionCodes
UNION ALL SELECT 'AppSettings', COUNT(*) FROM dbo.AppSettings
UNION ALL SELECT 'PortalUsers', COUNT(*) FROM dbo.PortalUsers
UNION ALL SELECT 'UserGroups', COUNT(*) FROM dbo.UserGroups
UNION ALL SELECT 'UserGroupMembers', COUNT(*) FROM dbo.UserGroupMembers
UNION ALL SELECT 'EmailConfigurations', COUNT(*) FROM dbo.EmailConfigurations;
GO

PRINT '=============================================';
PRINT 'Settings tables created and seeded!';
PRINT '9 new tables added to ServiceSphere.';
PRINT '=============================================';
GO
