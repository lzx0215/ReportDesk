# HIS 报表发布机制：跨电脑证据摘要

原检查时间：2026-09-17。整理时间：2026-09-18。

结论来自本地 `LIB` 程序的对应反编译代码、当前二进制与反编译清单的 SHA-256 核对、已有 HIS DDL。没有连接医院生产库，没有点击发布，也没有做运行时抓包或端到端更新测试。

本文提供完整语义、类/方法名和表结构摘要。另一台电脑不需要存在原来的 E 盘，便可继续设计；如果以后需要复核原始实现，可在有权访问的同版本程序中按这些符号定位。

## 1. 按钮到数据库

| 步骤 | 程序集 / 类 / 方法 | 已观察到的行为 |
| --- | --- | --- |
| 1 | FS.Core.UI / FS.Core.UI.Report.Common.Implement.ucManagerForm / InitEvent | btUpdata.Click 绑定 btUpdata_Click |
| 2 | 同类 / btUpdata_Click、AutoUpdate | 确认“是否确认将报表设置同步到服务器端？”后调用 updateCompleteHandler |
| 3 | FS.Core.UI / 同命名空间 ucCommonWindow | updateCompleteHandler 绑定 UpdateXml |
| 4 | ucCommonWindow.UpdateXml | 构建 Release，说明“一键发布配置信息”加 resourceID，记录操作者与时间 |
| 5 | ucCommonWindow.FillReleaseFileInfo | 读取本地文件，记录相对目录、文件名，用 WCompression.CompressFile 压缩 |
| 6 | FS.Manager.BLL / FS.Manager.BLL.SystemManager.SaveRelease | 保存发布主记录，关联并保存每个 ReleaseFile；带 Transaction 标记 |
| 7 | FS.Manager.DAL / FS.Manager.DAL.SystemManager.ReleaseLogic、ReleaseFileLogic | 映射到 SYS_UPDATE_RELEASE、SYS_UPDATE_RELEASE_FILE，Content 映射 CONTENT |

发布文件来源是 `MainDWDataObject`、`Report.QueryFilePath`，以及有内容时的 `DetailDWDataObject`。不是每次上传整个 LIB。

`SaveRelease` 的顺序是：取得发布主键 → 保存主记录 → 为每个文件填写 ReleaseID → 保存各文件。事务特性的运行时行为没有独立测试，不把“有标记”写成“生产原子性已验证”。

## 2. 数据表字段

### SYS_UPDATE_RELEASE

| 字段 | 含义 / 代码类型 |
| --- | --- |
| RELEASE_ID | 发布编号，Int32 |
| PUBLISHER | 发布人文字 |
| DESCRIPTION | 发布说明 |
| VERSION | 版本文字；本报表发布方法设为空 |
| FILECOUNT | 文件数量文字；本方法固定写 2，不能当唯一完整性依据 |
| OPER_ID | 操作人编号 |
| OPER_TIME | 操作时间，Date |

### SYS_UPDATE_RELEASE_FILE

| 字段 | 含义 / 代码类型 |
| --- | --- |
| FILE_ID | 文件编号，Varchar |
| RELEASE_ID | 所属发布编号，Int32 |
| FILE_NAME | 文件名 |
| DESCRIPTION | 文件描述 |
| DIRECTORY | 相对目录 |
| DIRECTORY_FLAG | 是否目录包 |
| CONTENT | 压缩文件字节，BLOB |

已有 HIS DDL 明确建表为 `HIS.SYS_UPDATE_RELEASE` 和 `HIS.SYS_UPDATE_RELEASE_FILE`。将来连接原库时核对实际 owner 与只读权限，不能因为代码没有显式 schema 就推断任何连接用户都能访问。

## 3. 压缩与客户端更新

`Fs.Core.dll` 的 `FS.Core.Util.WCompression.CompressFile` 先读完整文件，再调用 CompressBytes。CompressBytes 使用 SharpZipLib 的 ZipOutputStream 和空名称 ZipEntry，写入原文件字节。DecompressBytes 使用 ZipInputStream 读取首个条目的内容。

所以：CONTENT 不是明文 XML，不应直接用字符串编码解码；输出文件名要结合表中的 FILE_NAME 和 DIRECTORY。解压要实现受限输出目录和大小控制。不要为实现该格式而运行整个 HIS 客户端或加载其业务服务。

`HIP.exe` 的 `FS.HIP.Program` 在正常程序启动路径调用：

```text
SystemUpdate.Update(config, args)
SystemUpdate.MoveFile(config, args)
```

`FS.HIP.SystemUpdate.Update` 内部的行为是：

```text
QueryRelease(LastReleaseID, LastTime)
  → QueryReleaseFile(release.ID)
  → GetReleaseFileContent(file.ID)
  → 解压到 StartupPath / UpdateDirectory / 相对目录
  → 记录最后发布编号和时间
MoveFile
  → 复制到客户端运行目录，必要时启动独立更新程序
```

原 QueryRelease 的 DAL 同时添加 RELEASE_ID 大于和 OPER_TIME 大于的过滤。未来同步不能直接据此承诺不会漏掉并发晚提交记录，需要另外设计回查与幂等。

自动更新程序重新启动的 `StartMode=Auto` 分支跳过再次下载。已打开 HIS 中的单纯注销登录是否执行 Program 启动路径，没有确认。

## 4. 发现的边界

1. 本地文件用于编辑与运行，发布后数据库中同时保存压缩内容；服务器磁盘是否有同名 XML 不影响此结论。
2. 此表用于系统更新，可能同时存在 DLL、EXE、文件夹包。只同步已识别的报表 XML 和必要配套内容。
3. VERSION 可能为空、FILECOUNT 可能与实际文件数不一致；依据实际文件清单和内容验证。
4. FillReleaseFileInfo 捕获异常后未向外抛出，内容可能缺失；客户端返回成功文字不能替代接收端完整性检查。
5. 未发布本地编辑不应自动进入网页正式报表库。
6. 已发布文件可能同时涉及查询定义和显示定义；应同批校验、整体切换。

## 5. 已核对二进制哈希（2026-09-17）

这些值是上次静态检查时的记录；本次仅转录，没有把它们宣称为生产服务器当前版本。换一套 LIB 时需重新核对。

| 文件 | SHA-256 |
| --- | --- |
| FS.Core.UI.dll | 49E74C98EC905E05AB20EA6507FF78CAEB4C2FD786C7E45F867CD191CA9E98C7 |
| FS.Manager.BLL.dll | E5F85E3B80C36E89A4D7ED3A868A2FE6A12D1F71D146FF2707E5C08595A06053 |
| FS.Manager.DAL.dll | EAD5BEEBB4D0DCA00E035EB38AF031C735D2AE56F19B5EB40250D8DD6D682AB3 |
| FS.Manager.DMO.dll | 5DD42DC068998B193C4F95A9DEC804302DBC3FBBA17611764203A6AF83E86510 |
| Fs.Core.dll | 32BC6D8CE0762D8D0E653C82C5ED411AFD989F940607BDD1A54574EDFBE2E180 |
| HIP.exe | 108A0D218A88C287AF8DA6B38CE2F50E1FDFD222B1CDADD75E746A9BA16AB9CC |

## 6. 原电脑定位记录（仅供追溯）

原反编译根目录：`E:\his\work\01_src`。下列“目录 ID / 仓库内路径”的组合只作定位证据，不是新电脑运行依赖。

| 目录 ID | 文件及关键行 |
| --- | --- |
| 5FFE7BD20538207D59BAEC7A | FS/Core/UI/Report/Common/Implement/ucManagerForm.cs：159、303、327 |
| 5FFE7BD20538207D59BAEC7A | FS/Core/UI/Report/Common/Implement/ucCommonWindow.cs：389、420、1653 |
| B29F117DE56A17201E37BC07 | FS/Manager/BLL/SystemManager.cs：932、939、946、953 |
| 8D963AB7640AE4F50D5F5F34 | FS/Manager/DAL/SystemManager/ReleaseLogic.cs：49；ReleaseFileLogic.cs：22、53、80 |
| 20F5DF214CD1042E9B8359F7 | FS/Manager/DMO/Dao/SystemManager/SYS_UPDATE_RELEASE_FILE.cs：130 |
| BD9707695E18931D8B19E444 | FS/HIP/Program.cs：265；FS/HIP/SystemUpdate.cs：20、108 |
| 6B2FE89968F47B8279DCDDD1 | FS/Core/Util/WCompression.cs：96、123、281 |

原 DDL：`E:\his\数据库ddl\HIS.sql`，建表位置 72790、72858 行。此 DDL 包含其他破坏性操作，禁止为验证这两张表而执行整份 DDL。

## 7. 未来最小验证

使用既有已发布样本，只读取得发布与文件元数据，选一个已知报表文件的 BLOB，在隔离目录解压后与对应已更新 XML 比较字节哈希。

核对对象是“我们的读取实现”，不是医院各电脑的一致性。没有样本不阻止当前方案评审；无需为了本方案重新发布、重新登录全院客户端或导出全部业务数据。
