# 贡献说明

欢迎提交问题、改进建议和 Pull Request。

1. 在 Issue 中说明问题、期望行为和复现步骤；请移除真实仓库地址、账号、认证缓存及业务文件。
2. 在 Windows x64 上安装 .NET 10 SDK。运行集成测试另需 `svnadmin.exe` 在 PATH 中，可安装 SlikSVN。
3. 运行 `dotnet build SVNRevDesk.slnx -c Release`，并按 [开发与发布](docs/开发与发布.md) 执行相关测试。
4. 保持改动聚焦，描述行为变化与验证结果。更新第三方工具时，同步其许可证、对应源码、文件清单及 SHA-256 清单。

请勿提交 `bin/`、`obj/`、`.build/`、`artifacts/` 或运行程序生成的个人数据。
