-- Oracle 19c；仅在已授权的只读连接中执行。
-- 本交接阶段未执行；不包含连接凭据、DDL、DML、授权或发布操作。
-- 用途：开发同步功能时确认发布表字段及已有报表发布元数据。
-- HIS 是本地已有 DDL 的 owner；若现场实际 owner 不同，先核实再调整。
-- 这些结果用于验证读取方法，不用于质疑各客户端报表的一致性。

-- 1. 查看当前连接可见的两张表字段；无结果可能是权限或 owner 问题，
--    不能据此断言表不存在，更不能重新导入整个 HIS DDL。
SELECT owner, table_name, column_id, column_name, data_type, data_length
FROM all_tab_columns
WHERE owner = 'HIS'
  AND table_name IN ('SYS_UPDATE_RELEASE', 'SYS_UPDATE_RELEASE_FILE')
ORDER BY table_name, column_id;

-- 2. 最近 10 次“一键发布”及其实际文件清单；只取 BLOB 长度，不返回内容。
--    此描述前缀来自已检查的本地实现；无记录时先核对现场版本或历史清理情况。
WITH recent_report_releases AS (
    SELECT release_id, description, oper_time, version, filecount
    FROM HIS.SYS_UPDATE_RELEASE
    WHERE description LIKE '一键发布配置信息%'
    ORDER BY oper_time DESC, release_id DESC
    FETCH FIRST 10 ROWS ONLY
)
SELECT r.release_id,
       r.description AS release_description,
       r.oper_time,
       r.version,
       r.filecount AS declared_file_count,
       f.file_id,
       f.file_name,
       f.directory,
       f.directory_flag,
       DBMS_LOB.GETLENGTH(f.content) AS compressed_bytes
FROM recent_report_releases r
LEFT JOIN HIS.SYS_UPDATE_RELEASE_FILE f ON f.release_id = r.release_id
ORDER BY r.oper_time DESC, r.release_id DESC, f.file_id;

-- 后续只需选定一个已知报表 FILE_ID，在获准开发验证中读取对应 CONTENT，
-- 解压到隔离目录并比较原文件哈希；不要直接把 BLOB 转成文本，
-- 不需要重新发布，也不需要导出全库或所有患者数据。
