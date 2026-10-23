# 爱酱MOD管理器官网

纯静态站点，无前端构建依赖。正式域名为 https://www.modmanger.com/，Cloudflare Pages 项目为 `modmanger`，生产分支为 `main`。

## 页面

- `index.html`（正式地址 `/`）：首屏宣传片、五个卖点、支持游戏墙、下载区、常见问题、交流与支持入口。
- `help.html`（正式地址 `/help`）：初次使用、完整游戏预设（含引擎）、存储、迁移、版本、隐私、鸣谢与支持。
- `404.html`：无效地址的返回入口。
- `release.css`：首页、帮助页与 404 的样式，使用 `lp-` 类名前缀。
- `styles.css`：沿用原站样式，下载区的基础样式仍在这里，`release.css` 只在 `.lp-home` 下覆盖外观。

首页信息架构（2026-10-22 改版）：首屏（AI 主视觉背景 + 标题 + 一句副文 + 唯一的下载按钮 + 宣传片播放器）→ `#features` 五个卖点（导入 / 方案 / 冲突 / 回滚 / 整理，配图取自宣传片画面）→ `#games` 游戏墙（11 个内置预设 + 自定义游戏、引擎标签）→ `#download` → `#faq` → 交流与支持。导航只留 `#features`、`#games`、`#faq` 与帮助页；下载走右上角按钮（`#download`）。

游戏图标来源记录在 `assets/games/game-icon-sources.md`。帮助页使用新版存储设置实机截图。

## 宣传片与图片素材

- `assets/video/promo-v5b-1080p.mp4`（约 13 MB）与 `promo-v5b-720p.mp4`（约 7 MB）：由 `tools/promo/v5/out/` 的成片用 H.264 两遍编码压出，AAC 96k，`+faststart`。`<source media="(max-width: 760px)">` 让手机走 720p。
- `assets/video/promo-v5b-poster.webp`：播放器封面（1600 × 900）。
- v5b：修复导入段进度条重叠与冲突段序号（2026-10-22）。
- `assets/hero-hub-800.webp` / `hero-hub-1600.webp`、`assets/download-stage.webp`：来自 `tools/promo/v5/assets/gen/` 的 AI 主视觉。
- `assets/features/feature-01..05.webp`：宣传片五个功能段的界面截帧（1020 × 790），图中 MOD 名称为演示内容。

播放器使用 `preload="none"`，用户点击前不下载视频，也不自动播放。`_headers` 对 `/assets/video/*` 设置一年 `immutable` 缓存，**更换视频时必须换文件名**（如 `promo-v6-*.mp4`），否则老访客会一直看到旧片。Cloudflare Pages 单文件上限 25 MiB，`check.mjs` 会检查。

重新压制命令（在仓库根目录，`ffmpeg` 为 `tools/promo/node_modules/@ffmpeg-installer/win32-x64/ffmpeg.exe`）：

```bash
# 每种分辨率各跑两遍：-pass 1 输出到 NUL（-an -f mp4），-pass 2 输出成品
ffmpeg -i 成片.mp4 -vf scale=1920:1080:flags=lanczos -c:v libx264 -preset slow -profile:v high -level 4.1 -pix_fmt yuv420p   -b:v 2000k -maxrate 4000k -bufsize 4000k -g 60 -pass 2 -c:a aac -b:a 96k -ac 2 -movflags +faststart promo-vN-1080p.mp4
# 720p：scale=1280:720，-b:v 1000k -maxrate 2000k -bufsize 2000k
```

## 网盘区域

首页 `#download` 区域保留原有的文字、版本号、安装包说明、使用说明书链接，以及百度、迅雷、UC、夸克四张下载二维码与提取码；二维码目标由网盘端控制。2026-10-22 改版只做了外观调整（背景图、卡片样式、二维码图片补了宽高防止布局抖动），并在二维码下方增加“三步上手”和运行库说明。2026-10-22 晚间精简文案：首屏说明书小字、视频说明行与章节按钮、游戏区导语与底注已删除（MOD 前置/版本免责说明移到帮助页 `#games`）；版本号保留在标题与首屏按钮，不再单列。
2026-10-23 再次精简（文案原则：只留对玩家有信息量的内容，不写解释性小字）：删除首屏「看 52 秒宣传片」按钮与四条基本信息、导航里的「宣传片」「下载」、功能区 kicker 与各卖点补充小字（每个卖点只留标题 + 一句话）、游戏区「自定义游戏可选引擎」行、下载区 kicker/说明/安装包文件名/三步上手/迁移提示、FAQ kicker 与重复的 QQ 链接、支持区说明句；功能配图改用修复后的 v5 帧重新截取。帮助页删除已下线的硬链接说明。
2026-10-23 同步 v2.1.2：首屏/下载区版本号改为 v2.1.2，下载区加更新日期；卖点与 FAQ 改成玩家口吻短句；游戏区补一句多引擎说明（UE、Unity、RE Engine、Godot、Decima）；帮助页“这一版”改为 v2.1.2 要点，帮助页导航与首页一致（去掉「宣传片」「下载」）；隐私说明写明统计默认不上报。

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

检查页面标题、重复锚点、本地资源（含 `poster`、`srcset`）、内部链接、结构化数据，以及 canonical、分享地址、站点地图和抓取设置的一致性，并确认没有超过 25 MiB 的文件。

预览服务器支持 `/help`，并模拟 Cloudflare Pages 的 `/help.html` → `/help`、`/index.html` → `/` 永久跳转，也支持视频需要的 Range 请求（206）。默认端口被占用时，先设置 `$env:WEBSITE_PORT = '4174'` 再运行预览。

## 搜索与分享

可索引内容页只有首页和 `/help`。两页使用各自的标题、描述、canonical 和 WebPage 结构化数据；首页另含 WebSite、SoftwareApplication 与宣传片的 VideoObject 标记（`uploadDate` 按实际上线日期修改）。站点不添加虚构评分，搜索结果的样式与摘要由搜索引擎决定。

`robots.txt` 允许抓取并声明 `sitemap.xml`；站点地图只列正式地址。修改页面实质内容时同步更新相应 `lastmod` 日期。Cloudflare Pages 自带的 `.html` 跳转及根域名到 `www` 的跳转继续保留。

`_headers` 仅对 404、图标来源说明和 IndexNow 验证文件设置 `noindex`，不阻止抓取首页或帮助页；另对 `/assets/video/*` 设置长缓存（见上文“宣传片与图片素材”）。分享图片为 `assets/share.png`，搜索结果使用的 48 × 48 PNG 图标为 `assets/favicon-48.png`。

`indexnow-*.txt` 是公开的站点验证文件，不是 Cloudflare 凭据，后续部署需保留。通过 IndexNow 提交更新前，确认正式域名可以读取该文件；提交时使用对应 `keyLocation`，只提交首页和 `/help`。HTTP 200 表示请求已收到，202 表示已收到且等待验证；均不保证收录时间或排名，也不等同于向 Google、百度提交。

## 打包与发布

使用一个尚不存在的输出目录：

```powershell
node tools/website/package.mjs bin/website-publish-20260908
wrangler pages deploy bin/website-publish-20260908 --project-name modmanger --branch main
```

打包脚本仅收集静态页面、资源、`_headers` 和 IndexNow 验证文件，并在发布目录旁生成 SHA-256 清单；开发说明、工具和环境文件不会进入发布目录。Wrangler 使用本机已有登录或当前进程的 `CLOUDFLARE_API_TOKEN`。

发布后检查正式域名的首页、帮助页、旧锚点跳转和下载二维码，并核对 canonical、站点地图及响应头。需要回退时，在 Cloudflare Pages 的生产部署历史中回退到前一个成功部署。

**这个 Pages 项目没有连接 Git 仓库**（`source` 为 `null`），推送到 GitHub 不会触发任何部署，每次都必须手动执行上面的 `wrangler pages deploy`。也不要直接部署整个 `website/` 目录：本文件的 `README.md` 会被当成静态资源发布到站点根路径，正确做法始终是先跑 `package.mjs` 生成发布目录。
