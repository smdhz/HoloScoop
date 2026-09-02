SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Streams', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Streams
    (
        Id           bigint IDENTITY(1, 1) NOT NULL,
        Platform     nvarchar(32)           NOT NULL,
        ExternalId   nvarchar(128)          NOT NULL,
        ChannelId    nvarchar(128)          NULL,
        ChannelName  nvarchar(256)          NULL,
        Title        nvarchar(512)          NOT NULL,
        Description  nvarchar(max)          NULL,
        SourceUrl    nvarchar(2048)         NOT NULL,
        ThumbnailUrl nvarchar(2048)         NULL,
        ScheduledAt  datetimeoffset(3)      NULL,
        StartedAt    datetimeoffset(3)      NULL,
        EndedAt      datetimeoffset(3)      NULL,
        DurationMs   bigint                 NULL,
        CreatedAt    datetimeoffset(3)      NOT NULL
            CONSTRAINT DF_Streams_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt    datetimeoffset(3)      NOT NULL
            CONSTRAINT DF_Streams_UpdatedAt DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_Streams PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_Streams_DurationMs CHECK (DurationMs IS NULL OR DurationMs >= 0)
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Streams')
      AND name = N'UX_Streams_Platform_ExternalId'
)
BEGIN
    CREATE UNIQUE INDEX UX_Streams_Platform_ExternalId
        ON dbo.Streams (Platform, ExternalId);
END;

IF OBJECT_ID(N'dbo.Tasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Tasks
    (
        Id               bigint IDENTITY(1, 1) NOT NULL,
        RedisStream      nvarchar(256)          NOT NULL,
        RedisMessageId   nvarchar(64)           NOT NULL,
        StreamId         bigint                 NOT NULL,
        DownloadMode     varchar(32)            NULL,
        Status           varchar(32)            NOT NULL,
        ExpiresAt        datetimeoffset(3)       NULL,
        AttemptCount     int                     NOT NULL
            CONSTRAINT DF_Tasks_AttemptCount DEFAULT 0,
        LastError        nvarchar(max)           NULL,
        CreatedAt        datetimeoffset(3)       NOT NULL
            CONSTRAINT DF_Tasks_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt        datetimeoffset(3)       NOT NULL
            CONSTRAINT DF_Tasks_UpdatedAt DEFAULT SYSUTCDATETIME(),
        RowVersion       rowversion              NOT NULL,

        CONSTRAINT PK_Tasks PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_Tasks_Streams_StreamId FOREIGN KEY (StreamId)
            REFERENCES dbo.Streams (Id),
        CONSTRAINT CK_Tasks_DownloadMode CHECK
        (
            DownloadMode IS NULL
            OR DownloadMode IN ('VideoAndSubtitles', 'SubtitlesOnly')
        ),
        CONSTRAINT CK_Tasks_Status CHECK
        (
            Status IN
            (
                'PendingSelection',
                'Queued',
                'Downloading',
                'ParsingSubtitles',
                'Diarizing',
                'Indexing',
                'Completed',
                'Failed',
                'Expired'
            )
        ),
        CONSTRAINT CK_Tasks_AttemptCount CHECK (AttemptCount >= 0)
    );
END;

IF EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.Tasks')
      AND name = N'CK_Tasks_Status'
)
BEGIN
    ALTER TABLE dbo.Tasks DROP CONSTRAINT CK_Tasks_Status;
END;

ALTER TABLE dbo.Tasks WITH CHECK ADD CONSTRAINT CK_Tasks_Status CHECK
(
    Status IN
    (
        'PendingSelection',
        'Queued',
        'Downloading',
        'ParsingSubtitles',
        'Diarizing',
        'Indexing',
        'Completed',
        'Failed',
        'Expired'
    )
);

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Tasks')
      AND name = N'UX_Tasks_RedisStream_RedisMessageId'
)
BEGIN
    CREATE UNIQUE INDEX UX_Tasks_RedisStream_RedisMessageId
        ON dbo.Tasks (RedisStream, RedisMessageId);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Tasks')
      AND name = N'IX_Tasks_Status_ExpiresAt'
)
BEGIN
    CREATE INDEX IX_Tasks_Status_ExpiresAt
        ON dbo.Tasks (Status, ExpiresAt);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Tasks')
      AND name = N'IX_Tasks_StreamId'
)
BEGIN
    CREATE INDEX IX_Tasks_StreamId
        ON dbo.Tasks (StreamId);
END;

IF OBJECT_ID(N'dbo.SubtitleSegments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubtitleSegments
    (
        Id        bigint IDENTITY(1, 1) NOT NULL,
        StreamId  bigint                 NOT NULL,
        Language  nvarchar(32)           NOT NULL,
        Source    nvarchar(64)           NOT NULL,
        Sequence  int                    NOT NULL,
        StartMs   bigint                 NOT NULL,
        EndMs     bigint                 NOT NULL,
        Text      nvarchar(max)          NOT NULL,
        CreatedAt datetimeoffset(3)       NOT NULL
            CONSTRAINT DF_SubtitleSegments_CreatedAt DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_SubtitleSegments PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_SubtitleSegments_Streams_StreamId FOREIGN KEY (StreamId)
            REFERENCES dbo.Streams (Id) ON DELETE CASCADE,
        CONSTRAINT CK_SubtitleSegments_Sequence CHECK (Sequence >= 0),
        CONSTRAINT CK_SubtitleSegments_TimeRange CHECK
            (StartMs >= 0 AND EndMs >= StartMs)
    );
END;

IF COL_LENGTH(N'dbo.SubtitleSegments', N'SpeakerLabel') IS NULL
BEGIN
    ALTER TABLE dbo.SubtitleSegments ADD SpeakerLabel nvarchar(64) NULL;
END;

IF COL_LENGTH(N'dbo.SubtitleSegments', N'SpeakerName') IS NULL
BEGIN
    ALTER TABLE dbo.SubtitleSegments ADD SpeakerName nvarchar(256) NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SubtitleSegments')
      AND name = N'UX_SubtitleSegments_Stream_Language_Source_Sequence'
)
BEGIN
    CREATE UNIQUE INDEX UX_SubtitleSegments_Stream_Language_Source_Sequence
        ON dbo.SubtitleSegments (StreamId, Language, Source, Sequence);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SubtitleSegments')
      AND name = N'IX_SubtitleSegments_StreamId_StartMs'
)
BEGIN
    CREATE INDEX IX_SubtitleSegments_StreamId_StartMs
        ON dbo.SubtitleSegments (StreamId, StartMs);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SubtitleSegments')
      AND name = N'IX_SubtitleSegments_StreamId_SpeakerName'
)
BEGIN
    CREATE INDEX IX_SubtitleSegments_StreamId_SpeakerName
        ON dbo.SubtitleSegments (StreamId, SpeakerName);
END;

IF OBJECT_ID(N'dbo.SpeakerTurns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SpeakerTurns
    (
        Id           bigint IDENTITY(1, 1) NOT NULL,
        StreamId     bigint                 NOT NULL,
        SpeakerLabel nvarchar(64)           NOT NULL,
        SpeakerName  nvarchar(256)          NULL,
        StartMs      bigint                 NOT NULL,
        EndMs        bigint                 NOT NULL,
        CreatedAt    datetimeoffset(3)      NOT NULL
            CONSTRAINT DF_SpeakerTurns_CreatedAt DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_SpeakerTurns PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_SpeakerTurns_Streams_StreamId FOREIGN KEY (StreamId)
            REFERENCES dbo.Streams (Id) ON DELETE CASCADE,
        CONSTRAINT CK_SpeakerTurns_TimeRange CHECK
            (StartMs >= 0 AND EndMs > StartMs)
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SpeakerTurns')
      AND name = N'IX_SpeakerTurns_StreamId_StartMs'
)
BEGIN
    CREATE INDEX IX_SpeakerTurns_StreamId_StartMs
        ON dbo.SpeakerTurns (StreamId, StartMs);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SpeakerTurns')
      AND name = N'IX_SpeakerTurns_StreamId_SpeakerLabel'
)
BEGIN
    CREATE INDEX IX_SpeakerTurns_StreamId_SpeakerLabel
        ON dbo.SpeakerTurns (StreamId, SpeakerLabel);
END;

COMMIT TRANSACTION;
