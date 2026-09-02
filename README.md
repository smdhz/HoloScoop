# HoloScoop

## 干什么

HoloScoop 用于建立一个可检索的 Hololive 直播与字幕知识库，方便按关键词查找直播内容，并直接跳转到 YouTube 对应时间点。

它不是无脑保存全部直播视频的仓库。普通直播以保存元数据、字幕、聊天记录和缩略图为主；只有高价值直播才考虑长期保存视频，以控制存储成本。

HoloScoop 从 Redis Stream 接收待处理任务。任务先作为候选项展示在管理页面，由用户决定是否下载；收到任务不等于立即开始媒体处理。Forge 可以是 Redis Stream 的生产者之一，但 HoloScoop 不依赖 Forge 完成任务管理、数据存储和搜索。

## 准备怎么干

### 技术方案

- .NET 作为统一开发平台。
- ASP.NET Core Razor Pages 提供管理页面、搜索页面和必要的 API。
- Redis Stream 作为外部任务的接收通道。应用使用 Consumer Group 读取任务，先持久化到 MSSQL，再向 Redis 确认消息。
- Quartz.NET 在 Web 进程内负责任务调度和后台作业。
- EF Core + MSSQL 保存权威业务数据。
- Meilisearch 提供全文搜索；它只是可随时从 MSSQL 重建的搜索索引，不作为权威数据源。
- 媒体任务与 Web 放在同一个项目中，通过 `yt-dlp` 获取字幕及所需信息，必要时调用 `ffmpeg` 和 Whisper。等出现明确的独立部署或扩容需求后，再考虑拆分 Worker。

当前目录按以下职责组织：

```text
HoloScoop/
├── HoloScoop.csproj
├── Pages/       # 任务管理与字幕搜索页面
├── Jobs/        # Quartz 过期和任务处理作业
├── Data/        # EF Core 实体与 SQL Server 映射
├── Search/      # Meilisearch 索引与检索
├── Services/    # Redis 接入与媒体处理
├── database/    # 手工部署的 SQL Server schema
├── Dockerfile
├── compose.yaml
└── README.md
```

当前已经实现第一阶段的基本闭环：Redis Stream 候选任务落库、人工选择、Quartz 调度、`yt-dlp` 下载、VTT 字幕解析、Meilisearch 索引和带 YouTube 时间戳的搜索。数据库仍通过手工脚本部署，不在应用启动时自动修改结构。

### 建议的核心数据表

第一阶段只使用三张核心表，不为视频、字幕文件另建资产表。

数据库结构通过 [`database/schema.sql`](database/schema.sql) 手工维护和部署，不使用 EF Core migration。脚本采用存在性检查，可以对已经初始化的数据库重复执行；后续修改表结构时也应显式更新这份脚本。

#### `Tasks`

同一张表同时表示“等待用户选择的候选任务”和“已进入处理流程的任务”，不再拆分 `IncomingTasks` 和 `MediaJobs`。

主要字段：

- `Id`
- `RedisStream`
- `RedisMessageId`
- `StreamId`：关联 `Streams`
- `DownloadMode`：可空枚举，值为 `VideoAndSubtitles` 或 `SubtitlesOnly`
- `Status`：任务当前状态
- `ExpiresAt`
- `AttemptCount`
- `LastError`
- `CreatedAt`
- `UpdatedAt`
- `RowVersion`

`Status` 使用枚举：

```text
PendingSelection
Queued
Downloading
ParsingSubtitles
Indexing
Completed
Failed
Expired
```

使用 `(RedisStream, RedisMessageId)` 唯一约束抵抗 Redis 消息重投。`DownloadMode` 为空表示尚未选择；用户点击按钮后设置枚举值，并将同一行任务改为 `Queued`。

#### `Streams`

保存可被多次发现的直播/视频元数据。

主要字段：

- `Id`
- `Platform`
- `ExternalId`
- `ChannelId`
- `ChannelName`
- `Title`
- `Description`
- `SourceUrl`
- `ThumbnailUrl`
- `ScheduledAt`
- `StartedAt`
- `EndedAt`
- `DurationMs`
- `CreatedAt`
- `UpdatedAt`

使用 `(Platform, ExternalId)` 唯一约束，避免同一个视频被重复建档。

#### `SubtitleSegments`

保存解析后的字幕分段，作为 Meilisearch 索引的权威数据源。

主要字段：

- `Id`
- `StreamId`：关联 `Streams`
- `Language`
- `Source`：例如官方字幕、自动字幕或 Whisper
- `Sequence`
- `StartMs`
- `EndMs`
- `Text`
- `CreatedAt`

使用 `(StreamId, Language, Source, Sequence)` 唯一约束，并对 `(StreamId, StartMs)` 建立索引。起止时间统一使用整数毫秒。

视频和原始字幕文件按固定目录规则保存在本地文件系统中，数据库只保存相对路径，不保存宿主机绝对路径。数据库暂不单独记录每个文件；只有将来真正出现多存储后端、多版本媒体或文件迁移需求时，才考虑增加资产表。

### 处理流程

1. 使用 Consumer Group 从 Redis Stream 读取任务。
2. 根据 Redis 消息 ID 去重，将候选任务和过期时间写入 MSSQL；写入成功后再 `XACK`。
3. 管理页面展示尚未过期的候选任务，并提供两个操作：
   - **下载视频和字幕**：将 `DownloadMode` 设为 `VideoAndSubtitles`。
   - **仅下载字幕**：将 `DownloadMode` 设为 `SubtitlesOnly`。
4. 选择后将当前 `Tasks` 记录改为 `Queued`；用户不做选择时不开始下载，到期后将该记录改为 `Expired`。
5. `yt-dlp` 按用户选择下载视频和/或已有字幕，然后将字幕按时间切段写入 MSSQL。
6. 把可搜索内容同步到 Meilisearch。
7. Web 页面提供关键词搜索，并跳转到 YouTube 对应时间戳。

Redis Stream 只负责传递任务，不承担等待用户选择的状态。消息落库后即可确认；候选任务的选择、过期和媒体处理状态均以 MSSQL 为准。

### 本地文件存储策略

HoloScoop 只有一台本地服务器，并且不为存储增加额外预算，因此直接使用宿主机文件系统，不使用 AWS S3，也不额外部署 MinIO、Ceph 等对象存储服务。单机环境下引入这些服务不会增加可用存储空间，反而会增加部署和维护成本。

宿主机使用两个相互独立的目录：

```text
/data/holoscoop/
├── library/                         # 长期保留的文件
│   └── youtube/
│       └── {ExternalId}/
│           ├── metadata/
│           │   └── info.json
│           ├── subtitles/
│           ├── chat/
│           ├── thumbnails/
│           └── video/
└── work/                            # 下载、转码和转写的临时文件
    └── {TaskId}/
```

容器内分别挂载为 `/data/library` 和 `/data/work`。业务数据和配置只使用类似 `youtube/{ExternalId}/subtitles/ja.auto.vtt` 的相对路径，不能保存 `/data/holoscoop/...` 或 `/data/library/...` 之类依赖部署环境的绝对路径。

- 普通直播：长期保存元数据、字幕、聊天记录和缩略图。
- 高价值直播：按明确规则或人工标记，将视频保存在 `library`。
- 下载中的视频、抽取的音频以及 Whisper 中间文件保存在 `work/{TaskId}`。
- 任务完成后清理临时文件；失败任务的临时文件在保留一段排错时间后清理。
- MSSQL 保存任务状态、直播元数据和解析后的字幕分段。
- Meilisearch 只保存可从 MSSQL 重建的搜索索引。

未来只有在增加其他服务器或实际需要多机共享文件时，才考虑抽象并迁移到其他存储后端。当前不为尚未出现的多存储需求增加复杂度。

单台服务器上的文件不构成真正的备份；在没有额外磁盘和异地空间的前提下，这个风险无法通过软件消除。公开视频原则上允许从来源重新下载，数据库和无法重新生成的人工数据应优先保护。

这样可以把系统重点放在内容检索和知识沉淀上，而不是维护没有实际收益的存储基础设施。

## 第一阶段 MVP

- 在单个 Razor Pages 项目内按 Pages、Jobs、Data、Search、Services 划分职责。
- 通过 Consumer Group 接入 Redis Stream，将收到的任务去重后持久化。
- 在管理页面展示未过期候选任务，提供“下载视频和字幕”和“仅下载字幕”两个操作。
- 对未做选择的候选任务执行过期处理，不自动下载。
- 使用 `Tasks`、`Streams` 和 `SubtitleSegments` 三张核心表保存任务状态、直播元数据和字幕。
- 只在用户做出选择后将任务改为 `Queued`，并由 Quartz.NET 调度执行。
- 使用 `yt-dlp` 按选择获取视频和/或已有字幕。
- 解析字幕并切段写入 MSSQL。
- 将字幕索引到 Meilisearch，并支持从 MSSQL 全量重建索引。
- 提供简单的任务进度、失败原因和重试入口。
- 提供字幕关键词搜索，并能跳转到 YouTube 时间戳。
- 落实普通直播不长期保存视频的默认策略。
- 每天清理创建超过 14 天且从未选择下载模式的任务，并删除没有其他任务或字幕引用的孤立直播记录。

### Redis 消息格式

Consumer Group 默认读取 `holoscoop:tasks`。Redis Stream 是唯一任务入口，标准消息只包含 `Note.dbo.HololiveSchedule` 的主键：

```text
id = {HololiveSchedule.Id GUID}
```

Consumer 收到 ID 后，使用与 HoloScoop 相同的 SQL Server 账号连接 `Note` 数据库，读取 `HololiveSchedule` 的 `StartDt`、`MemberName`、`StreamUrl`、`StreamTitle` 和 `StreamImage`，再落入 HoloScoop。`Note` 仅用于按消息 ID 补全数据，不会被轮询，也不是第二个任务入口。找不到对应记录或消息无效时不会确认消息，会保留在 Redis Pending Entries List 中供排查。

## Docker 部署

`compose.yaml` 只启动 HoloScoop 和 Meilisearch。MSSQL 和 Redis 使用已有的外部实例，不由本项目的 Compose 创建。

首次启动前复制环境变量示例并填写真实连接信息：

```powershell
Copy-Item .env.example .env
docker compose up -d --build
```

首次部署还需要由具备建表权限的账号执行 [`database/schema.sql`](database/schema.sql)。

在本机试运行且暂时不挂载 NAS 时，使用本地卷覆盖文件：

```powershell
docker compose -f compose.yaml -f compose.local.yaml up -d --build
```

正式 `compose.yaml` 固定使用 `Production` 和 NAS NFS。`compose.local.yaml` 将应用覆盖为 `Development` 并使用本地 `library_data`。两种模式都使用 Redis Stream 入口、Note 数据补全和 Quartz 处理链路。

应用默认通过 `http://localhost:8080` 访问。Meilisearch 端口只绑定在宿主机的 `127.0.0.1:7700`，容器内的 HoloScoop 通过 `http://meilisearch:7700` 访问它。

应用镜像基于 .NET 10 Ubuntu 镜像构建，并安装 `yt-dlp`、`ffmpeg` 以及 YouTube 解析所需的 Deno JavaScript 运行时。Compose 将 `10.16.1.101:/volume1/media/Video/Hololive` 作为 NFS 卷挂载到容器内的 `/data/library`，长期文件直接保存在该目录；临时工作目录 `/data/work` 和 Meilisearch 数据分别使用本地 Docker 卷 `work_data` 和 `meilisearch_data`。

NAS 挂载参数由 `compose.yaml` 中的 `library_data` 卷配置统一管理。

## 暂不做

- 不做 embedding 或向量检索。
- 不引入复杂的分布式队列。
- 不做全量视频下载和长期归档。
