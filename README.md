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
├── Jobs/        # Quartz 候选状态同步和任务处理作业
├── Data/        # EF Core 实体与 SQL Server 映射
├── Search/      # Meilisearch 索引与检索
├── Services/    # Redis 接入与媒体处理
├── database/    # 手工部署的 SQL Server schema
├── Dockerfile
├── compose.yaml
└── README.md
```

当前已经实现第一阶段的基本闭环：Redis Stream 候选任务落库、人工选择、Quartz 调度、`yt-dlp` 下载、VTT 字幕解析、本地 CPU 说话人分离、Meilisearch 索引和带 YouTube 时间戳的搜索。数据库仍通过手工脚本部署，不在应用启动时自动修改结构。

### 建议的核心数据表

第一阶段使用四张核心表，不为视频、字幕文件另建资产表。

数据库结构通过 [`database/schema.sql`](database/schema.sql) 手工维护和部署，不使用 EF Core migration。脚本采用存在性检查，可以对已经初始化的数据库重复执行；后续修改表结构时也应显式更新这份脚本。

#### `Tasks`

同一张表同时表示“等待用户选择的候选任务”和“已进入处理流程的任务”，不再拆分 `IncomingTasks` 和 `MediaJobs`。

主要字段：

- `Id`
- `RedisStream`
- `RedisMessageId`
- `StreamId`：关联 `Streams`
- `DownloadMode`：可空枚举，值为 `VideoAndSubtitles` 或 `SubtitlesOnly`
- `SpeakerCount`：用户在排队前确认的本场实际说话人数
- `SpeakerNamesJson`：单人直播自动保存日程成员姓名；多人直播为空数组
- `ScheduledMemberName`：候选任务创建时固化的日程成员姓名，不受后续直播元数据更新影响
- `Status`：任务当前状态
- `ExpiresAt`：兼容旧数据保留，新任务不再使用本地到期时间
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
Diarizing
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
- `SpeakerLabel`：本场直播内的匿名标签，例如 `SPEAKER_00`
- `SpeakerName`：人工确认后的成员名，可空
- `CreatedAt`

使用 `(StreamId, Language, Source, Sequence)` 唯一约束，并对 `(StreamId, StartMs)` 建立索引。起止时间统一使用整数毫秒。

视频和原始字幕文件按固定目录规则保存在本地文件系统中，数据库只保存相对路径，不保存宿主机绝对路径。数据库暂不单独记录每个文件；只有将来真正出现多存储后端、多版本媒体或文件迁移需求时，才考虑增加资产表。

#### `SpeakerTurns`

保存说话人分离产生的时间段，是匿名标签与字幕归属的权威数据。主要字段为 `StreamId`、`SpeakerLabel`、`SpeakerName`、`StartMs`、`EndMs` 和 `CreatedAt`。匿名标签只保证在单场直播内一致；通过 `/Speakers/{StreamId}` 页面人工映射真实成员名后，会同时更新字幕分段并重建该场直播的搜索索引。

### 处理流程

1. 使用 Consumer Group 从 Redis Stream 读取任务。
2. 根据 Redis 消息 ID 去重，将候选任务写入 MSSQL；写入成功后再 `XACK`。
3. 管理页面仅展示 Redis Stream 中仍存在、并且已开播至少 1 小时的候选任务。排队前必须选择“只有一个人”或输入 2～20 的实际说话人数；单人直播直接使用日程成员姓名。页面提供两个操作：
   - **下载视频和字幕**：将 `DownloadMode` 设为 `VideoAndSubtitles`。
   - **仅下载字幕**：将 `DownloadMode` 设为 `SubtitlesOnly`。
4. 选择后将当前 `Tasks` 记录改为 `Queued`；用户不做选择时不开始下载。Redis 中对应消息被删除后，本地候选同步为 `Expired`；消息仍存在时不会因本地计时而过期。
5. `yt-dlp` 按用户选择下载视频和已有字幕；媒体库已有该视频时使用 `--skip-download`，复用本地视频抽取音轨，避免重复下载。没有本地视频且选择“仅下载字幕”时，会临时下载来源提供的默认最佳音频。
6. `ffmpeg` 把音轨转换为 16 kHz 单声道 WAV；没有可解析的远端 VTT 时，使用预构建的 whisper.cpp 程序和本地模型从该临时音频生成字幕。随后 sherpa-onnx 生成匿名说话人时间段，再按时间重叠把字幕归到说话人名下。
7. 把可搜索内容（包括匿名标签和已确认的成员名）同步到 Meilisearch。
8. Web 页面提供关键词与说话人过滤，并跳转到本地视频或 YouTube 的对应时间戳。

Redis Stream 同时控制候选生命周期。消息落库后即可确认，但确认不会影响消息本身是否存在；HoloScoop 定期按 Redis 中对应消息是否仍存在同步候选状态。本地只负责“开播满 1 小时才可选择”的时间门槛，不自行设置候选有效期。

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

NAS 的 `Hololive/library` 目录在容器内挂载为 `/data/library`；临时工作卷挂载为 `/data/work`。业务数据和配置只使用类似 `youtube/{ExternalId}/subtitles/ja.auto.vtt` 的相对路径，不能保存 `/data/holoscoop/...` 或 `/data/library/...` 之类依赖部署环境的绝对路径。

- 普通直播：长期保存元数据、字幕、聊天记录和缩略图。
- 高价值直播：按明确规则或人工标记，将视频保存在 `library`。
- 下载中的视频、抽取的 16 kHz WAV 和说话人分离中间文件保存在 `work/{TaskId}`。
- 任务完成后清理临时文件；失败任务的临时文件在保留一段排错时间后清理。
- MSSQL 保存任务状态、直播元数据和解析后的字幕分段。
- Meilisearch 只保存可从 MSSQL 重建的搜索索引。

未来只有在增加其他服务器或实际需要多机共享文件时，才考虑抽象并迁移到其他存储后端。当前不为尚未出现的多存储需求增加复杂度。

单台服务器上的文件不构成真正的备份；在没有额外磁盘和异地空间的前提下，这个风险无法通过软件消除。公开视频原则上允许从来源重新下载，数据库和无法重新生成的人工数据应优先保护。

这样可以把系统重点放在内容检索和知识沉淀上，而不是维护没有实际收益的存储基础设施。

## 第一阶段 MVP

- 在单个 Razor Pages 项目内按 Pages、Jobs、Data、Search、Services 划分职责。
- 通过 Consumer Group 接入 Redis Stream，将收到的任务去重后持久化。
- 在管理页面展示 Redis 中仍存在且已开播满 1 小时的候选任务，提供“下载视频和字幕”和“仅下载字幕”两个操作。
- Redis 消息移除后同步失效未选择的候选任务，不使用本地有效期。
- 使用 `Tasks`、`Streams`、`SubtitleSegments` 和 `SpeakerTurns` 四张核心表保存任务状态、直播元数据、字幕和说话人时间段。
- 只在用户做出选择后将任务改为 `Queued`，并由 Quartz.NET 调度执行。
- 使用 `yt-dlp` 按选择获取视频和已有字幕，并为本地转写及说话人分离临时获取默认最佳音频。
- 解析字幕并切段写入 MSSQL；使用 sherpa-onnx 在 CPU 上生成匿名说话人标签并支持人工映射姓名。
- 将字幕索引到 Meilisearch，并支持从 MSSQL 全量重建索引。
- 提供简单的任务进度、失败原因和重试入口。
- 提供字幕关键词搜索，并能跳转到 YouTube 时间戳。
- 落实普通直播不长期保存视频的默认策略。
- 每天清理失效超过 14 天且从未选择下载模式的任务、完成超过 100 天的任务记录，并删除没有其他任务或字幕引用的孤立直播记录。Redis 中仍存在的候选不会被这项清理删除；媒体库、字幕和直播数据不随已完成任务记录删除。

### Redis 消息格式

Consumer Group 默认读取 Forge 写入的 `forge:calendar:add`。Redis Stream 是唯一任务入口，标准消息只包含 `Note.dbo.HololiveSchedule` 的主键：

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

`compose.yaml` 固定使用 `Production` 和 NAS NFS，并启用 Redis Stream 入口、Note 数据补全和 Quartz 处理链路。

应用默认通过 `http://localhost:8080` 访问。Meilisearch 将宿主机的 `${MEILI_PORT:-7700}` 映射到容器端口 `7700`，容器内的 HoloScoop 通过 `http://meilisearch:7700` 访问它。

应用镜像基于 .NET 10 Ubuntu 镜像构建，并安装 `yt-dlp`、`ffmpeg`、YouTube 解析所需的 Deno JavaScript 运行时，以及公开可直接下载的 sherpa-onnx 说话人分离模型。Compose 将 `10.16.1.101:/volume1/media/Video/Hololive/library` 作为 NFS 卷挂载到容器内的 `/data/library`；临时工作目录 `/data/work` 和 Meilisearch 数据分别使用本地 Docker 卷 `work_data` 和 `meilisearch_data`。

NAS 挂载参数由 `compose.yaml` 中的 `library_data` 卷配置统一管理。

## 本地说话人分离

多人联动直播会在已有字幕处理之后执行完全本地的说话人分离，用于区分“谁在什么时候说了什么”。这条链路不调用云端语音 API，不需要 Hugging Face token，也不要求 NVIDIA GPU：

1. `yt-dlp` 下载已有日语字幕和来源提供的默认最佳音频；不会为了节省处理时间而特意选择低质量音频。
2. `ffmpeg` 临时生成 16 kHz 单声道 PCM WAV。
3. [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) 使用公开的 segmentation 和 TitaNet embedding ONNX 模型在 CPU 上生成 `SPEAKER_00`、`SPEAKER_01` 等时间段。
4. 系统按时间重叠将已有字幕归到匿名标签，写入 MSSQL 和 Meilisearch。每场直播可在说话人页面人工确认匿名标签与成员姓名，之后可以按姓名过滤全文搜索。
5. 任务完成后删除临时音频；选择“仅下载字幕”时也不会长期保存下载的原始音频。

单人直播会使用 TitaNet embedding 更新日程成员的声纹档案。多人直播在全声纹库中保守匹配。匹配结果只作为说话人页面输入框的建议值，必须人工保存后才写入字幕和搜索索引。相似度不足、与第二候选差距太小或同一成员被多个匿名说话人竞争时保持未匹配。

处理任务会为每个匿名说话人保存一份聚类声纹。多人直播中人工保存姓名时，如果声纹库还没有该成员，就用这份聚类声纹建立初始档案；已有成员档案不会被多人直播覆盖，避免串音污染。

当前方案优先复用 YouTube 已有字幕；字幕不存在或无法解析时，自动调用镜像中预构建的 whisper.cpp 和本地 `small` 模型转写，不在本机编译 Whisper，也不创建 Whisper Python 环境。Quartz 的媒体处理任务禁止重入，当前单进程实际一次只跑一个直播，避免 10980XE 同时处理多个长音频导致内存与 CPU 争用。以后如果积累每位成员的干净参考音频，可以增加本地声纹匹配；在此之前只承诺匿名标签和人工映射，不假定系统能自动知道真实姓名。

两人同时讲话、唱歌、变声、强背景音乐和游戏音效都会降低分离准确率；字幕时间轴本身较粗时，少量字幕可能保持未归属。内容总结应使用已确认的说话人标注，不能让语言模型仅根据台词猜测说话人。

## 暂不做

- 不做 embedding 或向量检索。
- 不引入复杂的分布式队列。
- 不做全量视频下载和长期归档。
