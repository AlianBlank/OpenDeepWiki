# OpenDeepWiki · HK 公网部署

连接已有远程 PG，3 容器：`opendeepwiki`（生成/导出）、`docs-sync`（Docs 仓自动 commit+push）、
`web`（管理后台，经宿主 nginx 反代对外）。

## 链路

```
源仓 push GitHub → 增量 worker 轮询（匿名拉公开仓）→ 生成窗口内再生成+翻译
→ 自动 Rspress 导出 → ./gfx-docs → docs-sync ≤2min commit+push → GitHub Docs 仓
新包加入 registry → AutoSync 每小时发现 → 入库生成 → 同上
浏览器 → https://域名 （nginx 443）→ web → 管理后台
```

## 镜像（GHCR，绑定本仓库）

`.github/workflows/docker-image.yml`：push 到 main 或手动触发，即构建并推送
`ghcr.io/alianblank/opendeepwiki` / `ghcr.io/alianblank/opendeepwiki-web`
（tag：`latest` + 版本号），无需任何 secrets（`GITHUB_TOKEN` 推送）。

- **首次推送后**：GitHub 仓库页右侧 Packages → 进包设置把 Visibility 改为 **Public**
  （各做一次），HK 才能匿名 `docker pull`
- 服务器更新镜像：`docker compose pull && docker compose up -d`

## 前置清单（一次性，按序）

1. **PG 可达**：远程 PG 放行 HK 服务器出口 IP
2. **执行 `sql/01-fix-local-urls.sql`**：local:: 仓 URL 改成 GitHub 地址
3. **建 PAT**：GitHub → Settings → Developer settings → Fine-grained tokens，
   仅 `GameFrameX/Docs`，Contents: Read and write
4. **Docs 仓清理**（不清则站点展示错乱）：删 `.auto/` 旧区、`_nav.json` 改指新路径、
   rspress `route.include` 移除 `**/.auto/**`
5. **本地停机**：本地容器 `make down` 且不再 up —— 同一库双后端会双消费抢任务、双份 token

## 部署

```bash
mkdir -p ~/odw && cd ~/odw          # 放入本目录所有文件
cp .env.example .env && vim .env    # 填 5 组必填
sudo cp nginx/odw.conf.example /etc/nginx/conf.d/odw.conf && sudo nginx -s reload
docker compose up -d
```

## 验证

```bash
docker compose ps                          # 3 容器 Up
docker compose logs docs-sync | tail -20   # clone 成功、无 push 失败
curl -f http://127.0.0.1:18081/health      # healthy
```

浏览器开 `https://域名` 登录管理台。改任一源仓文件 push，等增量周期 + 生成窗口，
确认 Docs 仓出现 `docs(sync)` commit。

## 运维

| 事项 | 命令 |
|---|---|
| 日志 | `docker compose logs -f opendeepwiki` / `docs-sync` |
| 换 PAT | 改 `.env` → `docker compose restart docs-sync` |
| 换 JWT 密钥 | 改 `.env` → `docker compose restart opendeepwiki`（已登录态全失效，重新登录） |
| 手动重推某仓导出 | `curl -X POST http://127.0.0.1:18081/api/admin/repositories/<id>/export-rspress -H "Authorization: Bearer <token>"` |
| Failed 仓重试 | 管理台 regenerate，或 `POST /api/admin/repositories/<id>/regenerate` |

## 红线

- **生成端唯一**：别在任何其他机器再跑 opendeepwiki 后端连这个库
- **bot 独占 `docs/<lang>/<docPath>/` 目录**：手工文档别放这些路径，会被导出覆盖
- 公网安全底线：`JWT_SECRET` 必填（compose 强制）、管理员强密码、建议启用 nginx basic auth
