# AllAgent.NET.Qoder

纯 C# 实现的 Qoder 登录与模型提供商插件。无需 Python、Node、Go 或外部 CLI，也不依赖 Qoder 客户端进程。

登录后插件会自动读取当前账号的实时模型目录，并把模型接入 AllAgent.NET 的 `/model` 选择器；模型页底部会显示当前额度。

## 使用

```text
/login qoder              浏览器打开 Qoder 设备授权页，完成后自动保存凭据
/model                    选择 Qoder 模型
/ext qoder-models         查看当前账号可用的模型
/ext qoder-status         查看登录状态与模型连接
/ext qoder-quota          查询额度、套餐与到期时间
/ext qoder-activities     查看官方活动与领取状态
/ext qoder-checkin        一键领取当前账号可领取的官方活动
/ext qoder-claim <id>     手动领取指定活动
/logout qoder             清除本地凭据并停用该账号的模型
```

首次 `/login qoder` 会：

1. 生成 PKCE S256 参数与设备授权链接；
2. 在默认浏览器打开 `qoder.com` 授权页；
3. 轮询设备令牌接口，成功后将令牌写入当前用户的 DPAPI 凭据库；
4. 读取用户信息与 Qoder 模型目录，自动创建 `Qoder` 模型配置。

## 模型

模型列表来自 Qoder 的实时目录接口（`/algo/api/v2/model/list`），包括账号实际可用的 `auto`、`ultimate`、`performance`、`efficient`、`lite` 等模型及其上下文/输出上限。目录不可用时回退到上述固定档位，避免登录后没有模型可选。

## 额度与活动

- 额度来自 `openapi.qoder.sh/api/v2/quota/usage`，并合并 `/api/v2/user/plan` 的套餐名。显示已用/总量/剩余、百分比、附加额度、到期时间和额度是否耗尽；模型页底部每 60 秒刷新一次。
- Qoder **目前没有固定的每日签到接口**。`/ext qoder-activities` 读取当前账号的官方活动资格（例如免费调用次数活动），显示可领取/已领取/不可领取及截止日期；`/ext qoder-checkin` 只领取接口明确标记为 `canClaim=true` 的活动，`/ext qoder-claim` 可单独领取。
- 活动领取是用户主动触发的单账号操作，不做多账号批量刷取，也不绕过 Qoder 的资格判定。

## 实现说明

- **设备登录**：`qoder.com/device/selectAccounts` + PKCE S256，轮询 `openapi.qoder.sh/api/v1/deviceToken/poll`。
- **令牌续期**：过期时尝试 `center.qoder.sh/algo/api/v3/user/refresh_token`；设备令牌可能不支持续期，此时需要重新登录。
- **COSY 请求签名**：RSA PKCS#1 v1.5 包装 AES-128 密钥，AES-CBC 加密身份信息，MD5 签名请求体，并按 Qoder 的自定义字母表重排/替换 Base64 请求体（`&Encode=1`）。
- **流式响应**：解析 Qoder `{statusCodeValue, body}` SSE 信封，输出文本、思考内容、工具调用与用量。
- **额度/活动接口**：使用普通 Bearer Token（非 COSY）访问 `openapi.qoder.sh` 的 quota、plan 和 activity 接口；额度缓存 60 秒，活动缓存 120 秒，领取后立即失效。
- **凭据安全**：令牌通过 `TokenVault` 使用 Windows 当前用户 DPAPI 加密保存；日志与设置中不会出现令牌。若本机安装了官方 Qoder 客户端，会读取其机器指纹以提升授权兼容性；不会修改客户端文件。

## 限制

- Qoder 上游接口可能随客户端版本变化；模型目录请求失败时使用固定档位回退。
- 图片以 `image_url` 形式内联发送；Qoder 原生的图片上传协议暂未实现。
- 活动资格和奖励由 Qoder 服务端决定；插件只展示并领取当前账号明确可领取的活动。
- 本项目仅用于个人学习与研究。请遵守 Qoder 的服务条款，不要用于商业用途或违反条款的场景。
