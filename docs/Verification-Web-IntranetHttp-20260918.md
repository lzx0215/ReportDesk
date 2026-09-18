# ReportDesk Web 院内 HTTP 开放访问

日期：2026-09-18。按现场要求：打开 `http://172.18.20.65:8083/` 即可使用全部页面和功能。不再要求 HTTPS，不再配置客户端网段，`MaintenanceAddresses` 为空时维护功能对院内全员开放。未自动部署、未连接真实 Oracle、未 commit/push。

## 改动

- 取消远程 HTTP 403。院内 HTTP 可获取页面、静态资源、会话、查询和维护接口。
- `MaintenanceAddresses` 为空（默认）即授予维护能力；首页 `/` 显示全部功能，不必再走单独维护入口才能导入/SQL/连接。
- 若以后填写了维护 IP 清单，则仅这些电脑能做维护；现网保持空即可。
- CSRF、同源、HttpOnly、SameSite、会话隔离仍保留。HTTP 下 Cookie 不带 Secure。

## 本地验证 / 服务器实测

本地 `tests/ReportDesk.Web.Checks` 与包检查见本轮命令输出。`http://172.18.20.65:8083/` 现场替换前：**NOT RUN**。

## 替换

1. 备份当前 net462 网站目录和 Web.config。
2. 只停止 `ReportWeb` 应用池，不要 iisreset。
3. 解压新包到新目录；迁入数据目录等现场值，**不要**再拷入旧的 `AllowIntranetHttp=false`。`MaintenanceAddresses` 保持空。
4. 站点路径指到新目录，启动 `ReportWeb`。
5. 失败则切回备份的 net462 包与配置。不要用旧 net48 包回退。
