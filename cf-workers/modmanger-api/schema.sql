-- 用量统计的 D1 表结构（注册数 / 在线数）
--
-- 部署方式（负责人执行一次即可，wrangler deploy 不会自动建表）：
--   npx wrangler d1 create modmanger-stats          # 把返回的 database_id 填进 wrangler.toml
--   npx wrangler d1 execute modmanger-stats --remote --file=schema.sql
--
-- 全部语句都带 IF NOT EXISTS，重复执行是安全的（升级时可以再跑一遍）。
--
-- 只有两张表，因为只有两个数字要回答：「有多少人注册过」和「此刻有多少人开着」。
-- 没有事件流、没有会话表、没有属性表——那些是产品分析平台的形状，不是这里的需求。

-- 设备：一台装了本程序的机器。
--
-- id 是**客户端自己生成的随机 UUID v4**，与硬件序列号、MachineGuid、MAC、
-- 机器名、用户名、邮箱全都没有关系。代价说清楚：重装系统或换机会产生新 id，
-- 累计设备数因此偏高。这是隐私换准确度的自觉取舍——这个数字本来就是趋势参考，
-- 而硬件指纹会把「基础统计」的叙事直接变成「设备追踪」。
--
-- 刻意不存的字段：IP、机器名、用户名、安装路径、游戏库、MOD 名称。
-- 每多存一个，合规叙事就弱一分，而它们一个都不服务于那两个数字。
CREATE TABLE IF NOT EXISTS devices (
  id         TEXT PRIMARY KEY,   -- 随机 UUID v4（小写）
  first_seen INTEGER NOT NULL,   -- unix 秒，**服务端时间**（客户端时钟可以是任意值）
  last_seen  INTEGER NOT NULL,
  ver        TEXT,               -- 应用版本，如 2.0.5
  os         TEXT                -- Windows 版本号，如 10.0.26200
);

-- 「此刻在线」= last_seen 落在最近一个时间窗内，是看板每次刷新都要跑的查询。
CREATE INDEX IF NOT EXISTS idx_devices_last_seen ON devices(last_seen);

-- 「每日新增」趋势线按 first_seen 分组。没有这个索引就得全表扫。
CREATE INDEX IF NOT EXISTS idx_devices_first_seen ON devices(first_seen);

-- 账号：登录过的邮箱，以哈希形式存在。
--
-- hash 由**客户端**算好再上报（邮箱规范化后 SHA-256，取前 32 位十六进制），
-- 明文邮箱永远不离开用户本机——服务端连一次也没见过，因此也没有「服务端不小心
-- 把邮箱写进日志」这种事故的可能。
--
-- 主键即去重：同一个人换设备、重装、反复登录都只占一行，注册数天然是去重值。
CREATE TABLE IF NOT EXISTS accounts (
  hash       TEXT PRIMARY KEY,   -- 32 位十六进制（小写）
  first_seen INTEGER NOT NULL,   -- 首次登录（服务端时间）
  last_seen  INTEGER NOT NULL    -- 最近一次登录
);

CREATE INDEX IF NOT EXISTS idx_accounts_first_seen ON accounts(first_seen);
