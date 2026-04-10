using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Enums;
using ServiceDesk.Core.Models;
using ServiceDesk.Core.Services;

namespace ServiceDesk.Infrastructure.Data;

public static class DbInitializer
{
    /// <summary>
    /// Applies any pending schema changes that aren't covered by EF migrations.
    /// Each ALTER TABLE / CREATE TABLE is guarded by an existence check so it
    /// is safe to run on every startup.
    /// </summary>
    public static void ApplySchemaUpgrades(ServiceDeskDbContext context)
    {
        try
        {
            // 1. Add BranchId to Employees (routing upgrade)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'BranchId'
                )
                BEGIN
                    ALTER TABLE dbo.Employees
                        ADD BranchId INT NULL
                        CONSTRAINT FK_Employees_Branches
                        REFERENCES dbo.Branches(Id)
                        ON DELETE SET NULL;
                END");

            // 2. Add BranchId to Tickets (routing upgrade)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'BranchId'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets
                        ADD BranchId INT NULL
                        CONSTRAINT FK_Tickets_Branches
                        REFERENCES dbo.Branches(Id)
                        ON DELETE SET NULL;
                END");

            // 3. Create AssignmentRules table (routing upgrade)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssignmentRules')
                BEGIN
                    CREATE TABLE dbo.AssignmentRules (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Name            NVARCHAR(200)   NOT NULL,
                        Category        INT             NULL,
                        SubCategoryId   INT             NULL
                            CONSTRAINT FK_AssignmentRules_SubCategories
                            REFERENCES dbo.TicketSubCategories(Id)
                            ON DELETE SET NULL,
                        BranchId        INT             NULL
                            CONSTRAINT FK_AssignmentRules_Branches
                            REFERENCES dbo.Branches(Id)
                            ON DELETE SET NULL,
                        AssigneeId      INT             NOT NULL
                            CONSTRAINT FK_AssignmentRules_Employees
                            REFERENCES dbo.Employees(Id),
                        SortOrder       INT             NOT NULL DEFAULT 100,
                        IsActive        BIT             NOT NULL DEFAULT 1,
                        CreatedDate     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_AssignmentRules_Active_Sort
                        ON dbo.AssignmentRules (IsActive, SortOrder);
                END
                ELSE
                BEGIN
                    -- Upgrade: add SubCategoryId if table already exists
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.AssignmentRules') AND name = 'SubCategoryId'
                    )
                    BEGIN
                        ALTER TABLE dbo.AssignmentRules
                            ADD SubCategoryId INT NULL
                            CONSTRAINT FK_AssignmentRules_SubCategories
                            REFERENCES dbo.TicketSubCategories(Id)
                            ON DELETE SET NULL;
                    END
                END");

            // 4. Add DueDate to Tickets (SLA upgrade)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'DueDate'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets ADD DueDate DATETIME2 NULL;
                END");

            // 5. Create TicketAttachments table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketAttachments')
                BEGIN
                    CREATE TABLE dbo.TicketAttachments (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId        INT             NOT NULL
                            CONSTRAINT FK_TicketAttachments_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE CASCADE,
                        FileName        NVARCHAR(255)   NOT NULL,
                        StoredFileName  NVARCHAR(255)   NOT NULL,
                        ContentType     NVARCHAR(100)   NOT NULL DEFAULT '',
                        FileSize        BIGINT          NOT NULL DEFAULT 0,
                        UploadedBy      NVARCHAR(200)   NOT NULL DEFAULT '',
                        UploadedDate    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                END");

            // 6. Create TicketHistory table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketHistory')
                BEGIN
                    CREATE TABLE dbo.TicketHistory (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId    INT             NOT NULL
                            CONSTRAINT FK_TicketHistory_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE CASCADE,
                        ChangedBy   NVARCHAR(200)   NOT NULL,
                        FieldName   NVARCHAR(100)   NOT NULL,
                        OldValue    NVARCHAR(500)   NULL,
                        NewValue    NVARCHAR(500)   NULL,
                        ChangedDate DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_TicketHistory_Ticket_Date
                        ON dbo.TicketHistory (TicketId, ChangedDate);
                END");

            // 7. Add SmtpUsername / SmtpPassword to EmailConfigurations (SMTP App Password support)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations') AND name = 'SmtpUsername'
                )
                BEGIN
                    ALTER TABLE dbo.EmailConfigurations ADD SmtpUsername NVARCHAR(200) NULL;
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.EmailConfigurations') AND name = 'SmtpPassword'
                )
                BEGIN
                    ALTER TABLE dbo.EmailConfigurations ADD SmtpPassword NVARCHAR(500) NULL;
                END");

            // 8. Create KbArticles table (Knowledge Base)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'KbArticles')
                BEGIN
                    CREATE TABLE dbo.KbArticles (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Title           NVARCHAR(300)   NOT NULL,
                        Problem         NVARCHAR(4000)  NOT NULL,
                        Solution        NVARCHAR(MAX)   NOT NULL,
                        Category        INT             NOT NULL DEFAULT 0,
                        SourceTicketId  INT             NULL
                            CONSTRAINT FK_KbArticles_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE SET NULL,
                        IsPublished     BIT             NOT NULL DEFAULT 1,
                        CreatedBy       NVARCHAR(200)   NOT NULL DEFAULT '',
                        CreatedDate     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                        LastUpdated     DATETIME2       NULL,
                        ViewCount       INT             NOT NULL DEFAULT 0
                    );

                    CREATE INDEX IX_KbArticles_Published_Category
                        ON dbo.KbArticles (IsPublished, Category);
                END");

            // 9. Add PasswordResetToken columns to PortalUsers (Forgot Password support)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.PortalUsers') AND name = 'PasswordResetToken'
                )
                BEGIN
                    ALTER TABLE dbo.PortalUsers
                        ADD PasswordResetToken NVARCHAR(200) NULL,
                            PasswordResetTokenExpiry DATETIME2 NULL;
                END");

            // 10. Create CannedResponses table (agent reply templates)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CannedResponses')
                BEGIN
                    CREATE TABLE dbo.CannedResponses (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Title       NVARCHAR(200)   NOT NULL,
                        Content     NVARCHAR(4000)  NOT NULL,
                        Category    NVARCHAR(100)   NULL,
                        SortOrder   INT             NOT NULL DEFAULT 0,
                        IsActive    BIT             NOT NULL DEFAULT 1
                    );

                    INSERT INTO dbo.CannedResponses (Title, Content, Category, SortOrder) VALUES
                    (N'Ticket Received',
                     N'Thank you for contacting IT Support. We have received your request and it is being reviewed by our team. We will update you shortly.',
                     N'General', 10),
                    (N'Requesting More Information',
                     N'To better assist you, could you please provide the following information:' + CHAR(13)+CHAR(10) +
                     N'- A description of the issue including any error messages' + CHAR(13)+CHAR(10) +
                     N'- The device name or asset tag affected' + CHAR(13)+CHAR(10) +
                     N'- When the issue first started' + CHAR(13)+CHAR(10) +
                     N'Thank you for your assistance.',
                     N'General', 20),
                    (N'Password Reset Instructions',
                     N'To reset your password please follow these steps:' + CHAR(13)+CHAR(10) +
                     N'1. Go to the login page and click Forgot Password' + CHAR(13)+CHAR(10) +
                     N'2. Enter your company email address' + CHAR(13)+CHAR(10) +
                     N'3. Check your email for the reset link (check spam if not received)' + CHAR(13)+CHAR(10) +
                     N'4. Follow the link to set a new password' + CHAR(13)+CHAR(10) +
                     N'Contact us if you need further assistance.',
                     N'Account', 30),
                    (N'Issue Resolved - Please Confirm',
                     N'We believe the issue described in this ticket has been resolved. Could you please confirm that everything is working correctly on your end? If the issue persists, please reply and we will continue to assist you.',
                     N'Resolution', 40),
                    (N'Remote Session Request',
                     N'To resolve this issue efficiently, I would like to connect to your device remotely. Please let me know a convenient time or if you are available now we can proceed immediately.',
                     N'Support', 50),
                    (N'Ticket Closed - No Response',
                     N'We have not received a response to our previous message. We will be closing this ticket as resolved. Please do not hesitate to open a new ticket if you continue to experience issues.',
                     N'Resolution', 60);
                END");

            // 12. Create TicketSubCategories table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketSubCategories')
                BEGIN
                    CREATE TABLE dbo.TicketSubCategories (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Category    INT             NOT NULL,
                        Name        NVARCHAR(100)   NOT NULL,
                        SortOrder   INT             NOT NULL DEFAULT 0,
                        IsActive    BIT             NOT NULL DEFAULT 1
                    );

                    -- Seed default sub-categories
                    INSERT INTO dbo.TicketSubCategories (Category, Name, SortOrder) VALUES
                    (0, N'New Software Request', 10),   -- ServiceRequest
                    (0, N'Hardware Procurement', 20),
                    (0, N'Access / Permissions', 30),
                    (0, N'New User Onboarding', 40),
                    (1, N'Laptop / Desktop', 10),       -- HardwareIssue
                    (1, N'Printer / Scanner', 20),
                    (1, N'Monitor / Display', 30),
                    (1, N'Peripheral Devices', 40),
                    (2, N'Application Error', 10),      -- SoftwareIssue
                    (2, N'OS / Windows Issue', 20),
                    (2, N'Microsoft 365', 30),
                    (2, N'Antivirus / Security Tool', 40),
                    (3, N'Performance Issue', 10),      -- EmployeeIssue
                    (3, N'Login / Authentication', 20),
                    (3, N'Email Problem', 30),
                    (4, N'No Internet / Slow Connection', 10), -- NetworkIssue
                    (4, N'VPN / Remote Access', 20),
                    (4, N'Wi-Fi Issue', 30),
                    (4, N'Network Drive / Share', 40),
                    (5, N'Suspicious Email / Phishing', 10),   -- SecurityIncident
                    (5, N'Unauthorised Access', 20),
                    (5, N'Data Breach', 30),
                    (6, N'Other / General Inquiry', 10);        -- Other
                END");

            // 13. Add SubCategoryId to Tickets table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'SubCategoryId'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets
                        ADD SubCategoryId INT NULL
                        CONSTRAINT FK_Tickets_SubCategories
                        REFERENCES dbo.TicketSubCategories(Id)
                        ON DELETE SET NULL;
                END");

            // 14. Add DescriptionHtml column to Tickets (rich HTML from email)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'DescriptionHtml'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets ADD DescriptionHtml NVARCHAR(MAX) NULL;
                END");

            // 14b. Add UserGroupId column to Tickets (group assignment)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'UserGroupId'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets
                        ADD UserGroupId INT NULL
                        CONSTRAINT FK_Tickets_UserGroups
                        REFERENCES dbo.UserGroups(Id)
                        ON DELETE SET NULL;
                END");

            // 15. Create SavedTicketViews table (user filter presets)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SavedTicketViews')
                BEGIN
                    CREATE TABLE dbo.SavedTicketViews (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Name                NVARCHAR(100)   NOT NULL,
                        OwnerPortalUserId   INT             NULL
                            CONSTRAINT FK_SavedTicketViews_PortalUsers
                            REFERENCES dbo.PortalUsers(Id)
                            ON DELETE CASCADE,
                        IsDefault           BIT             NOT NULL DEFAULT 0,
                        IsShared            BIT             NOT NULL DEFAULT 0,
                        FilterStatuses      NVARCHAR(200)   NULL,
                        FilterStatus        INT             NULL,
                        FilterCategory      INT             NULL,
                        FilterPriority      INT             NULL,
                        FilterBranchId      INT             NULL
                            CONSTRAINT FK_SavedTicketViews_Branches
                            REFERENCES dbo.Branches(Id)
                            ON DELETE SET NULL,
                        FilterDepartment    NVARCHAR(100)   NULL,
                        FilterGroupId       INT             NULL
                            CONSTRAINT FK_SavedTicketViews_UserGroups
                            REFERENCES dbo.UserGroups(Id)
                            ON DELETE SET NULL,
                        FilterAssignedToMe  BIT             NOT NULL DEFAULT 0,
                        FilterUnassignedOnly BIT            NOT NULL DEFAULT 0,
                        FilterUnmatchedOnly BIT             NOT NULL DEFAULT 0,
                        SortBy              NVARCHAR(20)    NOT NULL DEFAULT 'id',
                        SortDir             NVARCHAR(4)     NOT NULL DEFAULT 'desc',
                        PageSize            INT             NOT NULL DEFAULT 25,
                        CreatedDate         DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_SavedTicketViews_Owner_Shared
                        ON dbo.SavedTicketViews (OwnerPortalUserId, IsShared);
                END
                ELSE
                BEGIN
                    -- Upgrade: add FilterStatuses column if table already exists
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterStatuses'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterStatuses NVARCHAR(200) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterCategories'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterCategories NVARCHAR(300) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterPriorities'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterPriorities NVARCHAR(200) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterBranchIds'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterBranchIds NVARCHAR(200) NULL;
                    END

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.SavedTicketViews') AND name = 'FilterDepartments'
                    )
                    BEGIN
                        ALTER TABLE dbo.SavedTicketViews ADD FilterDepartments NVARCHAR(500) NULL;
                    END
                END");

            // Seed the system-level default view (active/non-resolved tickets).
            // OwnerPortalUserId = NULL means system-owned; applies to every user
            // who has not set their own personal default.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM dbo.SavedTicketViews
                    WHERE OwnerPortalUserId IS NULL AND IsDefault = 1
                )
                BEGIN
                    INSERT INTO dbo.SavedTicketViews
                        (Name, OwnerPortalUserId, IsDefault, IsShared,
                         FilterStatuses, SortBy, SortDir, PageSize, CreatedDate)
                    VALUES
                        (N'Active Tickets (Default)', NULL, 1, 1,
                         N'Open,InProgress,OnHold', 'id', 'desc', 25, SYSUTCDATETIME());
                END");

            // 16b. Add Extension column to Employees
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'Extension'
                )
                BEGIN
                    ALTER TABLE dbo.Employees ADD Extension NVARCHAR(10) NULL;
                END");

            // 17. Create CategoryKeywords table (DB-backed keyword detection for auto-categorisation)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CategoryKeywords')
                BEGIN
                    CREATE TABLE dbo.CategoryKeywords (
                        Id          INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Category    INT             NOT NULL,
                        Keyword     NVARCHAR(100)   NOT NULL,
                        IsActive    BIT             NOT NULL DEFAULT 1,
                        CreatedDate DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_CategoryKeywords_Category_Active
                        ON dbo.CategoryKeywords (Category, IsActive);

                    -- Seed default keywords (mirrors AssignmentResolverService hardcoded dictionary)
                    -- HardwareIssue = 1
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (1, N'printer'), (1, N'printing'), (1, N'keyboard'), (1, N'mouse'),
                    (1, N'monitor'), (1, N'screen'), (1, N'display'), (1, N'laptop'),
                    (1, N'desktop'), (1, N'computer'), (1, N'pc'), (1, N'hardware'),
                    (1, N'device'), (1, N'battery'), (1, N'charger'), (1, N'dock'),
                    (1, N'docking'), (1, N'headset'), (1, N'webcam'), (1, N'scanner'),
                    (1, N'projector'), (1, N'broken'), (1, N'damaged'), (1, N'physical'),
                    (1, N'power'), (1, N'overheating'), (1, N'fan noise');

                    -- SoftwareIssue = 2
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (2, N'software'), (2, N'application'), (2, N'app'), (2, N'program'),
                    (2, N'install'), (2, N'installation'), (2, N'uninstall'), (2, N'update'),
                    (2, N'upgrade'), (2, N'crash'), (2, N'crashes'), (2, N'error'),
                    (2, N'errors'), (2, N'bug'), (2, N'license'), (2, N'activation'),
                    (2, N'office'), (2, N'word'), (2, N'excel'), (2, N'outlook'),
                    (2, N'teams'), (2, N'zoom'), (2, N'adobe'), (2, N'browser'),
                    (2, N'chrome'), (2, N'firefox'), (2, N'edge'), (2, N'slow'),
                    (2, N'freezing'), (2, N'frozen'), (2, N'not responding'),
                    (2, N'blue screen'), (2, N'bsod'), (2, N'driver'),
                    (2, N'operating system'), (2, N'windows'), (2, N'macos'), (2, N'patch');

                    -- NetworkIssue = 4
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (4, N'vpn'), (4, N'network'), (4, N'internet'), (4, N'wifi'),
                    (4, N'wi-fi'), (4, N'wireless'), (4, N'ethernet'), (4, N'connection'),
                    (4, N'connectivity'), (4, N'firewall'), (4, N'dns'), (4, N'dhcp'),
                    (4, N'ip address'), (4, N'bandwidth'), (4, N'slow internet'),
                    (4, N'no internet'), (4, N'network drive'), (4, N'mapped drive'),
                    (4, N'remote access'), (4, N'remote desktop'), (4, N'rdp'),
                    (4, N'switch'), (4, N'router'), (4, N'cable');

                    -- SecurityIncident = 5
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (5, N'security'), (5, N'phishing'), (5, N'phish'), (5, N'suspicious'),
                    (5, N'hack'), (5, N'hacked'), (5, N'virus'), (5, N'malware'),
                    (5, N'ransomware'), (5, N'spyware'), (5, N'trojan'), (5, N'spam'),
                    (5, N'unauthorized'), (5, N'breach'), (5, N'password reset'),
                    (5, N'account locked'), (5, N'compromised'), (5, N'scam'),
                    (5, N'fraud'), (5, N'social engineering'), (5, N'2fa'), (5, N'mfa');

                    -- EmployeeIssue = 3
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (3, N'onboarding'), (3, N'new employee'), (3, N'new hire'),
                    (3, N'offboarding'), (3, N'termination'), (3, N'terminated'),
                    (3, N'access request'), (3, N'new user'), (3, N'user setup'),
                    (3, N'account setup'), (3, N'leave'), (3, N'absence'),
                    (3, N'transfer'), (3, N'promotion'), (3, N'department change'),
                    (3, N'badge'), (3, N'id card'), (3, N'equipment request'),
                    (3, N'role change');

                    -- ServiceRequest = 0
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (0, N'request'), (0, N'order'), (0, N'setup'), (0, N'configure'),
                    (0, N'configuration'), (0, N'provision'), (0, N'provisioning'),
                    (0, N'access'), (0, N'permission'), (0, N'grant'),
                    (0, N'create account'), (0, N'new account'), (0, N'service'),
                    (0, N'question'), (0, N'help'), (0, N'how to'), (0, N'assistance');
                END");

            // 18. Seed additional Company Branding AppSettings keys if not present
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CompanyLogoUrl')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CompanyLogoUrl', '', 'Branding', 'URL to your company logo (shown in the portal). Can be an external URL or a path like /images/company-logo.png');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CompanyPhone')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CompanyPhone', '', 'Branding', 'Company phone number displayed in portal footer');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CompanyWebsite')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CompanyWebsite', '', 'Branding', 'Company website URL displayed in portal footer');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CompanyAddress')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CompanyAddress', '', 'Branding', 'Company mailing address displayed in portal footer');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'BrandColor')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('BrandColor', '#4f46e5', 'Branding', 'Primary brand colour used in email headers and PDF exports (hex format, e.g. #4f46e5)');");

            // 19. Create EmailTemplates table (DB-backed email template management)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmailTemplates')
                BEGIN
                    CREATE TABLE dbo.EmailTemplates (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        [Key]           NVARCHAR(50)    NOT NULL,
                        Name            NVARCHAR(100)   NOT NULL,
                        Description     NVARCHAR(500)   NULL,
                        SubjectTemplate NVARCHAR(300)   NULL,
                        BodyTemplate    NVARCHAR(MAX)   NULL,
                        IsActive        BIT             NOT NULL DEFAULT 1,
                        UpdatedDate     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE UNIQUE INDEX IX_EmailTemplates_Key ON dbo.EmailTemplates ([Key]);

                    -- Seed default template metadata (BodyTemplate left NULL — system defaults used until admin customises)
                    INSERT INTO dbo.EmailTemplates ([Key], Name, Description, SubjectTemplate) VALUES
                    (N'TicketCreated',
                     N'Ticket Created Confirmation',
                     N'Sent to the submitter when a new ticket is created. Confirms receipt and provides the ticket reference.',
                     N'[#SS-{{TicketId}}] {{TicketTitle}}'),
                    (N'TicketAssigned',
                     N'Ticket Assigned — Agent Notification',
                     N'Sent to the IT agent when a ticket is assigned to them.',
                     N'Assigned: [#SS-{{TicketId}}] {{TicketTitle}}'),
                    (N'TicketUpdated',
                     N'Ticket Status Update',
                     N'Sent to the submitter when the ticket status or priority changes.',
                     N'Updated: [#SS-{{TicketId}}] {{TicketTitle}}'),
                    (N'NoteAdded',
                     N'New Comment / Note',
                     N'Sent to the submitter when an IT agent adds a public note or comment to the ticket.',
                     N'Re: [#SS-{{TicketId}}] {{TicketTitle}}'),
                    (N'PasswordReset',
                     N'Password Reset Request',
                     N'Sent to a portal user when they request a password reset link.',
                     N'Reset Your {{CompanyName}} Password');
                END");
        }
        catch (Exception ex)
        {
            // Log and continue — the app can still start even if upgrades fail
            // (tables may not exist yet on a fresh install)
            Console.WriteLine($"[DbInitializer] Schema upgrade warning: {ex.Message}");
        }
    }

    public static void Seed(ServiceDeskDbContext context)
    {
        try
        {
            // Only seeds system roles and the default admin login.
            // Sample data (employees, tickets, assets, etc.) is managed via the CSV import.
            SeedRolesAndAdminUser(context);
        }
        catch (Exception ex)
        {
            // Tables may not exist yet — skip seeding.
            // Run the Database/ServiceSphere_CreateTables.sql script on SQL Server first.
            Console.WriteLine($"[DbInitializer] Seed warning: {ex.Message}");
        }
    }

    private static void SeedRolesAndAdminUser(ServiceDeskDbContext context)
    {
        // Seed system roles if not present
        var roleNames = new[]
        {
            ("Admin",    "Full system access — manage tickets, assets, settings, users.",
                         "ManageTickets,ManageAssets,ManageEmployees,ManageSubscriptions,ManageServices,ViewReports,ManageSettings,ManageUsers", true),
            ("IT Agent", "IT staff — manage tickets, assets, employees, subscriptions, services, view reports.",
                         "ManageTickets,ManageAssets,ManageEmployees,ManageSubscriptions,ManageServices,ViewReports", true),
            ("End User", "Non-IT employee — portal access only (submit and view own tickets).",
                         "Portal", true),
            ("Viewer",   "Read-only access to the web app (dashboard, tickets, assets, subscriptions, services, reports). Can also use the portal for own tickets.",
                         "ViewReports,ViewDashboard,ViewTickets,ViewAssets,ViewSubscriptions,ViewServices", true),
        };

        foreach (var (name, desc, perms, isSystem) in roleNames)
        {
            if (!context.Roles.Any(r => r.Name == name))
            {
                context.Roles.Add(new Role
                {
                    Name = name,
                    Description = desc,
                    Permissions = perms,
                    IsSystem = isSystem,
                    CreatedDate = DateTime.UtcNow
                });
            }
        }
        context.SaveChanges();

        // Seed admin portal user
        if (!context.PortalUsers.Any(u => u.Email == "admin@servicedeskpro.com"))
        {
            var adminRole = context.Roles.First(r => r.Name == "Admin");
            context.PortalUsers.Add(new PortalUser
            {
                Email = "admin@servicedeskpro.com",
                FirstName = "System",
                LastName = "Admin",
                PasswordHash = PasswordService.HashPassword("Admin123!"),
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                RoleId = adminRole.Id
            });
        }

        // Seed IT Agent portal user linked to John Smith (Id=1 from seed)
        if (!context.PortalUsers.Any(u => u.Email == "john.smith@probuild.com"))
        {
            var agentRole = context.Roles.First(r => r.Name == "IT Agent");
            var emp = context.Employees.FirstOrDefault(e => e.Email == "john.smith@probuild.com");
            context.PortalUsers.Add(new PortalUser
            {
                Email = "john.smith@probuild.com",
                FirstName = "John",
                LastName = "Smith",
                PasswordHash = PasswordService.HashPassword("Agent123!"),
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                RoleId = agentRole.Id,
                EmployeeId = emp?.Id
            });
        }

        // Seed End User portal user linked to Emily Wilson (non-IT)
        if (!context.PortalUsers.Any(u => u.Email == "emily.wilson@probuild.com"))
        {
            var userRole = context.Roles.First(r => r.Name == "End User");
            var emp = context.Employees.FirstOrDefault(e => e.Email == "emily.wilson@probuild.com");
            context.PortalUsers.Add(new PortalUser
            {
                Email = "emily.wilson@probuild.com",
                FirstName = "Emily",
                LastName = "Wilson",
                PasswordHash = PasswordService.HashPassword("User123!"),
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                RoleId = userRole.Id,
                EmployeeId = emp?.Id
            });
        }

        context.SaveChanges();
    }
}
