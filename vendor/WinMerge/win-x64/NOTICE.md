# 随程序附带的 WinMerge

- 版本：WinMerge 2.16.58.2，官方 x64 便携版，未修改程序二进制。
- 官方下载页：https://winmerge.org/downloads/
- 发布页：https://github.com/WinMerge/winmerge/releases/tag/v2.16.58.2
- 二进制来源：https://github.com/WinMerge/winmerge/releases/download/v2.16.58.2/winmerge-2.16.58.2-x64-exe.zip
- 二进制 ZIP SHA-256：5732474ADD39283F44BD20C66E57503D26F435E695FEABC4140BA2F91D2E7804
- 主程序 WinMergeU.exe 的 Authenticode 签名经 Windows 验证有效。
- WinMerge 许可证见同目录 COPYING（GPL v2），第三方声明保留在官方包的各子目录。
- 对应完整源码随包附于 Source/winmerge-2.16.58.2-full-src.7z，可用 7-Zip 解压。
- 源码来源：https://github.com/WinMerge/winmerge/releases/download/v2.16.58.2/winmerge-2.16.58.2-full-src.7z
- 源码 7z SHA-256：1987438F30A46B21D78E269B9767FDF3B3171613A97D58C044994381BD5273FA

SVNRevDesk 将 WinMerge 作为独立进程调用，不注册资源管理器扩展，不运行安装 / 注册脚本。
WinMerge.ini 与临时目录由调用参数定位至主程序同级目录。比较入口将两侧文件以只读方式打开。
本目录整体在编译时压缩并嵌入主 EXE；首次使用释放到 Tools/WinMerge，许可证和源码同时可用。
升级本分发时须一起更新对应源码、声明、文件清单和 SHA-256 清单。

官方包保留了转换插件及辅助脚本；部分 Office 转换或 Java 工具仍需相应外部软件。
