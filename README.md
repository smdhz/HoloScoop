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

计划中的最小目录结构：

```text
HoloScoop/
├── HoloScoop.sln
├── src/
│   └── HoloScoop/
│       ├── Pages/       # Razor Pages
│       ├── Jobs/        # Quartz 作业（后续添加）
│       ├── Data/        # EF Core（后续添加）
│       ├── Search/      # Meilisearch（后续添加）
│       └── Services/    # 媒体处理等服务（后续添加）
└── README.md
```

当前只建立 solution、标准 Razor Pages 项目骨架和方案文档，不实现具体业务。

### 建议的核心数据表

第一阶段只使用三张核心表，不为视频、字幕文件另建资产表。

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

视频和原始字幕文件按固定目录规则保存，例如 `media/{Platform}/{ExternalId}/`，数据库不单独记录每个文件。只有将来真正出现多存储后端、多版本媒体或文件迁移需求时，才考虑增加资产表。

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

### 视频存储策略

- 普通直播：只长期保存元数据、字幕、聊天记录和缩略图。
- 高价值直播：按明确规则或人工标记长期保存视频。
- 转写过程中产生的临时媒体文件在任务完成后清理。

这样可以把系统重点放在内容检索和知识沉淀上，而不是持续扩张的视频仓库。

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

## Docker 部署

`compose.yaml` 只启动 HoloScoop 和 Meilisearch。MSSQL 和 Redis 使用已有的外部实例，不由本项目的 Compose 创建。

首次启动前复制环境变量示例并填写真实连接信息：

```powershell
Copy-Item .env.example .env
docker compose up -d --build
```

应用默认通过 `http://localhost:8080` 访问。Meilisearch 端口只绑定在宿主机的 `127.0.0.1:7700`，容器内的 HoloScoop 通过 `http://meilisearch:7700` 访问它。

应用镜像基于 .NET 10 Ubuntu 镜像构建，并安装 `yt-dlp`、`ffmpeg` 以及 YouTube 解析所需的 Deno JavaScript 运行时。下载的媒体保存在 Docker 命名卷 `media_data` 中，Meilisearch 数据保存在 `meilisearch_data` 中。

## 暂不做

- 不做 embedding 或向量检索。
- 不引入复杂的分布式队列。
- 不做全量视频下载和长期归档。
- 不在当前空骨架阶段实现具体业务。
