-- ============================================================================
-- ServiceSphere — Payroll Lifecycle Upgrade
-- ----------------------------------------------------------------------------
-- Adds:
--   1. Payment-method / reference / confirmation columns on PayrollReceipts.
--   2. LastReminderSentUtc column for the stale-Submitted reminder job.
--   3. PayrollReceiptComments table for the activity / discussion thread.
-- ----------------------------------------------------------------------------
-- Safe to re-run — every column add and table create is guarded.
-- ============================================================================

-- ── 1. New columns on PayrollReceipts ───────────────────────────────────────
IF COL_LENGTH('dbo.PayrollReceipts', 'PaymentMethod') IS NULL
BEGIN
    ALTER TABLE dbo.PayrollReceipts ADD PaymentMethod NVARCHAR(50) NULL;
    PRINT 'Added PayrollReceipts.PaymentMethod';
END

IF COL_LENGTH('dbo.PayrollReceipts', 'PaymentReference') IS NULL
BEGIN
    ALTER TABLE dbo.PayrollReceipts ADD PaymentReference NVARCHAR(200) NULL;
    PRINT 'Added PayrollReceipts.PaymentReference';
END

IF COL_LENGTH('dbo.PayrollReceipts', 'PaymentConfirmedDate') IS NULL
BEGIN
    ALTER TABLE dbo.PayrollReceipts ADD PaymentConfirmedDate DATETIME2(7) NULL;
    PRINT 'Added PayrollReceipts.PaymentConfirmedDate';
END

IF COL_LENGTH('dbo.PayrollReceipts', 'PaymentConfirmedNote') IS NULL
BEGIN
    ALTER TABLE dbo.PayrollReceipts ADD PaymentConfirmedNote NVARCHAR(500) NULL;
    PRINT 'Added PayrollReceipts.PaymentConfirmedNote';
END

IF COL_LENGTH('dbo.PayrollReceipts', 'LastReminderSentUtc') IS NULL
BEGIN
    ALTER TABLE dbo.PayrollReceipts ADD LastReminderSentUtc DATETIME2(7) NULL;
    PRINT 'Added PayrollReceipts.LastReminderSentUtc';
END
GO

-- ── 2. PayrollReceiptComments table ─────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollReceiptComments' AND schema_id = SCHEMA_ID('dbo'))
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

    PRINT 'Created dbo.PayrollReceiptComments.';
END
ELSE
BEGIN
    PRINT 'dbo.PayrollReceiptComments already exists — skipped.';
END
GO

-- ── 3. AppSettings keys for the stale-Submitted reminder ────────────────────
IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PayrollReminderEnabled')
BEGIN
    INSERT INTO dbo.AppSettings ([Key], [Value]) VALUES ('PayrollReminderEnabled', 'false');
    PRINT 'Seeded PayrollReminderEnabled = false';
END

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'PayrollReminderDays')
BEGIN
    INSERT INTO dbo.AppSettings ([Key], [Value]) VALUES ('PayrollReminderDays', '3');
    PRINT 'Seeded PayrollReminderDays = 3';
END
GO
