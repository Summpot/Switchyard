# Switchyard 详细设计文档

## 1. 概述

### 1.1 设计目标

`Switchyard` 是一个基于 MSBuild Task 和 AsmResolver 的编译时/发布时 IL 编织与引用重定向工具。其核心目标是**打破 .NET 默认的程序集版本唯一定律**，允许在同一个进程中，让主程序集和特定的依赖项分别调用同一个 NuGet 包的任意不同版本（例如主程序调用 V1.0.0，依赖项 A 调用 V3.5.0）。该方案完全不依赖 `AssemblyLoadContext` (ALC)、不使用 `extern alias`、不引入类似 Fody 的重型三方插件框架。

### 1.2 核心原则

* **零侵入性**：所有配置仅存在于主应用程序的 `.csproj` 文件中，不修改任何依赖项的源码或其独立的项目文件。
* **原生内聚**：配置完全寄生在原生的 `<PackageReference>` 节点上，通过 MSBuild 原生元数据机制传递。
* **物理隔离**：在编译/发布管道中修改 IL 元数据，重命名目标程序集，从根源上绕过 CLR 的版本加载器冲突校验。
* **自动包解析**：`Switchyard` 自动通过 NuGet 基础设施和项目资产文件定位或下载所需版本的包，无需开发者手动管理物理 DLL 路径。
* **调试友好**：同步重构对应的 `.pdb` 文件，确保多版本并存时依然可以单步调试进入目标源码。

---

## 2. 配置 Schema 设计

所有路由规则都在主程序的 `.csproj` 中，附加在目标 NuGet 包的 `<PackageReference>` 节点上。

### 2.1 基础语法

通过附加 `<SwitchyardRoutes>` 元数据，使用键值对字符串声明路由表。

* **语法格式**：`程序集名称=目标版本号;...`
* 支持通配符 `*` 作为兜底规则。

### 2.2 配置示例

```xml
<ItemGroup>
  <PackageReference Include="TargetLib" Version="2.0.0">
    <SwitchyardRoutes>
      MainApp=1.0.0;        PaymentModule=3.5.0;  *=2.0.0               </SwitchyardRoutes>
  </PackageReference>
</ItemGroup>

```

### 2.3 依赖级联隔离组（高级特性）

如果路由的包内部还依赖其他包，为了防止子依赖链断裂，提供 `<SwitchyardRouteGroup>` 元数据将多个包绑定为一个隔离沙箱。

```xml
<ItemGroup>
  <PackageReference Include="TargetLib" Version="2.0.0">
    <SwitchyardRoutes>AuthModule=1.0.0;*=2.0.0</SwitchyardRoutes>
    <SwitchyardRouteGroup>AuthIsolation</SwitchyardRouteGroup>
  </PackageReference>
  <PackageReference Include="CommonUtils" Version="2.0.0">
    <SwitchyardRoutes>AuthModule=1.0.0;*=2.0.0</SwitchyardRoutes>
    <SwitchyardRouteGroup>AuthIsolation</SwitchyardRouteGroup>
  </PackageReference>
</ItemGroup>

```

* **效果**：在 `AuthModule` 中，`TargetLib` 和 `CommonUtils` 都会被路由到 1.0.0，且 1.0.0 版本的 `TargetLib` 内部对 `CommonUtils` 的引用也会被强制重写为 1.0.0，形成闭环。

---

## 3. 核心执行流水线与 MSBuild 挂载

为了避免增量编译失效（Incremental Build）以及防止 `dotnet publish` 时遗漏文件，`Switchyard` 放弃在 `AfterBuild` 阶段直接覆盖磁盘文件，而是选择拦截 MSBuild 内部的文件 Item 集合，修改文件流向，让 MSBuild 自动执行拷贝。

### 3.1 MSBuild 生命周期挂载

Task 挂载在 `ResolveAssemblyReferences` 之后、`CopyFilesToOutputDirectory` 之前。此时项目资产已被解析，但文件尚未真正写入输出目录。

```xml
<Project>
  <Target Name="ExecuteSwitchyardWeaving" 
          BeforeTargets="CopyFilesToOutputDirectory;ComputeFilesToPublish"
          AfterTargets="ResolveAssemblyReferences">
    <PropertyGroup>
      <SwitchyardTaskAssembly>$(MSBuildThisFileDirectory)..\tasks\Switchyard.Task.dll</SwitchyardTaskAssembly>
    </PropertyGroup>
    
    <UsingTask TaskName="SwitchyardTask" AssemblyFile="$(SwitchyardTaskAssembly)" />
    
    <SwitchyardTask 
        PackageReferences="@(PackageReference)"
        ReferenceCopyLocalPaths="@(ReferenceCopyLocalPaths)"
        ProjectAssetsFile="$(ProjectAssetsFile)"
        IntermediateOutputPath="$(IntermediateOutputPath)"
        TargetFramework="$(TargetFramework)">
      <Output TaskParameter="NewReferenceCopyLocalPaths" ItemName="ReferenceCopyLocalPaths" />
    </SwitchyardTask>
  </Target>
</Project>

```

### 3.2 Task 执行步骤

1. **解析配置**：读取传入的 `PackageReferences`，提取带有 `SwitchyardRoutes` 元数据的项，构建内存中的路由映射表。
2. **拓扑图分析（依赖级联）**：读取 `project.assets.json`（由 `ProjectAssetsFile` 传入），分析目标依赖项的完整依赖图，识别出 `SwitchyardRouteGroup` 中声明的级联依赖，防止子依赖包断裂。
3. **包准备与动态生成**：
* 检查全局缓存（Global Nuget Cache），如本地缺少的版本，通过 `NuGet.Protocol` 进行静默下载。
* 将解析出的多版本包释放到项目的中间目录（如 `obj/Debug/net8.0/switchyard/`）。


4. **目标包重命名与元数据修复**：
* 使用 AsmResolver 同时读取目标 DLL 及其 `.pdb` 文件（若存在）。
* 修改 `AssemblyDefinition.Name` 为 `{PackageName}.Switchyard.{Version}`。
* 清除原包的强签名（`PublicKey = null`）。
* 调用 `module.Write()`，由 AsmResolver **同步重写并计算新的 DLL 和 PDB 元数据**，确保 MVID 一致，保留完整的调试能力。


5. **调用方引用重定向**：
* 针对通过 `ReferenceCopyLocalPaths` 传入的、即将拷贝到 bin 目录的所有项目和依赖项 DLL，如果在路由表中匹配成功：
* 使用 AsmResolver 载入，修改其 `AssemblyReferences` 中对应的项名称，重定向至新生成的程序集名称，并置空 `PublicKeyOrToken`。
* 将修改后的调用方 DLL 同样缓存在中间目录。


6. **修改 MSBuild 输出流（关键点）**：
* 清空原始的 `ReferenceCopyLocalPaths` 集合中被魔改的 DLL。
* 将中间目录下生成的重命名后的目标 DLL、对应的 `.pdb` 以及重定向后的调用方 DLL 追加到 `NewReferenceCopyLocalPaths` 输出参数中。
* **结果**：MSBuild 会自动将正确的、修改后的、带完整 PDB 映射的文件拷贝到 `bin` 目录或发布目录，完美适配增量编译。



---

## 4. 底层 IL 编织机制

使用 AsmResolver 进行元数据级修改。其核心优势在于不需要扫描或解析庞大的方法体（Method Body）内部的详细 IL 指令，只需修改元数据定义表（Metadata Tables），即可自动更新所有外部引用的底层 Token 指向。

### 4.1 目标包重命名与 PDB 同步代码逻辑

```csharp
public void PrepareAndRenameAssembly(string sourcePath, string routedName, string intermediateDir)
{
    var readerParameters = new ModuleReaderParameters();
    string pdbPath = Path.ChangeExtension(sourcePath, ".pdb");
    if (File.Exists(pdbPath))
    {
        readerParameters.SymbolReaderProvider = new PdbReaderProvider();
    }

    var module = ModuleDefinition.FromFile(sourcePath, readerParameters);
    
    // 修改程序集核心定义
    module.Assembly.Name = routedName;
    
    // 移除强名称
    module.Assembly.PublicKey = null; 
    module.Assembly.HashAlgorithm = AssemblyHashAlgorithm.None;

    string outDllPath = Path.Combine(intermediateDir, routedName + ".dll");
    var writerParameters = new ModuleWriterParameters(module);
    
    // 如果存在调试符号，同步进行重塑，防止调试破坏
    if (readerParameters.SymbolReaderProvider != null)
    {
        writerParameters.SymbolWriterProvider = new PdbWriterProvider(Path.ChangeExtension(outDllPath, ".pdb"));
    }
    
    module.Write(outDllPath, writerParameters);
}

```

### 4.2 调用方引用重定向代码逻辑

```csharp
public void RedirectReferences(string assemblyPath, string originalPkgName, string routedName, string intermediateDir)
{
    var readerParameters = new ModuleReaderParameters();
    string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
    if (File.Exists(pdbPath))
    {
        readerParameters.SymbolReaderProvider = new PdbReaderProvider();
    }

    var module = ModuleDefinition.FromFile(assemblyPath, readerParameters);
    var refsToReplace = module.AssemblyReferences
        .Where(ar => ar.Name == originalPkgName)
        .ToList();

    if (!refsToReplace.Any()) return;

    foreach (var oldRef in refsToReplace)
    {
        // 直接更新元数据引用名称，自动覆盖所有关联的 TypeRef
        oldRef.Name = routedName;
        oldRef.PublicKeyOrToken = null;
    }

    string outPath = Path.Combine(intermediateDir, Path.GetFileName(assemblyPath));
    var writerParameters = new ModuleWriterParameters(module);
    
    if (readerParameters.SymbolReaderProvider != null)
    {
        writerParameters.SymbolWriterProvider = new PdbWriterProvider(Path.ChangeExtension(outPath, ".pdb"));
    }
    
    module.Write(outPath, writerParameters);
}

```

---

## 5. 类型边界与运行时契约

由于 V1.0.0 和 V3.5.0 的目标包被重命名为了完全不同的程序集标识，CLR 将它们视为**两个相互独立、毫无关联的类型系统**。这意味着它们不能在代码中发生直接的“肉体碰撞”。

### 5.1 类型边界撕裂

如果 `PaymentModule`（路由到 3.5.0）的某个方法签名返回了 `TargetLib.MyClass`，而 `MainApp`（路由到 1.0.0）试图在代码里接收并强转该返回值，由于程序集名不匹配，运行时将直接抛出 `InvalidCastException`。

### 5.2 架构契约要求

* **基础类型跨越**：跨路由边界的方法调用，参数和返回值必须是 BCL 基础类型（如 `string`, `int`, `ReadOnlySpan<byte>`, `Stream` 等）或两方均不参与路由的公共第三方 DTO。
* **接口隔离模式**：推荐在独立的 `Contracts.dll` 类库中定义公共接口。不同版本区域的代码各自引入并在内部实现该接口，最终在主程序中通过依赖注入（DI）在边界处传递接口实例。

---

## 6. 局限性与已知问题

* **强名称剥离**：重命名后的程序集将失去原厂的强名称验证。对于启用了严格代码签名校验或需要部署到 GAC 的特种企业运行环境，需要在使用 `Switchyard` 后，利用自定义密钥对输出的程序集进行**二次签名**（Re-signing）。
* **字符串硬编码反射失效**：如果代码中使用了诸如 `Type.GetType("TargetLib.MyClass, TargetLib")` 这种基于字面量字符串的反射，编织器无法自动感知，运行时会导致反射失败。使用者应避免跨版本边界的硬编码反射。
* **AOT 与裁剪（Trimming）支持**：当启用 NativeAOT 裁剪时，静态分析器可能无法通过代码逻辑追踪到被 `Switchyard` 隐式重命名并注入的 `*.Switchyard.*.dll` 引用。若要使用 AOT，必须在项目的 `ILLink.Descriptors.xml` 中将对应的路由程序集声明为 Root 保留。

---

## 7. NuGet 包打包结构

```
Switchyard.nupkg
├── build/
│   ├── Switchyard.props    # 自动引入 targets
│   └── Switchyard.targets  # 注册并挂载编译期/发布期拦截钩子
├── tasks/
│   ├── Switchyard.Task.dll # 自定义 MSBuild Task 核心逻辑
│   ├── AsmResolver.dll     # 依赖：负责高性能元数据及 PDB 读写
│   ├── NuGet.Protocol.dll  # 依赖：负责静默下载特定版本 NuGet 包
│   └── NuGet.Packaging.dll
└── README.md

```

* **隔离性保证**：放置在 `tasks/` 目录下的所有 DLL 仅在 MSBuild 编译期间由编译引擎加载，**绝对不会**成为用户项目运行产物（`bin` 或 `publish` 目录）的最终依赖，确保输出纯净。