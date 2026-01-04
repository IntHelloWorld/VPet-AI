# 屏幕监控插件 - 隐私保护功能

## 功能说明

为了保护用户隐私，屏幕监控插件现在支持手动暂停/恢复监控功能。

## 使用方法

1. **暂停监控**：右键点击桌宠 → 系统 → "暂停屏幕监控"
2. **恢复监控**：右键点击桌宠 → 系统 → "恢复屏幕监控"

## 功能特性

- ✅ 一键暂停/恢复屏幕监控
- ✅ 暂停状态会自动保存，重启后保持
- ✅ 暂停时桌宠会提示"屏幕监控已暂停，我不会再偷看你的屏幕啦~"
- ✅ 恢复时桌宠会提示"屏幕监控已恢复，让我看看你在做什么~"
- ✅ 菜单按钮文本会根据当前状态动态更新（不会重复添加按钮）

## 使用场景

建议在以下场景暂停屏幕监控：

- 输入密码、银行账号等敏感信息时
- 处理工作机密文件时
- 进行私密聊天时
- 浏览不希望被记录的内容时

## 技术实现

- 使用 `volatile bool _isPaused` 标志位控制监控状态
- 暂停状态持久化到 `Setting.lps` 文件中（`screenmonitor.is_paused`）
- 定时器检测到暂停状态时会跳过截图和分析流程
- 通过保存菜单项引用 `_toggleMenuItem` 来动态更新按钮文本，避免重复添加

## 代码修改位置

- [ScreenMonitorPlugin.cs:25](ScreenMonitorPlugin.cs#L25) - 添加 `_toggleMenuItem` 字段保存菜单项引用
- [ScreenMonitorPlugin.cs:45-66](ScreenMonitorPlugin.cs#L45-L66) - 实现 `LoadDIY()` 方法，只添加一次菜单项
- [ScreenMonitorPlugin.cs:71-93](ScreenMonitorPlugin.cs#L71-L93) - 实现 `ToggleMonitoring()` 方法，直接更新菜单项文本
- [ScreenMonitorPlugin.cs:96](ScreenMonitorPlugin.cs#L96) - 定时器检查暂停状态
- [ScreenMonitorPlugin.cs:122](ScreenMonitorPlugin.cs#L122) - 加载暂停状态
- [ScreenMonitorPlugin.cs:203-207](ScreenMonitorPlugin.cs#L203-L207) - 保存暂停状态

