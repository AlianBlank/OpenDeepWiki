-- 一次性：把 local:: 本地导入的仓改为 GitHub 地址（HK 解析不了本地路径）
-- 在已有远程 PG 上执行。先跑 ① 核对，确认无误后填好 ② 的 <ORG> 再跑 ②
-- 表/列名依据 Repository 实体（EF Core 映射，PG 中带引号的 PascalCase）

-- ① 核对：当前 local:: 的仓都有哪些
SELECT "RepoName", "OrgName", "GitUrl", "Status"
FROM "Repositories"
WHERE "GitUrl" LIKE 'local::%'
ORDER BY "RepoName";

-- ② 修复：RepoName 即 GitHub 仓库名（先替换 <ORG> 为你的 GitHub 组织/用户名）
-- UPDATE "Repositories"
-- SET "GitUrl" = 'https://github.com/<ORG>/' || "RepoName" || '.git'
-- WHERE "GitUrl" LIKE 'local::%';

-- ③ 验证：应返回 0 行
-- SELECT COUNT(*) FROM "Repositories" WHERE "GitUrl" LIKE 'local::%';
