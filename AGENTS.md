# lf-portable 贡献规则

## 适用范围

本文件适用于仓库根目录及其子目录。全局 `AGENTS.md` 优先；本文件不得新增、运行、保留或依赖与全局规则冲突的自定义工作流控制。

## 默认开发要求

1. 不新增 checkpoint、hash marker、收据文件或其他用于扩大流程的状态代码。
2. 不保留与当前目标无关的兼容性代码、历史分支或迁移残骸；直接实现当前契约。
3. 构建和运行所需工具必须统一安装并实际使用。不得因为工具缺失而绕过安全检查、降低安全性或引入替代流程；真实 `CODEX_USB` 和 Windows Sandbox 按下文属于可选诊断环境，不是默认必需工具。
4. 用户指定主仓库路径时，先在该路径执行 `git rev-parse --show-toplevel` 并以返回的仓库根为准；构建和发布只使用该主仓库的源码、`dist/` 与明确指定的当前输入，不得把旧 worktree 或临时产物当作输入。
5. 以上要求约束新增和修改内容；除非任务明确要求，不删除已有的安全校验、架构选择和数据保护行为。

## 项目边界

- `src/portable-launcher/` 是启动器源码和构建脚本。
- `src/release-update/` 是 WSL 发布暂存及 WSL-to-Windows 桥接脚本。
- `dist/` 是本地构建和可运行交付输出目录：根目录只能有正式入口 `dist/CodexPortable.exe`，它必须是带内嵌 payload 的已组装包；架构 launcher 输入位于 `dist/CodexData/tools/launchers/`，裸 bootstrapper 位于外部的 `build/CodexPortable.bootstrapper.exe`。
- `build/CodexPortable.bootstrapper.exe` 是不带 payload 的裸 x86 bootstrapper，只能作为组装输入，禁止直接复制、重命名为正式入口、同步到 U 盘或上传发布；构建脚本不得把它写回 `dist/` 或用裸文件覆盖 `dist/CodexPortable.exe`。
- 准备 U 盘、上传发布或验收前，必须先按文件角色检查实际输入：可运行输入只能是 `release.py` 生成的 `LFPortable-x64.exe` 或 `LFPortable-arm64.exe`，并且必须能读到内嵌 release ZIP 及对应架构 MSIX；不能把文件名、版本号、文件大小或“位于 dist/”当作 payload 完整性的证明。同步脚本和发布入口都应在任何写盘/上传前拒绝裸 bootstrapper。
- 拒绝裸 bootstrapper 只是纠正输入，严禁把这次拒绝当作任务完成、停止或“不可用”结论；发现错误输入后必须继续从当前明确指定的 base/input 重新组装，或改用已验证的 `LFPortable-x64.exe`/`LFPortable-arm64.exe`，直到提供可正常启动的交付物。只有在确实缺少必要输入且所有安全替代路径都已检查后，才报告阻塞原因。
- 不把完整桌面 payload、用户数据、日志、截图、远程控制记录或 USB 备份提交到仓库。

## 构建与验证

全局规则优先。本项目不新增、运行、保留或依赖自定义 checkpoint、发布/审批门禁、hash 或 manifest 对比、Sandbox 证据链及其他等价的工作流状态控制。平台要求的签名、身份、架构和安全解压检查属于运行时安全行为，不得以此条为由删除或绕过。

真实 U 盘和 Windows Sandbox 可用于按需复现启动器或便携运行时问题，但这些观察不作为构建、完成、审批或发布的前置条件，也不生成 checkpoint、收据、hash、manifest 对比或持久化证据文件。未挂载卷标为 `CODEX_USB` 的卷时直接忽略 U 盘复现场景，不得因此阻塞交付或发布。

便携桌面在配置的权限模式下必须保持 composer 可发送，且不得因 Windows Sandbox setup/requirement readiness 显示“设置智能体沙盒以继续”并阻断提交。修复不得只隐藏 onboarding/setup 文案；上游载荷改变 readiness 字段或压缩变量名时，启动器应更新等长语义替换或明确报出不兼容，不能静默留下部分修补。排障时可优先覆盖实际发送链路，但只有获得当次用户授权后才能发送无敏感测试文本；该场景不作为完成、审批或发布前置条件。

公共运行库展开必须兼容 NTFS 和 exFAT。解压不得依赖 exFAT 无法表示的归档时间戳，也不得因 `tar.exe` 的非必要元数据恢复失败而拒绝已经可安全写入的内容；真实解压错误必须进入诊断并在有限时间内返回，不能被进度解析吞掉或让界面长期停在 0%。当用户在当前任务明确点名某块 U 盘要求实测时，只有从该卷根目录的 `CodexPortable.exe` 启动并观察到该卷内的 Codex Desktop 主进程实际出现，才可陈述为已在该 U 盘实测；固定盘、轻量夹具或仅看到 launcher 均不得替代这一事实陈述。

启动器长时间显示“展开中”、进度为 0% 或看似没有响应时，不得只凭界面下结论；应先检查本次启动器日志、启动器及子进程是否仍在运行、CPU/磁盘活动，以及暂存文件数量或字节是否继续增长，并区分首次慢速展开、真实解压错误和挂死。任何耗时下载、复制或展开都必须明确区分当前阶段，并按实际已处理字节、文件数或可观察活动持续报告进度；无法计算百分比时应明确说明并报告可验证的活动，不能只让用户无反馈地等待。不得把中间阶段的启动器窗口当作最终可用性证明，也不得要求用户先打开额外 launcher 再从 launcher 打开 Codex；正式单文件必须从其所在卷一次启动后自动完成准备和桌面交接，不得暴露成需要用户依次操作多个入口或经历多个相互独立等待界面的流程。

媒体健康警告、输入角色拒绝或一次失败的诊断结果只是纠正输入的信号，不是停止任务或宣称“不可用”的依据；应从当前明确的源码和输入继续组装，检查其他安全输入路径，并持续到有可正常启动的交付物。只有必要输入确实缺失且安全替代路径全部核实后，才报告具体阻塞原因。

真实 U 盘验收必须覆盖完整链路：从被点名卷根目录的正式单文件执行，等待展开完成，确认程序文件和 Codex Desktop 主进程都来自该卷，并观察后续启动阶段可用；只看到 launcher、弹出启动窗口、或重复首次展开而未确认桌面交接，都不能称为“已验证可用”。

单文件升级时不得把“版本号相同”或“文件长度相同”当作启动器内容相同；三个随 EXE 内嵌的架构 launcher 每次都应刷新，避免旧 launcher 与新外层 payload 混用。Sandbox smoke 脚本启动 `WindowsSandbox.exe` 后必须保留本次生成的 `.wsb`，直到该 Sandbox 会话退出；客户端进程提前返回、连接断开或超时，只能终止本次脚本创建的客户端并精确清理自己的临时文件，不能立即删除仍可能被 Sandbox 服务读取的配置。

进行真实 U 盘复现时，应先退出本任务启动的 LF Portable 进程，清除本任务在固定盘创建的可丢弃 LF 运行状态和临时缓存，然后只运行 `CODEX_USB` 根目录的 `CodexPortable.exe`。桌面主进程、程序文件和运行库应来自该便携根目录，不得依赖预装、预导入或跨次保留的本机 LF 程序镜像、包缓存或其他本机状态。复现操作仅用于诊断，不改变前述非门禁约束。

运行时行为仍应保持产品契约：首次启动不得闪现官方模型升级公告或 `Try model` CTA；启动器应在交接前抑制已知公告并写入相应的首次运行状态。启动器只对同一便携根目录内的 LF Portable 实例执行单实例保护；系统 WindowsApps 中的官方 Codex Desktop 可以并行运行，不能阻止便携启动，也不得被启动器终止。路径检查失败时，应按便携程序的唯一进程名安全拒绝同一便携根目录的重复启动。

完整桌面包、公共运行库包、程序文件、运行库、恢复输入、配置、SQLite、密钥、用户资料及其他可变数据均来自并保留在便携根目录。固定盘上的会话临时缓存只能作为可选、可丢弃的性能优化；创建或访问失败时必须回落到便携根目录，不得阻止启动。启动器不得创建、要求、复用或宣称依赖本机 LF 程序镜像，也不得从固定盘包缓存恢复便携程序。

## 发布闭环

凡会改变交付行为或交付内容的源码、构建脚本、发布脚本、规则或文档修改，在验证成功后都必须在同一任务内完成一个新的版本发布；仅完成本地构建、启动或测试不算任务完成，除非用户明确要求只做本地编辑。闭环顺序为：从当前主仓库和明确指定的输入重新组装正式架构 EXE，确认输入角色和可启动性，更新版本号，提交变更，创建版本 tag，推送提交和 tag，使用 `publish-release.py` 发布完整的 `LFPortable-x64.exe` 与 `LFPortable-arm64.exe`，再检查远端 tag、Release 和两个资产确实存在。发布操作失败时必须继续排查并修复可处理的输入或流程问题；只有确实缺少必要输入或外部权限且安全替代路径均已检查后，才能报告具体阻塞原因。拒绝裸 bootstrapper 只是纠正输入，不能代替后续组装、发布或完成结论。本闭环不新增 checkpoint、收据、hash、manifest 比对或其他自定义状态文件。

“已发布”只能在远端 tag 指向本次提交、Release 已创建且不是 draft，并且 `LFPortable-x64.exe` 与 `LFPortable-arm64.exe` 两个资产状态均为已上传时使用；本地文件存在、浏览器能打开发布页、文件名/版本号/大小看起来正确，都不能替代远端状态核实。发布前后都要保持架构单文件与裸 bootstrapper 的角色分离，不能把裸文件放回 `dist/`、同步到 U 盘或上传。

用户已明确授权：在本项目的构建、验收、发布和任务清理过程中，允许自动点击由本任务启动的 Windows 应用或临时 Sandbox 提供的确认对话框（包括关闭测试 Sandbox 的确认框）。该授权只适用于当前项目范围，不延伸到账号、凭据、网络权限、系统安全设置或其他未由当前任务明确授权的外部操作，并仍受更高层平台安全策略约束。

便携启动器和它启动的 Codex Desktop 必须沿用调用者的 Windows 令牌；内嵌 manifest 必须保持 `requestedExecutionLevel=asInvoker` 和 `uiAccess=false`。正常启动、交接和便携运行准备不得使用 `requireAdministrator`、`runas` 或其他隐式提权路径。UAC 是否显示提示不改变这条约束：高完整性进程会破坏默认 userdata 权限上下文，并可能让 Computer Use 的自动点击受到 UIPI 拒绝。

源码仓库不携带完整桌面 payload。

## 脱敏与提交

不得把真实凭据、用户路径、会话数据、调试截图、完整桌面 payload、用户资料或 USB 备份提交到仓库；仅保留项目相关的占位符和通用路径示例。提交前按当前任务需要检查工作区，避免把诊断产生的本机数据带入版本控制。

## 任务收尾清理

本次任务明确产生且不再作为当前源码、运行输入或待交付物的本机残留应按需精确清理。清理范围包括任务专用的 `build/`、`release/`、`package/` 暂存目录，已上传发布后的本地 ZIP 副本，临时截图、Sandbox `.wsb`/runner、可选本机会话缓存及其他任务创建的本机 LF 状态，以及由脚本变量误写入仓库根目录的临时目录。

清理前必须确认相关桌面、启动器和 Sandbox 进程已退出，并按精确路径操作。不得删除 `dist/` 中当前构建输入、源码、便携根目录内的 `CodexData/data`、密钥、SQLite、日志、用户资料、其他 portable root，或用途未知的文件。不得为清理新增或保留 checkpoint、收据、hash、manifest、清单比对或其他状态验证文件；完成后只做必要的路径和工作区复查。

若任务在固定盘根目录产生 `LFPortable-*`、`lf-sandbox-*` 或 `.lf-sandbox-*` 测试暂存目录，应逐项核对归属后清理；不得对 `C:\` 根目录执行未经核对的通配删除。即使名称含 `backup`，只要目录由本任务生成且用户未明确要求保留，也按残留清理；仅保留用户明确指定保留的备份。

## 领域要点

### 模型目录（自定义 base_url 的 /models 合成）

- 模型集合的唯一权威是每次启动前刷新的 `<base_url>/models`；网关成功返回空数组时必须删除旧 model-catalog.json 并阻止启动（防止已下线模型继续可用）；pi.dev（https://pi.dev/api/models）只做能力补充、不决定模型集合，pi.dev 失败时静默回退，网关模型照常写入。
- 产出 `CodexData/data/config/model-catalog.json`，并在 config.toml 写入 `model_catalog_json`；默认模型为 gpt-6-astra。
- 字段优先级：网关显式值 > pi.dev（精确 id / baseUrl / provider / 命名空间）> bundled CLI（`codex debug models --bundled`）同 slug 模板。只有存在 openai/openai-codex 供应商证据才允许套用 OpenAI 专属能力（use_responses_lite、tool_mode、comp_hash、supports_search_tool、node_repl_*）；opencode、azure 或供应商不明的同名模型不得继承。
- pi.dev 对同一模型在不同供应商导出的 id 可能带前缀（openrouter 用 `google/gemini-…`）也可能裸 id（google 用 `gemini-…`）：选择时应把“精确 id 桶”与“去前缀后缀桶”取并集（SelectPiMetadata + ContainsPiCandidate）再按 URL/provider 匹配，不能只信任精确桶。
- pi.dev `compat` 标志夹在 pi 顶层与 bundled 模板之间读取（ReadCatalogBoolean）：`supportsReasoningEffort=false` 保留 reasoning 但清空 supported/default_reasoning_level（网关显式 levels 仍最高）；`supports_reasoning_summary_parameter=false` 时 `default_reasoning_summary` 必须写 "none" 而非 "auto"；读取应同时接受 camelCase 与 snake_case（如 thinkingLevelMap/thinking_level_map）。验证 pi.dev 字段前先 curl https://pi.dev/api/models 看实时结构（顶层与 compat 键持续演进），不要按过时示例硬编码。
- 不原样透传外部 model_messages：同 slug bundled 指令可复用，只允许受限 base_instructions 覆盖；无模型专属指令时使用嵌入的 CodexModelFallbackPrompt.txt（只嵌入架构 launcher，不嵌入裸 bootstrapper）。
- 子进程读取 `codex debug models --bundled` 必须设置 `startInfo.StandardOutputEncoding = new UTF8Encoding(false)`：重定向 stdout 默认按控制台代码页解码，CJK/GBK 主机会把 UTF-8 JSON 读乱，导致模板集合为空并静默回退旧目录。

### 测试与复现捷径

- launcher、CLI、harness 都是 Windows PE：WSL 内不能直接 exec，需 `powershell.exe -NoProfile -Command "& 'winpath' args"` 运行，参数用 `wslpath -w` 转 Windows 路径。
- 反射 harness（参考 `build/model-catalog-test/Harness.cs` 模式）可直接调 private static（RefreshModelCatalog / CreateCodexModelInfo / SelectPiMetadata / ReadBundledModelTemplates）做单元级验证；用 Roslyn csc（dotnet SDK 的 Roslyn/bincore/csc.dll）+ mono 4.8-api 引用编译 Windows 控制台 exe，这些 harness 与 fixture 目录都是任务临时产物，用后清理。
- 网关 fixture 用 Windows 侧 `python.exe -m http.server <port> --bind 127.0.0.1 --directory <dir>` 起服：.NET harness 走 Windows loopback，而 WSL 内 curl 的 127.0.0.1 是 WSL 自己的回环、两者不通；验收以 Windows 侧结果为准。
- 生成目录的可读性用探针 CLI 验收：`codex.exe debug models -c model_catalog_json='"C:\…\model-catalog.json"'`（`-c` 的值是带引号的 JSON 字符串）。

### 发布载荷版本记录

- 随包 notice 是组装 base 内的 `CodexData/THIRD_PARTY.txt`（release.py 原样带入成品）：每次换新 base，组装前必须按该 base 实际载荷更新其中记录的 MSIX 版本、内部 Desktop 版本与 codex-cli 版本。
- 载荷事实以 base 实测为准：MSIX AppxManifest Identity Version、`resources/codex.exe --version`、内嵌 app 版本；不要把旧 release 文档的版本号带进新 base。

## 修改方式

- 使用 `apply_patch` 进行文本修改，保持 UTF-8 编码。
- 改动后直接验证，并在交付说明中记录实际执行的验证。
