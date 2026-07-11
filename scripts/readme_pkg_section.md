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

