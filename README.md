## 目录结构

```
SldFile.CleanUsers/
├─ README.md                    GitHub 首页
├─ 使用说明.md                  ← 本文件
├─ .github/workflows/build.yml  云编译与自动发布
├─ 编译.bat                     双击一键编译（自包含单文件）
├─ SldFile.CleanUsers.sln       VS 解决方案
├─ global.json                  SDK 版本约束
├─ LICENSE                      上游 MIT 许可证
│
├─ src/
│  ├─ SldFile/                  ★ 核心库：解析容器 + 清理写回
│  │  ├─ SwFile.cs              打开 / 保存 / 容器分派
│  │  ├─ SwFile.Data.cs         对外数据面（Users / Metadata / Revisions / References）
│  │  ├─ SwFile.Sanitize.cs     ★ 清理逻辑（两阶段）
│  │  ├─ SldStreamRegistry.cs   命名流分发
│  │  ├─ MetadataStore.cs       OLE 属性流读写
│  │  ├─ Binary/                流解析器（Header2、Config-0、Section 容器、ZLB…）
│  │  ├─ Mfc/                   MFC 序列化解析（标签树、WChar、CStringArray）
│  │  └─ Ole/                   CFB 复合文档读写
│  │
│  ├─ Cli/                      命令行工具（net8.0）
│  │  └─ Program.cs             参数解析 + 备份 + 逐文件处理
│  │
│  └─ Gui/                      窗体程序（net8.0-windows，WinForms，纯代码无设计器）
│     ├─ Program.cs             入口
│     ├─ MainForm.cs            界面：拖放、文件列表（含用户名列）、选项、日志
│     └─ app.manifest           应用程序清单（高 DPI 由项目属性配置）
│
└─ 发布/、发布1/                本地编译输出（已 gitignore，不上传；云编译产物见 Releases）
```

> 核心库已裁剪：只保留「解析 + 清理写回」所需的部分（76 个源文件），
> 去掉了原项目的几何提取、3D 网格、BOM、工程图图纸、图标资源等与清理无关的模块。

---

## 编译

**要求**：.NET 8 SDK。产物为 **Windows x64 自包含单文件**，拷到没装 .NET 的电脑也能直接运行（单个 exe 约 60–150 MB）。

```powershell
# 方式一：双击 编译.bat

# 方式二：命令行
dotnet build SldFile.CleanUsers.sln -c Release

dotnet publish src/Gui/SldFile.CleanUsers.Gui.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o 发布\窗体版

dotnet publish src/Cli/SldFile.CleanUsers.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o 发布\命令行版
```

## 云编译与发布

[`.github/workflows/build.yml`](.github/workflows/build.yml) 使用 GitHub Actions 在 Windows 云端编译并发布 Release。

**发布方式**：在仓库「Actions」页面选择「云编译并发布」，点「Run workflow」，填写版本号（如 `1.0.0`）即可。工作流会自动编译两个版本、上传构建产物，并创建同名 Release 附带上两个 ZIP。

- 版本号可写 `1.0.0` 或 `v1.0.0`（自动补 `v` 前缀作为 Git 标签）。
- 版本号中含 `alpha` / `beta` / `rc` 时，Release 自动标记为预发布（Pre-release）。

---

## 用法

### 窗体版

1. 双击 `SolidWorks用户清理工具.exe`
2. 「添加文件 / 添加文件夹」，或把文件、文件夹**直接拖进窗口**
3. 列表会显示三列（用户名会完整列出、单元格内自动换行，行高随内容变高）：

   | 列 | 含义 |
   |---|---|
   | **文件** | 文件名 |
   | **用户名** | 该文件当前记录的**全部**用户名，用「、」分隔，不省略；「（无）」表示没有记录 |
   | **状态** | 待处理 / 已删除 N 个 / 无需清理 / 失败原因 |

4. 选项：
   - **彻底删除用户列表**（默认）：用户数归零，界面显示为空
   - **替换为占位符**：等长替换，只匿名化、不动结构
   - **备份原文件**（默认开启）：生成 `xxx.SLDPRT.bak-时间戳`
5. 点「开始清理」——每行的用户名与状态实时更新（绿=成功、灰=跳过、红=失败）

### 命令行版

```powershell
SldFile.CleanUsers.exe <文件或文件夹> [选项]

  --purge               彻底删除用户列表（条目数归零，显示为空）
  --replacement <名称>  占位用户名（默认 "user"，仅占位模式用）
  --dry-run             仅预览，不修改文件
  --no-backup           不生成 .bak 备份
  --recursive, -r       递归处理子文件夹
```

示例：

```powershell
.\SldFile.CleanUsers.exe "D:\交付\模型" --purge -r --dry-run   # 先预览
.\SldFile.CleanUsers.exe "D:\交付\模型" --purge -r             # 再执行
```

---

## 实现原理

### 1. 用户名存在哪里

`.sldprt / .sldasm / .slddrw` 是复合文档，用户名单存放于 MFC 序列化的 `Header2` 流的
`su_CStringArray` 元素中：

```
u16 条目数 N，随后 N 条 WChar 字符串：FF FE FF | 单字节字符数 | UTF-16 数据
```

**第一条是文件的创建者**。修订日志 `moLogs_c` 的每条记录通过 `Handle`（索引）指向该数组。

### 2. 为什么只清 Header2 会「复活」

同一个名字在文件里**存了多达 8 份**：

| 流 | 说明 |
|---|---|
| `Header2` | 主用户名单 |
| `Contents/Config-0` | 每个配置的前端副本 |
| `Contents/Config-0-ModelHeader` | Header2 的完整镜像 |
| `Contents/CMgr` | 配置管理器 |
| `Contents/Definition` | 定义数据（CStringArray、UpdateModelEnv） |
| `Contents/DisplayLists` | 显示列表 |
| `ThirdPtyStore/VisualStates` | 第三方状态存储 |
| `_MO_VERSION_*/Biography` | 访问路径历史 |

SolidWorks 打开时汇总这些副本，保存时**按内存名单重写 Header2**，于是刚清掉的名字又回来了。
必须全流覆盖。

### 3. 两阶段清理（`src/SldFile/SwFile.Sanitize.cs` → `ClearUsers()`）

**阶段一 · 结构化清除**
`Header2`、`Contents/Config-0`、`Contents/Config-0-ModelHeader` 三处，把 `su_CStringArray`
重写为 `count = 0`，数组后的终止符与其余元素原样保留。

**阶段二 · 深度清除**
其余流中残留的名字用**等长空格**原地覆盖（长度不变、偏移不动），并做词边界校验
（只认 ASCII 字母数字，避免把中文标签当成「长单词」而漏清）。

**刻意保留**：外部引用路径中的用户名（`C:\Users\Administrator\...`）——它是装配定位数据，
删掉会导致装配体 / 工程图找不到零件。

### 4. 关键 API

```csharp
using var doc = SwFile.Open(@"D:\零件.SLDPRT", readOnly: false);

doc.Metadata.Author = "";
doc.Metadata.LastSavedBy = "";

IReadOnlyList<string> removed = doc.ClearUsers();   // 彻底清空
// 或 doc.SanitizeUsers("user");                    // 等长占位符

doc.Save();                                          // 合并待写入流后落盘
```

---

## 已知限制

1. **SolidWorks 每次保存都会重新记入当前 Windows 登录用户**，这是它的正常行为。
   清理要放在**交付前最后一步**；清完不要在 SW 里再保存。
2. 引用路径中的用户名（`C:\Users\xxx\...`）保留不动，否则装配关系会断。
3. 仅处理 `.sldprt / .sldasm / .slddrw`，其它文件自动跳过。
4. 默认每次运行生成 `.bak-时间戳` 备份；清理后建议用 SolidWorks 打开确认一次
   （**只看不保存**），没问题再删备份。
5. 新旧两种容器格式（纯 OLE / Section 容器）均已支持，含中文流名的最新版文件也已适配；
   极端异常的文件会被跳过并在日志标注「失败」，不会写坏原文件。

---

## 许可

核心解析库版权归上游项目作者（MIT，见 `LICENSE`）；
`SwFile.Sanitize.cs`、`src/Cli`、`src/Gui` 为实现清理能力的新增部分，随本包一同以 MIT 提供。
