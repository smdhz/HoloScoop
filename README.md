# HoloScoop

HoloScoop 是一个面向 Hololive 直播内容的本地化采集、转写与检索系统。系统从 Redis Stream 或管理页面接收直播任务，下载媒体并在本地完成语音转写和说话人分离，将结构化结果保存到 SQL Server，同时写入 Meilisearch，以便按关键词和说话人检索，并跳转到本地媒体或 YouTube 的对应时间点。

系统采用人工确认后处理的工作方式：外部任务首先作为候选项进入数据库，只有用户选择处理模式并确认说话人数后，任务才会进入下载、转写和索引流程。这一设计可以避免无条件下载全部直播，并允许按内容价值决定是否长期保存视频。

## 已实现功能

### 任务接入与管理

- 使用 Redis Consumer Group 消费任务，并以 Redis Stream 名称和消息 ID 去重。
- 根据任务中的日程 ID，从 `Note.dbo.HololiveSchedule` 读取直播时间、成员、标题、地址和缩略图。
- 支持在任务管理页面通过直播 URL 手动添加任务。
- 候选任务写入 SQL Server 后再确认 Redis 消息，避免任务在持久化前丢失。
- 定期检查原 Redis 消息是否仍然存在，并同步未选择候选项的有效状态。
- 仅展示已经开播至少一小时、仍可处理的候选任务。
- 支持失败原因展示、失败任务重试和中断任务恢复。
- 使用 Quartz.NET 调度候选同步、队列处理和历史数据清理作业。

### 媒体下载与本地转写

- 提供“下载视频并本地转写”和“仅本地转写”两种处理模式。
- 使用 `yt-dlp` 获取直播元数据及所需媒体，不使用 YouTube 官方或自动字幕。
- 检测并复用媒体库中已经存在的视频，避免重复下载。
- 使用 `ffmpeg` 将音轨转换为 16 kHz 单声道 PCM WAV。
- 使用预构建的 whisper.cpp 和本地模型生成字幕，不调用云端语音 API。
- 将字幕解析为带毫秒级起止时间的分段，并保存到 SQL Server。
- 任务完成后清理转码和转写产生的临时文件。

### 说话人分离与声纹辅助

- 使用 sherpa-onnx 在 CPU 上执行本地说话人分离。
- 根据用户确认的说话人数生成 `SPEAKER_00`、`SPEAKER_01` 等匿名说话人标签。
- 按时间重叠关系把字幕分段归属到对应说话人。
- 提供说话人管理页面，可试听样本并将匿名标签映射为真实成员名。
- 保存匿名说话人的聚类声纹，并为单人直播成员维护声纹档案。
- 多人直播可根据现有声纹档案给出保守的姓名建议；建议必须人工保存后才会更新字幕和搜索索引。
- 对低相似度、候选差距过小或多人竞争同一成员的结果保持未匹配，避免自动写入不可靠姓名。

### 搜索与播放

- 使用 Meilisearch 对字幕文本、匿名标签和已确认成员名建立全文索引。
- 支持关键词检索和说话人筛选。
- 搜索结果可跳转到 YouTube 对应时间点。
- 对本地保存的媒体提供视频或音频播放接口，并支持从对应时间位置播放。
- Meilisearch 仅作为派生索引；索引可以随时从 SQL Server 中的权威数据完整重建。

### 数据维护

- 每日清理失效超过 14 天且从未选择处理模式的候选任务。
- 清理完成超过 100 天的任务记录。
- 删除不再被任务或字幕引用的孤立直播记录。
- 清理任务记录时不删除仍需保留的媒体、字幕内容和直播资料。

## 系统架构

```text
Redis Stream ──┐
               ├──> 候选任务 ──> 人工确认 ──> Quartz 处理队列
管理页面 ──────┘                              │
                                             ├──> yt-dlp / ffmpeg
Note SQL Server ──> 日程资料补全              ├──> whisper.cpp
                                             └──> sherpa-onnx
                                                       │
                                  ┌────────────────────┴──────────────┐
                                  ▼                                   ▼
                          SQL Server 权威数据                  Meilisearch 索引
                                  │                                   │
                                  └────────> Razor Pages <────────────┘
```

主要技术组件：

- .NET 10 与 ASP.NET Core Razor Pages
- Entity Framework Core 与 Microsoft SQL Server
- Redis Streams
- Quartz.NET
- Meilisearch
- yt-dlp、ffmpeg 与 whisper.cpp
- sherpa-onnx
- Docker Compose

代码目录按职责组织：

```text
HoloScoop/
├── Data/          # EF Core 实体、映射、查询和命令
├── Jobs/          # Quartz 后台作业与任务状态机
├── Pages/         # 任务、视频、搜索、播放和说话人页面
├── Search/        # Meilisearch 检索与索引重建
├── Services/
│   ├── Media/     # 下载、转码、转写、分离、声纹和媒体存储
│   ├── Note/      # HololiveSchedule 查询与成员目录
│   └── Redis/     # Redis Stream 消费与候选同步
├── database/      # SQL Server 建库脚本
├── Dockerfile
└── compose.yaml
```

## 处理流程

1. Redis Stream Consumer 接收 `HololiveSchedule` ID，或用户在管理页面输入直播 URL 手动添加任务。
2. 系统补全直播资料，以来源和外部视频 ID 建立或更新直播记录，并创建待选择任务。
3. 用户选择处理模式，确认单人直播或填写 2～20 人的实际说话人数。
4. 任务进入 `Queued` 状态，由 Quartz 作业串行领取，防止同一进程同时处理多个长音频。
5. `yt-dlp` 获取媒体及元数据；已有本地视频时直接复用。
6. `ffmpeg` 提取标准 WAV，whisper.cpp 生成本地字幕。
7. sherpa-onnx 生成说话人时间段，系统将字幕分配给匿名说话人并生成声纹信息。
8. 字幕、说话人时间段和任务结果写入 SQL Server，并同步到 Meilisearch。
9. 用户可检索字幕、播放本地媒体、跳转 YouTube 时间点，并在说话人页面确认真实姓名。

任务状态依次使用以下枚举：

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

## 数据存储

SQL Server 是任务、直播、字幕、说话人和声纹信息的权威数据源。数据库结构由 [`database/schema.sql`](database/schema.sql) 创建和维护，应用不会在启动时自动执行 EF Core Migration。

主要数据表包括：

- `Tasks`：候选任务、处理模式、状态、重试次数及失败信息。
- `Streams`：直播平台、外部 ID、频道、标题、来源地址和时间等元数据。
- `DownloadedVideos`：本地长期保存的视频记录及相对路径。
- `SubtitleSegments`：字幕文本、时间范围、语言、模型及说话人归属。
- `SpeakerTurns`：说话人分离产生的匿名时间段和人工确认姓名。
- `VoiceProfiles`：成员声纹档案。
- `SpeakerClusterEmbeddings`：单场直播中匿名说话人的聚类声纹。

媒体文件使用宿主机文件系统，不在数据库中保存依赖部署环境的绝对路径。Compose 默认将 NAS 媒体库挂载为 `/data/library`，并将临时工作卷挂载为 `/data/work`。

```text
/data/library/youtube/{ExternalId}/
├── metadata/
├── subtitles/
├── chat/
├── thumbnails/
└── video/

/data/work/{TaskId}/
```

“下载视频并本地转写”会保留视频；“仅本地转写”主要保留转写结果及用于说话人样本试听的压缩音频。WAV 等中间文件在处理完成后删除。

## Redis 消息格式

默认 Stream 为 `forge:calendar:add`。标准消息包含 `Note.dbo.HololiveSchedule` 的主键：

```text
id = {HololiveSchedule.Id GUID}
```

系统使用配置的 SQL Server 连接查询 `HololiveSchedule`，读取 `StartDt`、`MemberName`、`StreamUrl`、`StreamTitle` 和 `StreamImage`。`Note` 数据库只用于按消息 ID 补全资料，不会被轮询，也不是另一个任务入口。无效消息或找不到对应日程时不会被确认，会保留在 Redis Pending Entries List 中以便排查。

## Docker 部署

### 前置条件

- Docker 与 Docker Compose
- 可访问的 Microsoft SQL Server
- 可访问的 Redis
- 可挂载的媒体存储目录或 NFS 共享

`compose.yaml` 启动 HoloScoop 和 Meilisearch。SQL Server 与 Redis 使用外部实例，不由本项目创建。

### 1. 创建数据库

使用具备建表权限的账号，在全新的 HoloScoop 数据库中执行：

```text
database/schema.sql
```

该脚本用于创建当前结构，不负责旧版本数据库的数据迁移。

### 2. 配置环境变量

复制示例文件：

```powershell
Copy-Item .env.example .env
```

Linux 或 macOS：

```bash
cp .env.example .env
```

编辑 `.env` 并设置：

| 变量 | 用途 | 默认值 |
| --- | --- | --- |
| `APP_PORT` | Web 服务宿主机端口 | `8080` |
| `MSSQL_CONNECTION_STRING` | HoloScoop 与 Note 数据库连接 | 必填 |
| `REDIS_CONNECTION_STRING` | Redis 连接字符串 | 必填 |
| `REDIS_STREAM_NAME` | 输入任务的 Redis Stream | `forge:calendar:add` |
| `MEILI_MASTER_KEY` | Meilisearch 主密钥 | 必填，至少 16 字节 |
| `MEILI_PORT` | Meilisearch 宿主机端口 | `7700` |

`.env` 已被 Git 忽略，不应提交真实连接字符串或密钥；`.env.example` 只包含示例值。

### 3. 配置媒体存储

项目中的 `compose.yaml` 已配置以下 NFS 共享：

```text
10.16.1.101:/volume1/media/Video/Hololive/library
```

如果部署环境不同，请修改 `library_data` 的 `addr` 和 `device`。容器内挂载位置应保持为 `/data/library`。

### 4. 启动服务

```bash
docker compose up -d --build
```

启动完成后访问：

```text
http://localhost:8080
```

查看服务状态与日志：

```bash
docker compose ps
docker compose logs -f app
```

## 运行特性与限制

- 所有转写、说话人分离和声纹计算均在本地执行，不需要云端语音服务、Hugging Face Token 或 NVIDIA GPU。
- 当前处理器按单进程串行执行媒体任务，以控制长直播处理时的 CPU 和内存占用。
- 说话人分离和声纹匹配属于辅助结果；重叠讲话、唱歌、变声、背景音乐和游戏音效会降低准确率。
- 多人直播的姓名建议必须人工确认，系统不会仅根据台词推测说话人身份。
- Meilisearch 数据可从 SQL Server 重建，不应作为唯一数据副本。
- 单台服务器上的媒体文件不等同于备份；数据库和人工确认的说话人数据应纳入独立备份策略。

## 安全说明

- 不要提交 `.env`、真实数据库连接字符串、Redis 密码或 Meilisearch 主密钥。
- 对外部署时应通过反向代理配置 HTTPS、访问控制和可信网络边界。
- `/api/streams/{streamId}/video` 与 `/api/streams/{streamId}/audio` 会提供本地媒体内容，不应在未配置访问控制的情况下直接暴露到公网。
