# PaperDrop

用于 Windows 的论文下载与 LaTeX 引用桌面 App。粘贴论文链接、选择文件夹，即可批量保存 PDF；也可生成 BibTeX 或带超链接的 `\bibitem`，用于 Overleaf 等 LaTeX 编辑器。

## 下载与使用

当前版本：**1.1.0**。新版采用双栏界面，左侧输入与操作，右侧切换任务和引用；支持状态标签、失败重试、复制与导出。

运行环境：Windows 10 / 11，.NET Framework 4.8。使用 App 无需 Python、命令行或账号。

1. 下载 [PaperDrop.exe](./PaperDrop.exe?raw=true)，或使用仓库的 **Code → Download ZIP** 后解压。
2. 双击 `PaperDrop.exe`。
3. 每行粘贴一篇论文的链接或编号，选择保存文件夹，点击 **下载 PDF**。
4. 在列表查看进度和结果；双击已完成的条目打开 PDF。

程序是便携版，单独保留 `PaperDrop.exe` 即可运行。保存文件夹偏好记录在程序旁的 `preferences.txt`；程序所在目录不可写时仍可使用，但不会记住偏好。

## 支持的输入

| 输入 | 示例 |
| --- | --- |
| arXiv 摘要、PDF、HTML 链接 | `https://arxiv.org/abs/1706.03762` |
| 新旧 arXiv 编号、指定版本 | `1706.03762`、`hep-th/9711200`、`2303.08774v1` |
| INSPIRE 单篇论文页 | `https://inspirehep.net/literature/451647` |
| INSPIRE 旧 record / API 链接或编号 | `inspire:451647` |
| DOI 链接或编号 | `10.4310/ATMP.1998.v2.n2.a1` |
| HTTP(S) PDF 直链 | 可带查询参数，无需以 `.pdf` 结尾；仅用于下载 |

INSPIRE 下载优先使用关联的 arXiv 全文，失败后尝试记录中的公开 PDF。**DOI 查询使用 INSPIRE，因此覆盖范围限于 INSPIRE 收录的论文；下载还需要可公开访问的全文。** 不展开搜索结果页、作者主页或文献列表。

## 功能

- 一次最多处理 200 篇，串行下载并保留 arXiv 请求间隔。
- 显示下载进度，支持取消及重试失败项；已完成文件会保留。
- 跳过已有完整 PDF，保留同名损坏文件并使用另一个文件名保存。
- 检查 PDF 首尾标记与传输长度，避免把登录页或错误页保存为论文。
- 从 arXiv / INSPIRE 结构化元数据生成引用，支持复制和导出。
- 快捷键：`Ctrl + Enter` 下载，`Ctrl + Shift + Enter` 生成引用。
- 使用 Windows 系统代理及正常 HTTPS 证书校验。

## Overleaf / LaTeX 引用

输入论文链接后点击 **生成 LaTeX 引用**，在引用页选择：

- **BibTeX**：导出为 `references.bib`，上传到 Overleaf，并用 `\cite{引用键}` 引用。
- **`\bibitem`**：包含可点击的标题链接，可加入参考文献列表；导言区需要 `\usepackage[hidelinks]{hyperref}`。已有 `thebibliography` 环境时，只复制其中的条目。
- **Overleaf 使用示例**：提供 `biblatex + Biber` 和 `hyperref` 的配置。若期刊模板已使用 BibTeX / natbib，应沿用模板，不要混用。

标题含数学公式时，请按需要加载 `amsmath`、`amssymb`；中文内容需要相应 LaTeX 引擎及字体配置。BibTeX 的 URL / DOI 显示由参考文献样式决定。PDF 直链没有可靠书目信息，不用于生成引用。

## 从源码构建

源码位于 `source/`，使用 C# 5、Windows Forms 和 .NET Framework，不依赖第三方 NuGet 包。在仓库根目录打开 PowerShell：

```powershell
& .\source\build.ps1
```

脚本自动查找系统 .NET Framework 编译器，生成仓库根目录的 `PaperDrop.exe`。构建前关闭正在运行的程序。系统若阻止执行本地脚本，可先检查脚本内容，再按本机策略允许执行。

## 测试与验证范围

运行离线测试：

```powershell
& .\source\tests\run-tests.ps1
```

测试在独立测试目录编译并运行，不覆盖 App，也不访问远程站点。当前 **32 项下载测试、39 项引用断言均通过**；arXiv / INSPIRE 下载、DOI 匹配和引用生成已有联网样本验证。

完整 GUI 操作流程与实际 LaTeX 编译尚未完成端到端验证。PDF 检查用于识别传输失败，不等同于完整内容解析。更多范围与限制见 [TESTING.md](./TESTING.md)。

## 接口文档

- [INSPIRE REST API](https://github.com/inspirehep/rest-api-doc)
- [arXiv API 用户手册](https://info.arxiv.org/help/api/user-manual.html)
- [arXiv API 使用条款](https://info.arxiv.org/help/api/tou.html)
