# Switchyard 测试方案与技术规范文档

## 1. 测试策略概述

`Switchyard` 作为一个深度拦截 MSBuild 编译管道并操纵底层 IL 元数据的核心级构建工具，其测试策略不能依赖传统的单元测试框架。由于 CLR 的运行时加载欺骗和 MSBuild 的文件流向拦截必须在真实的物理环境中才能暴露出潜在缺陷，本项目的测试金字塔采用“以黑盒端到端（E2E）测试为核心，集成与单元测试为辅助”的逆向架构。

```
                       ┌───────────────────────────────┐
                       │     Level 3: E2E 运行时验证    │ ─── 运行编译出的 Exe，断言控制台 Stdout
                       ├───────────────────────────────┤
                       │    Level 2: MSBuild 集成测试   │ ─── 检查 bin/publish 目录结构与 PDB 状态
                       ├───────────────────────────────┤
                       │   Level 1: 核心算法单元测试    │ ─── 内存中验证 AsmResolver 符号重写
                       └───────────────────────────────┘

```

---

## 2. 测试工程目录拓扑结构

测试套件独立于主业务代码，采用隔离设计。测试所需的各种依赖包冲突、版本交织等边界条件，通过特殊的 `TestSamples` 静态项目群进行模拟。

```text
test/
├── Switchyard.Core.Tests/       # Level 1: 针对元数据重写算法的单元测试工程
│   ├── AssemblyRenameTests.cs   # 程序集重命名算法测试
│   ├── ConfigurationParserTests.cs # 路由配置 / RouteGroup 解析测试
│   ├── PdbAlignmentTests.cs     # 调试符号对齐（MVID / CodeView 路径）测试
│   ├── ReferenceRedirectTests.cs# 引用关系洗脑重定向测试
│   └── TestAssemblyFactory.cs   # 用 AsmResolver 内存生成测试程序集的工厂
├── Switchyard.TestFixtures/     # 携带可移植 PDB 的标记程序集，供 PdbAlignmentTests 使用
├── Switchyard.IntegrationTests/ # Level 2 & 3: 管道集成与 E2E 运行时测试主工程
│   ├── BuildUtility.cs          # 封装 dotnet CLI、本地 NuGet feed 打包与全局缓存清理
│   ├── LocalFeedFixture.cs      # xUnit 集合级 Fixture，确保本地 feed 就绪
│   ├── PipelineInterceptTests.cs# 增量编译阻断、Publish 集合拦截、级联沙箱测试
│   ├── RuntimeRoutingTests.cs   # E2E 运行时输出断言测试
│   └── TestSamples/             # 预先定义好的、不引入主 Sln 编译的外部测试样本
│       ├── BasicRouteApp/       # 基础双版本路由分流测试样本（含 PaymentModule 子工程）
│       ├── RouteGroupApp/       # 级联闭环隔离组测试样本
│       ├── InvalidCastApp/      # 故意违反契约的边界撕裂测试样本（含 BoundaryBreaker 子工程）
│       └── nuget.config         # 指向 test/local-feed 的本地源
└── fixtures/                    # 被打包成多版本 NuGet 包的源桩程序集
    ├── TargetLib/               # 以 AssemblyVersion 区分 1.0.0 / 2.0.0 / 3.5.0 三个包版本
    ├── CommonUtils/             # TargetLib 的级联依赖，同样按版本打包
    └── nuget.config

```

> 说明：`fixtures/` 下的 `TargetLib`、`CommonUtils` 通过 `dotnet pack -p:...PackageVersion=...` 在测试启动时被打包成 1.0.0 / 2.0.0 / 3.5.0 三个版本，写入 `test/local-feed`，再由各 `TestSamples` 引用。`Switchyard` 包本身也每次重新打包并清除全局 NuGet 缓存中的旧副本，以保证 `.targets` / Task DLL 的最新改动生效。

---

## 3. 详细测试级别与用例设计

### 3.1 Level 1: 核心算法单元测试（内存黑盒）

该级别测试不需要启动物理编译，执行速度为毫秒级。主要用于卡死 `AsmResolver` 在内存中修改二进制程序集元数据时的边界。

* **用例 1：目标包定义重塑（Def-Reshaping）**
* **测试输入**：读取一个原厂带有强签名的 `TargetLib.dll` 字节流。
* **核心操作**：调用 `PrepareAndRenameAssembly`，传入预设名称 `TargetLib.Switchyard.1.0.0`。
* **断言验证**：
1. `module.Assembly.Name` 字符串与预设完全一致。
2. `module.Assembly.PublicKey` 指针为 `null`，且哈希算法重置为 `None`。




* **用例 2：调用方引用洗脑（Ref-Redirect）**
* **测试输入**：读取一个内部含有对 `TargetLib` 外部引用的 `MainApp.dll`。
* **核心操作**：调用 `RedirectReferences`，将 `TargetLib` 映射到 `TargetLib.Switchyard.1.0.0`。
* **断言验证**：
1. 遍历 `module.AssemblyReferences`，原有的 `TargetLib` 条目被完全覆盖或移除。
2. 新条目的 `PublicKeyOrToken` 字节数组为 `null`。




* **用例 3：调试符号对齐（PDB-Align）**
* **测试输入**：同时加载 `TargetLib.dll` 和 `TargetLib.pdb`。
* **核心操作**：在修改程序集定义后一并写出新的二进制文件。
* **断言验证**：使用 AsmResolver 重新载入写出的 `.dll` 和 `.pdb`，验证其 `MVID (Module Version ID)` 保持强一致，确保断言不损坏调试链。



### 3.2 Level 2: MSBuild 管道拦截与文件流集成测试

这一层用于验证 `Switchyard.targets` 的挂载时机是否能完美适配微软原生的构建生命周期。

* **用例 1：输出文件物理流向拦截**
* **步骤**：对 `BasicRouteApp` 触发 `Build` 指令。
* **断言**：验证最终的 `bin/` 目录下：
* 存在 `TargetLib.Switchyard.1.0.0.dll`
* 存在 `TargetLib.Switchyard.3.5.0.dll`
* **绝不能存在** 原厂的 `TargetLib.dll`（必须被 Task 从 `ReferenceCopyLocalPaths` 中彻底阻断并清除）。




* **用例 2：增量编译完整性保护**
* **步骤**：在不改动任何源码的情况下，连续执行两次 `dotnet build`。
* **断言**：第二次编译时，MSBuild 应正确触发 `Static analysis up-to-date` 增量跳过标识，且由于文件被缓存在 `obj/` 中，不能发生任何文件进程锁死（File Locking）异常。


* **用例 3：发布管道信息无损传递（Publish Verification）**
* **步骤**：执行 `dotnet publish -c Release`。
* **断言**：验证 `bin/Release/net8.0/publish/` 目录下同样完美包含全套魔改程序集（`TargetLib.Switchyard.1.0.0.dll`、`TargetLib.Switchyard.3.5.0.dll`），原厂 `TargetLib.dll` 绝不能出现，且运行发布产物应得到与 Debug 一致的双版本分流输出。
* **实现备注**：publish 会从锁文件重新解析 copy-local 资产、把原厂 DLL 再次拉回，因此由独立的 `SwitchyardPublishCleanup` 目标（`SwitchyardPublishFilterTask`）在 `_ComputeResolvedCopyLocalPublishAssets` 之前对 `ReferenceCopyLocalPaths` 与 `_ResolvedCopyLocalPublishAssets` 做二次剔除。



### 3.3 Level 3: 运行时端到端测试（E2E 行为印证）

这是证明 `Switchyard` 成功的终极测试。通过执行编译产物并拦截控制台的 `Stdout`，来确证同一个进程空间内的依赖分流。

#### 示例：`RuntimeRoutingTests.cs` 的自动化实现规范

```csharp
[Fact]
public void Run_BasicRouteApp_AssertsDualVersionDiversion()
{
    string projectPath = Path.Combine(BuildUtility.TestSamplesPath, "BasicRouteApp", "BasicRouteApp.csproj");
    BuildUtility.CleanProject(projectPath);

    var buildResult = BuildUtility.BuildProject(projectPath);
    Assert.True(buildResult.Success, $"Build failed:\n{buildResult.StandardError}");

    string exePath = BuildUtility.FindBuiltExecutable("BasicRouteApp");
    string binDir = BuildUtility.GetBinDirectory("BasicRouteApp");
    Assert.True(File.Exists(exePath), $"Built executable not found at {exePath}");

    var runResult = BuildUtility.RunExecutable(exePath, binDir);

    // 证实：主程序集和三方依赖项在没有任何 ALC 隔离的情况下，确实读取到了两个独立版本的 DLL
    Assert.Contains("[MAIN_APP] TargetLib loaded version: 1.0.0.0", runResult.StandardOutput);
    Assert.Contains("[PAYMENT_MODULE] TargetLib loaded version: 3.5.0.0", runResult.StandardOutput);
}
```

> 全部集成测试共享同一个 `[Collection("Integration")]`，由 xUnit 串行执行。这是因为各 `TestSamples` 被原样复制到测试输出目录后，多个用例会构建到同一份物理 `bin/obj` 树上，并行执行会触发 MSBuild 对 `obj/switchyard/*.dll` 的文件锁竞争。`LocalFeedFixture` 作为集合级 Fixture 负责一次性把 `Switchyard` / `TargetLib` / `CommonUtils` 打包进 `test/local-feed` 并清空全局 NuGet 缓存中的旧 `switchyard` 包。

#### 实现层面必须解决的三个底层坑位

下列问题在方案设计时未显式列出，但在真实落地中必须处理，否则 Level 2/3 全部无法通过：

1. **Task 挂载时机**：`ExecuteSwitchyardWeaving` 必须 `AfterTargets="CoreCompile"`（而非 `ResolveAssemblyReferences`），否则 `@(IntermediateAssembly)` 尚未生成，主程序集自身的引用永远得不到重定向。但其对 `deps.json` 的改写又必须放在 `GenerateBuildDependencyFile` 之后（该目标被 SDK 追加在 `CopyFilesToOutputDirectory` 之后），因此拆成 `ExecuteSwitchyardWeaving` + `PatchSwitchyardDepsJson` 两个目标。
2. **引用版本号同步**：`ReferenceRedirector` 在改名 `TargetLib → TargetLib.Switchyard.1.0.0` 的同时必须把 `AssemblyReference.Version` 同步成 `1.0.0.0`（从路由名后缀解析并补齐为四段式，避免 `0xFFFF` “未指定”哨位被 CLR 读成 65535）。否则 CLR 按 `(Name, Version, PublicKey)` 绑定失败。
3. **deps.json / TPA 注入**：.NET Core 框架依赖型应用只从 `deps.json` 构建的 TPA 列表解析程序集，放在 `bin` 目录下但不在 `deps.json` 里的 `TargetLib.Switchyard.*.dll` 运行时仍会 `FileNotFoundException`。`DepsJsonPatcher` 把每个路由程序集作为合成 `"type":"project"` 库写进 `targets`/`libraries`，并清掉原厂包的 `runtime` 条目，使其既进入 TPA 又不会被 publish 从 NuGet 全局缓存里原样拷回。publish 阶段还需 `SwitchyardPublishCleanup` 目标用 `SwitchyardPublishFilterTask` 把锁文件重新解析进来的原厂 DLL 从 `ReferenceCopyLocalPaths` / `_ResolvedCopyLocalPublishAssets` 里再剔除一次。

---

## 4. 特殊边界“恶魔测试”矩阵 (Edge/Matrix Cases)

为了应对复杂的真实工程环境，测试套件必须常设以下具有破坏性的专项测试场景：

### 4.1 影子包动态热下载验证

* **测试设计**：在样本的 `SwitchyardRoutes` 配置中，故意写入一个本地计算机全局 NuGet 缓存（`~/.nuget/packages`）中**绝对不存在**的历史远古版本号（如 `Newtonsoft.Json 4.5.11`）。
* **预期行为**：执行 `dotnet build`，编译不应中断。MSBuild 日志中应静默输出 `[Switchyard] Version 4.5.11 not found in local cache. Fetching from NuGet upstream...`，并验证最终 bin 目录下成功生成对应的魔改文件。

### 4.2 级联依赖沙箱完整性断言（RouteGroup 破坏性验证）

* **测试设计**：启用高级元数据 `<SwitchyardRouteGroup>AuthSandbox</SwitchyardRouteGroup>`，其中 `TargetLib` 依赖 `CommonUtils`。
* **预期行为**：编译完成后，自动化测试使用 AsmResolver **深度逆向打开** 已经重命名过的 `TargetLib.Switchyard.1.0.0.dll`，检查其引用的元数据表。断言其对 `CommonUtils` 的内部调用**已经级联改变**指向了 `CommonUtils.Switchyard.1.0.0`，同时 `bin` 目录下原厂 `TargetLib.dll` / `CommonUtils.dll` 均不存在。E2E 运行 `RouteGroupApp` 时主程序与 `CommonUtils` 直读版本均落到 `1.0.0.0`。

### 4.3 契约崩溃边界断言（InvalidCast 阻断测试）

* **测试设计**：编写 `InvalidCastApp` 样本，故意在代码中打破隔离契约：`BoundaryBreaker`（路由到 V3.5.0）通过 `GetVersionHolder()` 返回一个 `TargetLib.VersionReport` 实例，主程序（路由到 V1.0.0）直接强转接收。
* **预期行为**：执行端到端运行，主程序捕获到 `System.InvalidCastException`（消息形如 `[A]TargetLib.VersionReport cannot be cast to [B]TargetLib.VersionReport ... originates from 'TargetLib.Switchyard.3.5.0' ... 'TargetLib.Switchyard.1.0.0'`），样本在 `catch` 块中输出 `Type boundary successfully torn.` 并以 `ExitCode=0` 退出。以此证明两者的类型系统在 CLR 内部已被干净利落地切断。
* **实现备注**：`TargetLib.VersionReport` 被设计为非静态类（保留静态 `GetVersion()`），以便 `BoundaryBreaker` 能 `new VersionReport()` 跨路由版本制造类型实例。

---

## 5. 持续集成（CI）自动化流水线准入规范

由于本工具涉及多系统的底层路径处理（Windows 的 `\` 与 Linux 的 `/`）以及复杂的权限控制，CI 流程（如 GitHub Actions）必须遵循以下契约：

1. **环境双操作系统阵列（OS Matrix）**：
```yaml
matrix:
  os: [windows-latest, ubuntu-latest]

```


由于 `AsmResolver` 及 MSBuild 宏在 Windows 和 Linux 下的文件系统尾部斜杠行为不同，所有集成测试必须在双系统下全量跑通。
2. **强制影子缓存清理（Clean-Cache Contract）**：
在 CI 的 `steps` 阶段，执行测试前必须清空当前容器的临时的 `obj/` 文件夹以及针对测试桩包的本地缓存，强制触发 `NuGet.Protocol` 的并发拉取和解析逻辑，以验证网络层、解析层代码的绝对稳定性。