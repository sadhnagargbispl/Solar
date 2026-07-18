/* ============================================================================
   ADD-UserPanelColumns.sql
   ----------------------------------------------------------------------------
   Run ONCE against the live Solar DB (SSMS) after deploying the user-panel
   changes. Idempotent — safe to re-run; each column is added only if missing.

   Adds the columns backing:
     • Task 3  — Light Bill ownership (self / blood-relation + proof)
     • Task 7  — PM Surya Ghar Loan / Without-Loan selection
     • Task 11 — PM Surya Ghar ID no. + admin-uploaded downloadable documents

   No EF migration is generated (per project workflow); EF Core maps these
   columns by convention at runtime once they exist.
   ============================================================================ */

SET NOCOUNT ON;

/* ---- SolarRequests ------------------------------------------------------- */
IF COL_LENGTH('dbo.SolarRequests', 'LightBillOwnership') IS NULL
    ALTER TABLE dbo.SolarRequests ADD LightBillOwnership NVARCHAR(50) NULL;

IF COL_LENGTH('dbo.SolarRequests', 'LightBillRelationName') IS NULL
    ALTER TABLE dbo.SolarRequests ADD LightBillRelationName NVARCHAR(200) NULL;

IF COL_LENGTH('dbo.SolarRequests', 'LightBillProofPath') IS NULL
    ALTER TABLE dbo.SolarRequests ADD LightBillProofPath NVARCHAR(500) NULL;

IF COL_LENGTH('dbo.SolarRequests', 'PMSuryaLoanOption') IS NULL
    ALTER TABLE dbo.SolarRequests ADD PMSuryaLoanOption NVARCHAR(50) NULL;

IF COL_LENGTH('dbo.SolarRequests', 'PMSuryaGharIdNo') IS NULL
    ALTER TABLE dbo.SolarRequests ADD PMSuryaGharIdNo NVARCHAR(100) NULL;

/* ---- PMDocuments --------------------------------------------------------- */
IF COL_LENGTH('dbo.PMDocuments', 'IsAdminUpload') IS NULL
    ALTER TABLE dbo.PMDocuments ADD IsAdminUpload BIT NOT NULL CONSTRAINT DF_PMDocuments_IsAdminUpload DEFAULT(0);

PRINT 'User-panel columns are present (added where missing).';
