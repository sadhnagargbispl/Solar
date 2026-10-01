/* ============================================================================
   ADD-IncUploadFormat.sql  —  fixed INC TEAM PHOTO UPLOAD FORMAT
   ----------------------------------------------------------------------------
   Spec: "INC upload format ye fixed hai, isko ek table me save kar lo. INC panel
   par ye pura fill hoga tabhi INC commission de sakta hai. Jisme video ho wo
   video nahi hai to nahi kar sakta."

   Flow:
     • dbo.IncUploadFormats            — the 13 fixed lines of the sheet (seeded below).
     • dbo.InstallationPhotos.FormatItemId — which line a photo was uploaded for.
     • dbo.InstallationChecklistEntries — the video / typed detail per line.
     • INC panel (InstallationController) refuses Mark Complete / Re-submit until
       every line is filled, and holds the INC commission until it is.

   Idempotent — safe to run more than once. No EF migration is generated.
   ========================================================================== */
SET NOCOUNT ON;
GO

/* ---- master: the fixed format ------------------------------------------- */
IF OBJECT_ID('dbo.IncUploadFormats', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.IncUploadFormats
    (
        Id                 INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_IncUploadFormats PRIMARY KEY,
        SrNo               INT            NOT NULL,
        Work               NVARCHAR(300)  NOT NULL,
        PhotoRequired      BIT            NOT NULL CONSTRAINT DF_IncUploadFormats_Photo   DEFAULT(0),
        VideoCount         INT            NOT NULL CONSTRAINT DF_IncUploadFormats_Video   DEFAULT(0),
        MinPhotos          INT            NOT NULL CONSTRAINT DF_IncUploadFormats_Min     DEFAULT(0),
        MaxPhotos          INT            NOT NULL CONSTRAINT DF_IncUploadFormats_Max     DEFAULT(0),
        RemarkRequired     BIT            NOT NULL CONSTRAINT DF_IncUploadFormats_Remark  DEFAULT(0),
        IsActive           BIT            NOT NULL CONSTRAINT DF_IncUploadFormats_Active  DEFAULT(1),

        /* BaseEntity columns — same shape as every other table in this schema. */
        CreatedAt          DATETIME2      NOT NULL CONSTRAINT DF_IncUploadFormats_CreatedAt DEFAULT(GETUTCDATE()),
        CreatedBy          NVARCHAR(450)  NULL,
        UpdatedAt          DATETIME2      NULL,
        UpdatedBy          NVARCHAR(450)  NULL,
        IsDeleted          BIT            NOT NULL CONSTRAINT DF_IncUploadFormats_IsDeleted DEFAULT(0)
    );
    CREATE UNIQUE INDEX UX_IncUploadFormats_SrNo ON dbo.IncUploadFormats(SrNo) WHERE IsDeleted = 0;
    PRINT 'Created dbo.IncUploadFormats.';
END
ELSE PRINT 'dbo.IncUploadFormats already exists.';
GO

/* Seed — the "INC TEAM PHOTO UPLOAD FORMAT.xlsx" sheet, Work text in English:
   Sr.No | Work | Photo | Vedio | Mini No. of Photo | Max No. of Photo | Remark */
IF NOT EXISTS (SELECT 1 FROM dbo.IncUploadFormats)
BEGIN
    INSERT INTO dbo.IncUploadFormats (SrNo, Work, PhotoRequired, VideoCount, MinPhotos, MaxPhotos, RemarkRequired)
    VALUES
        ( 1, N'Serial numbers of all panels',                                          1, 0, 1, 20, 0),
        ( 2, N'Inverter from both the left and right sides, with the serial number',   1, 0, 1,  2, 0),
        ( 3, N'Geo-tagged photo of the plant with the customer',                       1, 0, 1,  1, 0),
        ( 4, N'Geo-tagged photo of the inverter ACDB & DCDB',                          1, 0, 1,  1, 0),
        ( 5, N'Geo-tagged photo of the lightning arrester with the lug fitted',        1, 0, 1,  1, 0),
        ( 6, N'Geo-tagged photo of the inverter body earthing with the lug fitted',    1, 0, 1,  1, 0),
        ( 7, N'Earthing photos',                                                       1, 0, 1,  2, 0),
        ( 8, N'Foundation photos',                                                     1, 0, 1, 10, 0),
        ( 9, N'Photo of the inverter switched on (live)',                              1, 0, 1,  1, 0),
        (10, N'AC cable details',                                                      0, 0, 0,  0, 1),
        (11, N'DC wire details',                                                       0, 0, 0,  0, 1),
        (12, N'Earthing wire details',                                                 0, 0, 0,  0, 1),
        (13, N'One full video of the structure and wiring route, with the welding clearly visible', 1, 1, 0, 0, 0);
    PRINT 'Seeded dbo.IncUploadFormats (13 rows).';
END
GO

/* The first version of this script seeded the Work text in Hinglish. Switch
   those rows to the English text above — only rows still holding the old text
   are touched, so an admin's later edit is never overwritten. */
UPDATE f SET Work = v.NewWork, UpdatedAt = GETUTCDATE()
FROM dbo.IncUploadFormats f
JOIN (VALUES
    ( 1, N'ALL PANEL SERIAL NUMBER KI',                                   N'Serial numbers of all panels'),
    ( 2, N'INVERTER KI LEFT RIGHT DONO SIDE SE WITH SERIAL NUMBER PHOTO', N'Inverter from both the left and right sides, with the serial number'),
    ( 3, N'PLANT KI GEO TAG PHOTO WITH CUSTMER',                          N'Geo-tagged photo of the plant with the customer'),
    ( 4, N'INVERTER ACDB & DCDB KI GEO TAG PHOTO',                        N'Geo-tagged photo of the inverter ACDB & DCDB'),
    ( 5, N'LIGHTING ARRESTER KI LUG LAGE HUI GEO TAG PHOTO',              N'Geo-tagged photo of the lightning arrester with the lug fitted'),
    ( 6, N'INVERTER KI BODY EARTHING KI LUG LAGE HUE GEO TAG PHOTO',      N'Geo-tagged photo of the inverter body earthing with the lug fitted'),
    ( 7, N'EARTHING KI PHOTOS',                                           N'Earthing photos'),
    ( 8, N'FOUNDATION KI PHOTO',                                          N'Foundation photos'),
    ( 9, N'INVERTER LIVE ON KRNE KI PHOTO',                               N'Photo of the inverter switched on (live)'),
    (10, N'AC CABEL KI DETAIL',                                           N'AC cable details'),
    (11, N'DC WIRE KI DETAIL',                                            N'DC wire details'),
    (12, N'EARTHING WIRE KI DETAIL',                                      N'Earthing wire details'),
    (13, N'EK FULL VIDEO STRUCTURE AND WIRING ROUTE KA JISME VAILDING PROPER DIKHNA CHAHIYE',
         N'One full video of the structure and wiring route, with the welding clearly visible')
) v(SrNo, OldWork, NewWork) ON v.SrNo = f.SrNo AND f.Work = v.OldWork;
PRINT CONCAT('Switched ', @@ROWCOUNT, ' upload-format line(s) to English.');
GO

/* ---- photos: which line each photo belongs to --------------------------- */
IF COL_LENGTH('dbo.InstallationPhotos', 'FormatItemId') IS NULL
BEGIN
    ALTER TABLE dbo.InstallationPhotos ADD FormatItemId INT NULL;
    PRINT 'Added dbo.InstallationPhotos.FormatItemId.';
END
GO

/* ---- video + typed detail per line -------------------------------------- */
IF OBJECT_ID('dbo.InstallationChecklistEntries', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InstallationChecklistEntries
    (
        Id                 INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_InstallationChecklistEntries PRIMARY KEY,
        InstallationId     INT            NOT NULL,
        SolarRequestId     INT            NOT NULL,
        FormatItemId       INT            NOT NULL,
        EntryType          INT            NOT NULL,   /* 2 = Video, 3 = Remark (detail text) */
        FilePath           NVARCHAR(500)  NULL,
        FileName           NVARCHAR(255)  NULL,
        ContentType        NVARCHAR(100)  NULL,
        FileSizeBytes      BIGINT         NOT NULL CONSTRAINT DF_InstallationChecklistEntries_Size DEFAULT(0),
        RemarkText         NVARCHAR(1000) NULL,
        UploadedByWorkerId INT            NULL,

        CreatedAt          DATETIME2      NOT NULL CONSTRAINT DF_InstallationChecklistEntries_CreatedAt DEFAULT(GETUTCDATE()),
        CreatedBy          NVARCHAR(450)  NULL,
        UpdatedAt          DATETIME2      NULL,
        UpdatedBy          NVARCHAR(450)  NULL,
        IsDeleted          BIT            NOT NULL CONSTRAINT DF_InstallationChecklistEntries_IsDeleted DEFAULT(0),

        CONSTRAINT FK_InstallationChecklistEntries_Installations
            FOREIGN KEY (InstallationId) REFERENCES dbo.Installations(Id) ON DELETE CASCADE,
        CONSTRAINT FK_InstallationChecklistEntries_IncUploadFormats
            FOREIGN KEY (FormatItemId) REFERENCES dbo.IncUploadFormats(Id)
    );
    CREATE INDEX IX_InstallationChecklistEntries_InstallationId ON dbo.InstallationChecklistEntries(InstallationId);
    PRINT 'Created dbo.InstallationChecklistEntries.';
END
ELSE PRINT 'dbo.InstallationChecklistEntries already exists.';
GO

/* Handy checks
SELECT * FROM dbo.IncUploadFormats ORDER BY SrNo;
SELECT FormatItemId, COUNT(*) FROM dbo.InstallationPhotos WHERE InstallationId = <id> AND IsDeleted = 0 GROUP BY FormatItemId;
SELECT * FROM dbo.InstallationChecklistEntries WHERE InstallationId = <id> AND IsDeleted = 0;
*/
