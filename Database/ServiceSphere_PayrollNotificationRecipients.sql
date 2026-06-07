-- ============================================================================
-- ServiceSphere — Payroll Notification Recipients
-- ----------------------------------------------------------------------------
-- Curated list of users (portal users and/or external emails) who should
-- receive the "contractor receipt submitted" notification. When this table
-- is empty, the notification falls back to every active Admin user.
-- ----------------------------------------------------------------------------
-- Run on the same database that already hosts dbo.PortalUsers and
-- dbo.PayrollReceipts. Safe to re-run — the IF NOT EXISTS guard prevents
-- duplicate-create errors.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollNotificationRecipients' AND schema_id = SCHEMA_ID('dbo'))
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

    PRINT 'Created dbo.PayrollNotificationRecipients.';
END
ELSE
BEGIN
    PRINT 'dbo.PayrollNotificationRecipients already exists — skipped.';
END
GO
