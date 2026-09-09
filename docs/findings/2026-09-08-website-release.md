# 2026-09-08 官网精简与发布

正式地址：https://www.modmanger.com/

后续 SEO 更新已发布，最新部署和验证结果见 [2026-09-08-website-seo.md](2026-09-08-website-seo.md)。本文保留精简改版时的发布记录。

## 已发布

- Cloudflare Pages 项目：`modmanger`，生产分支：`main`。
- 新部署：`4620d78a-b4df-45a6-bcb2-63107a580e7b`。
- 部署地址：https://4620d78a.modmanger.pages.dev
- 上一生产部署：`b1b1be4e-55f4-4b00-9eb2-a0e32e2668db`，可在 Pages 部署历史中回退。
- 根域名 `https://modmanger.com/` 已确认跳转到 `https://www.modmanger.com/` 并显示新版。

## 页面调整

首页收敛为产品介绍、简短功能说明、原有下载区和四条折叠问答。游戏大图与重复截图墙替换为小尺寸游戏图标和一张明确标注的 HTML 管理界面示意。

迁移、存储、完整游戏列表、版本、隐私、鸣谢与支持归入 `website/help.html`。存储说明采用新版 WPF 窗口实机截图，隐私说明按现有实现修正。保留旧首页锚点的兼容跳转。

新增分享图片、站点地图、robots.txt 和 404 页面。官网仍为纯静态文件，无前端框架或运行依赖。

## 下载区保留

按用户要求，首页 `#download` 的 HTML、网盘文字、提取码、安装包说明、四张下载二维码和原始 `styles.css` 均保持原样。发布前后分别与正式站和本地原件做了字节/hash 比较，全部一致。二维码目标和网盘端配置均未更改。

## 验证

- 静态检查：3 个页面、87 处本地链接与资源引用通过；无重复锚点或无效结构化数据。
- 首页断点：320、390、620、768、1024、1440 像素；帮助页断点：320、390、768、1440 像素，无横向溢出。
- 下载锚点、问答鼠标与键盘展开、帮助目录导航通过。
- 本地五个旧锚点全部跳转成功；线上额外验证 `/#privacy` 到 `/help#privacy` 的跳转。
- 线上 14 个关键文件与已验证源码字节一致，包括四张下载二维码。
- 线上 404 返回正确状态和页面；浏览器未发现页面控制台错误。
- 首页引用图片总计从 14,855,647 字节降到 59,537 字节。此数值是图片文件之和，不是整个网页的网络传输量。

## 文件与复现

- 源码：`D:/modmangerpd/codex-fix-build/website/`。
- 本地预览：`node tools/website/preview.mjs`，http://127.0.0.1:4173/。
- 静态检查：`node tools/website/check.mjs`。
- 打包：`node tools/website/package.mjs <尚不存在的输出目录>`。
- 本次发布目录与 hash 清单：`bin/website-release-20260908/publish/`、`publish.sha256.json`。
- 回退上下文与线上验证记录：`bin/website-release-20260908/cloudflare-context.json`、`live-verification.json`。
- 用户可查看的发布包与截图：`C:/Users/a/Documents/Codex/2026-09-08/modmanger-site/`。

所有部署凭据均留在用户提供的环境文件及部署进程内，不进入网站源码或发布包。
