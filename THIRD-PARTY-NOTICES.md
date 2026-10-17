# 第三方组件声明

SVNRevDesk 自有代码的 Apache-2.0 许可不替代下列独立组件及其依赖的许可。

| 组件 | 版本 | 分发与许可位置 |
| --- | --- | --- |
| SlikSVN / Apache Subversion | 1.14.5 x64 | `vendor/Subversion/win-x64/NOTICE.md`、`Licenses/`；运行时释放到 `Tools/Svn/`。 |
| WinMerge | 2.16.58.2 x64 | `vendor/WinMerge/win-x64/NOTICE.md`、`COPYING`（GPL v2）及各子目录声明；运行时释放到 `Tools/WinMerge/`。 |
| WinMerge 对应完整源码 | 2.16.58.2 | `vendor/WinMerge/win-x64/Source/winmerge-2.16.58.2-full-src.7z`；释放后位于 `Tools/WinMerge/Source/`。 |
| Microsoft Visual C++ 可再分发组件 | 14.44.35211.0 | 随 SlikSVN 应用本地部署；声明保留于其 `Licenses/`。 |
| Microsoft .NET | 10.0，自带运行时发布包 | 使用 .NET SDK 官方发布机制分发；包内 `Licenses/Microsoft.NETCore.App/` 和 `Licenses/Microsoft.WindowsDesktop.App/` 保留实际运行时版本的许可及第三方声明；另见 [.NET 许可说明](https://github.com/dotnet/core/blob/main/license-information.md)。 |

WinMerge 通过命令行作为独立程序调用，SVNRevDesk 不链接其代码。第三方分发包保留原有许可证、声明与源码，不应移除。各工具文件清单和 SHA-256 清单位于相应 `vendor` 上级目录。

- [SlikSVN 官方网站](https://sliksvn.com/)
- [Apache Subversion](https://subversion.apache.org/)
- [WinMerge 官方网站](https://winmerge.org/)
- [WinMerge 2.16.58.2 发布及源码](https://github.com/WinMerge/winmerge/releases/tag/v2.16.58.2)

WinMerge 的某些 Office 转换插件或 Java 工具需要额外外部软件；这不影响 SVNRevDesk 本身创建基础 Office 文档。
