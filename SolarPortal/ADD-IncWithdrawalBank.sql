/*
    Adds structured bank fields to the existing IncWithdrawals table.

    Until now the withdrawal form stored everything in one free-text
    BankDetails blob. The INC worker now picks his bank from the legacy
    M_BankMaster list and types IFSC / branch / account no separately, so
    each needs its own column and each shows as its own column in the
    withdrawal report.

    BankDetails is left in place - old rows keep their text and the column
    is still written with a readable summary.

    Safe to run more than once.
*/

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.IncWithdrawals') AND name = 'BankName')
    ALTER TABLE dbo.IncWithdrawals ADD BankName nvarchar(200) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.IncWithdrawals') AND name = 'IFSCode')
    ALTER TABLE dbo.IncWithdrawals ADD IFSCode nvarchar(30) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.IncWithdrawals') AND name = 'BranchName')
    ALTER TABLE dbo.IncWithdrawals ADD BranchName nvarchar(150) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.IncWithdrawals') AND name = 'AccountNo')
    ALTER TABLE dbo.IncWithdrawals ADD AccountNo nvarchar(50) NULL;
GO
