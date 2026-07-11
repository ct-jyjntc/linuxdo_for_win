# LinuxDo — Windows 客户端

非官方 [LinuxDo](https://linux.do) 原生 Windows 客户端，基于 Discourse REST API，使用 **WinUI 3 + Windows App SDK** 复刻自 macOS 版。

## 功能

### 浏览
- 最新 / 热门（日/周/月/年/全部）/ 新主题
- 分类树、标签浏览、未读列表
- 搜索（标题 / 分类 / 标签 / 用户筛选）
- 列表密度（紧凑 / 舒适 / 宽松）
- 列表键盘导航（可配置，默认 Ctrl+J / Ctrl+K / Ctrl+Enter）
- 列表右键：打开 / 新窗口 / 稍后阅读 / 复制链接 / 系统分享

### 主题与互动
- 详情分页、跳楼、楼层信息
- 富文本块：引用 / 代码 / 图片 / 链接卡 / 列表 / **表格** / 剧透 / 视频
- **内联样式**：粗体 / 斜体 / 行内代码 / 链接 / **@提及着色（点击进资料）**
- 点赞、表情反应、反应用户、投票、Boost
- 书签、回复、编辑、删除、恢复、举报
- 已解决 / 俺也一样、主题通知级别
- 版主工具（关闭 / 置顶 / 归档）
- 嵌套回复视图（`/n/topic`）
- 分享：复制链接 / 导出 Markdown / 分享长图 / **系统分享** / 浏览器打开
- 用户卡片（**悬停头像** / 点击）
- MessageBus 新回复提示
- 图片：应用内缩放预览（双击适应/100%、Shift+滚轮平移）、保存、复制地址
- **Cookie 感知图片缓存**（内存 + 磁盘）
- **Onebox / Open Graph** 链接预览元数据

### 本地
- 稍后阅读、浏览历史
- 关键词过滤、本地草稿自动保存
- 剪贴板主题链接识别

### 发帖
- 新建主题（分类、标签）、回复、编辑、私信
- Markdown 工具栏 + Emoji + **发帖模板** + @ 用户提示
- **Markdown 结构化预览**（标题/列表/引用/代码/图片/链接卡）
- 图片上传、**粘贴图片直传**
- 本地草稿 + **服务端草稿同步**

### 账号与消息
- 网页登录（Cookie + CSRF）+ Cloudflare 验证
- 高级：User API 浏览器 RSA 授权 / 手动 Key（站点开放时）
- 通知角标、系统 Toast、托盘图标未读数
- 私信收件箱 / 已发送 / 归档
- 书签（命名 / 提醒）、个人资料、关注 / 取关、静音 / 忽略
- 关注与粉丝列表、活动流与摘要
- 邀请链接、信任等级（原生解析 Connect 进度）
- 协议激活 `linuxdo://...`（含 auth 回调）
- 设置：外观、字号、密度、行距、折叠长帖、关键词、通知横幅、托盘、草稿云同步、**可配置快捷键**、站点 URL、缓存清理

### 窗口与系统
- 主题 **新窗口** 打开
- Windows Share 面板
- 深链：`linuxdo://topic/123`、`https://linux.do/t/...`

## 环境要求

- Windows 10 1903+（推荐 Windows 11）
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [WinApp CLI](https://github.com/microsoft/winapp)（`winget install Microsoft.WinAppCLI`）
- 开发者模式已开启
- WebView2 Runtime（系统通常已自带）

## 构建与运行

```powershell
cd "C:\Users\Administrator\OneDrive\桌面\linuxdo_for_win"
.\BuildAndRun.ps1
```

或：

```powershell
dotnet build -c Debug -p:Platform=x64
winapp run
```

## 一键打包（推荐 MSIX）

脚本：`Package.ps1`

WinUI 3 **推荐**用签名 MSIX 分发（完整身份、通知、协议激活更稳）。

```powershell
.\Package.ps1
.\Package.ps1 -InstallCert   # 可选：本机管理员信任证书
```

### 产物 dist/

| 文件 | 说明 |
|------|------|
| `LinuxDo_<构建号>_x64.msix` | 签名安装包 |
| `LinuxDo-latest.msix` | 最新副本 |
| `LinuxDo-Signing.cer` | 公钥证书（安装前必须信任） |
| `Install-LinuxDo.ps1` | **推荐**：一键信任证书 + 安装 |
| `INSTALL.txt` | 安装说明 |
| `SHA256SUMS.txt` | 校验和 |

---

## 用户如何安装（请先读）

> ⚠️ **不要一上来就双击 `.msix`！**  
> 开发版使用自签名证书（`CN=LinuxDo`），不是微软商店证书。  
> 直接双击常会提示 **「无法验证发布者」** 并拒绝安装——这是正常拦截，不是安装包坏了。

### 方式 A：推荐（一键脚本）

1. 打开 **设置 → 系统 → 开发者选项**（或「开发人员设置」）  
   - 打开 **开发人员模式**
2. 把发布包里这些文件放在同一文件夹：
   - `LinuxDo-latest.msix`（或带构建号的 `.msix`）
   - `LinuxDo-Signing.cer`
   - `Install-LinuxDo.ps1`
3. **右键** `Install-LinuxDo.ps1` → **使用 PowerShell 运行**  
   - 若弹出执行策略限制，用下面命令（可先普通窗口试）：
   ```powershell
   cd 放到安装文件的文件夹路径
   Set-ExecutionPolicy -Scope Process Bypass -Force
   .\Install-LinuxDo.ps1
   ```
4. 若仍提示无法验证发布者：  
   - **右键「Windows PowerShell」→ 以管理员身份运行**  
   - 再执行上面的 `.\Install-LinuxDo.ps1`
5. 安装成功后，在 **开始菜单** 搜索 **LinuxDo** 启动

### 方式 B：图形界面手动安装（适合不会 PowerShell 的用户）

1. 同样先打开 **开发人员模式**（同上）。
2. **先装证书，再装 MSIX**（顺序不能反）：
   1. 双击 `LinuxDo-Signing.cer`
   2. 选择 **安装证书…**
   3. 选 **本地计算机** → 下一步  
      （若没有「本地计算机」，选「当前用户」也可先试）
   4. 选 **将所有的证书都放入下列存储** → **浏览**  
      → 勾选 **受信任的根证书颁发机构** → 确定 → 完成  
   5. 看到「导入成功」后再继续
3. 双击 `LinuxDo-latest.msix`（或 `LinuxDo_*.msix`）→ 安装
4. 开始菜单启动 **LinuxDo**

### 方式 C：命令行安装（开发者）

```powershell
# 1) 管理员 PowerShell：信任证书
winapp cert install .\certs\devcert.pfx --password password
# 或：
Import-Certificate -FilePath .\dist\LinuxDo-Signing.cer -CertStoreLocation Cert:\LocalMachine\Root

# 2) 安装包
Add-AppxPackage -Path .\dist\LinuxDo-latest.msix
```

### 常见问题

| 现象 | 原因 | 处理 |
|------|------|------|
| 双击 MSIX 提示无法验证发布者 | 证书未信任 | 先装 `LinuxDo-Signing.cer` 或跑 `Install-LinuxDo.ps1`（管理员） |
| 提示需要开发人员模式 / 侧载 | 系统禁止未知来源 | 打开开发人员模式 |
| 脚本一闪而过 / 无法运行 | 执行策略 | `Set-ExecutionPolicy -Scope Process Bypass -Force` 后再运行 |
| 安装成功但没有图标 / 闪退 | 旧包不完整 | 卸载旧版后用最新 MSIX 重装 |

**分发建议（给作者）**：GitHub Release 请同时上传  
`LinuxDo_*.msix` + `LinuxDo-Signing.cer` + `Install-LinuxDo.ps1` + `INSTALL.txt`，  
并在 Release 说明里写：**请运行 Install-LinuxDo.ps1，不要直接双击 msix。**  
Release 的 tag 使用构建号（如 `20260711-103204`），便于应用内「检查更新」对比。


## 登录说明

LinuxDo **通常已禁用 User API Key**。本客户端以 **网页登录** 为主：

1. 打开应用 → **登录**
2. 在内嵌 WebView2 中完成 Connect / 密码 / 第三方登录
3. 点「我已登录，立即检测」或等待自动同步
4. 写操作走 Cookie + CSRF

高级页仍保留浏览器 User API 授权与手动 Key（仅当站点重新开放时可用）。

## Cloudflare 防护

1. 加载失败时弹出 **站点安全验证** 页
2. 在内嵌浏览器完成验证
3. 点 **验证完成，继续**；之后接口优先带 `cf_clearance` Cookie

## 工程结构

```
LinuxDo/
  App.xaml / MainWindow / MainPage   # 入口与 NavigationView 壳
  Core/
    Models/      # Discourse 模型
    Services/    # API、鉴权、会话、草稿、路由、ImageLoader、Onebox
    Utilities/   # 设置、安全存储、HTML、Markdown、快捷键、滚位
  Controls/      # CachedImage、InlineTextBlock、转换器
  Features/      # 按功能拆分的页面
```

## 技术栈

- C# / .NET 10
- WinUI 3 + Windows App SDK
- CommunityToolkit.Mvvm
- WebView2（登录 / CF / 敏感路径回退）
- HttpClient + 共享 Cookie 容器

## 快捷键（默认可改）

| 快捷键 | 功能 |
|--------|------|
| Ctrl+N | 新建主题 |
| Ctrl+Shift+N | 写私信 |
| Ctrl+F | 搜索 |
| Ctrl+R | 刷新 |
| Ctrl+1~7 | 侧栏切换 |
| Ctrl+J / K | 列表下/上一项 |
| Ctrl+Enter | 打开选中 |

在 **设置 → 快捷键** 中可录制与重置。

## 说明

- 本客户端与 LinuxDo / Discourse 官方无关。
- 帖子内容按 HTML 解析为块 + 内联样式渲染；极复杂嵌入可能简化。
- 复刻自 `linuxdo_for_mac`，Windows 上以等价实现为主（托盘≈菜单栏、Toast≈系统通知等）。
