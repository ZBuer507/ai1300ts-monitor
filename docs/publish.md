# 发布到 Git

本地仓库已经准备好：`main` 分支、首个提交、`.gitignore` 已排除构建产物与本地逆向资料。
剩下的是**创建远程仓库、配置认证、推送**，以下命令在项目根目录执行。

## 0. 检查本地状态

```powershell
git status
git log --oneline -1
```

应当看到工作区干净、`main` 分支上有一个提交。若 `git` 不在 PATH 中，重开终端或使用
`C:\Program Files\Git\cmd\git.exe`。

## 1. 确认提交身份（可选）

首个提交已使用本机 Git 身份 `zheng.jiatao`（邮箱见 `git log`），并且只对本仓库生效。
要改成别的身份，或设为全局默认：

```powershell
git config user.name "你的名字"
git config user.email "you@example.com"
git commit --amend --reset-author --no-edit
```

去掉 `--amend` 三行中的后两行，只改配置即可影响后续提交。

## 2. 创建远程仓库

在 GitHub 或 Gitee 新建**空仓库**：

- 不要勾选“添加 README / .gitignore / LICENSE”，否则推送会冲突；
- 仓库名建议 `ai1300ts-monitor` 或 `msicenter`；
- 由于源码包含硬件逆向内容，默认建议 **Private**。

建好后记下仓库地址，二选一：

```
git@github.com:<账号>/<仓库>.git
git@gitee.com:<账号>/<仓库>.git
```

## 3. 配置认证（二选一）

### 方案 A：SSH 密钥（推荐）

本机已经生成好 ed25519 密钥（`~\.ssh\id_ed25519`，无口令），只需要注册公钥：

```powershell
Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub | Set-Clipboard
```

把剪贴板里的公钥添加到：

- GitHub：Settings → SSH and GPG keys → New SSH key
- Gitee：设置 → SSH 公钥

如果想给私钥加口令：`ssh-keygen -p -f $env:USERPROFILE\.ssh\id_ed25519`。

验证连通：

```powershell
ssh -T git@github.com    # 或 ssh -T git@gitee.com
```

首次连接会询问是否信任主机指纹，输入 `yes`。

### 方案 B：HTTPS + 个人访问令牌（PAT）

- GitHub：Settings → Developer settings → Personal access tokens，勾选 `repo` 权限；
- Gitee：设置 → 私人令牌，勾选仓库读写。

推送时用户名填账号、密码填令牌；Windows 的 Git Credential Manager 会询问并记住。
不要把令牌写进 remote URL 或提交到仓库。

## 4. 关联远程并推送

```powershell
# 以 SSH + GitHub 为例
git remote add origin git@github.com:<账号>/<仓库>.git
git push -u origin main
```

HTTPS 方式把地址换成 `https://github.com/<账号>/<仓库>.git` 即可。
Gitee 如果默认分支是 `master`，推送后到仓库设置里把默认分支改为 `main`，或本地
`git branch -m main master` 后推送 `master`。

## 5. 验证

```powershell
git remote -v
git log --oneline -1
git status
```

浏览器打开仓库，确认文件齐全且没有 `artifacts/`、`work/`、`*.exe`。

## 6. 日常更新

```powershell
.\build.ps1 -Test
git add -A
git commit -m "说明本次改动"
git push
```

`artifacts/`、`work/`、`*.exe` 已被忽略；提交前用 `git status` 确认没有把构建产物带进去。

## 注意

- 原始 IL 导出（`work/*.il.txt`）、审计日志和旧版 exe 只保留在本地，未纳入仓库，
  避免包含第三方程序的导出内容。
- 仓库里只有源码、清单/图标、测试、脚本、文档与两张界面截图。
- 若要公开仓库，请自行确认 MSI/ASUS 相关协议描述的合规性。
