# 2026-09-08 官网 SEO 更新与发布

正式地址：https://www.modmanger.com/

## 发布结果

- Cloudflare Pages 项目：`modmanger`，生产分支：`main`。
- 本轮生产部署：`0cd11b59-1f9c-4003-bf24-c8403da17d28`。
- 部署地址：https://0cd11b59.modmanger.pages.dev
- 上一生产部署：`4620d78a-b4df-45a6-bcb2-63107a580e7b`，作为本轮回退目标。
- 正式域名、首页、帮助页及资源已在发布后核验。

## SEO 修改

原站 `/help.html` 已由 Cloudflare Pages 永久跳转到 `/help`，但 canonical 和站点地图仍指向 `.html` 地址。本轮将 canonical、Open Graph URL、JSON-LD、站点地图、内部链接和旧锚点跳转统一到正式的 `/help`，保留已有外部链接的跳转兼容性。

首页标题改为“爱酱MOD管理器官网｜Windows MOD 管理工具免费下载”；帮助页标题改为“爱酱MOD管理器使用帮助｜游戏设置、MOD 存储与旧版迁移”。描述说明 Windows 平台、实际 MOD 管理功能和无需登录的离线使用方式。首页仅微调简介，帮助页调整主标题，延续已上线的精简布局。

首页补齐 WebSite、WebPage、SoftwareApplication 结构化数据，帮助页使用独立 WebPage，并关联同一网站与软件。补齐分享信息、图片尺寸和图片说明，新增 48 × 48 PNG 站点图标。未添加虚构评分或 FAQ 富结果标记。

`robots.txt` 继续允许抓取。站点地图仅列首页与 `/help`，最后修改日期为 2026-09-08。`_headers` 仅对 404、图标来源说明和 IndexNow 验证文件加 `noindex`。版本部署地址继续使用 Cloudflare 自动提供的 `noindex`；正式首页与帮助页不包含禁止索引响应头。

本地检查增加了 canonical、分享地址、WebPage、站点地图和抓取设置的一致性校验。预览服务器支持省略 `.html` 的地址，打包脚本纳入 `_headers` 与站点验证文件。

## 下载区保护

首页 `#download` HTML、网盘文字、提取码、安装包说明、四张下载二维码及原始 `styles.css` 均未修改。发布前与原始基线/Git 原件比较，发布后再对正式站文件和下载区域 SHA-256 比较，结果全部一致。二维码目标及网盘端配置未调整。

## 验证结果

- 静态检查：3 个页面、87 处本地链接与资源引用通过；页面主标题、锚点和 JSON-LD 正常。
- 首页和帮助页在 320、390、768、1440 像素宽度下均无横向溢出；已保存并查看桌面和手机截图。
- FAQ 鼠标展开与 Enter 键收起通过；本地浏览器无控制台错误。
- 本地 `/help` 返回 200，`.html` 旧地址返回 308，跳转保留查询参数；不存在的页面返回 404。
- 正式站 18 个关键文件与发布包的 SHA-256 一致，包括两页内容、SEO 文件、图标、原样式及四张下载二维码。
- 正式站首页与 `/help` 返回 200，canonical、OG URL 和站点地图一致。
- `/help.html` → `/help`、`/index.html` → `/` 均保持 308；裸域名和 HTTP 地址继续以 301 跳转到 HTTPS 的 `www` 地址。
- 正式站 `/help.html#storage` 保留锚点；五个旧首页书签 `#migration`、`#privacy`、`#v2`、`#thanks`、`#support` 全部跳转到帮助页对应位置。
- 正式站无效页面返回 404，并含 `noindex`；正式站浏览器验收未发现控制台错误。

审计时使用 Googlebot、bingbot、Baiduspider 的模拟 User-Agent 均能获取首页 200 和产品内容。这只能说明当时未被这些请求的 UA 拦截，不能证明搜索引擎已经抓取或收录。

## 更新通知

于 2026-09-08 23:57（Asia/Shanghai）向官方 IndexNow 公共接口提交了首页和 `/help`。提交前确认正式站公开验证文件可访问且内容一致。接口返回 **HTTP 202**，含义为已收到请求、等待验证。

这不是已经收录或排名变化的证明，也不代表已向 Google、百度提交。搜索结果标题、摘要、站点图标和展示形式仍取决于搜索引擎后续重新抓取及处理。

## 保存与回退

- 网站源码：`D:/modmangerpd/codex-fix-build/website/`。
- 本轮发布包：`bin/website-seo-20260908/publish/`，旁附 `publish.sha256.json`，共 52 个文件（其中 `_headers` 由 Pages 作为配置处理）。
- 回退记录：`bin/website-seo-20260908/cloudflare-context.json`。
- 下载区对比：`bin/website-seo-20260908/preservation-check.json`。
- 本地 HTTP 和浏览器记录：`local-http-checks.json`、`browser-local-checks.json`。
- 线上记录：`live-verification.json`、`browser-live-checks.json`。
- IndexNow 回执：`indexnow-submission.json`。
- 上述 JSON 均位于 `bin/website-seo-20260908/`。
- 用户可查看的发布包、截图和验证记录：`C:/Users/a/Documents/Codex/2026-09-08/modmanger-seo/`。

如需回退，在 Cloudflare Pages 的 `modmanger` 生产部署历史中选择 `4620d78a-b4df-45a6-bcb2-63107a580e7b`。不要直接执行上一轮硬编码发布目录的部署脚本。

Cloudflare 令牌仅在授权文件和部署进程内使用；不进入源码、网站发布目录或发布压缩包。公开的 `indexnow-*.txt` 是站点验证文件，后续部署应保留。

参考：https://developers.cloudflare.com/pages/configuration/redirects/ 、https://www.indexnow.org/documentation
