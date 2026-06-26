Switchyard.Weaver 详细设计文档
1. 概述
1.1 设计目标
Switchyard.Weaver 是一个基于 MSBuild Task 和 AsmResolver 的编译时 IL 编织工具。其核心目标是打破 .NET 默认的程序集版本统一机制，允许在同一个进程中，让主程序集和特定的依赖项分别调用同一个 NuGet 包的任意不同版本（例如主程序调用 V1.0.0，依赖项 A 调用 V3.5.0），且完全不依赖 AssemblyLoadContext (ALC)、不使用 extern alias、不依赖 Fody。
1.2 核心原则
零侵入性：所有配置仅存在于主应用程序的 .csproj 文件中，不修改任何依赖项的源码或项目文件。
原生内聚：配置完全寄生在原生的 <PackageReference> 节点上，通过 MSBuild 原生元数据机制传递。
物理隔离：在编译后修改 IL 元数据，重命名目标程序集，从根源上欺骗 CLR 的版本加载器。
自动包解析：Weaver 自动通过 NuGet API 下载所需版本的包，无需开发者手动管理物理 DLL 路径。
2. 配置 Schema 设计
所有路由规则都在主程序的 .csproj 中，附加在目标 NuGet 包的 <PackageReference> 节点上。
2.1 基础语法
通过附加 <SwitchyardRoutes> 元数据，使用键值对字符串声明路由表。
语法格式：程序集名称=目标版本号;...，支持通配符 * 作为兜底规则。
2.2 配置示例
<!-- MainApp.csproj -->
<ItemGroup>
  <!-- 假设主程序被 NuGet 统一解析到了 2.0.0 -->
  <PackageReference Include="TargetLib" Version="2.0.0">
    <SwitchyardRoutes>
      MainApp=1.0.0;        <!-- 1. 主程序自身对 TargetLib 的调用路由到 1.0.0 -->
      PaymentModule=3.5.0;  <!-- 2. 依赖项 PaymentModule 路由到 3.5.0 -->
      *=2.0.0               <!-- 3. 其他所有未显式声明的程序集保持 2.0.0 -->
    </SwitchyardRoutes>
  </PackageReference>
</ItemGroup>
2.3 依赖级联隔离组 (可选高级特性)
如果路由的包内部还依赖其他包，为了防止依赖链断裂，提供 <SwitchyardRouteGroup> 元数据将多个包绑定为一个隔离沙箱。
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
效果：在 AuthModule 中，TargetLib 和 CommonUtils 都会被路由到 1.0.0，且 1.0.0 版本的 TargetLib 内部对 CommonUtils 的引用也会被强制重写为 1.0.0，形成闭环。
3. 核心执行流水线
Weaver 不介入各个依赖项的独立编译过程，而是作为主程序构建流程末端的全局拦截器。
3.1 MSBuild 生命周期挂载
Task 挂载在 AfterBuild 目标上。此时，主程序和所有依赖项的 DLL 已经被拷贝到输出目录 $(OutDir)，这是对物理文件进行统一手术的最佳时机。
<!-- Switchyard.Weaver.targets -->
<Project>
  <Target Name="ExecuteSwitchyardWeaving" AfterTargets="AfterBuild">
    <PropertyGroup>
      <WeaverTaskAssembly>$(MSBuildThisFileDirectory)..\tasks\Switchyard.WeaverTask.dll</WeaverTaskAssembly>
    </PropertyGroup>
    <UsingTask TaskName="SwitchyardWeaveTask" AssemblyFile="$(WeaverTaskAssembly)" />
    <SwitchyardWeaveTask 
        PackageReferences="@(PackageReference)"
        OutputDirectory="$(OutDir)"
        TargetFramework="$(TargetFramework)" />
  </Target>
</Project>
3.2 Task 执行步骤
解析配置：读取传入的 PackageReferences，提取带有 SwitchyardRoutes 元数据的项，构建内存中的路由映射表。
包准备与下载：
提取所有请求的版本号（如 1.0.0, 3.5.0, 2.0.0）。
使用 NuGet API (NuGet.Protocol) 检查全局缓存，若无则静默下载对应版本。
解析 nuspec，根据当前 $(TargetFramework) 提取最佳兼容的 lib DLL 路径。
目标包重命名：
使用 AsmResolver 读取目标 DLL。
修改 AssemblyDefinition.Name 为 {PackageName}.Switchyard.{Version}（例如 TargetLib.Switchyard.1.0.0）。
剥离强名称签名（由于重命名导致原签名失效）。
将修改后的 DLL 写入输出目录。
全局输出目录扫描：
遍历 $(OutDir) 下所有的 .dll 文件（排除 .Switchyard. 前缀的文件）。
读取每个 DLL 的程序集名，并在路由表中匹配规则（精确匹配优先，其次通配符 *）。
IL 引用重写：
若匹配到规则，且该 DLL 的 AssemblyReferences 包含目标包，则修改引用的 Name 指向重命名后的程序集，并清除 PublicKeyToken。
保存覆盖原 DLL。
清理残留：
删除输出目录中原始的、未重命名的 TargetLib.dll，防止 CLR 运行时加载到错误版本。
4. 底层 IL 编织机制
使用 AsmResolver 进行元数据修改，其核心优势是不需要遍历方法体内的 IL 指令，只需修改元数据表即可自动更新所有 TypeRef 和 MemberRef 的底层 Token 指向。
4.1 目标包重命名代码逻辑
public void PrepareAssembly(string sourcePath, string routedName, string outputDir)
{
    var module = ModuleDefinition.FromFile(sourcePath);
    module.Assembly.Name = routedName;
    // 清除强名称
    module.Assembly.PublicKey = null; 
    module.Assembly.HashAlgorithm = AssemblyHashAlgorithm.None;
    string outPath = Path.Combine(outputDir, routedName + ".dll");
    module.Write(outPath);
}
4.2 调用方引用重写代码逻辑
public void RouteReferences(string assemblyPath, string originalPkgName, string routedName)
{
    var module = ModuleDefinition.FromFile(assemblyPath);
    var refsToReplace = module.AssemblyReferences
        .Where(ar => ar.Name == originalPkgName)
        .ToList();
    foreach (var oldRef in refsToReplace)
    {
        // AsmResolver 魔法：直接修改 Name 即可更新所有引用映射
        oldRef.Name = routedName;
        oldRef.PublicKeyOrToken = null;
    }
    // 强制重新计算元数据并保存
    module.Write(assemblyPath);
}
5. 类型边界与运行时契约
由于 V1.0.0 和 V3.5.0 的目标包被重命名为了完全不同的程序集标识，CLR 将它们视为毫无关系的两个类型系统。这带来了不可忽视的运行时约束。
5.1 类型边界撕裂
如果 PaymentModule (路由到 3.5.0) 的方法签名返回了 TargetLib.MyClass，而 MainApp (路由到 1.0.0) 试图接收并强转该返回值，将抛出 InvalidCastException 或 MissingMethodException。
5.2 架构契约要求
基础类型跨越：跨路由边界的 API 调用，参数和返回值必须是 BCL 基础类型（string, int, Stream 等）或不参与路由的公共 DTO。
接口隔离模式：建议在独立的 Contracts.dll 中定义抽象接口，不同版本区域的代码各自实现，通过依赖注入在边界处传递接口实例。
6. 局限性与已知问题
强名称剥离：重命名后的程序集将失去强名称验证。对于启用 GAC 或严格安全策略的企业环境可能不兼容。
硬编码反射失效：如果代码中使用 Type.GetType("TargetLib.MyClass, TargetLib") 这种基于原名称的字符串反射，编织后将无法找到类型。使用者需避免此类硬编码。
PDB 调试映射：由于 Weaver 不修改任何方法体 IL 指令，仅修改元数据表，原有的 PDB 调试文件保持有效。但 Weaver 必须确保在覆盖 DLL 时不破坏同名的 .pdb 文件。
AOT / Trimming 裁剪警告：Trimmer 静态分析时可能无法发现 Weaver 动态生成的 Switchyard.*.dll 的引用链。若启用 AOT，可能需要手动在 Trimmer Root 描述文件中保留相关类型。
7. NuGet 包打包结构
Switchyard.Weaver.nupkg
├── build/
│   ├── Switchyard.Weaver.props    # 自动引入 targets
│   └── Switchyard.Weaver.targets  # 注册 AfterBuild 钩子
├── tasks/
│   ├── Switchyard.WeaverTask.dll  # MSBuild Task 执行逻辑
│   ├── AsmResolver.dll            # IL 修改依赖
│   ├── NuGet.Protocol.dll         # 自动下载依赖
│   └── NuGet.Packaging.dll
└── README.md
隔离性保证：tasks/ 目录下的所有 DLL 仅在 MSBuild 编译期间被 AppDomain 加载，绝对不会成为用户最终运行产物的依赖，保持最终输出的绝对纯净。