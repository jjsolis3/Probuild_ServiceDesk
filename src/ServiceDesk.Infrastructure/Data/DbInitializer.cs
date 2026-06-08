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

                    IF NOT EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id = OBJECT_ID('dbo.TicketNotes') AND name = 'ContentHtml'
                    )
                    BEGIN
                        ALTER TABLE dbo.TicketNotes ADD ContentHtml NVARCHAR(MAX) NULL;
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
                    VALUES ('BrandColor', '#4f46e5', 'Branding', 'Primary brand colour used in email headers and PDF exports (hex format, e.g. #4f46e5)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'EmailHeaderTagline')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('EmailHeaderTagline', 'IT Service Desk', 'Branding', 'Tagline shown below the company name in the email header banner (e.g. ''IT Service Desk'', ''Support Team'')');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'EmailShowLogo')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('EmailShowLogo', 'true', 'Branding', 'Show the company logo image in outgoing email headers (requires Company Logo URL to be set)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'EmailFooterText')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('EmailFooterText', '', 'Branding', 'Custom footer text for all outgoing emails. Leave blank to use the default automated-notification message.');");

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
            // 20. Create AiRecommendations table (AI triage suggestions per ticket)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AiRecommendations')
                BEGIN
                    CREATE TABLE dbo.AiRecommendations (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId            INT             NOT NULL
                            CONSTRAINT FK_AiRecommendations_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE CASCADE,
                        SuggestedCategory   INT             NULL,
                        SuggestedPriority   INT             NULL,
                        SuggestedAssigneeId INT             NULL
                            CONSTRAINT FK_AiRecommendations_Employees
                            REFERENCES dbo.Employees(Id)
                            ON DELETE SET NULL,
                        CategoryConfidence  REAL            NOT NULL DEFAULT 0,
                        PriorityConfidence  REAL            NOT NULL DEFAULT 0,
                        AiSummary           NVARCHAR(2000)  NULL,
                        AiDraftReply        NVARCHAR(MAX)   NULL,
                        Status              NVARCHAR(20)    NOT NULL DEFAULT 'Pending',
                        CreatedDate         DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                        ReviewedDate        DATETIME2       NULL,
                        ReviewedBy          NVARCHAR(200)   NULL
                    );

                    CREATE INDEX IX_AiRecommendations_Ticket_Status
                        ON dbo.AiRecommendations (TicketId, Status);
                END");

            // 21. Create AiRunLogs table (audit log for ML.NET training / prediction runs)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AiRunLogs')
                BEGIN
                    CREATE TABLE dbo.AiRunLogs (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        RunDate             DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                        RunType             NVARCHAR(20)    NOT NULL DEFAULT 'Training',
                        TrainingTicketCount INT             NOT NULL DEFAULT 0,
                        ModelVersion        NVARCHAR(50)    NOT NULL DEFAULT '',
                        Success             BIT             NOT NULL DEFAULT 1,
                        ErrorMessage        NVARCHAR(1000)  NULL,
                        DurationMs          FLOAT           NOT NULL DEFAULT 0
                    );
                END");

            // 22. Seed AI feature-flag AppSettings keys
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'AiTriageEnabled')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('AiTriageEnabled', 'false', 'AI Triage',
                            'Enable ML.NET-powered AI triage suggestions for new tickets');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'AiTriageMode')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('AiTriageMode', 'RecommendOnly', 'AI Triage',
                            'RecommendOnly: show suggestions to agents | AutoApply: automatically apply suggestions when confidence is high');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'AiConfidenceThreshold')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('AiConfidenceThreshold', '0.65', 'AI Triage',
                            'Minimum confidence score (0.0–1.0) required before a triage suggestion is shown or applied');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'AiMinTrainingTickets')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('AiMinTrainingTickets', '20', 'AI Triage',
                            'Minimum number of resolved/closed tickets required before the AI model is trained');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaEnabled')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('OllamaEnabled', 'false', 'AI Triage',
                            'Enable Ollama local LLM for AI-generated ticket summaries and draft replies');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaUrl')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('OllamaUrl', 'http://localhost:11434', 'AI Triage',
                            'URL of the locally running Ollama server (default: http://localhost:11434)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaModel')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('OllamaModel', 'phi3', 'AI Triage',
                            'Ollama model name to use for text generation (e.g. phi3, llama3.1, mistral)');");

            // 23. Add escalation columns to Tickets table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'IsEscalated'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets ADD IsEscalated       BIT           NOT NULL DEFAULT 0;
                    ALTER TABLE dbo.Tickets ADD EscalationReason  NVARCHAR(500) NULL;
                    ALTER TABLE dbo.Tickets ADD EscalatedAt       DATETIME2     NULL;
                    ALTER TABLE dbo.Tickets ADD EscalatedById     INT           NULL
                        CONSTRAINT FK_Tickets_EscalatedBy
                        REFERENCES dbo.Employees(Id)
                        ON DELETE SET NULL;
                END");

            // 24. Add ResolutionType column to Tickets table
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'ResolutionType'
                )
                BEGIN
                    ALTER TABLE dbo.Tickets ADD ResolutionType NVARCHAR(100) NULL;
                END");

            // 25. Seed notification-trigger AppSettings keys
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnTicketCreated')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnTicketCreated', 'true', 'Notifications',
                            'Send confirmation email to the requester when a new ticket is created');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnStatusChange')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnStatusChange', 'true', 'Notifications',
                            'Send email to the requester when the ticket status changes');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnAssignment')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnAssignment', 'true', 'Notifications',
                            'Send email to the assigned agent when a ticket is assigned to them');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnNoteAdded')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnNoteAdded', 'true', 'Notifications',
                            'Send email to the requester when a public comment is added to their ticket');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'NotifyOnEscalation')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('NotifyOnEscalation', 'true', 'Notifications',
                            'Send email to the assigned agent and admin when a ticket is escalated');"  );

            // 26. Seed Report Request category sub-categories and keywords (Category = 7)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketSubCategories WHERE Category = 7)
                BEGIN
                    INSERT INTO dbo.TicketSubCategories (Category, Name, SortOrder) VALUES
                    (7, N'AR Report',                10),
                    (7, N'Installation Report',      20),
                    (7, N'Inventory Report',         30),
                    (7, N'Rebate Report',            40),
                    (7, N'Sales Report',             50),
                    (7, N'Management Report',        60),
                    (7, N'Custom / Ad-Hoc Report',   70),
                    (7, N'General Report Request',   80);
                END

                -- Seed auto-classification keywords for Report Request
                IF NOT EXISTS (SELECT 1 FROM dbo.CategoryKeywords WHERE Category = 7)
                BEGIN
                    INSERT INTO dbo.CategoryKeywords (Category, Keyword) VALUES
                    (7, N'report'),
                    (7, N'reporting'),
                    (7, N'ar report'),
                    (7, N'accounts receivable report'),
                    (7, N'installation report'),
                    (7, N'inventory report'),
                    (7, N'rebate report'),
                    (7, N'sales report'),
                    (7, N'management report'),
                    (7, N'generate report'),
                    (7, N'run report'),
                    (7, N'pull report'),
                    (7, N'export report'),
                    (7, N'report request'),
                    (7, N'monthly report'),
                    (7, N'weekly report'),
                    (7, N'quarterly report');
                END");

            // 27. Create TicketCategories table and seed system categories
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketCategories')
                BEGIN
                    CREATE TABLE dbo.TicketCategories (
                        Id          INT             NOT NULL PRIMARY KEY,
                        Name        NVARCHAR(100)   NOT NULL,
                        IsSystem    BIT             NOT NULL DEFAULT 1,
                        IsActive    BIT             NOT NULL DEFAULT 1,
                        SortOrder   INT             NOT NULL DEFAULT 0,
                        Icon        NVARCHAR(60)    NULL,
                        Color       NVARCHAR(20)    NULL
                    );
                END

                -- Seed system categories (IDs 0-7 match the original TicketCategory enum values)
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 0)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (0, N'Service Request',   1, 1, 10, N'bi-clipboard-check',    N'#4f46e5');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 1)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (1, N'Hardware Issue',    1, 1, 20, N'bi-pc-display',         N'#0d6efd');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 2)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (2, N'Software Issue',    1, 1, 30, N'bi-code-square',        N'#198754');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 3)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (3, N'Employee Issue',    1, 1, 40, N'bi-person-badge',       N'#fd7e14');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 4)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (4, N'Network Issue',     1, 1, 50, N'bi-router',             N'#0dcaf0');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 5)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (5, N'Security Incident', 1, 1, 60, N'bi-shield-exclamation', N'#dc3545');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 6)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (6, N'Other',             1, 1, 70, N'bi-question-circle',    N'#6c757d');
                IF NOT EXISTS (SELECT 1 FROM dbo.TicketCategories WHERE Id = 7)
                    INSERT INTO dbo.TicketCategories (Id, Name, IsSystem, IsActive, SortOrder, Icon, Color)
                    VALUES (7, N'Report Request',    1, 1, 80, N'bi-file-earmark-bar-graph', N'#20c997');

                -- 28. Seed SLA policy hours AppSettings
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'SlaHoursCritical')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('SlaHoursCritical', '4', 'SLA', 'SLA response time in hours for Critical priority tickets (default: 4)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'SlaHoursHigh')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('SlaHoursHigh', '8', 'SLA', 'SLA response time in hours for High priority tickets (default: 8)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'SlaHoursMedium')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('SlaHoursMedium', '24', 'SLA', 'SLA response time in hours for Medium priority tickets (default: 24)');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'SlaHoursLow')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('SlaHoursLow', '72', 'SLA', 'SLA response time in hours for Low priority tickets (default: 72)');

                -- Migrate SavedTicketViews.FilterCategories from enum names to numeric IDs
                -- Only runs when alphabetic names are still present (one-time migration)
                IF EXISTS (SELECT 1 FROM dbo.SavedTicketViews WHERE FilterCategories LIKE '%[a-zA-Z]%')
                BEGIN
                    UPDATE dbo.SavedTicketViews
                    SET FilterCategories =
                        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                            ISNULL(FilterCategories, ''),
                            'ServiceRequest',   '0'),
                            'HardwareIssue',    '1'),
                            'SoftwareIssue',    '2'),
                            'EmployeeIssue',    '3'),
                            'NetworkIssue',     '4'),
                            'SecurityIncident', '5'),
                            'ReportRequest',    '7'),
                            'Other',            '6')
                    WHERE FilterCategories IS NOT NULL AND FilterCategories LIKE '%[a-zA-Z]%';
                END");
            // 29. Seed Portal Branding AppSettings
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalWelcomeMessage')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalWelcomeMessage', 'Track and manage your IT support requests.', 'Portal Branding',
                            'Short tagline shown below the greeting on the portal dashboard');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalSupportTitle')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalSupportTitle', 'IT Support Portal', 'Portal Branding',
                            'Text appended to the company name in portal browser tab titles');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalAnnouncement')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalAnnouncement', '', 'Portal Branding',
                            'Optional announcement banner shown at the top of every portal page. Leave blank to hide.');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalAnnouncementType')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalAnnouncementType', 'info', 'Portal Branding',
                            'Bootstrap alert colour for the announcement banner: info, warning, danger, or success');

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PortalShowKnowledgeBase')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('PortalShowKnowledgeBase', 'true', 'Portal Branding',
                            'Show the Knowledge Base link in the portal navigation bar');");

            // 30. ITAM enhancements — new asset-related tables + column additions
            context.Database.ExecuteSqlRaw(@"
                -- Network / system fields on Assets
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'IpAddress')
                    ALTER TABLE dbo.Assets ADD IpAddress NVARCHAR(50) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'MacAddress')
                    ALTER TABLE dbo.Assets ADD MacAddress NVARCHAR(17) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'Hostname')
                    ALTER TABLE dbo.Assets ADD Hostname NVARCHAR(200) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'OsVersion')
                    ALTER TABLE dbo.Assets ADD OsVersion NVARCHAR(100) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Assets') AND name = 'OsBuild')
                    ALTER TABLE dbo.Assets ADD OsBuild NVARCHAR(50) NULL;");

            context.Database.ExecuteSqlRaw(@"
                -- AssetId FK on Tickets (optional link to related asset)
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Tickets') AND name = 'AssetId')
                BEGIN
                    ALTER TABLE dbo.Tickets ADD AssetId INT NULL;
                    ALTER TABLE dbo.Tickets ADD CONSTRAINT FK_Tickets_Assets
                        FOREIGN KEY (AssetId) REFERENCES dbo.Assets(Id) ON DELETE SET NULL;
                END");

            context.Database.ExecuteSqlRaw(@"
                -- Assignment history / custody chain
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetAssignmentHistory')
                    CREATE TABLE dbo.AssetAssignmentHistory (
                        Id              INT IDENTITY PRIMARY KEY,
                        AssetId         INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        AssignedToId    INT NULL REFERENCES dbo.Employees(Id) ON DELETE NO ACTION,
                        AssignedById    INT NULL REFERENCES dbo.Employees(Id) ON DELETE NO ACTION,
                        AssignedDate    DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        ReturnedDate    DATETIME2 NULL,
                        Notes           NVARCHAR(500) NULL
                    );

                -- Asset change audit log
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetAuditLogs')
                    CREATE TABLE dbo.AssetAuditLogs (
                        Id              INT IDENTITY PRIMARY KEY,
                        AssetId         INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        FieldName       NVARCHAR(100) NOT NULL,
                        OldValue        NVARCHAR(1000) NULL,
                        NewValue        NVARCHAR(1000) NULL,
                        ChangedDate     DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        ChangedByEmail  NVARCHAR(200) NOT NULL
                    );

                -- Password / credential vault
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetCredentials')
                    CREATE TABLE dbo.AssetCredentials (
                        Id                  INT IDENTITY PRIMARY KEY,
                        AssetId             INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        Label               NVARCHAR(100) NOT NULL,
                        Username            NVARCHAR(200) NULL,
                        EncryptedPassword   NVARCHAR(MAX) NOT NULL,
                        Url                 NVARCHAR(500) NULL,
                        Notes               NVARCHAR(500) NULL,
                        CreatedDate         DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate         DATETIME2 NULL,
                        CreatedByEmail      NVARCHAR(200) NOT NULL
                    );

                -- Asset file attachments
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetAttachments')
                    CREATE TABLE dbo.AssetAttachments (
                        Id                  INT IDENTITY PRIMARY KEY,
                        AssetId             INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        FileName            NVARCHAR(260) NOT NULL,
                        StoredFileName      NVARCHAR(260) NOT NULL,
                        FileSizeBytes       BIGINT NOT NULL DEFAULT 0,
                        ContentType         NVARCHAR(100) NOT NULL DEFAULT 'application/octet-stream',
                        UploadedDate        DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UploadedByEmail     NVARCHAR(200) NOT NULL
                    );

                -- CMDB-lite asset-to-asset relationship web
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetRelationships')
                    CREATE TABLE dbo.AssetRelationships (
                        Id                  INT IDENTITY PRIMARY KEY,
                        SourceAssetId       INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        TargetAssetId       INT NOT NULL REFERENCES dbo.Assets(Id) ON DELETE NO ACTION,
                        RelationshipType    NVARCHAR(80) NOT NULL DEFAULT 'ConnectedTo',
                        Notes               NVARCHAR(300) NULL,
                        CreatedDate         DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        CreatedByEmail      NVARCHAR(200) NULL
                    );");

            // 31. Employee credential vault + Subscription → Asset link
            context.Database.ExecuteSqlRaw(@"
                -- Per-employee IT-managed credential vault
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeCredentials')
                    CREATE TABLE dbo.EmployeeCredentials (
                        Id                  INT IDENTITY PRIMARY KEY,
                        EmployeeId          INT NOT NULL REFERENCES dbo.Employees(Id) ON DELETE CASCADE,
                        Label               NVARCHAR(100) NOT NULL,
                        Username            NVARCHAR(200) NULL,
                        EncryptedPassword   NVARCHAR(MAX) NOT NULL,
                        Url                 NVARCHAR(500) NULL,
                        Notes               NVARCHAR(500) NULL,
                        CreatedDate         DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate         DATETIME2 NULL,
                        CreatedByEmail      NVARCHAR(200) NOT NULL
                    );

                -- Link software subscriptions to a specific asset (device-based license tracking)
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Subscriptions') AND name = 'AssetId')
                BEGIN
                    ALTER TABLE dbo.Subscriptions ADD AssetId INT NULL;
                    ALTER TABLE dbo.Subscriptions ADD CONSTRAINT FK_Subscriptions_Assets
                        FOREIGN KEY (AssetId) REFERENCES dbo.Assets(Id) ON DELETE SET NULL;
                END");

            // 32. Google Workspace integration settings
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GoogleWorkspaceSettings')
                    CREATE TABLE dbo.GoogleWorkspaceSettings (
                        Id                          INT IDENTITY PRIMARY KEY,
                        EncryptedServiceAccountJson NVARCHAR(MAX) NULL,
                        AdminEmail                  NVARCHAR(300) NOT NULL DEFAULT '',
                        Domain                      NVARCHAR(200) NOT NULL DEFAULT '',
                        IsConfigured                BIT NOT NULL DEFAULT 0,
                        LastTestedDate              DATETIME2 NULL,
                        LastTestResult              NVARCHAR(500) NULL,
                        LastTestPassed              BIT NOT NULL DEFAULT 0,
                        SignatureTemplate           NVARCHAR(MAX) NULL,
                        CreatedDate                 DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate                 DATETIME2 NULL
                    );");

            // 33. CSAT survey table + feature-flag AppSettings
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CsatSurveys')
                BEGIN
                    CREATE TABLE dbo.CsatSurveys (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId        INT             NOT NULL
                            CONSTRAINT FK_CsatSurveys_Tickets
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE CASCADE,
                        Token           NVARCHAR(64)    NOT NULL,
                        Score           INT             NULL,
                        Feedback        NVARCHAR(1000)  NULL,
                        SentDate        DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                        CompletedDate   DATETIME2       NULL,
                        CONSTRAINT UQ_CsatSurveys_Token UNIQUE (Token)
                    );

                    CREATE INDEX IX_CsatSurveys_TicketId
                        ON dbo.CsatSurveys (TicketId);
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'CsatSurveyEnabled')
                    INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                    VALUES ('CsatSurveyEnabled', 'false', 'Surveys',
                            'Send a post-resolution satisfaction survey (1–5 stars) to the ticket requester when a ticket is closed');");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'LastGoogleSignatureSync')
                    ALTER TABLE dbo.Employees ADD LastGoogleSignatureSync DATETIME2 NULL;");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'ScheduledOffboardingDate')
                    ALTER TABLE dbo.Employees ADD ScheduledOffboardingDate DATETIME2 NULL;");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'ManagerEmail')
                    ALTER TABLE dbo.Employees ADD ManagerEmail NVARCHAR(200) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'EmployeeType')
                    ALTER TABLE dbo.Employees ADD EmployeeType NVARCHAR(100) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'FloorSection')
                    ALTER TABLE dbo.Employees ADD FloorSection NVARCHAR(100) NULL;");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Branches') AND name = 'CostCenter')
                    ALTER TABLE dbo.Branches ADD CostCenter NVARCHAR(20) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Branches') AND name = 'BuildingId')
                    ALTER TABLE dbo.Branches ADD BuildingId NVARCHAR(20) NULL;");

            // 35. Asset Manager upgrades — Maintenance logs, Checkouts, Software Licenses, Consumables
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetMaintenanceLogs')
                BEGIN
                    CREATE TABLE dbo.AssetMaintenanceLogs (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        AssetId         INT             NOT NULL
                            CONSTRAINT FK_AssetMaintenanceLogs_Assets
                            REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        ServiceDate     DATE            NOT NULL,
                        ServiceType     NVARCHAR(100)   NOT NULL,
                        Description     NVARCHAR(1000)  NOT NULL,
                        Cost            DECIMAL(18,2)   NULL,
                        Vendor          NVARCHAR(200)   NULL,
                        PerformedBy     NVARCHAR(200)   NULL,
                        NextServiceDate DATE            NULL,
                        CreatedByEmail  NVARCHAR(200)   NOT NULL DEFAULT '',
                        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetCheckouts')
                BEGIN
                    CREATE TABLE dbo.AssetCheckouts (
                        Id                  INT           NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        AssetId             INT           NOT NULL
                            CONSTRAINT FK_AssetCheckouts_Assets
                            REFERENCES dbo.Assets(Id) ON DELETE CASCADE,
                        CheckedOutToId      INT           NULL
                            CONSTRAINT FK_AssetCheckouts_Employees
                            REFERENCES dbo.Employees(Id) ON DELETE SET NULL,
                        CheckedOutByEmail   NVARCHAR(200) NULL,
                        CheckoutDate        DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
                        DueDate             DATE          NOT NULL,
                        ReturnedDate        DATE          NULL,
                        Notes               NVARCHAR(500) NULL
                    );
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SoftwareLicenses')
                BEGIN
                    CREATE TABLE dbo.SoftwareLicenses (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        ProductName         NVARCHAR(200)   NOT NULL,
                        Publisher           NVARCHAR(200)   NULL,
                        LicenseKey          NVARCHAR(500)   NULL,
                        LicenseType         INT             NOT NULL DEFAULT 0,
                        TotalSeats          INT             NOT NULL DEFAULT 1,
                        SeatsInUse          INT             NOT NULL DEFAULT 0,
                        CostPerSeat         DECIMAL(18,2)   NULL,
                        PurchaseDate        DATE            NULL,
                        ExpiryDate          DATE            NULL,
                        Vendor              NVARCHAR(200)   NULL,
                        PurchaseOrderNumber NVARCHAR(100)   NULL,
                        Notes               NVARCHAR(1000)  NULL,
                        IsActive            BIT             NOT NULL DEFAULT 1,
                        CreatedDate         DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate         DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ConsumableItems')
                BEGIN
                    CREATE TABLE dbo.ConsumableItems (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Name            NVARCHAR(200)   NOT NULL,
                        Category        NVARCHAR(100)   NOT NULL,
                        Manufacturer    NVARCHAR(100)   NULL,
                        PartNumber      NVARCHAR(100)   NULL,
                        QuantityOnHand  INT             NOT NULL DEFAULT 0,
                        ReorderPoint    INT             NULL,
                        UnitCost        DECIMAL(18,2)   NULL,
                        Notes           NVARCHAR(1000)  NULL,
                        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ConsumableTransactions')
                BEGIN
                    CREATE TABLE dbo.ConsumableTransactions (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        ConsumableItemId    INT             NOT NULL
                            CONSTRAINT FK_ConsumableTransactions_Items
                            REFERENCES dbo.ConsumableItems(Id) ON DELETE CASCADE,
                        TransactionType     INT             NOT NULL DEFAULT 0,
                        Quantity            INT             NOT NULL,
                        QuantityBefore      INT             NOT NULL,
                        QuantityAfter       INT             NOT NULL,
                        Notes               NVARCHAR(500)   NULL,
                        PerformedByEmail    NVARCHAR(200)   NULL,
                        TransactionDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );
                END");

            // 36. Ticket time tracking, License seat assignments, Onboarding / Offboarding tasks
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketTimeEntries')
                BEGIN
                    CREATE TABLE dbo.TicketTimeEntries (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId            INT             NOT NULL
                            CONSTRAINT FK_TicketTimeEntries_Tickets
                            REFERENCES dbo.Tickets(Id) ON DELETE CASCADE,
                        LoggedByEmail       NVARCHAR(200)   NULL,
                        LoggedByEmployeeId  INT             NULL
                            CONSTRAINT FK_TicketTimeEntries_Employees
                            REFERENCES dbo.Employees(Id) ON DELETE SET NULL,
                        WorkDate            DATE            NOT NULL,
                        Hours               DECIMAL(6,2)    NOT NULL,
                        Description         NVARCHAR(1000)  NULL,
                        IsBillable          BIT             NOT NULL DEFAULT 0,
                        CreatedDate         DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );

                    CREATE INDEX IX_TicketTimeEntries_Ticket_Date
                        ON dbo.TicketTimeEntries (TicketId, WorkDate);
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LicenseSeats')
                BEGIN
                    CREATE TABLE dbo.LicenseSeats (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        SoftwareLicenseId   INT             NOT NULL
                            CONSTRAINT FK_LicenseSeats_Licenses
                            REFERENCES dbo.SoftwareLicenses(Id) ON DELETE CASCADE,
                        EmployeeId          INT             NULL
                            CONSTRAINT FK_LicenseSeats_Employees
                            REFERENCES dbo.Employees(Id) ON DELETE SET NULL,
                        AssetId             INT             NULL
                            CONSTRAINT FK_LicenseSeats_Assets
                            REFERENCES dbo.Assets(Id) ON DELETE SET NULL,
                        AssignedDate        DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
                        AssignedByEmail     NVARCHAR(200)   NULL,
                        RevokedDate         DATETIME2       NULL,
                        RevokedByEmail      NVARCHAR(200)   NULL,
                        Notes               NVARCHAR(500)   NULL
                    );

                    CREATE INDEX IX_LicenseSeats_License_Revoked
                        ON dbo.LicenseSeats (SoftwareLicenseId, RevokedDate);
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeTaskTemplates')
                BEGIN
                    CREATE TABLE dbo.EmployeeTaskTemplates (
                        Id                      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Title                   NVARCHAR(200)   NOT NULL,
                        Description             NVARCHAR(1000)  NULL,
                        Category                NVARCHAR(100)   NULL,
                        TaskType                INT             NOT NULL DEFAULT 0,
                        DefaultAssigneeEmail    NVARCHAR(200)   NULL,
                        DueInDays               INT             NULL,
                        SortOrder               INT             NOT NULL DEFAULT 0,
                        IsActive                BIT             NOT NULL DEFAULT 1,
                        CreatedDate             DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate             DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );

                    CREATE INDEX IX_EmployeeTaskTemplates_Type_Active_Sort
                        ON dbo.EmployeeTaskTemplates (TaskType, IsActive, SortOrder);
                END");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeTasks')
                BEGIN
                    CREATE TABLE dbo.EmployeeTasks (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        EmployeeId          INT             NOT NULL
                            CONSTRAINT FK_EmployeeTasks_Employees
                            REFERENCES dbo.Employees(Id) ON DELETE CASCADE,
                        Title               NVARCHAR(200)   NOT NULL,
                        Description         NVARCHAR(1000)  NULL,
                        Category            NVARCHAR(100)   NULL,
                        TaskType            INT             NOT NULL DEFAULT 0,
                        Status              INT             NOT NULL DEFAULT 0,
                        AssignedToEmail     NVARCHAR(200)   NULL,
                        DueDate             DATE            NULL,
                        CompletedDate       DATETIME2       NULL,
                        CompletedByEmail    NVARCHAR(200)   NULL,
                        Notes               NVARCHAR(1000)  NULL,
                        SortOrder           INT             NOT NULL DEFAULT 0,
                        CreatedDate         DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
                        UpdatedDate         DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );

                    CREATE INDEX IX_EmployeeTasks_Employee_Type_Status
                        ON dbo.EmployeeTasks (EmployeeId, TaskType, Status);
                END");

        context.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_Status' AND object_id = OBJECT_ID('dbo.Tickets'))
                CREATE INDEX IX_Tickets_Status ON dbo.Tickets (Status);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_Priority' AND object_id = OBJECT_ID('dbo.Tickets'))
                CREATE INDEX IX_Tickets_Priority ON dbo.Tickets (Priority);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_CreatedDate' AND object_id = OBJECT_ID('dbo.Tickets'))
                CREATE INDEX IX_Tickets_CreatedDate ON dbo.Tickets (CreatedDate);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_AssignedToId' AND object_id = OBJECT_ID('dbo.Tickets'))
                CREATE INDEX IX_Tickets_AssignedToId ON dbo.Tickets (AssignedToId);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_SubmittedById' AND object_id = OBJECT_ID('dbo.Tickets'))
                CREATE INDEX IX_Tickets_SubmittedById ON dbo.Tickets (SubmittedById);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_Status_Priority' AND object_id = OBJECT_ID('dbo.Tickets'))
                CREATE INDEX IX_Tickets_Status_Priority ON dbo.Tickets (Status, Priority);");

            // 37. Quarterly Store — StoreProducts
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreProducts')
                BEGIN
                    CREATE TABLE dbo.StoreProducts (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Name            NVARCHAR(200)   NOT NULL,
                        Description     NVARCHAR(2000)  NULL,
                        Category        NVARCHAR(100)   NULL,
                        ImagePath       NVARCHAR(500)   NULL,
                        UnitOfMeasure   NVARCHAR(50)    NULL,
                        IsActive        BIT             NOT NULL DEFAULT 1,
                        SortOrder       INT             NOT NULL DEFAULT 100,
                        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );
                END");

            // 38. Quarterly Store — StoreOrders
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreOrders')
                BEGIN
                    CREATE TABLE dbo.StoreOrders (
                        Id                      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        OrderNumber             NVARCHAR(50)    NOT NULL,
                        PortalUserId            INT             NOT NULL
                            CONSTRAINT FK_StoreOrders_PortalUsers
                            REFERENCES dbo.PortalUsers(Id),
                        OrderDate               DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
                        Status                  NVARCHAR(50)    NOT NULL DEFAULT 'Pending',
                        Quarter                 INT             NOT NULL,
                        Year                    INT             NOT NULL,
                        ConfirmationEmailSent   BIT             NOT NULL DEFAULT 0,
                        Notes                   NVARCHAR(1000)  NULL
                    );

                    CREATE INDEX IX_StoreOrders_User_Year_Quarter
                        ON dbo.StoreOrders (PortalUserId, Year, Quarter);
                END");

            // 39. Quarterly Store — StoreOrderItems
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreOrderItems')
                BEGIN
                    CREATE TABLE dbo.StoreOrderItems (
                        Id                      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        StoreOrderId            INT             NOT NULL
                            CONSTRAINT FK_StoreOrderItems_StoreOrders
                            REFERENCES dbo.StoreOrders(Id) ON DELETE CASCADE,
                        StoreProductId          INT             NOT NULL
                            CONSTRAINT FK_StoreOrderItems_StoreProducts
                            REFERENCES dbo.StoreProducts(Id),
                        Quantity                INT             NOT NULL,
                        ProductNameSnapshot     NVARCHAR(200)   NOT NULL,
                        ProductCategorySnapshot NVARCHAR(100)   NULL
                    );
                END");

            // 40. Quarterly Store — StoreAccessList
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreAccessList')
                BEGIN
                    CREATE TABLE dbo.StoreAccessList (
                        Id                      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        PortalUserId            INT             NOT NULL
                            CONSTRAINT FK_StoreAccessList_PortalUsers
                            REFERENCES dbo.PortalUsers(Id) ON DELETE CASCADE,
                        GrantedByPortalUserId   INT             NULL
                            CONSTRAINT FK_StoreAccessList_GrantedBy
                            REFERENCES dbo.PortalUsers(Id),
                        GrantedDate             DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
                        IsActive                BIT             NOT NULL DEFAULT 1
                    );

                    CREATE INDEX IX_StoreAccessList_User_Active
                        ON dbo.StoreAccessList (PortalUserId, IsActive);
                END");

            // 41. Store product variant support — new columns on StoreProducts
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'HasSizes')
                    ALTER TABLE dbo.StoreProducts ADD HasSizes BIT NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'HasGenderOption')
                    ALTER TABLE dbo.StoreProducts ADD HasGenderOption BIT NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'HasColorOptions')
                    ALTER TABLE dbo.StoreProducts ADD HasColorOptions BIT NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'AvailableSizes')
                    ALTER TABLE dbo.StoreProducts ADD AvailableSizes NVARCHAR(500) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'AvailableColors')
                    ALTER TABLE dbo.StoreProducts ADD AvailableColors NVARCHAR(500) NULL;");

            // 42. Store order item variant selections — new columns on StoreOrderItems
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreOrderItems') AND name = 'SelectedSize')
                    ALTER TABLE dbo.StoreOrderItems ADD SelectedSize NVARCHAR(50) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreOrderItems') AND name = 'SelectedGender')
                    ALTER TABLE dbo.StoreOrderItems ADD SelectedGender NVARCHAR(50) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreOrderItems') AND name = 'SelectedColor')
                    ALTER TABLE dbo.StoreOrderItems ADD SelectedColor NVARCHAR(100) NULL;");

            // 43. Quarterly Store — StoreOperationsAccess (operations team hub access)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreOperationsAccess')
                BEGIN
                    CREATE TABLE dbo.StoreOperationsAccess (
                        Id                      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        PortalUserId            INT             NOT NULL
                            CONSTRAINT FK_StoreOpsAccess_PortalUsers
                            REFERENCES dbo.PortalUsers(Id) ON DELETE CASCADE,
                        GrantedByPortalUserId   INT             NULL
                            CONSTRAINT FK_StoreOpsAccess_GrantedBy
                            REFERENCES dbo.PortalUsers(Id),
                        GrantedDate             DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
                        IsActive                BIT             NOT NULL DEFAULT 1
                    );
                    CREATE INDEX IX_StoreOpsAccess_User_Active
                        ON dbo.StoreOperationsAccess (PortalUserId, IsActive);
                END");

            // 44. Quarterly Store — StoreProductImages (gallery images for product modal)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreProductImages')
                BEGIN
                    CREATE TABLE dbo.StoreProductImages (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        StoreProductId  INT             NOT NULL
                            CONSTRAINT FK_StoreProductImages_StoreProducts
                            REFERENCES dbo.StoreProducts(Id) ON DELETE CASCADE,
                        ImagePath       NVARCHAR(500)   NOT NULL,
                        Alt             NVARCHAR(200)   NULL,
                        SortOrder       INT             NOT NULL DEFAULT 100,
                        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE()
                    );
                    CREATE INDEX IX_StoreProductImages_Product
                        ON dbo.StoreProductImages (StoreProductId);
                END");

            // 45. Add VariantTag to StoreProductImages so images can be associated with
            //     a specific color, size, or gender variant (null = shown for all variants)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.StoreProductImages')
                      AND name = 'VariantTag'
                )
                BEGIN
                    ALTER TABLE dbo.StoreProductImages
                        ADD VariantTag NVARCHAR(100) NULL;
                END");

            // 46. Store product flexibility — pricing toggle, free-form custom options,
            //     tags, per-product max-qty cap, and order-item snapshot fields.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'HasPrice')
                    ALTER TABLE dbo.StoreProducts ADD HasPrice BIT NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'Price')
                    ALTER TABLE dbo.StoreProducts ADD Price DECIMAL(10, 2) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'CustomOptionsJson')
                    ALTER TABLE dbo.StoreProducts ADD CustomOptionsJson NVARCHAR(4000) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'Tags')
                    ALTER TABLE dbo.StoreProducts ADD Tags NVARCHAR(500) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreProducts') AND name = 'MaxQtyPerOrder')
                    ALTER TABLE dbo.StoreProducts ADD MaxQtyPerOrder INT NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreOrderItems') AND name = 'CustomSelectionsJson')
                    ALTER TABLE dbo.StoreOrderItems ADD CustomSelectionsJson NVARCHAR(2000) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreOrderItems') AND name = 'UnitPriceSnapshot')
                    ALTER TABLE dbo.StoreOrderItems ADD UnitPriceSnapshot DECIMAL(10, 2) NULL;");

            // 47. Branch tracking on store orders — captures the originating
            //     branch/location of the ordering employee for fulfilment routing
            //     and reporting. BranchNameSnapshot keeps historical exports stable.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreOrders') AND name = 'BranchId')
                BEGIN
                    ALTER TABLE dbo.StoreOrders
                        ADD BranchId INT NULL
                        CONSTRAINT FK_StoreOrders_Branches
                        REFERENCES dbo.Branches(Id)
                        ON DELETE SET NULL;
                END
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreOrders') AND name = 'BranchNameSnapshot')
                    ALTER TABLE dbo.StoreOrders ADD BranchNameSnapshot NVARCHAR(200) NULL;");

            // 48. AI sub-category suggestion — adds SuggestedSubCategoryId to
            //     AiRecommendations so the triage engine can recommend not just
            //     category but also the most common sub-category for that category.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AiRecommendations') AND name = 'SuggestedSubCategoryId')
                BEGIN
                    ALTER TABLE dbo.AiRecommendations
                        ADD SuggestedSubCategoryId INT NULL
                        CONSTRAINT FK_AiRecommendations_SubCategory
                        REFERENCES dbo.TicketSubCategories(Id)
                        ON DELETE SET NULL;
                END");

            // 49. Contractor payroll — IsContractor + HourlyRate on Employees,
            //     PayrollReceipts table, and PayrollReceiptId claim column on
            //     TicketTimeEntries so each billable entry can be locked to one receipt.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'IsContractor')
                BEGIN
                    ALTER TABLE dbo.Employees ADD IsContractor BIT NOT NULL DEFAULT 0;
                END

                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'HourlyRate')
                BEGIN
                    ALTER TABLE dbo.Employees ADD HourlyRate DECIMAL(10,2) NULL;
                END

                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceipts')
                BEGIN
                    -- NOTE: ContractorId uses NO ACTION (SQL Server default) instead of CASCADE
                    -- because PayrollReceipts has a second FK back to Employees (ApprovedById),
                    -- and SQL Server forbids multiple cascade paths from the same parent.
                    -- Contractors with payroll history shouldn't be hard-deleted anyway.
                    CREATE TABLE dbo.PayrollReceipts (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        ContractorId        INT             NOT NULL
                            CONSTRAINT FK_PayrollReceipts_Contractor
                            REFERENCES dbo.Employees(Id),
                        PeriodStart         DATE            NOT NULL,
                        PeriodEnd           DATE            NOT NULL,
                        TotalHours          DECIMAL(10,2)   NOT NULL,
                        TotalBillableHours  DECIMAL(10,2)   NOT NULL,
                        HourlyRateSnapshot  DECIMAL(10,2)   NOT NULL,
                        TotalAmount         DECIMAL(12,2)   NOT NULL,
                        Status              NVARCHAR(20)    NOT NULL DEFAULT 'Draft',
                        Notes               NVARCHAR(2000)  NULL,
                        SubmittedDate       DATETIME        NULL,
                        ApprovedDate        DATETIME        NULL,
                        ApprovedById        INT             NULL
                            CONSTRAINT FK_PayrollReceipts_ApprovedBy
                            REFERENCES dbo.Employees(Id)
                            ON DELETE SET NULL,
                        PaidDate            DATETIME        NULL,
                        CreatedDate         DATETIME        NOT NULL DEFAULT GETUTCDATE()
                    );

                    CREATE INDEX IX_PayrollReceipts_Contractor_Status
                        ON dbo.PayrollReceipts (ContractorId, Status);
                END

                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TicketTimeEntries') AND name = 'PayrollReceiptId')
                BEGIN
                    ALTER TABLE dbo.TicketTimeEntries
                        ADD PayrollReceiptId INT NULL
                        CONSTRAINT FK_TicketTimeEntries_PayrollReceipt
                        REFERENCES dbo.PayrollReceipts(Id)
                        ON DELETE SET NULL;
                END");

            // 50. Notification activity log — records every outbound notification email
            //     so admins can audit what was sent, when, and whether it succeeded.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'NotificationLogs')
                BEGIN
                    CREATE TABLE dbo.NotificationLogs (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        TicketId            INT             NULL
                            CONSTRAINT FK_NotificationLogs_Ticket
                            REFERENCES dbo.Tickets(Id)
                            ON DELETE SET NULL,
                        NotificationType    NVARCHAR(50)    NOT NULL,
                        RecipientEmail      NVARCHAR(200)   NOT NULL,
                        RecipientName       NVARCHAR(200)   NULL,
                        Subject             NVARCHAR(500)   NULL,
                        Success             BIT             NOT NULL DEFAULT 1,
                        ErrorMessage        NVARCHAR(1000)  NULL,
                        SentDate            DATETIME        NOT NULL DEFAULT GETUTCDATE()
                    );

                    CREATE INDEX IX_NotificationLogs_TicketId
                        ON dbo.NotificationLogs (TicketId);
                    CREATE INDEX IX_NotificationLogs_SentDate
                        ON dbo.NotificationLogs (SentDate DESC);
                END");

            // 51. Payroll receipt rejection — allows admin to return a Submitted
            //     receipt to Draft with a written reason for revision.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'RejectionNote')
                BEGIN
                    ALTER TABLE dbo.PayrollReceipts ADD RejectionNote NVARCHAR(1000) NULL;
                END");

            // 52. Performance indexes on the Tickets table — every ticket query
            //     filters by Status, AssignedToId, or sorts by CreatedDate. Without
            //     indexes these queries do full table scans on every page load.
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Tickets')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_Status' AND object_id = OBJECT_ID('dbo.Tickets'))
                        CREATE INDEX IX_Tickets_Status ON dbo.Tickets (Status);

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_AssignedToId' AND object_id = OBJECT_ID('dbo.Tickets'))
                        CREATE INDEX IX_Tickets_AssignedToId ON dbo.Tickets (AssignedToId);

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_CreatedDate' AND object_id = OBJECT_ID('dbo.Tickets'))
                        CREATE INDEX IX_Tickets_CreatedDate ON dbo.Tickets (CreatedDate DESC);

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_SubmittedById' AND object_id = OBJECT_ID('dbo.Tickets'))
                        CREATE INDEX IX_Tickets_SubmittedById ON dbo.Tickets (SubmittedById);

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_Status_AssignedToId' AND object_id = OBJECT_ID('dbo.Tickets'))
                        CREATE INDEX IX_Tickets_Status_AssignedToId ON dbo.Tickets (Status, AssignedToId);
                END");

            // 53. Performance indexes on TicketTimeEntries — payroll receipt
            //     queries filter by TicketId and PayrollReceiptId.
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketTimeEntries')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TicketTimeEntries_TicketId' AND object_id = OBJECT_ID('dbo.TicketTimeEntries'))
                        CREATE INDEX IX_TicketTimeEntries_TicketId ON dbo.TicketTimeEntries (TicketId);

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TicketTimeEntries_PayrollReceiptId' AND object_id = OBJECT_ID('dbo.TicketTimeEntries'))
                        CREATE INDEX IX_TicketTimeEntries_PayrollReceiptId ON dbo.TicketTimeEntries (PayrollReceiptId);

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TicketTimeEntries_LoggedByEmployeeId' AND object_id = OBJECT_ID('dbo.TicketTimeEntries'))
                        CREATE INDEX IX_TicketTimeEntries_LoggedByEmployeeId ON dbo.TicketTimeEntries (LoggedByEmployeeId);
                END");

            // 54. Performance indexes on PayrollReceipts — Admin payroll queries
            //     filter by Status and ContractorId.
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceipts')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PayrollReceipts_Status' AND object_id = OBJECT_ID('dbo.PayrollReceipts'))
                        CREATE INDEX IX_PayrollReceipts_Status ON dbo.PayrollReceipts (Status);

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PayrollReceipts_ContractorId' AND object_id = OBJECT_ID('dbo.PayrollReceipts'))
                        CREATE INDEX IX_PayrollReceipts_ContractorId ON dbo.PayrollReceipts (ContractorId);
                END");

            // 55. In-app portal notifications (notification bell). One row per recipient.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PortalNotifications')
                BEGIN
                    CREATE TABLE dbo.PortalNotifications (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        PortalUserId    INT             NOT NULL
                            CONSTRAINT FK_PortalNotifications_User
                            REFERENCES dbo.PortalUsers(Id)
                            ON DELETE CASCADE,
                        [Type]          NVARCHAR(60)    NOT NULL,
                        Title           NVARCHAR(200)   NOT NULL,
                        Message         NVARCHAR(1000)  NULL,
                        LinkUrl         NVARCHAR(500)   NULL,
                        Icon            NVARCHAR(60)    NULL,
                        IsRead          BIT             NOT NULL DEFAULT 0,
                        CreatedDate     DATETIME        NOT NULL DEFAULT GETUTCDATE(),
                        ReadDate        DATETIME        NULL
                    );

                    CREATE INDEX IX_PortalNotifications_User_Unread
                        ON dbo.PortalNotifications (PortalUserId, IsRead, CreatedDate DESC);
                END");

            // 58. Per-user store favorites. (user, product) is unique.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreProductFavorites')
                BEGIN
                    CREATE TABLE dbo.StoreProductFavorites (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        PortalUserId    INT             NOT NULL
                            CONSTRAINT FK_StoreProductFavorites_User
                            REFERENCES dbo.PortalUsers(Id)
                            ON DELETE CASCADE,
                        StoreProductId  INT             NOT NULL
                            CONSTRAINT FK_StoreProductFavorites_Product
                            REFERENCES dbo.StoreProducts(Id)
                            ON DELETE CASCADE,
                        AddedDate       DATETIME        NOT NULL DEFAULT GETUTCDATE(),
                        CONSTRAINT UQ_StoreProductFavorites_User_Product
                            UNIQUE (PortalUserId, StoreProductId)
                    );
                END");

            // 59. Saved size / gender / color preference on PortalUsers. Used to
            //     pre-select variants in the catalog modal next visit so users
            //     don't have to re-pick their size every quarter.
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PortalUsers')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PortalUsers') AND name = 'PreferredStoreSize')
                        ALTER TABLE dbo.PortalUsers ADD PreferredStoreSize   NVARCHAR(50) NULL;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PortalUsers') AND name = 'PreferredStoreGender')
                        ALTER TABLE dbo.PortalUsers ADD PreferredStoreGender NVARCHAR(50) NULL;
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PortalUsers') AND name = 'PreferredStoreColor')
                        ALTER TABLE dbo.PortalUsers ADD PreferredStoreColor  NVARCHAR(50) NULL;
                END");

            // 57. Persistent shopping cart for the Company Store. One row per
            //     cart line per user. Cleared on order placement.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreCartItems')
                BEGIN
                    CREATE TABLE dbo.StoreCartItems (
                        Id                      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        PortalUserId            INT             NOT NULL
                            CONSTRAINT FK_StoreCartItems_User
                            REFERENCES dbo.PortalUsers(Id)
                            ON DELETE CASCADE,
                        StoreProductId          INT             NOT NULL
                            CONSTRAINT FK_StoreCartItems_Product
                            REFERENCES dbo.StoreProducts(Id),
                        Quantity                INT             NOT NULL DEFAULT 1,
                        SelectedSize            NVARCHAR(50)    NULL,
                        SelectedGender          NVARCHAR(50)    NULL,
                        SelectedColor           NVARCHAR(50)    NULL,
                        CustomSelectionsJson    NVARCHAR(MAX)   NULL,
                        AddedDate               DATETIME        NOT NULL DEFAULT GETUTCDATE()
                    );

                    CREATE INDEX IX_StoreCartItems_User
                        ON dbo.StoreCartItems (PortalUserId, AddedDate DESC);
                END");

            // 56. Track when a store order's status last changed (for the order timeline stepper).
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreOrders')
                   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.StoreOrders') AND name = 'LastStatusChangedDate')
                BEGIN
                    ALTER TABLE dbo.StoreOrders ADD LastStatusChangedDate DATETIME NULL;
                END");

            // 60. Inbound email log — one row per Gmail message the poller saw,
            //     with the outcome (TicketCreated / NoteAppended / Skipped:X /
            //     Failed). Diagnostic counterpart to NotificationLogs so admins
            //     can see WHY an incoming message did or didn't become a ticket.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InboundEmailLogs')
                BEGIN
                    CREATE TABLE dbo.InboundEmailLogs (
                        Id                      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        EmailConfigurationId    INT             NULL,
                        GmailMessageId          NVARCHAR(100)   NOT NULL,
                        MessageId               NVARCHAR(500)   NULL,
                        Subject                 NVARCHAR(500)   NULL,
                        FromAddress             NVARCHAR(200)   NULL,
                        ReceivedDate            DATETIME        NULL,
                        ProcessedDate           DATETIME        NOT NULL DEFAULT GETUTCDATE(),
                        Action                  NVARCHAR(50)    NOT NULL DEFAULT 'Unknown',
                        ActionDetail            NVARCHAR(500)   NULL,
                        ErrorMessage            NVARCHAR(2000)  NULL
                    );

                    CREATE INDEX IX_InboundEmailLogs_ProcessedDate
                        ON dbo.InboundEmailLogs (ProcessedDate DESC);
                    CREATE INDEX IX_InboundEmailLogs_Action
                        ON dbo.InboundEmailLogs (Action, ProcessedDate DESC);
                    CREATE INDEX IX_InboundEmailLogs_GmailMessageId
                        ON dbo.InboundEmailLogs (GmailMessageId);
                END");

            // 61. LastSuccessfulPollDate on EmailConfigurations — the existing
            //     LastPolledDate advances on failures too, which makes it
            //     useless for the diagnostic card's "Last successful poll"
            //     readout. This new column is only stamped after a clean cycle.
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmailConfigurations')
                   AND NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.EmailConfigurations')
                                     AND name = 'LastSuccessfulPollDate')
                BEGIN
                    ALTER TABLE dbo.EmailConfigurations
                        ADD LastSuccessfulPollDate DATETIME NULL;
                END");

            // 62. Hot-path indexes for tables that EF Core's HasIndex
            //     declarations cover but the raw-SQL DbInitializer path may not.
            //     Each statement is idempotent so re-running on an already-
            //     indexed DB is a no-op. Covers:
            //       - TicketEmails: anti-duplicate + threading lookups on
            //         every inbound Gmail message
            //       - StoreOrderItems: the "any order references this product?"
            //         check used by product deactivate / delete, plus the Ops
            //         Hub product-totals aggregation
            //       - TicketNotes: ticket detail page loads all notes for a ticket
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketEmails')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                   WHERE name = 'IX_TicketEmails_GmailMessageId'
                                     AND object_id = OBJECT_ID('dbo.TicketEmails'))
                        CREATE UNIQUE INDEX IX_TicketEmails_GmailMessageId
                            ON dbo.TicketEmails (GmailMessageId)
                            WHERE GmailMessageId IS NOT NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                   WHERE name = 'IX_TicketEmails_MessageId'
                                     AND object_id = OBJECT_ID('dbo.TicketEmails'))
                        CREATE INDEX IX_TicketEmails_MessageId
                            ON dbo.TicketEmails (MessageId);

                    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                   WHERE name = 'IX_TicketEmails_TicketId'
                                     AND object_id = OBJECT_ID('dbo.TicketEmails'))
                        CREATE INDEX IX_TicketEmails_TicketId
                            ON dbo.TicketEmails (TicketId);
                END

                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreOrderItems')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                   WHERE name = 'IX_StoreOrderItems_StoreProductId'
                                     AND object_id = OBJECT_ID('dbo.StoreOrderItems'))
                        CREATE INDEX IX_StoreOrderItems_StoreProductId
                            ON dbo.StoreOrderItems (StoreProductId);
                END

                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketNotes')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                   WHERE name = 'IX_TicketNotes_TicketId'
                                     AND object_id = OBJECT_ID('dbo.TicketNotes'))
                        CREATE INDEX IX_TicketNotes_TicketId
                            ON dbo.TicketNotes (TicketId, CreatedDate DESC);
                END
            ");

            // 63. Contractor payroll — second rate + monthly retainer columns
            //     on Employees, RateType on TicketTimeEntries, and snapshot
            //     columns on PayrollReceipts for the burn-down retainer model.
            //     All nullable / defaulted so existing receipts continue to
            //     load cleanly.
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Employees')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'EmergencyHourlyRate')
                        ALTER TABLE dbo.Employees ADD EmergencyHourlyRate DECIMAL(10,2) NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'MonthlyRetainerAmount')
                        ALTER TABLE dbo.Employees ADD MonthlyRetainerAmount DECIMAL(10,2) NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.Employees') AND name = 'MonthlyRetainerHoursIncluded')
                        ALTER TABLE dbo.Employees ADD MonthlyRetainerHoursIncluded DECIMAL(6,2) NULL;
                END

                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketTimeEntries')
                   AND NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.TicketTimeEntries') AND name = 'RateType')
                BEGIN
                    ALTER TABLE dbo.TicketTimeEntries
                        ADD RateType TINYINT NOT NULL CONSTRAINT DF_TicketTimeEntries_RateType DEFAULT 0;
                END

                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceipts')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'EmergencyRateSnapshot')
                        ALTER TABLE dbo.PayrollReceipts ADD EmergencyRateSnapshot DECIMAL(10,2) NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'TotalStandardHours')
                        ALTER TABLE dbo.PayrollReceipts ADD TotalStandardHours DECIMAL(10,2) NOT NULL CONSTRAINT DF_PayrollReceipts_TotalStandardHours DEFAULT 0;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'TotalEmergencyHours')
                        ALTER TABLE dbo.PayrollReceipts ADD TotalEmergencyHours DECIMAL(10,2) NOT NULL CONSTRAINT DF_PayrollReceipts_TotalEmergencyHours DEFAULT 0;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'MonthlyRetainerAmountSnapshot')
                        ALTER TABLE dbo.PayrollReceipts ADD MonthlyRetainerAmountSnapshot DECIMAL(10,2) NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'MonthlyRetainerHoursSnapshot')
                        ALTER TABLE dbo.PayrollReceipts ADD MonthlyRetainerHoursSnapshot DECIMAL(6,2) NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'TotalRetainerHoursApplied')
                        ALTER TABLE dbo.PayrollReceipts ADD TotalRetainerHoursApplied DECIMAL(10,2) NOT NULL CONSTRAINT DF_PayrollReceipts_TotalRetainerHoursApplied DEFAULT 0;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'TotalRetainerAmountApplied')
                        ALTER TABLE dbo.PayrollReceipts ADD TotalRetainerAmountApplied DECIMAL(12,2) NOT NULL CONSTRAINT DF_PayrollReceipts_TotalRetainerAmountApplied DEFAULT 0;
                END");

            // 64. Time-entry clock-in/out + modification audit + payroll
            //     receipt ApprovalNote + CompanyHolidays admin table. All
            //     additions are idempotent ALTER TABLE / CREATE TABLE checks.
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketTimeEntries')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.TicketTimeEntries') AND name = 'StartTime')
                        ALTER TABLE dbo.TicketTimeEntries ADD StartTime DATETIME NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.TicketTimeEntries') AND name = 'EndTime')
                        ALTER TABLE dbo.TicketTimeEntries ADD EndTime DATETIME NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.TicketTimeEntries') AND name = 'ModifiedDate')
                        ALTER TABLE dbo.TicketTimeEntries ADD ModifiedDate DATETIME NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.TicketTimeEntries') AND name = 'ModifiedByEmail')
                        ALTER TABLE dbo.TicketTimeEntries ADD ModifiedByEmail NVARCHAR(200) NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.TicketTimeEntries') AND name = 'ModificationReason')
                        ALTER TABLE dbo.TicketTimeEntries ADD ModificationReason NVARCHAR(500) NULL;

                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.TicketTimeEntries') AND name = 'ModificationCount')
                        ALTER TABLE dbo.TicketTimeEntries ADD ModificationCount INT NOT NULL CONSTRAINT DF_TicketTimeEntries_ModificationCount DEFAULT 0;
                END

                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceipts')
                   AND NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID('dbo.PayrollReceipts') AND name = 'ApprovalNote')
                BEGIN
                    ALTER TABLE dbo.PayrollReceipts ADD ApprovalNote NVARCHAR(1000) NULL;
                END

                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CompanyHolidays')
                BEGIN
                    CREATE TABLE dbo.CompanyHolidays (
                        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Date                DATE            NOT NULL,
                        Name                NVARCHAR(120)   NOT NULL,
                        IsRecurringYearly   BIT             NOT NULL DEFAULT 0,
                        CreatedDate         DATETIME        NOT NULL DEFAULT GETUTCDATE()
                    );

                    CREATE INDEX IX_CompanyHolidays_Date ON dbo.CompanyHolidays (Date);
                END");

            // Create WorkflowRules table (automation engine)
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkflowRules')
                BEGIN
                    CREATE TABLE dbo.WorkflowRules (
                        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
                        Name            NVARCHAR(200)   NOT NULL,
                        Description     NVARCHAR(500)   NULL,
                        [Trigger]       INT             NOT NULL DEFAULT 0,
                        ConditionsJson  NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
                        ActionsJson     NVARCHAR(MAX)   NOT NULL DEFAULT '[]',
                        IsActive        BIT             NOT NULL DEFAULT 1,
                        SortOrder       INT             NOT NULL DEFAULT 100,
                        StopOnMatch     BIT             NOT NULL DEFAULT 0,
                        RunCount        INT             NOT NULL DEFAULT 0,
                        LastRunAt       DATETIME2       NULL,
                        CreatedDate     DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
                    );

                    CREATE INDEX IX_WorkflowRules_Active_Trigger_Sort
                        ON dbo.WorkflowRules (IsActive, [Trigger], SortOrder);
                END");

            // 66. Payroll notification recipients — curated list of who gets
            //     the "receipt submitted" / payment-confirmed alerts. When
            //     empty the notifier falls back to "all active admins."
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollNotificationRecipients')
                BEGIN
                    CREATE TABLE dbo.PayrollNotificationRecipients (
                        Id                      INT             IDENTITY(1,1) NOT NULL,
                        PortalUserId            INT             NULL,
                        DisplayName             NVARCHAR(200)   NULL,
                        Email                   NVARCHAR(200)   NOT NULL,
                        IsActive                BIT             NOT NULL DEFAULT 1,
                        CreatedDate             DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),
                        AddedByPortalUserId     INT             NULL,
                        CONSTRAINT PK_PayrollNotificationRecipients PRIMARY KEY CLUSTERED (Id),
                        CONSTRAINT FK_PayrollNotificationRecipients_PortalUser
                            FOREIGN KEY (PortalUserId) REFERENCES dbo.PortalUsers (Id)
                            ON DELETE SET NULL
                    );

                    CREATE INDEX IX_PayrollNotificationRecipients_Email
                        ON dbo.PayrollNotificationRecipients (Email);
                END");

            // 67. Payroll lifecycle — payment method/reference, contractor's
            //     confirm-received attestation, and the stale-reminder
            //     throttle timestamp. All nullable so existing rows are
            //     untouched.
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceipts')
                BEGIN
                    IF COL_LENGTH('dbo.PayrollReceipts', 'PaymentMethod') IS NULL
                        ALTER TABLE dbo.PayrollReceipts ADD PaymentMethod NVARCHAR(50) NULL;
                    IF COL_LENGTH('dbo.PayrollReceipts', 'PaymentReference') IS NULL
                        ALTER TABLE dbo.PayrollReceipts ADD PaymentReference NVARCHAR(200) NULL;
                    IF COL_LENGTH('dbo.PayrollReceipts', 'PaymentConfirmedDate') IS NULL
                        ALTER TABLE dbo.PayrollReceipts ADD PaymentConfirmedDate DATETIME2(7) NULL;
                    IF COL_LENGTH('dbo.PayrollReceipts', 'PaymentConfirmedNote') IS NULL
                        ALTER TABLE dbo.PayrollReceipts ADD PaymentConfirmedNote NVARCHAR(500) NULL;
                    IF COL_LENGTH('dbo.PayrollReceipts', 'LastReminderSentUtc') IS NULL
                        ALTER TABLE dbo.PayrollReceipts ADD LastReminderSentUtc DATETIME2(7) NULL;
                END");

            // 68. Payroll receipt activity / discussion thread — shared by
            //     human comments (admin <-> contractor) and system-
            //     generated audit entries. Cascade-delete with the parent
            //     receipt; SetNull on author deletion so the audit row
            //     survives.
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceiptComments')
                BEGIN
                    CREATE TABLE dbo.PayrollReceiptComments (
                        Id                      INT             IDENTITY(1,1) NOT NULL,
                        PayrollReceiptId        INT             NOT NULL,
                        AuthorPortalUserId      INT             NULL,
                        AuthorEmployeeId        INT             NULL,
                        AuthorName              NVARCHAR(200)   NOT NULL,
                        AuthorRole              NVARCHAR(20)    NOT NULL DEFAULT N'System',
                        Body                    NVARCHAR(2000)  NOT NULL,
                        CreatedDate             DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),
                        CONSTRAINT PK_PayrollReceiptComments PRIMARY KEY CLUSTERED (Id),
                        CONSTRAINT FK_PayrollReceiptComments_Receipt
                            FOREIGN KEY (PayrollReceiptId) REFERENCES dbo.PayrollReceipts (Id)
                            ON DELETE CASCADE,
                        CONSTRAINT FK_PayrollReceiptComments_PortalUser
                            FOREIGN KEY (AuthorPortalUserId) REFERENCES dbo.PortalUsers (Id)
                            ON DELETE SET NULL,
                        CONSTRAINT FK_PayrollReceiptComments_Employee
                            FOREIGN KEY (AuthorEmployeeId) REFERENCES dbo.Employees (Id)
                            ON DELETE SET NULL
                    );

                    CREATE INDEX IX_PayrollReceiptComments_Receipt_Created
                        ON dbo.PayrollReceiptComments (PayrollReceiptId, CreatedDate);
                END");

            // 69. Seed the reminder-cadence AppSettings — off by default so
            //     a fresh install doesn't start emailing recipients before
            //     the admin has reviewed the list. Delivery mode defaults
            //     to "Individual" — privacy-safe for setups that include
            //     external recipients (AP@vendor.com, payroll bureau).
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AppSettings')
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PayrollReminderEnabled')
                        INSERT INTO dbo.AppSettings ([Key], [Value]) VALUES ('PayrollReminderEnabled', 'false');
                    IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PayrollReminderDays')
                        INSERT INTO dbo.AppSettings ([Key], [Value]) VALUES ('PayrollReminderDays', '3');
                    IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PayrollNotificationDeliveryMode')
                        INSERT INTO dbo.AppSettings ([Key], [Value]) VALUES ('PayrollNotificationDeliveryMode', 'Individual');
                END");

        }
        catch (Exception ex)
        {
            // Log and continue — the app can still start even if upgrades fail
            // (tables may not exist yet on a fresh install)
            Console.WriteLine($"[DbInitializer] Schema upgrade warning: {ex.Message}");
        }

        // 70. Admin SLA override — Tickets.OriginalDueDate captures the SLA
        //     target snapshot at creation so we still know what would have
        //     been due even after an admin extends DueDate. TicketHistory.Reason
        //     carries the admin's justification so the audit trail is
        //     self-contained. Both columns are nullable / additive — existing
        //     rows keep working unchanged.
        //
        //     These two blocks run in their own try/catch so a failure in
        //     any earlier schema upgrade above doesn't skip them — the
        //     Ticket model references OriginalDueDate and the dashboard
        //     query breaks if the column is missing.
        TryRunSchemaUpgrade(context, "AddOriginalDueDateColumn", @"
            IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Tickets')
               AND COL_LENGTH('dbo.Tickets', 'OriginalDueDate') IS NULL
            BEGIN
                ALTER TABLE dbo.Tickets ADD OriginalDueDate DATETIME2(7) NULL;
            END");

        // Backfill runs as a separate statement so the ALTER above can
        // commit first — SQL Server otherwise refuses to reference a
        // freshly-added column in the same batch.
        TryRunSchemaUpgrade(context, "BackfillOriginalDueDate", @"
            IF COL_LENGTH('dbo.Tickets', 'OriginalDueDate') IS NOT NULL
            BEGIN
                UPDATE dbo.Tickets
                   SET OriginalDueDate = DueDate
                 WHERE OriginalDueDate IS NULL AND DueDate IS NOT NULL;
            END");

        TryRunSchemaUpgrade(context, "AddTicketHistoryReasonColumn", @"
            IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketHistory')
               AND COL_LENGTH('dbo.TicketHistory', 'Reason') IS NULL
            BEGIN
                ALTER TABLE dbo.TicketHistory ADD Reason NVARCHAR(500) NULL;
            END");

        // 71. Recurring payroll charges — reusable per-contractor templates
        //     (e.g. "Daily Reports — $25 per weekday") that auto-suggest a
        //     line item on every new payroll receipt. Each saved receipt
        //     snapshots the rows it actually used into PayrollReceiptCharges
        //     so later template edits don't retroactively change historical
        //     totals. Three independent upgrades — each idempotent.
        TryRunSchemaUpgrade(context, "AddTotalRecurringChargesAmountColumn", @"
            IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceipts')
               AND COL_LENGTH('dbo.PayrollReceipts', 'TotalRecurringChargesAmount') IS NULL
            BEGIN
                ALTER TABLE dbo.PayrollReceipts
                    ADD TotalRecurringChargesAmount DECIMAL(12,2) NOT NULL
                    CONSTRAINT DF_PayrollReceipts_TotalRecurringChargesAmount DEFAULT 0;
            END");

        TryRunSchemaUpgrade(context, "CreateRecurringChargeTemplatesTable", @"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RecurringChargeTemplates')
            BEGIN
                CREATE TABLE dbo.RecurringChargeTemplates (
                    Id           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    ContractorId INT NOT NULL,
                    Label        NVARCHAR(120) NOT NULL,
                    Cadence      TINYINT NOT NULL DEFAULT 0,
                    WeekdayMask  TINYINT NOT NULL DEFAULT 62,
                    PricingMode  TINYINT NOT NULL DEFAULT 0,
                    UnitAmount   DECIMAL(12,4) NOT NULL DEFAULT 0,
                    StartDate    DATETIME2(7) NULL,
                    EndDate      DATETIME2(7) NULL,
                    IsActive     BIT NOT NULL DEFAULT 1,
                    Notes        NVARCHAR(500) NULL,
                    CreatedDate  DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_RecurringChargeTemplates_Employees_ContractorId
                        FOREIGN KEY (ContractorId) REFERENCES dbo.Employees(Id) ON DELETE CASCADE
                );
                CREATE INDEX IX_RecurringChargeTemplates_Contractor_Active
                    ON dbo.RecurringChargeTemplates (ContractorId, IsActive);
            END");

        TryRunSchemaUpgrade(context, "CreateTicketTemplatesTable", @"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TicketTemplates')
            BEGIN
                CREATE TABLE dbo.TicketTemplates (
                    Id             INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    Name           NVARCHAR(120) NOT NULL,
                    Description    NVARCHAR(300) NULL,
                    TitleTemplate  NVARCHAR(200) NOT NULL,
                    BodyTemplate   NVARCHAR(2000) NOT NULL,
                    Category       INT NOT NULL,
                    SubCategoryId  INT NULL,
                    Priority       INT NOT NULL DEFAULT 1,
                    SortOrder      INT NOT NULL DEFAULT 0,
                    IsActive       BIT NOT NULL DEFAULT 1,
                    CreatedDate    DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                    UpdatedDate    DATETIME2(7) NULL
                );
                CREATE INDEX IX_TicketTemplates_Active_Sort
                    ON dbo.TicketTemplates (IsActive, SortOrder);
            END");

        TryRunSchemaUpgrade(context, "CreatePayrollReceiptChargesTable", @"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceiptCharges')
            BEGIN
                CREATE TABLE dbo.PayrollReceiptCharges (
                    Id                  INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    PayrollReceiptId    INT NOT NULL,
                    TemplateId          INT NULL,
                    LabelSnapshot       NVARCHAR(120) NOT NULL,
                    CadenceSnapshot     TINYINT NOT NULL DEFAULT 0,
                    PricingModeSnapshot TINYINT NOT NULL DEFAULT 0,
                    UnitAmountSnapshot  DECIMAL(12,2) NOT NULL DEFAULT 0,
                    OccurrenceCount     INT NOT NULL DEFAULT 0,
                    TotalAmount         DECIMAL(12,2) NOT NULL DEFAULT 0,
                    CreatedDate         DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_PayrollReceiptCharges_PayrollReceipts
                        FOREIGN KEY (PayrollReceiptId) REFERENCES dbo.PayrollReceipts(Id) ON DELETE CASCADE,
                    CONSTRAINT FK_PayrollReceiptCharges_RecurringChargeTemplates
                        FOREIGN KEY (TemplateId) REFERENCES dbo.RecurringChargeTemplates(Id) ON DELETE SET NULL
                );
                CREATE INDEX IX_PayrollReceiptCharges_Receipt
                    ON dbo.PayrollReceiptCharges (PayrollReceiptId);
            END");

        TryRunSchemaUpgrade(context, "StoreOrders_AddFulfillmentColumns", @"
            IF COL_LENGTH('dbo.StoreOrders', 'TrackingNumber') IS NULL
            BEGIN
                ALTER TABLE dbo.StoreOrders ADD
                    ConfirmedDate      DATETIME2(7) NULL,
                    ShippedDate        DATETIME2(7) NULL,
                    FulfilledDate      DATETIME2(7) NULL,
                    CancelledDate      DATETIME2(7) NULL,
                    TrackingNumber     NVARCHAR(100) NULL,
                    Carrier            NVARCHAR(50)  NULL,
                    CancellationReason NVARCHAR(500) NULL;
            END");

        TryRunSchemaUpgrade(context, "SeedOllamaResilienceSettings", @"
            IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaTimeoutSeconds')
                INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                VALUES ('OllamaTimeoutSeconds', '300', 'AI Triage',
                        'HTTP timeout (seconds) for Ollama generation calls. CPU-only servers should use 300+; GPU servers can use 120.');

            IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaKeepAlive')
                INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                VALUES ('OllamaKeepAlive', '30m', 'AI Triage',
                        'How long Ollama keeps the model loaded between requests (e.g. 30m, 1h, -1 = forever). Eliminates 30s cold-load on every call.');

            IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaWarmupEnabled')
                INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                VALUES ('OllamaWarmupEnabled', 'true', 'AI Triage',
                        'Pre-load the configured model on app startup and every 25 minutes so the first user request never hits a cold load.');

            IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaNumPredict')
                INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                VALUES ('OllamaNumPredict', '600', 'AI Triage',
                        'Maximum tokens Ollama will generate per call. Caps runaway generation so a single request can''t burn the entire timeout.');

            IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaDraftModel')
                INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                VALUES ('OllamaDraftModel', '', 'AI Triage',
                        'Optional model override for Draft Reply / KB Article generation (e.g. gemma3:12b). Leave blank to use OllamaModel.');

            IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'OllamaTriageModel')
                INSERT INTO dbo.AppSettings ([Key], Value, Category, Description)
                VALUES ('OllamaTriageModel', '', 'AI Triage',
                        'Optional faster model for escalation detection / workflow classification (e.g. phi3). Leave blank to use OllamaModel.');");
    }

    private static void TryRunSchemaUpgrade(ServiceDeskDbContext context, string label, string sql)
    {
        try
        {
            context.Database.ExecuteSqlRaw(sql);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DbInitializer] {label} skipped: {ex.Message}");
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

        try
        {
            SeedEmployeeTaskTemplates(context);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DbInitializer] Task template seed warning: {ex.Message}");
        }
    }

    public static void SeedEmployeeTaskTemplates(ServiceDeskDbContext context, bool force = false)
    {
        // Skip if any templates already exist (so admin edits are preserved), unless force=true
        if (!force && context.EmployeeTaskTemplates.Any()) return;

        var templates = new List<EmployeeTaskTemplate>();
        int sort = 0;

        // ─── ONBOARDING ────────────────────────────────────────────────────────

        // Section: Google Account
        templates.AddRange(new[]
        {
            T("Generate new user password",
              "Use Welcome Letter Template to generate password. Print as PDF and store in New Hire Welcome Letters folder in Drive.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Admin: Create new user account",
              "Create new user in Google Admin Console. Set primary email to firstname@[domain]. Follow company email naming convention.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Admin: Set user password",
              "Apply Seamless Password following company rules or use Password Tool. Ensure complexity requirements are met.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Admin: Add default profile picture",
              "Upload generic company profile picture in Admin Console until official photo is taken.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Admin: User Details — phone & address",
              "Add mobile phone, work phone, and work address in User Details section of Admin Console.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Admin: User Details — alternate email addresses",
              "Add alternate email addresses: First Initial/Last Name, First Name/Last Name, and First Name.Last Name.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Admin: User Details — employee information",
              "Add Job Title, Manager's Email, Cost Center (ex. PHX), and Building Id (ex. Phoenix) in employee information section.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Admin: User Details — security information",
              "Add recovery email (jim@sf.com format) and recovery phone number (ex. 4803585561) in Security Information.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Admin: Add user to Groups",
              "Add to All@ group, location-appropriate groups, and Concur email group. Add other groups appropriate for user type and location.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Sign into new user in new Chrome profile",
              "Open Google Chrome, create a new profile, and sign in as the new user to verify account access.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Set up browser tabs in order",
              "Set up the following pinned tabs: Gmail, Calendar, Google Drive, SF Insight (Location Specific), SF Website.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Sign in to new user email — accept Terms & Conditions",
              "Sign in to the new user email account and accept Google Terms and Conditions on first login.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Gmail Settings: General Tab configuration",
              "Set Undo Send to 10 seconds, Default Reply Behavior to Reply, Default text style to Verdana, Desktop notifications to New Mail On, Stars to All Stars. Copy signature from existing user at same location/position.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Gmail Settings: Labels Tab",
              "Select Show for: Important, All Mail, Spam, Trash.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Gmail Settings: Accounts — import secondary address",
              "Go to Settings > Accounts to Import > Add Another Email Address. Enter user@sf.com and select Make Default.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Gmail Settings: Advanced Tab — Enable Templates",
              "Go to Settings > Advanced Tab and enable Email Templates feature.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Gmail Settings: Add @sf.com signature",
              "In General Settings Tab, add the newuser@sf.com email signature to complete dual-signature setup.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Send welcome Chats to IT team",
              "Send Chats to IT team members (Jim, Jose, Kolby, Chris) to introduce new employee.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Send location-wide Chat announcement (during training)",
              "During employee training, send Chat messages to all employees at that location, owners, and any other relevant employees.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Calendar: Add new user to Location and Company Calendars",
              "Open Google Calendar and add new user to the Location Calendar (ex. SeamlessPHX) and SeamlessLLC shared calendars.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("New User Terminal: Set up Google Drive Stream",
              "On new user's computer, download and sign in to Google Drive Stream. Share from Drive: Tutorials, Location, Logos folders and add to My Drive. Share the SEAMLESS FLOORING folder related to their position.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("New User Terminal: Add calendar email notifications",
              "On new user's computer, go to Calendar. Click each calendar notification email and accept/add each shared calendar.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Google Calendar: Change main view to Month",
              "Change the default calendar view from Day to Month for better scheduling visibility.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Set up Chrome Bookmarks",
              "Bookmark: Gmail, Calendar, Drive, SF Insight, SF Website, SAP Concur login, Paychex login, SF Help Desk.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Chrome Startup: Set to Use Current Pages",
              "Go to Chrome Settings > On Startup > select 'Open a specific set of pages' then click 'Use Current Pages'.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),

            T("Update Master Employee Spreadsheet",
              "Add new employee's information to the company Master Employee Spreadsheet.",
              "Google Account", EmployeeTaskType.Onboarding, sort += 10),
        });

        // Section: Hardware
        templates.AddRange(new[]
        {
            T("Determine: New Computer or Transfer?",
              "Confirm whether new user is receiving a brand new computer or a transferred computer from another employee.",
              "Hardware", EmployeeTaskType.Onboarding, sort += 10),

            T("Determine: New Phone or Transfer?",
              "Confirm whether new user is receiving a brand new phone or a transferred phone from another employee.",
              "Hardware", EmployeeTaskType.Onboarding, sort += 10),

            T("If Transfer — document transferee information",
              "If transferring a computer or phone, document the transferee using abbreviated location and full name (ex. PHX John Smith).",
              "Hardware", EmployeeTaskType.Onboarding, sort += 10),
        });

        // Section: Equipment Setup
        templates.AddRange(new[]
        {
            T("Equipment Setup: Laptop and Charger",
              "Set up and verify laptop and charger are working. Record serial number.",
              "Equipment Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Equipment Setup: Cell Phone and Charger",
              "Set up and verify cell phone and charger are working. Record serial number.",
              "Equipment Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Equipment Setup: Cell Phone Protective Case",
              "Provide and attach cell phone protective case to new employee's phone.",
              "Equipment Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Equipment Setup: Tablet, Cover and Charger",
              "Set up and verify tablet, protective cover, and charger are working. Record serial number.",
              "Equipment Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Equipment Setup: Disto",
              "Set up and verify Disto measuring device is working and assigned to new employee.",
              "Equipment Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Equipment Setup: Mouse/Keyboard",
              "Provide and set up wireless mouse and keyboard or standard mouse and keyboard.",
              "Equipment Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Equipment Setup: Monitor(s)",
              "Set up and configure monitor(s) for the new employee's workstation.",
              "Equipment Setup", EmployeeTaskType.Onboarding, sort += 10),
        });

        // Section: Computer Setup
        templates.AddRange(new[]
        {
            T("Transferred Computer: Upload backup files & clean drive",
              "Verify backup files have been uploaded to G-Drive. Run Recovery (Clean Drive and Keep Files) or remove previous user from laptop accounts.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Transferred Computer: Update TeamViewer name",
              "Verify TeamViewer unattended access name has been changed to the name of the new user.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Computer: Windows Setup",
              "Go through Windows Setup. Do not send information back to vendor. Select location finder. Sign in to local WiFi. Save files to computer (not OneDrive). Switch out of Windows 11 S mode.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Computer: Create local user account",
              "Create new local user. Select 'Without a Microsoft Account'. Set username and password same as Welcome Letter. Make administrator. Delete previous user account.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Computer: Remove MS OneDrive and McAfee",
              "Uninstall Microsoft OneDrive and McAfee from the computer.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Computer: Remove factory shortcuts from Desktop & Taskbar",
              "Remove unnecessary factory shortcuts not associated with company from Desktop and Taskbar (MS Edge, App Store, News, Cortana, etc.). Adjust unnecessary Taskbar tools to Hidden.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Computer: Set up all printers",
              "Install all needed printers. Refer to Printer Drivers in Google Drive for appropriate drivers.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: Adobe Reader",
              "Install Adobe Reader. IMPORTANT: Uncheck McAfee Offers during installation.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: Microsoft Office 365",
              "Sign in to activate Microsoft Office 365. Add Desktop and Taskbar shortcuts.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: Remote Desktop Service",
              "Install and configure Remote Desktop Service. In Program Settings, go to Local Resources Tab and save user info. Allow Open to Desktop. Add Desktop and Taskbar shortcuts.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: Google Chrome",
              "Install Google Chrome. Add Desktop and Taskbar shortcuts.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: Google Drive for Desktop",
              "Install Google Drive for Desktop. Add G-Drive shortcuts to Desktop as appropriate for the new user's position.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: Snipping Tool",
              "Install Snipping Tool and add Desktop and Taskbar shortcuts.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: Sticky Notes",
              "Install Sticky Notes and add Desktop and Taskbar shortcuts.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: Calculator",
              "Install Calculator and add Desktop and Taskbar shortcuts.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Personalize computer with company backgrounds",
              "Set company SF Backgrounds for Desktop background, Lock Screen, and Color theme.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Power Options: Set High Performance",
              "Set Power Options to High Performance. Configure Plugged In settings to Never sleep and Never turn off display.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Install: TeamViewer — unattended access",
              "Install TeamViewer and set up unattended access with company password. File location: Seamless IT > Software.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Yardi Matrix: Create user account",
              "Create new Yardi Matrix user account. Provide new user with password and login link.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("(Optional) Printer/Scan Folder Setup",
              "If required by position, configure printer scan-to-folder setup for the new user.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Update Windows and Drivers",
              "Run Windows Update fully and update all hardware drivers to latest versions.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Add new computer to Asset Inventory List",
              "Add the new computer with serial number, asset tag, and assigned user to the Asset Inventory tracking list.",
              "Computer Setup", EmployeeTaskType.Onboarding, sort += 10),
        });

        // Section: Cell Phone Setup
        templates.AddRange(new[]
        {
            T("Cell Phone: Sign in to Gmail",
              "Sign in new user to Gmail app on company cell phone.",
              "Cell Phone Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Cell Phone: Gmail — Mobile Signature",
              "Go to Gmail App > Settings > Mobile Signature. Copy signature from existing user at same location and comparable position. Paste and edit for new user.",
              "Cell Phone Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Cell Phone: Lock Screen & Wallpaper",
              "Go to Google Drive, download Cell Phone Pic3. Set image as both Lock Screen and Wallpaper.",
              "Cell Phone Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Cell Phone: Install company apps via Play Store",
              "Download and sign in to: SF Insight, Seamless Flooring app, Google Chat, Google Calendar (verify calendars show), Google Contacts (add seamlessflooring.org contacts for location and position), Instagram.",
              "Cell Phone Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Cell Phone: Configure Main Screen icons",
              "Move icons to bottom location in one row. Order: Call/Phone, Gmail, Chat, Contacts, Messages.",
              "Cell Phone Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Cell Phone: Configure Second Screen icon positions",
              "Top row: Play Store, Camera, Gallery, Visual Voicemail, Clock. 2nd row: G-Maps, Chrome, None, Instagram, Calculator. 5th row: SF Insight, SF Mobile, None, G-Calendar, G-Drive.",
              "Cell Phone Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Cell Phone: Add location contacts to Google Contacts",
              "Go to Google Contacts (Blue Contact Icon). Search seamlessflooring.org users and Add To Contacts for each user at that Location including Owners, Upper Management, IT, Corporate Office (HR, AP, AR), and other appropriate contacts.",
              "Cell Phone Setup", EmployeeTaskType.Onboarding, sort += 10),

            T("Cell Phone: Reset previous user settings to default",
              "Remove previous user's voicemail settings and restore any other changed settings to factory default.",
              "Cell Phone Setup", EmployeeTaskType.Onboarding, sort += 10),
        });

        // Section: Server & System Access
        templates.AddRange(new[]
        {
            T("Server access: Create user account",
              "Create user account on server using First Initial/Last Name format. Set password, configure Member Of groups, and set Sessions limits.",
              "Server & System Access", EmployeeTaskType.Onboarding, sort += 10),

            T("CompUFloor: Create user account",
              "Create CompUFloor user using First Initial/Last Name format. Set password, assign Warehouse/s access, and configure Permissions.",
              "Server & System Access", EmployeeTaskType.Onboarding, sort += 10),

            T("Insight: Create user account",
              "Create Insight user using First Initial/Last Name matching CompUFloor username. Configure location access and Permissions.",
              "Server & System Access", EmployeeTaskType.Onboarding, sort += 10),

            T("Spectrum/WordPress: Add new employee to website",
              "Add new employee to company website via WordPress/Spectrum. Use generic profile pic until official SF-attire photo is taken. Use local sports team logo for Fun Pic until real photo received. Leave Questionnaire blank for now.",
              "Server & System Access", EmployeeTaskType.Onboarding, sort += 10),

            T("Send website questionnaire & request photo",
              "Send new employee the Website Questionnaire Form to fill in. Request fun photo from employee. Ask local Branch Manager to take official photo in SF attire.",
              "Server & System Access", EmployeeTaskType.Onboarding, sort += 10),

            T("(If Sales) Add to CUF Salesman Master",
              "IF NEW SALES PERSON: Go to CUF > Maintenance > Salesman Master > F3 (check) > New > Enter Salesman Number and Name (first and last). Check Flat Percent > SAVE.",
              "Server & System Access", EmployeeTaskType.Onboarding, sort += 10),
        });

        // Section: Help Desk
        templates.AddRange(new[]
        {
            T("Help Desk: Create account and activate",
              "Add new user to Help Desk system (ServiceSphere) and activate their account with appropriate role and permissions.",
              "Help Desk", EmployeeTaskType.Onboarding, sort += 10),

            T("Help Desk: Send welcome email to new user",
              "Send 'Welcome to Our New SF Help Desk' email from ITSupport email address using the itsupport Template Layout.",
              "Help Desk", EmployeeTaskType.Onboarding, sort += 10),
        });

        // Section: Final Notifications & Wrap-Up
        templates.AddRange(new[]
        {
            T("Send credentials email to new user and manager",
              "Send User Important: Credentials Email to new employee. Include Manager and Trainer on the email.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Send company announcement email",
              "Send new employee location-specific announcement email to all@[domain]. Use location-specific template.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Add to G-Drive Employee Directory",
              "Add new employee to the G-Drive SF Employee Directory (Seamless Flooring Sales location). Update the Last Update Date. Share with new employee and notify them.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Schedule Fun Questions email for hire date",
              "Schedule the Fun Questions introduction email to be sent on the employee's official hire date.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Arrange company photo in SF attire",
              "Schedule photo of new employee in orange shirt for company website and business cards.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Replace website profile pic with actual photo",
              "Once official SF-attire photo is received, replace the generic/sports team placeholder on the company website.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Create email signature design in Canva",
              "Create email signature design in Canva (use no-photo template until official photo is taken).",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Create email profile photo",
              "Create and set the new user's email profile photo.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Add hardware to Location IT Inventory Tracker",
              "Record Laptop Name, Monitor information, and Mouse in the Location IT Inventory Tracker.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Configure Insight, CUF location access",
              "Grant Insight and CUF location-appropriate access. If Sales position, create customer portal login.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Grant access to Training Videos",
              "Provide access to Training Videos per Manager's request.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Update Verizon account",
              "Update Verizon business account with new employee's device and line information.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("Set up Favorite Contacts",
              "Go to Google Contacts in browser and save key contacts as Favorites. If iPhone: open Mail app > Google > enable Contacts sync.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),

            T("(If Requested) Create Business Cards",
              "Initiate business card order if requested by manager or new employee.",
              "Final Steps", EmployeeTaskType.Onboarding, sort += 10),
        });

        // Section: Tablet & Phone (generic for any device)
        templates.AddRange(new[]
        {
            T("Tablet: Full device setup",
              "Complete tablet setup — sign in with company account, install required apps, configure settings per company standards for employee's position.",
              "Tablet & Phone", EmployeeTaskType.Onboarding, sort += 10),

            T("Phone: Set up voicemail",
              "Configure voicemail on company phone with professional greeting following company standards.",
              "Tablet & Phone", EmployeeTaskType.Onboarding, sort += 10),

            T("Phone: Install Google Drive",
              "Download and sign in to Google Drive app on company phone.",
              "Tablet & Phone", EmployeeTaskType.Onboarding, sort += 10),

            T("Phone: Install Google Chat",
              "Download and sign in to Google Chat app on company phone.",
              "Tablet & Phone", EmployeeTaskType.Onboarding, sort += 10),

            T("Phone: Set up Google Contacts — location favorites",
              "Download Google Contacts and save location-specific contacts as favorites.",
              "Tablet & Phone", EmployeeTaskType.Onboarding, sort += 10),
        });

        // ─── OFFBOARDING ───────────────────────────────────────────────────────

        sort = 0;

        // Section: Pre-Departure Preparation
        templates.AddRange(new[]
        {
            T("Notify IT of departure date",
              "Confirm official last working day with HR. Schedule all offboarding tasks relative to that date. Alert IT team at least 1 week before if possible.",
              "Pre-Departure", EmployeeTaskType.Offboarding, sort += 10),

            T("Conduct exit interview",
              "Schedule and complete exit interview with HR. Document feedback for process improvement.",
              "Pre-Departure", EmployeeTaskType.Offboarding, sort += 10),

            T("Document knowledge transfer",
              "Ensure departing employee documents open projects, key contacts, ongoing work, and any institutional knowledge. Transfer to manager or successor.",
              "Pre-Departure", EmployeeTaskType.Offboarding, sort += 10),

            T("Identify data and files to transfer",
              "Identify all business-critical files, email threads, documents, and Drive folders to be transferred to manager or team.",
              "Pre-Departure", EmployeeTaskType.Offboarding, sort += 10),
        });

        // Section: Google Account Offboarding
        templates.AddRange(new[]
        {
            T("Google Workspace: Transfer Drive files to manager",
              "Transfer Google Drive files to manager or designated successor. Ensure all business-critical documents are accessible.",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10),

            T("Google Workspace: Set up email auto-reply",
              "Configure Out of Office auto-reply on the departing employee's Gmail indicating their last day and who to contact going forward.",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10),

            T("Google Workspace: Forward email to manager",
              "Set up email forwarding to manager or designated contact. Determine duration (typically 30–90 days).",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10),

            T("Google Workspace: Remove from all Groups",
              "Remove departing employee from all Google Groups (All@, location groups, Concur group, and any other assigned groups).",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10),

            T("Google Workspace: Remove from shared Calendars",
              "Remove employee from all shared Location Calendars and company-wide calendars.",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10),

            T("Google Workspace: Remove from Chat Spaces",
              "Remove employee from all Google Chat Spaces they are a member of.",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10),

            T("Google Workspace: Archive/export mailbox data",
              "Export a copy of the employee's mailbox data if required for compliance or business continuity. Store in designated archive location.",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10),

            T("Google Workspace: Suspend user account",
              "Suspend (not delete) the user account in Google Admin Console on last day of employment. Do not delete immediately — retain for minimum 30 days.",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10),

            T("Google Workspace: Delete user account (30-day hold)",
              "After 30-day data retention period, permanently delete user account from Google Admin Console.",
              "Google Account", EmployeeTaskType.Offboarding, sort += 10, DueInDays: 30),
        });

        // Section: Hardware Return
        templates.AddRange(new[]
        {
            T("Collect: Laptop and Charger",
              "Retrieve company laptop and charger from departing employee. Verify using Asset Inventory list.",
              "Hardware Return", EmployeeTaskType.Offboarding, sort += 10),

            T("Collect: Cell Phone and Charger",
              "Retrieve company cell phone and charger from departing employee.",
              "Hardware Return", EmployeeTaskType.Offboarding, sort += 10),

            T("Collect: Cell Phone Protective Case",
              "Retrieve cell phone protective case from departing employee.",
              "Hardware Return", EmployeeTaskType.Offboarding, sort += 10),

            T("Collect: Tablet, Cover and Charger",
              "Retrieve company tablet, protective cover, and charger from departing employee.",
              "Hardware Return", EmployeeTaskType.Offboarding, sort += 10),

            T("Collect: Disto and any other equipment",
              "Retrieve Disto, mouse, keyboard, monitor cables, and any other company-issued equipment.",
              "Hardware Return", EmployeeTaskType.Offboarding, sort += 10),

            T("Collect: Keys, badges, access cards",
              "Retrieve all physical keys, building access badges, and access cards from departing employee.",
              "Hardware Return", EmployeeTaskType.Offboarding, sort += 10),

            T("Employee signs Physical Asset Return Form",
              "Have departing employee sign the Physical Asset Return Form from G-Drive (Seamless Flooring Office location). Document any missing items.",
              "Hardware Return", EmployeeTaskType.Offboarding, sort += 10),
        });

        // Section: Computer Offboarding
        templates.AddRange(new[]
        {
            T("Computer: Upload all files to G-Drive",
              "Ensure all local files are backed up to Google Drive before wiping the machine.",
              "Computer Offboarding", EmployeeTaskType.Offboarding, sort += 10),

            T("Computer: Remove employee user account",
              "Remove or disable the departing employee's local Windows user account from the computer.",
              "Computer Offboarding", EmployeeTaskType.Offboarding, sort += 10),

            T("Computer: Remove from TeamViewer",
              "Remove or rename the computer in TeamViewer. Revoke the employee's individual TeamViewer access if applicable.",
              "Computer Offboarding", EmployeeTaskType.Offboarding, sort += 10),

            T("Computer: Factory reset or reassign prep",
              "If reassigning to new employee: run Recovery (Clean Drive and Keep Files). If retiring: factory reset and document disposal.",
              "Computer Offboarding", EmployeeTaskType.Offboarding, sort += 10),

            T("Update Asset Inventory — computer status",
              "Update the Asset Inventory list to mark computer as Available, In Repair, or Reassigned. Document wipe/reset performed.",
              "Computer Offboarding", EmployeeTaskType.Offboarding, sort += 10),
        });

        // Section: System Access Removal
        templates.AddRange(new[]
        {
            T("Server: Disable or remove user account",
              "Disable the departing employee's server account (First Initial/Last Name). Remove from Member Of groups and terminate active sessions.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),

            T("CompUFloor: Disable user account",
              "Disable or remove CompUFloor user account. Document warehouses and permissions that were assigned.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),

            T("Insight/Spectrum: Disable user account",
              "Disable Insight user account. Remove location access and permissions.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),

            T("Yardi Matrix: Disable user account",
              "Disable the departing employee's Yardi Matrix account and revoke login access.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),

            T("Remote Desktop: Revoke access",
              "Remove employee's Remote Desktop access permissions from all configured servers or workstations.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),

            T("Microsoft Office 365: Deactivate license",
              "Sign out all active Office 365 sessions. Reassign the license to another user or release it.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),

            T("(If Sales) Remove from CUF Salesman Master",
              "IF SALES PERSON: Go to CUF > Maintenance > Salesman Master and deactivate or remove the salesman record. Close any open customer portal login.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),

            T("SAP Concur: Deactivate account",
              "Deactivate departing employee's SAP Concur expense account. Ensure all outstanding expenses are submitted and approved.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),

            T("Paychex: Process termination",
              "Process termination in Paychex payroll system. Ensure final paycheck and any accrued PTO is processed per company policy.",
              "System Access", EmployeeTaskType.Offboarding, sort += 10),
        });

        // Section: Help Desk Offboarding
        templates.AddRange(new[]
        {
            T("Help Desk: Archive or deactivate account",
              "Archive or deactivate the employee's ServiceSphere Help Desk account. Reassign any open tickets to another agent.",
              "Help Desk", EmployeeTaskType.Offboarding, sort += 10),

            T("Help Desk: Reassign open tickets",
              "Review all open tickets assigned to the departing employee. Reassign to appropriate agents or the manager.",
              "Help Desk", EmployeeTaskType.Offboarding, sort += 10),
        });

        // Section: Software Licenses
        templates.AddRange(new[]
        {
            T("Review and reassign software licenses",
              "Review all software licenses assigned to the departing employee. Reassign to other users or release unused licenses.",
              "Licenses & Subscriptions", EmployeeTaskType.Offboarding, sort += 10),

            T("Revoke VPN and remote access",
              "Revoke all VPN credentials and remote access permissions for the departing employee.",
              "Licenses & Subscriptions", EmployeeTaskType.Offboarding, sort += 10),
        });

        // Section: Communications & Final Steps
        templates.AddRange(new[]
        {
            T("Update Verizon account — remove or transfer line",
              "Update company Verizon account. Transfer phone line to another employee or cancel per company policy.",
              "Final Steps", EmployeeTaskType.Offboarding, sort += 10),

            T("Remove from company website",
              "Remove departing employee's profile from the company website (Spectrum/WordPress staff directory).",
              "Final Steps", EmployeeTaskType.Offboarding, sort += 10),

            T("Remove from email distribution lists",
              "Remove employee from all location and department-specific email distribution lists.",
              "Final Steps", EmployeeTaskType.Offboarding, sort += 10),

            T("Notify clients/contacts of departure (if customer-facing)",
              "If the employee had direct client relationships, notify key clients of the transition and introduce their new point of contact.",
              "Final Steps", EmployeeTaskType.Offboarding, sort += 10),

            T("Update G-Drive Employee Directory",
              "Remove or mark as inactive in the G-Drive SF Employee Directory. Update Last Update Date.",
              "Final Steps", EmployeeTaskType.Offboarding, sort += 10),

            T("Cancel or redirect business cards (if applicable)",
              "Cancel any pending business card orders. If cards were recently printed, retrieve and shred.",
              "Final Steps", EmployeeTaskType.Offboarding, sort += 10),

            T("Final: Confirm all access has been revoked",
              "Perform a final audit to confirm all system accounts, physical access, and software licenses have been deactivated or reassigned.",
              "Final Steps", EmployeeTaskType.Offboarding, sort += 10),
        });

        context.EmployeeTaskTemplates.AddRange(templates);
        context.SaveChanges();
        Console.WriteLine($"[DbInitializer] Seeded {templates.Count} employee task templates.");
    }

    private static EmployeeTaskTemplate T(
        string title,
        string description,
        string category,
        EmployeeTaskType taskType,
        int sortOrder,
        int? DueInDays = null)
        => new()
        {
            Title                = title,
            Description          = description,
            Category             = category,
            TaskType             = taskType,
            SortOrder            = sortOrder,
            DueInDays            = DueInDays,
            IsActive             = true,
            CreatedDate          = DateTime.UtcNow,
            UpdatedDate          = DateTime.UtcNow,
        };

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

        // Seed Store AppSettings
        // Ensure the Timezone setting exists (IANA ID used for display conversion)
        if (!context.AppSettings.Any(s => s.Key == "Timezone"))
        {
            context.AppSettings.Add(new AppSetting
            {
                Key         = "Timezone",
                Value       = "UTC",
                Category    = "General",
                Description = "IANA timezone ID used when displaying dates (e.g. America/Los_Angeles, America/New_York)."
            });
        }

        var storeSettings = new[]
        {
            ("StoreEnabled",        "false",    "Store", "Enables or disables the quarterly store for portal users."),
            ("StoreOpenDate",       "",         "Store", "Date and time the store opens (UTC, format: yyyy-MM-ddTHH:mm)."),
            ("StoreCloseDate",      "",         "Store", "Date and time the store closes (UTC, format: yyyy-MM-ddTHH:mm)."),
            ("StoreWelcomeMessage", "Welcome to the company store! Place your supply orders below.", "Store",
                "Message shown to users at the top of the store catalog."),
        };

        foreach (var (key, value, category, description) in storeSettings)
        {
            if (!context.AppSettings.Any(s => s.Key == key))
            {
                context.AppSettings.Add(new AppSetting
                {
                    Key         = key,
                    Value       = value,
                    Category    = category,
                    Description = description
                });
            }
        }

        // Seed StoreOrderStatusUpdate email template
        if (!context.EmailTemplates.Any(t => t.Key == "StoreOrderStatusUpdate"))
        {
            context.EmailTemplates.Add(new EmailTemplate
            {
                Key             = "StoreOrderStatusUpdate",
                Name            = "Store Order Status Update",
                Description     = "Sent to a portal user when the operations team updates their order status.",
                SubjectTemplate = "Order Update — {{OrderNumber}} is now {{NewStatus}}",
                BodyTemplate    =
                    "<h3>Order Status Update</h3>" +
                    "<p>Hi {{RecipientName}},</p>" +
                    "<p>{{StatusMessage}}</p>" +
                    "<table style='width:100%;border-collapse:collapse;margin:15px 0;'>" +
                    "<tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:130px;'>Order #</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{{OrderNumber}}</td></tr>" +
                    "<tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Quarter</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{{Quarter}}</td></tr>" +
                    "<tr><td style='padding:8px;font-weight:bold;'>New Status</td><td style='padding:8px;'><strong>{{NewStatus}}</strong></td></tr>" +
                    "</table>" +
                    "<p style='color:#6b7280;font-size:13px;margin-top:20px;'>If you have questions about your order, please contact your operations department.</p>",
                IsActive    = true,
                UpdatedDate = DateTime.UtcNow
            });
        }

        // Seed StoreOrderConfirmation email template
        if (!context.EmailTemplates.Any(t => t.Key == "StoreOrderConfirmation"))
        {
            context.EmailTemplates.Add(new EmailTemplate
            {
                Key             = "StoreOrderConfirmation",
                Name            = "Store Order Confirmation",
                Description     = "Sent to a portal user after they successfully place a store order.",
                SubjectTemplate = "Order Confirmation — {{OrderNumber}}",
                BodyTemplate    =
                    "<h3>Order Confirmed — {{OrderNumber}}</h3>" +
                    "<p>Hi {{RecipientName}},</p>" +
                    "<p>Your order has been received. The operations team will review and process it shortly.</p>" +
                    "<table style='width:100%;border-collapse:collapse;margin:15px 0;'>" +
                    "<tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;width:130px;'>Order #</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{{OrderNumber}}</td></tr>" +
                    "<tr><td style='padding:8px;border-bottom:1px solid #e5e7eb;font-weight:bold;'>Date</td><td style='padding:8px;border-bottom:1px solid #e5e7eb;'>{{OrderDate}}</td></tr>" +
                    "<tr><td style='padding:8px;font-weight:bold;'>Quarter</td><td style='padding:8px;'>{{Quarter}}</td></tr>" +
                    "</table>" +
                    "<h4 style='margin-top:20px;'>Items Ordered</h4>" +
                    "{{OrderItemsHtml}}" +
                    "<p style='color:#6b7280;font-size:13px;margin-top:20px;'>If you have questions about your order, please contact your operations department.</p>",
                IsActive    = true,
                UpdatedDate = DateTime.UtcNow
            });
        }

        context.SaveChanges();
    }
}
