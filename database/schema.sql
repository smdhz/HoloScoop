SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.Streams
    (
        Id bigint IDENTITY(1, 1) NOT NULL,
        Platform nvarchar(32) NOT NULL,
        ExternalId nvarchar(128) NOT NULL,
        ChannelId nvarchar(128) NULL,
        ChannelName nvarchar(256) NULL,
        Title nvarchar(512) NOT NULL,
        Description nvarchar(max) NULL,
        SourceUrl nvarchar(2048) NOT NULL,
        ThumbnailUrl nvarchar(2048) NULL,
        ScheduledAt datetimeoffset(3) NULL,
        StartedAt datetimeoffset(3) NULL,
        EndedAt datetimeoffset(3) NULL,
        DurationMs bigint NULL,
        CreatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_Streams_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_Streams_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Streams PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_Streams_DurationMs CHECK (DurationMs IS NULL OR DurationMs >= 0)
    );
    CREATE UNIQUE INDEX UX_Streams_Platform_ExternalId ON dbo.Streams (Platform, ExternalId);

    CREATE TABLE dbo.Tasks
    (
        Id bigint IDENTITY(1, 1) NOT NULL,
        RedisStream nvarchar(256) NOT NULL,
        RedisMessageId nvarchar(64) NOT NULL,
        StreamId bigint NOT NULL,
        DownloadMode varchar(32) NULL,
        SpeakerCount int NULL,
        SpeakerNamesJson nvarchar(2000) NULL,
        ScheduledMemberName nvarchar(256) NULL,
        Status varchar(32) NOT NULL,
        AttemptCount int NOT NULL CONSTRAINT DF_Tasks_AttemptCount DEFAULT 0,
        LastError nvarchar(max) NULL,
        CreatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_Tasks_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_Tasks_UpdatedAt DEFAULT SYSUTCDATETIME(),
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_Tasks PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_Tasks_Streams_StreamId FOREIGN KEY (StreamId) REFERENCES dbo.Streams (Id),
        CONSTRAINT CK_Tasks_DownloadMode CHECK
            (DownloadMode IS NULL OR DownloadMode IN ('VideoAndSubtitles', 'SubtitlesOnly', 'VideoOnly')),
        CONSTRAINT CK_Tasks_Status CHECK
            (Status IN ('PendingSelection', 'Queued', 'Downloading', 'ParsingSubtitles', 'Diarizing', 'Indexing', 'Completed', 'Failed', 'Expired')),
        CONSTRAINT CK_Tasks_AttemptCount CHECK (AttemptCount >= 0),
        CONSTRAINT CK_Tasks_SpeakerCount CHECK
            (SpeakerCount IS NULL OR (SpeakerCount >= 1 AND SpeakerCount <= 20))
    );
    CREATE UNIQUE INDEX UX_Tasks_RedisStream_RedisMessageId ON dbo.Tasks (RedisStream, RedisMessageId);
    CREATE INDEX IX_Tasks_Status_UpdatedAt ON dbo.Tasks (Status, UpdatedAt);
    CREATE INDEX IX_Tasks_StreamId ON dbo.Tasks (StreamId);

    CREATE TABLE dbo.DownloadedVideos
    (
        Id bigint IDENTITY(1, 1) NOT NULL,
        StreamId bigint NOT NULL,
        DownloadedAt datetimeoffset(3) NOT NULL,
        CONSTRAINT PK_DownloadedVideos PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_DownloadedVideos_Streams_StreamId FOREIGN KEY (StreamId) REFERENCES dbo.Streams (Id)
    );
    CREATE UNIQUE INDEX UX_DownloadedVideos_StreamId ON dbo.DownloadedVideos (StreamId);
    CREATE INDEX IX_DownloadedVideos_DownloadedAt ON dbo.DownloadedVideos (DownloadedAt DESC);

    CREATE TABLE dbo.SubtitleSegments
    (
        Id bigint IDENTITY(1, 1) NOT NULL,
        StreamId bigint NOT NULL,
        Language nvarchar(32) NOT NULL,
        Source nvarchar(64) NOT NULL,
        TrackRole varchar(16) NOT NULL,
        DeclaredLanguage nvarchar(32) NOT NULL,
        OriginDeclaredLanguage nvarchar(32) NULL,
        OriginSource nvarchar(64) NOT NULL,
        GenerationVersion int NOT NULL CONSTRAINT DF_SubtitleSegments_GenerationVersion DEFAULT 1,
        IsActive bit NOT NULL CONSTRAINT DF_SubtitleSegments_IsActive DEFAULT 1,
        NeedsReview bit NOT NULL CONSTRAINT DF_SubtitleSegments_NeedsReview DEFAULT 0,
        BaseTrackQuality float NULL,
        BuildStatus varchar(32) NULL,
        DetectedLanguage nvarchar(32) NULL,
        LanguageDetectionMethod varchar(32) NULL,
        ModelVersion nvarchar(256) NULL,
        Sequence int NOT NULL,
        StartMs bigint NOT NULL,
        EndMs bigint NOT NULL,
        Text nvarchar(max) NOT NULL,
        SpeakerLabel nvarchar(64) NULL,
        SpeakerName nvarchar(256) NULL,
        SpeakerNameSource varchar(32) NULL,
        SpeakerNameScore float NULL,
        CreatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_SubtitleSegments_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SubtitleSegments PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_SubtitleSegments_Streams_StreamId FOREIGN KEY (StreamId) REFERENCES dbo.Streams (Id) ON DELETE CASCADE,
        CONSTRAINT CK_SubtitleSegments_Sequence CHECK (Sequence >= 0),
        CONSTRAINT CK_SubtitleSegments_TimeRange CHECK (StartMs >= 0 AND EndMs > StartMs),
        CONSTRAINT CK_SubtitleSegments_TrackRole CHECK (TrackRole IN ('raw', 'canonical')),
        CONSTRAINT CK_SubtitleSegments_GenerationVersion CHECK (GenerationVersion >= 1),
        CONSTRAINT CK_SubtitleSegments_BaseTrackQuality CHECK
            (BaseTrackQuality IS NULL OR (BaseTrackQuality >= 0 AND BaseTrackQuality <= 1)),
        CONSTRAINT CK_SubtitleSegments_BuildStatus CHECK
            (BuildStatus IS NULL OR BuildStatus IN ('source', 'normalized', 'repaired', 'retranscribed', 'repair-failed', 'failed')),
        CONSTRAINT CK_SubtitleSegments_SpeakerNameScore CHECK
            (SpeakerNameScore IS NULL OR (SpeakerNameScore >= -1 AND SpeakerNameScore <= 1))
    );
    CREATE UNIQUE INDEX UX_SubtitleSegments_Stream_Language_Source_Sequence
        ON dbo.SubtitleSegments (StreamId, Language, Source, Sequence);
    CREATE INDEX IX_SubtitleSegments_StreamId_StartMs ON dbo.SubtitleSegments (StreamId, StartMs);
    CREATE INDEX IX_SubtitleSegments_StreamId_SpeakerName ON dbo.SubtitleSegments (StreamId, SpeakerName);
    CREATE INDEX IX_SubtitleSegments_StreamId_TrackRole_IsActive
        ON dbo.SubtitleSegments (StreamId, TrackRole, IsActive);

    CREATE TABLE dbo.SpeakerTurns
    (
        Id bigint IDENTITY(1, 1) NOT NULL,
        StreamId bigint NOT NULL,
        SpeakerLabel nvarchar(64) NOT NULL,
        SpeakerName nvarchar(256) NULL,
        SpeakerNameSource varchar(32) NULL,
        SpeakerNameScore float NULL,
        SuggestedSpeakerName nvarchar(256) NULL,
        SuggestedSpeakerScore float NULL,
        StartMs bigint NOT NULL,
        EndMs bigint NOT NULL,
        CreatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_SpeakerTurns_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SpeakerTurns PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_SpeakerTurns_Streams_StreamId FOREIGN KEY (StreamId) REFERENCES dbo.Streams (Id) ON DELETE CASCADE,
        CONSTRAINT CK_SpeakerTurns_TimeRange CHECK (StartMs >= 0 AND EndMs > StartMs),
        CONSTRAINT CK_SpeakerTurns_SpeakerNameScore CHECK
            (SpeakerNameScore IS NULL OR (SpeakerNameScore >= -1 AND SpeakerNameScore <= 1)),
        CONSTRAINT CK_SpeakerTurns_SuggestedSpeakerScore CHECK
            (SuggestedSpeakerScore IS NULL OR (SuggestedSpeakerScore >= -1 AND SuggestedSpeakerScore <= 1))
    );
    CREATE INDEX IX_SpeakerTurns_StreamId_StartMs ON dbo.SpeakerTurns (StreamId, StartMs);
    CREATE INDEX IX_SpeakerTurns_StreamId_SpeakerLabel ON dbo.SpeakerTurns (StreamId, SpeakerLabel);

    CREATE TABLE dbo.SpeakerClusterEmbeddings
    (
        Id bigint IDENTITY(1, 1) NOT NULL,
        StreamId bigint NOT NULL,
        SpeakerLabel nvarchar(64) NOT NULL,
        Embedding varbinary(max) NOT NULL,
        Dimension int NOT NULL,
        CreatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_SpeakerClusterEmbeddings_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SpeakerClusterEmbeddings PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_SpeakerClusterEmbeddings_Streams_StreamId FOREIGN KEY (StreamId) REFERENCES dbo.Streams (Id) ON DELETE CASCADE,
        CONSTRAINT CK_SpeakerClusterEmbeddings_Dimension CHECK (Dimension > 0)
    );
    CREATE UNIQUE INDEX UX_SpeakerClusterEmbeddings_StreamId_SpeakerLabel
        ON dbo.SpeakerClusterEmbeddings (StreamId, SpeakerLabel);

    CREATE TABLE dbo.VoiceProfiles
    (
        Id bigint IDENTITY(1, 1) NOT NULL,
        MemberName nvarchar(256) NOT NULL,
        Embedding varbinary(max) NOT NULL,
        Dimension int NOT NULL,
        SampleCount int NOT NULL,
        SourceStreamId bigint NOT NULL,
        CreatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_VoiceProfiles_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_VoiceProfiles_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_VoiceProfiles PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_VoiceProfiles_Dimension CHECK (Dimension > 0),
        CONSTRAINT CK_VoiceProfiles_SampleCount CHECK (SampleCount > 0)
    );
    CREATE UNIQUE INDEX UX_VoiceProfiles_MemberName ON dbo.VoiceProfiles (MemberName);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
