# 环境反馈排查（2026-10-16）

本记录针对 agent-environment-feedback.zh-CN.md，依据当前工作区源码。未启动用户浏览器或复现原会话，因此运行时推断与源码结论分开记录。

| 反馈 | 排查结论与处理 |
|---|---|
| WebView2 后台动画停止 | Program.cs 未显式调用挂起 API，也未配置后台渲染参数。visibilityState=visible、动画列表为空不能证明挂起；还应对比前后台 requestAnimationFrame 计数、CSS transition 事件与 Vue 实际状态。尚未证实根因，未添加可能无效的 Chromium 开关。 |
| CDP 端点过期错误 | BrowserUseTools 的连接异常此前直接返回 ex.Message，确实缺少可执行指引。连接阶段失败现返回 reason=endpointUnavailable、hint=/browser reconnect 和 endpoint；使用这个名称是因为连接失败不一定意味着端点过期。AI 可通过 open 的 mode 切换会话模式，默认设置保持不变；普通任务优先内置，需要已有登录状态/个人资料时使用外部，不明确时询问用户。超时/授权等待仍走独立取消处理。 |
| 后台 shell state=7 | BackgroundTasks 构造时把持久化的活动任务转换为 Interrupted，并填入“应用退出中断任务”。这是重启恢复标记，不能由此断言存活进程被静默杀死。已有父会话完成通知机制，但要进一步确认 shell 是否登记通知及原会话的退出时间。 |
| file_search 的 file=null | SystemTools.Search.cs 返回匹配行的 path、line、text，没有 file 字段；路径不是按正则交替分支生成。很可能是调用方读取不存在的 file。已明确提示使用 path，并在交替正则测试中断言路径。 |
| 长图拒绝 | ImageAttachment.ReadAsync 已把最长边缩到 2000，并限制编码体积，但读取前仍拒绝超过 4MB 的文件。浏览器截图直接使用 FromBytes，绕过 ReadAsync 的缩放；服务端另有拒绝恢复机制。原图被哪种限制拒绝仍需原始响应确认；长图缩到 2000 高度也会损失可读性，分片需要独立实现与验证。 |
| Monty os.path | 运行时已说明 Python 子集与显式 import。补充了 os.path 不可假定可用，以及使用宿主文件工具的提示；未修改解释器自身异常。 |
| subagent 部分失败 | 确认原顶层错误过于笼统。新增 data.failed（taskId 数组）、data.succeeded；错误列出失败任务并明确成功产出仍在 results 中。链式模式才提示后续步骤未执行。添加部分成功/失败测试。 |
| web_fetch 链接 | WebTools 的文本转换会去掉 HTML 标签，链接属性会随之丢失；链接保留/提取仍待实现。 |
| npm registry 超时 | 拉取完整版本历史会增加数据量；尚无原始进程输出足以判断是网络还是响应体原因。不宜据此增加专用包管理工具。 |
| ask_user 误选 | 反馈体现选项覆盖不足，但缺少原始问题、答案及 UI 路径，尚不能确认协议不支持补充输入。此项未改。 |

修改均局限于诊断、提示与返回摘要，按用户要求允许 AI 切换浏览器模式，保留总开关及外部调试连接授权。
