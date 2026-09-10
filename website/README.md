# 爱酱MOD管理器官网

纯静态站点，无前端构建依赖。正式域名为 https://www.modmanger.com/，Cloudflare Pages 项目为 `modmanger`，生产分支为 `main`。

## 页面

- `index.html`（正式地址 `/`）：产品介绍、简短功能说明、下载区、四条折叠问答。
- `help.html`（正式地址 `/help`）：初次使用、完整游戏预设、存储、迁移、版本、隐私、鸣谢与支持。
- `404.html`：无效地址的返回入口。
- `release.css`：新版首页与帮助页样式，使用 `lp-` 类名前缀。
- `styles.css`：沿用原站样式，保留下载区域的原有呈现。

首页的管理界面为 HTML 示意，MOD 名称是演示内容；帮助页使用新版存储设置实机截图。游戏图标来源记录在 `assets/games/game-icon-sources.md`。资源目录保留现用图片，以及原有网盘和捐赠二维码；未被页面引用的旧原型图和已替换图标已移除。

## 网盘区域

本次改版完整保留首页 `#download` 区域的原始 HTML，以及百度、迅雷、UC、夸克四张下载二维码。网盘文字、提取码、安装包说明与跳转方式均未调整。二维码目标由网盘端控制。

旧首页的 `#migration`、`#privacy`、`#v2`、`#thanks`、`#support` 会转到帮助页的对应位置；无 JavaScript 时仍可通过页脚同名链接访问。`#download` 和 `#faq` 保留在首页。

## 本地预览与检查

从仓库根目录运行，使用 Node.js 18 或更新版本：

```powershell
node tools/website/preview.mjs
```

打开 http://127.0.0.1:4173/。服务器仅监听本机，文件修改后刷新即可。

```powershell
node tools/website/check.mjs
```

检查页面标题、重复锚点、本地资源、内部链接、结构化数据，以及 canonical、分享地址、站点地图和抓取设置的一致性。

预览服务器支持 `/help`，并模拟 Cloudflare Pages 的 `/help.html` → `/help`、`/index.html` → `/` 永久跳转。默认端口被占用时，先设置 `$env:WEBSITE_PORT = '4174'` 再运行预览。

## 搜索与分享

可索引内容页只有首页和 `/help`。两页使用各自的标题、描述、canonical 和 WebPage 结构化数据；首页另含 WebSite 与 SoftwareApplication 标记。站点不添加虚构评分，搜索结果的样式与摘要由搜索引擎决定。

`robots.txt` 允许抓取并声明 `sitemap.xml`；站点地图只列正式地址。修改页面实质内容时同步更新相应 `lastmod` 日期。Cloudflare Pages 自带的 `.html` 跳转及根域名到 `www` 的跳转继续保留。

`_headers` 仅对 404、图标来源说明和 IndexNow 验证文件设置 `noindex`，不阻止抓取首页或帮助页。分享图片为 `assets/share.png`，搜索结果使用的 48 × 48 PNG 图标为 `assets/favicon-48.png`。

`indexnow-*.txt` 是公开的站点验证文件，不是 Cloudflare 凭据，后续部署需保留。通过 IndexNow 提交更新前，确认正式域名可以读取该文件；提交时使用对应 `keyLocation`，只提交首页和 `/help`。HTTP 200 表示请求已收到，202 表示已收到且等待验证；均不保证收录时间或排名，也不等同于向 Google、百度提交。

## 打包与发布

使用一个尚不存在的输出目录：

```powershell
node tools/website/package.mjs bin/website-publish-20260908
wrangler pages deploy bin/website-publish-20260908 --project-name modmanger --branch main
```

打包脚本仅收集静态页面、资源、`_headers` 和 IndexNow 验证文件，并在发布目录旁生成 SHA-256 清单；开发说明、工具和环境文件不会进入发布目录。Wrangler 使用本机已有登录或当前进程的 `CLOUDFLARE_API_TOKEN`。

发布后检查正式域名的首页、帮助页、旧锚点跳转和下载二维码，并核对 canonical、站点地图及响应头。需要回退时，在 Cloudflare Pages 的生产部署历史中回退到前一个成功部署。
