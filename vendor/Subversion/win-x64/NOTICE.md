# 随程序附带的 SVN 客户端

- Subversion：SlikSVN 1.14.5，x64，文件版本 1.14.5.21638。
- 来源：SlikSVN 官方 Windows 发行版（https://sliksvn.com/download/），本机安装副本。
- 保留 `svn.exe`、其全部 `SlikSvn-*.dll` 依赖、OpenSSL engine、语言文件与原发行版许可证。
- 不包含 Java 集成 DLL、SVN 管理工具或 .NET 运行时。
- Visual C++ DLL：`msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`，文件版本 14.44.35211.0；来自 Visual Studio 2022 的 `VC/Redist/MSVC/14.44.35112/x64/Microsoft.VC143.CRT` 可再分发目录，按应用本地部署。
- 所有第三方许可证和分发清单保存在 `Licenses/`。Visual Studio 分发说明：https://learn.microsoft.com/visualstudio/releases/2022/redistribution 。

编译时会将此目录压缩并嵌入程序；调试输出也会附带文件副本。发布为单个 EXE，首次使用 SVN 时自动将内置文件释放到程序旁的 `Tools/Svn/`。请保留目录结构；程序使用 `Tools/Svn/bin/svn.exe`，DLL 与它放在同一个目录。此版本面向 Windows x64，UCRT 等 Windows 自带组件由操作系统提供。

升级 SVN 时应同时替换 EXE、依赖 DLL、语言文件和许可证，并重新检查本地 DLL 依赖；不要混用不同版本的文件。文件清单为上一级的 `win-x64.files`，原始文件的 SHA-256 为 `win-x64.sha256.json`，升级后应同步更新。
